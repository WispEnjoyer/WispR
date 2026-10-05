using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The right-click menu for the bars, drawn in the WispR theme. It never takes focus
    /// (so the app you're working in stays active) and closes when you pick something, click
    /// anywhere else, press Esc, or switch windows.
    /// </summary>
    sealed class BarMenu : Form
    {
        sealed class Entry { public string Text; public Action Click; public bool Enabled = true, Bold, Separator; }

        static BarMenu current;
        public static bool IsOpen => current != null;

        readonly List<Entry> entries = new List<Entry>();
        readonly Theme t;
        readonly float s;
        readonly Font font = new Font("Segoe UI", 9.5f);
        readonly Font boldFont = new Font("Segoe UI Semibold", 9.5f);
        readonly Timer watch = new Timer { Interval = 40 };
        int hover = -1;
        IntPtr foregroundAtOpen;
        bool armed; // ignore the button release that opened us

        readonly bool attached;   // grows out of the bar (no gap, no shadow, curves into the edge)
        LauncherShell shell;      // draws the rounded top and the curves (per-pixel transparency)
        int edgeY, flare;

        public BarMenu(Theme theme, bool attachedStyle = false)
        {
            t = theme;
            attached = attachedStyle;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = attached ? t.Background : t.Surface;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            watch.Tick += (o, e) => Watch();
        }

        int S(float px) => (int)Math.Round(px * s);
        int ItemH => S(30);
        int SepH => S(9);

        public void AddItem(string text, Action click, bool enabled = true, bool bold = false) =>
            entries.Add(new Entry { Text = text, Click = click, Enabled = enabled && click != null, Bold = bold });

        public void AddSeparator()
        {
            if (entries.Count > 0 && !entries[entries.Count - 1].Separator) entries.Add(new Entry { Separator = true, Enabled = false });
        }

        Size MenuSize()
        {
            int w = S(190);
            foreach (var e in entries)
                if (!e.Separator) w = Math.Max(w, TextRenderer.MeasureText(e.Text, e.Bold ? boldFont : font).Width + S(40));
            int h = S(6) * 2;
            foreach (var e in entries) h += e.Separator ? SepH : ItemH;
            return new Size(w, h);
        }

        int FrameInset => (int)Math.Ceiling(Radius * 0.3) + 1;
        int Radius => Ui.CornerPx(s);

        /// <summary>
        /// Shows the menu growing up out of the bar: its bottom sits on <paramref name="edge"/> (the top of
        /// the bar band, or of the box), it rises from there, and with <paramref name="curves"/> its sides
        /// curve out into that edge like the launcher does.
        /// </summary>
        public void ShowAttached(Point pt, int edge, bool curves)
        {
            if (entries.Count > 0 && entries[entries.Count - 1].Separator) entries.RemoveAt(entries.Count - 1);
            current?.CloseMenu();
            current = this;
            var size = MenuSize();
            int w = size.Width, h = size.Height;
            edgeY = edge;
            flare = curves ? S(12) : 1;
            var scr = Screen.FromPoint(pt).Bounds;
            int m = FrameInset;
            int x = Math.Max(scr.Left + m + flare, Math.Min(scr.Right - w - m - flare, pt.X - w / 2));
            Bounds = new Rectangle(x, edge, w, h); // starts fully "inside" the edge, then rises
            shell = new LauncherShell();
            Owner = shell; // the menu always stays in front of its outline
            foregroundAtOpen = Native.GetForegroundWindow();
            Region = new Region(Rectangle.Empty);
            Show();
            Anim.Run(this, 160, e =>
            {
                if (IsDisposed) return;
                int visible = (int)Math.Round(h * e);
                Top = edgeY - visible;
                Region?.Dispose();
                Region = new Region(new Rectangle(0, 0, w, visible));
                RenderShell();
            }, () => { if (!IsDisposed) { Region?.Dispose(); Region = null; RenderShell(); } }, Anim.OutCubic);
            watch.Start();
        }

        void RenderShell()
        {
            if (shell == null || shell.IsDisposed) return;
            int m = FrameInset;
            int top = Math.Min(Top - m, edgeY - Radius * 2 - m); // keep the outline valid while it's still small
            var frame = new Rectangle(Left - m - flare, top, Width + (m + flare) * 2, edgeY - top);
            var hole = Rectangle.Intersect(Bounds, new Rectangle(Left, Top, Width, Math.Max(0, edgeY - Top)));
            try { shell.Render(frame, hole, flare, Radius, null, Point.Empty, t.Background, t.Border, 255); }
            catch (Exception ex) { Log.Error("BarMenu.Shell", ex); }
        }

        /// <summary>Shows the menu near <paramref name="pt"/>, above (or below) the bar rectangle.</summary>
        public void ShowAt(Point pt, Rectangle bar, bool above)
        {
            if (entries.Count > 0 && entries[entries.Count - 1].Separator) entries.RemoveAt(entries.Count - 1);
            current?.CloseMenu();
            current = this;

            var size = MenuSize();
            int w = size.Width, h = size.Height;

            var scr = Screen.FromPoint(pt).Bounds;
            int x = Math.Max(scr.Left + S(6), Math.Min(scr.Right - w - S(6), pt.X - w / 2));
            int y = above ? bar.Top - h - S(8) : bar.Bottom + S(8);
            y = Math.Max(scr.Top, Math.Min(scr.Bottom - h, y));
            Bounds = new Rectangle(x, y, w, h);

            foregroundAtOpen = Native.GetForegroundWindow();
            Anim.PopIn(this, above ? S(6) : -S(6), 130);
            Show();
            watch.Start();
        }

        void Watch()
        {
            bool anyButton = Down(0x01) || Down(0x02) || Down(0x04);
            if (!armed) { if (!anyButton) armed = true; return; }
            if (anyButton && !Bounds.Contains(Cursor.Position)) { CloseMenu(); return; }
            if (Down(0x1B /* Esc */)) { CloseMenu(); return; }
            if (Native.GetForegroundWindow() != foregroundAtOpen) CloseMenu();
        }

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        bool closingMenu;

        public void CloseMenu()
        {
            watch.Stop();
            if (current == this) current = null;
            if (closingMenu) return;
            closingMenu = true;
            if (IsDisposed || !Visible) { FinishClose(); return; }
            if (shell != null && !shell.IsDisposed)
            {
                // sinks back into the edge it grew out of
                int w = Width, from = Math.Max(0, edgeY - Top);
                Anim.Run(this, 110, e =>
                {
                    if (IsDisposed) return;
                    int visible = (int)Math.Round(from * (1 - e));
                    Top = edgeY - visible;
                    Region?.Dispose();
                    Region = new Region(new Rectangle(0, 0, w, visible));
                    RenderShell();
                }, FinishClose, e => e * e);
            }
            else
            {
                double op = Opacity;
                Anim.Run(this, 100, e => { if (!IsDisposed) Opacity = Math.Max(0.01, op * (1 - e)); }, FinishClose);
            }
        }

        void FinishClose()
        {
            Anim.Stop(this);
            if (!IsDisposed) { Hide(); BeginInvoke((Action)Dispose); }
            if (shell != null && !shell.IsDisposed) { shell.Hide(); var sh = shell; BeginInvoke((Action)sh.Dispose); }
        }

        int HitTest(Point p)
        {
            int y = S(6);
            for (int i = 0; i < entries.Count; i++)
            {
                int h = entries[i].Separator ? SepH : ItemH;
                if (p.Y >= y && p.Y < y + h) return i;
                y += h;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i >= 0 && !entries[i].Enabled) i = -1;
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i < 0 || !entries[i].Enabled || entries[i].Click == null) return;
            var action = entries[i].Click;
            CloseMenu();
            try { action(); }
            catch (Exception ex)
            {
                if (ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223) return;
                MessageBox.Show("Couldn't complete \"" + entries[i].Text + "\":\n" + ex.Message, "WispR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = S(6);
            for (int i = 0; i < entries.Count; i++)
            {
                var en = entries[i];
                if (en.Separator)
                {
                    using var pen = new Pen(t.Border);
                    g.DrawLine(pen, S(12), y + SepH / 2, Width - S(12), y + SepH / 2);
                    y += SepH;
                    continue;
                }
                var r = new Rectangle(S(4), y, Width - S(8), ItemH);
                if (i == hover)
                    using (var p = BarForm.Rounded(r, S(5)))
                    using (var b = new SolidBrush(t.Selection))
                        g.FillPath(b, p);
                var color = en.Enabled ? t.Text : en.Bold ? t.Text : t.SubText;
                TextRenderer.DrawText(g, en.Text, en.Bold ? boldFont : font, new Rectangle(r.X + S(12), r.Y, r.Width - S(16), r.Height), color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                y += ItemH;
            }
            if (!SystemStatus.IsWindows11 && !attached)
                using (var pen = new Pen(t.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = attached ? 1 /* square: the outline window draws the corners */ : 2, dark = t.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            if (attached) { int none = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(Handle, 34 /* border colour */, ref none, 4); }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // TOOLWINDOW | NOACTIVATE | TOPMOST
                if (!attached) cp.ClassStyle |= 0x20000; // drop shadow (not when it grows out of the bar)
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
            if (disposing) { watch.Dispose(); font.Dispose(); boldFont.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    }
}
