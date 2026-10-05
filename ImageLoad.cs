using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WispR
{
    /// <summary>
    /// Decodes pictures that come from outside (wallpapers, Wallpaper Engine previews, album covers, the
    /// account picture) safely and lightly: the size in the file's header is checked before any pixels are
    /// decoded (a small file can claim to be 20000×20000 pixels), and the picture is scaled down to what's
    /// actually needed, so an 8K wallpaper doesn't sit in memory at full size.
    /// </summary>
    static class ImageLoad
    {
        const long MaxPixels = 80_000_000; // ~ 10000 × 8000: anything bigger is refused

        public static Bitmap FromStream(Stream s, int maxSide)
        {
            using var img = Image.FromStream(s, false, false); // reads the header; pixels are decoded on draw
            long px = (long)img.Width * img.Height;
            if (img.Width <= 0 || img.Height <= 0 || px > MaxPixels) return null;
            double scale = Math.Min(1.0, maxSide / (double)Math.Max(img.Width, img.Height));
            int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(img, new Rectangle(0, 0, w, h));
            }
            return bmp;
        }

        public static Bitmap FromFile(string path, int maxSide)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return FromStream(fs, maxSide);
        }

        /// <summary>The longest side of the biggest screen: wallpapers are never needed larger than that.</summary>
        public static int ScreenSide()
        {
            int side = 1920;
            try { foreach (var sc in System.Windows.Forms.Screen.AllScreens) side = Math.Max(side, Math.Max(sc.Bounds.Width, sc.Bounds.Height)); } catch { }
            return side;
        }
    }
}
