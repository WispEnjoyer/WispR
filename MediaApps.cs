using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WispR
{
    /// <summary>
    /// Turns the app id a media session reports into a readable name: "Spotify.exe" → "Spotify",
    /// "Microsoft.ZuneMusic_…!…" → "ZuneMusic". Firefox-based browsers (Floorp, Firefox, Zen, …) report a hash
    /// of their install folder instead; that's matched against the ids their windows carry, or the ids they
    /// write to the registry, and the program's own product name is used.
    /// </summary>
    static class MediaApps
    {
        static readonly Dictionary<string, (string name, DateTime at)> cache = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        public static string Name(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            if (cache.TryGetValue(id, out var c) && (c.name.Length > 0 || DateTime.UtcNow - c.at < TimeSpan.FromMinutes(5)))
                return c.name;
            string name;
            try { name = Resolve(id); } catch { name = ""; }
            cache[id] = (name ?? "", DateTime.UtcNow);
            return name ?? "";
        }

        static readonly Dictionary<string, (string exe, DateTime at)> exeCache = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The program behind a media session, as a file name without ".exe" ("floorp", "spotify"), or null.</summary>
        public static string ExeName(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (exeCache.TryGetValue(id, out var c) && (c.exe != null || DateTime.UtcNow - c.at < TimeSpan.FromMinutes(1))) return c.exe;
            string exe = null;
            try
            {
                string s = id.Split('!')[0];
                if (Regex.IsMatch(s, "^[0-9A-Fa-f]{12,}$"))
                {
                    // the browser's window carries the same code: its process is the program
                    EnumWindows((h, _) =>
                    {
                        if (!IsWindowVisible(h) || !(WindowAppId(h) is string w) || !w.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
                        GetWindowThreadProcessId(h, out uint pid);
                        string path = ProcessPath(pid);
                        if (path != null) exe = Path.GetFileNameWithoutExtension(path);
                        return exe == null;
                    }, IntPtr.Zero);
                }
                else if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = Path.GetFileNameWithoutExtension(s);
                else if (s.IndexOf('_') < 0 && s.IndexOf('.') < 0) exe = s; // e.g. "Chrome", "Spotify"
            }
            catch { }
            exeCache[id] = (exe, DateTime.UtcNow);
            return exe;
        }

        static string Resolve(string id)
        {
            string s = id.Split('!')[0];
            if (Regex.IsMatch(s, "^[0-9A-Fa-f]{12,}$"))
                return FromWindows(s) ?? FromTaskbarIds(s) ?? "";
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int us = s.IndexOf('_');
            if (us > 0) s = s.Substring(0, us);
            int dot = s.LastIndexOf('.');
            return dot >= 0 && dot < s.Length - 1 ? s.Substring(dot + 1) : s;
        }

        // ---------- a window that carries the same id ----------

        static string FromWindows(string aumid)
        {
            string found = null;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                if (WindowAppId(h) is string w && w.Equals(aumid, StringComparison.OrdinalIgnoreCase))
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    found = ProgramName(ProcessPath(pid));
                    return found == null; // keep looking if this one couldn't be read
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static string WindowAppId(IntPtr hwnd)
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
            try
            {
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var pv = new PropVariant();
                if (store.GetValue(ref key, out pv) != 0) return null;
                try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null; }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int len = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        static string ProgramName(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(v.ProductName)) return v.ProductName.Trim();
                if (!string.IsNullOrWhiteSpace(v.FileDescription)) return v.FileDescription.Trim();
            }
            catch { }
            string n = Path.GetFileNameWithoutExtension(exe);
            return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n.Substring(1) : null;
        }

        // ---------- Mozilla-style "TaskBarIDs" registry keys: install folder → id ----------

        static string FromTaskbarIds(string aumid)
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var software = root.OpenSubKey("Software");
                    if (software == null) continue;
                    foreach (var vendor in software.GetSubKeyNames())
                    {
                        try
                        {
                            using var vk = software.OpenSubKey(vendor);
                            if (vk == null) continue;
                            if (Match(vk, aumid, vendor) is string a) return a;
                            foreach (var product in vk.GetSubKeyNames())
                            {
                                using var pk = vk.OpenSubKey(product);
                                if (pk != null && Match(pk, aumid, product) is string b) return b;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return null;
        }

        static string Match(RegistryKey key, string aumid, string keyName)
        {
            using var ids = key.OpenSubKey("TaskBarIDs");
            if (ids == null) return null;
            foreach (var folder in ids.GetValueNames())
            {
                if (!(ids.GetValue(folder) is string v) || !v.Equals(aumid, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // the program in that folder named after the key ("Floorp" → floorp.exe), else the key itself
                    string exe = Path.Combine(folder, keyName + ".exe");
                    return ProgramName(File.Exists(exe) ? exe : null) ?? keyName;
                }
                catch { return keyName; }
            }
            return null;
        }

        // ---------- interop ----------

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
