using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The timers' little widget on the right edge of the screen. Collapsed it's a small tab growing out of
    /// the edge (the same flared shape as the drop-down) with a progress ring and the time left of the next
    /// timer; touch it with the mouse and it slides out into the full list (pause, +1 min, cancel). When a
    /// timer ends it opens by itself, glows and chimes until it's ticked off.
    /// </summary>
    sealed class TimerWidget : Form
    {
        readonly Settings settings;
        readonly float s;
        Theme T => settings.Theme;

        /// <summary>The x of the screen's right edge to hang from (inside the frame), or null when it shouldn't show (fullscreen app).</summary>
        public Func<Screen, int?> RightEdgeFor;

        float open;            // 0 = collapsed tab, 1 = full list
        bool wantOpen;
        DateTime outsideSince = DateTime.MinValue;
        Point mouse = new Point(-1, -1);
        readonly List<(Rectangle r, Action click)> buttons = new List<(Rectangle, Action)>();
        readonly Timer watch = new Timer { Interval = 40 };   // mouse near the edge?
        readonly Timer tick = new Timer { Interval = 250 };   // time left, done?
        System.Media.SoundPlayer chime;
        DateTime ringingSince = DateTime.MinValue;

        static readonly Font titleFont = new Font("Segoe UI Semibold", 10f), timeFont = new Font("Segoe UI Semibold", 15f),
            smallFont = new Font("Segoe UI", 8.5f), tinyTimeFont = new Font("Segoe UI Semibold", 8.5f), headFont = new Font("Segoe UI Semibold", 9f);
        Font glyphFont;

        int Flare => (int)(14 * s);
        int Radius => (int)(14 * s);
        const int MaxRows = 5;

        public TimerWidget(Settings settings)
        {
            this.settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "WispR Timers";
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            _ = Handle;
            Timers.Load();
            Timers.Changed += () => { if (!IsDisposed) BeginInvoke((Action)Refresh); };
            Timers.Rang += t => { if (!IsDisposed) BeginInvoke((Action)Ring); };
            watch.Tick += (o, e) => Watch();
            tick.Tick += (o, e) => { Timers.Check(); Redraw(); StopChimeIfQuiet(); };
            Refresh();
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

        /// <summary>Shows or hides the widget depending on whether there are timers.</summary>
        new void Refresh()
        {
            bool any = Timers.Items.Count > 0;
            if (any) { if (!watch.Enabled) watch.Start(); if (!tick.Enabled) tick.Start(); Redraw(); }
            else
            {
                watch.Stop(); tick.Stop(); StopChime();
                Anim.Stop(this);
                open = 0; wantOpen = false;
                if (Visible) Hide();
            }
        }

        // ---------- opening / closing ----------

        void Watch()
        {
            if (Timers.Items.Count == 0) return;
            var place = Placement(out _);
            if (place == null) { if (Visible && !Timers.AnyRinging) Hide(); return; }
            var c = Cursor.Position;
            var b = Bounds;
            bool near = Visible && Rectangle.Inflate(b, (int)(10 * s), (int)(10 * s)).Contains(c);
            // or the mouse pushed against the right edge near it
            if (!near && Visible && c.X >= place.Value.edge - 2 && Math.Abs(c.Y - place.Value.centerY) <= b.Height / 2 + (int)(40 * s)) near = true;
            bool buttonsDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (near) { outsideSince = DateTime.MinValue; if (!wantOpen && !buttonsDown) SetOpen(true); return; }
            if (Timers.AnyRinging) return; // stays open until it's ticked off
            if (outsideSince == DateTime.MinValue) outsideSince = DateTime.Now;
            if (wantOpen && (DateTime.Now - outsideSince).TotalMilliseconds >= 300) SetOpen(false);
        }

        void SetOpen(bool on)
        {
            if (wantOpen == on) return;
            wantOpen = on;
            float from = open, to = on ? 1 : 0;
            Anim.Run(this, on ? 240 : 190, e => { open = (float)Anim.Lerp(from, to, e); Redraw(); }, null, on ? (Func<double, double>)Anim.OutCubic : Anim.InCubic);
        }

        // ---------- ringing ----------

        void Ring()
        {
            ringingSince = DateTime.Now;
            SetOpen(true);
            chimesLeft = 5;
            PlayChime();
            chimeTimer ??= new Timer { Interval = 8000 };
            chimeTimer.Tick -= ChimeTick; chimeTimer.Tick += ChimeTick;
            chimeTimer.Start();
            // the glow pulses at ~30 frames a second (plenty for a slow pulse)
            if (!Anim.FramesRunning(PulseKey)) Anim.Frames(PulseKey, () =>
            {
                if (IsDisposed || !Timers.AnyRinging) { Redraw(); return false; }
                if ((DateTime.Now - lastPulse).TotalMilliseconds < 33) return true;
                lastPulse = DateTime.Now;
                Redraw();
                return true;
            });
        }

        const string PulseKey = "timer-pulse";
        DateTime lastPulse;

        // A soft three-note bell, made here (no file needed): quiet, short, repeated every 8 s, five times at most.
        int chimesLeft;
        Timer chimeTimer;
        void ChimeTick(object o, EventArgs e) { if (--chimesLeft <= 0 || !Timers.AnyRinging) { chimeTimer.Stop(); return; } PlayChime(); }

        void PlayChime()
        {
            try
            {
                chime ??= new System.Media.SoundPlayer(new System.IO.MemoryStream(Bell()));
                chime.Play();
            }
            catch { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } }
        }

        static byte[] Bell()
        {
            const int rate = 44100;
            // three gentle notes (E5, G5, C6), each a soft sine with a little shimmer, fading out
            var notes = new[] { (f: 659.25, at: 0.00), (f: 783.99, at: 0.16), (f: 1046.5, at: 0.32) };
            int n = (int)(rate * 1.6);
            var pcm = new short[n];
            foreach (var note in notes)
            {
                int start = (int)(note.at * rate);
                for (int i = start; i < n; i++)
                {
                    double t = (i - start) / (double)rate;
                    double attack = Math.Min(1, t / 0.012);                 // no click at the start
                    double env = attack * Math.Exp(-t * 3.2);               // bell-like fade
                    double v = Math.Sin(2 * Math.PI * note.f * t) * 0.8 + Math.Sin(2 * Math.PI * note.f * 2.01 * t) * 0.12;
                    int s16 = pcm[i] + (int)(v * env * 0.16 * short.MaxValue); // quiet: ~16 % of full scale per note
                    pcm[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s16));
                }
            }
            using var ms = new System.IO.MemoryStream();
            using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.ASCII, true))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
                foreach (var v in pcm) w.Write(v);
            }
            return ms.ToArray();
        }

        void StopChimeIfQuiet()
        {
            // nothing left ringing, or it has been ringing for a minute: stop the sound (the widget keeps glowing)
            if (!Timers.AnyRinging || (ringingSince != DateTime.MinValue && (DateTime.Now - ringingSince).TotalSeconds > 60)) StopChime();
        }

        void StopChime()
        {
            try { chime?.Stop(); } catch { }
            chimeTimer?.Stop();
            ringingSince = DateTime.MinValue;
        }

        // ---------- geometry ----------

        (int edge, int centerY)? Placement(out Screen scr)
        {
            scr = Screen.PrimaryScreen;
            int? edge = RightEdgeFor?.Invoke(scr);
            if (edge == null && Timers.AnyRinging) edge = scr.Bounds.Right; // a timer that ends shows even over a game
            if (edge == null) return null;
            var wa = scr.WorkingArea;
            return (edge.Value, wa.Y + wa.Height / 2);
        }

        int Rows => Math.Min(MaxRows, Timers.Items.Count);
        float Depth => Lerp(58 * s, 300 * s + edgeInset, Ease(open));                                       // how far it reaches into the screen
        float Length => Lerp(76 * s, (40 + Rows * 66 + 10) * s, Ease(open));                     // along the edge
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Ease(float t) => t;

        // ---------- drawing ----------

        void Redraw()
        {
            if (Timers.Items.Count == 0) return;
            var place = Placement(out _);
            if (place == null) { if (Visible) Hide(); return; }
            edgeInset = Math.Max(0, Screen.PrimaryScreen.Bounds.Right - place.Value.edge);
            using var bmp = Paint();
            int w = bmp.Width, h = bmp.Height;
            var pos = new Rectangle(place.Value.edge - w, place.Value.centerY - h / 2, w, h);
            if (Bounds != pos) Bounds = pos;
            Push(bmp, pos.Location);
            if (!Visible) { Show(); KeepOnTop(); }
        }

        int edgeInset; // the frame strip to the right of the widget: same colour, so it looks like part of the tab

        /// <summary>The widget as it looks right now.</summary>
        Bitmap Paint()
        {
            glyphFont ??= new Font(BarForm.GlyphFamily, 11f);
            int d = (int)Math.Round(Depth), len = (int)Math.Round(Length), fl = Flare;
            int w = d, h = len + fl * 2;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            buttons.Clear();
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                // the drop-down's shape, turned to grow out of the right edge
                using var shape = LauncherShell.Shape(h, w, fl, Radius);
                using (var turn = new Matrix(0, 1, 1, 0, 0, 0)) shape.Transform(turn);
                bool ringing = Timers.AnyRinging;
                float pulse = ringing ? (float)(0.5 + 0.5 * Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 5)) : 0;
                using (var b = new SolidBrush(ringing ? Ui.Mix(T.Background, T.Accent, 0.10f + 0.12f * pulse) : T.Background)) g.FillPath(b, shape);
                using (var pen = new Pen(ringing ? Color.FromArgb((int)(120 + 120 * pulse), T.Accent) : Color.FromArgb(90, T.Border), ringing ? 1.6f * s : 1f)) g.DrawPath(pen, shape);
                g.SetClip(shape);

                var body = new Rectangle(0, fl, w, len);
                // centred on what you see: the tab plus the frame strip it merges into
                var seen = new Rectangle(0, fl, w + edgeInset, len);
                float collapsedAlpha = Math.Max(0, 1 - open * 2.2f), listAlpha = Math.Max(0, (open - 0.45f) / 0.55f);
                if (collapsedAlpha > 0.01f) DrawCollapsed(g, seen, collapsedAlpha);
                // the list: as far from the left edge as the frame strip makes the right side look, so both margins match
                if (listAlpha > 0.01f) DrawList(g, new Rectangle(body.X + edgeInset, body.Y, body.Width - edgeInset, body.Height), listAlpha);
            }
            return bmp;
        }

        Color A(Color c, float alpha) => Color.FromArgb((int)(c.A * Math.Max(0, Math.Min(1, alpha))), c);

        void DrawCollapsed(Graphics g, Rectangle body, float alpha)
        {
            var t = Timers.Primary;
            if (t == null) return;
            Color col = t.Done ? T.Accent : t.Paused ? T.SubText : T.Accent;
            float ring = 30 * s, th = 3.2f * s, gap = 3 * s, textH = 16 * s;
            float top = body.Y + (body.Height - (ring + gap + textH)) / 2f; // ring and time together, centred
            var rr = new RectangleF(body.X + (body.Width - ring) / 2f, top, ring, ring);
            using (var pen = new Pen(A(Color.FromArgb(55, col), alpha), th)) g.DrawEllipse(pen, rr);
            float p = t.Done ? 1 : 1 - t.Progress; // the ring empties as time runs out
            if (p > 0.001f)
                using (var pen = new Pen(A(col, alpha), th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rr, -90, 360 * p);
            string mid = t.Done ? "" : t.Paused ? "" : ""; // check / pause / stopwatch
            DrawText(g, mid, glyphFont, Rectangle.Round(rr), A(t.Done ? T.Accent : T.SubText, alpha));
            string time = t.Done ? "Done" : Timers.Clock2(t.Left);
            DrawText(g, time, tinyTimeFont, new Rectangle(body.X, (int)(rr.Bottom + gap), body.Width, (int)textH), A(T.Text, alpha));
            if (Timers.Items.Count > 1) // how many are running
            {
                var badge = new RectangleF(body.X + 6 * s, body.Y + 5 * s, 15 * s, 15 * s);
                using (var b = new SolidBrush(A(T.Accent, alpha))) g.FillEllipse(b, badge);
                DrawText(g, Timers.Items.Count.ToString(), smallFont, Rectangle.Round(badge), A(Ui.OnAccent(T.Accent), alpha));
            }
        }

        void DrawList(Graphics g, Rectangle body, float alpha)
        {
            int pad = (int)(16 * s);
            DrawText(g, "TIMERS", headFont, new Rectangle(body.X + pad, body.Y + (int)(10 * s), body.Width - pad * 2, (int)(20 * s)), A(T.SubText, alpha), left: true);
            DrawText(g, "set timer 10m", smallFont, new Rectangle(body.X + pad, body.Y + (int)(10 * s), body.Width - pad * 2, (int)(20 * s)), A(Color.FromArgb(150, T.SubText), alpha), right: true);
            int y = body.Y + (int)(40 * s);
            foreach (var t in Timers.Items.Take(MaxRows).ToList())
            {
                var row = new Rectangle(body.X + (int)(8 * s), y, body.Width - (int)(16 * s), (int)(60 * s));
                DrawRow(g, row, t, alpha);
                y += (int)(66 * s);
            }
        }

        void DrawRow(Graphics g, Rectangle r, TimerItem t, float alpha)
        {
            bool hot = r.Contains(mouse);
            Color col = t.Paused ? T.SubText : T.Accent;
            using (var p = Ui.Round(r, 10 * s))
            using (var b = new SolidBrush(A(t.Done ? Color.FromArgb(55, T.Accent) : hot ? Color.FromArgb(T.IsLight ? 140 : 90, T.Surface) : Color.FromArgb(T.IsLight ? 100 : 60, T.Surface), alpha)))
                g.FillPath(b, p);

            int pad = (int)(10 * s);
            // title and time left
            int textW = r.Width - pad * 2 - (int)(96 * s);
            DrawText(g, t.DisplayTitle + (t.Paused ? "  \u00B7  paused" : ""), titleFont, new Rectangle(r.X + pad, r.Y + (int)(6 * s), textW, (int)(18 * s)), A(T.SubText, alpha), left: true);
            DrawText(g, t.Done ? "Done!" : Timers.Clock2(t.Left), timeFont, new Rectangle(r.X + pad, r.Y + (int)(22 * s), textW, (int)(26 * s)), A(t.Done ? T.Accent : T.Text, alpha), left: true);

            // progress along the bottom
            float bw = r.Width - pad * 2, by = r.Bottom - 7 * s, bh = 3 * s;
            using (var p = Ui.Round(new RectangleF(r.X + pad, by, bw, bh), bh / 2)) using (var b = new SolidBrush(A(Color.FromArgb(50, col), alpha))) g.FillPath(b, p);
            float f = t.Done ? 1 : t.Progress;
            if (f > 0.001f) using (var p = Ui.Round(new RectangleF(r.X + pad, by, Math.Max(bh, bw * f), bh), bh / 2)) using (var b = new SolidBrush(A(col, alpha))) g.FillPath(b, p);

            // buttons on the right
            int bs = (int)(28 * s), bx = r.Right - pad - bs;
            int byy = r.Y + (int)(27 * s) - bs / 2; // centred on the title + time, above the progress line
            if (t.Done)
            {
                Button(g, new Rectangle(bx, byy, bs, bs), "", "Dismiss", () => Timers.Dismiss(t), alpha, primary: true);
                Button(g, new Rectangle(bx - bs - (int)(4 * s), byy, bs, bs), "+1", "One more minute", () => Timers.AddTime(t, TimeSpan.FromMinutes(1)), alpha, text: true);
            }
            else
            {
                Button(g, new Rectangle(bx, byy, bs, bs), "", "Cancel", () => Timers.Cancel(t), alpha);
                Button(g, new Rectangle(bx - bs - (int)(4 * s), byy, bs, bs), "+1", "One more minute", () => Timers.AddTime(t, TimeSpan.FromMinutes(1)), alpha, text: true);
                Button(g, new Rectangle(bx - (bs + (int)(4 * s)) * 2, byy, bs, bs), t.Paused ? "" : "", t.Paused ? "Resume" : "Pause", () => Timers.TogglePause(t), alpha);
            }
            if (t.Done) buttons.Add((r, () => Timers.Dismiss(t))); // clicking a finished timer anywhere ticks it off
        }

        void Button(Graphics g, Rectangle r, string glyph, string tip, Action click, float alpha, bool primary = false, bool text = false)
        {
            bool hot = r.Contains(mouse);
            if (primary || hot)
                using (var b = new SolidBrush(A(primary ? T.Accent : Color.FromArgb(60, T.Accent), alpha))) g.FillEllipse(b, r);
            Color fg = primary ? Ui.OnAccent(T.Accent) : hot ? Ui.Mix(T.Accent, T.Text, 0.3f) : T.Text;
            DrawText(g, glyph, text ? headFont : glyphFont, r, A(fg, alpha));
            buttons.Insert(0, (r, click)); // buttons win over the row behind them
        }

        static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color, bool left = false, bool right = false)
        {
            using var sf = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = left ? StringAlignment.Near : right ? StringAlignment.Far : StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
            };
            using var b = new SolidBrush(color);
            g.DrawString(text ?? "", font, b, r, sf);
        }

        // ---------- mouse ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            Cursor = open > 0.9f && buttons.Any(b => b.r.Contains(mouse)) ? Cursors.Hand : Cursors.Default;
            if (open > 0.9f) Redraw();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouse = new Point(-1, -1); Redraw(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (open < 0.9f) { SetOpen(true); return; }
            foreach (var (r, click) in buttons.ToArray())
                if (r.Contains(e.Location)) { click(); StopChimeIfQuiet(); Redraw(); return; }
        }

        void KeepOnTop() => Native.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);

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
                byte alpha = (byte)Math.Max(0, Math.Min(255, settings.Opacity * 255 / 100));
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
            if (disposing) { watch.Dispose(); tick.Dispose(); StopChime(); chime?.Dispose(); chimeTimer?.Dispose(); glyphFont?.Dispose(); Anim.StopFrames(PulseKey); }
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    }
}
