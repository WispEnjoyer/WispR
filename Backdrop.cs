using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The shared background picture (custom image or desktop wallpaper) used by the launcher
    /// and the taskbar, plus the image-derived colour theme.
    /// </summary>
    sealed class Backdrop
    {
        readonly Settings settings;
        Bitmap source;
        string sourceSig, paletteKey, sourcePath;
        byte[] fingerprint;          // tiny summary of what the picture looks like
        int version;                 // bumps only when the picture visibly changed
        DateTime lastReload = DateTime.MinValue;

        public Backdrop(Settings settings) { this.settings = settings; }

        string ImagePath => settings.UseWallpaper ? Wallpaper.GetPath() : settings.BackgroundImage;

        /// <summary>Reloads the picture / regenerates the palette if needed. Returns true if anything changed.</summary>
        public bool Refresh()
        {
            bool changed = false;
            string path = ImagePath, sig = null;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    sig = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch { }

            // A file that keeps changing (e.g. rewritten by a wallpaper app) is re-read at most every 2 min.
            if (sig != sourceSig && path == sourcePath && sig != null && (DateTime.Now - lastReload).TotalSeconds < 120)
                sig = sourceSig;

            if (sig != sourceSig)
            {
                lastReload = DateTime.Now;
                bool samePath = path == sourcePath;
                sourcePath = path;
                sourceSig = sig;
                Bitmap loaded = null;
                if (sig != null)
                    try
                    {
                        loaded = ImageLoad.FromFile(path, ImageLoad.ScreenSide());
                    }
                    catch { loaded = null; }

                var fp = loaded == null ? null : Fingerprint(loaded);
                if (samePath && loaded != null && source != null && Similar(fp, fingerprint))
                {
                    loaded.Dispose(); // same picture saved again (wallpaper apps do this): nothing to redo
                }
                else
                {
                    source?.Dispose();
                    source = loaded;
                    fingerprint = fp;
                    version++;
                    changed = true;
                    Log.Throttled("backdrop", "Background picture changed (" + (path ?? "none") + ").");
                }
            }

            if (!settings.MatchImageColors || source == null) paletteKey = null;
            else
            {
                string key = version + "|" + settings.PaletteMode;
                if (key != paletteKey)
                {
                    paletteKey = key;
                    try
                    {
                        settings.Theme = Palette.FromImage(source, settings.PaletteMode);
                        settings.Save();
                        settings.RaiseThemeRecomputed();
                        changed = true;
                    }
                    catch { }
                }
            }
            return changed;
        }

        /// <summary>Identifies what <see cref="Render"/> would produce, for caching.</summary>
        public string KeyFor(Rectangle screenRect) => string.Join("|", version, sourceSig != null, settings.ShowImage,
            settings.UseWallpaper ? screenRect.ToString() : screenRect.Size.ToString(),
            settings.ImageDim, settings.ImageBlur, settings.Theme.Background.ToArgb());

        /// <summary>8×8 colour summary of a picture.</summary>
        static byte[] Fingerprint(Bitmap b)
        {
            using var small = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(b, 0, 0, 8, 8);
            }
            var fp = new byte[8 * 8 * 3];
            int k = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var c = small.GetPixel(x, y);
                    fp[k++] = c.R; fp[k++] = c.G; fp[k++] = c.B;
                }
            return fp;
        }

        /// <summary>Do two summaries look the same (tiny differences, e.g. from re-compressing, don't count)?</summary>
        static bool Similar(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            long diff = 0;
            for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
            return diff / (double)a.Length < 6; // average difference under 6 of 255
        }

        /// <summary>The backdrop for a window at <paramref name="screenRect"/>, or null when there's no picture.</summary>
        public Bitmap Render(Rectangle screenRect)
        {
            if (source == null || !settings.ShowImage || screenRect.Width <= 0 || screenRect.Height <= 0) return null;
            try
            {
                int w = screenRect.Width, h = screenRect.Height;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(settings.Theme.Background);
                    if (settings.UseWallpaper)
                    {
                        // The part of the wallpaper that's behind the window: a frosted-glass look.
                        var screen = Screen.FromRectangle(screenRect).Bounds;
                        DrawWallpaperSlice(g, source, screen, screenRect);
                    }
                    else
                    {
                        float sc = Math.Max((float)w / source.Width, (float)h / source.Height);
                        float dw = source.Width * sc, dh = source.Height * sc;
                        g.DrawImage(source, new RectangleF((w - dw) / 2, (h - dh) / 2, dw, dh));
                    }
                }
                if (settings.ImageBlur) bmp = Blur(bmp);
                using (var g = Graphics.FromImage(bmp))
                using (var tint = new SolidBrush(Color.FromArgb(settings.ImageDim * 255 / 100, settings.Theme.Background)))
                    g.FillRectangle(tint, 0, 0, bmp.Width, bmp.Height);
                return bmp;
            }
            catch { return null; }
        }

        static void DrawWallpaperSlice(Graphics g, Bitmap img, Rectangle screen, Rectangle win)
        {
            var fit = Wallpaper.GetFit();
            float sx, sy;
            if (fit == Wallpaper.Fit.Stretch)
            {
                sx = (float)screen.Width / img.Width;
                sy = (float)screen.Height / img.Height;
            }
            else
            {
                float sc = fit == Wallpaper.Fit.Fit
                    ? Math.Min((float)screen.Width / img.Width, (float)screen.Height / img.Height)
                    : Math.Max((float)screen.Width / img.Width, (float)screen.Height / img.Height);
                sx = sy = sc;
            }
            float ox = screen.X + (screen.Width - img.Width * sx) / 2;
            float oy = screen.Y + (screen.Height - img.Height * sy) / 2;

            var src = new RectangleF((win.X - ox) / sx, (win.Y - oy) / sy, win.Width / sx, win.Height / sy);
            var clipped = RectangleF.Intersect(src, new RectangleF(0, 0, img.Width, img.Height));
            if (clipped.Width <= 0 || clipped.Height <= 0) return;
            var dest = Rectangle.Round(new RectangleF((clipped.X - src.X) * sx, (clipped.Y - src.Y) * sy, clipped.Width * sx, clipped.Height * sy));
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(img, dest, clipped.X, clipped.Y, clipped.Width, clipped.Height, GraphicsUnit.Pixel, attrs);
        }

        // Cheap, smooth blur: shrink a lot, then scale back up with bicubic filtering.
        static Bitmap Blur(Bitmap bmp)
        {
            int sw = Math.Max(2, bmp.Width / 16), sh = Math.Max(2, bmp.Height / 16);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(bmp, new Rectangle(0, 0, sw, sh), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            }
            var result = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(small, new Rectangle(0, 0, result.Width, result.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, attrs);
            }
            bmp.Dispose();
            return result;
        }
    }
}
