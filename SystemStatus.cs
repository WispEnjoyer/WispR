using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WispR
{
    /// <summary>Volume, network, battery and keyboard-layout readings for the system box.</summary>
    static class SystemStatus
    {
        // ---------- volume (Core Audio) ----------

        public static bool TryGetVolume(out float level, out bool muted)
        {
            level = 0; muted = false;
            var v = Endpoint();
            if (v == null) return false;
            try
            {
                v.GetMasterVolumeLevelScalar(out level);
                v.GetMute(out muted);
                return true;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(v); }
        }

        public static void SetVolume(float level)
        {
            var v = Endpoint();
            if (v == null) return;
            try
            {
                var ctx = Guid.Empty;
                v.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, level)), ref ctx);
                if (level > 0) v.SetMute(false, ref ctx);
            }
            catch { }
            finally { Marshal.ReleaseComObject(v); }
        }

        public static void ToggleMute()
        {
            var v = Endpoint();
            if (v == null) return;
            try
            {
                var ctx = Guid.Empty;
                v.GetMute(out bool m);
                v.SetMute(!m, ref ctx);
            }
            catch { }
            finally { Marshal.ReleaseComObject(v); }
        }

        static IAudioEndpointVolume Endpoint()
        {
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out dev) != 0 || dev == null) return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0) return null;
                return o as IAudioEndpointVolume;
            }
            catch { return null; }
            finally
            {
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

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
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        }

        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        /// <summary>Name of the current speakers/headphones, e.g. "Speakers (Realtek Audio)".</summary>
        public static string GetOutputDeviceName()
        {
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            IPropertyStore store = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null;
                if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // friendly name
                if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return null;
                try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; }
                finally { PropVariantClear(ref pv); }
            }
            catch { return null; }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // ---------- network ----------

        public enum Net { None, Wifi, Ethernet }

        public static Net GetNetwork()
        {
            try
            {
                var up = NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    !n.Description.ToLowerInvariant().Contains("virtual") &&
                    n.GetIPProperties().GatewayAddresses.Count > 0).ToList();
                if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) return Net.Wifi;
                return up.Count > 0 ? Net.Ethernet : Net.None;
            }
            catch { return Net.None; }
        }

        // ---------- network speed ----------

        static List<NetworkInterface> speedNics = new List<NetworkInterface>();
        static DateTime nicsRefreshed = DateTime.MinValue;
        static long lastSent = -1, lastReceived = -1;
        static DateTime lastSample;

        /// <summary>Bytes per second sent/received since the previous call, over real (non-virtual) connections.</summary>
        public static void SampleSpeed(out double upPerSec, out double downPerSec)
        {
            upPerSec = downPerSec = 0;
            try
            {
                if ((DateTime.Now - nicsRefreshed).TotalSeconds > 10) // the adapter list rarely changes
                {
                    nicsRefreshed = DateTime.Now;
                    speedNics = NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        !n.Description.ToLowerInvariant().Contains("virtual") &&
                        !n.Description.ToLowerInvariant().Contains("vpn")).ToList();
                }
                long sent = 0, received = 0;
                foreach (var n in speedNics)
                {
                    var st = n.GetIPStatistics();
                    sent += st.BytesSent;
                    received += st.BytesReceived;
                }
                var now = DateTime.Now;
                double secs = (now - lastSample).TotalSeconds;
                if (lastSent >= 0 && secs > 0.2 && sent >= lastSent && received >= lastReceived)
                {
                    upPerSec = (sent - lastSent) / secs;
                    downPerSec = (received - lastReceived) / secs;
                }
                lastSent = sent; lastReceived = received; lastSample = now;
            }
            catch { }
        }

        /// <summary>"0 KB/s", "340 KB/s", "12.4 MB/s", "1.05 GB/s".</summary>
        public static string FormatSpeed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024, mb = kb / 1024, gb = mb / 1024;
            if (gb >= 1) return gb.ToString("0.00") + " GB/s";
            if (mb >= 1) return mb.ToString(mb >= 100 ? "0" : "0.0") + " MB/s";
            return kb.ToString(kb >= 10 ? "0" : "0.0") + " KB/s";
        }

        // ---------- battery ----------

        public static bool TryGetBattery(out int percent, out bool charging)
        {
            var p = SystemInformation.PowerStatus;
            percent = (int)Math.Round(p.BatteryLifePercent * 100);
            charging = p.PowerLineStatus == PowerLineStatus.Online;
            return (p.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 &&
                   p.BatteryChargeStatus != BatteryChargeStatus.Unknown && percent <= 100;
        }

        // ---------- keyboard layout ----------

        /// <summary>"DEU", "ENG"… for the active window, or null if only one layout is installed.</summary>
        public static string GetKeyboardLayoutName(out string fullName)
        {
            fullName = null;
            try
            {
                if (Native.GetKeyboardLayoutList(0, null) <= 1) return null;
                IntPtr fg = Native.GetForegroundWindow();
                uint thread = Native.GetWindowThreadProcessId(fg, out _);
                int lang = Native.GetKeyboardLayout(thread).ToInt32() & 0xFFFF;
                var ci = new CultureInfo(lang);
                fullName = ci.DisplayName;
                return ci.ThreeLetterISOLanguageName.ToUpperInvariant();
            }
            catch { return null; }
        }

        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    }
}
