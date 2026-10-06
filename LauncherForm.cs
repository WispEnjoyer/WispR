using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    sealed class ResultItem
    {
        public enum Kind { App, Run, Web, Calc, Setting, Timer }
        public Kind Type;
        public AppEntry Entry;
        public string Title, Subtitle, Text;
        public SettingsCatalog.Page Page;
        public TimeSpan TimerLength; // Timer: how long (zero = no time given yet)
    }

    /// <summary>The popup: a search box with a list of results underneath.</summary>
    sealed class LauncherForm : Form
    {
        int MaxRows => settings.LauncherRows;

        readonly AppIndex index;
        readonly Usage usage;
        readonly Settings settings;
        readonly TextBox box;
        readonly ListBox list;
        readonly float s;
        readonly int rowH;
        readonly Font boxFont, titleFont, subFont, glyphFont, menuFont;
        Font boldMenuFont;
        Font iconFont;

        Rectangle pill;          // the rounded search field
        int contentTop;          // where the results / wallpaper carousel start
        int anchorY = -1;        // "Bottom": the window's bottom edge; otherwise its top edge
        Rectangle bgRect;        // where the background picture is on screen
        readonly LauncherShell shell = new LauncherShell();
        bool createdAttached;    // the window was created for the attached look (no drop shadow)
        int slide;               // slide-in offset (px below the final position) while opening
        readonly System.Diagnostics.Stopwatch slideClock = new System.Diagnostics.Stopwatch();
        int slideFrom;

        /// <summary>"Bottom": attached to the bottom edge of the screen, growing out of it.</summary>
        bool Attached => settings.LauncherPosition == "Bottom";
        /// <summary>The bottom taskbar on a screen (set by the app); the attached launcher sits on it.</summary>
        public Func<Point, (Rectangle Bounds, IntPtr Handle)?> TaskbarAt;
        /// <summary>With the screen frame on: the top of its bar band (the launcher grows out of it).</summary>
        public Func<Point, int?> FrameEdgeAt;
        bool framed; // rising out of the screen frame's band
        IntPtr dockBar;          // the taskbar the launcher currently rises out of
        Rectangle dock;          // its bounds (empty when not docked)
        int dockScreenBottom;    // bottom edge of that screen
        bool Docked => Attached && dockBar != IntPtr.Zero && !dock.IsEmpty;
        int DockGap => FrameInset + (int)(4 * s); // space between the results and the taskbar row
        int TopRadius => Ui.CornerPx(s);
        int Flare => Ui.CornerPx(s);
        /// <summary>How far the launcher window sits inside the frame, so it stays clear of the rounded corners.</summary>
        int FrameInset => (int)Math.Ceiling(TopRadius * 0.3) + 1;
        bool SearchBelow => settings.LauncherPosition == "Bottom"; // results above, search box at the bottom
        Bitmap background;       // pre-rendered image + tint, or null
        string bgKey;            // what the background bitmap was built for
        readonly Backdrop backdrop;
        ShellMenu activeShellMenu;
        bool menuOpen;
        Point dragStart;
        int dragIndex = -1;
        bool dragged;

        /// <summary>Raised when Windows announces a new desktop wallpaper.</summary>
        public event Action WallpaperChanged;

        /// <summary>Pins/unpins an app on the WispR taskbar; null when the taskbar is off (set by the tray).</summary>
        public Func<AppEntry, bool> IsOnTaskbar;
        public Action<AppEntry> ToggleTaskbarPin;

        /// <summary>Shows a tray notification (set by the tray).</summary>
        public Action<string, string> Notify;

        Theme T => settings.Theme;
        public int IconPixelSize => (int)(32 * s);

        public LauncherForm(AppIndex index, Usage usage, Settings settings, Backdrop backdrop)
        {
            this.backdrop = backdrop;
            this.index = index;
            this.usage = usage;
            this.settings = settings;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Text = "WispR";

            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            rowH = Math.Min(255, (int)(48 * s));
            picker = new WallpaperPicker(settings, this, s);
            Owner = shell; // the launcher always stays in front of its frame
            picker.Apply += OnWallpaperChosen;

            boxFont = new Font("Segoe UI", 14f);
            titleFont = new Font("Segoe UI", 10.5f);
            subFont = new Font("Segoe UI", 8.25f);
            glyphFont = new Font("Segoe UI Semibold", 11f);
            menuFont = new Font("Segoe UI", 9.5f);

            box = new TextBox { BorderStyle = BorderStyle.None, Font = boxFont };
            list = new SmoothList
            {
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = rowH,
                IntegralHeight = false,
                TabStop = false,
            };
            Controls.Add(box);
            Controls.Add(list);

            box.TextChanged += (o, e) => UpdateResults();
            list.DrawItem += DrawItem;
            ((SmoothList)list).PaintEmpty = (g, r) =>
            {
                var bb = RowBackgroundBrush();
                if (bb != null) g.FillRectangle(bb, r);
                else using (var b = new SolidBrush(T.Background)) g.FillRectangle(b, r);
            };
            list.SelectedIndexChanged += (o, e) => GlideSelection();
            list.MouseMove += (o, e) =>
            {
                if (menuOpen) return;
                if (e.Button == MouseButtons.Left && dragIndex >= 0 && !dragged)
                {
                    var ds = SystemInformation.DragSize;
                    var zone = new Rectangle(dragStart.X - ds.Width / 2, dragStart.Y - ds.Height / 2, ds.Width, ds.Height);
                    if (!zone.Contains(e.Location)) { dragged = true; StartDrag(dragIndex); }
                    return;
                }
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0 && i != list.SelectedIndex) list.SelectedIndex = i;
            };
            list.MouseDown += (o, e) =>
            {
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0) list.SelectedIndex = i;
                if (e.Button == MouseButtons.Left) { dragStart = e.Location; dragIndex = i; dragged = false; }
            };
            list.MouseUp += (o, e) =>
            {
                int i = list.IndexFromPoint(e.Location);
                bool wasDrag = dragged;
                dragIndex = -1; dragged = false;
                if (i < 0 || wasDrag) return;
                if (e.Button == MouseButtons.Left)
                    Launch((ResultItem)list.Items[i], admin: (ModifierKeys & (Keys.Control | Keys.Shift)) == (Keys.Control | Keys.Shift));
                else if (e.Button == MouseButtons.Right)
                    ShowItemMenu(i, list.PointToScreen(e.Location));
            };
            Deactivate += (o, e) =>
            {
                if (menuOpen || !Visible) return;
                // Handing the focus over can bounce for a moment right after opening — that isn't you
                // clicking elsewhere, so take the focus back instead of closing.
                if ((DateTime.Now - shownAt).TotalMilliseconds < 600) { BeginInvoke((Action)ForceForeground); return; }
                HideLauncher("lost focus");
            };
            outsideWatch.Tick += (o, e) => WatchOutside();
            Activated += (o, e) => TuckUnderTaskbar(); // activating raises it; keep the taskbar row on top

            ApplySettings();
        }

        // ---------- settings / theme ----------

        // ---------- wallpaper picker ("wallpaper") ----------

        readonly WallpaperPicker picker;

        /// <summary>The launcher is wider while the wallpaper carousel shows.</summary>
        /// <summary>Width of the launcher window itself (inside the frame when attached).</summary>
        int FormWidth() => Docked ? Math.Max(TargetWidth() - FrameInset * 2, dock.Width)
                         : Attached ? TargetWidth() - FrameInset * 2 : TargetWidth();

        int TargetWidth()
        {
            int normal = (int)(settings.Width * s);
            if (!picker.Active) return normal;
            var area = Screen.FromPoint(new Point(Left + Width / 2, Top + 10)).WorkingArea;
            return Math.Min(Math.Max(normal, (int)(960 * s)), area.Width - (int)(40 * s));
        }

        Rectangle PickerArea
        {
            get
            {
                int pad = (int)(10 * s), w = Width - pad * 2;
                return new Rectangle(pad, contentTop, w, picker.PreferredHeight(w));
            }
        }

        /// <summary>Changes the width while keeping the launcher centred where it is.</summary>
        void Rewidth()
        {
            int center = Left + Width / 2, w = FormWidth();
            if (w != Width) Left = center - w / 2;
            ApplySettings();
        }

        void EnterPicker(string nameFilter)
        {
            bool was = picker.Active;
            picker.Open(nameFilter);
            if (!was) Rewidth();
            Relayout(0);
        }

        void ExitPicker()
        {
            picker.Close();
            Cursor = Cursors.Default;
            Rewidth();
        }

        void OnWallpaperChosen(WallpaperPicker.Item item, bool alsoColours)
        {
            string path = item.Path, weJson = item.WeJson;
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = false;
                if (weJson != null)
                {
                    // Wallpaper Engine plays it — animations, effects and all
                    try { ok = WallpaperEngine.Open(weJson); } catch (Exception ex) { Log.Error("WallpaperEngine.Open", ex); }
                    if (!ok) Log.Write("Couldn't open " + weJson + " in Wallpaper Engine");
                }
                else
                {
                    try { ok = Wallpaper.Set(path); } catch (Exception ex) { Log.Error("SetWallpaper", ex); }
                    if (!ok) Log.Write("Couldn't set the wallpaper to " + path);
                    // Wallpaper Engine would keep drawing over the picture: take its wallpaper down
                    try { WallpaperEngine.CloseWallpaper(); } catch (Exception ex) { Log.Error("WallpaperEngine.Close", ex); }
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (alsoColours)
                        {
                            settings.UseWallpaper = true;
                            settings.MatchImageColors = true;
                            settings.NotifyChanged();
                        }
                        WallpaperChanged?.Invoke();
                    }));
                }
                catch { }
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (picker.Active) picker.MouseMove(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (picker.Active) picker.MouseLeave();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (picker.Active && e.Button == MouseButtons.Left) picker.Click(e.Location, (ModifierKeys & Keys.Control) != 0);
            if (picker.Active && e.Button == MouseButtons.Right) picker.RightClick(e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (picker.Active) picker.Wheel(e.Delta);
        }

        public void ApplySettings()
        {
            if (IsHandleCreated && createdAttached != Attached) { RecreateHandle(); shell.Hide(); }
            Width = FormWidth();
            int inset = (int)(10 * s), ip = (int)(10 * s);
            pill = new Rectangle(inset, inset, Width - inset * 2, box.PreferredHeight + ip * 2);
            int iconSpace = (int)(32 * s);
            box.Location = new Point(pill.X + ip + iconSpace, pill.Y + (pill.Height - box.PreferredHeight) / 2 + (int)s);
            box.Width = pill.Right - ip - box.Left;
            list.Location = new Point((int)(6 * s), list.Top);
            list.Width = Width - (int)(12 * s);

            backdrop.Refresh();

            BackColor = T.Background;
            list.BackColor = T.Background;
            box.BackColor = T.Surface;
            box.ForeColor = T.Text;
            if (Math.Abs(Opacity - settings.Opacity / 100.0) > 0.001) Opacity = settings.Opacity / 100.0;

            RebuildBackground();
            if (IsHandleCreated) ApplyDwm();
            Relayout(list.Items.Count);
            list.Invalidate();
        }

        int MaxHeight
        {
            get
            {
                int pad = (int)(10 * s);
                int content = Math.Max(MaxRows * rowH, picker.PreferredHeight(Width - pad * 2));
                return pill.Height + pad * 2 + (int)(12 * s) + content;
            }
        }

        void RebuildBackground()
        {
            // The picture covers the largest the launcher can get, anchored like the window, so typing
            // (which changes the height) doesn't need a new one.
            int top = anchorY < 0 ? Top : SearchBelow ? anchorY - MaxHeight : anchorY;
            var rect = new Rectangle(Left, top, Width, MaxHeight);
            if (Attached) // also covers the frame around the window, so both show exactly the same picture
                rect = new Rectangle(Left - FrameInset - Flare, top - FrameInset, Width + (FrameInset + Flare) * 2,
                    (Docked ? dockScreenBottom - top : MaxHeight) + FrameInset);
            bgRect = rect;
            string key = backdrop.KeyFor(rect);
            if (key == bgKey) return;
            bgKey = key;
            background?.Dispose(); rowBrush?.Dispose(); rowBrush = null;
            background = backdrop.Render(rect);
        }

        void ApplyDwm()
        {
            try
            {
                int round = Attached ? 1 /* DONOTROUND: the frame draws the corners */ : 2 /* ROUND */;
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int border = Attached ? unchecked((int)0xFFFFFFFE) /* no border */ : unchecked((int)0xFFFFFFFF) /* default */;
                DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
                int dark = T.IsLight ? 0 : 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            }
            catch { /* older Windows */ }
        }

        // ---------- show / hide ----------

        public void Toggle()
        {
            if (IsOpen) HideLauncher("toggled"); else ShowLauncher();
        }

        public void ShowLauncher()
        {
            if (closing) FinishHide(); // reopened while still closing
            var fgBefore = GetForegroundWindow();
            if (fgBefore != IntPtr.Zero && !OwnProcess(fgBefore)) previousForeground = fgBefore;
            selPos = -1;
            index.RefreshIfStale(TimeSpan.FromMinutes(2));
            picker.Close();
            var screen = Screen.FromPoint(Cursor.Position);
            var area = Attached ? screen.Bounds : screen.WorkingArea; // attached: the very bottom edge of the screen
            int gap = (int)(12 * s);
            // With WispR's taskbar at the bottom, the launcher becomes one panel with it: the
            // results sit above the icon row, and the panel wraps around both.
            var bar = Attached ? TaskbarAt?.Invoke(Cursor.Position) : null;
            int? frameEdge = Attached ? FrameEdgeAt?.Invoke(Cursor.Position) : null;
            framed = frameEdge != null;
            dockBar = bar?.Handle ?? IntPtr.Zero;   // stays below the taskbar row either way
            // in the frame, the band already holds the taskbar: the launcher just grows out of its top edge
            dock = framed ? Rectangle.Empty : bar?.Bounds ?? Rectangle.Empty;
            dockScreenBottom = screen.Bounds.Bottom;
            Width = FormWidth();
            anchorY = framed ? frameEdge.Value
                    : Docked ? dock.Top - DockGap
                    : Attached ? area.Bottom
                    : settings.LauncherPosition == "Top" ? area.Top + gap
                    : area.Y + (int)(area.Height * 0.2);
            int centerX = Docked ? dock.X + dock.Width / 2 : area.X + area.Width / 2;
            Location = new Point(centerX - Width / 2, SearchBelow ? anchorY - Height : anchorY);
            slide = 0;
            ApplySettings(); // cheap when nothing changed; picks up a new wallpaper / position
            box.Text = "";
            UpdateResults();
            if (Attached) StartSlide();
            else Anim.PopIn(this, (int)((SearchBelow ? 10 : -10) * s), 170); // fade in, gliding a little into place
            Show();
            UpdateShell();
            ForceForeground();
            TuckUnderTaskbar();
            box.Focus();
            buttonsDown = MouseButtonsDown(); // a press that's already happening doesn't count
            shownAt = DateTime.Now;
            hadFocus = false;
            focusRetries = 0;
            outsideWatch.Start();
            Log.Throttled("launcher-open", "Launcher opened.");
        }

        // ---------- close when you click somewhere else ----------
        // "Deactivate" alone isn't enough: Windows doesn't always hand the launcher the focus, and
        // clicks on windows that never take focus (the bars, the desktop under Wallpaper Engine)
        // don't deactivate it. So any new mouse press outside the launcher closes it, and so does
        // another window coming to the front.

        readonly System.Windows.Forms.Timer outsideWatch = new System.Windows.Forms.Timer { Interval = 40 };
        int buttonsDown;
        DateTime shownAt;

        static int MouseButtonsDown() =>
            ((GetAsyncKeyState(0x01) & 0x8000) != 0 ? 1 : 0) | ((GetAsyncKeyState(0x02) & 0x8000) != 0 ? 2 : 0) |
            ((GetAsyncKeyState(0x04) & 0x8000) != 0 ? 4 : 0);

        void WatchOutside()
        {
            if (!Visible) { outsideWatch.Stop(); return; }
            int now = MouseButtonsDown();
            int pressed = now & ~buttonsDown;
            buttonsDown = now;
            if (menuOpen) return;
            if (pressed != 0 && !Bounds.Contains(Cursor.Position) && !(shell.Visible && shell.Bounds.Contains(Cursor.Position)))
            {
                HideLauncher("clicked outside");
                return;
            }
            // some other app came to the front (e.g. Alt+Tab, a notification click)
            // (only once the launcher really had the focus, so a failed hand-over doesn't close it)
            IntPtr fg = GetForegroundWindow();
            double age = (DateTime.Now - shownAt).TotalMilliseconds;
            if (fg == Handle) hadFocus = true;
            else if (!hadFocus)
            {
                // Windows didn't give us the focus yet (it can refuse right after you clicked another
                // app): keep asking for a moment, so you can type straight away.
                if (age < 1500 && ++focusRetries % 5 == 0) ForceForeground();
            }
            else if (age > 400 && fg != IntPtr.Zero && fg != shell.Handle && !OwnProcess(fg)) HideLauncher("another window came to the front");
        }

        bool hadFocus;
        int focusRetries;
        static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        static bool OwnProcess(IntPtr h)
        {
            GetWindowThreadProcessId(h, out uint pid);
            return pid == myPid;
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        /// <summary>Open and not on its way out.</summary>
        public bool IsOpen => Visible && !closing;
        bool closing;
        const string CloseAnim = "launcher-close";

        /// <summary>Closes with a short exit animation: back into the edge / taskbar, or a quick fade.</summary>
        void HideLauncher(string reason = null)
        {
            if (!Visible || closing) return;
            if (reason != null) Log.Throttled("launcher-close:" + reason, "Launcher closed: " + reason + ".");
            closing = true;
            // closed without going anywhere (Windows key, Esc): the focus goes back to where it was.
            // Not after launching something: the new app must be free to take the front.
            giveFocusBack = reason == "toggled" || reason == "escape";
            Anim.Settle(this); // a fade-in still running would fight the exit
            outsideWatch.Stop();
            Anim.StopFrames(SlideKey);
            if (Attached)
            {
                // sinks back down behind the taskbar row: it stays under the bars the whole way
                // (losing the focus can lift it above them, so it's put back underneath every frame)
                int from = slide, to = Math.Max(Height, (int)(60 * s)) + FrameInset;
                TuckUnderTaskbar();
                Anim.Run(CloseAnim, 150, e =>
                {
                    slide = (int)Math.Round(Anim.Lerp(from, to, e));
                    int top = anchorY - Height + slide;
                    if (Top != top) Top = top;
                    ClipWhileSliding();
                    UpdateShell();
                    TuckUnderTaskbar();
                    Update();
                }, FinishHide, InQuad);
            }
            else
            {
                double op = Opacity;
                Anim.Run(CloseAnim, 110, e => Opacity = Math.Max(0.01, op * (1 - e)), FinishHide);
            }
        }

        /// <summary>Starts gently and picks up speed: reads as "dropping away" without a jolt at the start.</summary>
        static double InQuad(double t) => t * t;

        IntPtr previousForeground;
        bool giveFocusBack;

        void RestorePreviousForeground()
        {
            var h = previousForeground;
            if (h == IntPtr.Zero || !IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || OwnProcess(h)) return;
            try { SetForegroundWindow(h); } catch { }
        }

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        /// <summary>Hides right away (end of the exit animation, or when it must go now).</summary>
        void FinishHide()
        {
            Anim.Stop(CloseAnim);
            closing = false;
            outsideWatch.Stop();
            Anim.StopFrames(SlideKey);
            slide = 0;
            // hand the focus back to where it was, so Windows doesn't pick something random
            // (like the wallpaper's full-screen window, which looked like a fullscreen app to the bars)
            if (giveFocusBack && Visible && GetForegroundWindow() == Handle) RestorePreviousForeground();
            giveFocusBack = false;
            if (shell.Visible) shell.Hide();
            if (Visible) Hide();
            if (Region != null) { Region.Dispose(); Region = null; } // only once hidden: no flash of the whole window
            double op = settings.Opacity / 100.0;
            if (Math.Abs(Opacity - op) > 0.001) Opacity = op;
            if (shell.Visible) shell.Hide();
            if (picker.Active) picker.Close();
            picker.ReleaseImages();
        }

        public void OnIndexChanged(bool listChanged)
        {
            if (!Visible) return;
            if (listChanged) RefreshKeepingSelection();
            else list.Invalidate();
        }

        void RefreshKeepingSelection()
        {
            var sel = (list.SelectedItem as ResultItem)?.Entry?.Key;
            UpdateResults();
            if (sel != null)
                for (int i = 0; i < list.Items.Count; i++)
                    if (((ResultItem)list.Items[i]).Entry?.Key == sel) { list.SelectedIndex = i; break; }
        }

        // ---------- searching ----------

        void UpdateResults()
        {
            string raw = box.Text.Trim();
            // "wallpaper" (optionally followed by a name filter) opens the wallpaper carousel
            if (WallpaperPicker.IsTrigger(raw, out var wallpaperFilter)) { EnterPicker(wallpaperFilter); return; }
            if (picker.Active) ExitPicker();
            string q = Matcher.Normalize(raw);
            var items = new List<ResultItem>();
            var entries = index.Entries;

            if (q.Length == 0)
            {
                // Empty box: your pinned apps, then the ones you use most.
                var byKey = entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
                foreach (var key in settings.Pinned)
                    if (byKey.TryGetValue(key, out var e) && items.Count < MaxRows) items.Add(AppItem(e, "Pinned"));
                items.AddRange(entries
                    .Where(e => usage.Count(e) > 0 && !settings.IsPinned(e))
                    .OrderByDescending(e => usage.Boost(e, ""))
                    .ThenByDescending(e => usage.LastUsed(e))
                    .Take(MaxRows - items.Count)
                    .Select(e => AppItem(e, "Frequent")));
            }
            else
            {
                // "set timer 10m", "set timer "Brot backen" 10m": a timer, and nothing else
                if (Timers.TryParse(raw, out var timerTitle, out var timerLength))
                {
                    items.Add(timerLength > TimeSpan.Zero
                        ? new ResultItem { Type = ResultItem.Kind.Timer, TimerLength = timerLength, Text = timerTitle,
                                           Title = (timerTitle.Length > 0 ? timerTitle + " — " : "Timer — ") + Timers.Describe(timerLength),
                                           Subtitle = "Start a timer   ·   it shows on the right edge of the screen" }
                        : new ResultItem { Type = ResultItem.Kind.Timer, Text = timerTitle,
                                           Title = "How long?", Subtitle = "e.g.  set timer 10m   ·   timer 1h30m   ·   set timer \"Brot backen\" 10m" });
                    ShowItems(items);
                    return;
                }

                // Maths / unit conversion: if it clearly is one, the answer goes first.
                var answer = Calculator.TryAnswer(raw);
                if (answer != null)
                    items.Add(new ResultItem { Type = ResultItem.Kind.Calc, Title = answer.Display, Subtitle = answer.Detail + "   \u00B7   Enter to copy", Text = answer.Copy });

                // Apps and Windows settings pages, ranked together (an app wins a tie).
                var apps = entries
                    .Select(e => (e, score: Matcher.Score(q, e)))
                    .Where(x => x.score >= 0)
                    .Select(x => (item: AppItem(x.e, null), score: x.score + usage.Boost(x.e, q) + Matcher.Penalty(x.e) + (settings.IsPinned(x.e) ? 60 : 0), len: x.e.Name.Length));
                var pages = answer != null ? Enumerable.Empty<(ResultItem, int, int)>() : SettingsCatalog.Search(q)
                    .Select(x => (item: new ResultItem { Type = ResultItem.Kind.Setting, Page = x.page, Title = x.page.Name, Subtitle = "Windows settings" },
                                  score: x.score - 40, len: x.page.Name.Length));
                items.AddRange(apps.Concat(pages)
                    .OrderByDescending(x => x.Item2)
                    .ThenBy(x => x.Item3)
                    .Take(MaxRows - items.Count)
                    .Select(x => x.Item1));

                if (items.Count < MaxRows)
                    items.Add(new ResultItem { Type = ResultItem.Kind.Run, Text = raw, Title = "Run  " + raw, Subtitle = "Command, file, folder or URL" });
                if (items.Count < MaxRows)
                    items.Add(new ResultItem { Type = ResultItem.Kind.Web, Text = raw, Title = "Search the web for  " + raw, Subtitle = settings.SearchEngine });
            }

            ShowItems(items);
        }

        void ShowItems(List<ResultItem> items)
        {
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(items.ToArray());
            noGlide = true;
            if (items.Count > 0) list.SelectedIndex = 0;
            noGlide = false;
            selPos = list.SelectedIndex;
            list.EndUpdate();
            Relayout(items.Count);
        }

        ResultItem AppItem(AppEntry e, string subtitle) => new ResultItem
        {
            Type = ResultItem.Kind.App,
            Entry = e,
            Title = e.Name,
            Subtitle = subtitle ?? (settings.IsPinned(e) ? "Pinned" : e.IsStoreApp ? "App" : "Application"),
        };

        /// <summary>
        /// Places the search box and the results (or the wallpaper carousel). With the launcher at the
        /// bottom the search box is at the bottom and the window grows upwards; otherwise it's the other way round.
        /// </summary>
        void Relayout(int rows)
        {
            int inset = (int)(10 * s), gap = (int)(6 * s);
            bool hasContent = picker.Active || rows > 0;
            int contentH = picker.Active ? picker.PreferredHeight(Width - inset * 2) : rows * rowH;

            int pillY;
            if (SearchBelow)
            {
                contentTop = gap;
                pillY = hasContent ? contentTop + contentH + gap : inset;
            }
            else
            {
                pillY = inset;
                contentTop = pillY + pill.Height + gap;
            }
            pill = new Rectangle(pill.X, pillY, pill.Width, pill.Height);
            box.Top = pill.Y + (pill.Height - box.PreferredHeight) / 2 + (int)s;
            list.Top = contentTop;
            list.Height = rows * rowH;
            list.Visible = !picker.Active && rows > 0;

            int h = SearchBelow || !hasContent ? pill.Bottom + inset : contentTop + contentH + gap;
            int top = anchorY < 0 ? Top : (SearchBelow ? anchorY - h : anchorY) + slide;
            if (h != Height || top != Top) SetBounds(Left, top, Width, h);
            Invalidate();
            UpdateShell();
        }

        // ---------- attached look: frame + slide-in ----------

        /// <summary>Redraws the frame around the window (or hides it when not attached).</summary>
        void UpdateShell()
        {
            if (!Attached || !Visible)
            {
                if (shell.Visible) shell.Hide();
                return;
            }
            int m = FrameInset, f = Flare;
            byte alpha = (byte)Math.Max(0, Math.Min(255, settings.Opacity * 255 / 100));
            try
            {
                if (Docked)
                {
                    // One panel around the results and the taskbar row. If the taskbar floats clearly above
                    // the screen edge, the panel is a rounded box around it; otherwise it flows into the edge.
                    int top = Top - m;
                    int bottom = dock.Bottom + m;
                    bool floating = bottom < dockScreenBottom - (int)(6 * s); // a sliver of gap would look accidental
                    if (!floating) bottom = dockScreenBottom;
                    top = Math.Min(top, dock.Top - m - TopRadius * 2); // keeps the shape valid while sliding in
                    var frame = new Rectangle(Left - m - f, top, Width + (m + f) * 2, bottom - top);
                    var visible = Rectangle.Intersect(Bounds, new Rectangle(Left, Top, Width, Math.Max(0, anchorY - Top)));
                    shell.Render(frame, visible, f, TopRadius, background, bgRect.Location, T.Background, T.Border, alpha,
                                 floating, dock, 8 * s);
                }
                else if (framed)
                {
                    // grows out of the frame's band: the outline ends exactly at the band's edge, so nothing of
                    // it reaches into the band while the launcher slides in or out
                    int top = Math.Min(Top - m, anchorY - TopRadius * 2 - m);
                    var frame = new Rectangle(Left - m - f, top, Width + (m + f) * 2, anchorY - top);
                    var visible = Rectangle.Intersect(Bounds, new Rectangle(Left, Top, Width, Math.Max(0, anchorY - Top)));
                    shell.Render(frame, visible, f, TopRadius, background, bgRect.Location, T.Background, T.Border, alpha);
                }
                else
                {
                    var frame = new Rectangle(Left - m - f, Top - m, Width + (m + f) * 2, Height + m);
                    shell.Render(frame, Bounds, f, TopRadius, background, bgRect.Location, T.Background, T.Border, alpha);
                }
            }
            catch (Exception ex) { Log.Error("LauncherShell.Render", ex); }
        }

        /// <summary>Opening animation: the launcher rises out of the bottom edge.</summary>
        void StartSlide()
        {
            slideFrom = Math.Max(Height, (int)(60 * s)) + FrameInset;
            slide = slideFrom;
            slideClock.Restart();
            Top = anchorY - Height + slide;
            ClipWhileSliding();
            Anim.Frames(SlideKey, SlideTick);
        }

        const string SlideKey = "launcher-slide";

        bool SlideTick()
        {
            if (!Visible || closing) return false;
            double p = Anim.Speed <= 0.01 ? 1 : Math.Min(1, slideClock.Elapsed.TotalMilliseconds / (200.0 * Anim.Speed));
            slide = (int)Math.Round((1 - Anim.OutQuint(p)) * slideFrom);
            if (p >= 1) slide = 0;
            int top = anchorY - Height + slide;
            if (Top != top) Top = top;
            ClipWhileSliding();
            UpdateShell();
            TuckUnderTaskbar();
            Update();
            return p < 1;
        }

        /// <summary>
        /// Keeps the launcher just *below* the taskbar in the z-order, so it rises out from behind the
        /// icon row and the row stays visible and clickable, as if it were part of the launcher.
        /// </summary>
        /// <summary>Docked: while rising, only the part above the taskbar row shows.</summary>
        void ClipWhileSliding()
        {
            if (!(Docked || framed) || slide <= 0) { if (Region != null) { Region.Dispose(); Region = null; } return; }
            Region?.Dispose();
            Region = new Region(new Rectangle(0, 0, Width, Math.Max(0, Height - slide)));
        }

        void TuckUnderTaskbar()
        {
            if (dockBar == IntPtr.Zero || !Visible) return;
            const uint flags = 0x1 | 0x2 | 0x10 | 0x200; // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
            Native.SetWindowPos(Handle, dockBar, 0, 0, 0, 0, flags);
            if (shell.Visible) Native.SetWindowPos(shell.Handle, Handle, 0, 0, 0, 0, flags);
        }

        // ---------- keyboard ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool ctrlShift = (keyData & (Keys.Control | Keys.Shift)) == (Keys.Control | Keys.Shift);
            if (picker.Active)
            {
                bool shift = (keyData & Keys.Shift) != 0;
                switch (key)
                {
                    case Keys.Left: picker.Move(-1); return true;
                    case Keys.Right: picker.Move(+1); return true;
                    case Keys.Tab: picker.Move(shift ? -1 : 1); return true;
                    case Keys.PageUp: picker.Move(-5); return true;
                    case Keys.PageDown: picker.Move(+5); return true;
                    case Keys.Home when (keyData & Keys.Control) != 0: picker.JumpTo(0); return true;
                    case Keys.End when (keyData & Keys.Control) != 0: picker.JumpTo(int.MaxValue); return true;
                    case Keys.Enter: picker.ApplySelected((keyData & Keys.Control) != 0); return true;
                    case Keys.F when (keyData & Keys.Control) != 0: picker.ToggleHeart(); return true;
                    case Keys.Up: case Keys.Down: return true;
                }
            }
            switch (key)
            {
                case Keys.Escape:
                    if (box.Text.Length > 0) box.Clear(); else HideLauncher("escape");
                    return true;
                case Keys.Down:
                case Keys.Tab when (keyData & Keys.Shift) == 0:
                    MoveSelection(+1); return true;
                case Keys.Up:
                case Keys.Tab:
                    MoveSelection(-1); return true;
                case Keys.Enter:
                    if (list.SelectedItem is ResultItem it) Launch(it, admin: ctrlShift);
                    return true;
                case Keys.Apps:
                case Keys.F10 when (keyData & Keys.Shift) != 0:
                    if (list.SelectedIndex >= 0)
                    {
                        var r = list.GetItemRectangle(list.SelectedIndex);
                        ShowItemMenu(list.SelectedIndex, list.PointToScreen(new Point(r.Left + (int)(60 * s), r.Bottom - (int)(8 * s))));
                    }
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void MoveSelection(int delta)
        {
            int n = list.Items.Count;
            if (n == 0) return;
            list.SelectedIndex = ((list.SelectedIndex + delta) % n + n) % n;
        }

        // ---------- launching ----------

        void Launch(ResultItem it, bool admin)
        {
            if (it.Type == ResultItem.Kind.Timer && it.TimerLength <= TimeSpan.Zero) return; // no time typed yet
            string q = Matcher.Normalize(box.Text);
            Run(it.Title, () =>
            {
                switch (it.Type)
                {
                    case ResultItem.Kind.App:
                        Launcher.LaunchApp(it.Entry, admin);
                        usage.Record(it.Entry, q);
                        break;
                    case ResultItem.Kind.Run:
                        Launcher.RunCommand(it.Text, admin);
                        break;
                    case ResultItem.Kind.Web:
                        Launcher.Start(settings.SearchUrl(it.Text), null, null);
                        break;
                    case ResultItem.Kind.Calc:
                        Clipboard.SetText(it.Text);
                        break;
                    case ResultItem.Kind.Timer:
                        if (it.TimerLength > TimeSpan.Zero) Timers.Start(it.Text, it.TimerLength);
                        break;
                    case ResultItem.Kind.Setting:
                        Launcher.Start(it.Page.Target, it.Page.Args, admin ? "runas" : null);
                        break;
                }
            });
        }

        /// <summary>Runs an action, hides the launcher, and reports failures (except a cancelled UAC prompt).</summary>
        void Run(string what, Action action, bool hide = true)
        {
            try
            {
                action();
                if (hide) HideLauncher();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x800704C7)) { }
            catch (Exception ex)
            {
                HideLauncher();
                MessageBox.Show("Couldn't complete \"" + what + "\":\n" + ex.Message, "WispR",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------- right-click menu ----------

        void ShowItemMenu(int index, Point screen)
        {
            if (index < 0 || index >= list.Items.Count) return;
            var it = (ResultItem)list.Items[index];
            if (it.Type == ResultItem.Kind.Web || it.Type == ResultItem.Kind.Calc || it.Type == ResultItem.Kind.Setting || it.Type == ResultItem.Kind.Timer) return;

            var menu = new ContextMenuStrip
            {
                Renderer = new ThemedMenuRenderer(T),
                ShowImageMargin = false,
                Font = menuFont,
                BackColor = T.Surface,
                ForeColor = T.Text,
            };
            ToolStripMenuItem Add(string text, Action onClick, bool enabled = true, bool bold = false)
            {
                var mi = new ToolStripMenuItem(text) { Enabled = enabled, Padding = new Padding(0, (int)(3 * s), 0, (int)(3 * s)) };
                if (bold) mi.Font = boldMenuFont ??= new Font(menuFont, FontStyle.Bold); // one font for all menus (was a new one per right-click)
                if (onClick != null) mi.Click += (o, e) => onClick();
                menu.Items.Add(mi);
                return mi;
            }

            if (it.Type == ResultItem.Kind.Run)
            {
                Add("Run", () => Launch(it, false), bold: true);
                Add("Run as administrator", () => Launch(it, true));
            }
            else
            {
                var e = it.Entry;
                Add("Open", () => Launch(it, false), bold: true);
                Add("Run as administrator", () => Launch(it, true), Launcher.CanElevate(e));
                menu.Items.Add(new ToolStripSeparator());

                Add(settings.IsPinned(e) ? "Unpin from Start" : "Pin to Start", () =>
                {
                    settings.TogglePin(e);
                    RefreshKeepingSelection();
                });

                if (settings.TaskbarEnabled && ToggleTaskbarPin != null)
                    Add(IsOnTaskbar(e) ? "Unpin from taskbar" : "Pin to taskbar", () => ToggleTaskbarPin(e));
                else
                Add("Pin to taskbar…", () => Run("Pin to taskbar", () =>
                {
                    // Windows only lets Explorer pin things. Try its command anyway (some builds allow it),
                    // otherwise show the app in Explorer, where the user's right-click can pin it.
                    if (TaskbarPin.Toggle(e, Handle)) return;
                    Launcher.RevealForPinning(e);
                    Notify?.Invoke("Pin \"" + e.Name + "\" to the taskbar",
                        e.LnkPath != null
                            ? "Right-click the selected shortcut → Pin to taskbar. (Windows 11: Show more options → Pin to taskbar.)"
                            : "Find it in the Applications window, right-click it → Pin to taskbar.");
                }));

                menu.Items.Add(new ToolStripSeparator());
                string install = Launcher.GetInstallLocation(e);
                Add("Open install folder", () => Run("Open install folder", () => Launcher.ShowInExplorer(install)), install != null);
                if (e.LnkPath != null)
                    Add("Open shortcut location", () => Run("Open shortcut location", () => Launcher.ShowInExplorer(e.LnkPath)));

                menu.Items.Add(new ToolStripSeparator());
                Add("More Windows options…", () => BeginInvoke((Action)(() => ShowNativeMenu(e, screen))));
            }

            menuOpen = true;
            menu.Closed += (o, a) =>
            {
                menuOpen = false;
                BeginInvoke((Action)(() =>
                {
                    menu.Dispose();
                    if (Visible && Form.ActiveForm != this) ForceForeground();
                }));
            };
            menu.Show(screen);
        }

        /// <summary>The full Explorer context menu for this app (Uninstall, Properties, Send to, …).</summary>
        void ShowNativeMenu(AppEntry e, Point screen)
        {
            string source = e.ParsingName != null ? @"shell:AppsFolder\" + e.ParsingName : e.LnkPath;
            using var sm = ShellMenu.TryCreate(source, extended: (ModifierKeys & Keys.Shift) != 0);
            if (sm == null) return;
            menuOpen = true;
            activeShellMenu = sm;
            int cmd;
            try { cmd = sm.Track(Handle, screen); }
            finally { activeShellMenu = null; menuOpen = false; }
            if (cmd > 0) Run("Windows command", () => sm.Invoke(cmd, Handle, screen));
            else if (Visible) ForceForeground();
        }

        protected override void WndProc(ref Message m)
        {
            if (activeShellMenu != null && activeShellMenu.HandleMenuMessage(ref m)) return;
            if (m.Msg == 0x1A /* WM_SETTINGCHANGE */ && m.WParam == (IntPtr)Wallpaper.SPI_SETDESKWALLPAPER)
                WallpaperChanged?.Invoke();
            base.WndProc(ref m);
        }

        // ---------- drag out (to desktop, a folder, or the taskbar on Windows 10) ----------

        void StartDrag(int index)
        {
            if (index < 0 || index >= list.Items.Count) return;
            var it = (ResultItem)list.Items[index];
            if (it.Type != ResultItem.Kind.App) return;
            string path = it.Entry.LnkPath;
            if (path == null)
            {
                var loc = Launcher.GetInstallLocation(it.Entry);
                if (loc != null && File.Exists(loc)) path = loc;
            }
            if (path == null) return;

            menuOpen = true; // don't hide while dragging
            DragDropEffects result;
            try { result = list.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Link); }
            finally { menuOpen = false; }
            if (result != DragDropEffects.None) HideLauncher();
            else if (Visible) ForceForeground();
        }

        // ---------- painting ----------

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (background != null) e.Graphics.DrawImage(background, bgRect.X - Left, bgRect.Y - Top);
            else using (var b = new SolidBrush(T.Background)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var path = RoundedRect(pill, 8 * s))
            using (var fill = new SolidBrush(T.Surface))
            using (var edge = new Pen(T.Border, 1))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }

            // magnifier glyph
            float r = 6.5f * s, cx = pill.X + 22 * s, cy = pill.Y + pill.Height / 2f - 1.5f * s;
            using (var pen = new Pen(T.SubText, 1.8f * s))
            {
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(pen, cx + r * 0.7f, cy + r * 0.7f, cx + r * 1.55f, cy + r * 1.55f);
            }

            if (picker.Active) picker.Paint(g, PickerArea, T);

            if (!IsWin11OrLater && !Attached) // Windows 11 draws its own rounded border
                using (var pen = new Pen(T.Border, 1))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        // ---------- the selection glides from row to row ----------

        float selPos = -1;          // animated position of the highlight, in rows (-1: follow the selection)
        bool noGlide;               // set while the list is refilled: jump instead of gliding

        /// <summary>A list box that doesn't wipe its background first (each row paints all of itself), so no flicker.</summary>
        /// <summary>
        /// A list box that paints every pixel of itself in one go, through a buffer: all visible rows plus
        /// whatever is below the last one. (Letting Windows paint row by row with no background left 1-pixel
        /// strips of the list's white default showing while the launcher slid in, until something repainted.)
        /// </summary>
        sealed class SmoothList : ListBox
        {
            public Action<Graphics, Rectangle> PaintEmpty; // the list's background (behind and below the rows)

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x14 /* WM_ERASEBKGND */) { m.Result = (IntPtr)1; return; }
                if (m.Msg == 0x0F /* WM_PAINT */ && DrawMode != DrawMode.Normal) { PaintAll(); m.Result = IntPtr.Zero; return; }
                base.WndProc(ref m);
            }

            void PaintAll()
            {
                var ps = new PAINTSTRUCT();
                IntPtr hdc = BeginPaint(Handle, ref ps);
                try
                {
                    int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
                    var all = new Rectangle(0, 0, w, h);
                    // WinForms' own double buffer: a screen-compatible memory surface, so Windows' text
                    // (ClearType, the grey subtitles) draws exactly as it does on screen
                    using var buffered = BufferedGraphicsManager.Current.Allocate(hdc, all);
                    var g = buffered.Graphics;
                    // the whole list first gets its background, so nothing (like a black seam) can show
                    // between rows
                    if (PaintEmpty != null) PaintEmpty(g, all);
                    else using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, all);
                    int sel = SelectedIndex;
                    for (int i = Math.Max(0, TopIndex), y = 0; i < Items.Count && y < h; i++)
                    {
                        var r = GetItemRectangle(i);
                        y = r.Bottom;
                        var state = i == sel ? DrawItemState.Selected | (Focused ? DrawItemState.Focus : 0) : DrawItemState.None;
                        var saved = g.Save();
                        g.SetClip(r);
                        OnDrawItem(new DrawItemEventArgs(g, Font, r, i, state, ForeColor, BackColor));
                        g.Restore(saved);
                    }
                    buffered.Render(hdc);
                }
                finally { EndPaint(Handle, ref ps); }
            }


            [StructLayout(LayoutKind.Sequential)]
            struct PAINTSTRUCT
            {
                public IntPtr hdc; public bool fErase; public int left, top, right, bottom; public bool fRestore, fIncUpdate;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
            }
            [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
            [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        }

        void GlideSelection()
        {
            int target = list.SelectedIndex;
            if (target < 0 || noGlide || selPos < 0 || !Visible)
            {
                Anim.Stop("launcher-sel");
                selPos = target;
                return;
            }
            float from = selPos;
            if (Math.Abs(from - target) < 0.001f) return;
            Anim.Run("launcher-sel", 120, e =>
            {
                float old = selPos;
                selPos = (float)Anim.Lerp(from, target, e);
                InvalidateRows(Math.Min(old, selPos), Math.Max(old, selPos));
            }, () => { selPos = list.SelectedIndex; list.Invalidate(); }, Anim.OutQuint);
        }

        void InvalidateRows(float a, float b)
        {
            int top = (int)Math.Floor(a) - list.TopIndex, bottom = (int)Math.Ceiling(b) - list.TopIndex + 1;
            list.Invalidate(new Rectangle(0, top * rowH, list.Width, (bottom - top) * rowH));
        }

        TextureBrush rowBrush;
        Bitmap rowBrushImage;
        Point rowBrushOffset;

        TextureBrush RowBackgroundBrush()
        {
            if (background == null) return null;
            var off = new Point(list.Left + Left - bgRect.X, list.Top + Top - bgRect.Y);
            if (rowBrush == null || !ReferenceEquals(rowBrushImage, background))
            {
                rowBrush?.Dispose();
                rowBrush = new TextureBrush(background, WrapMode.Tile);
                rowBrushImage = background;
                rowBrushOffset = new Point(int.MinValue, 0);
            }
            if (off != rowBrushOffset)
            {
                rowBrush.ResetTransform();
                rowBrush.TranslateTransform(-off.X, -off.Y);
                rowBrushOffset = off;
            }
            return rowBrush;
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var it = (ResultItem)list.Items[e.Index];
            var g = e.Graphics;
            var b = e.Bounds;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // The row's slice of the background, copied 1:1 through a texture brush. (DrawImage with a source
            // rectangle blends the slice's edge pixels with transparency, which left faint seams between rows.)
            var bgBrush = RowBackgroundBrush();
            g.SmoothingMode = SmoothingMode.None; // crisp row edges: no half-covered seam pixels
            if (bgBrush != null) g.FillRectangle(bgBrush, b);
            else using (var bg = new SolidBrush(T.Background)) g.FillRectangle(bg, b);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            bool selected = (e.State & DrawItemState.Selected) != 0;
            float pos = selPos >= 0 ? selPos : list.SelectedIndex;
            if (pos >= 0)
            {
                // the highlight sits at its (possibly in-between) position; each row draws its share of it
                var hb = new Rectangle(b.X, (int)Math.Round((pos - list.TopIndex) * rowH), b.Width, b.Height);
                if (hb.IntersectsWith(b))
                {
                    var state = g.Save();
                    g.SetClip(b);
                    var rr = Rectangle.Inflate(hb, -(int)(4 * s), -(int)(2 * s));
                    using var path = RoundedRect(rr, 6 * s);
                    using var sb = new SolidBrush(Color.FromArgb(background != null ? 210 : 255, T.Selection));
                    g.FillPath(sb, path);
                    using var bar = new SolidBrush(T.Accent);
                    g.FillRectangle(bar, rr.X, rr.Y + rr.Height / 4, (int)(3 * s), rr.Height / 2);
                    g.Restore(state);
                }
            }

            int iconSz = IconPixelSize;
            var ir = new Rectangle(b.X + (int)(14 * s), b.Y + (rowH - iconSz) / 2, iconSz, iconSz);
            var icon = it.Entry?.Icon;
            if (icon != null) g.DrawImage(icon, ir);
            else DrawGlyph(g, ir, it);

            int textX = ir.Right + (int)(12 * s);
            int hintW = selected ? (int)(180 * s) : 0;
            int textW = b.Right - textX - (int)(12 * s) - hintW;
            int titleH = titleFont.Height, subH = subFont.Height;
            int top = b.Y + (rowH - titleH - subH) / 2;
            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, it.Title, titleFont, new Rectangle(textX, top, textW, titleH), T.Text, flags);
            TextRenderer.DrawText(g, it.Subtitle, subFont, new Rectangle(textX, top + titleH, textW, subH), T.SubText, flags);

            if (selected)
            {
                string hint = it.Type == ResultItem.Kind.Calc ? "Enter to copy"
                            : it.Type == ResultItem.Kind.Timer ? (it.TimerLength > TimeSpan.Zero ? "Enter to start" : "")
                            : it.Type == ResultItem.Kind.Web || it.Type == ResultItem.Kind.Setting ? "Enter" : "Enter  ·  right-click for more";
                var hr = new Rectangle(b.Right - (int)(14 * s) - hintW, b.Y, hintW, rowH);
                TextRenderer.DrawText(g, hint, subFont, hr, T.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        void DrawGlyph(Graphics g, Rectangle r, ResultItem it)
        {
            bool app = it.Type == ResultItem.Kind.App;
            using var path = RoundedRect(r, 7 * s);
            using var fill = new SolidBrush(app ? T.Border : Color.FromArgb(45, T.Accent));
            g.FillPath(fill, path);
            bool icon = it.Type == ResultItem.Kind.Setting || it.Type == ResultItem.Kind.Calc || it.Type == ResultItem.Kind.Web || it.Type == ResultItem.Kind.Timer;
            string letter = it.Type switch
            {
                ResultItem.Kind.Run => ">",
                ResultItem.Kind.Web => "\uE774",     // globe
                ResultItem.Kind.Calc => "\uE8EF",    // calculator
                ResultItem.Kind.Timer => "\uE916",   // stopwatch
                ResultItem.Kind.Setting => "\uE713", // gear
                _ => it.Title.Length > 0 ? it.Title.Substring(0, 1).ToUpperInvariant() : "?",
            };
            iconFont ??= new Font(BarForm.GlyphFamily, 13f);
            TextRenderer.DrawText(g, letter, icon ? iconFont : glyphFont, r, app ? T.Text : T.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- window plumbing ----------

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                createdAttached = settings != null && Attached;
                if (!createdAttached) cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW (the attached frame has none)
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwm();
        }

        static readonly bool IsWin11OrLater = Environment.OSVersion.Version.Build >= 22000;

        // Windows normally blocks background apps from stealing focus; attaching to the
        // foreground thread's input queue is the standard, reliable way around that.
        void ForceForeground()
        {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                AttachThreadInput(me, fgThread, true);
                SetForegroundWindow(Handle);
                BringWindowToTop(Handle);
                AttachThreadInput(me, fgThread, false);
            }
            else SetForegroundWindow(Handle);
            Activate();
            box.Focus();
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    }

    /// <summary>Paints context menus in the current theme's colours.</summary>
    sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        readonly Theme t;
        public ThemedMenuRenderer(Theme t) : base(new Colors(t)) { this.t = t; RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? t.Text : t.SubText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = t.SubText;
            base.OnRenderArrow(e);
        }

        sealed class Colors : ProfessionalColorTable
        {
            readonly Theme t;
            public Colors(Theme t) { this.t = t; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground => t.Surface;
            public override Color ImageMarginGradientBegin => t.Surface;
            public override Color ImageMarginGradientMiddle => t.Surface;
            public override Color ImageMarginGradientEnd => t.Surface;
            public override Color MenuBorder => t.Border;
            public override Color MenuItemBorder => t.Selection;
            public override Color MenuItemSelected => t.Selection;
            public override Color MenuItemSelectedGradientBegin => t.Selection;
            public override Color MenuItemSelectedGradientEnd => t.Selection;
            public override Color MenuItemPressedGradientBegin => t.Selection;
            public override Color MenuItemPressedGradientEnd => t.Selection;
            public override Color SeparatorDark => t.Border;
            public override Color SeparatorLight => t.Surface;
        }
    }
}
