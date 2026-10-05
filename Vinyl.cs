using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace WispR
{
    /// <summary>
    /// A vinyl record for the playing song: coloured from its cover, in one of several pressings picked
    /// per song (solid, splatter, swirl, smoke, galaxy, pinwheel, bullseye, marble, glitter, haze), with grooves, a label made from the cover
    /// and a centre hole. Rendered once per song; the panel just rotates the picture while it plays.
    /// </summary>
    static class Vinyl
    {
        public enum Style { Solid, Splatter, Swirl, Smoke, Galaxy, Pinwheel, Bullseye, Marble, Glitter, Haze }

        /// <summary>A stable "random" design for a song (the same song always gets the same record).</summary>
        public static Style StyleFor(string key)
        {
            int h = 17;
            foreach (char c in key ?? "") h = unchecked(h * 31 + c);
            var all = (Style[])Enum.GetValues(typeof(Style));
            return all[(h & 0x7fffffff) % all.Length];
        }

        static int Seed(string key)
        {
            int h = 7;
            foreach (char c in key ?? "") h = unchecked(h * 131 + c);
            return h;
        }

        /// <summary>
        /// The cover's colours for the record: its vivid colours first (grouped by hue, weighted by how much
        /// of the cover they fill and how strong they are), then its main dark/light tone. Small, soft
        /// covers (browsers often send tiny ones) still give their real colours this way.
        /// </summary>
        public static List<Color> Colours(Bitmap cover)
        {
            var result = new List<Color>();
            if (cover == null) return result;
            const int n = 32;
            using var small = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(cover, new Rectangle(0, 0, n, n));
            }
            var hues = new (double r, double g, double b, double w, int count)[12];
            double nr = 0, ng = 0, nb = 0; int ncount = 0;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var c = small.GetPixel(x, y);
                    float sat = c.GetSaturation(), bri = c.GetBrightness();
                    float vivid = sat * (1 - Math.Abs(bri - 0.5f) * 1.6f); // strong colour, not near black/white
                    if (vivid > 0.18f)
                    {
                        int bin = (int)(c.GetHue() / 30f) % 12;
                        var h = hues[bin];
                        hues[bin] = (h.r + c.R * vivid, h.g + c.G * vivid, h.b + c.B * vivid, h.w + vivid, h.count + 1);
                    }
                    else { nr += c.R; ng += c.G; nb += c.B; ncount++; }
                }
            var vividCols = hues.Where(h => h.w > 0).OrderByDescending(h => h.w)
                .Select(h => (col: Color.FromArgb((int)(h.r / h.w), (int)(h.g / h.w), (int)(h.b / h.w)), share: h.count / (double)(n * n)))
                .Where(v => v.share > 0.01).ToList();
            var neutral = ncount > 0 ? Color.FromArgb((int)(nr / ncount), (int)(ng / ncount), (int)(nb / ncount)) : Color.FromArgb(30, 30, 34);
            bool mostlyNeutral = vividCols.Sum(v => v.share) < 0.18;
            // a mostly dark (or light) cover with bits of colour: a record in that tone with the colours as the pattern
            if (mostlyNeutral) result.Add(neutral);
            foreach (var v in vividCols)
            {
                if (result.All(o => Math.Abs(o.R - v.col.R) + Math.Abs(o.G - v.col.G) + Math.Abs(o.B - v.col.B) > 70)) result.Add(Boost(v.col));
                if (result.Count == 4) break;
            }
            if (!mostlyNeutral && result.Count < 4) result.Add(neutral);
            return result;
        }

        /// <summary>A little more saturation: small, soft covers come out washed out.</summary>
        static Color Boost(Color c)
        {
            float h = c.GetHue(), sat = Math.Min(1f, c.GetSaturation() * 1.2f), l = c.GetBrightness();
            float q = l < 0.5f ? l * (1 + sat) : l + sat - l * sat, p = 2 * l - q;
            float Hue(float t) { t = (t + 1) % 1; return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p; }
            float hh = h / 360f;
            return Color.FromArgb(c.A, (int)(Hue(hh + 1f / 3) * 255), (int)(Hue(hh) * 255), (int)(Hue(hh - 1f / 3) * 255));
        }

        static Color Shade(Color c, float f) => f >= 0
            ? Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f))
            : Color.FromArgb(c.A, (int)(c.R * (1 + f)), (int)(c.G * (1 + f)), (int)(c.B * (1 + f)));

        /// <summary>The record (transparent outside the disc), <paramref name="size"/> pixels across.</summary>
        public static Bitmap Render(Bitmap cover, string key, int size)
        {
            var cols = Colours(cover);
            while (cols.Count < 3) cols.Add(cols.Count == 0 ? Color.FromArgb(40, 40, 46) : Shade(cols[0], cols.Count == 1 ? 0.45f : -0.5f));
            var rnd = new Random(Seed(key));
            var style = StyleFor(key);
            // a mostly dark/light cover: patterned pressings keep that tone as the base, but a solid
            // record takes the cover's main colour instead (an all-black record would show nothing of it)
            if (cols[0].GetSaturation() < 0.25f && style == Style.Solid && cols.Count > 1)
                cols = new List<Color> { cols[1], cols.Count > 2 ? cols[2] : cols[0], cols[0] };

            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float R = size / 2f;
            var disc = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
            using var discPath = new GraphicsPath();
            discPath.AddEllipse(disc);
            g.SetClip(discPath);

            Color base0 = cols[0], c1 = cols[1], c2 = cols[2];
            // pressings
            switch (style)
            {
                case Style.Solid:
                    using (var b = new PathGradientBrush(discPath) { CenterColor = Shade(base0, 0.12f), SurroundColors = new[] { Shade(base0, -0.25f) } })
                        g.FillEllipse(b, disc);
                    break;

                case Style.Splatter:
                    using (var b = new SolidBrush(Shade(base0, -0.1f))) g.FillEllipse(b, disc);
                    for (int i = 0; i < 70; i++)
                    {
                        var col = i % 3 == 0 ? c2 : c1;
                        float cx = (float)rnd.NextDouble() * size, cy = (float)rnd.NextDouble() * size;
                        float rad = size * (0.008f + (float)Math.Pow(rnd.NextDouble(), 2.2) * 0.07f);
                        using var bb = new SolidBrush(Color.FromArgb(230, col));
                        g.FillEllipse(bb, cx - rad, cy - rad, rad * 2, rad * 2);
                        // droplets around a blob make it look splashed
                        for (int d = 0; d < 4; d++)
                        {
                            double a = rnd.NextDouble() * Math.PI * 2, dist = rad * (1.2 + rnd.NextDouble() * 1.6);
                            float dr = rad * (0.12f + (float)rnd.NextDouble() * 0.25f);
                            g.FillEllipse(bb, cx + (float)(Math.Cos(a) * dist) - dr, cy + (float)(Math.Sin(a) * dist) - dr, dr * 2, dr * 2);
                        }
                    }
                    break;

                case Style.Swirl:
                    using (var b = new SolidBrush(base0)) g.FillEllipse(b, disc);
                    for (int arm = 0; arm < 3; arm++)
                    {
                        var col = arm % 2 == 0 ? c1 : c2;
                        var pts = new List<PointF>();
                        for (double t = 0; t < 1; t += 0.01)
                        {
                            double a = arm * 2.1 + t * Math.PI * 5, rr = R * (0.12 + t * 0.9);
                            pts.Add(new PointF(R + (float)(Math.Cos(a) * rr), R + (float)(Math.Sin(a) * rr)));
                        }
                        using var pen = new Pen(Color.FromArgb(200, col), size * 0.07f) { LineJoin = LineJoin.Round };
                        g.DrawCurve(pen, pts.ToArray(), 0.5f);
                    }
                    break;

                case Style.Galaxy:
                {
                    // deep space: dark base, coloured nebula clouds and little stars
                    using (var b = new SolidBrush(Shade(base0.GetSaturation() < 0.25f ? base0 : c2, -0.75f))) g.FillEllipse(b, disc);
                    for (int i = 0; i < 26; i++)
                    {
                        var col = i % 2 == 0 ? c1 : (cols.Count > 3 ? cols[3] : c2);
                        float cx = (float)rnd.NextDouble() * size, cy = (float)rnd.NextDouble() * size, rad = size * (0.06f + (float)rnd.NextDouble() * 0.18f);
                        using var cp = new GraphicsPath(); cp.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
                        using var cb = new PathGradientBrush(cp) { CenterColor = Color.FromArgb(70 + rnd.Next(60), col), SurroundColors = new[] { Color.FromArgb(0, col) } };
                        g.FillPath(cb, cp);
                    }
                    for (int i = 0; i < 160; i++)
                    {
                        float sx = (float)rnd.NextDouble() * size, sy = (float)rnd.NextDouble() * size, sr = size * (0.002f + (float)Math.Pow(rnd.NextDouble(), 4) * 0.008f);
                        using var sb = new SolidBrush(Color.FromArgb(150 + rnd.Next(105), 255, 255, 255));
                        g.FillEllipse(sb, sx - sr, sy - sr, sr * 2, sr * 2);
                    }
                    break;
                }

                case Style.Pinwheel:
                {
                    int wedges = 6 + rnd.Next(4) * 2;
                    float off = (float)rnd.NextDouble() * 360;
                    for (int i = 0; i < wedges; i++)
                        using (var b = new SolidBrush(i % 2 == 0 ? base0 : c1))
                            g.FillPie(b, disc.X, disc.Y, disc.Width, disc.Height, off + i * 360f / wedges, 360f / wedges + 0.5f);
                    break;
                }

                case Style.Bullseye:
                {
                    // bands of colour from the edge to the label
                    var ring = new[] { base0, c1, c2, cols.Count > 3 ? cols[3] : Shade(c1, 0.3f) };
                    int bands = 4 + rnd.Next(3);
                    for (int i = 0; i < bands; i++)
                    {
                        float rr = R * (1 - i * 0.62f / bands);
                        using var b = new SolidBrush(ring[i % ring.Length]);
                        g.FillEllipse(b, R - rr, R - rr, rr * 2, rr * 2);
                    }
                    break;
                }

                case Style.Marble:
                {
                    // wavy bands that flow around the record
                    using (var b = new SolidBrush(base0)) g.FillEllipse(b, disc);
                    double ph = rnd.NextDouble() * 6;
                    for (int band = 0; band < 7; band++)
                    {
                        var pts = new List<PointF>();
                        float y0 = size * (band + 0.5f) / 7f;
                        for (int k = 0; k <= 24; k++)
                        {
                            float xx = size * k / 24f;
                            pts.Add(new PointF(xx, y0 + (float)(Math.Sin(k * 0.55 + ph + band) * size * 0.06 + Math.Sin(k * 0.21 + band * 2) * size * 0.04)));
                        }
                        using var pen = new Pen(Color.FromArgb(150 + rnd.Next(80), band % 2 == 0 ? c1 : c2), size * (0.025f + (float)rnd.NextDouble() * 0.05f)) { LineJoin = LineJoin.Round };
                        g.DrawCurve(pen, pts.ToArray(), 0.5f);
                    }
                    break;
                }

                case Style.Glitter:
                {
                    // a solid colour full of tiny sparkles
                    using (var b = new PathGradientBrush(discPath) { CenterColor = Shade(base0, 0.1f), SurroundColors = new[] { Shade(base0, -0.3f) } })
                        g.FillEllipse(b, disc);
                    for (int i = 0; i < 900; i++)
                    {
                        float sx = (float)rnd.NextDouble() * size, sy = (float)rnd.NextDouble() * size, sr = size * (0.002f + (float)rnd.NextDouble() * 0.004f);
                        var col = rnd.Next(4) == 0 ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(200, rnd.Next(2) == 0 ? c1 : c2);
                        using var sb = new SolidBrush(col);
                        g.FillEllipse(sb, sx - sr, sy - sr, sr * 2, sr * 2);
                    }
                    break;
                }

                case Style.Haze:
                {
                    // "colour in colour": one colour blooming out of the middle into another
                    using (var b = new SolidBrush(c1)) g.FillEllipse(b, disc);
                    using var hp = new GraphicsPath();
                    var pts = new PointF[18];
                    for (int k = 0; k < pts.Length; k++)
                    {
                        double a = k * Math.PI * 2 / pts.Length;
                        float rr = R * (0.55f + (float)rnd.NextDouble() * 0.3f);
                        pts[k] = new PointF(R + (float)Math.Cos(a) * rr, R + (float)Math.Sin(a) * rr);
                    }
                    hp.AddClosedCurve(pts, 0.6f);
                    var inner = Math.Abs(base0.GetBrightness() - c1.GetBrightness()) > 0.15f ? base0 : c2; // make sure the two differ
                    using (var hb = new PathGradientBrush(hp) { CenterPoint = new PointF(R, R), CenterColor = inner, SurroundColors = new[] { Color.FromArgb(0, inner) }, FocusScales = new PointF(0.62f, 0.62f) })
                        g.FillPath(hb, hp);
                    break;
                }

                case Style.Smoke:
                    using (var b = new SolidBrush(Shade(base0, -0.35f))) g.FillEllipse(b, disc);
                    for (int i = 0; i < 40; i++)
                    {
                        var col = Color.FromArgb(50 + rnd.Next(60), i % 2 == 0 ? c1 : c2);
                        float cx = (float)rnd.NextDouble() * size, cy = (float)rnd.NextDouble() * size, rad = size * (0.08f + (float)rnd.NextDouble() * 0.22f);
                        using var bb = new SolidBrush(col);
                        g.FillEllipse(bb, cx - rad, cy - rad, rad * 2, rad * 2);
                    }
                    break;
            }

            // grooves: fine rings, slightly lighter and darker, with a smooth band near the label
            for (float rr = R * 0.97f; rr > R * 0.36f; rr -= Math.Max(1.6f, size / 160f))
            {
                bool gap = rr < R * 0.42f || (rr > R * 0.62f && rr < R * 0.64f) || (rr > R * 0.8f && rr < R * 0.815f);
                using var pen = new Pen(gap ? Color.FromArgb(55, 0, 0, 0) : Color.FromArgb(rnd.Next(2) == 0 ? 26 : 16, 0, 0, 0), 1f);
                g.DrawEllipse(pen, R - rr, R - rr, rr * 2, rr * 2);
            }
            // the outer edge
            using (var pen = new Pen(Color.FromArgb(120, 0, 0, 0), Math.Max(1.5f, size / 120f))) g.DrawEllipse(pen, disc);

            // label: the cover, as a circle
            float lr = R * 0.34f;
            var label = new RectangleF(R - lr, R - lr, lr * 2, lr * 2);
            using (var lp = new GraphicsPath())
            {
                lp.AddEllipse(label);
                var st = g.Save();
                g.SetClip(lp, CombineMode.Intersect);
                if (cover != null)
                {
                    int side = Math.Min(cover.Width, cover.Height);
                    g.DrawImage(cover, label, new RectangleF((cover.Width - side) / 2f, (cover.Height - side) / 2f, side, side), GraphicsUnit.Pixel);
                }
                else using (var b = new SolidBrush(c1)) g.FillEllipse(b, label);
                g.Restore(st);
                using var pen = new Pen(Color.FromArgb(90, 0, 0, 0), 1.5f);
                g.DrawEllipse(pen, label);
            }
            // centre hole (transparent)
            float hr = Math.Max(2.5f, R * 0.035f);
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var b = new SolidBrush(Color.Transparent)) g.FillEllipse(b, R - hr, R - hr, hr * 2, hr * 2);
            return bmp;
        }

        /// <summary>The light reflection on top (it doesn't turn with the record).</summary>
        public static void DrawSheen(Graphics g, RectangleF disc)
        {
            using var clip = new GraphicsPath();
            clip.AddEllipse(disc);
            var st = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            float cx = disc.X + disc.Width / 2, cy = disc.Y + disc.Height / 2;
            for (int i = 0; i < 2; i++)
            {
                using var p = new GraphicsPath();
                float a0 = i == 0 ? -60 : 120;
                p.AddPie(disc.X, disc.Y, disc.Width, disc.Height, a0, 28);
                using var b = new PathGradientBrush(p)
                {
                    CenterPoint = new PointF(cx, cy),
                    CenterColor = Color.FromArgb(0, 255, 255, 255),
                    SurroundColors = new[] { Color.FromArgb(38, 255, 255, 255) },
                };
                g.FillPath(b, p);
            }
            g.Restore(st);
        }
    }
}
