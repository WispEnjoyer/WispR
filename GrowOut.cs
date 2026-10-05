using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The "grows out of the bar" look for popups (Start menu, volume, calendar, previews, menus):
    /// the popup sits directly on the bar band, rounded at the top, its sides curving out into the band,
    /// and it rises out of it when it opens. The popup window itself stays square; a per-pixel
    /// transparent outline window behind it (<see cref="LauncherShell"/>) draws the smooth shape.
    /// </summary>
    static class GrowOut
    {
        sealed class State
        {
            public LauncherShell Shell;
            public int Edge, Flare, Radius;
            public Theme Theme;
            public Func<Rectangle, Bitmap> Background; // picture for the outline (null: theme colour)
            public Bitmap Bg; public Rectangle BgRect;
            public bool Hooked;
            public byte Alpha = 255;
            public bool Closing;
        }

        static readonly Dictionary<Form, State> states = new Dictionary<Form, State>();

        public static int Inset(int radius) => (int)Math.Ceiling(radius * 0.3) + 1;

        /// <summary>Square corners, no border, no shadow — the outline window does the shape. Call once the handle exists.</summary>
        public static void PrepareWindow(Form f)
        {
            try
            {
                int round = 1 /* DONOTROUND */, none = unchecked((int)0xFFFFFFFE);
                Native.DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
                Native.DwmSetWindowAttribute(f.Handle, 34, ref none, 4);
            }
            catch { }
        }

        /// <summary>
        /// Shows <paramref name="f"/> (already placed with its bottom on <paramref name="edge"/>) rising out of
        /// that edge. <paramref name="curves"/>: curve into the edge (there's a band to curve into).
        /// </summary>
        public static void Show(Form f, int edge, bool curves, Theme theme, float scale,
                                Func<Rectangle, Bitmap> background = null, bool animate = true, int ms = 170, byte alpha = 255)
        {
            if (!states.TryGetValue(f, out var st)) states[f] = st = new State();
            st.Alpha = alpha;
            st.Closing = false;
            st.Edge = edge;
            st.Radius = (int)(14 * scale);
            st.Flare = curves ? (int)(14 * scale) : 1;
            st.Theme = theme;
            st.Background = background;
            st.Bg?.Dispose(); st.Bg = null;
            if (st.Shell == null || st.Shell.IsDisposed) st.Shell = new LauncherShell();
            if (f.Owner != st.Shell) f.Owner = st.Shell; // the popup always stays in front of its outline
            if (!st.Hooked)
            {
                st.Hooked = true;
                f.VisibleChanged += (o, e) => { if (!f.Visible) Hide(f); };
                f.LocationChanged += (o, e) => { if (f.Visible && !Anim.IsRunning(Key(f))) Render(f); };
                f.SizeChanged += (o, e) => { if (f.Visible && !Anim.IsRunning(Key(f))) Render(f); };
                f.Disposed += (o, e) => { if (states.TryGetValue(f, out var s2)) { s2.Shell?.Dispose(); s2.Bg?.Dispose(); states.Remove(f); } };
            }

            int finalTop = edge - f.Height, h = f.Height, w = f.Width;
            if (!animate)
            {
                f.Region = null;
                if (!f.Visible) f.Show();
                Render(f);
                return;
            }
            f.Top = edge;
            f.Region = new Region(Rectangle.Empty);
            if (!f.Visible) f.Show();
            Anim.Run(Key(f), ms, e =>
            {
                if (f.IsDisposed) return;
                int visible = (int)Math.Round(h * e);
                f.Top = edge - visible;
                var old = f.Region;
                f.Region = new Region(new Rectangle(0, 0, w, visible));
                old?.Dispose();
                Render(f);
            }, () =>
            {
                if (f.IsDisposed) return;
                f.Top = finalTop;
                var old = f.Region; f.Region = null; old?.Dispose();
                Render(f);
            }, Anim.OutCubic);
        }

        static object Key(Form f) => ("grow", f);

        /// <summary>
        /// The way out: the popup sinks back into the edge it grew from, then <paramref name="finish"/> runs
        /// (which hides it). Popups that didn't grow out of an edge just finish straight away.
        /// </summary>
        public static void Close(Form f, Action finish, int ms = 120)
        {
            if (f.IsDisposed || !f.Visible || !states.TryGetValue(f, out var st) || st.Shell == null || st.Shell.IsDisposed || !st.Shell.Visible)
            { finish(); return; }
            int w = f.Width, from = Math.Max(0, st.Edge - f.Top);
            if (from <= 0) { finish(); return; }
            st.Closing = true;
            Anim.Run(Key(f), ms, e =>
            {
                if (f.IsDisposed) return;
                int visible = (int)Math.Round(from * (1 - e));
                f.Top = st.Edge - visible;
                var old = f.Region;
                f.Region = new Region(new Rectangle(0, 0, w, visible));
                old?.Dispose();
                Render(f);
            }, () => { st.Closing = false; if (!f.IsDisposed) finish(); }, e => e * e);
        }

        /// <summary>On its way out (clicking its button again should open it again, not close it twice).</summary>
        public static bool IsClosing(Form f) => states.TryGetValue(f, out var st) && st.Closing && Anim.IsRunning(Key(f));

        static void Render(Form f)
        {
            if (!states.TryGetValue(f, out var st) || st.Shell == null || st.Shell.IsDisposed || !f.Visible) return;
            int m = Inset(st.Radius), r = st.Radius, fl = st.Flare;
            int top = Math.Min(f.Top - m, st.Edge - r * 2 - m); // stays a valid shape while still small
            var frame = new Rectangle(f.Left - m - fl, top, f.Width + (m + fl) * 2, st.Edge - top);
            var hole = Rectangle.Intersect(f.Bounds, new Rectangle(f.Left, f.Top, f.Width, Math.Max(0, st.Edge - f.Top)));
            Bitmap bg = null;
            if (st.Background != null)
            {
                // one picture for the whole final outline, reused for every animation frame
                var full = new Rectangle(f.Left - m - fl, st.Edge - f.Height - m, f.Width + (m + fl) * 2, f.Height + m);
                if (st.Bg == null || st.BgRect != full) { st.Bg?.Dispose(); st.Bg = st.Background(full); st.BgRect = full; }
                bg = st.Bg;
            }
            try { st.Shell.Render(frame, hole, fl, r, bg, st.BgRect.Location, st.Theme.Background, st.Theme.Border, st.Alpha); }
            catch (Exception ex) { Log.Error("GrowOut.Render", ex); }
        }

        public static void Hide(Form f)
        {
            Anim.Stop(Key(f));
            if (states.TryGetValue(f, out var st) && st.Shell != null && !st.Shell.IsDisposed && st.Shell.Visible) st.Shell.Hide();
            if (!f.IsDisposed && f.Region != null) { var old = f.Region; f.Region = null; old.Dispose(); }
        }

        /// <summary>The edge a popup for the bars grows out of: the band's top, or the top of the box it belongs to.</summary>
        public static (int edge, bool curves) EdgeFor(Rectangle anchorScreen, int fallbackTop)
        {
            int? band = BarForm.BandTopAt?.Invoke(new Point(anchorScreen.X + anchorScreen.Width / 2, anchorScreen.Top));
            return band != null ? (band.Value, true) : (fallbackTop, false);
        }
    }
}
