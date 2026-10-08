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
        internal readonly List<IntPtr> Sessions = new List<IntPtr>(); // their volume controls, released on the next refresh
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
            IntPtr en = IntPtr.Zero, dev = IntPtr.Zero, mgr = IntPtr.Zero, list = IntPtr.Zero;
            string step = "create";
            int hr = 0, count = -1, kept = 0;
            try
            {
                // Talked to through the raw interfaces (no .NET COM wrappers in between), every step checked.
                var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                var iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iidEnum, out en);
                if (hr != 0 || en == IntPtr.Zero) return Done(fresh, step, hr, count, kept);
                step = "default device";
                hr = Fn<FnDefaultEndpoint>(en, 4)(en, 0 /* eRender */, 1 /* eMultimedia */, out dev);
                if (hr != 0 || dev == IntPtr.Zero) return Done(fresh, step, hr, count, kept);
                step = "session manager";
                var iidMgr = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                hr = Fn<FnActivate>(dev, 3)(dev, ref iidMgr, 0x17, IntPtr.Zero, out mgr);
                if (hr != 0 || mgr == IntPtr.Zero) return Done(fresh, step, hr, count, kept);
                step = "session list";
                hr = Fn<FnOutPtr>(mgr, 5)(mgr, out list);
                if (hr != 0 || list == IntPtr.Zero) return Done(fresh, step, hr, count, kept);
                step = "count";
                hr = Fn<FnOutInt>(list, 3)(list, out count);
                if (hr != 0) return Done(fresh, step, hr, count, kept);
                step = "sessions";
                var byKey = new Dictionary<string, MixerApp>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count && i < 200; i++)
                {
                    if (Fn<FnIndexOutPtr>(list, 4)(list, i, out IntPtr control) != 0 || control == IntPtr.Zero) continue;
                    try { if (Add(control, byKey, fresh)) kept++; }
                    catch (Exception ex) { if (!loggedError) { loggedError = true; Log.Error("AppMixer.Add", ex); } }
                    finally { Marshal.Release(control); }
                }
                step = "done"; hr = 0;
            }
            catch (Exception ex)
            {
                if (!loggedError) { loggedError = true; Log.Error("AppMixer.Refresh (" + step + ")", ex); }
            }
            finally
            {
                if (list != IntPtr.Zero) Marshal.Release(list);
                if (mgr != IntPtr.Zero) Marshal.Release(mgr);
                if (dev != IntPtr.Zero) Marshal.Release(dev);
                if (en != IntPtr.Zero) Marshal.Release(en);
            }
            return Done(fresh, step, hr, count, kept);
        }

        static string lastReport;

        static bool Done(List<MixerApp> fresh, string step, int hr, int count, int kept)
        {
            // for the log (once per different outcome): how far it got and what it found
            string report = step == "done"
                ? "Mixer: " + count + " sound sessions, " + kept + " usable, " + fresh.Count + " apps (" + string.Join(", ", fresh.Select(a => a.Name)) + ")."
                : "Mixer: stopped at \"" + step + "\", error 0x" + hr.ToString("X8") + ".";
            if (report != lastReport) { lastReport = report; Log.Write(report); }

            // playing ones first, then by name; Windows' own sounds last
            fresh = fresh.OrderBy(a => a.IsSystem).ThenByDescending(a => a.Active).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            bool changed = fresh.Count != apps.Count || fresh.Where((a, i) => a.Key != apps[i].Key).Any();
            var old = apps;
            apps = fresh;
            foreach (var a in old) ReleaseSessions(a);
            return changed;
        }

        /// <summary>Adds one session (an IAudioSessionControl) to its program's entry. True when it was used.</summary>
        static bool Add(IntPtr control, Dictionary<string, MixerApp> byKey, List<MixerApp> into)
        {
            if (Fn<FnOutInt>(control, 3)(control, out int state) != 0 || state == 2 /* expired */) return false;
            uint pid = 0; bool system = false;
            var iid2 = new Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"); // IAudioSessionControl2: whose it is
            if (Marshal.QueryInterface(control, ref iid2, out IntPtr c2) == 0 && c2 != IntPtr.Zero)
                try
                {
                    Fn<FnOutUInt>(c2, 14)(c2, out pid);
                    system = Fn<FnNoArgs>(c2, 15)(c2) == 0; // S_OK: the system sounds session
                }
                finally { Marshal.Release(c2); }
            if (!system && pid == ownPid) return false; // WispR's own timer chime

            var iidVol = new Guid("87CE5498-68D6-44E5-9215-6F24D2593233"); // ISimpleAudioVolume
            if (Marshal.QueryInterface(control, ref iidVol, out IntPtr vol) != 0 || vol == IntPtr.Zero) return false;

            string path = system ? null : PathOf(pid);
            string key = system ? "|system" : path ?? ("pid:" + pid);
            if (!byKey.TryGetValue(key, out var app))
            {
                app = new MixerApp { Key = key, Path = path, IsSystem = system };
                app.Name = system ? "System sounds" : NameFor(path, DisplayName(control), pid);
                app.Icon = IconFor(path);
                if (Fn<FnOutFloat>(vol, 4)(vol, out float level) == 0) app.Level = level;
                if (Fn<FnOutInt>(vol, 6)(vol, out int muted) == 0) app.Muted = muted != 0;
                byKey[key] = app;
                into.Add(app);
            }
            if (state == 1 /* active */) app.Active = true;
            app.Sessions.Add(vol); // kept (with its reference) until the next refresh
            return true;
        }

        public static void Set(MixerApp app, float level)
        {
            level = Math.Max(0, Math.Min(1, level));
            app.Level = level;
            foreach (var v in app.Sessions)
                try
                {
                    Fn<FnSetFloat>(v, 3)(v, level, ref ctx);
                    if (level > 0 && app.Muted) Fn<FnSetBool>(v, 5)(v, 0, ref ctx);
                }
                catch { }
            if (level > 0) app.Muted = false;
        }

        public static void SetMute(MixerApp app, bool mute)
        {
            app.Muted = mute;
            foreach (var v in app.Sessions)
                try { Fn<FnSetBool>(v, 5)(v, mute ? 1 : 0, ref ctx); } catch { }
        }

        /// <summary>Lets go of the session objects (when the mixer isn't shown).</summary>
        public static void Release()
        {
            foreach (var a in apps) ReleaseSessions(a);
            apps = new List<MixerApp>();
        }

        static void ReleaseSessions(MixerApp a)
        {
            foreach (var v in a.Sessions) try { Marshal.Release(v); } catch { }
            a.Sessions.Clear();
        }

        // ---------- names and icons ----------

        static string DisplayName(IntPtr control)
        {
            if (Fn<FnOutPtr>(control, 4)(control, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
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

        // ---------- Core Audio, called through the interfaces' function tables ----------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnDefaultEndpoint(IntPtr self, int flow, int role, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnActivate(IntPtr self, ref Guid iid, int clsCtx, IntPtr p, out IntPtr iface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutPtr(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutInt(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutUInt(IntPtr self, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutFloat(IntPtr self, out float value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnIndexOutPtr(IntPtr self, int index, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnNoArgs(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSetFloat(IntPtr self, float value, ref Guid ctx);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSetBool(IntPtr self, int value, ref Guid ctx);

        static readonly Dictionary<(IntPtr, Type), Delegate> fns = new Dictionary<(IntPtr, Type), Delegate>();

        /// <summary>The function in slot <paramref name="slot"/> of the object's table.</summary>
        static T Fn<T>(IntPtr obj, int slot) where T : Delegate
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
            if (!fns.TryGetValue((fn, typeof(T)), out var d))
            {
                if (fns.Count > 200) fns.Clear();
                fns[(fn, typeof(T))] = d = Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
            }
            return (T)d;
        }

        [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid, out IntPtr obj);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
