using System;
using System.Collections.Generic;
using System.Linq;
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
        int PickerH => (int)(590 * s);                       // the record looks picker takes much more room…
        int PickerW => (int)(1100 * s);                      // …taller and wider
        float expand;                                        // 0 = normal size, 1 = picker size
        int CurrentH => (int)Math.Round(PanelH + (PickerH - PanelH) * expand);
        int CurrentW => (int)Math.Round(PanelW + (PickerW - PanelW) * expand);
        int MaxW => PickerW + Flare * 2;
        int panelCenterX;

        /// <summary>The panel's rectangle at the current size, centred where it hangs.</summary>
        void SizeToExpand()
        {
            int w = CurrentW + Flare * 2;
            screenRect = new Rectangle(panelCenterX - w / 2, screenRect.Y, w, CurrentH);
        }
        int Flare => Ui.CornerPx(s);
        int Radius => Ui.CornerPx(s);
        int TabH => (int)(58 * s);
        Rectangle screenRect;      // where the window is when fully open (screen coordinates)
        int topEdge;
        float slide;               // 0 = hidden above the edge, 1 = fully down
        bool open, closing;

        // ---- state ----
        int tab;                   // 0 = Media, 1 = Performance, 2 = LumenR (only when it's installed)
        bool tabChosen;            // the user picked a tab during this session
        readonly string[] tabs = { "Media", "Performance", "LumenR" };
        readonly string[] tabGlyphs = { "\uE8D6", "\uE9D9", "\uE8B2" };
        LumenR.State lumen;        // what LumenR reports (null: not installed)
        /// <summary>The tabs shown, in order: LumenR sits between Media and Performance when it's there.</summary>
        int[] VisibleTabs => lumen != null ? new[] { 0, 2, 1 } : new[] { 0, 1 };
        Point mouse = new Point(-1, -1);
        bool seeking; float seekFrac;
        DateTime seekHoldUntil; long seekTargetTicks; DateTime seekAt;

        MediaInfo info;
        DateTime missingSince = DateTime.MinValue;
        readonly MediaService media = new MediaService();

        float cpu = -1; long lastIdle, lastTotal;
        double memUsedGb, memTotalGb; float memPct = -1;
        GpuSensor.Reading gpu;
        readonly List<float> cpuHistory = new List<float>(), memHistory = new List<float>(), gpuHistory = new List<float>();
        DateTime lastSample = DateTime.MinValue;
        const int HistoryLength = 40;
        // what the gauges show right now: they glide to each new reading instead of jumping
        float shownCpu = -1, shownMem = -1, shownGpu = -1;

        // hit areas (client coordinates), filled while drawing
        readonly List<(Rectangle r, Action click)> buttons = new List<(Rectangle, Action)>();
        Rectangle trackRect = Rectangle.Empty;

        readonly Timer watch = new Timer { Interval = 40 };   // edge trigger / leave detection
        readonly Timer tick = new Timer { Interval = 1000 };  // readings + progress while open
        DateTime atEdgeSince = DateTime.MinValue, outsideSince = DateTime.MinValue;

        static readonly Font tabFont = new Font("Segoe UI", 10f), bigFont = new Font("Segoe UI Light", 26f),
            titleFont = new Font("Segoe UI Semibold", 15f), textFont = new Font("Segoe UI", 10.5f),
            smallFont = new Font("Segoe UI", 8.5f), sideFont = new Font("Segoe UI Semibold", 11f),
            valueFont = new Font("Segoe UI Semibold", 19f), labelFont = new Font("Segoe UI Semibold", 8.5f);
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
            tick.Tick += (o, e) =>
            {
                lumen = LumenR.Read(); // cheap: re-read only when LumenR wrote something new
                if (tab == 2 && lumen == null) tab = 0;
                Sample(); if (Anim.FramesRunning(SpinKey)) layersDirty = true; else Redraw(); UpdateSpin(); };
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
            if (!open && (DateTime.Now - lastSample).TotalSeconds >= 2) Sample(); // keeps the graphs filled (cheap)
            // far from the top edge nothing can happen soon: look less often (40 ms only when it matters)
            if (!open)
            {
                int want = c.Y - Screen.FromPoint(c).Bounds.Top > (int)(220 * s) ? 150 : 40;
                if (watch.Interval != want) watch.Interval = want;
            }
            else if (watch.Interval != 40) watch.Interval = 40;
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
                if ((DateTime.Now - atEdgeSince).TotalMilliseconds >= settings.TopPanelDelay) { atEdgeSince = DateTime.MinValue; OpenPanel(scr, edge.Value); }
                return;
            }
            if (closing || seeking) return;
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
            expand = 0;
            panelCenterX = scr.Bounds.X + scr.Bounds.Width / 2;
            screenRect = new Rectangle(panelCenterX - w / 2, edge, w, CurrentH);
            open = true; closing = false; outsideSince = DateTime.MinValue;
            lumen = LumenR.Read();
            switch (settings.TopPanelTab)
            {
                case "Media": tab = 0; break;
                case "Performance": tab = 1; break;
                case "LumenR": tab = 2; break;
                case "Last": break; // where you left it
                default:
                    // something playing → Media first; an episode in LumenR → LumenR
                    if (!tabChosen) tab = LumenR.Playing != null ? 2 : info != null ? 0 : 1;
                    break;
            }
            if (tab == 2 && lumen == null) tab = info != null ? 0 : 1;
            media.Paused = false;
            Sample();
            BuildFonts();
            bgKey = null;
            Redraw();
            if (!Visible) Show();
            KeepOnTop();
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
            void Done()
            {
                open = closing = false;
                slide = 0;
                Hide();
                media.Paused = true;
                ReleaseBitmap();
                recordsOpen = false;
                expand = 0;
                Anim.Stop("top-panel-picker");
                foreach (var b in lookThumbs.Values) b.Dispose();
                lookThumbs.Clear(); lookThumbsKey = null;
                foreach (var b in posters.Values) b?.Dispose();
                posters.Clear();
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
                if ((DateTime.Now - lastSample).TotalSeconds >= 0.9)
                {
                    lastSample = DateTime.Now;
                    Push(cpuHistory, cpu);
                    Push(memHistory, memPct);
                    Push(gpuHistory, gpu.Ok ? gpu.Temp : -1);
                }
            }
            catch { }
            if (open && tab == 1 && !Anim.FramesRunning(GaugeKey)) Anim.Frames(GaugeKey, GaugeFrame);
        }

        static void Push(List<float> list, float v)
        {
            if (v < 0) return;
            list.Add(v);
            if (list.Count > HistoryLength) list.RemoveAt(0);
        }

        // ---- the gauges glide to new readings (≤60 redraws a second, only while moving) ----
        const string GaugeKey = "top-panel-gauges";
        DateTime lastGauge;

        bool GaugeFrame()
        {
            if (!open || tab != 1) return false;
            var now = DateTime.Now;
            if ((now - lastGauge).TotalMilliseconds < 15) return true;
            float dt = (float)Math.Min(0.1, (now - lastGauge).TotalSeconds);
            lastGauge = now;
            float k = 1 - (float)Math.Exp(-dt * 9); // ~0.3 s to settle
            bool moving = false;
            void Glide(ref float shown, float target)
            {
                if (target < 0) { shown = -1; return; }
                if (shown < 0) { shown = target; moving = true; return; }
                float d = target - shown;
                if (Math.Abs(d) < 0.05f) { if (shown != target) { shown = target; moving = true; } return; }
                shown += d * k; moving = true;
            }
            Glide(ref shownCpu, cpu);
            Glide(ref shownMem, memPct);
            Glide(ref shownGpu, gpu.Ok ? gpu.Temp : -1);
            if (moving) Redraw();
            return moving;
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
            info = i;
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
            if (!EnsureDib(Math.Max(w, MaxW), Math.Max(h, PickerH))) return;
            if (full || layersDirty || under == null) RenderLayers(w, h);
            using (var g = Graphics.FromImage(dibBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(under, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                if (tab == 0 && info != null && !recordsOpen)
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
            int hAlloc = Math.Max(h, PickerH), wAlloc = Math.Max(w, MaxW); // big enough for the picker: growing doesn't reallocate per frame
            if (under == null || under.Width != wAlloc || under.Height != hAlloc)
            {
                under?.Dispose(); over?.Dispose();
                under = new Bitmap(wAlloc, hAlloc, PixelFormat.Format32bppPArgb);
                over = new Bitmap(wAlloc, hAlloc, PixelFormat.Format32bppPArgb);
            }
            shape?.Dispose();
            // the outline: flares into the top edge, rounded bottom corners
            shape = LauncherShell.Shape(w, h, Flare, Radius);
            using (var flip = new Matrix(1, 0, 0, -1, 0, h)) shape.Transform(flip);
            var body = new Rectangle(Flare, 0, w - Flare * 2, h);
            content = Rectangle.FromLTRB(body.X + (int)(28 * s), TabH + (int)(14 * s), body.Right - (int)(28 * s), h - (int)(22 * s));

            using (var g = NewGraphics(under))
            {
                // the background picture is made once for the biggest size the panel can grow to, and only
                // shifted while it grows (rendering it per frame would be costly)
                var bgArea = new Rectangle(panelCenterX - MaxW / 2, screenRect.Y, MaxW, Math.Max(PickerH, h));
                string key = backdrop.KeyFor(bgArea) + settings.ShowImage;
                if (key != bgKey) { bgKey = key; bg?.Dispose(); bg = settings.ShowImage ? backdrop.Render(bgArea) : null; }
                if (bg != null)
                    using (var tb = new TextureBrush(bg, WrapMode.Clamp))
                    {
                        tb.TranslateTransform(bgArea.X - screenRect.X, 0);
                        g.FillPath(tb, shape);
                    }
                else using (var b = new SolidBrush(T.Background)) g.FillPath(b, shape);
                using (var pen = new Pen(Color.FromArgb(90, T.Border))) g.DrawPath(pen, shape);
                g.SetClip(shape);
                DrawTabs(g, new Rectangle(body.X + (int)(24 * s), (int)(6 * s), body.Width - (int)(48 * s), TabH));
                if (tab == 0 && recordsOpen) DrawRecords(g, content);
                else if (tab == 0) DrawMedia(g, content, 0);
                else if (tab == 2) DrawLumen(g, content);
                else DrawPerformance(g, content);
            }
            using (var g = NewGraphics(over))
            {
                g.SetClip(shape);
                if (tab == 0 && !recordsOpen) DrawMedia(g, content, 2);
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
                var size = new SIZE { cx = Math.Min(dibBmp.Width, screenRect.Width), cy = Math.Min(dibBmp.Height, screenRect.Height) }; // only the panel's current size
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
            var order = VisibleTabs;
            int n = order.Length, w = r.Width / n;
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                var cell = new Rectangle(r.X + k * w, r.Y, w, r.Height);
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
                buttons.Add((cell, () => { tab = idx; tabChosen = true; if (recordsOpen) ShowPicker(false); Redraw(); UpdateSpin(); }));
            }
            using var line = new Pen(Color.FromArgb(60, T.Text));
            g.DrawLine(line, r.X, r.Bottom, r.Right, r.Bottom);

            // the record looks picker: a small icon at the end of the tab row, on the Media tab
            if (tab == 0)
            {
                int bs = (int)(30 * s);
                var ir = new Rectangle(r.Right - bs, r.Y + (r.Height - bs) / 2 - (int)(2 * s), bs, bs);
                bool hot = ir.Contains(mouse);
                if (hot || recordsOpen) using (var b = new SolidBrush(Color.FromArgb(recordsOpen ? 70 : 40, T.Accent))) g.FillEllipse(b, ir);
                DrawText(g, "\uE790", tabGlyphFont, ir, recordsOpen || hot ? T.Accent : T.SubText); // palette
                buttons.Insert(0, (ir, () => ShowPicker(!recordsOpen)));
            }
        }

        // ---- the record looks picker ----
        bool recordsOpen;
        readonly Dictionary<Vinyl.Style, Bitmap> lookThumbs = new Dictionary<Vinyl.Style, Bitmap>();
        string lookThumbsKey;
        static readonly Font lookFont = new Font("Segoe UI", 9.5f);

        /// <summary>All record looks in the song's colours; click one to keep it out of the per-song mix (or let it back in).</summary>
        void DrawRecords(Graphics g, Rectangle r)
        {
            var all = (Vinyl.Style[])Enum.GetValues(typeof(Vinyl.Style));
            int hiddenCount = all.Count(a => Vinyl.Hidden.Contains(a));

            // header: back, what this is, and "show all"
            int hh = (int)(26 * s);
            var back = new Rectangle(r.X, r.Y, (int)(26 * s), hh);
            if (back.Contains(mouse)) using (var b = new SolidBrush(Color.FromArgb(40, T.Accent))) g.FillEllipse(b, back);
            DrawText(g, "\uE72B", tabGlyphFont, back, T.Text); // back arrow
            buttons.Add((back, () => ShowPicker(false)));
            DrawText(g, "Record looks", titleFont, new Rectangle(back.Right + (int)(6 * s), r.Y - (int)(2 * s), (int)(200 * s), hh), T.Text, left: true);
            string note = Vinyl.Fixed.HasValue ? "Settings pick one look for every song right now"
                        : hiddenCount == 0 ? "Click a record to keep it out of the mix" : (all.Length - hiddenCount) + " of " + all.Length + " in the mix · click to switch one off or on";
            DrawText(g, note, smallFont, new Rectangle(back.Right + (int)(160 * s), r.Y, r.Width - (int)(300 * s), hh), T.SubText, left: true);
            if (hiddenCount > 0)
            {
                var allR = new Rectangle(r.Right + (PickerW - CurrentW) - (int)(80 * s), r.Y + (int)(2 * s), (int)(80 * s), hh - (int)(4 * s));
                bool hot = allR.Contains(mouse);
                using (var p = Ui.Round(allR, allR.Height / 2f)) using (var b = new SolidBrush(Color.FromArgb(hot ? 80 : 45, T.Accent))) g.FillPath(b, p);
                DrawText(g, "Show all", smallFont, allR, hot ? T.Text : Ui.Mix(T.Accent, T.Text, 0.35f));
                buttons.Insert(0, (allR, () => { settings.VinylHidden.Clear(); settings.NotifyChanged(); Redraw(); }));
            }

            // the grid: three rows of eight, laid out for the picker's full height (it shows as the panel grows)
            int perRow = 8, rows = (all.Length + perRow - 1) / perRow;
            int fullBottom = r.Bottom + (PickerH - CurrentH), fullRight = r.Right + (PickerW - CurrentW);
            var grid = Rectangle.FromLTRB(r.X, r.Y + hh + (int)(16 * s), fullRight, fullBottom);
            float cw = grid.Width / (float)perRow, ch = grid.Height / (float)rows;
            int d = (int)Math.Min(cw - 20 * s, ch - 30 * s);
            EnsureLookThumbs(d);
            for (int k = 0; k < all.Length; k++)
            {
                var look = all[k];
                bool off = Vinyl.Hidden.Contains(look);
                var cell = new Rectangle((int)(grid.X + (k % perRow) * cw), (int)(grid.Y + (k / perRow) * ch), (int)cw, (int)ch);
                var discR = new Rectangle(cell.X + (cell.Width - d) / 2, cell.Y, d, d);
                bool hot = cell.Contains(mouse);
                if (lookThumbs.TryGetValue(look, out var thumb))
                {
                    if (off)
                    {
                        using var ia = new ImageAttributes();
                        ia.SetColorMatrix(new ColorMatrix(new[]
                        {   // switched off: faded and grey
                            new[] { .21f, .21f, .21f, 0, 0 }, new[] { .55f, .55f, .55f, 0, 0 }, new[] { .07f, .07f, .07f, 0, 0 },
                            new[] { 0f, 0, 0, 0.35f, 0 }, new[] { 0f, 0, 0, 0, 1 },
                        }));
                        g.DrawImage(thumb, discR, 0, 0, thumb.Width, thumb.Height, GraphicsUnit.Pixel, ia);
                    }
                    else g.DrawImage(thumb, discR);
                }
                if (hot) using (var pen = new Pen(T.Accent, 2 * s)) g.DrawEllipse(pen, Rectangle.Inflate(discR, (int)(2 * s), (int)(2 * s)));
                if (off) // a small "off" mark
                {
                    var mark = new RectangleF(discR.Right - 12 * s, discR.Y, 13 * s, 13 * s);
                    using (var b = new SolidBrush(T.Background)) g.FillEllipse(b, mark);
                    using (var pen = new Pen(T.SubText, 1.6f * s))
                    {
                        float q = 3.5f * s;
                        g.DrawLine(pen, mark.X + q, mark.Y + q, mark.Right - q, mark.Bottom - q);
                        g.DrawLine(pen, mark.Right - q, mark.Y + q, mark.X + q, mark.Bottom - q);
                    }
                }
                DrawText(g, look.ToString(), lookFont, new Rectangle(cell.X, discR.Bottom + (int)(3 * s), cell.Width, (int)(16 * s)), off ? Color.FromArgb(130, T.SubText) : T.SubText);
                var lk = look;
                buttons.Add((cell, () => ToggleLook(lk)));
            }
        }

        /// <summary>Opens or closes the picker: the panel grows taller for it, and shrinks back to the player.</summary>
        void ShowPicker(bool on)
        {
            if (on) recordsOpen = true; // the picker shows while it grows; closing keeps it until shrunk
            float from = expand, to = on ? 1 : 0;
            Anim.Run("top-panel-picker", 240, e =>
            {
                if (!open) return;
                expand = (float)Anim.Lerp(from, to, e);
                SizeToExpand();
                Redraw();
            }, () =>
            {
                if (!on) { recordsOpen = false; expand = 0; SizeToExpand(); Redraw(); UpdateSpin(); }
            }, on ? (Func<double, double>)Anim.OutCubic : Anim.InCubic);
        }

        void ToggleLook(Vinyl.Style look)
        {
            string name = look.ToString();
            if (settings.VinylHidden.Contains(name)) settings.VinylHidden.Remove(name);
            else
            {
                int total = Enum.GetValues(typeof(Vinyl.Style)).Length;
                if (settings.VinylHidden.Count >= total - 1) return; // at least one look has to stay
                settings.VinylHidden.Add(name);
            }
            settings.NotifyChanged(); // saved; the playing song gets a new record if its look was switched off
            Redraw();
        }

        /// <summary>Small previews of every look in the playing song's colours (made once per song and size).</summary>
        void EnsureLookThumbs(int d)
        {
            string key = (info?.Key ?? "") + "|" + d + "|" + (info?.Thumbnail?.GetHashCode() ?? 0);
            if (key == lookThumbsKey) return;
            lookThumbsKey = key;
            foreach (var b in lookThumbs.Values) b.Dispose();
            lookThumbs.Clear();
            foreach (Vinyl.Style look in Enum.GetValues(typeof(Vinyl.Style)))
                try { lookThumbs[look] = Vinyl.Render(info?.Thumbnail, "preview", Math.Max(16, d), look); } catch { }
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

            // controls: previous / play-pause / next, centred under the waveform
            int cy = r.Bottom - (int)(30 * s), cx = x + w / 2;
            int ps = (int)(58 * s), bs = (int)(42 * s), gap = (int)(78 * s);
            DrawButton(g, new Rectangle(cx - gap - bs / 2, cy - bs / 2, bs, bs), "", false, media.Previous, accent);
            DrawButton(g, new Rectangle(cx - ps / 2, cy - ps / 2, ps, ps), i.Playing ? "" : "", true,
                () => { media.PlayPause(); if (info != null) info.Playing = !info.Playing; Redraw(); UpdateSpin(); }, accent);
            DrawButton(g, new Rectangle(cx + gap - bs / 2, cy - bs / 2, bs, bs), "", false, media.Next, accent);
        }

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
            string key = i.Key + "|" + size + "|" + (i.Thumbnail?.GetHashCode() ?? 0) + "|" + Vinyl.ChoiceKey;
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
            if (!open || tab != 0 || info == null || recordsOpen) return false;
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
            if (open && tab == 0 && info != null && !recordsOpen && !Anim.FramesRunning(SpinKey)) { lastSpin = DateTime.Now; Anim.Frames(SpinKey, SpinFrame); }
        }

        // ---- LumenR ----

        readonly Dictionary<string, Bitmap> posters = new Dictionary<string, Bitmap>();

        /// <summary>
        /// The LumenR add-on: what's playing there (if anything) and the shows you're in the middle of,
        /// as posters. A poster continues that show (next episode, or resume); "Open LumenR" opens the app.
        /// </summary>
        void DrawLumen(Graphics g, Rectangle r)
        {
            var st = lumen;
            var now = LumenR.Playing;
            int headH = (int)(24 * s), gap = (int)(14 * s);

            // header: what this is, and a way into the app
            DrawText(g, now != null ? "NOW WATCHING" : "CONTINUE WATCHING", labelFont, new Rectangle(r.X, r.Y, r.Width / 2, headH), T.SubText, left: true);
            string openText = "Open LumenR  \u203A";
            var openSize = g.MeasureString(openText, textFont);
            var openR = new Rectangle(r.Right - (int)openSize.Width - (int)(14 * s), r.Y - (int)(3 * s), (int)openSize.Width + (int)(14 * s), headH + (int)(6 * s));
            bool openHot = openR.Contains(mouse);
            if (openHot) using (var p = Ui.Round(openR, openR.Height / 2f)) using (var b = new SolidBrush(Color.FromArgb(50, T.Accent))) g.FillPath(b, p);
            DrawText(g, openText, textFont, openR, openHot ? T.Accent : T.Text);
            buttons.Add((openR, () => { LumenR.Open(); ClosePanel(false); }));

            var row = new Rectangle(r.X, r.Y + headH + (int)(10 * s), r.Width, r.Bottom - (r.Y + headH + (int)(10 * s)));
            int textH = (int)(36 * s);
            int ph = row.Height - textH, pw = ph * 2 / 3;
            int x = row.X;

            if (now != null)
            {
                // the episode playing: its poster, what it is, how far along
                int cardW = Math.Min(row.Width, pw + (int)(300 * s));
                var card = new Rectangle(x, row.Y, cardW, row.Height);
                bool hot = card.Contains(mouse);
                using (var p = Ui.Round(card, 14 * s))
                {
                    using (var b = new SolidBrush(Color.FromArgb(T.IsLight ? 150 : 110, hot ? T.Selection : T.Surface))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(hot ? 120 : 40, hot ? T.Accent : T.Text))) g.DrawPath(pen, p);
                }
                int pad = (int)(10 * s);
                var pr = new Rectangle(card.X + pad, card.Y + pad, (card.Height - pad * 2) * 2 / 3, card.Height - pad * 2);
                DrawPoster(g, pr, now.Poster, now.Title, 0);
                int tx = pr.Right + (int)(16 * s), tw = card.Right - tx - (int)(16 * s);
                int ty = card.Y + (int)(18 * s);
                DrawText(g, now.Title, titleFont, new Rectangle(tx, ty, tw, (int)(30 * s)), T.Text, left: true);
                string ep = now.Episode + (now.EpisodeTitle.Length > 0 ? (now.Episode.Length > 0 ? "  ·  " : "") + now.EpisodeTitle : "");
                if (ep.Length > 0) DrawText(g, ep, textFont, new Rectangle(tx, ty + (int)(32 * s), tw, (int)(22 * s)), T.SubText, left: true);
                // progress
                int by = card.Bottom - (int)(44 * s), bh = Math.Max(3, (int)(4 * s));
                var bar = new Rectangle(tx, by, tw, bh);
                float f = now.Dur > 0 ? (float)Math.Max(0, Math.Min(1, now.Pos / now.Dur)) : 0;
                using (var p = Ui.Round(bar, bh / 2f)) using (var b = new SolidBrush(Color.FromArgb(60, T.Text))) g.FillPath(b, p);
                if (f > 0) using (var p = Ui.Round(new RectangleF(bar.X, bar.Y, Math.Max(bh, bar.Width * f), bh), bh / 2f)) using (var b = new SolidBrush(T.Accent)) g.FillPath(b, p);
                if (now.Dur > 0)
                {
                    DrawText(g, Time(TimeSpan.FromSeconds(now.Pos)), smallFont, new Rectangle(tx, by + (int)(8 * s), tw / 2, (int)(18 * s)), T.SubText, left: true);
                    DrawText(g, "-" + Time(TimeSpan.FromSeconds(Math.Max(0, now.Dur - now.Pos))), smallFont, new Rectangle(tx + tw / 2, by + (int)(8 * s), tw / 2, (int)(18 * s)), T.SubText, right: true);
                }
                string key = now.Key;
                buttons.Add((card, () => { LumenR.OpenShow(key); ClosePanel(false); }));
                x = card.Right + gap;
            }

            var shows = (st?.Continue ?? new List<LumenR.Show>()).Where(c => now == null || c.Key != now.Key).ToList();
            if (shows.Count == 0)
            {
                if (now == null)
                    DrawText(g, "Nothing in progress. Open LumenR and pick something to watch.", textFont,
                             new Rectangle(row.X, row.Y + row.Height / 2 - (int)(14 * s), row.Width, (int)(28 * s)), T.SubText);
                return;
            }
            int cw = pw, cgap = (int)(16 * s);
            foreach (var show in shows)
            {
                if (x + cw > row.Right) break;
                var cell = new Rectangle(x, row.Y, cw, row.Height);
                var pr = new Rectangle(x, row.Y, cw, ph);
                bool hot = cell.Contains(mouse);
                DrawPoster(g, pr, show.Poster, show.Title, show.Total > 0 && show.Watched > 0 ? show.Watched / (float)show.Total : 0, hot);
                DrawText(g, show.Title, sideFont, new Rectangle(x - (int)(2 * s), pr.Bottom + (int)(4 * s), cw + (int)(4 * s), (int)(18 * s)), T.Text);
                DrawText(g, show.Label, smallFont, new Rectangle(x - (int)(6 * s), pr.Bottom + (int)(21 * s), cw + (int)(12 * s), (int)(15 * s)), hot ? T.Accent : T.SubText);
                string key = show.Key;
                buttons.Add((cell, () => { LumenR.PlayNext(key); ClosePanel(false); }));
                x += cw + cgap;
            }
        }

        /// <summary>A poster with rounded corners (a placeholder with initials when there's no picture), a thin bar for how much is watched, and a play button on hover.</summary>
        void DrawPoster(Graphics g, Rectangle r, string path, string title, float watched, bool hot = false)
        {
            float rad = Math.Min(10 * s, Ui.CornerPx(s) * 0.7f);
            using var shape = Ui.Round(r, rad);
            var pic = Poster(path, r.Width, r.Height);
            var clip = g.Clip;
            g.SetClip(shape, CombineMode.Intersect);
            if (pic != null) g.DrawImageUnscaled(pic, r.X, r.Y);
            else
            {
                using (var b = new LinearGradientBrush(r, Ui.Mix(T.Surface, T.Accent, 0.35f), Ui.Mix(T.Background, T.Accent, 0.12f), 70f)) g.FillRectangle(b, r);
                string initials = new string((title ?? "").Split(new[] { ' ', '-', ':' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w2 => char.IsLetterOrDigit(w2[0])).Take(2).Select(w2 => char.ToUpperInvariant(w2[0])).ToArray());
                DrawText(g, initials, valueFont, r, Color.FromArgb(200, T.Text));
            }
            if (watched > 0)
            {
                int bh = Math.Max(3, (int)(4 * s));
                using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(b, r.X, r.Bottom - bh, r.Width, bh);
                using (var b = new SolidBrush(T.Accent)) g.FillRectangle(b, r.X, r.Bottom - bh, Math.Max(bh, r.Width * Math.Min(1, watched)), bh);
            }
            if (hot)
            {
                using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(b, r);
                float d = Math.Min(r.Width, r.Height) * 0.34f;
                var c = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
                using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, c);
                float t3 = d * 0.2f, cx = c.X + c.Width / 2 + t3 * 0.15f, cy = c.Y + c.Height / 2;
                using (var b = new SolidBrush(T.IsLight || T.Accent.GetBrightness() > 0.7f ? Color.FromArgb(20, 20, 20) : Color.White))
                    g.FillPolygon(b, new[] { new PointF(cx - t3 * 0.8f, cy - t3), new PointF(cx - t3 * 0.8f, cy + t3), new PointF(cx + t3, cy) });
            }
            g.Clip = clip;
            using (var pen = new Pen(Color.FromArgb(hot ? 160 : 40, hot ? T.Accent : T.Text), hot ? 2f : 1f)) g.DrawPath(pen, shape);
        }

        /// <summary>A poster picture cut to exactly w×h (cover), cached while the panel is open.</summary>
        Bitmap Poster(string path, int w, int h)
        {
            if (string.IsNullOrEmpty(path) || w <= 0 || h <= 0) return null;
            string key = path + "|" + w + "x" + h;
            if (posters.TryGetValue(key, out var bmp)) return bmp;
            Bitmap result = null;
            try
            {
                if (File.Exists(path))
                    using (var src = ImageLoad.FromFile(path, Math.Max(w, h) * 2))
                    {
                        result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                        using var g = Graphics.FromImage(result);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float sc = Math.Max(w / (float)src.Width, h / (float)src.Height);
                        float dw = src.Width * sc, dh = src.Height * sc;
                        g.DrawImage(src, new RectangleF((w - dw) / 2f, (h - dh) / 2f, dw, dh));
                    }
            }
            catch { result?.Dispose(); result = null; }
            if (posters.Count > 60) { foreach (var b in posters.Values) b?.Dispose(); posters.Clear(); }
            posters[key] = result;
            return result;
        }

        // ---- Performance ----

        void DrawPerformance(Graphics g, Rectangle r)
        {
            int n = 3, gap = (int)(14 * s), cw = (r.Width - gap * (n - 1)) / n;
            string F(double v) => v >= 100 ? v.ToString("0") : v.ToString("0.0");
            Rectangle Cell(int k) => new Rectangle(r.X + k * (cw + gap), r.Y, cw, r.Height);

            float c = shownCpu >= 0 ? shownCpu : cpu;
            Card(g, Cell(0), "CPU", Environment.ProcessorCount + " threads",
                 c < 0 ? 0 : c / 100f, c < 0 ? "–" : Math.Round(c) + "%", "in use",
                 cpuHistory, 0, Math.Max(20, (cpuHistory.Count > 0 ? cpuHistory.Max() : 0) * 1.3f), Heat(cpu));

            float m = shownMem >= 0 ? shownMem : memPct;
            Card(g, Cell(1), "MEMORY", memPct < 0 ? "" : F(memTotalGb) + " GB",
                 m < 0 ? 0 : m / 100f, memPct < 0 ? "–" : F(memUsedGb) + " GB", memPct < 0 ? "" : Math.Round(memPct) + "% in use",
                 memHistory, 0, 100, Heat(memPct));

            float gt = shownGpu >= 0 ? shownGpu : gpu.Temp;
            string gpuSub = !gpu.Ok ? "" : gpu.Load >= 0 ? gpu.Load + "% load" : gpu.Hotspot > 0 ? "hotspot " + gpu.Hotspot + "°" : "";
            Color gpuColor = !gpu.Ok ? T.SubText : gpu.Temp >= 90 ? HeatRed : gpu.Temp >= 80 ? HeatOrange : T.Accent;
            Card(g, Cell(2), "GPU", gpu.Ok && gpu.Hotspot > 0 && gpu.Load >= 0 ? "hotspot " + gpu.Hotspot + "°" : "",
                 gpu.Ok ? gt / 100f : 0, gpu.Ok ? Math.Round(gt) + "°C" : "–", gpu.Ok ? gpuSub : "temperature unavailable",
                 gpuHistory, 30, 100, gpuColor);
        }

        static readonly Color HeatOrange = Color.FromArgb(255, 167, 38), HeatRed = Color.FromArgb(239, 83, 80);

        /// <summary>Accent normally; warmer as it gets busy.</summary>
        Color Heat(float pct) => pct >= 90 ? HeatRed : pct >= 75 ? HeatOrange : T.Accent;

        /// <summary>
        /// One reading as a card: a header (name, detail), a gauge with the value in the middle, and a small
        /// graph of the last 40 seconds along the bottom.
        /// </summary>
        void Card(Graphics g, Rectangle cell, string title, string detail, float value, string big, string caption,
                  List<float> history, float lo, float hi, Color color)
        {
            // the card
            using (var p = Ui.Round(cell, 14 * s))
            {
                using (var b = new SolidBrush(Color.FromArgb(T.IsLight ? 150 : 110, T.Surface))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(40, T.Text))) g.DrawPath(pen, p);
            }
            int pad = (int)(14 * s);

            // header: a coloured dot and the name, the detail on the right
            int hy = cell.Y + (int)(10 * s), hh = (int)(18 * s);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cell.X + pad, hy + hh / 2f - 3 * s, 6 * s, 6 * s);
            DrawText(g, title, labelFont, new Rectangle(cell.X + pad + (int)(12 * s), hy, cell.Width / 2, hh), T.SubText, left: true);
            if (!string.IsNullOrEmpty(detail))
                DrawText(g, detail, smallFont, new Rectangle(cell.X + cell.Width / 2, hy, cell.Width / 2 - pad, hh), T.SubText, right: true);

            // the graph along the bottom
            int graphH = (int)(42 * s);
            var graph = new Rectangle(cell.X + 1, cell.Bottom - graphH - 1, cell.Width - 2, graphH);
            DrawHistory(g, graph, history, lo, hi, color, cell);

            // the gauge, between header and graph
            int top = hy + hh + (int)(4 * s), bottom = graph.Top + (int)(6 * s);
            float d = Math.Min(bottom - top, cell.Width - pad * 4);
            var c = new RectangleF(cell.X + (cell.Width - d) / 2f, top + (bottom - top - d) / 2f, d, d);
            float thick = 7 * s;
            var arc = RectangleF.Inflate(c, -thick / 2 - 3 * s, -thick / 2 - 3 * s);
            const float start = 135, sweep = 270; // open at the bottom
            using (var pen = new Pen(Color.FromArgb(45, color), thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, start, sweep);
            float v = Math.Max(0.004f, Math.Min(1, value)), end = start + sweep * v;
            using (var path = new GraphicsPath())
            {
                path.AddArc(arc, start, sweep * v);
                // a soft glow under the filled part
                using (var glow = new Pen(Color.FromArgb(45, color), thick * 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawPath(glow, path);
                using (var pen = new Pen(color, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawPath(pen, path);
            }
            // a bright dot at the tip
            double a = end * Math.PI / 180;
            float tx = arc.X + arc.Width / 2 + (float)Math.Cos(a) * arc.Width / 2, ty = arc.Y + arc.Height / 2 + (float)Math.Sin(a) * arc.Height / 2;
            using (var b = new SolidBrush(Ui.Mix(color, Color.White, 0.55f))) g.FillEllipse(b, tx - thick * 0.32f, ty - thick * 0.32f, thick * 0.64f, thick * 0.64f);

            // the value in the middle, the caption under it
            var mid = new Rectangle((int)c.X, (int)(c.Y + c.Height / 2 - 22 * s), (int)c.Width, (int)(34 * s));
            DrawText(g, big, valueFont, mid, T.Text);
            if (!string.IsNullOrEmpty(caption))
                DrawText(g, caption, smallFont, new Rectangle((int)c.X - (int)(10 * s), mid.Bottom - (int)(2 * s), (int)c.Width + (int)(20 * s), (int)(16 * s)), T.SubText);
        }

        /// <summary>The last 40 seconds as a soft area graph, fading out towards the top, clipped to the card.</summary>
        void DrawHistory(Graphics g, Rectangle r, List<float> history, float lo, float hi, Color color, Rectangle card)
        {
            if (history.Count < 2) return;
            var st = g.Save();
            using (var clip = Ui.Round(card, 14 * s)) g.SetClip(clip, CombineMode.Intersect);
            int count = HistoryLength;
            float step = r.Width / (float)(count - 1);
            var pts = new List<PointF>();
            int offset = count - history.Count; // newest on the right
            for (int k = 0; k < history.Count; k++)
            {
                float f = Math.Max(0, Math.Min(1, (history[k] - lo) / Math.Max(1, hi - lo)));
                pts.Add(new PointF(r.X + (offset + k) * step, r.Bottom - 2 * s - f * (r.Height - 4 * s)));
            }
            using (var area = new GraphicsPath())
            {
                area.AddCurve(pts.ToArray(), 0.4f);
                area.AddLine(pts[pts.Count - 1].X, pts[pts.Count - 1].Y, pts[pts.Count - 1].X, r.Bottom + 2);
                area.AddLine(pts[pts.Count - 1].X, r.Bottom + 2, pts[0].X, r.Bottom + 2);
                area.CloseFigure();
                using (var fill = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 3), Color.FromArgb(95, color), Color.FromArgb(0, color), LinearGradientMode.Vertical))
                    g.FillPath(fill, area);
            }
            using (var pen = new Pen(Color.FromArgb(170, color), 1.6f * s) { LineJoin = LineJoin.Round }) g.DrawCurve(pen, pts.ToArray(), 0.4f);
            g.Restore(st);
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
            int hot = buttons.FindIndex(b => b.r.Contains(mouse));
            bool track = tab == 0 && HotTrack();
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
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
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
                foreach (var n in notches.Values) n.Dispose(); watch.Dispose(); tick.Dispose(); Anim.StopFrames(SpinKey); media.Dispose(); vinyl?.Dispose();
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
