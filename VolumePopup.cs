using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The volume popup opened from the system box: output device, mute button, a slider you can
    /// drag or scroll, and a shortcut to Windows' sound settings. The arrow opens it further into a
    /// mixer: every program with sound, each with its own slider (click its icon to mute it).
    /// Never takes focus; closes when you click elsewhere, press Esc or switch windows.
    /// </summary>
    sealed class VolumePopup : Form
    {
        readonly Settings settings;
        readonly float s;
        readonly Timer watch = new Timer { Interval = 40 };
        readonly Timer refresh = new Timer { Interval = 500 };
        readonly Font nameFont = new Font("Segoe UI", 8.5f);
        readonly Font percentFont = new Font("Segoe UI Semibold", 11f);
        readonly WheelHook wheel;
        Font glyphFont, smallGlyphFont;

        float volume;
        bool muted, hasDevice, dragging, armed;
        string device;
        Rectangle muteRect, sliderRect, gearRect, valueRect, headRect, anchor, expandRect;
        int hoverZone; // 0 none, 1 mute, 2 gear, 3 slider, 4 expand; 100 + 2i app i's icon, 101 + 2i its slider
        int dragApp = -1; // the app whose slider is being dragged (-1: the main one)
        IntPtr foregroundAtOpen;

        // the mixer part
        static bool expanded;             // stays open or closed between openings
        float open;                       // 0 = just the main part, 1 = mixer fully out (animated)
        bool above;                       // grows up out of the bar (the mixer then sits on top)
        int edge, baseH, x0, w0, top0;    // top0: the popup's top when it hangs below a bar
        int listFullH;                    // the mixer's height when fully out
        int shownCount;                   // how many apps the height is made for
        List<MixerApp> apps = new List<MixerApp>();
        Font badgeFont;
        const int MaxApps = 8;
        int AppRowH => S(46);
        int paintH;                                       // set while drawing a picture at another height
        int H => paintH > 0 ? paintH : Height;
        int MainY => above ? H - baseH : 0;               // where the main part starts
        int ListTop => above ? MainY - listFullH : baseH; // where the (full) mixer starts

        public event Action VolumeChanged;

        Theme T => settings.Theme;

        public VolumePopup(Settings settings)
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
            refresh.Tick += (o, e) => { if (!dragging) { Read(); if (expanded && !Anim.IsRunning("volume-mixer") && !GrowOut.IsMorphing(this)) ReadApps(); Invalidate(); } };
            wheel = new WheelHook(this, (delta, pt) =>
            {
                int i = AppAt(PointToClient(pt));
                if (i >= 0) { var a = apps[i]; AppMixer.Set(a, a.Level + Math.Sign(delta) * 0.02f); Invalidate(); }
                else SetVolume(volume + Math.Sign(delta) * 0.02f);
            });
        }

        int S(float px) => (int)Math.Round(px * s);

        public void ToggleAt(Rectangle item, bool above, int alignRight = -1)
        {
            if (Visible && !GrowOut.IsClosing(this)) { CloseMenu(); return; }
            anchor = item;
            glyphFont ??= new Font(BarForm.GlyphFamily, 14f);
            smallGlyphFont ??= new Font(BarForm.GlyphFamily, 10.5f);
            BackColor = above ? T.Background : T.Surface; // growing out of the bar: same colour as the bar
            Read();
            device = SystemStatus.GetOutputDeviceName() ?? "Speakers";

            // One margin (m) for every edge; two rows: device + settings, then mute + slider + value.
            int m = S(16), w = S(340), headH = S(28), rowH = S(36), gap = S(10);
            baseH = m + headH + gap + rowH + m;
            this.above = above;
            var scr = Screen.FromRectangle(item).Bounds;
            int x = alignRight >= 0 ? alignRight - w : item.X + item.Width / 2 - w / 2; // line up with the system box
            x = Math.Max(scr.Left + S(22), Math.Min(scr.Right - w - S(22), x)); // room for the curves into the bar
            bool curves;
            (edge, curves) = GrowOut.EdgeFor(item, item.Top - S(4));
            x0 = x; w0 = w; top0 = item.Bottom + S(12);

            Anim.Stop("volume-mixer");
            if (expanded) ReadApps(); else apps.Clear();
            shownCount = apps.Count;
            listFullH = ListHeight(shownCount);
            open = expanded ? 1 : 0;
            Bounds = BoundsFor(open);

            Layout(Height);
            foregroundAtOpen = Native.GetForegroundWindow();
            armed = false;
            int round = 2, dark = T.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            if (above) { GrowOut.PrepareWindow(this); GrowOut.Show(this, edge, curves, T, s); }
            else
            {
                Anim.PopIn(this, -S(10));
                Show();
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            watch.Start();
            refresh.Start();
            wheel.Install();
            Invalidate();
        }

        public void CloseMenu()
        {
            watch.Stop();
            refresh.Stop();
            wheel.Uninstall();
            dragging = false;
            Anim.Stop("volume-mixer");
            expanded = false; // opens plain again next time
            if (Visible) GrowOut.Close(this, () => { Hide(); AppMixer.Release(); apps.Clear(); }); // sinks back into the bar
        }

        // ---------- layout ----------

        int ListHeight(int n) => S(6) + (n == 0 ? S(40) : n * AppRowH) + S(16);

        Rectangle BoundsFor(float amount)
        {
            int h = baseH + (int)Math.Round(listFullH * amount);
            return new Rectangle(x0, above ? edge - h : top0, w0, h);
        }

        /// <summary>The main part's buttons and slider, wherever the main part sits right now.</summary>
        void Layout(int height)
        {
            int m = S(16), w = w0, headH = S(28), rowH = S(36), gap = S(10), y0 = above ? height - baseH : 0;
            int rowY = y0 + m + headH + gap, btn = S(32);
            // buttons extend a little past the margin so their *glyphs* sit exactly on it
            gearRect = new Rectangle(w - m - btn + S(7), y0 + m + (headH - btn) / 2, btn, btn);
            expandRect = new Rectangle(gearRect.X - btn - S(2), gearRect.Y, btn, btn);
            muteRect = new Rectangle(m - S(7), rowY + (rowH - btn) / 2, btn, btn);
            valueRect = new Rectangle(w - m - S(36), rowY, S(36), rowH);
            sliderRect = new Rectangle(muteRect.Right + S(8), rowY + (rowH - S(20)) / 2, valueRect.X - S(14) - (muteRect.Right + S(8)), S(20));
            headRect = new Rectangle(m, y0 + m, expandRect.X - m - S(6), headH);
        }

        // one app's row: icon (its mute button), name above its slider, percentage
        Rectangle AppRow(int i) => new Rectangle(0, ListTop + (above ? S(16) : S(6)) + i * AppRowH, w0, AppRowH);
        Rectangle AppIcon(int i) { var r = AppRow(i); return new Rectangle(muteRect.X, r.Y + (r.Height - S(32)) / 2, S(32), S(32)); }
        Rectangle AppSlider(int i) { var r = AppRow(i); return new Rectangle(sliderRect.X, r.Y + S(21), sliderRect.Width, S(20)); }
        Rectangle AppName(int i) { var r = AppRow(i); return new Rectangle(sliderRect.X, r.Y + S(3), valueRect.Right - sliderRect.X, S(18)); }
        Rectangle AppValue(int i) { var s2 = AppSlider(i); return new Rectangle(valueRect.X, s2.Y, valueRect.Width, s2.Height); }

        int AppAt(Point p)
        {
            if (open < 0.5f) return -1;
            for (int i = 0; i < apps.Count; i++) if (AppRow(i).Contains(p)) return i;
            return -1;
        }

        void ReadApps()
        {
            AppMixer.Refresh();
            apps = AppMixer.Apps.Take(MaxApps).ToList();
            if (dragApp >= apps.Count) { dragging = false; dragApp = -1; }
            if (Visible && expanded && apps.Count != shownCount && open >= 1) Grow(true, refit: true);
        }

        void ToggleMixer()
        {
            expanded = !expanded;
            if (expanded) { ReadApps(); shownCount = apps.Count; listFullH = ListHeight(shownCount); }
            Grow(expanded);
        }

        /// <summary>Opens or closes the mixer part, or makes it fit a changed number of apps.</summary>
        void Grow(bool on, bool refit = false)
        {
            if (above && Visible && GrowOut.IsMorphing(this)) GrowOut.EndMorph(this);
            if (above && Visible && MorphTo(on, refit)) return;
            int fromPx = (int)Math.Round(listFullH * open);
            int toH = ListHeight(apps.Count);
            int toPx = on ? toH : 0;
            // drawn at the larger of the two heights while it moves; only how much of it shows changes
            int drawH = Math.Max(toH, listFullH);
            Anim.Run("volume-mixer", refit ? 160 : 230, e =>
            {
                if (IsDisposed) return;
                listFullH = drawH;
                open = (float)(Anim.Lerp(fromPx, toPx, e) / drawH);
                ApplySize();
            }, () =>
            {
                if (IsDisposed) return;
                shownCount = apps.Count;
                listFullH = toH;
                open = on ? 1 : 0;
                ApplySize();
                if (!on) { AppMixer.Release(); apps.Clear(); shownCount = 0; }
            }, Anim.OutCubic);
        }

        /// <summary>
        /// Out of a bottom bar: the size change is animated as one picture (popup and outline together), so
        /// nothing comes apart while it moves. False when that isn't possible.
        /// </summary>
        bool MorphTo(bool on, bool refit)
        {
            int fromH = Height;
            int toList = on ? ListHeight(apps.Count) : 0;
            int toH = baseH + toList;
            if (toH == fromH) { Finish(); return true; }
            if (refit && toH < fromH) { Finish(); ApplySize(); return true; } // a row less: just fit

            // the picture: the popup drawn at the larger height, mixer fully out
            int bigH = Math.Max(fromH, toH);
            listFullH = bigH - baseH;
            open = 1;
            Bitmap pic = null;
            try { pic = Snapshot(bigH); }
            catch (Exception ex) { Log.Error("VolumePopup.Snapshot", ex); }
            if (pic == null || !GrowOut.Morph(this, pic, fromH, toH, refit ? 160 : 230, () => { Finish(); ApplySize(); pic.Dispose(); }))
            {
                pic?.Dispose();
                listFullH = Math.Max(1, ListHeight(shownCount));
                open = (fromH - baseH) / (float)listFullH;
                return false;
            }
            return true;

            void Finish()
            {
                shownCount = on ? apps.Count : 0;
                listFullH = ListHeight(on ? apps.Count : 0);
                open = on ? 1 : 0;
                if (!on) { AppMixer.Release(); apps.Clear(); }
            }
        }

        Bitmap Snapshot(int h)
        {
            var bmp = new Bitmap(w0, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            paintH = h;
            try
            {
                Layout(h);
                using var g = Graphics.FromImage(bmp);
                g.Clear(BackColor);
                PaintAll(g);
            }
            finally { paintH = 0; Layout(Height); }
            return bmp;
        }

        void ApplySize()
        {
            var b = BoundsFor(open);
            Layout(b.Height); // first: the popup draws itself right away once it has its new size
            if (Visible) GrowOut.Resize(this, b); else Bounds = b;
            Invalidate();
        }

        void Read()
        {
            hasDevice = SystemStatus.TryGetVolume(out volume, out muted);
        }

        void SetVolume(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            SystemStatus.SetVolume(v);
            Read();
            Invalidate();
            VolumeChanged?.Invoke();
        }

        void Watch()
        {
            bool anyButton = Down(0x01) || Down(0x02) || Down(0x04);
            if (!armed) { if (!anyButton) armed = true; return; }
            var cur = Cursor.Position;
            if (anyButton && !dragging && !Bounds.Contains(cur) && !anchor.Contains(cur)) { CloseMenu(); return; }
            if (Down(0x1B)) { CloseMenu(); return; }
            if (Native.GetForegroundWindow() != foregroundAtOpen) CloseMenu();
        }

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e) => PaintAll(e.Graphics);

        void PaintAll(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // device name, mixer arrow, settings button
            TextRenderer.DrawText(g, device, nameFont, headRect, T.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (hoverZone == 2) using (var p = BarForm.Rounded(gearRect, S(5))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            TextRenderer.DrawText(g, "\uE713", smallGlyphFont, gearRect, T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (hoverZone == 4 || expanded)
                using (var p = BarForm.Rounded(expandRect, S(5))) using (var b = new SolidBrush(hoverZone == 4 ? T.Selection : Color.FromArgb(T.IsLight ? 110 : 80, T.Selection))) g.FillPath(b, p);
            // the arrow points the way the mixer opens (up, out of a bottom bar), and turns round once it's open
            bool pointsUp = above != expanded;
            TextRenderer.DrawText(g, pointsUp ? "\uE70E" : "\uE70D", smallGlyphFont, expandRect, expanded ? T.Accent : T.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (open > 0.001f) PaintMixer(g);

            // mute button
            if (hoverZone == 1) using (var p = BarForm.Rounded(muteRect, S(6))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
            string glyph = !hasDevice || muted || volume <= 0.001f ? "\uE74F" : volume < 0.34f ? "\uE993" : volume < 0.67f ? "\uE994" : "\uE995";
            TextRenderer.DrawText(g, glyph, glyphFont, muteRect, muted ? T.SubText : T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // slider
            float v = hasDevice ? volume : 0;
            DrawSlider(g, sliderRect, v, muted, hoverZone == 3 || (dragging && dragApp < 0), S(8));

            // percentage
            TextRenderer.DrawText(g, hasDevice ? ((int)Math.Round(v * 100)).ToString() : "\u2013", percentFont, valueRect, T.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (!SystemStatus.IsWindows11)
                using (var pen = new Pen(T.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, H - 1);
        }

        void DrawSlider(Graphics g, Rectangle r, float v, bool off, bool hot, int knob)
        {
            int cy = r.Y + r.Height / 2, th = S(4);
            var track = new Rectangle(r.X, cy - th / 2, r.Width, th);
            using (var p = BarForm.Rounded(track, th / 2f)) using (var b = new SolidBrush(T.Border)) g.FillPath(b, p);
            int fx = r.X + (int)(r.Width * Math.Max(0, Math.Min(1, v)));
            var fill = new Rectangle(track.X, track.Y, Math.Max(th, fx - track.X), th);
            using (var p = BarForm.Rounded(fill, th / 2f)) using (var b = new SolidBrush(off ? T.SubText : T.Accent)) g.FillPath(b, p);
            int kr = hot ? knob + S(1) : knob;
            using (var b = new SolidBrush(off ? T.SubText : T.Accent)) g.FillEllipse(b, fx - kr, cy - kr, kr * 2, kr * 2);
            using (var b = new SolidBrush(T.Surface)) g.FillEllipse(b, fx - kr / 2.4f, cy - kr / 2.4f, kr / 1.2f, kr / 1.2f);
        }

        void PaintMixer(Graphics g)
        {
            int m = S(16);
            var area = above ? new Rectangle(0, 0, w0, MainY) : new Rectangle(0, baseH, w0, H - baseH);
            if (area.Height <= 0) return;
            var saved = g.Save();
            g.SetClip(area);
            // a hairline between the mixer and the main part
            int lineY = above ? MainY - 1 : baseH;
            using (var pen = new Pen(Color.FromArgb(T.IsLight ? 160 : 120, T.Border))) g.DrawLine(pen, m, lineY, Width - m, lineY);

            if (apps.Count == 0)
            {
                var r = new Rectangle(m, AppRow(0).Y, Width - m * 2, S(40));
                TextRenderer.DrawText(g, "No apps are playing sound", nameFont, r, T.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            badgeFont ??= new Font(BarForm.GlyphFamily, 6.5f);
            for (int i = 0; i < apps.Count; i++)
            {
                var a = apps[i];
                if (!AppRow(i).IntersectsWith(area)) continue;
                var ir = AppIcon(i);
                if (hoverZone == 100 + i * 2) using (var p = BarForm.Rounded(ir, S(6))) using (var b = new SolidBrush(T.Selection)) g.FillPath(b, p);
                int ip = S(24);
                var iconBox = new Rectangle(ir.X + (ir.Width - ip) / 2, ir.Y + (ir.Height - ip) / 2, ip, ip);
                if (a.Icon != null)
                {
                    if (a.Muted)
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            // greyed and faded while muted
                            var cm = new System.Drawing.Imaging.ColorMatrix(new[]
                            {
                                new[] { 0.3f, 0.3f, 0.3f, 0, 0 }, new[] { 0.59f, 0.59f, 0.59f, 0, 0 }, new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                                new[] { 0f, 0, 0, 0.45f, 0 }, new[] { 0f, 0, 0, 0, 1 },
                            });
                            ia.SetColorMatrix(cm);
                            g.DrawImage(a.Icon, iconBox, 0, 0, a.Icon.Width, a.Icon.Height, GraphicsUnit.Pixel, ia);
                        }
                    else if (a.Icon.Width == ip && a.Icon.Height == ip) g.DrawImageUnscaled(a.Icon, iconBox.Location);
                    else { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(a.Icon, iconBox); }
                }
                else
                    TextRenderer.DrawText(g, a.IsSystem ? "\uE7F5" : "\uE768", smallGlyphFont, ir, a.Muted ? T.SubText : T.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (a.Muted)
                {
                    // a small "muted" badge on the icon's corner
                    int bs = S(14);
                    var br = new Rectangle(iconBox.Right - bs + S(4), iconBox.Bottom - bs + S(4), bs, bs);
                    using (var b = new SolidBrush(T.Surface)) g.FillEllipse(b, br);
                    TextRenderer.DrawText(g, "\uE74F", badgeFont, br, T.SubText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                var nr = AppName(i);
                if (a.Active && !a.Muted)
                {
                    // playing right now: a small accent dot before the name
                    int dot = S(6);
                    using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, nr.X, nr.Y + (nr.Height - dot) / 2f, dot, dot);
                    nr = new Rectangle(nr.X + dot + S(6), nr.Y, nr.Width - dot - S(6), nr.Height);
                }
                TextRenderer.DrawText(g, a.Name, nameFont, nr, a.Muted ? T.SubText : T.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                DrawSlider(g, AppSlider(i), a.Level, a.Muted, hoverZone == 101 + i * 2 || (dragging && dragApp == i), S(7));
                TextRenderer.DrawText(g, ((int)Math.Round(a.Level * 100)).ToString(), nameFont, AppValue(i), a.Muted ? T.SubText : T.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            g.Restore(saved);
        }

        // ---------- mouse ----------

        Rectangle SliderHit => Rectangle.Inflate(sliderRect, S(6), S(8));

        Rectangle AppSliderHit(int i) => Rectangle.Inflate(AppSlider(i), S(6), S(4));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (SliderHit.Contains(e.Location)) { dragging = true; dragApp = -1; SetFromX(e.X); return; }
            int i = AppAt(e.Location);
            if (i >= 0 && AppSliderHit(i).Contains(e.Location)) { dragging = true; dragApp = i; SetFromX(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { SetFromX(e.X); return; }
            int z = muteRect.Contains(e.Location) ? 1 : gearRect.Contains(e.Location) ? 2 : SliderHit.Contains(e.Location) ? 3
                  : expandRect.Contains(e.Location) ? 4 : 0;
            int i = z == 0 ? AppAt(e.Location) : -1;
            if (i >= 0) z = AppIcon(i).Contains(e.Location) ? 100 + i * 2 : AppSliderHit(i).Contains(e.Location) ? 101 + i * 2 : 0;
            if (z != hoverZone) { hoverZone = z; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hoverZone = 0; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) { dragging = false; dragApp = -1; Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            int app = AppAt(e.Location);
            if (muteRect.Contains(e.Location)) { SystemStatus.ToggleMute(); Read(); Invalidate(); VolumeChanged?.Invoke(); }
            else if (expandRect.Contains(e.Location)) ToggleMixer();
            else if (app >= 0 && AppIcon(app).Contains(e.Location)) { AppMixer.SetMute(apps[app], !apps[app].Muted); Invalidate(); }
            else if (gearRect.Contains(e.Location)) { CloseMenu(); try { Launcher.Start("ms-settings:sound", null, null); } catch { } }
        }

        void SetFromX(int x)
        {
            if (dragApp >= 0 && dragApp < apps.Count)
            {
                var r = AppSlider(dragApp);
                AppMixer.Set(apps[dragApp], (x - r.X) / (float)r.Width);
                Invalidate();
            }
            else SetVolume((x - sliderRect.X) / (float)sliderRect.Width);
        }

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
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; return; }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { watch.Dispose(); refresh.Dispose(); wheel.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    }
}
