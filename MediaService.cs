using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WispR
{
    sealed class MediaInfo
    {
        public string Title = "", Artist = "", AppId = "";
        public bool Playing;
        public Bitmap Thumbnail;             // may be null
        public long PositionTicks, DurationTicks, StartTicks;
        public bool CanSeek;
        public DateTime PositionAtUtc;       // when PositionTicks was true
        public string Key => AppId + "|" + Title + "|" + Artist;

        /// <summary>Position right now (it keeps moving while playing).</summary>
        public TimeSpan Position
        {
            get
            {
                long p = PositionTicks;
                if (Playing && PositionAtUtc != default) p += (DateTime.UtcNow - PositionAtUtc).Ticks;
                return TimeSpan.FromTicks(Math.Max(0, Math.Min(DurationTicks > 0 ? DurationTicks : long.MaxValue, p)));
            }
        }
    }

    /// <summary>
    /// Reads "now playing" from Windows' media sessions (Spotify, browsers playing YouTube, media
    /// players…) — the same source as the media controls in Windows' own quick settings — and sends
    /// play/pause/next/previous. Runs on its own background thread. The Windows API involved is a
    /// modern (WinRT) one, called here directly through its interface tables; if anything about it is
    /// unavailable the service simply reports "nothing playing".
    /// </summary>
    sealed class MediaService : IDisposable
    {
        public event Action<MediaInfo> Updated; // raised on the background thread; null = nothing playing

        readonly ConcurrentQueue<Action<IntPtr>> commands = new ConcurrentQueue<Action<IntPtr>>();
        readonly Playhead playhead = new Playhead(); // only touched on the media thread
        long startTicks;
        readonly Thread thread;
        volatile bool stop;
        IntPtr manager;
        string lastThumbKey;
        Bitmap lastThumb;

        public MediaService()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "Media" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void PlayPause() => commands.Enqueue(s => Fire(CallPtr(s, SlotTogglePlayPause)));
        public void Next() => commands.Enqueue(s => Fire(CallPtr(s, SlotSkipNext)));
        public void Previous() => commands.Enqueue(s => Fire(CallPtr(s, SlotSkipPrevious)));

        /// <summary>Jump to a position (measured from the start of the track).</summary>
        public void Seek(TimeSpan position)
        {
            long target = startTicks + Math.Max(0, position.Ticks);
            commands.Enqueue(s =>
            {
                playhead.Seek(Math.Max(0, position.Ticks), DateTime.UtcNow);
                if (Slot<FnLongPtr>(s, SlotChangePosition)(s, target, out IntPtr op) == 0) Fire(op);
            });
        }

        static void Fire(IntPtr op) { if (op != IntPtr.Zero) Marshal.Release(op); } // fire and forget

        // Slots in IGlobalSystemMediaTransportControlsSession (after IUnknown's 3 and IInspectable's 3 methods).
        const int SlotSourceAppId = 6, SlotGetProps = 7, SlotGetTimeline = 8, SlotGetPlayback = 9,
                  SlotSkipNext = 16, SlotSkipPrevious = 17, SlotTogglePlayPause = 20, SlotChangePosition = 24;

        /// <summary>Set while the media box is turned off: nothing is read until it's back on.</summary>
        public volatile bool Paused;

        void Loop()
        {
            try { RoInitialize(1 /* multithreaded */); } catch { }
            int failures = 0;
            while (!stop)
            {
                if (Paused && commands.IsEmpty) { Thread.Sleep(500); continue; } // media box turned off
                MediaInfo info = null;
                try
                {
                    info = Poll();
                    failures = 0;
                }
                catch
                {
                    failures++;
                    Release(ref manager); // start over next time
                }
                try { Updated?.Invoke(info); } catch { }

                // Commands are handled quickly; otherwise check once a second (slower if it keeps failing).
                int wait = failures > 3 ? 10000 : 1000;
                for (int t = 0; t < wait && !stop; t += 50)
                {
                    if (!commands.IsEmpty) break;
                    Thread.Sleep(50);
                }
            }
            Release(ref manager);
        }

        MediaInfo Poll()
        {
            if (manager == IntPtr.Zero)
            {
                IntPtr statics = Factory("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager", IID_ManagerStatics);
                if (statics == IntPtr.Zero) return null;
                try { manager = Await(CallPtr(statics, 6)); } // RequestAsync()
                finally { Marshal.Release(statics); }
                if (manager == IntPtr.Zero) return null;
            }

            IntPtr session = CallPtr(manager, 6); // GetCurrentSession()
            if (session == IntPtr.Zero) { DrainCommands(); return null; }
            try
            {
                while (commands.TryDequeue(out var command))
                    try { command(session); } catch { }

                var info = new MediaInfo { AppId = CallString(session, SlotSourceAppId) ?? "" };

                IntPtr props = Await(CallPtr(session, SlotGetProps));
                if (props != IntPtr.Zero)
                {
                    try
                    {
                        info.Title = CallString(props, 6) ?? "";   // Title
                        info.Artist = CallString(props, 9) ?? "";  // Artist
                        if (info.Artist.Length == 0) info.Artist = CallString(props, 8) ?? ""; // AlbumArtist
                        string thumbKey = info.Key;
                        if (thumbKey != lastThumbKey)
                        {
                            lastThumbKey = thumbKey;
                            lastThumb = null; // the media box disposes the old cover itself, once it has the new one
                            IntPtr thumbRef = CallPtr(props, 15); // Thumbnail
                            if (thumbRef != IntPtr.Zero)
                                try { lastThumb = ReadThumbnail(thumbRef); } finally { Marshal.Release(thumbRef); }
                        }
                        info.Thumbnail = lastThumb;
                    }
                    finally { Marshal.Release(props); }
                }

                IntPtr playback = CallPtr(session, SlotGetPlayback);
                if (playback != IntPtr.Zero)
                    try { info.Playing = CallInt(playback, 7) == 4; } // PlaybackStatus: 4 = Playing
                    finally { Marshal.Release(playback); }

                var tl = ReadTimeline(session);
                if (tl.Duration <= 0 && info.Title.Length > 0)
                {
                    // Browsers can have several media sessions (one per tab). The one Windows calls "current"
                    // isn't always the one that reports timing — look for the same song in the others.
                    var other = TimelineFromOtherSessions(session, info.AppId, info.Title);
                    if (other.Duration > 0) tl = other;
                }
                if (tl.Ok) { info.StartTicks = tl.Start; startTicks = tl.Start; }

                // Where the song is comes from WispR's own clock, fed by the app's reports (see Playhead):
                // junk reports ("0 of 0", stale positions) can't throw the progress back to the start.
                var (pos, at) = playhead.Update(info.Key, info.Playing, tl.Ok, tl.Pos, tl.Updated, tl.Duration, tl.MaxSeek > tl.MinSeek, DateTime.UtcNow);
                info.PositionTicks = pos;
                info.PositionAtUtc = at;
                info.DurationTicks = playhead.Duration;
                info.CanSeek = playhead.CanSeek || playhead.Duration > 0;
                if (info.DurationTicks <= 0 && info.Title.Length > 0 && info.Key != loggedMissingKey)
                {
                    loggedMissingKey = info.Key;
                    Log.Write("Media: no song length from " + info.AppId + " (start " + tl.Start + ", end " + tl.End + ", seek " + tl.MinSeek + "–" + tl.MaxSeek +
                              ", position " + tl.Pos + ", timeline " + (tl.Ok ? "read" : "missing") + ", sessions " + sessionCount + ").");
                }

                return info.Title.Length > 0 || info.Artist.Length > 0 ? info : null;
            }
            finally { Marshal.Release(session); }
        }


        string loggedMissingKey;
        int sessionCount = -1;

        struct Timeline { public bool Ok; public long Start, End, MinSeek, MaxSeek, Pos, Updated; public long Duration; }

        static Timeline ReadTimeline(IntPtr session)
        {
            var tl = new Timeline();
            IntPtr timeline = CallPtr(session, SlotGetTimeline);
            if (timeline == IntPtr.Zero) return tl;
            try
            {
                tl.Ok = true;
                tl.Start = CallLong(timeline, 6); tl.End = CallLong(timeline, 7);
                tl.MinSeek = CallLong(timeline, 8); tl.MaxSeek = CallLong(timeline, 9);
                tl.Pos = Math.Max(0, CallLong(timeline, 10) - tl.Start);
                tl.Updated = CallLong(timeline, 11);
                tl.Duration = tl.End > tl.Start ? tl.End - tl.Start : Math.Max(0, tl.MaxSeek - tl.MinSeek); // some apps only report the seek range
            }
            finally { Marshal.Release(timeline); }
            return tl;
        }

        Timeline TimelineFromOtherSessions(IntPtr current, string appId, string title)
        {
            var found = new Timeline();
            IntPtr list = CallPtr(manager, 7); // GetSessions() → IVectorView<session>
            if (list == IntPtr.Zero) return found;
            try
            {
                int n = CallInt(list, 7); // Size
                sessionCount = n;
                for (uint i = 0; i < n && i < 32; i++)
                {
                    if (Slot<FnIndexPtr>(list, 6)(list, i, out IntPtr s) != 0 || s == IntPtr.Zero) continue; // GetAt
                    try
                    {
                        if (s == current) continue;
                        if (!string.Equals(CallString(s, SlotSourceAppId) ?? "", appId, StringComparison.OrdinalIgnoreCase)) continue;
                        IntPtr props = Await(CallPtr(s, SlotGetProps));
                        string t = "";
                        if (props != IntPtr.Zero) try { t = CallString(props, 6) ?? ""; } finally { Marshal.Release(props); }
                        if (t != title) continue;
                        var tl = ReadTimeline(s);
                        if (tl.Duration > 0) { found = tl; break; }
                    }
                    finally { Marshal.Release(s); }
                }
            }
            finally { Marshal.Release(list); }
            return found;
        }

        void DrainCommands() { while (commands.TryDequeue(out _)) { } }

        static Bitmap ReadThumbnail(IntPtr streamRef)
        {
            IntPtr stream = Await(CallPtr(streamRef, 6)); // OpenReadAsync()
            if (stream == IntPtr.Zero) return null;
            try
            {
                var iid = IID_IStream;
                if (CreateStreamOverRandomAccessStream(stream, ref iid, out IntPtr pStream) != 0 || pStream == IntPtr.Zero) return null;
                var com = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(pStream);
                Marshal.Release(pStream);
                try
                {
                    using var ms = new MemoryStream();
                    var buf = new byte[65536];
                    IntPtr readPtr = Marshal.AllocHGlobal(4);
                    try
                    {
                        while (ms.Length < 20 * 1024 * 1024)
                        {
                            com.Read(buf, buf.Length, readPtr);
                            int n = Marshal.ReadInt32(readPtr);
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                        }
                    }
                    finally { Marshal.FreeHGlobal(readPtr); }
                    if (ms.Length == 0) return null;
                    ms.Position = 0;
                    using var img = Image.FromStream(ms);
                    return new Bitmap(img);
                }
                finally { Marshal.ReleaseComObject(com); }
            }
            catch { return null; }
            finally { Marshal.Release(stream); }
        }

        // ---------- WinRT plumbing ----------

        static readonly Guid IID_ManagerStatics = new Guid("2050C4EE-11A0-57DE-AED7-C97C70338245");
        static readonly Guid IID_IAsyncInfo = new Guid("00000036-0000-0000-C000-000000000046");
        static readonly Guid IID_IStream = new Guid("0000000c-0000-0000-C000-000000000046");

        static IntPtr Factory(string className, Guid iid)
        {
            WindowsCreateString(className, (uint)className.Length, out IntPtr h);
            try { return RoGetActivationFactory(h, ref iid, out IntPtr f) == 0 ? f : IntPtr.Zero; }
            finally { WindowsDeleteString(h); }
        }

        /// <summary>Waits (up to ~3 s) for a WinRT async operation and returns its result; releases the operation.</summary>
        static IntPtr Await(IntPtr op)
        {
            if (op == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var iid = IID_IAsyncInfo;
                if (Marshal.QueryInterface(op, ref iid, out IntPtr info) != 0) return IntPtr.Zero;
                try
                {
                    for (int i = 0; i < 300; i++)
                    {
                        int status = CallInt(info, 7); // IAsyncInfo.Status
                        if (status == 1) break;        // Completed
                        if (status >= 2) return IntPtr.Zero; // Canceled / Error
                        Thread.Sleep(10);
                    }
                }
                finally { Marshal.Release(info); }
                return CallPtr(op, 8); // IAsyncOperation<T>.GetResults()
            }
            finally { Marshal.Release(op); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnPtr(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnInt(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnLong(IntPtr self, out long value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnLongPtr(IntPtr self, long arg, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnIndexPtr(IntPtr self, uint index, out IntPtr value);

        // Making a delegate for a native function is slow; the same functions are called every second.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(IntPtr, Type), Delegate> slotCache =
            new System.Collections.Concurrent.ConcurrentDictionary<(IntPtr, Type), Delegate>();

        static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
            if (slotCache.Count > 512) slotCache.Clear();
            return slotCache.GetOrAdd((fn, typeof(T)), k => Marshal.GetDelegateForFunctionPointer(k.Item1, k.Item2)) as T;
        }

        static IntPtr CallPtr(IntPtr obj, int slot) => Slot<FnPtr>(obj, slot)(obj, out IntPtr v) == 0 ? v : IntPtr.Zero;
        static int CallInt(IntPtr obj, int slot) => Slot<FnInt>(obj, slot)(obj, out int v) == 0 ? v : -1;
        static long CallLong(IntPtr obj, int slot) => Slot<FnLong>(obj, slot)(obj, out long v) == 0 ? v : 0;

        static string CallString(IntPtr obj, int slot)
        {
            IntPtr h = CallPtr(obj, slot);
            if (h == IntPtr.Zero) return null;
            try
            {
                IntPtr raw = WindowsGetStringRawBuffer(h, out uint len);
                return raw == IntPtr.Zero ? "" : Marshal.PtrToStringUni(raw, (int)len);
            }
            finally { WindowsDeleteString(h); }
        }

        static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { try { Marshal.Release(p); } catch { } p = IntPtr.Zero; }
        }

        public void Dispose() { stop = true; }

        [DllImport("combase.dll")] static extern int RoInitialize(int type);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, uint len, out IntPtr h);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr h);
        [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);
        [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("shcore.dll")] static extern int CreateStreamOverRandomAccessStream(IntPtr randomAccessStream, ref Guid riid, out IntPtr stream);
    }
}
