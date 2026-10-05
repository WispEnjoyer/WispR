using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// A small player next to the CPU/RAM box: cover art, title and artist, a progress line, and
    /// previous / play-pause / next. Shown only while something is playing (or paused).
    /// </summary>
    sealed class MediaBoxForm : BarForm
    {
        readonly MediaService media;
        readonly Timer tick = new Timer { Interval = 1000 };
        readonly Font titleFont = new Font("Segoe UI Semibold", 9f);
        readonly Font artistFont = new Font("Segoe UI", 8f);
        Font glyphFont, playFont, noteFont;
        MediaInfo info;
        int barHeight;

        /// <summary>True while there's something to show; the bars hide the box otherwise.</summary>
        public bool HasMedia => info != null;
        public event Action HasMediaChanged;
        /// <summary>Brings the playing app to the front (set by the bars).</summary>
        public Func<string, bool> ActivateApp;

        public MediaBoxForm(Settings settings, Backdrop backdrop) : base(settings, backdrop)
        {
            Text = "WispR Media";
            _ = Handle;
            media = new MediaService();
            media.Updated += i => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => Apply(i))); };
            // keep the progress line moving — only that strip is repainted
            tick.Tick += (o, e) => { if (info?.Playing == true && Visible) { if (trackRect.Width > 0) Invalidate(Rectangle.Inflate(trackRect, S(8), S(8))); else Invalidate(); } };
            tick.Start();
        }

        public void SetHeight(int h) { barHeight = h; }

        public override void ApplyTheme()
        {
            if (media != null) media.Paused = !settings.ShowMediaBox || settings.TopPanel; // the drop-down has its own reader
            glyphFont?.Dispose(); playFont?.Dispose(); noteFont?.Dispose();
            glyphFont = new Font(GlyphFamily, 10f);
            playFont = new Font(GlyphFamily, 13f);
            noteFont = new Font(GlyphFamily, 14f);
            base.ApplyTheme();
            RequestRelayout();
        }

        DateTime missingSince = DateTime.MinValue;

        void Apply(MediaInfo i)
        {
            if (i == null && info != null)
            {
                if (missingSince == DateTime.MinValue) missingSince = DateTime.Now;
                if ((DateTime.Now - missingSince).TotalSeconds < 4) return; // keep showing it for a moment
            }
            else missingSince = DateTime.MinValue;
            bool had = info != null;
            string oldKey = info?.Key;
            var oldThumb = info?.Thumbnail;
            // Right after a seek the app may still report the old position for a moment: keep ours.
            if (i != null && DateTime.UtcNow < seekHoldUntil && info != null && i.Key == info.Key)
            {
                i.PositionTicks = seekTargetTicks;
                i.PositionAtUtc = seekAt;
            }
            info = i;
            // Dispose the previous cover only now, on this thread, once nothing draws it any more.
            if (oldThumb != null && oldThumb != info?.Thumbnail) oldThumb.Dispose();
            if (had != (info != null)) { RequestRelayout(); HasMediaChanged?.Invoke(); }
            else if (oldKey != info?.Key) RequestRelayout();
            Invalidate();
        }

        // ---------- layout ----------

        public override Size Measure()
        {
            items.Clear();
            int h = barHeight > 0 ? barHeight : S(48);
            if (info == null || glyphFont == null) return new Size(h, h);
            int pad = S(4), cell = h - S(8), top = (h - cell) / 2, x = pad;

            // cover + text: click to bring the app to the front
            int textW = S(150);
            items.Add(new BarItem
            {
                Bounds = new Rectangle(x, top, cell + S(8) + textW, cell),
                Tooltip = () => info == null ? null : info.Title + (info.Artist.Length > 0 ? "\n" + info.Artist : "") +
                                (info.DurationTicks > 0 ? "\n" + Time(info.Position) + " / " + Time(TimeSpan.FromTicks(info.DurationTicks)) : "") +
                                "\nClick to open the app",
                Paint = (g, r, hv) => PaintNowPlaying(g, r),
                Click = (b, p, d) => { if (b == MouseButtons.Left && info != null) ActivateApp?.Invoke(info.AppId); },
            });
            x += cell + S(8) + textW + S(4);

            void Button(string tip, Func<string> glyph, Func<Font> font, Action action)
            {
                items.Add(new BarItem
                {
                    Bounds = new Rectangle(x, top, S(30), cell),
                    Tooltip = () => tip,
                    Paint = (g, r, hv) => DrawGlyph(g, glyph(), font(), r, T.Text),
                    Click = (b, p, d) => { if (b == MouseButtons.Left) { action(); Invalidate(); } },
                });
                x += S(30) + S(1);
            }
            Button("Previous", () => "", () => glyphFont, media.Previous);
            Button("Play / pause", () => info?.Playing == true ? "" : "", () => playFont, () =>
            {
                media.PlayPause();
                if (info != null) info.Playing = !info.Playing; // instant feedback; the next reading confirms
            });
            Button("Next", () => "", () => glyphFont, media.Next);

            return new Size(x - S(1) + pad, h);
        }

        void PaintNowPlaying(Graphics g, Rectangle r)
        {
            try { PaintNowPlayingCore(g, r); }
            catch { /* a broken cover image must never take the bar down */ }
        }

        void PaintNowPlayingCore(Graphics g, Rectangle r)
        {
            if (info == null) return;
            int cell = r.Height;
            var art = new Rectangle(r.X + S(2), r.Y + S(2), cell - S(4), cell - S(4));
            using (var clip = Rounded(art, S(5)))
            {
                if (info.Thumbnail != null)
                {
                    var state = g.Save();
                    g.SetClip(clip);
                    // crop to a square from the middle (YouTube thumbnails are 16:9)
                    var src = info.Thumbnail;
                    int side = Math.Min(src.Width, src.Height);
                    g.DrawImage(src, art, new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                    g.Restore(state);
                }
                else
                {
                    using (var b = new SolidBrush(T.Selection)) g.FillPath(b, clip);
                    DrawGlyph(g, "", noteFont, art, T.SubText); // music note
                }
            }

            int tx = art.Right + S(8), tw = r.Right - tx - S(4);
            int th = titleFont.Height, ah = artistFont.Height;
            int ty = r.Y + (r.Height - th - ah - S(4)) / 2;
            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, info.Title, titleFont, new Rectangle(tx, ty, tw, th), T.Text, flags);
            TextRenderer.DrawText(g, info.Artist.Length > 0 ? info.Artist : AppName(info.AppId), artistFont, new Rectangle(tx, ty + th, tw, ah), T.SubText, flags);

            // progress line — only when the app reports a length (some, e.g. Firefox-based browsers, don't)
            bool hot = seeking || overTrack;
            int lineH = hot ? S(4) : S(3);
            var track = new Rectangle(tx, ty + th + ah + S(2) - (lineH - S(3)) / 2, tw, lineH);
            trackRect = new Rectangle(tx, ty + th + ah + S(2), tw, S(3));
            if (info.DurationTicks <= 0) { trackRect = Rectangle.Empty; return; }
            using (var p = Rounded(track, lineH / 2f)) using (var b = new SolidBrush(T.Border)) g.FillPath(b, p);
            float f = seeking ? seekFrac : (float)Math.Min(1, info.Position.Ticks / (double)info.DurationTicks);
            int w = Math.Max(lineH, (int)(track.Width * f));
            using (var p = Rounded(new Rectangle(track.X, track.Y, w, track.Height), lineH / 2f))
            using (var b = new SolidBrush(info.Playing || seeking ? T.Accent : T.SubText))
                g.FillPath(b, p);
            if (hot) // knob while hovering / dragging
            {
                int kr = S(5), kx = track.X + w, ky = track.Y + track.Height / 2;
                using var kb = new SolidBrush(T.Accent);
                g.FillEllipse(kb, kx - kr, ky - kr, kr * 2, kr * 2);
            }
        }

        // ---------- seeking: click or drag on the progress line ----------

        Rectangle trackRect;
        bool seeking, overTrack;
        float seekFrac;
        long seekTargetTicks;
        DateTime seekAt, seekHoldUntil;

        bool OnTrack(Point p) => trackRect.Width > 0 && info != null && info.DurationTicks > 0 &&
            new Rectangle(trackRect.X - S(4), trackRect.Y - S(7), trackRect.Width + S(8), trackRect.Height + S(14)).Contains(p);

        float FracAt(int x) => Math.Max(0, Math.Min(1, (x - trackRect.X) / (float)Math.Max(1, trackRect.Width)));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && OnTrack(e.Location))
            {
                seeking = true;
                seekFrac = FracAt(e.X);
                Capture = true;
                Invalidate();
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (seeking) { seekFrac = FracAt(e.X); Invalidate(); return; }
            bool over = OnTrack(e.Location);
            if (over != overTrack) { overTrack = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (seeking)
            {
                seeking = false;
                Capture = false;
                if (info != null && info.DurationTicks > 0)
                {
                    var target = TimeSpan.FromTicks((long)(info.DurationTicks * (double)seekFrac));
                    media.Seek(target);
                    // show the new position straight away
                    seekTargetTicks = target.Ticks;
                    seekAt = DateTime.UtcNow;
                    seekHoldUntil = seekAt.AddSeconds(1.5);
                    info.PositionTicks = seekTargetTicks;
                    info.PositionAtUtc = seekAt;
                }
                CancelPress();
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (overTrack) { overTrack = false; Cursor = Cursors.Default; }
            base.OnMouseLeave(e);
        }

        static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        static string AppName(string id) => MediaApps.Name(id);

        protected override void OnBackgroundRightClick(Point screen) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tick.Dispose(); media.Dispose(); titleFont.Dispose(); artistFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
