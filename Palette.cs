using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WispR
{
    /// <summary>Builds a readable theme from an image's colours.</summary>
    static class Palette
    {
        /// <param name="mode">"Auto", "Dark" or "Light".</param>
        public static Theme FromImage(Bitmap src, string mode)
        {
            var px = Sample(src, 48);
            var clusters = KMeans(px, 6);

            double avgLum = px.Average(p => Lum(p));
            bool light = mode == "Light" || (mode == "Auto" && avgLum > 0.62);

            // Neutrals take their tint from the colour that covers most of the image.
            var dominant = clusters.OrderByDescending(c => c.Count).First();
            float baseHue = dominant.Color.GetHue();
            float baseSat = Math.Min(dominant.Color.GetSaturation(), 0.4f) * 0.6f;

            // Accent: the most vivid colour that still covers a reasonable part of the image.
            var vivid = clusters
                .Select(c => (c, score: Math.Sqrt(c.Count) * Math.Pow(c.Color.GetSaturation(), 2) * MidLightness(c.Color.GetBrightness())))
                .OrderByDescending(x => x.score)
                .First().c.Color;
            float accentHue = vivid.GetHue(), accentSat = vivid.GetSaturation();
            bool grey = accentSat < 0.12f;
            if (grey) { accentHue = 212; accentSat = 0.18f; baseSat = 0; } // greyscale image: calm steel accent
            else accentSat = Clamp(Math.Max(accentSat * 1.3f, 0.55f), 0.55f, 0.9f);
            if (baseSat < 0.04f) baseHue = accentHue;

            Color N(float l, float satMul = 1f) => FromHsl(baseHue, Clamp(baseSat * satMul, 0, 1), l);

            return light
                ? new Theme
                {
                    Name = "From image",
                    Background = N(0.955f), Surface = N(0.99f, 0.5f), Selection = N(0.87f, 1.6f), Border = N(0.82f),
                    Text = N(0.12f, 0.6f), SubText = N(0.42f, 0.5f),
                    Accent = FromHsl(accentHue, accentSat, 0.42f),
                }
                : new Theme
                {
                    Name = "From image",
                    Background = N(0.105f), Surface = N(0.155f), Selection = N(0.22f, 1.4f), Border = N(0.27f),
                    Text = N(0.95f, 0.4f), SubText = N(0.68f, 0.5f),
                    Accent = FromHsl(accentHue, accentSat, 0.66f),
                };
        }

        static double MidLightness(float l) => l < 0.15 || l > 0.9 ? 0.2 : 1.0 - Math.Abs(l - 0.55) * 1.2;

        static List<Color> Sample(Bitmap src, int n)
        {
            using var small = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, n, n);
            }
            var data = small.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf = new int[n * n];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            small.UnlockBits(data);
            return buf.Select(v => Color.FromArgb(255, Color.FromArgb(v))).ToList();
        }

        sealed class Cluster { public Color Color; public int Count; }

        static List<Cluster> KMeans(List<Color> px, int k)
        {
            // deterministic start: pixels spread across the brightness range
            var sorted = px.OrderBy(Lum).ToList();
            var cent = Enumerable.Range(0, k).Select(i => sorted[(int)((i + 0.5) * sorted.Count / k)]).Select(c => new double[] { c.R, c.G, c.B }).ToArray();
            var assign = new int[px.Count];
            for (int iter = 0; iter < 10; iter++)
            {
                for (int i = 0; i < px.Count; i++)
                {
                    double best = double.MaxValue;
                    for (int j = 0; j < k; j++)
                    {
                        double dr = px[i].R - cent[j][0], dg = px[i].G - cent[j][1], db = px[i].B - cent[j][2];
                        double d = dr * dr + dg * dg + db * db;
                        if (d < best) { best = d; assign[i] = j; }
                    }
                }
                var sum = new double[k, 3]; var cnt = new int[k];
                for (int i = 0; i < px.Count; i++) { int j = assign[i]; sum[j, 0] += px[i].R; sum[j, 1] += px[i].G; sum[j, 2] += px[i].B; cnt[j]++; }
                for (int j = 0; j < k; j++) if (cnt[j] > 0) cent[j] = new[] { sum[j, 0] / cnt[j], sum[j, 1] / cnt[j], sum[j, 2] / cnt[j] };
            }
            var counts = new int[k];
            foreach (var a in assign) counts[a]++;
            return Enumerable.Range(0, k).Where(j => counts[j] > 0)
                .Select(j => new Cluster { Color = Color.FromArgb((int)cent[j][0], (int)cent[j][1], (int)cent[j][2]), Count = counts[j] })
                .ToList();
        }

        static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        static float Clamp(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));

        public static Color FromHsl(float h, float s, float l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60.0, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; } else if (hp < 2) { r = x; g = c; } else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; } else if (hp < 5) { r = x; b = c; } else { r = c; b = x; }
            int To(double v) => Math.Max(0, Math.Min(255, (int)Math.Round((v + m) * 255)));
            return Color.FromArgb(To(r), To(g), To(b));
        }
    }

    /// <summary>Reads the current desktop wallpaper and how it's fitted to the screen.</summary>
    static class Wallpaper
    {
        public enum Fit { Fill, Fit, Stretch }

        /// <summary>
        /// The picture that's on screen: while Wallpaper Engine plays a wallpaper, its preview picture
        /// (Wallpaper Engine draws over the Windows wallpaper), otherwise the Windows wallpaper.
        /// </summary>
        public static string GetPath()
        {
            try { var we = WallpaperEngine.CurrentPreviewIfRunning(); if (we != null) return we; } catch { }
            return GetWindowsPath();
        }

        /// <summary>The Windows desktop wallpaper file (ignores Wallpaper Engine).</summary>
        public static string GetWindowsPath()
        {
            try
            {
                var sb = new StringBuilder(1024);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0) && File.Exists(sb.ToString()))
                    return sb.ToString();
            }
            catch { }
            // Slideshows / Spotlight keep the current picture here
            var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            return File.Exists(transcoded) ? transcoded : null;
        }

        public static Fit GetFit()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                switch (key?.GetValue("WallpaperStyle") as string)
                {
                    case "2": return Fit.Stretch;
                    case "6": return Fit.Fit;
                    default: return Fit.Fill; // Fill, Span, Center, Tile: close enough for a backdrop
                }
            }
            catch { return Fit.Fill; }
        }

        const int SPI_GETDESKWALLPAPER = 0x73;
        public const int SPI_SETDESKWALLPAPER = 0x14;

        /// <summary>Sets the desktop wallpaper (keeps the current fit setting). Can take a moment: call off the UI thread.</summary>
        public static bool Set(string path) =>
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, 0x1 | 0x2 /* UPDATEINIFILE | SENDCHANGE */);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfo")]
        static extern bool SystemParametersInfo(int action, int param, string value, int winIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);
    }
}
