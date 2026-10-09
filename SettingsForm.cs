using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Settings window, styled like the rest of WispR: a sidebar with sections, and cards of settings
    /// that use the current theme (and follow it live). Every change applies at once and is saved.
    /// </summary>
    sealed class SettingsForm : Form
    {
        readonly Settings st;
        readonly float s;
        bool loading;
        Theme T => st.Theme;

        // controls other code reads or updates
        readonly Dictionary<string, ColorChip> swatches = new Dictionary<string, ColorChip>();
        readonly List<PresetTile> presetTiles = new List<PresetTile>();
        Label themeNote, imagePath;
        ToggleSwitch useWallpaper, matchColors;
        ComboBox paletteMode;
        Button browse, clearImage;

        readonly List<Action> loaders = new List<Action>();          // refresh controls from the settings
        readonly List<Action<Theme>> stylers = new List<Action<Theme>>(); // recolour controls for the theme

        // layout
        Panel sidebar, content;
        readonly List<(NavButton nav, Panel page)> pages = new List<(NavButton, Panel)>();
        int contentW;
        readonly Font titleFont, sectionFont, rowFont, descFont, uiFont;

        public event Action PreviewRequested;

        public SettingsForm(Settings settings)
        {
            st = settings;
            Text = "WispR Settings";
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            uiFont = new Font("Segoe UI", 9.5f);
            titleFont = new Font("Segoe UI Semibold", 20f);
            sectionFont = new Font("Segoe UI Semibold", 10f);
            rowFont = new Font("Segoe UI", 10f);
            descFont = new Font("Segoe UI", 8.75f);
            Font = uiFont;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(960), S(680));
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildChrome();
            AddPage("", "Appearance", BuildAppearance);
            AddPage("", "Background", BuildBackground);
            AddPage("", "Wallpapers", BuildWallpapers);
            AddPage("", "Taskbar", BuildTaskbar);
            AddPage("", "On the bars", BuildBarItems);
            AddPage("\uE70D", "Drop-down", BuildDropDown);
            AddPage("", "General", BuildGeneral);
            Select(pages[0]);

            LoadValues();
            Restyle();
            st.ThemeRecomputed += OnThemeRecomputed;
            st.Changed += OnSettingsChanged;
        }

        int S(float px) => (int)Math.Round(px * s);

        // ---------- frame: sidebar + scrolling content ----------

        void BuildChrome()
        {
            content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(36), S(28), S(36), S(28)) };
            sidebar = new Panel { Dock = DockStyle.Left, Width = S(230), Padding = new Padding(S(12), S(18), S(12), S(16)) };
            Controls.Add(content);
            Controls.Add(sidebar);
            contentW = ClientSize.Width - sidebar.Width - content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;

            var brand = new Label { Text = "WispR", Font = new Font("Segoe UI Semibold", 13f), AutoSize = true, Location = new Point(S(16), S(14)) };
            var sub = new Label { Text = "Settings", Font = descFont, AutoSize = true, Location = new Point(S(17), S(40)) };
            sidebar.Controls.Add(brand);
            sidebar.Controls.Add(sub);
            stylers.Add(t => { brand.ForeColor = t.Text; sub.ForeColor = t.SubText; });

            // bottom of the sidebar: preview + reset
            var preview = AccentButton("Preview launcher", () => PreviewRequested?.Invoke());
            var reset = FlatButton("Reset appearance", () => { st.ResetAppearance(); LoadValues(); Changed(); });
            preview.Width = reset.Width = sidebar.Width - S(24);
            preview.Location = new Point(S(12), ClientSize.Height - S(16) - preview.Height * 2 - S(8));
            reset.Location = new Point(S(12), ClientSize.Height - S(16) - reset.Height);
            preview.Anchor = reset.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            sidebar.Controls.Add(preview);
            sidebar.Controls.Add(reset);

            var closeKey = new Button { Size = Size.Empty, TabStop = false };
            closeKey.Click += (o, e) => Close();
            Controls.Add(closeKey);
            CancelButton = closeKey; // Esc closes
        }

        void AddPage(string glyph, string title, Action<Panel> build)
        {
            var nav = new NavButton(() => T, s, glyph, title)
            {
                Width = sidebar.Width - S(24),
                Location = new Point(S(12), S(76) + pages.Count * S(42)),
            };
            var page = new Panel { Location = new Point(content.Padding.Left, content.Padding.Top), Width = contentW, Visible = false };
            var head = new Label { Text = title, Font = titleFont, AutoSize = true, Location = new Point(0, 0) };
            page.Controls.Add(head);
            stylers.Add(t => head.ForeColor = t.Text);
            build(page);
            // stack everything in the page top to bottom
            int y = 0;
            foreach (Control c in page.Controls)
            {
                c.Top = y;
                y = c.Bottom + (c is Label l && l.Font == sectionFont ? S(8) : c == head ? S(18) : S(14));
            }
            page.Height = y + S(12);
            content.Controls.Add(page);
            sidebar.Controls.Add(nav);
            var entry = (nav, page);
            nav.Click += (o, e) => Select(entry);
            pages.Add(entry);
        }

        void Select((NavButton nav, Panel page) entry)
        {
            content.SuspendLayout();
            foreach (var p in pages)
            {
                p.nav.Selected = p.nav == entry.nav;
                p.page.Visible = p.page == entry.page;
            }
            content.AutoScrollPosition = Point.Empty;
            entry.page.Location = new Point(content.Padding.Left, content.Padding.Top);
            content.ResumeLayout();
            // a soft entrance for the page
            var page = entry.page;
            int finalLeft = content.Padding.Left;
            Anim.Run(content, 160, e => { page.Left = finalLeft + (int)Math.Round(S(14) * (1 - e)); });
        }

        // ---------- building blocks ----------

        Label Section(Panel page, string text)
        {
            var l = new Label { Text = text, Font = sectionFont, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false };
            stylers.Add(t => l.ForeColor = t.SubText);
            page.Controls.Add(l);
            return l;
        }

        /// <summary>A card of rows (each row: title, optional description, and the control on the right).</summary>
        Card AddCard(Panel page, params Control[] rows)
        {
            var card = new Card(() => T, s) { Width = contentW };
            int y = card.Padding.Top, inner = contentW - card.Padding.Horizontal;
            foreach (var r in rows)
            {
                r.Location = new Point(card.Padding.Left, y);
                r.Width = inner;
                if (r is RowPanel rp) rp.Arrange();
                y += r.Height + 1; // 1 px for the hairline between rows
                card.Controls.Add(r);
            }
            card.Height = y + card.Padding.Bottom;
            page.Controls.Add(card);
            return card;
        }

        sealed class RowPanel : Panel
        {
            public Label Title, Desc;
            public Control Right;
            public float S;
            public void Arrange()
            {
                int pad = (int)(14 * S), gap = (int)(16 * S);
                int rightW = Right?.Width ?? 0;
                int textW = Width - rightW - gap;
                // sized explicitly so long descriptions wrap onto more lines instead of being cut off
                Title.AutoSize = false;
                Title.Size = new Size(textW, TextRenderer.MeasureText(Title.Text, Title.Font, new Size(textW, 0), TextFormatFlags.WordBreak).Height);
                if (Desc != null)
                {
                    Desc.AutoSize = false;
                    Desc.Size = new Size(textW, TextRenderer.MeasureText(Desc.Text, Desc.Font, new Size(textW, 0), TextFormatFlags.WordBreak).Height);
                }
                int textH = Title.Height + (Desc != null ? Desc.Height + (int)(2 * S) : 0);
                Height = Math.Max(textH, Right?.Height ?? 0) + pad * 2;
                Title.Location = new Point(0, (Height - textH) / 2);
                if (Desc != null) Desc.Location = new Point(0, Title.Bottom + (int)(2 * S));
                if (Right != null) Right.Location = new Point(Width - Right.Width, (Height - Right.Height) / 2);
            }
        }

        Control Row(string title, string desc, Control right)
        {
            var row = new RowPanel { S = s };
            row.Title = new Label { Text = title, Font = rowFont, AutoSize = true, UseMnemonic = false };
            row.Controls.Add(row.Title);
            if (!string.IsNullOrEmpty(desc))
            {
                row.Desc = new Label { Text = desc, Font = descFont, AutoSize = true, UseMnemonic = false };
                row.Controls.Add(row.Desc);
            }
            if (right != null) { row.Right = right; row.Controls.Add(right); }
            stylers.Add(t =>
            {
                row.BackColor = t.Surface;
                row.Title.ForeColor = t.Text;
                if (row.Desc != null) row.Desc.ForeColor = t.SubText;
            });
            return row;
        }

        ToggleSwitch Toggle(Func<bool> get, Action<bool> set)
        {
            var t = new ToggleSwitch(() => T, s);
            t.CheckedChanged += (o, e) => { if (!loading) { set(t.Checked); Changed(); } };
            loaders.Add(() => t.Checked = get());
            return t;
        }

        Control ToggleRow(string title, string desc, Func<bool> get, Action<bool> set) => Row(title, desc, Toggle(get, set));

        ComboBox Combo(int width)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = S(width),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(24), Font = uiFont,
            };
            c.DrawItem += (o, e) =>
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(hot ? T.Selection : InputBack(T))) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, c.Items[e.Index].ToString(), uiFont,
                    new Rectangle(e.Bounds.X + S(6), e.Bounds.Y, e.Bounds.Width - S(8), e.Bounds.Height), T.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            stylers.Add(t => { c.BackColor = InputBack(t); c.ForeColor = t.Text; c.Invalidate(); });
            return c;
        }

        ComboBox Choice(string[] values, string[] labels, Func<string> get, Action<string> set, int width = 200)
        {
            var c = Combo(width);
            c.Items.AddRange(labels.Cast<object>().ToArray());
            c.SelectedIndexChanged += (o, e) => { if (!loading && c.SelectedIndex >= 0) { set(values[c.SelectedIndex]); Changed(); } };
            loaders.Add(() => c.SelectedIndex = Math.Max(0, Array.IndexOf(values, get())));
            return c;
        }

        /// <summary>A slider with its value shown next to it; moves in <paramref name="step"/>s.</summary>
        Control Slider(int min, int max, int step, Func<int> get, Action<int> set, Func<int, string> format, int width = 180)
        {
            int steps = (max - min) / step;
            var slider = new FlatSlider(() => T, s, 0, steps) { Width = S(width) };
            var value = new Label { AutoSize = false, Width = S(64), Height = slider.Height, TextAlign = ContentAlignment.MiddleRight, Font = uiFont };
            var box = new Panel { Width = slider.Width + value.Width + S(6), Height = slider.Height };
            slider.Location = new Point(0, 0);
            value.Location = new Point(slider.Right + S(6), 0);
            box.Controls.Add(slider);
            box.Controls.Add(value);
            slider.ValueChanged += (o, e) =>
            {
                int v = min + slider.Value * step;
                value.Text = format(v);
                if (!loading) { set(v); Changed(); }
            };
            loaders.Add(() =>
            {
                int v = Math.Max(min, Math.Min(max, get()));
                slider.Value = (v - min) / step;
                value.Text = format(min + slider.Value * step);
            });
            stylers.Add(t => { box.BackColor = t.Surface; value.ForeColor = t.SubText; slider.Invalidate(); });
            return box;
        }

        Button FlatButton(string text, Action click)
        {
            // fixed size from the text (auto-size would change it after the row is laid out)
            int w = Math.Max(S(84), TextRenderer.MeasureText(text, uiFont).Width + S(28));
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Size = new Size(w, S(32)), Cursor = Cursors.Hand, Font = uiFont };
            b.Click += (o, e) => click();
            stylers.Add(t =>
            {
                b.BackColor = InputBack(t); b.ForeColor = t.Text;
                b.FlatAppearance.BorderColor = Ui.Mix(t.Border, t.Text, 0.15f);
                b.FlatAppearance.MouseOverBackColor = t.Selection;
                b.FlatAppearance.MouseDownBackColor = Ui.Mix(t.Selection, t.Accent, 0.25f);
            });
            return b;
        }

        Button AccentButton(string text, Action click)
        {
            var b = FlatButton(text, click);
            stylers.Add(t =>
            {
                b.BackColor = t.Accent; b.ForeColor = Ui.OnAccent(t.Accent);
                b.FlatAppearance.BorderColor = t.Accent;
                b.FlatAppearance.MouseOverBackColor = Ui.Mix(t.Accent, Color.White, 0.12f);
                b.FlatAppearance.MouseDownBackColor = Ui.Mix(t.Accent, Color.Black, 0.12f);
            });
            return b;
        }

        static Color InputBack(Theme t) => Ui.Mix(t.Surface, t.Background, 0.55f);

        /// <summary>Several controls side by side (right-aligned in a row).</summary>
        Control Group(params Control[] controls)
        {
            var p = new Panel();
            int x = 0, h = controls.Max(c => c.Height);
            foreach (var c in controls) { c.Location = new Point(x, (h - c.Height) / 2); x = c.Right + S(8); p.Controls.Add(c); }
            p.Size = new Size(x - S(8), h);
            stylers.Add(t => p.BackColor = t.Surface);
            return p;
        }

        Label PathLabel(int width)
        {
            var l = new Label { AutoSize = false, AutoEllipsis = true, Width = S(width), Height = S(30), TextAlign = ContentAlignment.MiddleLeft, Font = descFont };
            stylers.Add(t => { l.BackColor = t.Surface; l.ForeColor = t.SubText; });
            return l;
        }

        // ---------- pages ----------

        void BuildAppearance(Panel page)
        {
            Section(page, "Theme");
            // preset tiles: a mini preview of each theme
            var tiles = new FlowLayoutPanel { Width = contentW - S(36), WrapContents = true, AutoSize = false, Margin = Padding.Empty };
            foreach (var p in Theme.Presets)
            {
                var tile = new PresetTile(p, () => T, s);
                tile.Click += (o, e) =>
                {
                    st.Theme = tile.Preset.Clone();
                    st.MatchImageColors = false;
                    loading = true; matchColors.Checked = false; loading = false;
                    Changed();
                };
                presetTiles.Add(tile);
                tiles.Controls.Add(tile);
            }
            int perRow = Math.Max(1, tiles.Width / (presetTiles[0].Width + S(6)));
            tiles.Height = (int)Math.Ceiling(presetTiles.Count / (double)perRow) * (presetTiles[0].Height + S(6));
            stylers.Add(t => tiles.BackColor = t.Surface);
            themeNote = new Label { AutoSize = true, Font = descFont };
            stylers.Add(t => { themeNote.BackColor = t.Surface; themeNote.ForeColor = t.SubText; });

            var tileRow = new Panel { Height = tiles.Height + themeNote.PreferredHeight + S(30) };
            tiles.Location = new Point(0, S(14));
            themeNote.Location = new Point(0, tiles.Bottom + S(4));
            tileRow.Controls.Add(tiles);
            tileRow.Controls.Add(themeNote);
            stylers.Add(t => tileRow.BackColor = t.Surface);

            // the individual colours
            var chips = new FlowLayoutPanel { Width = contentW - S(36), WrapContents = true, Margin = Padding.Empty };
            foreach (var (key, label) in Theme.ColorSlots)
            {
                var chip = new ColorChip(() => T, s, label);
                chip.Click += (o, e) => PickColor(key);
                swatches[key] = chip;
                chips.Controls.Add(chip);
            }
            chips.Height = chips.Controls[0].Height + S(6);
            stylers.Add(t => chips.BackColor = t.Surface);
            var chipRow = new Panel { Height = chips.Height + S(46) };
            var chipTitle = new Label { Text = "Colours", Font = rowFont, AutoSize = true, Location = new Point(0, S(14)) };
            var chipDesc = new Label { Text = "Click a colour to change it", Font = descFont, AutoSize = true };
            chipDesc.Location = new Point(chipTitle.Right + S(10), S(17));
            chips.Location = new Point(0, S(40));
            chipRow.Controls.AddRange(new Control[] { chipTitle, chipDesc, chips });
            stylers.Add(t => { chipRow.BackColor = t.Surface; chipTitle.ForeColor = t.Text; chipDesc.ForeColor = t.SubText; chipTitle.BackColor = chipDesc.BackColor = t.Surface; });
            AddCard(page, tileRow, chipRow);

            Section(page, "Window");
            AddCard(page,
                Row("Opacity", "How see-through the launcher and bars are", Slider(60, 100, 1, () => st.Opacity, v => st.Opacity = v, v => v + " %")),
                Row("Launcher width", null, Slider(480, 1000, 20, () => st.Width, v => st.Width = v, v => v + " px")));

            Section(page, "Shape & motion");
            AddCard(page,
                Row("Corner roundness", "The launcher, popups, drop-down, timers and screen frame",
                    Slider(0, 28, 1, () => st.CornerRadius, v => st.CornerRadius = v, v => v == 0 ? "Square" : v + " px", 180)),
                Row("Animations", "Off makes everything appear at once (lightest)",
                    Choice(new[] { "Off", "Fast", "Normal", "Relaxed" }, new[] { "Off", "Fast", "Normal", "Relaxed" }, () => st.Animations, v => st.Animations = v, 160)));
        }

        void BuildBackground(Panel page)
        {
            Section(page, "Picture");
            useWallpaper = Toggle(() => st.UseWallpaper, v => st.UseWallpaper = v);
            useWallpaper.CheckedChanged += (o, e) => UpdateEnabled();
            imagePath = PathLabel(170);
            browse = FlatButton("Browse…", () =>
            {
                using var dlg = new OpenFileDialog { Title = "Choose a background image", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
                if (File.Exists(st.BackgroundImage)) dlg.InitialDirectory = Path.GetDirectoryName(st.BackgroundImage);
                if (dlg.ShowDialog(this) == DialogResult.OK) { st.BackgroundImage = dlg.FileName; ShowImagePath(); Changed(); }
            });
            clearImage = FlatButton("Remove", () => { st.BackgroundImage = ""; ShowImagePath(); Changed(); });
            loaders.Add(ShowImagePath);
            AddCard(page,
                Row("Use my desktop wallpaper", "Follows your wallpaper — including Wallpaper Engine — when it changes", useWallpaper),
                Row("Custom picture", "When the wallpaper is off", Group(imagePath, browse, clearImage)),
                ToggleRow("Show the picture", "Off: keep a solid background and only take the picture's colours", () => st.ShowImage, v => st.ShowImage = v));

            Section(page, "Look");
            AddCard(page,
                Row("Tint", "Darkens (or lightens) the picture with the background colour", Slider(0, 90, 1, () => st.ImageDim, v => st.ImageDim = v, v => v + " %")),
                ToggleRow("Blur", "Frosted glass — keeps text readable", () => st.ImageBlur, v => st.ImageBlur = v));

            Section(page, "Colours from the picture");
            matchColors = Toggle(() => st.MatchImageColors, v => st.MatchImageColors = v);
            matchColors.CheckedChanged += (o, e) =>
            {
                UpdateEnabled();
                if (!loading && matchColors.Checked && !HasImage())
                    MessageBox.Show(this, "Choose a picture or turn on \"Use my desktop wallpaper\" first.", "WispR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            paletteMode = Combo(140);
            paletteMode.Items.AddRange(Settings.PaletteModes.Cast<object>().ToArray());
            paletteMode.SelectedIndexChanged += (o, e) => { if (!loading) { st.PaletteMode = (string)paletteMode.SelectedItem; Changed(); } };
            loaders.Add(() => paletteMode.SelectedItem = st.PaletteMode);
            AddCard(page,
                Row("Match theme colours", "Builds the theme from the picture's colours", matchColors),
                Row("Palette", "Light, dark, or whatever suits the picture", paletteMode));
        }

        void ShowImagePath() => imagePath.Text = string.IsNullOrEmpty(st.BackgroundImage) ? "No picture chosen" : Path.GetFileName(st.BackgroundImage);

        void BuildWallpapers(Panel page)
        {
            Section(page, "Wallpaper picker");
            var folder = PathLabel(190);
            void ShowFolder() => folder.Text = st.WallpaperFolder.Length > 0 ? st.WallpaperFolder : "Automatic (" + WallpaperPicker.DefaultFolderLabel() + ")";
            loaders.Add(ShowFolder);
            var pick = FlatButton("Browse…", () =>
            {
                using var dlg = new FolderBrowserDialog { Description = "Folder with your wallpapers (shown when you type \"wallpaper\" in the launcher)" };
                if (Directory.Exists(st.WallpaperFolder)) dlg.SelectedPath = st.WallpaperFolder;
                if (dlg.ShowDialog(this) == DialogResult.OK) { st.WallpaperFolder = dlg.SelectedPath; ShowFolder(); Changed(); }
            });
            var auto = FlatButton("Automatic", () => { st.WallpaperFolder = ""; ShowFolder(); Changed(); });
            AddCard(page,
                Row("Show", "What the carousel lists when you type \"wallpaper\"",
                    Choice(new[] { "Favorites", "Engine", "All" },
                        new[] { "Favourites (♥)", "All Wallpaper Engine wallpapers", "Wallpaper Engine + pictures" },
                        () => st.WallpaperSource, v => st.WallpaperSource = v, 250)),
                Row("Pictures folder", "Your own wallpaper pictures", Group(folder, pick, auto)));
            var tip = new Label
            {
                Text = "Tip: type \"wallpaper all\" to see everything, then right-click a wallpaper (or Ctrl+F) to ♥ it.",
                Font = descFont, AutoSize = true, MaximumSize = new Size(contentW, 0),
            };
            stylers.Add(t => tip.ForeColor = t.SubText);
            page.Controls.Add(tip);
        }

        void BuildTaskbar(Panel page)
        {
            Section(page, "Taskbar");
            AddCard(page,
                ToggleRow("Use the WispR taskbar", "Replaces the Windows taskbar (it comes back when WispR exits)", () => st.TaskbarEnabled, v => st.TaskbarEnabled = v),
                Row("Screen edge", null, Choice(new[] { "Bottom", "Top" }, new[] { "Bottom", "Top" }, () => st.TaskbarEdge, v => st.TaskbarEdge = v, 160)),
                Row("Apps", null, Choice(new[] { "Center", "Left" }, new[] { "Centered", "Left" }, () => st.TaskbarAlign, v => st.TaskbarAlign = v, 160)),
                Row("System box", "Tray, network, volume, clock", Choice(new[] { "Right", "Left", "Beside" }, new[] { "Right corner", "Left corner", "Next to the apps" }, () => st.SystemBoxPlace, v => st.SystemBoxPlace = v, 160)),
                Row("Icon size", null, Choice(new[] { "Small", "Medium", "Large" }, new[] { "Small", "Medium", "Large" }, () => st.TaskbarIconSize, v => st.TaskbarIconSize = v, 160)),
                Row("Gap from the screen edge", null, Slider(0, 40, 2, () => st.EdgeGap, v => st.EdgeGap = v, v => v + " px", 180)));

            Section(page, "Screen frame");
            AddCard(page,
                ToggleRow("Frame around the screen", "A border all the way around the screen; the bars sit in it and the launcher grows out of it",
                    () => st.ScreenFrame, v => st.ScreenFrame = v),
                Row("Frame width", "On the three sides without bars", Slider(2, 40, 1, () => st.FrameThickness, v => st.FrameThickness = v, v => v + " px", 180)));

            Section(page, "Windows");
            AddCard(page,
                ToggleRow("Keep maximized windows clear of the bars", null, () => st.ReserveSpace, v => st.ReserveSpace = v),
                ToggleRow("Fill the bar area while a window is maximized", "A solid band behind the bars (not needed with the screen frame)", () => st.FillWhenMaximized, v => st.FillWhenMaximized = v));

            Section(page, "Multiple monitors");
            AddCard(page,
                ToggleRow("Show the bars on all monitors", null, () => st.TaskbarAllMonitors, v => st.TaskbarAllMonitors = v),
                ToggleRow("Only show each monitor's own windows", null, () => st.TaskbarAppsOnOwnMonitor, v => st.TaskbarAppsOnOwnMonitor = v));
        }

        void BuildBarItems(Panel page)
        {
            Section(page, "Boxes");
            AddCard(page,
                ToggleRow("Start & power", "Their own box in the bottom-left corner", () => st.ShowStartButton, v => st.ShowStartButton = v),
                ToggleRow("Task view button", null, () => st.ShowTaskView, v => st.ShowTaskView = v));

            Section(page, "System box");
            AddCard(page,
                ToggleRow("Tray icons", null, () => st.ShowTray, v => st.ShowTray = v),
                ToggleRow("Tray menus in WispR's style", "Standard Windows tray menus grow out of the bar too (apps with their own menu design keep it)",
                    () => st.ThemedTrayMenus, v => st.ThemedTrayMenus = v),
                ToggleRow("Keyboard layout", null, () => st.ShowKeyboardLayout, v => st.ShowKeyboardLayout = v),
                ToggleRow("Network", null, () => st.ShowNetwork, v => st.ShowNetwork = v),
                ToggleRow("Upload / download speed", null, () => st.ShowNetSpeed, v => st.ShowNetSpeed = v),
                ToggleRow("Volume", null, () => st.ShowVolume, v => st.ShowVolume = v),
                ToggleRow("Battery", null, () => st.ShowBattery, v => st.ShowBattery = v),
                ToggleRow("Notifications button", null, () => st.ShowNotifications, v => st.ShowNotifications = v),
                ToggleRow("Show-desktop corner", null, () => st.ShowDesktopCorner, v => st.ShowDesktopCorner = v));

            Section(page, "Clock");
            AddCard(page,
                ToggleRow("Clock", null, () => st.ShowClock, v => st.ShowClock = v),
                ToggleRow("Date under the time", null, () => st.ShowDate, v => st.ShowDate = v),
                ToggleRow("Seconds", null, () => st.ShowSeconds, v => st.ShowSeconds = v));
        }

        void BuildDropDown(Panel page)
        {
            Section(page, "Top drop-down");
            AddCard(page,
                ToggleRow("Top drop-down", "Media and performance slide down when the mouse rests at the top edge of the screen",
                    () => st.TopPanel, v => st.TopPanel = v),
                ToggleRow("Notch at the top edge", "A small tab that shows where the drop-down is and lights up as you get close",
                    () => st.ShowNotch, v => st.ShowNotch = v),
                Row("Opens after", "How long the mouse rests at the edge first (longer = fewer accidental openings)",
                    Slider(0, 600, 10, () => st.TopPanelDelay, v => st.TopPanelDelay = v, v => v == 0 ? "At once" : v + " ms", 180)),
                Row("Opens on", LumenR.Installed ? "Auto: LumenR while an episode plays there, Media while music plays" : null,
                    LumenR.Installed
                    ? Choice(new[] { "Auto", "Media", "LumenR", "Performance", "Last" },
                        new[] { "What's playing", "Media", "LumenR", "Performance", "Where I left it" }, () => st.TopPanelTab, v => st.TopPanelTab = v, 220)
                    : Choice(new[] { "Auto", "Media", "Performance", "Last" },
                        new[] { "Media while something plays", "Media", "Performance", "Where I left it" }, () => st.TopPanelTab, v => st.TopPanelTab = v, 220)));

            Section(page, "Media");
            var looks = new[] { "Random" }.Concat(Enum.GetNames(typeof(Vinyl.Style))).ToArray();
            var lookLabels = new[] { "A different one per song" }.Concat(Enum.GetNames(typeof(Vinyl.Style))).ToArray();
            AddCard(page,
                Row("Record speed", "How fast the vinyl spins (0 = it stays still)",
                    Slider(0, 45, 1, () => st.VinylRpm, v => st.VinylRpm = v, v => v == 0 ? "Still" : v + " rpm", 180)),
                Row("Record look", "The pressing the vinyl gets, coloured from the cover",
                    Choice(looks, lookLabels, () => st.VinylStyle, v => st.VinylStyle = v, 220)));
        }

        void BuildGeneral(Panel page)
        {
            Section(page, "Windows key");
            AddCard(page,
                ToggleRow("Open WispR with the Windows key", "Instead of the Windows Start menu", () => st.InterceptWinKey, v => st.InterceptWinKey = v),
                Row("Double-tap speed", "Tap once for the launcher, twice quickly to hide or show the bars",
                    Slider(150, 800, 25, () => st.DoubleTapMs, v => st.DoubleTapMs = v, v => v + " ms", 180)));

            Section(page, "Launcher");
            var engine = Combo(160);
            engine.Items.AddRange(Settings.SearchEngines.Keys.Cast<object>().ToArray());
            engine.SelectedIndexChanged += (o, e) => { if (!loading) { st.SearchEngine = (string)engine.SelectedItem; Changed(); } };
            loaders.Add(() => engine.SelectedItem = st.SearchEngine);
            AddCard(page,
                Row("Position", null, Choice(new[] { "Bottom", "Center", "Top" },
                    new[] { "Attached to the bottom", "Middle of the screen", "Top of the screen" },
                    () => st.LauncherPosition, v => st.LauncherPosition = v, 200)),
                Row("Web search", "Used for \"Search the web\" results", engine),
                Row("Results shown", null, Slider(4, 12, 1, () => st.LauncherRows, v => st.LauncherRows = v, v => v + " rows", 180)));

            Section(page, "Timers");
            AddCard(page,
                ToggleRow("Sound when a timer ends", "Type \"set timer 10m\" in the launcher to start one", () => st.TimerSound, v => st.TimerSound = v));

            Section(page, "Start menu");
            var nameBox = new TextBox { Width = S(180), BorderStyle = BorderStyle.FixedSingle, Font = rowFont };
            nameBox.TextChanged += (o, e) => { if (!loading) { st.AccountName = nameBox.Text; Changed(); } };
            loaders.Add(() => nameBox.Text = st.AccountName);
            stylers.Add(t => { nameBox.BackColor = InputBack(t); nameBox.ForeColor = t.Text; });
            AddCard(page,
                Row("Name", "Shown in the Start menu", nameBox),
                ToggleRow("Account picture", "Your Windows account picture in the Start menu", () => st.ShowAccountPicture, v => st.ShowAccountPicture = v));

            Section(page, "Startup");
            var auto = new ToggleSwitch(() => T, s);
            auto.CheckedChanged += (o, e) => { if (!loading) Autostart.IsEnabled = auto.Checked; };
            loaders.Add(() => auto.Checked = Autostart.IsEnabled);
            AddCard(page, Row("Start with Windows", null, auto));
        }

        // ---------- behaviour ----------

        void LoadValues()
        {
            loading = true;
            foreach (var load in loaders) load();
            SyncPreset();
            RefreshSwatches();
            loading = false;
            UpdateEnabled();
        }

        void SyncPreset()
        {
            bool isPreset = !st.MatchImageColors && Theme.Presets.Any(p => p.Name == st.Theme.Name);
            foreach (var tile in presetTiles) tile.Selected = isPreset && tile.Preset.Name == st.Theme.Name;
            if (themeNote != null)
                themeNote.Text = st.MatchImageColors ? "Current colours: taken from your picture"
                               : isPreset ? "Current theme: " + st.Theme.Name : "Current colours: your own mix";
        }

        void RefreshSwatches()
        {
            foreach (var kv in swatches) kv.Value.Color = st.Theme.Get(kv.Key);
        }

        void PickColor(string key)
        {
            using var dlg = new ColorDialog { Color = st.Theme.Get(key), FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            st.Theme.Set(key, dlg.Color);
            st.Theme.Name = "Custom";
            st.MatchImageColors = false; // hand-picked colours win over the picture
            loading = true; matchColors.Checked = false; loading = false;
            Changed();
        }

        void Changed() => st.NotifyChanged();

        bool HasImage() => st.UseWallpaper ? Wallpaper.GetPath() != null : File.Exists(st.BackgroundImage);

        void UpdateEnabled()
        {
            bool custom = !useWallpaper.Checked;
            imagePath.Enabled = browse.Enabled = clearImage.Enabled = custom;
            paletteMode.Enabled = matchColors.Checked;
        }

        /// <summary>Recolours the whole window for the current theme.</summary>
        void Restyle()
        {
            var t = T;
            BackColor = t.Background;
            content.BackColor = t.Background;
            sidebar.BackColor = Ui.Mix(t.Background, t.Surface, 0.45f);
            foreach (var p in pages) p.page.BackColor = t.Background;
            foreach (var a in stylers) a(t);
            try
            {
                int dark = t.IsLight ? 0 : 1;
                Native.DwmSetWindowAttribute(Handle, 20 /* immersive dark mode */, ref dark, 4);
                int caption = ColorTranslator.ToWin32(sidebar.BackColor);
                Native.DwmSetWindowAttribute(Handle, 35 /* caption colour (Windows 11) */, ref caption, 4);
            }
            catch { }
            // dark scrollbars and dropdowns for dark themes (Windows 10/11)
            try { SetWindowTheme(content.Handle, t.IsLight ? "Explorer" : "DarkMode_Explorer", null); } catch { }
            Invalidate(true);
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        string lastThemeKey;

        void OnSettingsChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnSettingsChanged); return; }
            // colours changed (preset, picker, or taken from the picture): recolour
            string key = string.Join(",", Theme.ColorSlots.Select(c => st.Theme.Get(c.Key).ToArgb())) + st.MatchImageColors + st.Theme.Name;
            if (key == lastThemeKey) return;
            lastThemeKey = key;
            SyncPreset();
            RefreshSwatches();
            Restyle();
        }

        void OnThemeRecomputed()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnThemeRecomputed); return; }
            lastThemeKey = null;
            OnSettingsChanged();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            st.ThemeRecomputed -= OnThemeRecomputed;
            st.Changed -= OnSettingsChanged;
            Anim.Stop(content);
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); sectionFont.Dispose(); rowFont.Dispose(); descFont.Dispose(); }
            base.Dispose(disposing);
        }

        // ---------- theme tile ----------

        /// <summary>A small preview of a theme: its background with a search bar, accent and text lines.</summary>
        sealed class PresetTile : Control
        {
            public readonly Theme Preset;
            readonly Func<Theme> current;
            readonly float s;
            bool selected, hover;
            static Font font;

            public PresetTile(Theme preset, Func<Theme> current, float scale)
            {
                Preset = preset; this.current = current; s = scale;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Size = new Size((int)(128 * s), (int)(94 * s));
                Margin = new Padding(0, 0, (int)(6 * s), (int)(6 * s));
                Cursor = Cursors.Hand;
            }

            public bool Selected { get => selected; set { selected = value; Invalidate(); } }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var t = current();
                var p = Preset;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(t.Surface);
                var card = new RectangleF(2, 2, Width - 4, Height - 26 * s);
                using (var path = Ui.Round(card, 7 * s))
                {
                    using (var b = new SolidBrush(p.Background)) g.FillPath(b, path);
                    // a mini launcher: search bar, a highlighted row, two text lines
                    var bar = new RectangleF(card.X + 8 * s, card.Y + 8 * s, card.Width - 16 * s, 12 * s);
                    using (var bp = Ui.Round(bar, 4 * s)) using (var b = new SolidBrush(p.Surface)) g.FillPath(b, bp);
                    var row = new RectangleF(card.X + 8 * s, bar.Bottom + 6 * s, card.Width - 16 * s, 14 * s);
                    using (var rp = Ui.Round(row, 4 * s)) using (var b = new SolidBrush(p.Selection)) g.FillPath(b, rp);
                    using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, row.X, row.Y + 4 * s, 2.5f * s, row.Height - 8 * s);
                    using (var b = new SolidBrush(p.Text)) g.FillRectangle(b, row.X + 8 * s, row.Y + 5 * s, row.Width * 0.45f, 3 * s);
                    using (var b = new SolidBrush(p.SubText)) g.FillRectangle(b, row.X + 8 * s, row.Bottom + 7 * s, row.Width * 0.6f, 3 * s);
                    using (var b = new SolidBrush(p.Accent)) g.FillEllipse(b, card.Right - 16 * s, card.Bottom - 14 * s, 8 * s, 8 * s);
                    using (var pen = new Pen(selected ? t.Accent : hover ? Ui.Mix(t.Border, t.Text, 0.4f) : Color.FromArgb(80, t.Border), selected ? 2.5f * s : 1f))
                        g.DrawPath(pen, path);
                }
                font ??= new Font("Segoe UI", 8.75f);
                TextRenderer.DrawText(g, p.Name, font, new Rectangle(0, (int)(Height - 22 * s), Width, (int)(20 * s)), selected ? t.Text : t.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
