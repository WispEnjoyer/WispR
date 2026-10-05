using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The outline of the launcher when it's attached to the bottom of the screen: rounded top corners
    /// and sides that curve outwards into the screen edge, so the launcher looks like it grows out of it.
    /// Drawn with per-pixel transparency (smooth, anti-aliased curves), which a normal window with text
    /// boxes can't do — so this sits *behind* the launcher window, which shows the search box and results.
    /// The area under the launcher window is left fully transparent, so see-through settings stay even.
    /// </summary>
    sealed class LauncherShell : Form
    {
        public LauncherShell()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "WispR Launcher Frame";
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; return; }
            base.WndProc(ref m);
        }

        /// <summary>A rounded rectangle (used when the panel ends above the screen edge).</summary>
        public static GraphicsPath RoundedBox(RectangleF r, float radius)
        {
            float d = Math.Max(1, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>The outline: rounded top corners, straight sides, outward curves at the bottom.</summary>
        public static GraphicsPath Shape(float w, float h, float flare, float radius)
        {
            float left = flare, right = w - flare;
            var p = new GraphicsPath();
            p.AddArc(-flare, h - 2 * flare, 2 * flare, 2 * flare, 90, -90);          // left curve into the screen edge
            p.AddArc(left, 0, 2 * radius, 2 * radius, 180, 90);                      // top-left corner
            p.AddArc(right - 2 * radius, 0, 2 * radius, 2 * radius, 270, 90);        // top-right corner
            p.AddArc(right, h - 2 * flare, 2 * flare, 2 * flare, 180, -90);          // right curve into the screen edge
            p.CloseFigure();                                                         // along the screen edge
            return p;
        }

        /// <summary>
        /// Draws the frame at <paramref name="screenRect"/>. <paramref name="hole"/> (screen coordinates) is
        /// where the launcher window sits; it stays transparent. The fill is taken from <paramref name="background"/>
        /// (whose top-left is at <paramref name="backgroundAt"/> on screen) or is a solid colour.
        /// </summary>
        public void Render(Rectangle screenRect, Rectangle hole, int flare, int radius,
                           Bitmap background, Point backgroundAt, Color fill, Color border, byte alpha,
                           bool floating = false, Rectangle? dockHole = null, float dockRadius = 0)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
            using var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // floating: a rounded panel above the screen edge (the taskbar floats); otherwise it
                // flows into the screen edge with outward curves
                using var path = floating
                    ? RoundedBox(new RectangleF(flare + 0.5f, 0.5f, screenRect.Width - 2 * flare - 1, screenRect.Height - 1), radius)
                    : Shape(screenRect.Width, screenRect.Height, flare, radius);
                if (background != null)
                {
                    using var tb = new TextureBrush(background, WrapMode.Clamp);
                    tb.TranslateTransform(backgroundAt.X - screenRect.X, backgroundAt.Y - screenRect.Y);
                    g.FillPath(tb, path);
                }
                else using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(90, border), 1f)) g.DrawPath(pen, path);

                // where the launcher window is: fully transparent
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.None;
                using var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
                g.FillRectangle(clear, hole.X - screenRect.X, hole.Y - screenRect.Y, hole.Width, hole.Height);
                if (dockHole is Rectangle dh) // under the taskbar: it draws itself there
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var r = new RectangleF(dh.X - screenRect.X + 1, dh.Y - screenRect.Y + 1, dh.Width - 2, dh.Height - 2);
                    using var hp = RoundedBox(r, Math.Max(1, dockRadius - 1));
                    g.FillPath(clear, hp);
                }
            }
            Push(bmp, screenRect.Location, alpha);
            if (!Native.IsWindowVisible(Handle)) Native.ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
        }

        void Push(Bitmap bmp, Point at, byte alpha)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hbmp);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var dst = new POINT { x = at.X, y = at.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
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
