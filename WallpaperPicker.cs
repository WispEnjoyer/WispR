using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The launcher's wallpaper carousel (type "wallpaper"): thumbnails of your wallpapers, the one in
    /// the middle large, each with the colour palette WispR would use for it. Scroll, ← / → or click
    /// to browse; click the middle one or press Enter to set it (Ctrl+Enter also switches WispR to its colours).
    /// The launcher owns the window; this class does the data, loading, painting and hit-testing.
    /// </summary>
    sealed class WallpaperPicker : IDisposable
    {
        public sealed class Item
        {
            public string Path, Name;      // Path: the picture (for Wallpaper Engine: its preview)
            public string WeJson, Kind;    // set for Wallpaper Engine wallpapers
            public bool EngineFav;         // ♥ in Wallpaper Engine
            public string HeartKey => WeJson ?? Path;
            public volatile Bitmap Thumb;     // 16:9, filled in by the loader thread
            public volatile Theme Palette;
            public bool Failed;
        }

        static readonly string[] Extensions = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        static readonly Regex Trigger = new Regex(@"^>?\s*(wallpapers?|hintergr[uü]nde?|hintergrundbild(er)?)\b\s*(?<filter>.*)$", RegexOptions.IgnoreCase);
        const int ThumbW = 320, ThumbH = 180;
        // fonts are made once (the carousel repaints every frame while it moves)
        static readonly Font MsgFont = new Font("Segoe UI", 9.5f), NameFont = new Font("Segoe UI", 8.5f),
            SelFont = new Font("Segoe UI Semibold", 10f), SmallFont = new Font("Segoe UI", 8f),
            TagFont = new Font("Segoe UI Semibold", 7f), HeartFont = new Font("Segoe UI Symbol", 9f),
            BadgeFont = new Font("Segoe UI Semibold", 7.5f);

        readonly Settings settings;
        readonly Control host;
        readonly float s;
        bool animating; // the carousel is gliding (on the shared frame clock)
        void StartGlide() { if (animating) return; animating = true; Anim.Frames(this, () => { if (animating) Animate(); return animating; }); }
        void StopGlide() { animating = false; Anim.StopFrames(this); }
        readonly Dictionary<string, Item> cache = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        List<Item> all = new List<Item>();
        List<Item> shown = new List<Item>();
        string filter = "";
        string currentPath;
        // The carousel loops endlessly: positions are "virtual" indices that keep counting past the
        // ends; the picture shown at virtual index v is shown[v mod count].
        float pos;                   // animated position (fractional virtual index)
        int selected;                // virtual index of the selected picture
        int hover = -1;
        int wheelCarry;
        Thread loader;
        volatile int generation;
        string status;               // short feedback, e.g. "Wallpaper set"
        DateTime statusUntil;
        readonly List<(int index, RectangleF rect)> cardRects = new List<(int, RectangleF)>();

        public bool Active { get; private set; }
        public event Action<Item, bool> Apply; // (item, alsoUseColours)

        public WallpaperPicker(Settings settings, Control host, float scale)
        {
            this.settings = settings;
            this.host = host;
            s = scale;
        }

        int S(float px) => (int)Math.Round(px * s);

        /// <summary>Is this launcher text a request for the wallpaper picker? Returns the name filter.</summary>
        public static bool IsTrigger(string text, out string nameFilter)
        {
            var m = Trigger.Match(text.Trim());
            nameFilter = m.Success ? m.Groups["filter"].Value.Trim() : "";
            return m.Success;
        }

        static readonly Regex AllWord = new Regex(@"^(all|alle)\b\s*", RegexOptions.IgnoreCase);
        bool showAll; // "wallpaper all": everything, not just favourites

        bool Hearted(Item it) => it.EngineFav || settings.WallpaperHearts.Contains(it.HeartKey, StringComparer.OrdinalIgnoreCase);

        /// <summary>♥ / un-♥ the selected wallpaper (right-click or Ctrl+F).</summary>
        public void ToggleHeart(Item it = null)
        {
            it ??= SelectedItem;
            if (it == null || it.HeartKey == null) return;
            if (it.EngineFav)
                status = "♥ comes from Wallpaper Engine — remove it there";
            else if (settings.WallpaperHearts.RemoveAll(k => string.Equals(k, it.HeartKey, StringComparison.OrdinalIgnoreCase)) > 0)
                status = "Removed from favourites";
            else { settings.WallpaperHearts.Add(it.HeartKey); status = "♥ Added to favourites"; }
            settings.Save();
            statusUntil = DateTime.Now.AddSeconds(2.5);
            host.Invalidate();
        }

        public void RightClick(Point p)
        {
            int i = HitTest(p);
            if (i >= 0) ToggleHeart(shown[Mod(i)]);
        }

        // ---------- folders & items ----------

        static string PicturesWallpapers => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallpapers");
        static string WindowsWallpapers => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper");

        public static string DefaultFolderLabel() =>
            Directory.Exists(PicturesWallpapers) ? @"Pictures\Wallpapers" : "Windows' own wallpapers";

        IEnumerable<string> Folders()
        {
            if (settings.WallpaperFolder.Length > 0 && Directory.Exists(settings.WallpaperFolder)) yield return settings.WallpaperFolder;
            else if (Directory.Exists(PicturesWallpapers)) yield return PicturesWallpapers;
            else if (Directory.Exists(WindowsWallpapers)) yield return WindowsWallpapers;
        }

        string note; // e.g. "no favourites found" — shown in the hint line

        void Scan()
        {
            var list = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            note = null;
            string source = settings.WallpaperSource;
            bool engineInstalled = WallpaperEngine.Folder != null;
            // pictures only in "All" mode, or when there's no Wallpaper Engine to show
            bool pictures = source == "All" || !engineInstalled;
            if (pictures)
            foreach (var folder in Folders())
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                {
                    if (list.Count >= 300) break;
                    if (!Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) || !seen.Add(f)) continue;
                    list.Add(Get(f));
                }
            }
            list = list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            currentPath = Wallpaper.GetWindowsPath();
            if (pictures && currentPath != null && File.Exists(currentPath) && !seen.Contains(currentPath))
            {
                var cur = Get(currentPath);
                if (cur.Name.Equals("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase)) cur.Name = "Current wallpaper";
                list.Insert(0, cur);
            }

            // Wallpaper Engine's wallpapers (Workshop + your own); first while Wallpaper Engine is running
            weRunning = false; currentWe = null;
            if (engineInstalled)
            {
                try
                {
                    var projects = WallpaperEngine.Projects();
                    var favs = WallpaperEngine.Favorites();
                    var we = projects
                        .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                        .Select(p =>
                        {
                            if (!cache.TryGetValue("we:" + p.Json, out var it))
                                cache["we:" + p.Json] = it = new Item { Path = p.Preview, Name = p.Title, WeJson = p.Json, Kind = p.Kind, Failed = p.Preview == null };
                            it.EngineFav = WallpaperEngine.IsFavorite(p, favs);
                            return it;
                        }).ToList();
                    if (source == "Favorites" && !showAll)
                    {
                        we = we.Where(Hearted).ToList();
                        if (we.Count == 0)
                            note = "No favourites yet. Type “wallpaper all”, then right-click a wallpaper (or Ctrl+F) to ♥ it.";
                    }
                    weRunning = WallpaperEngine.IsRunning;
                    currentWe = weRunning ? WallpaperEngine.CurrentProject() : null;
                    if (we.Count > 0) Log.Throttled("we-scan", "Wallpaper Engine: " + we.Count + " wallpapers found in " + WallpaperEngine.Folder);
                    if (weRunning || !pictures) list.InsertRange(0, we); else list.AddRange(we);
                }
                catch (Exception ex) { Log.Error("WallpaperPicker.WallpaperEngine", ex); }
            }
            all = list;
        }

        bool weRunning;
        string currentWe;

        /// <summary>Is this the wallpaper on screen right now?</summary>
        bool IsCurrent(Item it) => it.WeJson != null
            ? weRunning && string.Equals(it.WeJson, currentWe, StringComparison.OrdinalIgnoreCase)
            : !(weRunning && currentWe != null) && string.Equals(it.Path, currentPath, StringComparison.OrdinalIgnoreCase);

        Item Get(string path)
        {
            if (!cache.TryGetValue(path, out var it))
                cache[path] = it = new Item { Path = path, Name = Path.GetFileNameWithoutExtension(path) };
            return it;
        }

        // ---------- open / filter / close ----------

        public void Open(string nameFilter)
        {
            var am = AllWord.Match(nameFilter);
            bool wantAll = am.Success;
            if (wantAll) nameFilter = nameFilter.Substring(am.Length);
            if (!Active || wantAll != showAll)
            {
                showAll = wantAll;
                Scan();
                Active = true;
                filter = null; // force ApplyFilter
            }
            if (nameFilter != filter) ApplyFilter(nameFilter);
            StartLoader();
        }

        void ApplyFilter(string nameFilter)
        {
            filter = nameFilter;
            var q = Matcher.Normalize(nameFilter);
            shown = q.Length == 0 ? all : all.Where(i => Matcher.Score(q, new AppEntry(i.Name)) >= 0).ToList();
            int cur = shown.FindIndex(IsCurrent);
            selected = q.Length == 0 && cur >= 0 ? cur : 0;
            pos = selected;
            hover = -1;
        }

        public void Close()
        {
            Active = false;
            StopGlide();
            StopGif();
        }

        /// <summary>Frees the thumbnails (when the launcher hides); they come back quickly from Windows' cache.</summary>
        public void ReleaseImages()
        {
            generation++;
            StopGif();
            foreach (var it in cache.Values)
            {
                var b = it.Thumb;
                it.Thumb = null;
                b?.Dispose();
                it.Failed = false;
            }
        }

        // ---------- background loading (nearest to the selection first) ----------

        void StartLoader()
        {
            if (loader != null && loader.IsAlive) return;
            int gen = generation;
            loader = new Thread(() =>
            {
                while (gen == generation && Active)
                {
                    var list = shown;
                    int n = list.Count;
                    int sel = n == 0 ? 0 : ((selected % n) + n) % n;
                    Item next = null;
                    int best = int.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        var it = list[i];
                        if (it.Thumb != null || it.Failed) continue;
                        int d = Math.Abs(i - sel);
                        d = Math.Min(d, n - d); // the carousel wraps around
                        if (d < best) { best = d; next = it; }
                    }
                    if (next == null) break;
                    try
                    {
                        var thumb = MakeThumb(next.Path);
                        if (thumb == null) { next.Failed = true; continue; }
                        var palette = Palette.FromImage(thumb, settings.PaletteMode);
                        if (gen != generation) { thumb.Dispose(); break; }
                        next.Palette = palette;
                        next.Thumb = thumb;
                        try { host.BeginInvoke((Action)(() => host.Invalidate())); } catch { }
                    }
                    catch (Exception ex)
                    {
                        next.Failed = true;
                        Log.Error("WallpaperPicker.Load", ex);
                    }
                }
            })
            { IsBackground = true, Name = "WallpaperThumbs", Priority = ThreadPriority.BelowNormal };
            loader.SetApartmentState(ApartmentState.STA); // shell thumbnails want STA
            loader.Start();
        }

        /// <summary>16:9 cover-cropped thumbnail: Windows' thumbnail cache first, decoding the file as fallback.</summary>
        static Bitmap MakeThumb(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            Bitmap src = null;
            try
            {
                if (new FileInfo(path).Length < 2 * 1024 * 1024) // previews are small: read the file itself
                {
                    src = ImageLoad.FromFile(path, 960);
                }
            }
            catch { src = null; }
            src ??= IconLoader.LoadThumbnail(path, 480);
            src ??= ImageLoad.FromFile(path, 960);
            if (src == null) return null;
            using (src)
            {
                var dst = new Bitmap(ThumbW, ThumbH, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(dst);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float sc = Math.Max((float)ThumbW / src.Width, (float)ThumbH / src.Height);
                float w = src.Width * sc, h = src.Height * sc;
                g.DrawImage(src, new RectangleF((ThumbW - w) / 2, (ThumbH - h) / 2, w, h));
                return dst;
            }
        }

        // ---------- navigation ----------

        public int Count => shown.Count;
        int Mod(int v) => shown.Count == 0 ? 0 : ((v % shown.Count) + shown.Count) % shown.Count;
        public Item SelectedItem => shown.Count == 0 ? null : shown[Mod(selected)];

        /// <summary>Moves by <paramref name="delta"/> pictures, endlessly in both directions.</summary>
        public void Move(int delta)
        {
            if (shown.Count <= 1 || delta == 0) return;
            selected += delta;
            StartGlide();
            host.Invalidate();
        }

        /// <summary>Jumps to the first / last picture (a long jump doesn't scroll through everything).</summary>
        public void JumpTo(int realIndex)
        {
            if (shown.Count <= 1) return;
            int delta = Math.Max(0, Math.Min(shown.Count - 1, realIndex)) - Mod(selected);
            if (delta == 0) return;
            selected += delta;
            if (Math.Abs(delta) > 3) pos = selected - Math.Sign(delta) * 3;
            StartGlide();
            host.Invalidate();
        }

        public void Wheel(int delta)
        {
            wheelCarry += delta;
            while (wheelCarry >= 120) { wheelCarry -= 120; Move(-1); }
            while (wheelCarry <= -120) { wheelCarry += 120; Move(+1); }
            if (Math.Abs(delta) < 120 && Math.Abs(wheelCarry) >= 60) { Move(wheelCarry > 0 ? -1 : 1); wheelCarry = 0; } // touchpads
        }

        void Animate()
        {
            float d = selected - pos;
            if (Math.Abs(d) < 0.002f) { pos = selected; animating = false; }
            else pos += d * (1f - (float)Math.Pow(1 - 0.22, Anim.Dt)); // same glide at any refresh rate
            host.Invalidate();
        }

        public void ApplySelected(bool alsoColours)
        {
            var it = SelectedItem;
            if (it == null) return;
            if (it.WeJson != null)
            {
                weRunning = true;
                currentWe = it.WeJson;
                status = alsoColours ? "Playing in Wallpaper Engine · colours set" : "Playing in Wallpaper Engine";
            }
            else
            {
                currentPath = it.Path;
                currentWe = null; // a picture replaces Wallpaper Engine's wallpaper
                status = alsoColours ? "Wallpaper and colours set" : "Wallpaper set";
            }
            statusUntil = DateTime.Now.AddSeconds(2.5);
            Apply?.Invoke(it, alsoColours);
            host.Invalidate();
        }

        /// <summary>Mouse up on the carousel: a side card is selected, the middle one is applied.</summary>
        public void Click(Point p, bool ctrl)
        {
            int i = HitTest(p);
            if (i < 0) return;
            if (i == selected) ApplySelected(ctrl);
            else { selected = i; StartGlide(); host.Invalidate(); }
        }

        public void MouseMove(Point p)
        {
            int h = HitTest(p);
            if (h != hover) { hover = h; host.Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; host.Invalidate(); }
        }

        public void MouseLeave() { if (hover != -1) { hover = -1; host.Cursor = Cursors.Default; host.Invalidate(); } }

        int HitTest(Point p)
        {
            // front-most (closest to the middle) first
            foreach (var (index, rect) in cardRects.OrderBy(c => Math.Abs(c.index - pos)))
                if (rect.Contains(p)) return index;
            return -1;
        }

        // ---------- painting ----------

        public int PreferredHeight(int width)
        {
            var (selW, _) = CardSizes(width);
            return (int)(selW * 9 / 16) + S(16) + S(26) + S(22) + S(26);
        }

        (float selW, float sideW) CardSizes(int width)
        {
            float gap = S(12);
            float selW = Math.Min(S(300), (width - 4 * gap) / 4f); // five fit: selW + 4·sideW + 4·gap, sideW = ¾·selW
            return (selW, selW * 0.75f);
        }

        public void Paint(Graphics g, Rectangle area, Theme t)
        {
            cardRects.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool moving = animating;
            g.InterpolationMode = moving ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;

            if (shown.Count == 0)
            {
                string msg = all.Count == 0 && note != null ? note
                    : all.Count == 0
                    ? "No wallpapers found. Choose a folder in Settings → Appearance → Wallpaper folder, or install Wallpaper Engine."
                    : "No wallpaper matches “" + filter + "”.";
                TextRenderer.DrawText(g, msg, MsgFont, area, t.SubText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            UpdateGif();
            var (selW, sideW) = CardSizes(area.Width);
            float gap = S(12), selH = selW * 9 / 16, top = area.Y + S(16);
            float cx = area.X + area.Width / 2f;
            float near = (selW + sideW) / 2 + gap, far = sideW + gap;

            var state = g.Save();
            g.SetClip(area);

            // draw from the outside in, so the middle card is on top
            // which virtual positions to draw: each picture at most once, up to four either side
            int count = shown.Count;
            int first = (int)Math.Ceiling(pos - count / 2.0), last = first + count - 1;
            first = Math.Max(first, (int)Math.Floor(pos) - 4);
            last = Math.Min(last, (int)Math.Ceiling(pos) + 4);
            var order = Enumerable.Range(first, Math.Max(0, last - first + 1)).OrderByDescending(i => Math.Abs(i - pos)).ToList();
            foreach (int i in order)
            {
                var item = shown[Mod(i)];
                float d = i - pos, ad = Math.Abs(d);
                float k = Math.Min(1, ad);                          // 0 = middle, 1 = side
                float w = selW + (sideW - selW) * k, h = w * 9 / 16;
                float offset = ad <= 1 ? d * near : Math.Sign(d) * (near + (ad - 1) * far);
                var r = new RectangleF(cx + offset - w / 2, top + (selH - h) / 2, w, h);
                if (r.Right < area.Left || r.Left > area.Right) continue;
                cardRects.Add((i, r));
                DrawCard(g, item, r, t, k, i == hover, i == selected);

                // name + palette circles under the card
                bool sel = k < 0.5f;
                var font = sel ? SelFont : NameFont;
                var nameRect = new Rectangle((int)r.X - S(6), (int)(r.Bottom + S(6)), (int)r.Width + S(12), S(22));
                TextRenderer.DrawText(g, item.Name, font, nameRect, sel ? t.Text : t.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                DrawPalette(g, item.Palette, r.X + r.Width / 2, nameRect.Bottom + S(4), sel ? S(13) : S(9), t);
            }
            g.Restore(state);

            // bottom line: position, and either feedback or the key hints
            int y = area.Bottom - S(22);
            TextRenderer.DrawText(g, (Mod(selected) + 1) + " / " + shown.Count, SmallFont, new Rectangle(area.X + S(4), y, S(80), S(20)), t.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            string hint = DateTime.Now < statusUntil ? "✓ " + status
                : note != null ? note
                : settings.WallpaperSource == "Favorites" && !showAll && WallpaperEngine.Folder != null
                    ? "Enter: set   ·   right-click / Ctrl+F: ♥   ·   “wallpaper all”: show all   ·   scroll or ← →"
                : "Enter: set as wallpaper   ·   Ctrl+Enter: also use its colours   ·   scroll or ← → to browse";
            TextRenderer.DrawText(g, hint, SmallFont, new Rectangle(area.X + S(80), y, area.Width - S(84), S(20)),
                DateTime.Now < statusUntil ? t.Accent : t.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (DateTime.Now < statusUntil && !animating) RepaintLater();
        }

        void DrawCard(Graphics g, Item it, RectangleF r, Theme t, float k, bool hovered, bool isSelected)
        {
            float radius = S(8);
            using var path = Rounded(r, radius);
            var thumb = it.Thumb;
            if (gif != null && it == gifFor && k < 0.05f && DrawGif(g, path, r)) { }
            else if (thumb != null)
            {
                try
                {
                    using var brush = new TextureBrush(thumb, WrapMode.Clamp);
                    brush.TranslateTransform(r.X, r.Y);
                    brush.ScaleTransform(r.Width / thumb.Width, r.Height / thumb.Height);
                    g.FillPath(brush, path);
                }
                catch { /* image released while drawing */ }
            }
            else
            {
                using var b = new SolidBrush(t.Selection);
                g.FillPath(b, path);
                var f = SmallFont;
                TextRenderer.DrawText(g, it.Failed ? "can't preview" : "…", f, Rectangle.Round(r), t.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // side cards slightly dimmed, the middle one framed
            if (k > 0.01f && !hovered)
                using (var dim = new SolidBrush(Color.FromArgb((int)(70 * k), t.Background))) g.FillPath(dim, path);
            float frame = (1 - k);
            if (frame > 0.05f || hovered)
                using (var pen = new Pen(Color.FromArgb((int)(255 * Math.Max(frame, hovered ? 0.6f : 0)), t.Accent), Math.Max(1.5f, 2 * s)))
                    g.DrawPath(pen, path);

            if (it.WeJson != null) // Wallpaper Engine tag, bottom left
            {
                var f = TagFont;
                string tag = "▶ " + it.Kind;
                var sz = TextRenderer.MeasureText(tag, f);
                var badge = new RectangleF(r.X + S(6), r.Bottom - sz.Height - S(8), sz.Width + S(6), sz.Height + S(2));
                using (var p = Rounded(badge, badge.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillPath(b, p);
                TextRenderer.DrawText(g, tag, f, Rectangle.Round(badge), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            if (Hearted(it)) // ♥ badge, top left
            {
                float d = S(20);
                var circle = new RectangleF(r.X + S(6), r.Y + S(6), d, d);
                using (var b = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) g.FillEllipse(b, circle);
                var hf = HeartFont;
                TextRenderer.DrawText(g, "♥", hf, Rectangle.Round(circle), Color.FromArgb(255, 90, 110),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (IsCurrent(it)) // "in use" badge
            {
                var f = BadgeFont;
                var sz = TextRenderer.MeasureText("Current", f);
                var badge = new RectangleF(r.Right - sz.Width - S(14), r.Y + S(6), sz.Width + S(8), sz.Height + S(2));
                using (var p = Rounded(badge, badge.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(220, t.Accent))) g.FillPath(b, p);
                var onAccent = t.Accent.GetBrightness() > 0.6f ? Color.FromArgb(20, 20, 20) : Color.White;
                TextRenderer.DrawText(g, "Current", f, Rectangle.Round(badge), onAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>The colours WispR would use for this wallpaper: background, surface, selection, accent, text.</summary>
        void DrawPalette(Graphics g, Theme p, float centerX, float y, int d, Theme t)
        {
            int n = 5, gap = Math.Max(3, d / 3);
            float x = centerX - (n * d + (n - 1) * gap) / 2f;
            var colours = p == null ? null : new[] { p.Background, p.Surface, p.Selection, p.Accent, p.Text };
            for (int i = 0; i < n; i++)
            {
                var rc = new RectangleF(x + i * (d + gap), y, d, d);
                using (var b = new SolidBrush(colours?[i] ?? Color.FromArgb(60, t.SubText))) g.FillEllipse(b, rc);
                using (var pen = new Pen(Color.FromArgb(120, t.Border), 1)) g.DrawEllipse(pen, rc);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
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

        // ---------- the selected Wallpaper Engine card plays its animated preview ----------

        Image gif;
        Item gifFor;
        RectangleF gifRect;
        EventHandler gifFrame;

        void UpdateGif()
        {
            var it = SelectedItem;
            Item want = Active && !animating && it?.WeJson != null && it.Path != null &&
                        it.Path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? it : null;
            if (want == gifFor) return;
            StopGif();
            if (want == null) return;
            gifFor = want;
            try
            {
                if (new FileInfo(want.Path).Length > 30 * 1024 * 1024) return; // huge previews stay still
                gif = Image.FromFile(want.Path);
                // animated previews play at their own size: refuse oversized ones (each frame is decoded in full)
                if (!ImageAnimator.CanAnimate(gif) || (long)gif.Width * gif.Height > 4_000_000) { gif.Dispose(); gif = null; return; }
                gifFrame ??= (o, e) =>
                {
                    try { host.BeginInvoke((Action)(() => { if (gif != null && gifRect.Width > 0) host.Invalidate(Rectangle.Inflate(Rectangle.Round(gifRect), 2, 2)); })); } catch { }
                };
                ImageAnimator.Animate(gif, gifFrame);
            }
            catch (Exception ex) { gif?.Dispose(); gif = null; Log.Error("WallpaperPicker.Gif", ex); }
        }

        bool DrawGif(Graphics g, GraphicsPath clip, RectangleF r)
        {
            try
            {
                ImageAnimator.UpdateFrames(gif);
                gifRect = r;
                var state = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                float sc = Math.Max(r.Width / gif.Width, r.Height / gif.Height);
                float w = gif.Width * sc, h = gif.Height * sc;
                g.DrawImage(gif, new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
                g.Restore(state);
                return true;
            }
            catch { return false; }
        }

        void StopGif()
        {
            if (gif != null)
            {
                try { ImageAnimator.StopAnimate(gif, gifFrame); } catch { }
                gif.Dispose();
                gif = null;
            }
            gifFor = null;
            gifRect = RectangleF.Empty;
        }

        bool repaintQueued;
        void RepaintLater()
        {
            if (repaintQueued) return;
            repaintQueued = true;
            var t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += (o, e) => { t.Stop(); t.Dispose(); repaintQueued = false; host.Invalidate(); };
            t.Start();
        }

        public void Dispose()
        {
            Close();
            ReleaseImages();
            StopGlide();
        }
    }
}
