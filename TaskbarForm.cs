using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>One button on the taskbar: a pinned app, a running app, or both.</summary>
    sealed class TaskButton
    {
        public string PinKey;          // entry in Settings.TaskbarPinned, or null if not pinned
        public AppEntry Entry;         // Start-menu app, if known
        public string ExePath;         // for apps without a Start-menu entry
        public string Aumid;
        public string Name;
        public List<AppWindow> Windows = new List<AppWindow>();
        public Bitmap Icon;
    }

    sealed class TaskbarForm : BarForm
    {
        readonly AppIndex index;
        readonly WindowTracker tracker;
        readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>();
        readonly Dictionary<string, string> entryExe = new Dictionary<string, string>();
        List<TaskButton> buttons = new List<TaskButton>();
        Size lastSize;
        string lastVisual;

        // ---- animations: the active indicator glides, new apps pop in ----
        readonly Dictionary<string, float> activeLevel = new Dictionary<string, float>();
        readonly Dictionary<string, float> activeTarget = new Dictionary<string, float>();
        readonly Dictionary<string, float> appearLevel = new Dictionary<string, float>();
        readonly DateTime createdAt = DateTime.Now;

        readonly Dictionary<string, int> lastLit = new Dictionary<string, int>();

        static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        static string AnimKey(TaskButton b) => b.PinKey ?? b.ExePath ?? b.Aumid ?? b.Name ?? "";

        protected override bool AnimateStep()
        {
            bool moving = false;
            foreach (var k in activeLevel.Keys.ToList())
            {
                float v = activeLevel[k];
                moving |= Anim.Approach(ref v, activeTarget.TryGetValue(k, out var t) ? t : 0f, Anim.PerFrame(0.11f));
                activeLevel[k] = v;
            }
            foreach (var k in appearLevel.Keys.ToList())
            {
                float v = appearLevel[k];
                moving |= Anim.Approach(ref v, 1f, Anim.PerFrame(0.065f));
                appearLevel[k] = v;
            }
            return moving;
        }
        Font glyphFont;
        readonly Font letterFont = new Font("Segoe UI Semibold", 10f);
        readonly FontFamily badgeFamily = new FontFamily("Segoe UI");
        // exe path / AppUserModelID -> Start-menu app, built in the background
        Dictionary<string, AppEntry> exeMap = new Dictionary<string, AppEntry>();
        Dictionary<string, AppEntry> aumidMap = new Dictionary<string, AppEntry>();
        IReadOnlyList<AppEntry> mapsFor;
        bool buildingMaps;

        public Action ToggleLauncher;
        public Action<Rectangle> OpenStartMenu;
        /// <summary>Which windows this taskbar shows (null = all). Used for one taskbar per monitor.</summary>
        public Func<AppWindow, bool> WindowFilter;
        /// <summary>Pins were added, removed or reordered here — other taskbars should refresh.</summary>
        public event Action PinsChanged;
        public Action OpenSettings;
        public Action HideBars;
        public Action DisableTaskbar;

        readonly PreviewForm preview;
        readonly Timer hoverTimer = new Timer { Interval = 60 };

        public TaskbarForm(Settings settings, Backdrop backdrop, AppIndex index, WindowTracker tracker) : base(settings, backdrop)
        {
            this.index = index;
            this.tracker = tracker;
            Text = "WispR Taskbar";
            tracker.Changed += Rebuild;
            // a title alone only matters for apps named after their window, and for an open preview
            tracker.TitlesChanged += () =>
            {
                if (buttons.Any(b => b.ExePath == null && b.Entry == null && b.Windows.Count > 0)) Rebuild();
                else if (preview.Visible) preview.Invalidate();
            };
            preview = new PreviewForm(settings);
            preview.WindowChosen += h => { Native.Activate(h); tracker.Refresh(); };
            hoverTimer.Tick += (o, e) => HoverTick();
            _ = Handle;
        }

        int IconPx => settings.TaskbarIconSize == "Small" ? S(20) : settings.TaskbarIconSize == "Large" ? S(30) : S(24);
        int ButtonPx => IconPx + S(16);
        public int BarHeight => ButtonPx + S(8);

        int iconCacheSize;
        readonly Dictionary<string, DateTime> iconRetryAt = new Dictionary<string, DateTime>();

        public override void ApplyTheme()
        {
            glyphFont?.Dispose();
            glyphFont = new Font(GlyphFamily, IconPx * 0.62f / s);
            if (iconCacheSize != IconPx)
            {
                foreach (var b in iconCache.Values.Distinct()) if (b != null && !buttons.Any(x => x.Icon == b)) b.Dispose();
                iconCache.Clear(); iconRetryAt.Clear(); iconCacheSize = IconPx;
            }
            base.ApplyTheme();
            Rebuild();
        }

        // ---------- model ----------

        public bool IsPinned(AppEntry e) => settings.TaskbarPinned.Contains(e.Key);

        public void TogglePin(AppEntry e)
        {
            if (!settings.TaskbarPinned.Remove(e.Key)) settings.TaskbarPinned.Add(e.Key);
            settings.Save();
            Rebuild();
            PinsChanged?.Invoke();
        }

        void TogglePin(TaskButton b)
        {
            if (b.PinKey != null) settings.TaskbarPinned.Remove(b.PinKey);
            else if (b.Entry != null) settings.TaskbarPinned.Add(b.Entry.Key);
            else if (b.ExePath != null) settings.TaskbarPinned.Add("exe:" + b.ExePath);
            settings.Save();
            Rebuild();
            PinsChanged?.Invoke();
        }

        string ExeOf(AppEntry e)
        {
            if (!entryExe.TryGetValue(e.Key, out var exe))
            {
                var loc = Launcher.GetInstallLocation(e);
                exe = loc != null && File.Exists(loc) ? loc.ToLowerInvariant() : null;
                entryExe[e.Key] = exe;
            }
            return exe;
        }

        bool Matches(AppEntry e, AppWindow w)
        {
            if (w.Aumid != null && e.ParsingName != null && string.Equals(w.Aumid, e.ParsingName, StringComparison.OrdinalIgnoreCase)) return true;
            var exe = ExeOf(e);
            return exe != null && w.ExePath == exe;
        }

        void EnsureMaps(IReadOnlyList<AppEntry> entries)
        {
            if (mapsFor == entries || buildingMaps || entries.Count == 0) return;
            buildingMaps = true;
            var t = new System.Threading.Thread(() =>
            {
                var exe = new Dictionary<string, AppEntry>();
                var aumid = new Dictionary<string, AppEntry>();
                foreach (var e in entries)
                {
                    if (e.ParsingName != null && !aumid.ContainsKey(e.ParsingName.ToLowerInvariant())) aumid[e.ParsingName.ToLowerInvariant()] = e;
                    string loc = null;
                    try { loc = Launcher.GetInstallLocation(e); } catch { }
                    if (loc != null && loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var k = loc.ToLowerInvariant();
                        if (!exe.ContainsKey(k)) exe[k] = e;
                    }
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        exeMap = exe; aumidMap = aumid; mapsFor = entries; buildingMaps = false;
                        foreach (var kv in exe) entryExe[kv.Value.Key] = kv.Key;
                        Rebuild();
                    }));
                }
                catch { buildingMaps = false; }
            })
            { IsBackground = true, Name = "TaskbarAppMap" };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }

        AppEntry FindEntry(AppWindow w)
        {
            if (w.Aumid != null && aumidMap.TryGetValue(w.Aumid.ToLowerInvariant(), out var e)) return e;
            if (w.ExePath != null && exeMap.TryGetValue(w.ExePath, out e)) return e;
            return null;
        }

        public void Rebuild()
        {
            if (!IsHandleCreated) return;
            var entries = index.Entries;
            EnsureMaps(entries);
            var byKey = new Dictionary<string, AppEntry>();
            foreach (var e in entries) if (!byKey.ContainsKey(e.Key)) byKey[e.Key] = e;

            var list = new List<TaskButton>();
            foreach (var key in settings.TaskbarPinned)
            {
                if (key.StartsWith("exe:"))
                {
                    string path = key.Substring(4);
                    list.Add(new TaskButton { PinKey = key, ExePath = path.ToLowerInvariant(), Name = FriendlyName(path) });
                }
                else if (byKey.TryGetValue(key, out var e))
                    list.Add(new TaskButton { PinKey = key, Entry = e, Name = e.Name, Aumid = e.IsStoreApp ? e.ParsingName : null });
            }

            foreach (var w in tracker.Windows)
            {
                if (WindowFilter != null && !WindowFilter(w)) continue; // e.g. only windows on this monitor
                var b = list.FirstOrDefault(x =>
                    (x.Entry != null && Matches(x.Entry, w)) ||
                    (x.Entry == null && x.ExePath != null && w.ExePath == x.ExePath && (x.Aumid == null || w.Aumid == null)) ||
                    (x.Aumid != null && w.Aumid != null && string.Equals(x.Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase)));
                if (b == null)
                {
                    var entry = FindEntry(w);
                    if (entry != null)
                    {
                        // a second window of an app that's already shown via its Start-menu entry
                        var same = list.FirstOrDefault(x => x.Entry == entry);
                        if (same != null) { same.Windows.Add(w); continue; }
                    }
                    b = new TaskButton
                    {
                        Entry = entry,
                        ExePath = w.ExePath,
                        Aumid = w.Aumid,
                        Name = entry?.Name ?? FriendlyName(w.ExePath) ?? w.Title,
                    };
                    list.Add(b);
                }
                b.Windows.Add(w);
            }

            foreach (var b in list) b.Icon = IconFor(b);
            buttons = list;

            var size = Measure();
            if (size != lastSize) { lastSize = size; RequestRelayout(); }
            SyncPreview();
            var fg = tracker.Foreground;
            string visual = string.Join("|", buttons.Select(b => (b.PinKey ?? b.Entry?.Key ?? b.ExePath ?? b.Name) + ":" + b.Windows.Count + ":" +
                (b.Windows.Any(w => w.Handle == fg) ? "a" : "") + (b.Windows.Any(w => w.Flashing) ? "f" : "") + ":" + (b.Icon == null ? 0 : b.Icon.GetHashCode())));
            if (visual != lastVisual) { lastVisual = visual; Invalidate(); }
        }

        static readonly Dictionary<string, string> names = new Dictionary<string, string>();

        static string FriendlyName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (names.TryGetValue(exePath, out var n)) return n;
            n = Path.GetFileNameWithoutExtension(exePath);
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) n = desc.Trim();
            }
            catch { }
            return names[exePath] = n;
        }

        Bitmap IconFor(TaskButton b)
        {
            string key = b.Entry?.Key ?? b.Aumid ?? b.ExePath ?? b.Windows.FirstOrDefault()?.Handle.ToString();
            if (key == null) return null;
            if (iconCache.TryGetValue(key, out var bmp) &&
                (bmp != null || (iconRetryAt.TryGetValue(key, out var retry) && DateTime.UtcNow < retry))) return bmp;
            int px = Math.Max(32, IconPx);
            if (b.Entry != null) bmp = IconLoader.Load(b.Entry.IconSource, px);
            if (bmp == null && b.Aumid != null) bmp = IconLoader.Load(@"shell:AppsFolder\" + b.Aumid, px);
            if (bmp == null && b.ExePath != null && File.Exists(b.ExePath) && !b.ExePath.EndsWith("applicationframehost.exe"))
                bmp = IconLoader.Load(b.ExePath, px);
            if (bmp == null && b.Windows.Count > 0) bmp = WindowTracker.LoadIcon(b.Windows[0], px);
            iconCache[key] = bmp;
            // no icon found: don't ask Windows again on every rebuild (each try can block for a moment)
            if (bmp == null) iconRetryAt[key] = DateTime.UtcNow.AddSeconds(10); else iconRetryAt.Remove(key);
            return bmp;
        }

        // ---------- layout ----------

        public override Size Measure()
        {
            items.Clear();
            int pad = S(4), x = pad, bw = ButtonPx, top = (BarHeight - bw) / 2;

            // (the Start button has its own box in the bottom-left corner now — StartBoxForm)
            if (settings.ShowTaskView)
            {
                items.Add(new BarItem
                {
                    Bounds = new Rectangle(x, top, bw, bw),
                    Tooltip = () => "Task view",
                    Paint = (g, r, h) => DrawGlyph(g, "", glyphFont, r, T.Text),
                    Click = (btn, pt, dbl) => { if (btn == MouseButtons.Left) Native.SendCombo(Native.VK_LWIN, Native.VK_TAB); },
                });
                x += bw + S(2);
            }
            if (items.Count > 0 && buttons.Count > 0) x += S(6); // little gap after the system buttons

            // forget animation state of apps that are gone, so they pop in again when reopened
            var live = new HashSet<string>(buttons.Select(AnimKey));
            foreach (var k in appearLevel.Keys.Where(k => !live.Contains(k)).ToList()) { appearLevel.Remove(k); activeLevel.Remove(k); activeTarget.Remove(k); }

            foreach (var b in buttons)
            {
                var btn = b;
                items.Add(new BarItem
                {
                    Bounds = new Rectangle(x, top, bw, bw),
                    Tooltip = () => btn.Windows.Count > 0 ? null : btn.Name, // running apps get a live preview instead
                    Paint = (g, r, h) => PaintApp(g, r, btn),
                    Click = (mb, pt, dbl) => ClickApp(btn, mb, pt),
                    Tag = btn,
                });
                x += bw + S(2);
            }
            return new Size(Math.Max(x - S(2) + pad, BarHeight), BarHeight);
        }

        void PaintApp(Graphics g, Rectangle r, TaskButton b)
        {
            if (reordering && b.PinKey == dragKey) // the app being dragged
                using (var p = Rounded(r, S(6)))
                using (var br = new SolidBrush(Color.FromArgb(90, T.Accent)))
                using (var pen = new Pen(T.Accent, Math.Max(1f, s)))
                {
                    g.FillPath(br, p);
                    g.DrawPath(pen, p);
                }
            bool active = b.Windows.Any(w => w.Handle == tracker.Foreground);
            bool flashing = b.Windows.Any(w => w.Flashing);

            string key = AnimKey(b);
            float target = active ? 1f : 0f;
            activeTarget[key] = target;
            if (!activeLevel.TryGetValue(key, out float lv)) activeLevel[key] = lv = target;
            if (lv != target) Animate();
            if (!appearLevel.TryGetValue(key, out float ap))
            {
                // apps that show up after start pop in; the ones there at start just appear
                appearLevel[key] = ap = (DateTime.Now - createdAt).TotalSeconds > 4 ? 0f : 1f;
                if (ap < 1) Animate();
            }

            if (flashing)
                using (var p = Rounded(r, S(6)))
                using (var br = new SolidBrush(Color.FromArgb(110, T.Accent)))
                    g.FillPath(br, p);
            else if (lv > 0.01f)
                using (var p = Rounded(r, S(6)))
                using (var br = new SolidBrush(Color.FromArgb((int)(150 * lv), T.Selection)))
                    g.FillPath(br, p);

            int ip = IconPx;
            var ir = new Rectangle(r.X + (r.Width - ip) / 2, r.Y + (r.Height - ip) / 2 - S(2), ip, ip);
            var iconState = g.Save();
            if (ap < 1)
            {
                float k = (float)(0.4 + 0.6 * Anim.OutBack(ap)), cx = ir.X + ir.Width / 2f, cy = ir.Y + ir.Height / 2f;
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(k, k);
                g.TranslateTransform(-cx, -cy);
            }
            if (b.Icon != null) g.DrawImage(b.Icon, ir);
            else
            {
                using (var p = Rounded(ir, S(5))) using (var br = new SolidBrush(T.Border)) g.FillPath(br, p);
                TextRenderer.DrawText(g, string.IsNullOrEmpty(b.Name) ? "?" : b.Name.Substring(0, 1).ToUpperInvariant(), letterFont, ir, T.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            g.Restore(iconState);

            int count = b.Windows.Count;
            if (count == 1) // running indicator: one pill, wider when active
            {
                int w = (int)Math.Round(Anim.Lerp(S(6), S(16), lv)), h = S(3);
                var ind = new Rectangle(r.X + (r.Width - w) / 2, r.Bottom - h - S(2), w, h);
                using var p = Rounded(ind, h / 2f);
                using var br = new SolidBrush(Mix(T.SubText, T.Accent, lv));
                g.FillPath(br, p);
            }
            else if (count > 1)
            {
                // Several windows: one dot per window (up to 3), the active one drawn as a short pill…
                int dots = Math.Min(count, 3), h = S(3), dot = S(4), gap = S(3);
                int activeIdx = b.Windows.FindIndex(w => w.Handle == tracker.Foreground);
                int[] widths = new int[dots];
                int lit = activeIdx >= 0 ? Math.Min(activeIdx, dots - 1) : (lv > 0.01f ? lastLit.TryGetValue(key, out var ll) ? Math.Min(ll, dots - 1) : 0 : -1);
                if (activeIdx >= 0) lastLit[key] = activeIdx;
                for (int i = 0; i < dots; i++) widths[i] = i == lit ? (int)Math.Round(Anim.Lerp(dot, S(10), lv)) : dot;
                int total = widths.Sum() + gap * (dots - 1), x = r.X + (r.Width - total) / 2;
                for (int i = 0; i < dots; i++)
                {
                    var ind = new Rectangle(x, r.Bottom - h - S(2), widths[i], h);
                    using (var p = Rounded(ind, h / 2f))
                    using (var br = new SolidBrush(i == lit ? Mix(T.SubText, T.Accent, lv) : T.SubText))
                        g.FillPath(br, p);
                    x += widths[i] + gap;
                }

                // …and a small count badge on the icon's corner: a circle (a pill only for "9+").
                string n = count > 9 ? "9+" : count.ToString();
                float bh = S(15);
                using (var text = new GraphicsPath())
                {
                    // Draw the number as a shape so it can be centred exactly (font metrics add uneven padding).
                    text.AddString(n, badgeFamily, (int)FontStyle.Bold, bh * 0.66f, PointF.Empty, StringFormat.GenericTypographic);
                    var tb = text.GetBounds();
                    float bw = Math.Max(bh, tb.Width + S(7));
                    var badge = new RectangleF(ir.Right - bw / 2 - S(1), ir.Y - S(4), bw, bh);
                    using (var br = new SolidBrush(T.Accent))
                    using (var edge = new Pen(T.Background, Math.Max(1f, 1.5f * s)))
                    using (var shape = new GraphicsPath())
                    {
                        if (bw <= bh) shape.AddEllipse(badge);
                        else
                        {
                            shape.AddArc(badge.X, badge.Y, bh, bh, 90, 180);
                            shape.AddArc(badge.Right - bh, badge.Y, bh, bh, 270, 180);
                            shape.CloseFigure();
                        }
                        g.FillPath(br, shape);
                        g.DrawPath(edge, shape);
                    }
                    using (var m = new Matrix())
                    {
                        m.Translate(badge.X + (badge.Width - tb.Width) / 2 - tb.X, badge.Y + (badge.Height - tb.Height) / 2 - tb.Y);
                        text.Transform(m);
                    }
                    var onAccent = T.Accent.GetBrightness() > 0.6f ? Color.FromArgb(20, 20, 20) : Color.White;
                    using (var tbr = new SolidBrush(onAccent)) g.FillPath(tbr, text);
                }
            }
        }

        // ---------- actions ----------

        void ClickApp(TaskButton b, MouseButtons mb, Point pt)
        {
            try
            {
                if (mb == MouseButtons.Right) { preview.HidePreview(); ShowAppMenu(b, pt); return; }
                if (mb == MouseButtons.Middle || b.Windows.Count == 0) { preview.HidePreview(); Launch(b); return; }
                if (b.Windows.Count == 1)
                {
                    preview.HidePreview();
                    var w = b.Windows[0];
                    if (w.Handle == Native.GetForegroundWindow() && !Native.IsIconic(w.Handle)) Native.Minimize(w.Handle);
                    else Native.Activate(w.Handle);
                }
                else if (preview.Button == null) ShowPreview(b); // several windows: pick one from the previews
                BeginInvoke((Action)(() => tracker.Refresh()));
            }
            catch (Exception ex) { ShowError(b.Name, ex); }
        }

        void Launch(TaskButton b)
        {
            if (b.Entry != null) Launcher.LaunchApp(b.Entry, false);
            else if (b.Aumid != null) Launcher.Start(@"shell:AppsFolder\" + b.Aumid, null, null);
            else if (b.ExePath != null) Launcher.Start(b.ExePath, null, null);
        }

        static string Short(string t) => t.Length > 60 ? t.Substring(0, 57) + "…" : t;

        void ShowAppMenu(TaskButton b, Point pt)
        {
            var m = NewMenu();
            AddItem(m, b.Name ?? "App", null, bold: true);
            if (b.Windows.Count > 1)
            {
                m.AddSeparator();
                foreach (var w in b.Windows)
                {
                    var win = w;
                    AddItem(m, Short(win.Title), () => Native.Activate(win.Handle));
                }
            }
            m.AddSeparator();
            AddItem(m, b.Windows.Count > 0 ? "New window" : "Open", () => Launch(b));
            if (b.Entry != null && Launcher.CanElevate(b.Entry)) AddItem(m, "Run as administrator", () => Launcher.LaunchApp(b.Entry, true));
            bool canPin = b.PinKey != null || b.Entry != null || b.ExePath != null;
            AddItem(m, b.PinKey != null ? "Unpin from taskbar" : "Pin to taskbar", () => TogglePin(b), canPin);
            string folder = b.Entry != null ? Launcher.GetInstallLocation(b.Entry) : b.ExePath != null && File.Exists(b.ExePath) ? b.ExePath : null;
            AddItem(m, "Open file location", folder == null ? (Action)null : () => Launcher.ShowInExplorer(folder));

            if (b.Windows.Count == 1)
            {
                var h = b.Windows[0].Handle;
                m.AddSeparator();
                if (Native.IsIconic(h) || IsZoomed(h)) AddItem(m, "Restore", () => { Native.ShowWindowAsync(h, 9); Native.SetForegroundWindow(h); });
                if (!Native.IsIconic(h)) AddItem(m, "Minimize", () => Native.Minimize(h));
                if (!IsZoomed(h)) AddItem(m, "Maximize", () => { Native.ShowWindowAsync(h, 3); Native.SetForegroundWindow(h); });
            }
            if (b.Windows.Count > 0)
            {
                m.AddSeparator();
                // End task kills the process; not offered for Store apps (their frame host is shared by all of them).
                bool canKill = !b.Windows.Any(w => w.ExePath != null &&
                    (w.ExePath.EndsWith("applicationframehost.exe") || w.ExePath.EndsWith("explorer.exe")));
                AddItem(m, "End task", canKill ? (Action)(() => EndTask(b)) : null);
                AddItem(m, b.Windows.Count > 1 ? "Close all windows" : "Close window", () => { foreach (var w in b.Windows) Native.Close(w.Handle); });
            }
            PopUp(m, pt);
        }

        static void EndTask(TaskButton b)
        {
            foreach (var pid in b.Windows.Select(w => w.Pid).Distinct())
                try { using var p = Process.GetProcessById((int)pid); p.Kill(); } catch { }
        }

        protected override void OnBackgroundRightClick(Point screen) => ShowGeneralMenu(screen);
        public void ShowBackgroundMenu(Point screen) => ShowGeneralMenu(screen);

        void ShowGeneralMenu(Point pt)
        {
            preview.HidePreview();
            var m = NewMenu();
            AddItem(m, "WispR settings…", () => OpenSettings?.Invoke());
            AddItem(m, "Task Manager", () => Launcher.Start("taskmgr.exe", null, null));
            AddItem(m, "Show desktop", () => Native.WinPlus('D'));
            m.AddSeparator();
            AddItem(m, "Hide bars (double-tap Windows key)", () => HideBars?.Invoke());
            AddItem(m, "Turn off WispR taskbar", () => DisableTaskbar?.Invoke());
            PopUp(m, pt);
        }

        // ---------- hover previews ----------

        TaskButton hoverButton;
        Rectangle hoverRect;
        bool previewSuppressed; // set by a click; cleared when the mouse moves to another button

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Pressing any button (especially right-click) cancels a preview that's about to appear.
            previewSuppressed = true;
            preview.HidePreview();
            base.OnMouseDown(e);

            // A pinned app may be about to be dragged to a new spot.
            var hit = items.LastOrDefault(i => i.Bounds.Contains(e.Location));
            dragKey = e.Button == MouseButtons.Left && hit?.Tag is TaskButton b ? b.PinKey : null;
            dragFrom = e.Location;
        }

        // ---------- drag to reorder pinned apps ----------

        string dragKey;     // pin key of the app being dragged
        Point dragFrom;
        bool reordering;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragKey != null && (e.Button & MouseButtons.Left) != 0)
            {
                var ds = SystemInformation.DragSize;
                if (!reordering && (Math.Abs(e.X - dragFrom.X) > ds.Width || Math.Abs(e.Y - dragFrom.Y) > ds.Height))
                {
                    reordering = true;
                    Capture = true;
                    Cursor = Cursors.SizeWE;
                    preview.HidePreview();
                }
                if (reordering) { DragTo(e.X); return; }
            }
            base.OnMouseMove(e);
        }

        /// <summary>Moves the dragged app to the slot under the mouse; the others slide aside.</summary>
        void DragTo(int x)
        {
            var pins = settings.TaskbarPinned;
            int cur = pins.IndexOf(dragKey);
            if (cur < 0) return;
            int target = 0;
            foreach (var it in items)
                if (it.Tag is TaskButton b && b.PinKey != null && b.PinKey != dragKey && it.Bounds.X + it.Bounds.Width / 2 < x)
                    target++;
            target = Math.Max(0, Math.Min(pins.Count - 1, target));
            if (target == cur) return;
            pins.RemoveAt(cur);
            pins.Insert(target, dragKey);
            Rebuild();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (reordering)
            {
                reordering = false;
                dragKey = null;
                Capture = false;
                Cursor = Cursors.Default;
                settings.Save();
                PinsChanged?.Invoke();
                CancelPress();
                return; // a drag isn't a click
            }
            dragKey = null;
            base.OnMouseUp(e);
        }
        DateTime hoverSince, outsideSince = DateTime.MaxValue;

        protected override void OnHoverChanged(BarItem item)
        {
            if (item?.Tag is TaskButton b && b.Windows.Count > 0)
            {
                if (hoverButton != b)
                {
                    previewSuppressed = false;
                    hoverButton = b;
                    hoverRect = RectangleToScreen(item.Bounds);
                    hoverSince = DateTime.Now;
                    if (preview.Visible && !BarMenu.IsOpen) ShowPreview(b); // already open: follow the mouse right away
                }
            }
            else { hoverButton = null; previewSuppressed = false; }
            hoverTimer.Start();
        }

        void HoverTick()
        {
            var cur = Cursor.Position;
            bool blocked = previewSuppressed || BarMenu.IsOpen;
            if (blocked && preview.Visible && BarMenu.IsOpen) preview.HidePreview();
            if (!blocked && hoverButton != null && !preview.Visible && (DateTime.Now - hoverSince).TotalMilliseconds >= 500)
                ShowPreview(hoverButton);

            bool keep = (preview.Visible && preview.Bounds.Contains(cur)) || (hoverButton != null && hoverRect.Contains(cur));
            if (keep) outsideSince = DateTime.MaxValue;
            else
            {
                if (outsideSince == DateTime.MaxValue) outsideSince = DateTime.Now;
                if ((DateTime.Now - outsideSince).TotalMilliseconds > 350)
                {
                    preview.HidePreview();
                    hoverButton = null;
                }
            }
            if (!preview.Visible && hoverButton == null) hoverTimer.Stop();
        }

        void ShowPreview(TaskButton b)
        {
            var item = items.FirstOrDefault(i => i.Tag == b);
            if (item != null) hoverRect = RectangleToScreen(item.Bounds);
            preview.ShowFor(b, hoverRect, settings.TaskbarEdge != "Top");
        }

        /// <summary>Keeps an open preview in step with windows opening/closing.</summary>
        void SyncPreview()
        {
            var old = preview.Button;
            if (old == null || !preview.Visible) return;
            var nb = buttons.FirstOrDefault(x => x.Windows.Any(w => old.Windows.Any(o => o.Handle == w.Handle)));
            if (nb == null || nb.Windows.Count == 0) { preview.HidePreview(); hoverButton = null; return; }
            if (nb.Windows.Count != old.Windows.Count) ShowPreview(nb);
            if (hoverButton == old) hoverButton = nb;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hoverTimer.Dispose(); preview.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
