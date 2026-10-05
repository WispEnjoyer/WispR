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

        // ---- change events (see WinRtEventSink): keep Windows' copy of the sessions fresh, and wake us up ----
        readonly AutoResetEvent changed = new AutoResetEvent(false);
        WinRtEventSink sink;
        readonly System.Collections.Generic.Dictionary<IntPtr, (IntPtr session, long t1, long t2, long t3)> subscribed =
            new System.Collections.Generic.Dictionary<IntPtr, (IntPtr, long, long, long)>();
        readonly System.Collections.Generic.List<IntPtr> subscribedOrder = new System.Collections.Generic.List<IntPtr>();

        const int SlotAddTimelineChanged = 25, SlotRemoveTimelineChanged = 26, SlotAddPlaybackChanged = 27,
                  SlotRemovePlaybackChanged = 28, SlotAddMediaChanged = 29, SlotRemoveMediaChanged = 30;
        const int SlotManagerAddCurrentChanged = 8, SlotManagerAddSessionsChanged = 10;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnAddEvent(IntPtr self, IntPtr handler, out long token);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnRemoveEvent(IntPtr self, long token);

        static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        static long AddEvent(IntPtr obj, int slot, IntPtr handler)
        {
            try { return Slot<FnAddEvent>(obj, slot)(obj, handler, out long token) == 0 ? token : 0; }
            catch { return 0; }
        }

        static void RemoveEvent(IntPtr obj, int slot, long token)
        {
            if (token == 0) return;
            try { Slot<FnRemoveEvent>(obj, slot)(obj, token); } catch { }
        }

        void SubscribeManager()
        {
            sink ??= new WinRtEventSink(changed);
            AddEvent(manager, SlotManagerAddCurrentChanged, sink.Pointer);
            AddEvent(manager, SlotManagerAddSessionsChanged, sink.Pointer);
        }

        /// <summary>Listens to this session's timeline, playback and song changes (once per session).</summary>
        void EnsureSubscribed(IntPtr session)
        {
            sink ??= new WinRtEventSink(changed);
            var iid = IID_IUnknown;
            if (Marshal.QueryInterface(session, ref iid, out IntPtr identity) != 0) return;
            Marshal.Release(identity); // only used as the session's identity; the session itself is held below
            if (subscribed.ContainsKey(identity)) return;
            Marshal.AddRef(session); // keep it (and so the subscriptions) alive
            long a = AddEvent(session, SlotAddTimelineChanged, sink.Pointer);
            long b = AddEvent(session, SlotAddPlaybackChanged, sink.Pointer);
            long c = AddEvent(session, SlotAddMediaChanged, sink.Pointer);
            subscribed[identity] = (session, a, b, c);
            subscribedOrder.Add(identity);
            Log.Throttled("media-subscribe", "Media: listening to timeline changes" + (a != 0 ? "" : " (not supported by this session)") + ".");
            while (subscribedOrder.Count > 8) // old sessions (closed tabs, players): let them go
            {
                var old = subscribedOrder[0];
                subscribedOrder.RemoveAt(0);
                Unsubscribe(old);
            }
        }

        void Unsubscribe(IntPtr identity)
        {
            if (!subscribed.TryGetValue(identity, out var e)) return;
            subscribed.Remove(identity);
            RemoveEvent(e.session, SlotRemoveTimelineChanged, e.t1);
            RemoveEvent(e.session, SlotRemovePlaybackChanged, e.t2);
            RemoveEvent(e.session, SlotRemoveMediaChanged, e.t3);
            try { Marshal.Release(e.session); } catch { }
        }

        void UnsubscribeAll()
        {
            foreach (var id in subscribedOrder.ToArray()) Unsubscribe(id);
            subscribedOrder.Clear();
        }
        string lastThumbKey;
        Bitmap lastThumb;

        public MediaService()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "Media" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        readonly AutoResetEvent commandSignal = new AutoResetEvent(false);
        void Enqueue(Action<IntPtr> command) { commands.Enqueue(command); commandSignal.Set(); }

        public void PlayPause() => Enqueue(s => Fire(CallPtr(s, SlotTogglePlayPause)));
        public void Next() => Enqueue(s => Fire(CallPtr(s, SlotSkipNext)));
        public void Previous() => Enqueue(s => Fire(CallPtr(s, SlotSkipPrevious)));

        /// <summary>Jump to a position (measured from the start of the track).</summary>
        public void Seek(TimeSpan position)
        {
            long target = startTicks + Math.Max(0, position.Ticks);
            Enqueue(s =>
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
        public bool Paused
        {
            get => paused;
            set { paused = value; if (!value) commandSignal.Set(); } // reading again: wake up now, not in a second
        }
        volatile bool paused;

        void Loop()
        {
            try { RoInitialize(1 /* multithreaded */); } catch { }
            int failures = 0;
            while (!stop)
            {
                if (Paused && commands.IsEmpty) { commandSignal.WaitOne(1000); continue; } // nothing shows media right now
                MediaInfo info = null;
                try
                {
                    info = Poll();
                    failures = 0;
                }
                catch
                {
                    failures++;
                    UnsubscribeAll();
                    Release(ref manager); // start over next time
                }
                // nothing playing: forget the cached cover — the UI disposes its copy, and handing that same
                // (disposed) picture out again if the song comes back made every redraw fail
                if (info == null) { lastThumbKey = null; lastThumb = null; }
                try { Updated?.Invoke(info); } catch { }

                // Commands are handled quickly, and a change event from the player (new length, seek, play/pause)
                // wakes us straight away; otherwise check once a second (slower if it keeps failing).
                int wait = failures > 3 ? 10000 : 1000;
                Thread.Sleep(120); // at most ~8 reads a second, however chatty the player's events are
                if (commands.IsEmpty && !stop) WaitHandle.WaitAny(new WaitHandle[] { changed, commandSignal }, wait - 120); // sleeps until something happens
            }
            UnsubscribeAll();
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
                SubscribeManager();
            }

            IntPtr session = CallPtr(manager, 6); // GetCurrentSession()
            if (session == IntPtr.Zero) { DrainCommands(); return null; }
            try
            {
                EnsureSubscribed(session);
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
                              ", position " + tl.Pos + ", timeline " + (tl.Ok ? "read" : "missing") + ", sessions " + sessionCount + ", change events so far " + (sink?.Fired ?? 0) + ").");
                }

                if (info.DurationTicks <= 0 && info.Playing && info.Title.Length > 0 && Refresh(session, info))
                    info.Playing = PlaybackStatus(session) == 4; // it's playing again (checked inside)
                return info.Title.Length > 0 || info.Artist.Length > 0 ? info : null;
            }
            finally { Marshal.Release(session); }
        }


        string loggedMissingKey;
        int sessionCount = -1;

        // ---------- making the player report the song's length ----------
        // Firefox-based browsers (YouTube in Floorp) hand Windows the song's length only when playback
        // changes state, so it can be missing for the whole song. Pausing and playing again makes them send
        // it. Done carefully: only for a song that's playing without a length, at most twice per song,
        // waiting until the player has really paused before playing again, then checking it plays (and
        // asking again if not) — so a song is never left paused. One reader at a time (the top panel and
        // the media box each have their own).
        static readonly object refreshLock = new object();
        static string refreshKey;
        static int refreshCount;
        static DateTime firstSeenWithoutLength, lastRefresh;

        bool Refresh(IntPtr session, MediaInfo info)
        {
            lock (refreshLock)
            {
                var now = DateTime.UtcNow;
                if (info.Key != refreshKey) { refreshKey = info.Key; refreshCount = 0; firstSeenWithoutLength = now; return false; }
                if (refreshCount >= 2) return false;                                    // twice didn't help: leave it
                double waited = (now - firstSeenWithoutLength).TotalSeconds;
                if (waited < (refreshCount == 0 ? 0.6 : 4) || (now - lastRefresh).TotalSeconds < 3) return false;
                refreshCount++;
                lastRefresh = now;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                Fire(CallPtr(session, 11));                                              // TryPauseAsync
                bool paused = WaitForStatus(session, s => s != 4, 700);                  // 4 = Playing
                long pausedAfter = sw.ElapsedMilliseconds;
                bool playing = false;
                for (int attempt = 0; attempt < 4 && !playing; attempt++)
                {
                    Fire(CallPtr(session, 10));                                          // TryPlayAsync
                    playing = WaitForStatus(session, s => s == 4, attempt == 0 ? 900 : 600);
                }
                Log.Write("Media: " + info.AppId + " didn't report the song length — paused and resumed to make it (" +
                          (paused ? "paused after " + pausedAfter + " ms" : "didn't pause") + ", " +
                          (playing ? "playing again after " + sw.ElapsedMilliseconds + " ms" : "COULDN'T RESUME") + ", try " + refreshCount + ").");
                return true;
            }
        }

        static int PlaybackStatus(IntPtr session)
        {
            IntPtr playback = CallPtr(session, SlotGetPlayback);
            if (playback == IntPtr.Zero) return -1;
            try { return CallInt(playback, 7); } finally { Marshal.Release(playback); }
        }

        static bool WaitForStatus(IntPtr session, Func<int, bool> ok, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (ok(PlaybackStatus(session))) return true;
                if (sw.ElapsedMilliseconds >= ms) return false;
                Thread.Sleep(10);
            }
        }

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
                    return ImageLoad.FromStream(ms, 640);
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
                // other apps decide these strings: keep them to a sane length
                return raw == IntPtr.Zero ? "" : Marshal.PtrToStringUni(raw, (int)Math.Min(len, 512u));
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
