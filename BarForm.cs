using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>A clickable area on a bar.</summary>
    sealed class BarItem
    {
        public Rectangle Bounds;
        public Func<string> Tooltip;
        public Action<Graphics, Rectangle, bool> Paint;           // (g, bounds, hovered)
        public Action<MouseButtons, Point, bool> Click;          // (button, screen point, double click)
        public Action<int> Wheel;
        public bool HoverBackground = true;
        public object Tag;
    }

    /// <summary>
    /// Base for the taskbar and the system box: a floating, rounded, always-on-top window that
    /// never takes focus (so clicking it doesn't deactivate the app you're working in).
    /// </summary>
    abstract class BarForm : Form
    {
        protected readonly Settings settings;
        protected readonly Backdrop backdrop;
        protected readonly float s;
        protected readonly List<BarItem> items = new List<BarItem>();
        protected Theme T => settings.Theme;

        // Items are rebuilt often (every second in the system box), so hover/press are remembered by
        // position rather than by object — otherwise a rebuild between press and release lost the click.
        Rectangle hoverBounds, pressedBounds;

        // ---- small animations: hover highlights fade, pressed buttons dip ----
        readonly Dictionary<Rectangle, float> hoverLevel = new Dictionary<Rectangle, float>();
        float pressLevel;
        Rectangle pressAnimBounds;

        /// <summary>Subclasses' own animations: advance one frame, return true while still moving.</summary>
        protected virtual bool AnimateStep() => false;

        /// <summary>Starts the frame timer (it stops by itself once everything has settled).</summary>
        protected void Animate() { if (IsHandleCreated && !IsDisposed && !Anim.FramesRunning(this)) Anim.Frames(this, AnimTick); }

        bool AnimTick()
        {
            if (IsDisposed) return false;
            bool moving = false;
            // only repaint what animates: the buttons whose highlight is fading or that are pressed
            var dirty = pressAnimBounds;
            foreach (var key in hoverLevel.Keys) dirty = dirty.IsEmpty ? key : Rectangle.Union(dirty, key);
            foreach (var key in hoverLevel.Keys.ToList())
            {
                float v = hoverLevel[key];
                bool target = key == hoverBounds;
                moving |= Anim.Approach(ref v, target ? 1f : 0f, Anim.PerFrame(target ? 0.16f : 0.09f)); // in ~100 ms, out ~170 ms
                if (v <= 0 && !target) hoverLevel.Remove(key); else hoverLevel[key] = v;
            }
            moving |= Anim.Approach(ref pressLevel, pressedBounds.IsEmpty ? 0f : 1f, Anim.PerFrame(pressedBounds.IsEmpty ? 0.12f : 0.3f));
            if (pressLevel <= 0) pressAnimBounds = Rectangle.Empty;
            bool own = AnimateStep();
            if (own) Invalidate();
            else if (!dirty.IsEmpty) Invalidate(Rectangle.Inflate(dirty, 2, 2));
            return moving || own;
        }
        Bitmap bg;
        string bgKey;
        static TipForm tip;
        DateTime lastClick;
        Rectangle lastClickBounds;

        protected BarForm(Settings settings, Backdrop backdrop)
        {
            this.settings = settings;
            this.backdrop = backdrop;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            // No minimize/maximize: Show desktop (Win+D) and "minimize all" (Win+M) skip windows without them.
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            tip ??= new TipForm();
        }

        protected int S(float px) => (int)Math.Round(px * s);

        /// <summary>Lays out <see cref="items"/> for the current state and returns the bar's size.</summary>
        public abstract Size Measure();

        public event Action SizeWanted;
        protected void RequestRelayout() => SizeWanted?.Invoke();

        public virtual void ApplyTheme()
        {
            BackColor = T.Background;
            double op = settings.Opacity / 100.0;
            if (Math.Abs(Opacity - op) > 0.001) Opacity = op;
            UpdateBackdrop(); // only re-rendered if the picture, colours or position actually changed
            if (IsHandleCreated) ApplyDwm();
            Invalidate();
        }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); UpdateBackdrop(); }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateBackdrop();
            if (!SystemStatus.IsWindows11 && RoundCorners && Width > 0 && Height > 0)
                using (var p = Rounded(new Rectangle(0, 0, Width, Height), S(8))) Region = new Region(p);
        }

        void UpdateBackdrop()
        {
            if (Width <= 0 || Height <= 0) return;
            string key = backdrop.KeyFor(Bounds);
            if (key == bgKey) return;
            bgKey = key;
            bg?.Dispose();
            bg = backdrop.Render(Bounds);
            Invalidate();
        }

        void ApplyDwm()
        {
            try
            {
                int round = RoundCorners ? 2 : 1; // rounded (Windows 11) / square
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
                // Windows 11 puts a soft shadow around rounded windows; in a band that halo outlined each box
                int policy = RoundCorners ? 0 /* DWMNCRP_USEWINDOWSTYLE */ : 1 /* DWMNCRP_DISABLED */;
                Native.DwmSetWindowAttribute(Handle, 2 /* DWMWA_NCRENDERING_POLICY */, ref policy, 4);
                int none = unchecked((int)0xFFFFFFFE); // no border line around the boxes
                Native.DwmSetWindowAttribute(Handle, 34, ref none, 4);
                int dark = T.IsLight ? 0 : 1;
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwm();
            // belt and braces: strip the minimize/maximize styles at the window level too
            long style = Native.GetWindowLong(Handle, -16);
            SetWindowLong(Handle, -16, (int)(style & ~0x00020000L & ~0x00010000L)); // WS_MINIMIZEBOX, WS_MAXIMIZEBOX
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);

        /// <summary>Puts the bar back if something hid, minimized or buried it (called by the watchdog).</summary>
        public void EnsureShown(bool checkTopmost)
        {
            if (!IsHandleCreated) return;
            if (!Native.IsWindowVisible(Handle) || Native.IsIconic(Handle))
            {
                Log.Throttled("hidden:" + GetType().Name, "Watchdog: " + GetType().Name + " was " + (Native.IsIconic(Handle) ? "minimized" : "hidden") + " — showing it again. " + Log.Resources());
                Native.ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE: also un-minimizes */);
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            }
            // Only restore "always on top" if the bar actually lost it. Never push it above other windows
            // that are on top already — that covered (and closed) apps' tray menus and popups.
            if (checkTopmost && (Native.GetWindowLong(Handle, -20) & 0x8 /* WS_EX_TOPMOST */) == 0)
            {
                Log.Throttled("topmost:" + GetType().Name, "Watchdog: " + GetType().Name + " had lost always-on-top — restored.");
                Native.SetWindowPos(Handle, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200 /* NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER */);
            }
        }

        protected virtual bool RoundCorners => !onBand;

        bool onBand;
        /// <summary>
        /// Set while the box sits in a band (the screen frame or the strip behind maximized windows): it
        /// becomes part of that band, so square, with no shadow and no rounded edge to give it away.
        /// </summary>
        public bool OnBand
        {
            get => onBand;
            set
            {
                if (onBand == value) return;
                onBand = value;
                if (!IsHandleCreated) return;
                ApplyDwm();
                if (!SystemStatus.IsWindows11)
                {
                    var old = Region;
                    if (RoundCorners && Width > 0 && Height > 0)
                        using (var p = Rounded(new Rectangle(0, 0, Width, Height), S(8))) Region = new Region(p);
                    else Region = null;
                    old?.Dispose();
                }
                Invalidate();
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp; // no drop shadow: the boxes are part of the bar, not floating cards
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; /* MA_NOACTIVATE */ return; }
            if (m.Msg == 0x112 /* WM_SYSCOMMAND */)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == 0xF020 /* SC_MINIMIZE */ || cmd == 0xF030 /* SC_MAXIMIZE */) return; // bars never minimize
            }
            base.WndProc(ref m);
        }

        // ---------- painting ----------

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (bg != null) e.Graphics.DrawImage(bg, 0, 0);
            else using (var b = new SolidBrush(T.Background)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var it in items)
            {
                bool hov = it.Bounds == hoverBounds;
                hoverLevel.TryGetValue(it.Bounds, out float level);
                if (it.HoverBackground && level > 0.01f)
                {
                    int a = (int)(200 * level + (it.Bounds == pressAnimBounds ? 55 * pressLevel : 0));
                    using (var p = Rounded(it.Bounds, S(6)))
                    using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, a), T.Selection)))
                        g.FillPath(b, p);
                }
                var state = g.Save();
                if (it.Bounds == pressAnimBounds && pressLevel > 0.01f)
                {
                    // the pressed button dips slightly, then springs back on release
                    float k = 1 - 0.08f * pressLevel, cx = it.Bounds.X + it.Bounds.Width / 2f, cy = it.Bounds.Y + it.Bounds.Height / 2f;
                    g.TranslateTransform(cx, cy);
                    g.ScaleTransform(k, k);
                    g.TranslateTransform(-cx, -cy);
                }
                try { it.Paint?.Invoke(g, it.Bounds, hov); }
                catch (Exception ex) { Log.Error(GetType().Name + ".Paint", ex); } // one broken button must not blank the rest
                g.Restore(state);
            }
        }

        // ---------- mouse ----------

        BarItem HitTest(Point p) => items.LastOrDefault(i => i.Bounds.Contains(p));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitTest(e.Location);
            var hb = h?.Bounds ?? Rectangle.Empty;
            if (hb == hoverBounds) return;
            hoverBounds = hb;
            if (!hb.IsEmpty && !hoverLevel.ContainsKey(hb)) hoverLevel[hb] = 0;
            Animate();
            OnHoverChanged(h);
            var text = h?.Tooltip?.Invoke();
            if (string.IsNullOrEmpty(text)) tip.HideTip();
            else tip.ShowTip(text, RectangleToScreen(h.Bounds), settings.TaskbarEdge == "Top", T);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverBounds = pressedBounds = Rectangle.Empty;
            tip.HideTip();
            wheelHook?.Uninstall();
            OnHoverChanged(null);
            Animate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            pressedBounds = HitTest(e.Location)?.Bounds ?? Rectangle.Empty;
            if (!pressedBounds.IsEmpty) pressAnimBounds = pressedBounds;
            Animate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var it = HitTest(e.Location);
            bool same = it != null && it.Bounds == pressedBounds;
            pressedBounds = Rectangle.Empty;
            Animate();
            tip.HideTip();
            if (same)
            {
                bool dbl = e.Button == MouseButtons.Left && it.Bounds == lastClickBounds &&
                           (DateTime.Now - lastClick).TotalMilliseconds <= SystemInformation.DoubleClickTime;
                lastClick = dbl ? DateTime.MinValue : DateTime.Now; lastClickBounds = it.Bounds;
                it.Click?.Invoke(e.Button, PointToScreen(e.Location), dbl);
            }
            else if (it == null && e.Button == MouseButtons.Right) OnBackgroundRightClick(PointToScreen(e.Location));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            HitTest(e.Location)?.Wheel?.Invoke(e.Delta);
        }

        // The bars never have focus, so wheel turns are caught directly while the mouse is over one.
        WheelHook wheelHook;

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!items.Exists(i => i.Wheel != null)) return;
            wheelHook ??= new WheelHook(this, (delta, pt) => HitTest(PointToClient(pt))?.Wheel?.Invoke(delta));
            wheelHook.Install();
        }

        protected virtual void OnBackgroundRightClick(Point screen) { }

        /// <summary>Forget the pressed button (e.g. after a drag), so releasing doesn't count as a click.</summary>
        protected void CancelPress() { pressedBounds = Rectangle.Empty; Animate(); }
        protected virtual void OnHoverChanged(BarItem item) { }

        // ---------- shared drawing helpers ----------

        public static GraphicsPath Rounded(Rectangle r, float radius)
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

        static string glyphFamily;
        /// <summary>Windows' own icon font (Segoe Fluent Icons on 11, Segoe MDL2 Assets on 10).</summary>
        public static string GlyphFamily
        {
            get
            {
                if (glyphFamily != null) return glyphFamily;
                using var fonts = new InstalledFontCollection();
                var names = fonts.Families.Select(f => f.Name).ToList();
                glyphFamily = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                            : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "Segoe UI Symbol";
                return glyphFamily;
            }
        }

        protected void DrawGlyph(Graphics g, string glyph, Font font, Rectangle r, Color c) =>
            TextRenderer.DrawText(g, glyph, font, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        /// <summary>A themed right-click menu.</summary>
        protected BarMenu NewMenu() => new BarMenu(T, attachedStyle: settings.TaskbarEdge != "Top");

        /// <summary>Top of the bar band (frame or full-width strip) at a point, if one is showing (set by the bars).</summary>
        public static Func<Point, int?> BandTopAt;

        protected void AddItem(BarMenu m, string text, Action click, bool enabled = true, bool bold = false) =>
            m.AddItem(text, click, enabled, bold);

        /// <summary>Opens the menu above the bar (or below it when the bar is at the top).</summary>
        protected void PopUp(BarMenu m, Point pt)
        {
            tip.HideTip();
            if (settings.TaskbarEdge == "Top") { m.ShowAt(pt, Bounds, false); return; }
            // grows out of the band above the bars (curving into it), or straight out of this box
            int? band = BandTopAt?.Invoke(new Point(pt.X, Bounds.Top));
            m.ShowAttached(pt, band ?? Bounds.Top, band != null);
        }

        protected static void ShowError(string what, Exception ex)
        {
            if (ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223) return; // UAC cancelled
            MessageBox.Show("Couldn't complete \"" + what + "\":\n" + ex.Message, "WispR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { bg?.Dispose(); wheelHook?.Dispose(); Anim.StopFrames(this); }
            base.Dispose(disposing);
        }
    }

    /// <summary>A small themed tooltip that never takes focus.</summary>
    sealed class TipForm : Form
    {
        string text = "";
        Theme theme;
        readonly Font font = new Font("Segoe UI", 9f);

        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8 | 0x20; // TOOLWINDOW | NOACTIVATE | TOPMOST | TRANSPARENT
                return cp;
            }
        }

        public void ShowTip(string t, Rectangle anchor, bool below, Theme th)
        {
            Anim.Settle(this); // moving to another button mid-fade: jump to the end first
            text = t; theme = th;
            var size = TextRenderer.MeasureText(text, font, new Size(400, 0), TextFormatFlags.WordBreak);
            int pad = (int)(font.Height * 0.5);
            var sz = new Size(size.Width + pad * 2, size.Height + pad);
            var scr = Screen.FromRectangle(anchor).WorkingArea;
            int x = Math.Max(scr.Left + 4, Math.Min(scr.Right - sz.Width - 4, anchor.X + (anchor.Width - sz.Width) / 2));
            int y = below ? anchor.Bottom + pad : anchor.Top - sz.Height - pad;
            Bounds = new Rectangle(x, y, sz.Width, sz.Height);
            BackColor = theme.Surface;
            Invalidate();
            if (!Visible) { Anim.PopIn(this, below ? -4 : 4, 110); Show(); }
        }

        public void HideTip() { if (Visible) Hide(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (theme == null) return;
            using (var pen = new Pen(theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, text, font, ClientRectangle, theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }
}
