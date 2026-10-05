using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WispR
{
    sealed class AppEntry
    {
        public readonly string Name, NameLower;
        public string ParsingName; // shell:AppsFolder item id (covers desktop + Store apps)
        public string LnkPath;     // Start-menu shortcut, used for "run as admin"
        public string[] Words;
        public string Initials;
        public bool[] WordStarts;
        public volatile Bitmap Icon;

        public AppEntry(string name)
        {
            Name = name;
            NameLower = name.ToLowerInvariant();
            Tokenize();
        }

        public string Key => NameLower;
        public bool IsStoreApp => ParsingName != null && ParsingName.Contains("!");
        public string IconSource => ParsingName != null ? @"shell:AppsFolder\" + ParsingName : LnkPath;

        // "Visual Studio Code" -> words [visual, studio, code], initials "vsc"
        // "PowerShell"         -> words [powershell, power, shell], initials "ps"
        void Tokenize()
        {
            var parts = new List<string>();
            WordStarts = new bool[NameLower.Length];
            var sb = new StringBuilder();
            void Flush() { if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); } }

            for (int i = 0; i < Name.Length; i++)
            {
                char c = Name[i];
                if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
                if (sb.Length > 0)
                {
                    char p = Name[i - 1];
                    bool camel = char.IsUpper(c) && char.IsLower(p);
                    bool digit = char.IsDigit(c) != char.IsDigit(p);
                    if (camel || digit) Flush();
                }
                if (sb.Length == 0 && i < WordStarts.Length) WordStarts[i] = true;
                sb.Append(char.ToLowerInvariant(c));
            }
            Flush();

            var raw = NameLower.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            Words = raw.Concat(parts).Distinct().ToArray();
            Initials = string.Concat(parts.Select(p => p[0]));
        }

        static readonly char[] Separators = " -_.,()[]&+:/\\'\"".ToCharArray();
    }

    /// <summary>Builds the list of installed apps in the background.</summary>
    sealed class AppIndex
    {
        volatile List<AppEntry> entries = new List<AppEntry>();
        DateTime lastRefresh = DateTime.MinValue;
        int refreshing;

        /// <summary>Raised from a background thread. Arg: true if the app list itself changed (vs. only icons).</summary>
        public event Action<bool> Changed;

        public IReadOnlyList<AppEntry> Entries => entries;

        public void RefreshIfStale(TimeSpan maxAge)
        {
            if (DateTime.UtcNow - lastRefresh > maxAge) RefreshAsync();
        }

        public void RefreshAsync()
        {
            if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
            var t = new Thread(() =>
            {
                try { Refresh(); }
                catch { /* keep the old list */ }
                finally { Interlocked.Exchange(ref refreshing, 0); }
            })
            { IsBackground = true, Name = "AppIndex" };
            t.SetApartmentState(ApartmentState.STA); // shell COM objects need STA
            t.Start();
        }

        void Refresh()
        {
            var oldIcons = new Dictionary<string, Bitmap>();
            foreach (var e in entries)
                if (e.Icon != null && e.IconSource != null) oldIcons[e.IconSource] = e.Icon;

            var map = new Dictionary<string, AppEntry>();

            // 1) Everything the Start menu knows about (desktop + Microsoft Store apps)
            try
            {
                foreach (var (name, path) in EnumerateAppsFolder())
                    if (!map.ContainsKey(name.ToLowerInvariant()))
                        map[name.ToLowerInvariant()] = new AppEntry(name) { ParsingName = path };
            }
            catch { /* fall back to shortcuts only */ }

            // 2) Start menu shortcuts (needed for "run as administrator", and as a fallback)
            foreach (var lnk in EnumerateShortcuts())
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                string key = name.ToLowerInvariant();
                if (map.TryGetValue(key, out var existing)) { existing.LnkPath ??= lnk; }
                else map[key] = new AppEntry(name) { LnkPath = lnk };
            }

            var list = map.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var e in list)
                if (e.IconSource != null && oldIcons.TryGetValue(e.IconSource, out var icon)) e.Icon = icon;

            entries = list;
            lastRefresh = DateTime.UtcNow;
            Changed?.Invoke(true);

            int loaded = 0;
            foreach (var e in list)
            {
                if (e.Icon != null) continue;
                e.Icon = IconLoader.Load(e.IconSource);
                if (++loaded % 25 == 0) Changed?.Invoke(false);
            }
            if (loaded > 0) Changed?.Invoke(false);
        }

        static List<(string, string)> EnumerateAppsFolder()
        {
            var result = new List<(string, string)>();
            var type = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, path = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                        result.Add((name.Trim(), path));
                }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            return result;
        }

        static IEnumerable<string> EnumerateShortcuts()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            };
            foreach (var root in roots.Where(Directory.Exists))
            {
                string[] files;
                try { files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms") yield return f;
                }
            }
        }
    }

    /// <summary>Gets real shell icons (incl. Store app icons) with proper transparency.</summary>
    static class IconLoader
    {
        public static int Size = 32;

        public static Bitmap Load(string parsingName) => Load(parsingName, Size);

        public static Bitmap Load(string parsingName, int size) => Load(parsingName, size, SIIGBF_ICONONLY);

        /// <summary>A picture's thumbnail from Windows' own thumbnail cache (fast, and already scaled).</summary>
        public static Bitmap LoadThumbnail(string path, int size) => Load(path, size, 0x8 /* THUMBNAILONLY */ | 0x1 /* BIGGERSIZEOK */);

        static Bitmap Load(string parsingName, int size, int flags)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
                return ToBitmap(hbm);
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        static Bitmap ToBitmap(IntPtr hbm)
        {
            var ds = new DIBSECTION();
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.bm.bmBitsPixel != 32 || ds.bm.bmBits == IntPtr.Zero)
                return Image.FromHbitmap(hbm);

            int w = ds.bm.bmWidth, h = ds.bm.bmHeight, stride = ds.bm.bmWidthBytes;
            var buf = new byte[stride * h];
            Marshal.Copy(ds.bm.bmBits, buf, 0, buf.Length);

            bool hasAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { hasAlpha = true; break; }
            // No transparency (photo thumbnails): let GDI+ convert it — it knows which way up the
            // shell's bitmap is; reading the rows by hand turned wallpaper previews upside down.
            if (!hasAlpha) return Image.FromHbitmap(hbm);

            bool bottomUp = ds.bmih.biHeight > 0;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < h; y++)
            {
                int srcRow = bottomUp ? h - 1 - y : y;
                Marshal.Copy(buf, srcRow * stride, data.Scan0 + y * data.Stride, w * 4);
            }
            bmp.UnlockBits(data);
            return bmp;
        }

        const int SIIGBF_ICONONLY = 0x4;

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DIBSECTION { public BITMAP bm; public BITMAPINFOHEADER bmih; public uint bf0, bf1, bf2; public IntPtr dshSection; public uint dsOffset; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, ref DIBSECTION ds);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    }
}
