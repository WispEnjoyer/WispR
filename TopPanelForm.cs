using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The drop-down at the top of the screen: touch the top edge with the mouse and it slides down out
    /// of the frame; move away and it slides back up. Two tabs — Media (now playing, with cover, seeking
    /// and controls) and Performance (CPU, memory and storage as rings).
    /// Drawn as one per-pixel-transparent picture (smooth curves into the screen edge), never takes focus.
    /// </summary>
    sealed class TopPanelForm : Form
    {
        readonly Settings settings;
        readonly Backdrop backdrop;
        readonly float s;
        Theme T => settings.Theme;

        /// <summary>Where the panel hangs from on a screen: the inner top edge (below the frame), or null to not open there.</summary>
        public Func<Screen, int?> TopEdgeFor;

        // ---- geometry ----
        int PanelW => (int)(780 * s);
        int PanelH => (int)(300 * s);
        int Flare => (int)(18 * s);
        int Radius => (int)(18 * s);
        int TabH => (int)(58 * s);
        Rectangle screenRect;      // where the window is when fully open (screen coordinates)
        int topEdge;
        float slide;               // 0 = hidden above the edge, 1 = fully down
        bool open, closing;

        // ---- state ----
        int tab;                   // 0 = Media, 1 = Performance
        bool tabChosen;            // the user picked a tab during this session
        readonly string[] tabs = { "Media", "Performance" };
        readonly string[] tabGlyphs = { "\uE8D6", "\uE9D9" };
        Point mouse = new Point(-1, -1);
        bool seeking; float seekFrac;
        DateTime seekHoldUntil; long seekTargetTicks; DateTime seekAt;

        MediaInfo info;
        DateTime missingSince = DateTime.MinValue;
        readonly MediaService media = new MediaService();

        float cpu = -1; long lastIdle, lastTotal;
        double memUsedGb, memTotalGb; float memPct = -1;
        GpuSensor.Reading gpu;
        readonly List<float> cpuHistory = new List<float>();

        // hit areas (client coordinates), filled while drawing
        readonly List<(Rectangle r, Action click)> buttons = new List<(Rectangle, Action)>();
        Rectangle trackRect = Rectangle.Empty;

        readonly Timer watch = new Timer { Interval = 40 };   // edge trigger / leave detection
        readonly Timer tick = new Timer { Interval = 1000 };  // readings + progress while open
        DateTime atEdgeSince = DateTime.MinValue, outsideSince = DateTime.MinValue;

        static readonly Font tabFont = new Font("Segoe UI", 10f), bigFont = new Font("Segoe UI Light", 26f),
            titleFont = new Font("Segoe UI Semibold", 15f), textFont = new Font("Segoe UI", 10.5f),
            smallFont = new Font("Segoe UI", 8.5f), sideFont = new Font("Segoe UI Semibold", 11f);
        Font glyphFont, bigGlyphFont, tabGlyphFont;

        public TopPanelForm(Settings settings, Backdrop backdrop)
        {
            this.settings = settings;
            this.backdrop = backdrop;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "WispR Top Panel";
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            _ = Handle;
            media.Paused = true; // only reads while open
            media.Updated += i => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => ApplyMedia(i))); };
            watch.Tick += (o, e) => Watch();
            tick.Tick += (o, e) => { Sample(); if (Anim.FramesRunning(SpinKey)) layersDirty = true; else Redraw(); UpdateSpin(); };
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

        /// <summary>Turns the edge trigger on or off (follows the setting).</summary>
        public void SetEnabled(bool on)
        {
            if (on) { if (!watch.Enabled) watch.Start(); }
            else { watch.Stop(); if (open) ClosePanel(true); ConcealNotches(); }
        }

        // ---------- opening and closing ----------

        // ---------- the notch: a little tab at the top edge showing where the panel comes from ----------

        readonly Dictionary<string, NotchWindow> notches = new Dictionary<string, NotchWindow>();
        Screen[] screens; DateTime screensAt = DateTime.MinValue;
        string openDevice;

        void UpdateNotches(Point c)
        {
            if (screens == null || (DateTime.Now - screensAt).TotalSeconds > 2) { screens = Screen.AllScreens; screensAt = DateTime.Now; }
            byte alpha = (byte)Math.Max(0, Math.Min(255, settings.Opacity * 255 / 100));
            foreach (var scr in screens)
            {
                int? edge = settings.ShowNotch ? TopEdgeFor?.Invoke(scr) : null;
                notches.TryGetValue(scr.DeviceName, out var n);
                if (edge == null || ((open || closing) && openDevice == scr.DeviceName))
                {
                    n?.Conceal(); // the panel itself is there (or it can't open here)
                    continue;
                }
                if (n == null || n.IsDisposed) notches[scr.DeviceName] = n = new NotchWindow(s);
                int cx = scr.Bounds.X + scr.Bounds.Width / 2;
                // lights up as the mouse comes near: within the panel's width, closer to the edge = brighter
                float hover = 0;
                if (scr.Bounds.Contains(c) && Math.Abs(c.X - cx) <= PanelW / 2)
                {
                    float reach = 140 * s;
                    hover = Math.Max(0, 1 - (c.Y - edge.Value) / reach);
                    hover *= Math.Max(0.35f, 1 - Math.Abs(c.X - cx) / (PanelW / 2f) * 0.65f); // brightest straight below it
                }
                n.Set(cx, edge.Value, hover, T, alpha);
            }
        }

        void ConcealNotches() { foreach (var n in notches.Values) n.Conceal(); }

        void Watch()
        {
            var c = Cursor.Position;
            try { UpdateNotches(c); } catch (Exception ex) { Log.Throttled("notch", "Notch: " + ex.Message); }
            bool buttonsDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (!open)
            {
                // the top edge, roughly above where the panel would be, with no button held
                // (dragging a window to the top to maximize it must not open the panel)
                Screen scr;
                try { scr = Screen.FromPoint(c); } catch { return; }
                int? edge = TopEdgeFor?.Invoke(scr);
                bool atEdge = edge != null && !buttonsDown && c.Y <= scr.Bounds.Top + 1 &&
                              Math.Abs(c.X - (scr.Bounds.X + scr.Bounds.Width / 2)) <= PanelW / 2;
                if (!atEdge) { atEdgeSince = DateTime.MinValue; return; }
                if (atEdgeSince == DateTime.MinValue) atEdgeSince = DateTime.Now;
                if ((DateTime.Now - atEdgeSince).TotalMilliseconds >= 90) { atEdgeSince = DateTime.MinValue; OpenPanel(scr, edge.Value); }
                return;
            }
            if (closing || seeking || volDragging) return;
            // leave: the mouse is away from the panel (and the edge strip above it) for a moment,
            // or you click somewhere else
            var keep = Rectangle.Inflate(screenRect, (int)(14 * s), (int)(14 * s));
            keep = Rectangle.Union(keep, new Rectangle(screenRect.X, screenRect.Y - topEdge - 2, screenRect.Width, topEdge + 4));
            bool inside = keep.Contains(c);
            if (!inside && buttonsDown) { ClosePanel(false); return; }
            if (inside) { outsideSince = DateTime.MinValue; return; }
            if (outsideSince == DateTime.MinValue) outsideSince = DateTime.Now;
            if ((DateTime.Now - outsideSince).TotalMilliseconds >= 280) ClosePanel(false);
        }

        void OpenPanel(Screen scr, int edge)
        {
            topEdge = edge - scr.Bounds.Top;
            openDevice = scr.DeviceName;
            if (notches.TryGetValue(scr.DeviceName, out var nn)) nn.Conceal(); // the panel takes over from the notch
            int w = PanelW + Flare * 2;
            screenRect = new Rectangle(scr.Bounds.X + (scr.Bounds.Width - w) / 2, edge, w, PanelH);
            open = true; closing = false; outsideSince = DateTime.MinValue;
            if (!tabChosen) tab = info != null ? 0 : 1; // something playing → Media first
            media.Paused = false;
            Sample();
            BuildFonts();
            bgKey = null;
            Redraw();
            if (!Visible) Show();
            KeepOnTop();
            volWheel ??= new WheelHook(this, (delta, pt) =>
            {
                if (!open || tab != 0 || vol == null) return;
                float cur = vol.Value.muted ? 0 : vol.Value.level;
                SetVolume(cur + Math.Sign(delta) * 0.05f);
            });
            volWheel.Install();
            tick.Start();
            UpdateSpin();
            float from = slide;
            Anim.Run(this, 220, e => { slide = (float)Anim.Lerp(from, 1, e); Place(); }, null, Anim.OutCubic);
        }

        void ClosePanel(bool now)
        {
            if (!open) return;
            closing = true;
            tick.Stop();
            volWheel?.Uninstall();
            volDragging = false;
            void Done()
            {
                open = closing = false;
                slide = 0;
                Hide();
                media.Paused = true;
                ReleaseBitmap();
            }
            if (now) { Anim.Stop(this); Done(); return; }
            float from = slide;
            Anim.Run(this, 170, e => { slide = (float)Anim.Lerp(from, 0, e); Place(); }, Done, Anim.InCubic);
        }

        void KeepOnTop() => Native.SetWindowPos(Handle, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);

        // ---------- readings ----------

        void Sample()
        {
            try
            {
                if (GetSystemTimes(out long idle, out long kernel, out long user))
                {
                    long total = kernel + user;
                    if (lastTotal > 0 && total > lastTotal)
                    {
                        cpu = Math.Max(0, Math.Min(100, 100f * (1f - (float)(idle - lastIdle) / (total - lastTotal))));
                        cpuHistory.Add(cpu);
                        if (cpuHistory.Count > 40) cpuHistory.RemoveAt(0);
                    }
                    lastIdle = idle; lastTotal = total;
                }
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    memTotalGb = mem.ullTotalPhys / 1073741824.0;
                    memUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                    memPct = mem.dwMemoryLoad;
                }
                gpu = GpuSensor.Read();
                if (tab == 0) ReadVolume();
            }
            catch { }
        }

        void ApplyMedia(MediaInfo i)
        {
            if (i == null && info != null)
            {
                if (missingSince == DateTime.MinValue) missingSince = DateTime.Now;
                if ((DateTime.Now - missingSince).TotalSeconds < 4) return;
            }
            else missingSince = DateTime.MinValue;
            var old = info?.Thumbnail;
            if (i != null && info != null && i.Key == info.Key && DateTime.UtcNow < seekHoldUntil)
            {
                i.PositionTicks = seekTargetTicks; i.PositionAtUtc = seekAt;
            }
            bool newSong = i?.Key != info?.Key;
            info = i;
            if (newSong && open) ReadVolume();
            if (old != null && old != info?.Thumbnail) old.Dispose();
            if (open) { Redraw(); UpdateSpin(); }
        }

        // ---------- drawing ----------

        void BuildFonts()
        {
            glyphFont ??= new Font(BarForm.GlyphFamily, 14f);
            bigGlyphFont ??= new Font(BarForm.GlyphFamily, 20f);
            tabGlyphFont ??= new Font(BarForm.GlyphFamily, 12f);
        }

        // The panel is drawn in two cached layers — everything under the record, and everything over it —
        // which are only redrawn when something on them changes. A frame of the spinning record just stacks
        // them around the turned record, straight into the window's own bitmap (no copies per frame).
        Bitmap under, over;
        bool layersDirty = true;
        GraphicsPath shape;
        Rectangle content;
        Bitmap bg; string bgKey;

        IntPtr dib, dibBits, memDc;   // the window's bitmap (a DIB section), wrapped by dibBmp
        Bitmap dibBmp;

        /// <summary>Redraws the panel. <paramref name="full"/>: content changed (false: only the record turned).</summary>
        void Redraw(bool full = true)
        {
            if (!open) return;
            int w = screenRect.Width, h = screenRect.Height;
            if (!EnsureDib(w, h)) return;
            if (full || layersDirty || under == null) RenderLayers(w, h);
            using (var g = Graphics.FromImage(dibBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(under, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                if (tab == 0 && info != null)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SetClip(shape);
                    DrawMedia(g, content, 1);
                    g.ResetClip();
                }
                g.DrawImageUnscaled(over, 0, 0);
            }
            GdiFlush();
            Place();
        }

        void RenderLayers(int w, int h)
        {
            layersDirty = false;
            buttons.Clear();
            if (under == null || under.Width != w || under.Height != h)
            {
                under?.Dispose(); over?.Dispose();
                under = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                over = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            shape?.Dispose();
            // the outline: flares into the top edge, rounded bottom corners
            shape = LauncherShell.Shape(w, h, Flare, Radius);
            using (var flip = new Matrix(1, 0, 0, -1, 0, h)) shape.Transform(flip);
            var body = new Rectangle(Flare, 0, PanelW, h);
            content = Rectangle.FromLTRB(body.X + (int)(28 * s), TabH + (int)(14 * s), body.Right - (int)(28 * s), h - (int)(22 * s));

            using (var g = NewGraphics(under))
            {
                string key = backdrop.KeyFor(screenRect) + settings.ShowImage;
                if (key != bgKey) { bgKey = key; bg?.Dispose(); bg = settings.ShowImage ? backdrop.Render(screenRect) : null; }
                if (bg != null) using (var tb = new TextureBrush(bg, WrapMode.Clamp)) g.FillPath(tb, shape);
                else using (var b = new SolidBrush(T.Background)) g.FillPath(b, shape);
                using (var pen = new Pen(Color.FromArgb(90, T.Border))) g.DrawPath(pen, shape);
                g.SetClip(shape);
                DrawTabs(g, new Rectangle(body.X + (int)(24 * s), (int)(6 * s), body.Width - (int)(48 * s), TabH));
                if (tab == 0) DrawMedia(g, content, 0); else DrawPerformance(g, content);
            }
            using (var g = NewGraphics(over))
            {
                g.SetClip(shape);
                if (tab == 0) DrawMedia(g, content, 2);
            }
        }

        static Graphics NewGraphics(Bitmap b)
        {
            var g = Graphics.FromImage(b);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        bool EnsureDib(int w, int h)
        {
            if (dibBmp != null && dibBmp.Width == w && dibBmp.Height == h) return true;
            ReleaseBitmap();
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h /* top-down */, biPlanes = 1, biBitCount = 32 };
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                dib = CreateDIBSection(screenDc, ref bi, 0, out dibBits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return false;
                memDc = CreateCompatibleDC(screenDc);
                SelectObject(memDc, dib);
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
            dibBmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, dibBits);
            return true;
        }

        void ReleaseBitmap()
        {
            dibBmp?.Dispose(); dibBmp = null;
            if (memDc != IntPtr.Zero) { DeleteDC(memDc); memDc = IntPtr.Zero; }
            if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
            dibBits = IntPtr.Zero;
        }

        /// <summary>Shows the rendered picture at the current slide position (cheap: used for every animation frame).</summary>
        void Place()
        {
            if (memDc == IntPtr.Zero || dibBmp == null) return;
            int y = screenRect.Y - (int)Math.Round((1 - slide) * (screenRect.Height + topEdge + 2));
            var pos = new Rectangle(screenRect.X, y, screenRect.Width, screenRect.Height);
            if (Bounds != pos) Bounds = pos; // keep WinForms in step (it would otherwise resize the window)
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                var size = new SIZE { cx = dibBmp.Width, cy = dibBmp.Height };
                var src = new POINT();
                var dst = new POINT { x = pos.X, y = pos.Y };
                byte alpha = (byte)Math.Max(0, Math.Min(255, settings.Opacity * 255 / 100));
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();

        void DrawTabs(Graphics g, Rectangle r)
        {
            int n = tabs.Length, w = r.Width / n;
            for (int i = 0; i < n; i++)
            {
                var cell = new Rectangle(r.X + i * w, r.Y, w, r.Height);
                bool sel = i == tab, hot = cell.Contains(mouse);
                var col = sel ? T.Accent : hot ? T.Text : T.SubText;
                DrawText(g, tabGlyphs[i], tabGlyphFont, new Rectangle(cell.X, cell.Y + (int)(4 * s), cell.Width, (int)(24 * s)), col);
                DrawText(g, tabs[i], tabFont, new Rectangle(cell.X, cell.Y + (int)(28 * s), cell.Width, (int)(20 * s)), sel ? T.Text : col);
                if (sel)
                {
                    int lw = (int)(84 * s);
                    using var p = Ui.Round(new RectangleF(cell.X + (cell.Width - lw) / 2f, cell.Bottom - 3 * s, lw, 3 * s), 1.5f * s);
                    using var b = new SolidBrush(T.Accent);
                    g.FillPath(b, p);
                }
                int idx = i;
                buttons.Add((cell, () => { tab = idx; tabChosen = true; Redraw(); UpdateSpin(); }));
            }
            using var line = new Pen(Color.FromArgb(60, T.Text));
            g.DrawLine(line, r.X, r.Bottom, r.Right, r.Bottom);
        }

        // ---- Media ----

        /// <summary>Phase 0: under the record (glow), 1: the record itself, 2: over it (cover, text, controls).</summary>
        void DrawMedia(Graphics g, Rectangle r, int phase)
        {
            var i = info;
            int art = r.Height;
            var cover = new Rectangle(r.X, r.Y, art, art);
            if (i != null) EnsureVinyl(i, art);

            if (phase == 0 && i != null)
            {
                Color glow = vinylColours.Count > 0 ? vinylColours[0] : T.Accent;
                using (var gp = new GraphicsPath())
                {
                    var gr = RectangleF.Inflate(new RectangleF(cover.X, cover.Y, art * 1.9f, art), art * 0.6f, art * 0.5f);
                    gp.AddEllipse(gr);
                    using var gb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(70, glow), SurroundColors = new[] { Color.FromArgb(0, glow) } };
                    g.FillPath(gb, gp); // a soft glow in the cover's colour
                }
                return;
            }

            // the record slides out from behind the cover and spins while the song plays
            if (phase == 1)
            {
                if (i != null && vinyl != null)
                {
                    float slide = art * 0.46f * vinylOut;
                    var disc = new RectangleF(cover.X + slide + art * 0.03f, cover.Y + art * 0.03f, art * 0.94f, art * 0.94f);
                    var st = g.Save();
                    float dcx = disc.X + disc.Width / 2, dcy = disc.Y + disc.Height / 2;
                    g.TranslateTransform(dcx, dcy);
                    g.RotateTransform(vinylAngle);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(vinyl, new RectangleF(-disc.Width / 2, -disc.Height / 2, disc.Width, disc.Height));
                    g.Restore(st);
                    Vinyl.DrawSheen(g, disc);
                    // the sleeve casts a little shadow onto the record
                    // (the gradient is made a bit wider than the area it fills: GDI+ wraps gradients around at
                    //  their far edge, which drew a hard dark line through the record)
                    var sh = new RectangleF(cover.Right - 2, cover.Y, art * 0.09f, art);
                    using var sb = new LinearGradientBrush(RectangleF.Inflate(sh, 2, 0), Color.FromArgb(90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Horizontal)
                    { WrapMode = WrapMode.TileFlipX };
                    using var clip = new GraphicsPath(); clip.AddEllipse(disc);
                    var st2 = g.Save(); g.SetClip(clip, CombineMode.Intersect); g.FillRectangle(sb, sh); g.Restore(st2);
                }
                return;
            }
            if (phase != 2) return;

            using (var p = Ui.Round(cover, 14 * s))
            {
                if (i?.Thumbnail != null)
                {
                    var st = g.Save();
                    g.SetClip(p);
                    var src = i.Thumbnail;
                    int side = Math.Min(src.Width, src.Height);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, cover, new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                    g.Restore(st);
                }
                else
                {
                    using (var b = new SolidBrush(T.Surface)) g.FillPath(b, p);
                    DrawText(g, "\uE8D6", bigGlyphFont, cover, T.SubText);
                }
            }

            int x = cover.Right + (i != null ? (int)(art * 0.46f) : 0) + (int)(26 * s), w = r.Right - x;
            if (i == null)
            {
                DrawText(g, "Nothing is playing", titleFont, new Rectangle(x, r.Y + (int)(40 * s), w, (int)(30 * s)), T.Text, left: true);
                DrawText(g, "Play something in Spotify, your browser or any media app.", textFont, new Rectangle(x, r.Y + (int)(72 * s), w, (int)(24 * s)), T.SubText, left: true);
                return;
            }
            var accent = SongAccent();

            // title, artist and where it plays
            DrawText(g, i.Title, titleFont, new Rectangle(x, r.Y, w, (int)(30 * s)), T.Text, left: true);
            string app = AppName(i.AppId);
            int chipW = 0;
            if (app.Length > 0)
            {
                var sz = g.MeasureString(app, smallFont);
                chipW = (int)sz.Width + (int)(16 * s);
                var chip = new RectangleF(x + w - chipW, r.Y + 34 * s, chipW, 20 * s);
                using (var p = Ui.Round(chip, chip.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(45, accent))) g.FillPath(b, p);
                DrawText(g, app, smallFont, Rectangle.Round(chip), Ui.Mix(accent, T.Text, 0.35f));
            }
            DrawText(g, i.Artist, textFont, new Rectangle(x, r.Y + (int)(32 * s), w - chipW - (int)(8 * s), (int)(24 * s)), T.SubText, left: true);

            // progress as a waveform: the part already played in the song's colour
            var wave = new Rectangle(x, r.Y + (int)(70 * s), w, (int)(36 * s));
            DrawWave(g, wave, i, accent);

            // controls: previous / play-pause / next, centred under the waveform — a little to the left when
            // the app's own volume slider sits at the right end of the row
            bool hasVol = vol != null;
            int cy = r.Bottom - (int)(30 * s), cx = hasVol ? x + (int)(w * 0.33f) : x + w / 2;
            if (hasVol) DrawVolume(g, Rectangle.FromLTRB(x + w - (int)(124 * s), cy - (int)(16 * s), x + w, cy + (int)(16 * s)), accent);
            else { volBar = Rectangle.Empty; }
            int ps = (int)(58 * s), bs = (int)(42 * s), gap = (int)(78 * s);
            DrawButton(g, new Rectangle(cx - gap - bs / 2, cy - bs / 2, bs, bs), "", false, media.Previous, accent);
            DrawButton(g, new Rectangle(cx - ps / 2, cy - ps / 2, ps, ps), i.Playing ? "" : "", true,
                () => { media.PlayPause(); if (info != null) info.Playing = !info.Playing; Redraw(); UpdateSpin(); }, accent);
            DrawButton(g, new Rectangle(cx + gap - bs / 2, cy - bs / 2, bs, bs), "", false, media.Next, accent);
        }

        // ---- the playing app's own volume (like the Volume mixer: for Floorp, all of Floorp) ----
        string volExe;
        (float level, bool muted)? vol;
        Rectangle volBar = Rectangle.Empty;
        bool volDragging;
        WheelHook volWheel;

        void ReadVolume()
        {
            if (volDragging) return;
            volExe = info != null ? MediaApps.ExeName(info.AppId) : null;
            vol = volExe != null ? AppVolume.Get(volExe) : null;
        }

        void SetVolume(float level)
        {
            if (volExe == null || vol == null) return;
            level = Math.Max(0, Math.Min(1, level));
            AppVolume.Set(volExe, level);
            vol = (level, false);
            Redraw();
        }

        void DrawVolume(Graphics g, Rectangle r, Color accent)
        {
            var v = vol.Value;
            bool hot = r.Contains(mouse) || volDragging;
            // speaker: click to mute / unmute
            var glyphR = new Rectangle(r.X, r.Y, (int)(26 * s), r.Height);
            string glyph = v.muted || v.level <= 0.001f ? "\uE74F" : v.level < 0.34f ? "\uE993" : v.level < 0.67f ? "\uE994" : "\uE995";
            if (glyphR.Contains(mouse)) using (var b = new SolidBrush(Color.FromArgb(40, accent))) g.FillEllipse(b, Rectangle.Inflate(glyphR, -(int)(1 * s), (int)(-r.Height / 2 + 13 * s)));
            DrawText(g, glyph, glyphFont, glyphR, glyphR.Contains(mouse) ? Ui.Mix(accent, T.Text, 0.3f) : T.Text);
            buttons.Add((glyphR, () => { if (volExe != null && vol != null) { AppVolume.SetMute(volExe, !vol.Value.muted); vol = (vol.Value.level, !vol.Value.muted); } }));

            // the slider
            int bx = glyphR.Right + (int)(6 * s), bw = r.Right - bx - (int)(6 * s);
            float th = 4 * s, by = r.Y + r.Height / 2f - (int)(4 * s);
            volBar = new Rectangle(bx, (int)(by - 8 * s), bw, (int)(16 * s + th));
            float f = v.muted ? 0 : v.level;
            var rest = Ui.Mix(T.Surface, T.SubText, 0.32f);
            using (var p = Ui.Round(new RectangleF(bx, by, bw, th), th / 2)) using (var b = new SolidBrush(rest)) g.FillPath(b, p);
            if (f > 0) using (var p = Ui.Round(new RectangleF(bx, by, Math.Max(th, bw * f), th), th / 2)) using (var b = new SolidBrush(v.muted ? rest : accent)) g.FillPath(b, p);
            float kr = (hot ? 7 : 5.5f) * s, kx = bx + bw * f;
            using (var b = new SolidBrush(hot ? T.Text : Ui.Mix(accent, T.Text, 0.4f))) g.FillEllipse(b, kx - kr, by + th / 2 - kr, kr * 2, kr * 2);
            // whose volume it is
            string label = (AppName(info.AppId) is string n && n.Length > 0 ? n : volExe) + " · " + (v.muted ? "muted" : Math.Round(v.level * 100) + "%");
            DrawText(g, label, smallFont, new Rectangle(bx - (int)(40 * s), (int)(by + 9 * s), bw + (int)(40 * s), (int)(16 * s)), T.SubText, right: true);
        }

        float VolFrac(int mouseX) => volBar.Width <= 0 ? 0 : Math.Max(0, Math.Min(1, (mouseX - volBar.X) / (float)volBar.Width));

        /// <summary>A strong, readable colour from the cover for the controls (falls back to the theme's accent).</summary>
        Color SongAccent()
        {
            Color c = T.Accent;
            foreach (var v in vinylColours) if (v.GetSaturation() > 0.3f) { c = v; break; }
            // keep it bright enough on the dark panel (or dark enough on a light one)
            float b = c.GetBrightness();
            if (!T.IsLight && b < 0.55f) c = Ui.Mix(c, Color.White, Math.Min(0.6f, (0.55f - b) * 1.3f));
            if (T.IsLight && b > 0.5f) c = Ui.Mix(c, Color.Black, Math.Min(0.6f, (b - 0.5f) * 1.2f));
            return c;
        }

        void DrawWave(Graphics g, Rectangle r, MediaInfo i, Color accent)
        {
            trackRect = Rectangle.Empty;
            float bw = 3 * s, gapW = 2.2f * s;
            int bars = Math.Max(10, (int)((r.Width + gapW) / (bw + gapW)));
            float step = (r.Width + gapW) / bars;
            bool known = i.DurationTicks > 0;
            float f = !known ? 0 : seeking ? seekFrac : (float)Math.Min(1, i.Position.Ticks / (double)i.DurationTicks);
            if (known) trackRect = Rectangle.Inflate(r, 0, (int)(4 * s));
            float hover = known && !seeking && HotTrack() ? Math.Max(0, Math.Min(1, (mouse.X - r.X) / (float)r.Width)) : -1;
            var rest = Ui.Mix(T.Surface, T.SubText, 0.32f);
            double now = (DateTime.Now - DateTime.Today).TotalSeconds;
            int seed = 0; foreach (char ch in i.Key) seed = unchecked(seed * 31 + ch);
            var rnd = new Random(seed);
            float prev = 0.5f;
            for (int k = 0; k < bars; k++)
            {
                // a made-up waveform that stays the same for the song (smoothed random)
                float target = 0.22f + (float)rnd.NextDouble() * 0.78f;
                prev = prev * 0.45f + target * 0.55f;
                float amp = prev;
                if (!known) // no length from the app: a gentle live equaliser instead
                    amp = i.Playing ? 0.25f + 0.6f * (float)(0.5 + 0.5 * Math.Sin(now * 5.2 + k * 0.55) * Math.Sin(now * 2.1 + k * 0.21)) : 0.18f;
                float h = Math.Max(3 * s, r.Height * amp), bx = r.X + k * step;
                var bar = new RectangleF(bx, r.Y + (r.Height - h) / 2, bw, h);
                float pos = (k + 0.5f) / bars;
                Color col = !known ? Color.FromArgb(i.Playing ? 200 : 90, accent)
                          : pos <= f ? accent
                          : hover >= 0 && pos <= hover ? Ui.Mix(rest, accent, 0.45f) // preview of where a click would jump
                          : rest;
                using var p = Ui.Round(bar, bw / 2);
                using var b = new SolidBrush(col);
                g.FillPath(b, p);
            }
            if (known)
            {
                float px = r.X + r.Width * f; // playhead
                using (var b = new SolidBrush(T.Text)) g.FillRectangle(b, px - 1 * s, r.Y - 2 * s, 2 * s, r.Height + 4 * s);
                var pos = seeking ? TimeSpan.FromTicks((long)(i.DurationTicks * (double)seekFrac)) : i.Position;
                var total = TimeSpan.FromTicks(i.DurationTicks);
                DrawText(g, Time(pos), smallFont, new Rectangle(r.X, r.Bottom + (int)(4 * s), (int)(80 * s), (int)(18 * s)), T.Text, left: true);
                DrawText(g, "-" + Time(total - pos), smallFont, new Rectangle(r.Right - (int)(80 * s), r.Bottom + (int)(4 * s), (int)(80 * s), (int)(18 * s)), T.SubText, right: true);
                if (hover >= 0) // where a click would jump to
                {
                    var at = TimeSpan.FromTicks((long)(i.DurationTicks * (double)hover));
                    DrawText(g, Time(at), smallFont, new Rectangle((int)(r.X + r.Width * hover) - (int)(40 * s), r.Bottom + (int)(4 * s), (int)(80 * s), (int)(18 * s)), accent);
                }
            }
            else
            {
                // no length from the app: still show how far in we are (WispR keeps its own clock)
                DrawText(g, Time(i.Position), smallFont, new Rectangle(r.X, r.Bottom + (int)(4 * s), (int)(80 * s), (int)(18 * s)), T.Text, left: true);
                DrawText(g, "length not reported by the player", smallFont, new Rectangle(r.Right - (int)(260 * s), r.Bottom + (int)(4 * s), (int)(260 * s), (int)(18 * s)), T.SubText, right: true);
            }
        }

        void DrawButton(Graphics g, Rectangle r, string glyph, bool primary, Action click, Color accent)
        {
            bool hot = r.Contains(mouse);
            if (primary)
            {
                // the play button: the song's colour, with a soft glow
                var glowR = RectangleF.Inflate(r, 10 * s, 10 * s);
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(glowR);
                    using var gb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(hot ? 120 : 80, accent), SurroundColors = new[] { Color.FromArgb(0, accent) } };
                    g.FillPath(gb, gp);
                }
                using (var b = new SolidBrush(hot ? Ui.Mix(accent, Color.White, 0.15f) : accent)) g.FillEllipse(b, r);
                DrawText(g, glyph, bigGlyphFont, r, Ui.OnAccent(accent));
            }
            else
            {
                if (hot) using (var b = new SolidBrush(Color.FromArgb(50, accent))) g.FillEllipse(b, r);
                DrawText(g, glyph, glyphFont, r, hot ? Ui.Mix(accent, T.Text, 0.3f) : T.Text);
            }
            buttons.Add((r, click));
        }

        bool HotTrack() => trackRect.Contains(mouse);

        // ---- the spinning record ----
        Bitmap vinyl; string vinylKey; List<Color> vinylColours = new List<Color>();
        float vinylAngle, vinylOut;
        const string SpinKey = "top-panel-spin";
        DateTime lastSpin = DateTime.Now, lastFull = DateTime.MinValue;

        void EnsureVinyl(MediaInfo i, int size)
        {
            string key = i.Key + "|" + size + "|" + (i.Thumbnail?.GetHashCode() ?? 0);
            if (key == vinylKey) return;
            vinylKey = key;
            vinyl?.Dispose();
            try
            {
                vinylColours = Vinyl.Colours(i.Thumbnail);
                vinyl = Vinyl.Render(i.Thumbnail, i.Key, size);
            }
            catch (Exception ex) { vinyl = null; Log.Error("Vinyl", ex); }
        }

        /// <summary>
        /// Keeps the record turning (and sliding out / in) while the Media tab is open. Runs on the frame
        /// clock but at most ~60 times a second (it's idle motion: smooth, but no need to burn more), and
        /// only the record is redrawn — the rest of the panel comes from the cached layers.
        /// </summary>
        bool SpinFrame()
        {
            if (!open || tab != 0 || info == null) return false;
            var now = DateTime.Now;
            if ((now - lastSpin).TotalMilliseconds < 15) return true; // faster screens: skip every other frame
            float dt = (float)Math.Min(0.1, (now - lastSpin).TotalSeconds);
            lastSpin = now;
            float target = info.Playing ? 1f : 0.5f;
            bool moving = Anim.Approach(ref vinylOut, target, dt * 2.2f);
            if (info.Playing && settings.VinylRpm > 0) { vinylAngle = (vinylAngle + dt * settings.VinylRpm * 6f) % 360f; moving = true; } // rpm → degrees per second
            // the live equaliser (no song length) changes the waveform itself: redraw the layers ~30×/s
            bool eq = info.Playing && !(info.DurationTicks > 0);
            bool full = eq && (now - lastFull).TotalMilliseconds >= 33;
            if (full) lastFull = now;
            if (eq) moving = true;
            if (!moving) return false;
            Redraw(full);
            return true;
        }

        void UpdateSpin()
        {
            if (open && tab == 0 && info != null && !Anim.FramesRunning(SpinKey)) { lastSpin = DateTime.Now; Anim.Frames(SpinKey, SpinFrame); }
        }

        // ---- Performance ----

        void DrawPerformance(Graphics g, Rectangle r)
        {
            int n = 3, cw = r.Width / n;
            string F(double v) => v >= 100 ? v.ToString("0") : v.ToString("0.0");
            Ring(g, new Rectangle(r.X, r.Y, cw, r.Height), cpu < 0 ? 0 : cpu / 100f,
                cpu < 0 ? "–" : Math.Round(cpu) + "%", "CPU", Environment.ProcessorCount.ToString(), "Threads", Heat(cpu));
            Ring(g, new Rectangle(r.X + cw, r.Y, cw, r.Height), memPct < 0 ? 0 : memPct / 100f,
                memPct < 0 ? "–" : F(memUsedGb) + " GB", "Memory", memPct < 0 ? "–" : Math.Round(memPct) + "%", "of " + F(memTotalGb) + " GB", Heat(memPct));
            // GPU temperature on a 0–100 °C scale; warmer colours from 80 °C (edge) on
            Ring(g, new Rectangle(r.X + cw * 2, r.Y, cw, r.Height), gpu.Ok ? gpu.Temp / 100f : 0,
                gpu.Ok ? gpu.Temp + "°C" : "–", gpu.Ok ? "GPU temp" : "GPU temp unavailable",
                gpu.Ok && gpu.Load >= 0 ? gpu.Load + "%" : gpu.Ok && gpu.Hotspot > 0 ? gpu.Hotspot + "°C" : "",
                gpu.Ok && gpu.Load >= 0 ? "Usage" : gpu.Ok && gpu.Hotspot > 0 ? "Hotspot" : "",
                !gpu.Ok ? T.SubText : gpu.Temp >= 90 ? Color.FromArgb(239, 83, 80) : gpu.Temp >= 80 ? Color.FromArgb(255, 167, 38) : T.Accent);
        }

        /// <summary>Accent normally; warmer as it gets busy.</summary>
        Color Heat(float pct) => pct >= 90 ? Color.FromArgb(239, 83, 80) : pct >= 75 ? Color.FromArgb(255, 167, 38) : T.Accent;

        void Ring(Graphics g, Rectangle cell, float value, string big, string label, string side, string sideLabel, Color color)
        {
            float d = Math.Min(cell.Height, cell.Width * 0.78f), thick = 9 * s;
            var c = new RectangleF(cell.X + (cell.Width - d) / 2, cell.Y + (cell.Height - d) / 2, d, d);
            var arc = RectangleF.Inflate(c, -thick / 2, -thick / 2);
            const float start = 135, sweep = 270; // open at the bottom
            using (var pen = new Pen(Color.FromArgb(70, color), thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, start, sweep);
            float v = Math.Max(0.005f, Math.Min(1, value));
            using (var pen = new Pen(color, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, start, sweep * v);
            var center = new Rectangle((int)c.X, (int)(c.Y + c.Height / 2 - 30 * s), (int)c.Width, (int)(36 * s));
            DrawText(g, big, bigFont, center, T.Text);
            DrawText(g, label, smallFont, new Rectangle((int)c.X, center.Bottom, (int)c.Width, (int)(18 * s)), T.SubText);
            // the second value sits centred in the ring's opening at the bottom
            if (!string.IsNullOrEmpty(side))
            {
                var sv = new Rectangle((int)c.X, (int)(c.Bottom - 34 * s), (int)c.Width, (int)(20 * s));
                DrawText(g, side, sideFont, sv, T.Text);
                DrawText(g, sideLabel, smallFont, new Rectangle(sv.X, sv.Bottom - (int)(2 * s), sv.Width, (int)(16 * s)), T.SubText);
            }
        }

        // ---------- text helpers (GDI+ text: it keeps the transparency right) ----------

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

        static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        static string AppName(string id) => MediaApps.Name(id);

        // ---------- mouse ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            if (seeking) { seekFrac = Frac(e.X); Redraw(); return; }
            if (volDragging) { SetVolume(VolFrac(e.X)); return; }
            int hot = buttons.FindIndex(b => b.r.Contains(mouse));
            bool track = tab == 0 && (HotTrack() || volBar.Contains(mouse));
            Cursor = hot >= 0 || track ? Cursors.Hand : Cursors.Default;
            int hoverKey = track ? 1000 : hot;
            if (hoverKey != lastHover || track) { lastHover = hoverKey; Redraw(); } // the waveform previews where a click lands
        }

        int lastHover = -1;
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouse = new Point(-1, -1); lastHover = -1; Redraw(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && tab == 0 && info != null && info.DurationTicks > 0 && trackRect.Contains(e.Location))
            {
                seeking = true; seekFrac = Frac(e.X); Capture = true; Redraw();
            }
            else if (e.Button == MouseButtons.Left && tab == 0 && vol != null && volBar.Contains(e.Location))
            {
                volDragging = true; Capture = true; SetVolume(VolFrac(e.X));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (volDragging) { volDragging = false; Capture = false; Redraw(); return; }
            if (seeking)
            {
                seeking = false; Capture = false;
                if (info != null && info.DurationTicks > 0)
                {
                    var target = TimeSpan.FromTicks((long)(info.DurationTicks * (double)seekFrac));
                    media.Seek(target);
                    seekTargetTicks = target.Ticks; seekAt = DateTime.UtcNow; seekHoldUntil = seekAt.AddSeconds(1.5);
                    info.PositionTicks = seekTargetTicks; info.PositionAtUtc = seekAt;
                }
                Redraw();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            foreach (var (r, click) in buttons.ToArray())
                if (r.Contains(e.Location)) { click(); Redraw(); return; }
        }

        float Frac(int x) => trackRect.Width <= 0 ? 0 : Math.Max(0, Math.Min(1, (x - trackRect.X) / (float)trackRect.Width));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var n in notches.Values) n.Dispose(); volWheel?.Dispose(); watch.Dispose(); tick.Dispose(); Anim.StopFrames(SpinKey); media.Dispose(); vinyl?.Dispose();
                ReleaseBitmap(); under?.Dispose(); over?.Dispose(); shape?.Dispose(); bg?.Dispose();
                glyphFont?.Dispose(); bigGlyphFont?.Dispose(); tabGlyphFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- native ----------

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    }
}
