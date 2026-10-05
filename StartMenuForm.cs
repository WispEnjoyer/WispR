using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The Start menu opened by the taskbar's Start button: pinned apps, recommended apps,
    /// an A–Z list of all apps, and account / settings / power buttons. (Search lives in the
    /// launcher on the Windows key.) Like the bar menus it never takes focus and closes when
    /// you click elsewhere, press Esc or switch windows.
    /// </summary>
    sealed class StartMenuForm : Form
    {
        sealed class Hit
        {
            public Rectangle R;
            public AppEntry Entry;
            public Action Click;
            public Action<Point> RightClick;
            public string Tip;
        }

        readonly Settings settings;
        readonly Backdrop backdrop;
        readonly AppIndex index;
        readonly Usage usage;
        readonly float s;
        readonly Timer watch = new Timer { Interval = 40 };
        readonly Font titleFont = new Font("Segoe UI Semibold", 10.5f);
        readonly Font nameFont = new Font("Segoe UI", 8.75f);
        readonly Font rowFont = new Font("Segoe UI", 9.5f);
        readonly Font smallFont = new Font("Segoe UI", 8f);
        readonly Font letterFont = new Font("Segoe UI Semibold", 10f);
        readonly Font avatarFont = new Font("Segoe UI Semibold", 10f);
        Font glyphFont;

        readonly List<Hit> hits = new List<Hit>();
        int hover = -1;
        bool allApps;
        int scroll;          // All apps scroll offset (px)
        int scrollMax;
        Rectangle anchor;    // the Start button; clicks on it toggle instead of closing twice
        IntPtr foregroundAtOpen;
        bool armed;
        Bitmap bg;
        string bgKey;

        Theme T => settings.Theme;

        /// <summary>Pins to the taskbar (set by the taskbar).</summary>
        public Func<AppEntry, bool> IsOnTaskbar;
        public Action<AppEntry> ToggleTaskbarPin;
        /// <summary>Called before shutdown / restart / sign out so the Windows taskbar setting is restored.</summary>
        public Action BeforeLeaving;

        public StartMenuForm(Settings settings, Backdrop backdrop, AppIndex index, Usage usage)
        {
            this.settings = settings;
            this.backdrop = backdrop;
            this.index = index;
            this.usage = usage;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Text = "WispR Start";
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            glyphFont = new Font(BarForm.GlyphFamily, 11f);
            watch.Tick += (o, e) => Watch();
            wheel = new WheelHook(this, (delta, pt) => { StopAutoScroll(); ScrollBy(delta); });

            search = new TextBox { BorderStyle = BorderStyle.None, Visible = false };
            search.TextChanged += (o, e) => { scroll = 0; hover = -1; Invalidate(); };
            search.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    var first = SearchResults(search.Text).FirstOrDefault();
                    if (first != null && search.Text.Trim().Length > 0) Run(() => { Launcher.LaunchApp(first, false); usage.Record(first, Matcher.Normalize(search.Text)); });
                    e.SuppressKeyPress = true;
                }
            };
            search.GotFocus += (o, e) => Invalidate();
            search.LostFocus += (o, e) => Invalidate();
            Controls.Add(search);
            autoTimer.Tick += (o, e) => AutoScrollTick();
        }

        readonly WheelHook wheel;

        int S(float px) => (int)Math.Round(px * s);

        // ---------- open / close ----------

        public void Toggle(Rectangle startButton, bool above)
        {
            if (Visible && !GrowOut.IsClosing(this)) { CloseMenu(); return; }
            anchor = startButton;
            allApps = false;
            scroll = 0;
            hover = -1;

            int w = S(640), h = S(640);
            var scr = Screen.FromRectangle(startButton).Bounds;
            int x = Math.Max(scr.Left + S(22), Math.Min(scr.Right - w - S(22), startButton.X + startButton.Width / 2 - w / 2));
            var (edge, curves) = GrowOut.EdgeFor(startButton, startButton.Top - S(4));
            int y = above ? edge - h : startButton.Bottom + S(12);
            Bounds = new Rectangle(x, Math.Max(scr.Top + S(8), y), w, h);

            BackColor = T.Background;
            double op = settings.Opacity / 100.0;
            if (Math.Abs(Opacity - op) > 0.001) Opacity = op;
            UpdateBackdrop();
            foregroundAtOpen = Native.GetForegroundWindow();
            armed = false;
            if (above)
            {
                // grows out of the bar, like the launcher
                ApplyDwm(square: true);
                GrowOut.Show(this, edge, curves, T, s, r => backdrop.Render(r), ms: 200, alpha: (byte)(255 * settings.Opacity / 100));
            }
            else
            {
                Anim.PopIn(this, -S(16), 190);
                Show();
                ApplyDwm();
            }
            watch.Start();
            wheel.Install();
            atTopSince = DateTime.MinValue;
            search.Text = "";
            LayoutSearch();
            Invalidate();
        }

        public void CloseMenu()
        {
            watch.Stop();
            wheel.Uninstall();
            StopAutoScroll();
            dragging = false;
            if (Visible) GrowOut.Close(this, Hide); // sinks back into the bar
            DisableTyping();
            search.Text = "";
        }

        void Watch()
        {
            bool anyButton = Down(0x01) || Down(0x02) || Down(0x04);
            if (!armed) { if (!anyButton) armed = true; return; }
            if (BarMenu.IsOpen) return; // a right-click menu of ours is open on top
            var cur = Cursor.Position;
            if (anyButton && !Bounds.Contains(cur) && !anchor.Contains(cur)) { CloseMenu(); return; }
            bool esc = Down(0x1B);
            if (esc && !escWasDown)
            {
                if (autoScroll) StopAutoScroll();
                else if (search.Text.Length > 0) search.Text = "";
                else { CloseMenu(); return; }
            }
            escWasDown = esc;
            var fg = Native.GetForegroundWindow();
            if (fg != foregroundAtOpen && fg != Handle) CloseMenu();
        }

        bool escWasDown;

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        void UpdateBackdrop()
        {
            string key = backdrop.KeyFor(Bounds);
            if (key == bgKey) return;
            bgKey = key;
            bg?.Dispose();
            bg = backdrop.Render(Bounds);
        }

        void ApplyDwm(bool square = false)
        {
            int round = square ? 1 : 2, dark = T.IsLight ? 0 : 1;
            if (square) GrowOut.PrepareWindow(this);
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        }

        // ---------- data ----------

        List<AppEntry> AllEntries() =>
            index.Entries.Where(e => Matcher.Penalty(e) == 0).GroupBy(e => e.Key).Select(g => g.First())
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        List<AppEntry> Pinned(out bool suggested)
        {
            var byKey = index.Entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
            var list = settings.Pinned.Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();
            suggested = list.Count == 0;
            if (suggested) list = Frequent(12, new HashSet<string>());
            return list;
        }

        List<AppEntry> Frequent(int n, HashSet<string> exclude) =>
            index.Entries.Where(e => usage.Count(e) > 0 && !exclude.Contains(e.Key))
                .GroupBy(e => e.Key).Select(g => g.First())
                .OrderByDescending(e => usage.Boost(e, "")).ThenByDescending(e => usage.LastUsed(e))
                .Take(n).ToList();

        // ---------- painting (also builds the hit list) ----------

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
            hits.Clear();

            int pad = S(24), footerH = S(64);
            var content = new Rectangle(pad, pad, Width - pad * 2, Height - pad - footerH - S(8));
            if (allApps) PaintAllApps(g, content); else PaintHome(g, content);
            PaintFooter(g, new Rectangle(0, Height - footerH, Width, footerH));

            if (!SystemStatus.IsWindows11)
                using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        void SectionHeader(Graphics g, Rectangle area, int y, string title, string button, Action click)
        {
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(area.X + S(4), y, area.Width, S(30)), T.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (button == null) return;
            int bw = TextRenderer.MeasureText(button, smallFont).Width + S(22);
            var br = new Rectangle(area.Right - bw, y + S(3), bw, S(24));
            int i = AddHit(new Hit { R = br, Click = click });
            using (var p = BarForm.Rounded(br, S(5)))
            using (var b = new SolidBrush(Color.FromArgb(i == hover ? 255 : 170, T.Surface)))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, button, smallFont, br, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        int AddHit(Hit h) { hits.Add(h); return hits.Count - 1; }

        void PaintHome(Graphics g, Rectangle area)
        {
            int y = area.Y;
            var pinned = Pinned(out bool suggested);
            SectionHeader(g, area, y, suggested ? "Suggested" : "Pinned", "All apps  ›", () => OpenAllApps(focusSearch: true));
            y += S(40);

            int cols = 6, cellW = area.Width / cols, cellH = S(92);
            int rows = Math.Max(1, Math.Min(3, (pinned.Count + cols - 1) / cols)); // grid shrinks to what's pinned
            for (int i = 0; i < Math.Min(pinned.Count, cols * rows); i++)
            {
                var e = pinned[i];
                var r = new Rectangle(area.X + (i % cols) * cellW, y + (i / cols) * cellH, cellW, cellH - S(4));
                int hi = AddAppHit(r, e);
                if (hi == hover) using (var p = BarForm.Rounded(Rectangle.Inflate(r, -S(3), 0), S(6))) using (var b = new SolidBrush(Color.FromArgb(200, T.Selection))) g.FillPath(b, p);
                int ic = S(32);
                DrawIcon(g, e, new Rectangle(r.X + (r.Width - ic) / 2, r.Y + S(10), ic, ic));
                // up to two lines, wrapped at word boundaries like Windows' Start menu
                TextRenderer.DrawText(g, e.Name, nameFont, new Rectangle(r.X + S(4), r.Y + S(48), r.Width - S(8), nameFont.Height * 2), T.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (pinned.Count == 0)
                TextRenderer.DrawText(g, "Right-click any app (here or in the launcher) and choose “Pin to Start”.", rowFont,
                    new Rectangle(area.X, y, area.Width, cellH), T.SubText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            y += cellH * rows + S(8);

            // Recommended: most-used apps that aren't already in the grid above.
            var shown = new HashSet<string>(pinned.Take(cols * rows).Select(e => e.Key));
            int colW = area.Width / 2, rowH = S(50);
            int recRows = Math.Max(1, (area.Bottom - y - S(38)) / rowH); // fill whatever space is left
            var rec = Frequent(recRows * 2, shown);
            SectionHeader(g, area, y, "Recommended", null, null);
            y += S(38);
            for (int i = 0; i < rec.Count; i++)
            {
                var e = rec[i];
                var r = new Rectangle(area.X + (i % 2) * colW, y + (i / 2) * rowH, colW - S(6), rowH - S(4));
                int hi = AddAppHit(r, e);
                if (hi == hover) using (var p = BarForm.Rounded(r, S(6))) using (var b = new SolidBrush(Color.FromArgb(200, T.Selection))) g.FillPath(b, p);
                int ic = S(28);
                DrawIcon(g, e, new Rectangle(r.X + S(8), r.Y + (r.Height - ic) / 2, ic, ic));
                int tx = r.X + S(8) + ic + S(10);
                TextRenderer.DrawText(g, e.Name, rowFont, new Rectangle(tx, r.Y + S(5), r.Right - tx - S(4), S(20)), T.Text,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, UsedText(e), smallFont, new Rectangle(tx, r.Y + S(24), r.Right - tx - S(4), S(18)), T.SubText,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            if (rec.Count == 0)
                TextRenderer.DrawText(g, "Apps you use often will show up here.", rowFont, new Rectangle(area.X, y, area.Width, rowH), T.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        string UsedText(AppEntry e)
        {
            var last = usage.LastUsed(e);
            if (last <= 0) return "";
            var ago = DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);
            if (ago.TotalMinutes < 60) return "Used " + Math.Max(1, (int)ago.TotalMinutes) + " min ago";
            if (ago.TotalHours < 24) return "Used " + (int)ago.TotalHours + " h ago";
            if (ago.TotalDays < 2) return "Used yesterday";
            return "Used " + (int)ago.TotalDays + " days ago";
        }

        // All apps state
        TextBox search;
        Rectangle searchRect, listRect, trackRect, thumbRect;
        bool dragging; int dragOffset;
        bool autoScroll, autoMoved; Point autoAnchor; DateTime autoStart;
        readonly Timer autoTimer = new Timer { Interval = 16 };
        float autoCarry;

        void OpenAllApps(bool focusSearch)
        {
            allApps = true; scroll = 0; hover = -1;
            LayoutSearch();
            Invalidate();
            if (focusSearch) BeginInvoke((Action)EnableTyping);
        }

        void LayoutSearch()
        {
            int pad = S(24);
            int titleW = TextRenderer.MeasureText("All apps", titleFont).Width + S(20);
            int backW = TextRenderer.MeasureText("\u2039  Back", smallFont).Width + S(22);
            searchRect = new Rectangle(pad + titleW, pad + S(1), Width - pad * 2 - titleW - backW - S(14), S(30));
            int ip = S(30);
            search.Font = rowFont;
            search.Bounds = new Rectangle(searchRect.X + ip, searchRect.Y + (searchRect.Height - search.PreferredHeight) / 2 + 1,
                searchRect.Width - ip - S(10), search.PreferredHeight);
            search.BackColor = T.Surface;
            search.ForeColor = T.Text;
            search.Visible = allApps;
        }

        /// <summary>Lets the Start menu take keyboard focus (only when you want to type in the search box).</summary>
        void EnableTyping()
        {
            if (!Visible) return;
            long ex = Native.GetWindowLong(Handle, -20);
            SetWindowLong(Handle, -20, (int)(ex & ~0x08000000L)); // drop NOACTIVATE
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me) AttachThreadInput(me, fgThread, true);
            Native.SetForegroundWindow(Handle);
            if (fgThread != 0 && fgThread != me) AttachThreadInput(me, fgThread, false);
            Activate();
            search.Focus();
            foregroundAtOpen = Handle;
        }

        void DisableTyping()
        {
            if (!IsHandleCreated) return;
            long ex = Native.GetWindowLong(Handle, -20);
            SetWindowLong(Handle, -20, (int)(ex | 0x08000000L));
        }

        List<AppEntry> SearchResults(string q)
        {
            q = Matcher.Normalize(q);
            return AllEntries()
                .Select(e => (e, score: Matcher.Score(q, e)))
                .Where(x => x.score >= 0)
                .OrderByDescending(x => x.score + usage.Boost(x.e, q))
                .ThenBy(x => x.e.Name.Length)
                .Select(x => x.e).ToList();
        }

        void PaintAllApps(Graphics g, Rectangle area)
        {
            SectionHeader(g, area, area.Y, "All apps", "\u2039  Back", () => { allApps = false; search.Text = ""; search.Visible = false; hover = -1; Invalidate(); });

            // search field (the text box itself is a real control sitting inside this pill)
            using (var p = BarForm.Rounded(searchRect, S(6)))
            using (var b = new SolidBrush(T.Surface))
            using (var pen = new Pen(search.Focused ? T.Accent : T.Border))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, "\uE721", glyphFont, new Rectangle(searchRect.X + S(4), searchRect.Y, S(26), searchRect.Height), T.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (search.Text.Length == 0 && !search.Focused)
                TextRenderer.DrawText(g, "Search apps", rowFont, new Rectangle(search.Left, searchRect.Y, search.Width, searchRect.Height), T.SubText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            listRect = new Rectangle(area.X, area.Y + S(44), area.Width, area.Bottom - area.Y - S(44));
            var list = listRect;
            int rowH = S(40), headH = S(30);
            // Clip text too: TextRenderer ignores the Graphics clip unless told to keep it.
            var clipText = TextFormatFlags.PreserveGraphicsClipping;

            bool searching = search.Text.Trim().Length > 0;
            var entries = searching ? SearchResults(search.Text) : AllEntries();

            var state = g.Save();
            g.SetClip(list);
            int y = list.Y - scroll;
            char? letter = null;
            foreach (var e in entries)
            {
                if (!searching)
                {
                    char c = char.ToUpperInvariant(e.Name.FirstOrDefault(char.IsLetterOrDigit));
                    if (!char.IsLetter(c)) c = '#';
                    if (letter != c)
                    {
                        letter = c;
                        if (y + headH > list.Y && y < list.Bottom)
                            TextRenderer.DrawText(g, c.ToString(), letterFont, new Rectangle(list.X + S(12), y, S(40), headH), T.Accent,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | clipText);
                        y += headH;
                    }
                }
                var r = new Rectangle(list.X, y, list.Width - S(16), rowH - S(2));
                if (y + rowH > list.Y && y < list.Bottom)
                {
                    var visible = Rectangle.Intersect(r, list);
                    int hi = AddAppHit(visible, e);
                    if (hi == hover && !dragging && !autoScroll)
                        using (var p = BarForm.Rounded(r, S(6))) using (var b = new SolidBrush(Color.FromArgb(200, T.Selection))) g.FillPath(b, p);
                    int ic = S(24);
                    DrawIcon(g, e, new Rectangle(r.X + S(12), r.Y + (r.Height - ic) / 2, ic, ic));
                    TextRenderer.DrawText(g, e.Name, rowFont, new Rectangle(r.X + S(48), r.Y, r.Width - S(56), r.Height), T.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | clipText);
                }
                y += rowH;
            }
            if (searching && entries.Count == 0)
                TextRenderer.DrawText(g, "No apps match \u201C" + search.Text.Trim() + "\u201D", rowFont, new Rectangle(list.X, list.Y + S(20), list.Width, S(30)), T.SubText,
                    TextFormatFlags.HorizontalCenter | clipText);
            g.Restore(state);

            int total = y + scroll - list.Y;
            scrollMax = Math.Max(0, total - list.Height);
            scroll = Math.Min(scroll, scrollMax);
            trackRect = new Rectangle(list.Right - S(12), list.Y, S(12), list.Height);
            if (scrollMax > 0)
            {
                int th = Math.Max(S(36), (int)((long)list.Height * list.Height / total));
                int ty = list.Y + (int)((long)(list.Height - th) * scroll / scrollMax);
                thumbRect = new Rectangle(trackRect.X, ty, trackRect.Width, th);
                bool hot = dragging || thumbRect.Contains(PointToClient(Cursor.Position));
                int w = hot ? S(7) : S(4);
                using var p = BarForm.Rounded(new Rectangle(trackRect.Right - w - S(2), ty, w, th), w / 2f);
                using var b = new SolidBrush(hot ? T.SubText : T.Border);
                g.FillPath(b, p);
            }
            else thumbRect = Rectangle.Empty;

            if (autoScroll) // the familiar middle-click "anchor" marker
            {
                var a = new Rectangle(autoAnchor.X - S(14), autoAnchor.Y - S(14), S(28), S(28));
                using (var b = new SolidBrush(Color.FromArgb(230, T.Surface))) g.FillEllipse(b, a);
                using (var pen = new Pen(T.SubText, Math.Max(1f, s))) g.DrawEllipse(pen, a);
                using var tri = new SolidBrush(T.Text);
                int cx = autoAnchor.X, cy = autoAnchor.Y, t = S(4);
                g.FillPolygon(tri, new[] { new Point(cx, cy - S(9)), new Point(cx - t, cy - S(4)), new Point(cx + t, cy - S(4)) });
                g.FillPolygon(tri, new[] { new Point(cx, cy + S(9)), new Point(cx - t, cy + S(4)), new Point(cx + t, cy + S(4)) });
                g.FillEllipse(tri, cx - S(2), cy - S(2), S(4), S(4));
            }
        }

        void PaintFooter(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(bg != null ? 150 : 255, T.Surface))) g.FillRectangle(b, r);
            using (var pen = new Pen(T.Border)) g.DrawLine(pen, r.X, r.Y, r.Right, r.Y);

            // account
            string user = string.IsNullOrWhiteSpace(settings.AccountName) ? "Wisp" : settings.AccountName.Trim();
            int av = S(32);
            int uw = Math.Min(S(260), av + S(20) + TextRenderer.MeasureText(user, rowFont).Width + S(16));
            var ur = new Rectangle(r.X + S(16), r.Y + (r.Height - S(44)) / 2, uw, S(44));
            int ui = AddHit(new Hit { R = ur, Click = () => AccountMenu(ur) });
            if (ui == hover) using (var p = BarForm.Rounded(ur, S(6))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            var avr = new Rectangle(ur.X + S(8), ur.Y + (ur.Height - av) / 2, av, av);
            var picture = settings.ShowAccountPicture ? AccountPicture.Get() : null;
            if (picture != null)
            {
                using var clip = new GraphicsPath();
                clip.AddEllipse(avr);
                var state = g.Save();
                g.SetClip(clip);
                g.DrawImage(picture, avr);
                g.Restore(state);
                using var ring = new Pen(T.Border, Math.Max(1f, s));
                g.DrawEllipse(ring, avr);
            }
            else
            {
                using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, avr);
                var onAccent = T.Accent.GetBrightness() > 0.6f ? Color.FromArgb(20, 20, 20) : Color.White;
                TextRenderer.DrawText(g, user.Substring(0, 1).ToUpperInvariant(), avatarFont, avr, onAccent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            TextRenderer.DrawText(g, user, rowFont, new Rectangle(avr.Right + S(10), ur.Y, ur.Right - avr.Right - S(14), ur.Height), T.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // right-hand buttons: File Explorer, Settings, Power
            int bs = S(40), x = r.Right - S(16) - bs;
            void Button(string glyph, string tip, Action click)
            {
                var br = new Rectangle(x, r.Y + (r.Height - bs) / 2, bs, bs);
                int i = AddHit(new Hit { R = br, Click = click, Tip = tip });
                if (i == hover) using (var p = BarForm.Rounded(br, S(6))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
                TextRenderer.DrawText(g, glyph, glyphFont, br, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x -= bs + S(4);
            }
            // (power has its own button next to Start in the bottom-left corner)
            Button("", "Settings", () => Run(() => Launcher.Start("ms-settings:", null, null)));
            Button("", "File Explorer", () => Run(() => Launcher.Start("explorer.exe", null, null)));
        }

        void DrawIcon(Graphics g, AppEntry e, Rectangle r)
        {
            if (e.Icon != null) { g.DrawImage(e.Icon, r); return; }
            using (var p = BarForm.Rounded(r, S(6))) using (var b = new SolidBrush(T.Border)) g.FillPath(b, p);
            TextRenderer.DrawText(g, e.Name.Substring(0, 1).ToUpperInvariant(), letterFont, r, T.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int AddAppHit(Rectangle r, AppEntry e) => AddHit(new Hit
        {
            R = r,
            Entry = e,
            Click = () => Run(() => { Launcher.LaunchApp(e, false); usage.Record(e, ""); }),
            RightClick = pt => AppMenu(e, pt),
        });

        // ---------- menus & actions ----------

        void Run(Action a)
        {
            CloseMenu();
            try { a(); }
            catch (Exception ex)
            {
                if (ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223) return;
                MessageBox.Show("Couldn't complete that:\n" + ex.Message, "WispR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void AppMenu(AppEntry e, Point pt)
        {
            var m = new BarMenu(T);
            m.AddItem(e.Name, null, bold: true);
            m.AddSeparator();
            m.AddItem("Open", () => Run(() => { Launcher.LaunchApp(e, false); usage.Record(e, ""); }));
            if (Launcher.CanElevate(e)) m.AddItem("Run as administrator", () => Run(() => Launcher.LaunchApp(e, true)));
            m.AddSeparator();
            m.AddItem(settings.IsPinned(e) ? "Unpin from Start" : "Pin to Start", () => { settings.TogglePin(e); Invalidate(); });
            if (ToggleTaskbarPin != null)
                m.AddItem(IsOnTaskbar(e) ? "Unpin from taskbar" : "Pin to taskbar", () => { ToggleTaskbarPin(e); Invalidate(); });
            string loc = Launcher.GetInstallLocation(e);
            m.AddItem("Open file location", loc == null ? (Action)null : () => Run(() => Launcher.ShowInExplorer(loc)));
            m.AddSeparator();
            m.AddItem("Uninstall", () => Run(() => Launcher.Start(e.IsStoreApp ? "ms-settings:appsfeatures" : "appwiz.cpl", null, null)));
            m.ShowAt(pt, new Rectangle(pt, Size.Empty), above: false);
        }

        void AccountMenu(Rectangle r)
        {
            var m = new BarMenu(T);
            m.AddItem("Account settings", () => Run(() => Launcher.Start("ms-settings:yourinfo", null, null)));
            m.AddSeparator();
            m.AddItem("Lock", () => Run(() => LockWorkStation()));
            m.AddItem("Sign out", () => Run(() => { BeforeLeaving?.Invoke(); Shutdown("/l"); }));
            var screen = new Rectangle(PointToScreen(r.Location), r.Size);
            m.ShowAt(new Point(screen.X + screen.Width / 2, screen.Y), screen, above: true);
        }

        static void Shutdown(string args) =>
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

        // ---------- mouse ----------

        int HitAt(Point p) => hits.FindLastIndex(h => h.R.Contains(p));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (autoScroll && !(e.Button == MouseButtons.Middle && !autoMoved)) { StopAutoScroll(); swallowUp = true; return; }
            if (!allApps) return;

            if (e.Button == MouseButtons.Left && thumbRect.Width > 0 && Rectangle.Inflate(thumbRect, S(2), 0).Contains(e.Location))
            {
                dragging = true;
                dragOffset = e.Y - thumbRect.Y;
                swallowUp = true;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Left && scrollMax > 0 && trackRect.Contains(e.Location))
            {
                // click above/below the handle: jump a page
                scroll = Math.Max(0, Math.Min(scrollMax, scroll + (e.Y < thumbRect.Y ? -listRect.Height : listRect.Height)));
                swallowUp = true;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Middle && listRect.Contains(e.Location) && scrollMax > 0)
            {
                if (autoScroll) { StopAutoScroll(); swallowUp = true; return; } // second middle click stops it
                autoScroll = true; autoMoved = false;
                autoAnchor = e.Location; autoStart = DateTime.Now; autoCarry = 0;
                Cursor = Cursors.NoMoveVert;
                autoTimer.Start();
                swallowUp = true;
                Invalidate();
            }
        }

        bool swallowUp;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                int travel = Math.Max(1, trackRect.Height - thumbRect.Height);
                int top = e.Y - dragOffset - trackRect.Y;
                scroll = Math.Max(0, Math.Min(scrollMax, (int)((long)top * scrollMax / travel)));
                Invalidate();
                return;
            }
            if (autoScroll) return;
            int h = HitAt(e.Location);
            bool overTrack = allApps && trackRect.Contains(e.Location);
            if (h != hover || overTrack != wasOverTrack) { hover = h; wasOverTrack = overTrack; Invalidate(); }
        }

        bool wasOverTrack;

        protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hover = -1; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging && e.Button == MouseButtons.Left) { dragging = false; swallowUp = false; Invalidate(); return; }
            if (autoScroll && e.Button == MouseButtons.Middle)
            {
                // held and moved = "hold to scroll": releasing stops. A quick click keeps it running.
                if (autoMoved || (DateTime.Now - autoStart).TotalMilliseconds > 400) StopAutoScroll();
                swallowUp = false;
                return;
            }
            if (swallowUp) { swallowUp = false; return; }
            int i = HitAt(e.Location);
            if (i < 0) return;
            var hit = hits[i];
            if (e.Button == MouseButtons.Left) hit.Click?.Invoke();
            else if (e.Button == MouseButtons.Right) hit.RightClick?.Invoke(PointToScreen(e.Location));
        }

        void AutoScrollTick()
        {
            if (!autoScroll || !Visible) { StopAutoScroll(); return; }
            int dy = PointToClient(Cursor.Position).Y - autoAnchor.Y, dead = S(10);
            if (Math.Abs(dy) <= dead) return;
            autoMoved = true;
            // the further from the anchor, the faster (like browsers)
            autoCarry += Math.Sign(dy) * (float)Math.Pow(Math.Abs(dy) - dead, 1.3) * 0.08f;
            int step = (int)autoCarry;
            if (step == 0) return;
            autoCarry -= step;
            int before = scroll;
            scroll = Math.Max(0, Math.Min(scrollMax, scroll + step));
            if (scroll != before) Invalidate();
        }

        void StopAutoScroll()
        {
            if (!autoScroll) return;
            autoScroll = false;
            autoTimer.Stop();
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e) => ScrollBy(e.Delta);

        void ScrollBy(int delta)
        {
            if (!allApps) { if (delta < 0) OpenAllApps(focusSearch: false); return; }
            if (scroll == 0 && delta > 0 && search.Text.Length == 0 && atTopSince != DateTime.MinValue && (DateTime.Now - atTopSince).TotalMilliseconds > 400)
            {
                allApps = false; search.Visible = false; hover = -1; Invalidate(); // scrolling up past the top goes back to Pinned
                return;
            }
            scroll = Math.Max(0, Math.Min(scrollMax, scroll - delta * S(40) / 120 * 2));
            atTopSince = scroll == 0 ? (atTopSince == DateTime.MinValue ? DateTime.Now : atTopSince) : DateTime.MinValue;
            Invalidate();
            Update();
            hover = HitAt(PointToClient(Cursor.Position)); // keep the highlight under the mouse while scrolling
            Invalidate();
        }

        DateTime atTopSince = DateTime.MinValue;

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
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */)
            {
                if (allApps && searchRect.Contains(PointToClient(Cursor.Position)))
                {
                    BeginInvoke((Action)EnableTyping);
                    m.Result = (IntPtr)1; // MA_ACTIVATE
                }
                else m.Result = (IntPtr)3; // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { watch.Dispose(); wheel.Dispose(); autoTimer.Dispose(); bg?.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    }

    /// <summary>The picture Windows shows for the signed-in account (Settings › Accounts › Your info).</summary>
    static class AccountPicture
    {
        static Bitmap cached;
        static string cachedPath;
        static DateTime checkedAt = DateTime.MinValue;

        public static Bitmap Get()
        {
            if ((DateTime.Now - checkedAt).TotalSeconds < 30) return cached; // look again at most every 30 s
            checkedAt = DateTime.Now;
            string path = FindPath();
            if (path == cachedPath) return cached;
            cached?.Dispose();
            cached = null;
            cachedPath = path;
            if (path == null) return null;
            try
            {
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                using var img = Image.FromStream(fs);
                cached = new Bitmap(img);
            }
            catch { cached = null; }
            return cached;
        }

        // Windows keeps several sizes per user under HKLM\...\AccountPicture\Users\<SID>.
        static string FindPath()
        {
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                if (sid == null) return null;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
                if (key == null) return null;
                foreach (var name in new[] { "Image192", "Image208", "Image240", "Image96", "Image448", "Image1080", "Image64" })
                    if (key.GetValue(name) is string p && System.IO.File.Exists(p)) return p;
            }
            catch { }
            return null;
        }
    }
}
