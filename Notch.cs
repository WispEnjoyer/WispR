using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The little tab hanging from the top edge that shows where the drop-down lives. Same shape as the
    /// panel in miniature (flares into the edge, rounded below) with a grip line; it grows and lights up
    /// in the accent colour as the mouse comes close. Click-through: it never gets in the way.
    /// </summary>
    sealed class NotchWindow : Form
    {
        readonly float s;
        float level, target;      // 0 = resting, 1 = mouse right at it
        int centerX, edge;
        Theme theme; byte alpha = 255;
        string drawnKey;

        public NotchWindow(float scale)
        {
            s = scale;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "WispR Notch";
            _ = Handle;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8; // LAYERED | TRANSPARENT (click-through) | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84 /* WM_NCHITTEST */) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; return; }
            base.WndProc(ref m);
        }

        /// <summary>Places the notch (centred on <paramref name="x"/>, hanging from <paramref name="edgeY"/>) and sets how lit it should be.</summary>
        public void Set(int x, int edgeY, float hover, Theme t, byte opacity)
        {
            bool moved = x != centerX || edgeY != edge || t != theme || opacity != alpha;
            centerX = x; edge = edgeY; theme = t; alpha = opacity;
            target = Math.Max(0, Math.Min(1, hover));
            if (!Visible) { level = target; Render(); Show(); return; }
            if (moved) Render();
            if (Math.Abs(level - target) > 0.001f && !Anim.FramesRunning(this))
                Anim.Frames(this, () =>
                {
                    if (IsDisposed || !Visible) return false;
                    bool moving = Anim.Approach(ref level, target, Anim.PerFrame(target > level ? 0.09f : 0.06f));
                    Render();
                    return moving;
                });
        }

        public void Conceal()
        {
            Anim.StopFrames(this);
            if (Visible) Hide();
            drawnKey = null;
        }

        void Render()
        {
            if (theme == null) return;
            float e = (float)Anim.OutCubic(level);
            int bodyW = (int)Math.Round((54 + 34 * e) * s), h = (int)Math.Round((7 + 4 * e) * s);
            int flare = Math.Max(1, (int)Math.Round(Ui.Corner * 0.4f * s)), radius = Math.Max(1, Math.Min(h, (int)(Ui.Corner * 0.45f * s))); // follows the corner roundness
            int w = bodyW + flare * 2;
            string key = centerX + "|" + edge + "|" + w + "|" + h + "|" + (int)(e * 64) + "|" + theme.Accent.ToArgb() + theme.Background.ToArgb() + alpha;
            if (key == drawnKey) return;
            drawnKey = key;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                using var shape = LauncherShell.Shape(w, h, flare, radius);
                using (var flip = new Matrix(1, 0, 0, -1, 0, h)) shape.Transform(flip); // flares into the top edge
                using (var b = new SolidBrush(theme.Background)) g.FillPath(b, shape);
                using (var pen = new Pen(Color.FromArgb((int)(70 + 60 * e), Ui.Mix(theme.Border, theme.Accent, e)))) g.DrawPath(pen, shape);
                // the grip: a short line that warms up to the accent colour
                float gw = (16 + 14 * e) * s, gh = Math.Max(2f, 2.4f * s);
                var grip = new RectangleF((w - gw) / 2f, h - gh - Math.Max(2f, 2.2f * s), gw, gh);
                using var gp = Ui.Round(grip, gh / 2);
                using var gb = new SolidBrush(Ui.Mix(Color.FromArgb(170, theme.SubText), theme.Accent, e));
                g.FillPath(gb, gp);
            }
            var pos = new Rectangle(centerX - w / 2, edge, w, h);
            if (Bounds != pos) Bounds = pos;
            Push(bmp, pos.Location);
        }

        void Push(Bitmap bmp, Point at)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hbmp);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var dst = new POINT { x = at.X, y = at.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            catch { }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Anim.StopFrames(this);
            base.Dispose(disposing);
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
