using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The screen frame: a border all the way around the screen in the theme's background (or the frosted
    /// wallpaper), thin on three sides and as tall as the bars on the taskbar side, with rounded inner
    /// corners — so the desktop and your windows sit inside one continuous frame and the bars are part
    /// of it. Drawn with per-pixel transparency and click-through: clicks pass straight to what's below.
    /// </summary>
    sealed class FrameForm : Form
    {
        string renderedKey;

        public FrameForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "WispR Frame";
            _ = Handle;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST | TRANSPARENT (clicks go through)
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8 | 0x20;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; return; }
            if (m.Msg == 0x84 /* WM_NCHITTEST */) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        /// <summary>The area inside the frame (screen coordinates).</summary>
        public static Rectangle Inner(Rectangle screen, int side, int band, bool bandAtBottom) =>
            bandAtBottom ? Rectangle.FromLTRB(screen.Left + side, screen.Top + side, screen.Right - side, screen.Bottom - band)
                         : Rectangle.FromLTRB(screen.Left + side, screen.Top + band, screen.Right - side, screen.Bottom - side);

        /// <summary>Draws the frame for <paramref name="screen"/> (only when something about it changed).</summary>
        public void Render(Rectangle screen, Rectangle inner, int radius, Backdrop backdrop, Theme t, bool showImage, int opacityPercent)
        {
            string key = string.Join("|", screen, inner, radius, backdrop.KeyFor(screen), showImage, opacityPercent,
                t.Background.ToArgb(), t.Border.ToArgb());
            if (key == renderedKey && Native.IsWindowVisible(Handle)) return;
            renderedKey = key;
            if (screen.Width <= 0 || screen.Height <= 0) return;
            // keep WinForms' idea of the window's size in step with the picture; otherwise showing the
            // window would shrink it back to the default size and only a corner of the frame would show
            if (Bounds != screen) Bounds = screen;

            using var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var hole = new RectangleF(inner.X - screen.X, inner.Y - screen.Y, inner.Width, inner.Height);
                using var holePath = Ui.Round(hole, radius);
                using var frame = new GraphicsPath(FillMode.Alternate);
                frame.AddRectangle(new Rectangle(0, 0, screen.Width, screen.Height));
                frame.AddPath(holePath, false); // the inside is cut out

                Bitmap picture = showImage ? backdrop.Render(screen) : null;
                try
                {
                    if (picture != null)
                        using (var tb = new TextureBrush(picture, WrapMode.Clamp)) g.FillPath(tb, frame);
                    else
                        using (var b = new SolidBrush(t.Background)) g.FillPath(b, frame);
                }
                finally { picture?.Dispose(); }
                // a fine edge where the frame meets the desktop
                using (var pen = new Pen(Color.FromArgb(110, t.Border), 1f)) g.DrawPath(pen, holePath);
            }
            byte alpha = (byte)Math.Max(0, Math.Min(255, opacityPercent * 255 / 100));
            Push(bmp, screen.Location, alpha);
        }

        public void Invalidated() => renderedKey = null;

        void Push(Bitmap bmp, Point at, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screenDc), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hbmp);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var dst = new POINT { x = at.X, y = at.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    }
}
