using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Live window previews shown when hovering an app on the taskbar. The pictures are real
    /// DWM thumbnails (the same thing Windows' taskbar uses), so they update live.
    /// </summary>
    sealed class PreviewForm : Form
    {
        sealed class Card
        {
            public AppWindow Window;
            public Rectangle Bounds, Thumb, CloseBox;
            public IntPtr Handle; // DWM thumbnail
        }

        readonly Settings settings;
        readonly float s;
        readonly List<Card> cards = new List<Card>();
        readonly Font titleFont = new Font("Segoe UI", 9f);
        readonly Font glyphFont;
        Bitmap appIcon;
        int hover = -1;
        bool hoverClose;

        public TaskButton Button { get; private set; }
        public event Action<IntPtr> WindowChosen;

        Theme T => settings.Theme;

        public PreviewForm(Settings settings)
        {
            this.settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            glyphFont = new Font(BarForm.GlyphFamily, 8f);
        }

        int S(float px) => (int)Math.Round(px * s);

        /// <summary>Shows previews of <paramref name="b"/>'s windows above/below the button.</summary>
        public void ShowFor(TaskButton b, Rectangle buttonScreen, bool above)
        {
            Clear();
            Button = b;
            appIcon = b.Icon;
            BackColor = above ? T.Background : T.Surface; // growing out of the bar: same colour as the bar

            int pad = S(8), cardW = S(230), titleH = S(28), thumbH = S(132), gap = S(6);
            var scr = Screen.FromRectangle(buttonScreen).WorkingArea;
            int maxCards = Math.Max(1, (scr.Width - pad * 2) / (cardW + gap));
            int n = Math.Min(b.Windows.Count, maxCards);
            if (n == 0) { Hide(); return; }

            int w = pad * 2 + n * cardW + (n - 1) * gap, h = pad * 2 + titleH + thumbH;
            int x = Math.Max(scr.Left + S(6), Math.Min(scr.Right - w - S(6), buttonScreen.X + buttonScreen.Width / 2 - w / 2));
            var (edge, curves) = GrowOut.EdgeFor(buttonScreen, buttonScreen.Top - S(4));
            int y = above ? edge - h : buttonScreen.Bottom + S(14);
            Anim.Settle(this);
            Bounds = new Rectangle(x, y, w, h);

            for (int i = 0; i < n; i++)
            {
                var c = new Card { Window = b.Windows[i] };
                c.Bounds = new Rectangle(pad + i * (cardW + gap), pad, cardW, titleH + thumbH);
                c.Thumb = new Rectangle(c.Bounds.X + S(6), c.Bounds.Y + titleH, cardW - S(12), thumbH - S(6));
                c.CloseBox = new Rectangle(c.Bounds.Right - S(26), c.Bounds.Y + S(3), S(22), S(22));
                cards.Add(c);
            }

            if (above)
            {
                // grows out of the bar; shown straight away (live window thumbnails don't clip while rising)
                GrowOut.PrepareWindow(this);
                GrowOut.Show(this, edge, curves, T, s, animate: false);
            }
            else if (!Visible) { Anim.PopIn(this, -S(8), 140); Show(); }
            RegisterThumbnails();
            Invalidate();
        }

        void RegisterThumbnails()
        {
            foreach (var c in cards)
            {
                if (Native.IsIconic(c.Window.Handle)) continue; // minimized windows have no live picture; show the icon
                if (DwmRegisterThumbnail(Handle, c.Window.Handle, out c.Handle) != 0) { c.Handle = IntPtr.Zero; continue; }
                DwmQueryThumbnailSourceSize(c.Handle, out var src);
                var dest = Fit(c.Thumb, src.cx, src.cy);
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 | 0x8 | 0x10, // destination | visible | client area only
                    rcDestination = new Native.RECT { Left = dest.Left, Top = dest.Top, Right = dest.Right, Bottom = dest.Bottom },
                    fVisible = true,
                    fSourceClientAreaOnly = false,
                };
                DwmUpdateThumbnailProperties(c.Handle, ref props);
            }
        }

        static Rectangle Fit(Rectangle box, int w, int h)
        {
            if (w <= 0 || h <= 0) return box;
            float sc = Math.Min((float)box.Width / w, (float)box.Height / h);
            int dw = (int)(w * sc), dh = (int)(h * sc);
            return new Rectangle(box.X + (box.Width - dw) / 2, box.Y + (box.Height - dh) / 2, dw, dh);
        }

        void Clear()
        {
            foreach (var c in cards) if (c.Handle != IntPtr.Zero) DwmUnregisterThumbnail(c.Handle);
            cards.Clear();
            hover = -1;
            hoverClose = false;
        }

        public void HidePreview()
        {
            Clear();
            Button = null;
            if (Visible) Hide();
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (i == hover)
                    using (var p = BarForm.Rounded(c.Bounds, S(6)))
                    using (var b = new SolidBrush(T.Selection))
                        g.FillPath(b, p);

                int ic = S(16);
                var titleRect = new Rectangle(c.Bounds.X + S(8), c.Bounds.Y, c.Bounds.Width - S(40), S(28));
                if (appIcon != null) g.DrawImage(appIcon, new Rectangle(titleRect.X, titleRect.Y + (titleRect.Height - ic) / 2, ic, ic));
                TextRenderer.DrawText(g, c.Window.Title, titleFont, new Rectangle(titleRect.X + ic + S(6), titleRect.Y, titleRect.Width - ic - S(6), titleRect.Height),
                    T.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                if (i == hover)
                {
                    if (hoverClose)
                        using (var p = BarForm.Rounded(c.CloseBox, S(4)))
                        using (var b = new SolidBrush(Color.FromArgb(196, 43, 28)))
                            g.FillPath(b, p);
                    TextRenderer.DrawText(g, "", glyphFont, c.CloseBox, hoverClose ? Color.White : T.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                if (c.Handle == IntPtr.Zero) // minimized (or no live picture): big app icon instead
                {
                    using (var p = BarForm.Rounded(c.Thumb, S(4)))
                    using (var b = new SolidBrush(T.Background))
                        g.FillPath(b, p);
                    int big = S(48);
                    if (appIcon != null)
                        g.DrawImage(appIcon, new Rectangle(c.Thumb.X + (c.Thumb.Width - big) / 2, c.Thumb.Y + (c.Thumb.Height - big) / 2, big, big));
                    if (Native.IsIconic(c.Window.Handle))
                        TextRenderer.DrawText(g, "Minimized", titleFont, new Rectangle(c.Thumb.X, c.Thumb.Bottom - S(24), c.Thumb.Width, S(20)), T.SubText,
                            TextFormatFlags.HorizontalCenter);
                }
            }
            if (!SystemStatus.IsWindows11)
                using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        // ---------- mouse ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = cards.FindIndex(c => c.Bounds.Contains(e.Location));
            bool hc = h >= 0 && cards[h].CloseBox.Contains(e.Location);
            if (h != hover || hc != hoverClose) { hover = h; hoverClose = hc; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverClose = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = cards.FindIndex(c => c.Bounds.Contains(e.Location));
            if (i < 0) return;
            var c = cards[i];
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && c.CloseBox.Contains(e.Location)))
            {
                Native.Close(c.Window.Handle);
                HidePreview();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                var h = c.Window.Handle;
                HidePreview();
                WindowChosen?.Invoke(h);
            }
        }

        // ---------- window plumbing ----------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2, dark = T.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // TOOLWINDOW | NOACTIVATE | TOPMOST
                // (no drop shadow: it grows out of the bar)
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; return; }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Clear(); titleFont.Dispose(); glyphFont.Dispose(); }
            base.Dispose(disposing);
        }

        // ---------- DWM ----------

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public Native.RECT rcDestination;
            public Native.RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    }
}
