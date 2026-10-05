using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The volume popup opened from the system box: output device, mute button, a slider you can
    /// drag or scroll, and a shortcut to Windows' sound settings. Never takes focus; closes when you
    /// click elsewhere, press Esc or switch windows.
    /// </summary>
    sealed class VolumePopup : Form
    {
        readonly Settings settings;
        readonly float s;
        readonly Timer watch = new Timer { Interval = 40 };
        readonly Timer refresh = new Timer { Interval = 500 };
        readonly Font nameFont = new Font("Segoe UI", 8.5f);
        readonly Font percentFont = new Font("Segoe UI Semibold", 11f);
        readonly WheelHook wheel;
        Font glyphFont, smallGlyphFont;

        float volume;
        bool muted, hasDevice, dragging, armed;
        string device;
        Rectangle muteRect, sliderRect, gearRect, valueRect, headRect, anchor;
        int hoverZone; // 0 none, 1 mute, 2 gear, 3 slider
        IntPtr foregroundAtOpen;

        public event Action VolumeChanged;

        Theme T => settings.Theme;

        public VolumePopup(Settings settings)
        {
            this.settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            watch.Tick += (o, e) => Watch();
            refresh.Tick += (o, e) => { if (!dragging) { Read(); Invalidate(); } };
            wheel = new WheelHook(this, (delta, pt) => SetVolume(volume + Math.Sign(delta) * 0.02f));
        }

        int S(float px) => (int)Math.Round(px * s);

        public void ToggleAt(Rectangle item, bool above, int alignRight = -1)
        {
            if (Visible && !GrowOut.IsClosing(this)) { CloseMenu(); return; }
            anchor = item;
            glyphFont ??= new Font(BarForm.GlyphFamily, 14f);
            smallGlyphFont ??= new Font(BarForm.GlyphFamily, 10.5f);
            BackColor = above ? T.Background : T.Surface; // growing out of the bar: same colour as the bar
            Read();
            device = SystemStatus.GetOutputDeviceName() ?? "Speakers";

            // One margin (m) for every edge; two rows: device + settings, then mute + slider + value.
            int m = S(16), w = S(320), headH = S(28), rowH = S(36), gap = S(10);
            int h = m + headH + gap + rowH + m;
            var scr = Screen.FromRectangle(item).Bounds;
            int x = alignRight >= 0 ? alignRight - w : item.X + item.Width / 2 - w / 2; // line up with the system box
            x = Math.Max(scr.Left + S(22), Math.Min(scr.Right - w - S(22), x)); // room for the curves into the bar
            var (edge, curves) = GrowOut.EdgeFor(item, item.Top - S(4));
            int y = above ? edge - h : item.Bottom + S(12);
            Bounds = new Rectangle(x, y, w, h);

            int rowY = m + headH + gap, btn = S(32);
            // buttons extend a little past the margin so their *glyphs* sit exactly on it
            gearRect = new Rectangle(w - m - btn + S(7), m + (headH - btn) / 2, btn, btn);
            muteRect = new Rectangle(m - S(7), rowY + (rowH - btn) / 2, btn, btn);
            valueRect = new Rectangle(w - m - S(36), rowY, S(36), rowH);
            sliderRect = new Rectangle(muteRect.Right + S(8), rowY + (rowH - S(20)) / 2, valueRect.X - S(14) - (muteRect.Right + S(8)), S(20));
            headRect = new Rectangle(m, m, gearRect.X - m - S(8), headH);

            foregroundAtOpen = Native.GetForegroundWindow();
            armed = false;
            int round = 2, dark = T.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            if (above) { GrowOut.PrepareWindow(this); GrowOut.Show(this, edge, curves, T, s); }
            else
            {
                Anim.PopIn(this, -S(10));
                Show();
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            watch.Start();
            refresh.Start();
            wheel.Install();
            Invalidate();
        }

        public void CloseMenu()
        {
            watch.Stop();
            refresh.Stop();
            wheel.Uninstall();
            dragging = false;
            if (Visible) GrowOut.Close(this, Hide); // sinks back into the bar
        }

        void Read()
        {
            hasDevice = SystemStatus.TryGetVolume(out volume, out muted);
        }

        void SetVolume(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            SystemStatus.SetVolume(v);
            Read();
            Invalidate();
            VolumeChanged?.Invoke();
        }

        void Watch()
        {
            bool anyButton = Down(0x01) || Down(0x02) || Down(0x04);
            if (!armed) { if (!anyButton) armed = true; return; }
            var cur = Cursor.Position;
            if (anyButton && !dragging && !Bounds.Contains(cur) && !anchor.Contains(cur)) { CloseMenu(); return; }
            if (Down(0x1B)) { CloseMenu(); return; }
            if (Native.GetForegroundWindow() != foregroundAtOpen) CloseMenu();
        }

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // device name + settings button
            TextRenderer.DrawText(g, device, nameFont, headRect, T.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (hoverZone == 2) using (var p = BarForm.Rounded(gearRect, S(5))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            TextRenderer.DrawText(g, "", smallGlyphFont, gearRect, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // mute button
            if (hoverZone == 1) using (var p = BarForm.Rounded(muteRect, S(6))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            string glyph = !hasDevice || muted || volume <= 0.001f ? "" : volume < 0.34f ? "" : volume < 0.67f ? "" : "";
            TextRenderer.DrawText(g, glyph, glyphFont, muteRect, muted ? T.SubText : T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // slider
            int cy = sliderRect.Y + sliderRect.Height / 2, th = S(4);
            var track = new Rectangle(sliderRect.X, cy - th / 2, sliderRect.Width, th);
            using (var p = BarForm.Rounded(track, th / 2f)) using (var b = new SolidBrush(T.Border)) g.FillPath(b, p);
            float v = hasDevice ? volume : 0;
            int fx = sliderRect.X + (int)(sliderRect.Width * v);
            var fill = new Rectangle(track.X, track.Y, Math.Max(th, fx - track.X), th);
            using (var p = BarForm.Rounded(fill, th / 2f)) using (var b = new SolidBrush(muted ? T.SubText : T.Accent)) g.FillPath(b, p);
            int kr = hoverZone == 3 || dragging ? S(9) : S(8);
            using (var b = new SolidBrush(muted ? T.SubText : T.Accent)) g.FillEllipse(b, fx - kr, cy - kr, kr * 2, kr * 2);
            using (var b = new SolidBrush(T.Surface)) g.FillEllipse(b, fx - kr / 2.4f, cy - kr / 2.4f, kr / 1.2f, kr / 1.2f);

            // percentage
            TextRenderer.DrawText(g, hasDevice ? ((int)Math.Round(v * 100)).ToString() : "\u2013", percentFont, valueRect, T.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (!SystemStatus.IsWindows11)
                using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        // ---------- mouse ----------

        Rectangle SliderHit => Rectangle.Inflate(sliderRect, S(6), S(8));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (SliderHit.Contains(e.Location)) { dragging = true; SetFromX(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { SetFromX(e.X); return; }
            int z = muteRect.Contains(e.Location) ? 1 : gearRect.Contains(e.Location) ? 2 : SliderHit.Contains(e.Location) ? 3 : 0;
            if (z != hoverZone) { hoverZone = z; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hoverZone = 0; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) { dragging = false; Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (muteRect.Contains(e.Location)) { SystemStatus.ToggleMute(); Read(); Invalidate(); VolumeChanged?.Invoke(); }
            else if (gearRect.Contains(e.Location)) { CloseMenu(); try { Launcher.Start("ms-settings:sound", null, null); } catch { } }
        }

        void SetFromX(int x) => SetVolume((x - sliderRect.X) / (float)sliderRect.Width);

        // ---------- window plumbing ----------

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
            if (disposing) { watch.Dispose(); refresh.Dispose(); wheel.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    }
}
