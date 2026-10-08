using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WispR
{
    /// <summary>One program in the volume mixer: all of its sound sessions on the current output device, as one.</summary>
    sealed class MixerApp
    {
        public string Key = "", Name = "", Path;
        public float Level;
        public bool Muted, Active, IsSystem;
        public Bitmap Icon;                 // owned by the mixer (cached per program)
        internal readonly List<object> Sessions = new List<object>(); // the COM objects, released on the next refresh
    }

    /// <summary>
    /// Windows' volume mixer in small: every program that has a sound session on the default output
    /// device, with its own volume and mute. Sessions of the same program (browsers have several) are one
    /// entry. The list is read again with <see cref="Refresh"/>; nothing runs in the background.
    /// </summary>
    static class AppMixer
    {
        static List<MixerApp> apps = new List<MixerApp>();
        static readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<uint, (string path, DateTime at)> pidPaths = new Dictionary<uint, (string, DateTime)>();
        static readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Guid ctx = Guid.NewGuid();
        static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        static bool loggedError;

        public static IReadOnlyList<MixerApp> Apps => apps;
        public static int IconPx = 24;

        /// <summary>Reads the sessions again. True when the set of programs changed (not just their levels).</summary>
        public static bool Refresh()
        {
            var fresh = new List<MixerApp>();
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioSessionManager2 mgr = null; IAudioSessionEnumerator list = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out dev) == 0 && dev != null)
                {
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (dev.Activate(ref iid, 0x17, IntPtr.Zero, out object o) == 0 && o is IAudioSessionManager2 m)
                    {
                        mgr = m;
                        if (mgr.GetSessionEnumerator(out list) == 0 && list != null && list.GetCount(out int n) == 0)
                        {
                            var byKey = new Dictionary<string, MixerApp>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < n && i < 200; i++)
                            {
                                if (list.GetSession(i, out IAudioSessionControl2 s) != 0 || s == null) continue;
                                bool keep = false;
                                try { keep = Add(s, byKey, fresh); }
                                catch { }
                                finally { if (!keep) Marshal.ReleaseComObject(s); }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!loggedError) { loggedError = true; Log.Error("AppMixer.Refresh", ex); }
            }
            finally
            {
                if (list != null) Marshal.ReleaseComObject(list);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }

            // playing ones first, then by name; Windows' own sounds last
            fresh = fresh.OrderBy(a => a.IsSystem).ThenByDescending(a => a.Active).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            bool changed = fresh.Count != apps.Count || fresh.Where((a, i) => a.Key != apps[i].Key).Any();
            var old = apps;
            apps = fresh;
            foreach (var a in old) ReleaseSessions(a);
            return changed;
        }

        /// <summary>Adds one session to its program's entry. True when the session object is kept.</summary>
        static bool Add(IAudioSessionControl2 s, Dictionary<string, MixerApp> byKey, List<MixerApp> into)
        {
            if (s.GetState(out int state) != 0 || state == 2 /* expired */) return false;
            if (!(s is ISimpleAudioVolume v)) return false;
            s.GetProcessId(out uint pid);
            bool system = s.IsSystemSoundsSession() == 0;
            if (!system && pid == ownPid) return false; // WispR's own timer chime
            string path = system ? null : PathOf(pid);
            string key = system ? "|system" : path ?? ("pid:" + pid);

            if (!byKey.TryGetValue(key, out var app))
            {
                app = new MixerApp { Key = key, Path = path, IsSystem = system };
                app.Name = system ? "System sounds" : NameFor(path, DisplayName(s), pid);
                app.Icon = IconFor(path);
                v.GetMasterVolume(out app.Level);
                v.GetMute(out bool muted);
                app.Muted = muted;
                byKey[key] = app;
                into.Add(app);
            }
            if (state == 1 /* active */) app.Active = true;
            app.Sessions.Add(s);
            return true;
        }

        public static void Set(MixerApp app, float level)
        {
            level = Math.Max(0, Math.Min(1, level));
            app.Level = level;
            foreach (var o in app.Sessions)
                try
                {
                    if (!(o is ISimpleAudioVolume v)) continue;
                    v.SetMasterVolume(level, ref ctx);
                    if (level > 0 && app.Muted) v.SetMute(false, ref ctx);
                }
                catch { }
            if (level > 0) app.Muted = false;
        }

        public static void SetMute(MixerApp app, bool mute)
        {
            app.Muted = mute;
            foreach (var o in app.Sessions)
                try { if (o is ISimpleAudioVolume v) v.SetMute(mute, ref ctx); } catch { }
        }

        /// <summary>Lets go of the session objects (when the mixer isn't shown).</summary>
        public static void Release()
        {
            foreach (var a in apps) ReleaseSessions(a);
            apps = new List<MixerApp>();
        }

        static void ReleaseSessions(MixerApp a)
        {
            foreach (var o in a.Sessions) try { Marshal.ReleaseComObject(o); } catch { }
            a.Sessions.Clear();
        }

        // ---------- names and icons ----------

        static string DisplayName(IAudioSessionControl2 s)
        {
            if (s.GetDisplayName(out IntPtr p) != 0 || p == IntPtr.Zero) return null;
            try
            {
                string n = Marshal.PtrToStringUni(p);
                return string.IsNullOrWhiteSpace(n) || n.StartsWith("@") ? null : n.Trim(); // "@%SystemRoot%\…": a resource reference
            }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        static string NameFor(string path, string display, uint pid)
        {
            if (!string.IsNullOrEmpty(display)) return display.Length > 60 ? display.Substring(0, 60) : display;
            if (string.IsNullOrEmpty(path)) return "App " + pid;
            if (names.TryGetValue(path, out var n)) return n;
            n = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                n = !string.IsNullOrWhiteSpace(info.FileDescription) && info.FileDescription.Trim().Length <= 40 ? info.FileDescription.Trim()
                  : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : null;
            }
            catch { }
            if (string.IsNullOrEmpty(n))
            {
                n = System.IO.Path.GetFileNameWithoutExtension(path);
                if (n.Length > 0) n = char.ToUpperInvariant(n[0]) + n.Substring(1);
            }
            if (n.Length > 60) n = n.Substring(0, 60);
            if (names.Count > 300) names.Clear();
            names[path] = n;
            return n;
        }

        static Bitmap IconFor(string path)
        {
            string key = path ?? "|system";
            if (icons.TryGetValue(key, out var b)) return b;
            Bitmap icon = null;
            try { icon = IconLoader.Load(path ?? System.IO.Path.Combine(Environment.SystemDirectory, "SndVol.exe"), IconPx); } catch { }
            if (icons.Count > 100) { foreach (var x in icons.Values) x?.Dispose(); icons.Clear(); }
            icons[key] = icon;
            return icon;
        }

        static string PathOf(uint pid)
        {
            if (pid == 0) return null;
            if (pidPaths.TryGetValue(pid, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(2)) return c.path;
            string path = null;
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != IntPtr.Zero)
                try
                {
                    var sb = new StringBuilder(1024); int len = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref len)) path = sb.ToString();
                }
                finally { CloseHandle(h); }
            if (pidPaths.Count > 300) pidPaths.Clear();
            pidPaths[pid] = (path, DateTime.UtcNow);
            return path;
        }

        // ---------- Core Audio interfaces ----------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator list);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6F24D2593233"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
