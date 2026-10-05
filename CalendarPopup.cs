using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The clock popup: the time with seconds, the full date, and a month calendar with week numbers.
    /// Arrows or the mouse wheel change the month; clicking the month name returns to today.
    /// Never takes focus; closes when you click elsewhere, press Esc or switch windows.
    /// </summary>
    sealed class CalendarPopup : Form
    {
        readonly Settings settings;
        readonly float s;
        readonly Timer watch = new Timer { Interval = 40 };
        readonly Timer clock = new Timer { Interval = 250 };
        readonly WheelHook wheel;
        readonly Font timeFont = new Font("Segoe UI Light", 26f);
        readonly Font dateFont = new Font("Segoe UI", 9.5f);
        readonly Font monthFont = new Font("Segoe UI Semibold", 10f);
        readonly Font dayFont = new Font("Segoe UI", 9f);
        readonly Font smallFont = new Font("Segoe UI", 7.5f);
        Font glyphFont;

        DateTime shownMonth; // first day of the month on screen
        Rectangle prevRect, nextRect, monthRect, gearRect, anchor;
        Rectangle[] dayRects = new Rectangle[42];
        DateTime[] dayDates = new DateTime[42];
        int hover = -1; // 0..41 days, 100 prev, 101 next, 102 month, 103 gear
        IntPtr foregroundAtOpen;
        bool armed;

        Theme T => settings.Theme;
        CultureInfo C => CultureInfo.CurrentCulture;

        public CalendarPopup(Settings settings)
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
            clock.Tick += (o, e) => Invalidate(new Rectangle(0, 0, Width, S(96))); // just the time area
            wheel = new WheelHook(this, (delta, pt) => ChangeMonth(delta > 0 ? -1 : 1));
        }

        int S(float px) => (int)Math.Round(px * s);

        public void ToggleAt(Rectangle item, bool above, int alignRight)
        {
            if (Visible && !GrowOut.IsClosing(this)) { CloseMenu(); return; }
            LayoutFor(item, above, alignRight);

            foregroundAtOpen = Native.GetForegroundWindow();
            armed = false;
            hover = -1;
            int round = 2, dark = T.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            if (above) { GrowOut.PrepareWindow(this); GrowOut.Show(this, growEdge, growCurves, T, s); }
            else
            {
                Anim.PopIn(this, -S(10));
                Show();
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            watch.Start();
            clock.Start();
            wheel.Install();
            Invalidate();
        }

        int growEdge; bool growCurves;

        void LayoutFor(Rectangle item, bool above, int alignRight)
        {
            anchor = item;
            glyphFont ??= new Font(BarForm.GlyphFamily, 9.5f);
            BackColor = above ? T.Background : T.Surface; // growing out of the bar: same colour as the bar
            shownMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            int m = S(18), cell = S(38), weekCol = S(30);
            int w = m * 2 + weekCol + cell * 7;
            int h = m + S(78) + S(14) + S(32) + S(24) + cell * 6 + m;
            var scr = Screen.FromRectangle(item).Bounds;
            int x = Math.Max(scr.Left + S(22), Math.Min(scr.Right - w - S(22), alignRight - w)); // room for the curves into the bar
            (growEdge, growCurves) = GrowOut.EdgeFor(item, item.Top - S(4));
            int y = above ? growEdge - h : item.Bottom + S(12);
            Bounds = new Rectangle(x, y, w, h);

            // fixed geometry
            gearRect = new Rectangle(w - m - S(32) + S(7), m, S(32), S(32));
            int navY = m + S(78) + S(14);
            nextRect = new Rectangle(w - m - S(32) + S(7), navY, S(32), S(30));
            prevRect = new Rectangle(nextRect.X - S(34), navY, S(32), S(30));
            monthRect = new Rectangle(m, navY, prevRect.X - m - S(8), S(30));
            int gridTop = navY + S(32) + S(24);
            for (int i = 0; i < 42; i++)
                dayRects[i] = new Rectangle(m + weekCol + (i % 7) * cell, gridTop + (i / 7) * cell, cell, cell);
        }

        public void CloseMenu()
        {
            watch.Stop();
            clock.Stop();
            wheel.Uninstall();
            if (Visible) GrowOut.Close(this, Hide); // sinks back into the bar
        }

        void ChangeMonth(int delta)
        {
            shownMonth = shownMonth.AddMonths(delta);
            Invalidate();
        }

        void Watch()
        {
            bool anyButton = Down(0x01) || Down(0x02) || Down(0x04);
            if (!armed) { if (!anyButton) armed = true; return; }
            var cur = Cursor.Position;
            if (anyButton && !Bounds.Contains(cur) && !anchor.Contains(cur)) { CloseMenu(); return; }
            if (Down(0x1B)) { CloseMenu(); return; }
            if (Native.GetForegroundWindow() != foregroundAtOpen) CloseMenu();
        }

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int m = S(18);
            var now = DateTime.Now;
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            // time + date
            string timePattern = C.DateTimeFormat.LongTimePattern;
            TextRenderer.DrawText(g, now.ToString(timePattern, C), timeFont, new Rectangle(m - S(2), m - S(4), Width - m * 2 - S(40), S(48)), T.Text, flags);
            TextRenderer.DrawText(g, now.ToString("D", C), dateFont, new Rectangle(m, m + S(46), Width - m * 2, S(24)), T.SubText, flags);
            Button(g, gearRect, "", 103);

            using (var pen = new Pen(T.Border)) g.DrawLine(pen, m, m + S(84), Width - m, m + S(84));

            // month header
            string month = C.TextInfo.ToTitleCase(shownMonth.ToString("MMMM yyyy", C));
            if (hover == 102) using (var p = BarForm.Rounded(Rectangle.Inflate(monthRect, S(6), 0), S(5))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            TextRenderer.DrawText(g, month, monthFont, monthRect, T.Text, flags);
            Button(g, prevRect, "", 100); // up = earlier month
            Button(g, nextRect, "", 101); // down = later month

            // weekday names (starting on the culture's first day of the week) + week column title
            var dtf = C.DateTimeFormat;
            int first = (int)dtf.FirstDayOfWeek;
            int headY = dayRects[0].Y - S(24);
            TextRenderer.DrawText(g, KwLabel(), smallFont, new Rectangle(m, headY, S(30), S(20)), T.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            for (int i = 0; i < 7; i++)
            {
                string name = dtf.GetAbbreviatedDayName((DayOfWeek)((first + i) % 7)).TrimEnd('.');
                if (name.Length > 2) name = name.Substring(0, 2); // "Mo", "Di" / "Mo", "Tu"
                TextRenderer.DrawText(g, name, smallFont, new Rectangle(dayRects[i].X, headY, dayRects[i].Width, S(20)), T.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // day grid
            int offset = ((int)shownMonth.DayOfWeek - first + 7) % 7;
            var start = shownMonth.AddDays(-offset);
            var onAccent = T.Accent.GetBrightness() > 0.6f ? Color.FromArgb(20, 20, 20) : Color.White;
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                dayDates[i] = d;
                var r = dayRects[i];
                if (i % 7 == 0) // ISO week number at the start of each row
                    TextRenderer.DrawText(g, WeekOfYear(d).ToString(), smallFont, new Rectangle(m, r.Y, S(30), r.Height), T.SubText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                bool today = d == DateTime.Today, inMonth = d.Month == shownMonth.Month;
                var circle = Rectangle.Inflate(r, -S(3), -S(3));
                if (today) using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, circle);
                else if (i == hover) using (var b = new SolidBrush(T.Selection)) g.FillEllipse(b, circle);
                var color = today ? onAccent : inMonth ? T.Text : T.SubText;
                if (!today && !inMonth) color = Color.FromArgb(160, color);
                TextRenderer.DrawText(g, d.Day.ToString(), dayFont, r, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (!SystemStatus.IsWindows11)
                using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        void Button(Graphics g, Rectangle r, string glyph, int id)
        {
            if (hover == id) using (var p = BarForm.Rounded(r, S(5))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            TextRenderer.DrawText(g, glyph, glyphFont, r, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        string KwLabel() => C.TwoLetterISOLanguageName == "de" ? "KW" : "Wk";

        /// <summary>ISO 8601 week number (the one used in Germany and most of Europe).</summary>
        static int WeekOfYear(DateTime d)
        {
            var day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(d);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday) d = d.AddDays(3);
            return CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(d, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        }

        // ---------- mouse ----------

        int HitTest(Point p)
        {
            if (prevRect.Contains(p)) return 100;
            if (nextRect.Contains(p)) return 101;
            if (monthRect.Contains(p)) return 102;
            if (gearRect.Contains(p)) return 103;
            for (int i = 0; i < 42; i++) if (dayRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h == 100) ChangeMonth(-1);
            else if (h == 101) ChangeMonth(1);
            else if (h == 102) { shownMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Invalidate(); }
            else if (h == 103) { CloseMenu(); try { Launcher.Start("ms-settings:dateandtime", null, null); } catch { } }
            else if (h >= 0 && h < 42 && dayDates[h].Month != shownMonth.Month) ChangeMonth(dayDates[h] < shownMonth ? -1 : 1);
        }

        // ---------- window plumbing ----------

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8;
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
            if (disposing) { watch.Dispose(); clock.Dispose(); wheel.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    }
}
