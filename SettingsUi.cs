using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace WispR
{
    // Small themed controls for the settings window: they take their colours from the current theme,
    // so the settings look like the rest of WispR and follow it live.

    /// <summary>An on/off switch with a gliding knob.</summary>
    sealed class ToggleSwitch : Control
    {
        readonly Func<Theme> theme;
        bool on;
        float knob; // 0 = off … 1 = on (animated)
        bool hover;

        public event EventHandler CheckedChanged;

        public ToggleSwitch(Func<Theme> theme, float scale)
        {
            this.theme = theme;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size((int)(42 * scale), (int)(22 * scale));
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get => on;
            set
            {
                if (on == value) return;
                on = value;
                float from = knob, to = on ? 1 : 0;
                if (IsHandleCreated && Visible)
                    Anim.Run(this, 140, e => { knob = (float)Anim.Lerp(from, to, e); Invalidate(); }, null, Anim.OutCubic);
                else { knob = to; Invalidate(); }
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) { Focus(); Checked = !Checked; } }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = theme();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            float alpha = Enabled ? 1f : 0.4f;
            Color off = Ui.Mix(t.Surface, t.SubText, hover ? 0.55f : 0.4f), onC = t.Accent;
            Color track = Ui.Mix(off, onC, knob);
            using (var p = Ui.Round(r, r.Height / 2))
            {
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * alpha), track))) g.FillPath(b, p);
                if (Focused) using (var pen = new Pen(Color.FromArgb(160, t.Text), 1.2f)) g.DrawPath(pen, p);
            }
            float d = r.Height - 6, x = r.X + 3 + (r.Width - 6 - d) * knob;
            var knobColor = knob > 0.5f ? Ui.OnAccent(t.Accent) : t.Text;
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * alpha), knobColor))) g.FillEllipse(b, x, r.Y + 3, d, d);
        }
    }

    /// <summary>A flat slider with an accent fill and a round thumb.</summary>
    sealed class FlatSlider : Control
    {
        readonly Func<Theme> theme;
        readonly float s;
        int min, max = 100, value;
        bool dragging, hover;

        public event EventHandler ValueChanged;

        public FlatSlider(Func<Theme> theme, float scale, int min, int max)
        {
            this.theme = theme; s = scale; this.min = min; this.max = max; value = min;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size((int)(220 * s), (int)(24 * s));
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Value
        {
            get => value;
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        float Pad => 9 * s;
        void SetFromX(int x) => Value = min + (int)Math.Round((max - min) * Math.Max(0, Math.Min(1, (x - Pad) / (Width - 2 * Pad))));

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); dragging = true; SetFromX(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        // the wheel only adjusts a slider you clicked first, so scrolling the page never changes settings by accident
        protected override void OnMouseWheel(MouseEventArgs e) { if (Focused) Value += e.Delta > 0 ? 1 : -1; else base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Value--;
            else if (e.KeyCode == Keys.Right) Value++;
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = theme();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cy = Height / 2f, h = 4 * s, w = Width - 2 * Pad;
            float f = max == min ? 0 : (value - min) / (float)(max - min);
            int a = Enabled ? 255 : 100;
            using (var p = Ui.Round(new RectangleF(Pad, cy - h / 2, w, h), h / 2))
            using (var b = new SolidBrush(Color.FromArgb(a, Ui.Mix(t.Surface, t.SubText, 0.35f)))) g.FillPath(b, p);
            if (f > 0)
                using (var p = Ui.Round(new RectangleF(Pad, cy - h / 2, Math.Max(h, w * f), h), h / 2))
                using (var b = new SolidBrush(Color.FromArgb(a, t.Accent))) g.FillPath(b, p);
            float r = (dragging ? 8 : hover || Focused ? 7.5f : 7) * s, x = Pad + w * f;
            using (var b = new SolidBrush(Color.FromArgb(a, t.Accent))) g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
            float ir = r * 0.45f;
            using (var b = new SolidBrush(Color.FromArgb(a, Ui.OnAccent(t.Accent)))) g.FillEllipse(b, x - ir, cy - ir, ir * 2, ir * 2);
        }
    }

    /// <summary>A sidebar entry: glyph + text, with a pill highlight and an accent mark when selected.</summary>
    sealed class NavButton : Control
    {
        readonly Func<Theme> theme;
        readonly float s;
        readonly string glyph;
        bool selected, hover;
        float sel; // animated selection

        public NavButton(Func<Theme> theme, float scale, string glyph, string text)
        {
            this.theme = theme; s = scale; this.glyph = glyph; Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Height = (int)(38 * scale);
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                float from = sel, to = value ? 1 : 0;
                Anim.Run(this, 160, e => { sel = (float)Anim.Lerp(from, to, e); Invalidate(); });
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        static Font glyphFont, textFont;

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = theme();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new RectangleF(2, 2, Width - 4, Height - 4);
            float bg = Math.Max(sel, hover ? 0.5f : 0);
            if (bg > 0.01f)
                using (var p = Ui.Round(r, 6 * s))
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * bg), t.Selection))) g.FillPath(b, p);
            if (sel > 0.01f)
            {
                float h = (Height * 0.42f) * sel;
                using var p = Ui.Round(new RectangleF(r.X, Height / 2f - h / 2, 3 * s, Math.Max(1, h)), 1.5f * s);
                using var b = new SolidBrush(t.Accent);
                g.FillPath(b, p);
            }
            glyphFont ??= new Font(BarForm.GlyphFamily, 11f);
            textFont ??= new Font("Segoe UI", 10f);
            var ir = new Rectangle((int)(14 * s), 0, (int)(22 * s), Height);
            TextRenderer.DrawText(g, glyph, glyphFont, ir, selected ? t.Accent : t.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, textFont, new Rectangle(ir.Right + (int)(10 * s), 0, Width - ir.Right, Height), t.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>A rounded card that groups setting rows.</summary>
    sealed class Card : Panel
    {
        readonly Func<Theme> theme;
        readonly float s;

        public Card(Func<Theme> theme, float scale)
        {
            this.theme = theme; s = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding((int)(18 * s), (int)(4 * s), (int)(18 * s), (int)(4 * s));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var t = theme();
            e.Graphics.Clear(Parent?.BackColor ?? t.Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8 * s);
            using (var b = new SolidBrush(t.Surface)) e.Graphics.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(70, t.Border))) e.Graphics.DrawPath(pen, p);
            // hairlines between rows
            using var line = new Pen(Color.FromArgb(45, t.Text));
            Control prev = null;
            foreach (Control c in Controls)
            {
                if (prev != null && c.Visible) e.Graphics.DrawLine(line, Padding.Left, c.Top - 1, Width - Padding.Right, c.Top - 1);
                if (c.Visible) prev = c;
            }
        }
    }

    /// <summary>A colour chip: a round sample with its name; click to change it.</summary>
    sealed class ColorChip : Control
    {
        readonly Func<Theme> theme;
        readonly float s;
        Color color;
        bool hover;

        public ColorChip(Func<Theme> theme, float scale, string label)
        {
            this.theme = theme; s = scale; Text = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size((int)(78 * s), (int)(66 * s));
            Cursor = Cursors.Hand;
        }

        public Color Color { get => color; set { color = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        static Font font;

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = theme();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = (hover ? 34 : 32) * s, cx = Width / 2f, top = 6 * s + (hover ? -1 * s : 0);
            var c = new RectangleF(cx - d / 2, top, d, d);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, c);
            using (var pen = new Pen(hover ? t.Accent : Color.FromArgb(90, t.Text), hover ? 2f : 1f)) g.DrawEllipse(pen, c);
            font ??= new Font("Segoe UI", 8.5f);
            TextRenderer.DrawText(g, Text, font, new Rectangle(0, (int)(44 * s), Width, (int)(20 * s)), t.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    static class Ui
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        /// <summary>Readable colour on top of the accent.</summary>
        public static Color OnAccent(Color accent) => accent.GetBrightness() > 0.62f ? Color.FromArgb(24, 24, 28) : Color.White;
    }
}
