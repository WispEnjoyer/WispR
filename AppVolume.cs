using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WispR
{
    /// <summary>
    /// One app's own volume — the same per-app level as in Windows' Volume mixer. All of the app's audio
    /// sessions are matched by program name (browsers play sound from helper processes of the same name).
    /// </summary>
    static class AppVolume
    {
        static Guid ctx = Guid.NewGuid(); // marks changes as ours

        /// <summary>The app's volume (0–1) and whether it's muted, or null when it has no sound session right now.</summary>
        /// <summary>For the log: which programs currently have sound sessions.</summary>
        public static string LastSeen = "";

        public static (float level, bool muted)? Get(string exeName)
        {
            float? level = null; bool muted = false;
            ForEach(exeName, v =>
            {
                if (v.GetMasterVolume(out float l) == 0 && level == null) level = l;
                if (v.GetMute(out bool m) == 0 && m) muted = true;
            });
            return level == null ? ((float, bool)?)null : (level.Value, muted);
        }

        public static void Set(string exeName, float level)
        {
            level = Math.Max(0, Math.Min(1, level));
            ForEach(exeName, v => { v.SetMasterVolume(level, ref ctx); if (level > 0) v.SetMute(false, ref ctx); });
        }

        public static void SetMute(string exeName, bool mute) => ForEach(exeName, v => v.SetMute(mute, ref ctx));

        static readonly Dictionary<uint, (string name, DateTime at)> pidNames = new Dictionary<uint, (string, DateTime)>();

        static void ForEach(string exeName, Action<ISimpleAudioVolume> action)
        {
            if (string.IsNullOrEmpty(exeName)) return;
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioSessionManager2 mgr = null; IAudioSessionEnumerator list = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out dev) != 0 || dev == null) return;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, 0x17, IntPtr.Zero, out object o) != 0) return;
                mgr = (IAudioSessionManager2)o;
                if (mgr.GetSessionEnumerator(out list) != 0 || list == null) return;
                list.GetCount(out int n);
                var seen = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    if (list.GetSession(i, out IAudioSessionControl2 s) != 0 || s == null) continue;
                    try
                    {
                        if (s.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                        string pn = NameOf(pid);
                        if (pn != null && !seen.Contains(pn)) seen.Add(pn);
                        if (!string.Equals(pn, exeName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (s is ISimpleAudioVolume v) action(v);
                    }
                    catch { }
                    finally { Marshal.ReleaseComObject(s); }
                }
                LastSeen = string.Join(", ", seen);
            }
            catch (Exception ex) { LastSeen = "error: " + ex.GetType().Name + " " + ex.Message; }
            finally
            {
                if (list != null) Marshal.ReleaseComObject(list);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        static string NameOf(uint pid)
        {
            if (pidNames.TryGetValue(pid, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(2)) return c.name;
            string name = null;
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != IntPtr.Zero)
                try
                {
                    var sb = new StringBuilder(1024); int len = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref len)) name = Path.GetFileNameWithoutExtension(sb.ToString());
                }
                finally { CloseHandle(h); }
            if (pidNames.Count > 200) pidNames.Clear();
            pidNames[pid] = (name, DateTime.UtcNow);
            return name;
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
            [PreserveSig] int SetDisplayName(string name, ref Guid ctx);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
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
