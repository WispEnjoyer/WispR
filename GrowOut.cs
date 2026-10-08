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
            public bool Resizing;          // Resize() is moving it: the outline is redrawn once, afterwards
            public bool Morphing;          // Morph() is animating a size change as one picture; the popup is invisible
            public bool HoldOutline;       // the outline keeps showing the animation picture until the popup is drawn
            public Action MorphDone;
            public double MorphOpacity = 1;
            public bool Proxying;          // the outline window is showing the animation picture; the popup itself is invisible
            public double TargetOpacity = 1;
            public Rectangle ProxyFrame;   // where the full picture goes when fully out
            public int ProxyRows;          // how many of its rows show right now
        }

        static readonly Dictionary<Form, State> states = new Dictionary<Form, State>();

        /// <summary>
        /// The popup's opacity when shown. Never quite 1: at exactly 1 Windows Forms turns the window from
        /// layered back into a normal one, which redraws it from scratch and lets what's behind it show
        /// through for a frame. Just under 1 it stays layered, keeps its picture, and looks the same.
        /// </summary>
        static double Shown(double o) => Math.Max(0.02, Math.Min(0.996, o));

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
            st.Radius = Ui.CornerPx(scale);
            st.Flare = curves ? Ui.CornerPx(scale) : 1;
            st.Theme = theme;
            st.Background = background;
            st.Bg?.Dispose(); st.Bg = null;
            if (st.Shell == null || st.Shell.IsDisposed) st.Shell = new LauncherShell();
            if (f.Owner != st.Shell) f.Owner = st.Shell; // the popup always stays in front of its outline
            if (!st.Hooked)
            {
                st.Hooked = true;
                f.VisibleChanged += (o, e) => { if (!f.Visible) Hide(f); };
                f.LocationChanged += (o, e) => { if (f.Visible && !st.Proxying && !st.Resizing && !st.Morphing && !Anim.IsRunning(Key(f))) Render(f); };
                f.SizeChanged += (o, e) => { if (f.Visible && !st.Proxying && !st.Resizing && !st.Morphing && !Anim.IsRunning(Key(f))) Render(f); };
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
            // The popup goes straight to its place but stays invisible; a picture of it together with its
            // outline rises out of the edge in the outline window (one window, nothing redrawn per frame),
            // and the real popup takes over at the end. Started a moment later, so the popup's own layout
            // (search box, scroll position…) is done before the picture is taken.
            Anim.Stop(Key(f)); // reopened while sinking back: that animation must not finish (and hide it)
            if (!st.Proxying) st.TargetOpacity = f.Opacity > 0.02 ? f.Opacity : alpha / 255.0;
            st.Proxying = true; // first: moving the popup must not redraw the outline
            f.Opacity = 0;
            f.Region = null;
            f.Top = finalTop;
            if (!f.Visible) f.Show();
            f.BeginInvoke((Action)(() =>
            {
                if (f.IsDisposed || !f.Visible || !st.Proxying || st.Closing) return;
                if (!PrepareProxy(f, st)) { FinishProxy(f, st); Render(f); return; }
                int from = st.ProxyRows, total = st.ProxyFrame.Height;
                Anim.Run(Key(f), ms, e =>
                {
                    if (f.IsDisposed) return;
                    int rows = (int)Math.Round(Anim.Lerp(from, total, e));
                    ShowRows(st, rows, e);
                }, () =>
                {
                    if (f.IsDisposed) return;
                    f.Update();          // the real popup is drawn (still invisible)…
                    FinishProxy(f, st);  // …shown…
                    Render(f);           // …and only then does the outline give up the picture
                }, Anim.OutQuint);
            }));
        }

        /// <summary>Takes the picture of the popup with its outline and loads it into the outline window.</summary>
        static bool PrepareProxy(Form f, State st)
        {
            if (st.Shell == null || st.Shell.IsDisposed || f.Width <= 0 || f.Height <= 0) return false;
            try
            {
                int m = Inset(st.Radius), fl = st.Flare;
                var hole = new Rectangle(f.Left, st.Edge - f.Height, f.Width, f.Height);
                var frame = new Rectangle(hole.X - m - fl, hole.Y - m, hole.Width + (m + fl) * 2, hole.Height + m);
                Bitmap bg = null;
                if (st.Background != null)
                {
                    if (st.Bg == null || st.BgRect != frame) { st.Bg?.Dispose(); st.Bg = st.Background(frame); st.BgRect = frame; }
                    bg = st.Bg;
                }
                using var shot = new Bitmap(f.Width, f.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                f.DrawToBitmap(shot, new Rectangle(0, 0, f.Width, f.Height));
                using var picture = LauncherShell.Build(frame, hole, fl, st.Radius, bg, st.BgRect.Location, st.Theme.Background, st.Theme.Border, shot);
                st.Shell.BeginProxy(picture);
                st.ProxyFrame = frame;
                if (st.ProxyRows <= 0 || st.ProxyRows > frame.Height) st.ProxyRows = 0;
                return true;
            }
            catch (Exception ex) { Log.Error("GrowOut.PrepareProxy", ex); return false; }
        }

        /// <summary>Shows the top <paramref name="rows"/> rows of the picture, standing on the edge; fades in over the first part.</summary>
        static void ShowRows(State st, int rows, double fade)
        {
            st.ProxyRows = rows;
            var fr = st.ProxyFrame;
            byte a = (byte)Math.Round(st.Alpha * Math.Min(1.0, 0.35 + fade * 2.2));
            st.Shell.ShowProxy(new Point(fr.X, fr.Bottom - Math.Max(1, rows)), rows, a);
        }

        static void FinishProxy(Form f, State st)
        {
            st.Proxying = false;
            st.ProxyRows = 0;
            if (!f.IsDisposed && Math.Abs(f.Opacity - Shown(st.TargetOpacity)) > 0.001) f.Opacity = Shown(st.TargetOpacity);
            st.Shell?.EndProxy();
        }

        static object Key(Form f) => ("grow", f);

        /// <summary>
        /// The way out: the popup sinks back into the edge it grew from, then <paramref name="finish"/> runs
        /// (which hides it). Popups that didn't grow out of an edge just finish straight away.
        /// </summary>
        public static void Close(Form f, Action finish, int ms = 130)
        {
            if (f.IsDisposed || !f.Visible || !states.TryGetValue(f, out var st) || st.Shell == null || st.Shell.IsDisposed || !st.Shell.Visible)
            { finish(); return; }
            EndMorph(f);
            st.Closing = true;
            if (!st.Proxying)
            {
                // take the picture of the popup as it is now, show it in the outline window, then let the
                // popup itself go invisible: the picture sinks back into the edge
                st.ProxyRows = 0;
                if (!PrepareProxy(f, st)) { st.Closing = false; finish(); return; }
                st.TargetOpacity = f.Opacity > 0.02 ? f.Opacity : st.TargetOpacity;
                ShowRows(st, st.ProxyFrame.Height, 1);
                st.Proxying = true;
                f.Opacity = 0;
            }
            int from = st.ProxyRows > 0 ? st.ProxyRows : st.ProxyFrame.Height;
            Anim.Run(Key(f), ms, e =>
            {
                if (f.IsDisposed) return;
                ShowRows(st, (int)Math.Round(from * (1 - e)), 1 - e * 0.6);
            }, () =>
            {
                st.Closing = false;
                if (f.IsDisposed) return;
                st.Shell?.Hide();
                finish();              // hidden first…
                FinishProxy(f, st);    // …then its opacity goes back (no one-frame flash)
            }, e => e * e);
        }

        /// <summary>
        /// Gives an open popup new bounds (taller, say), its outline following in the same step. The popup's
        /// bottom should stay on the edge it grew from. Popups that didn't grow out of an edge just move.
        /// </summary>
        public static void Resize(Form f, Rectangle bounds)
        {
            if (f.IsDisposed) return;
            if (!states.TryGetValue(f, out var st) || st.Shell == null || st.Shell.IsDisposed || !st.Shell.Visible || st.Proxying || st.Closing)
            {
                Move(f, bounds);
                return;
            }
            st.Resizing = true;
            try
            {
                // the outline first (it's behind and bigger), then the popup: no frame where they don't meet
                if (!st.HoldOutline) RenderFor(f, st, bounds);
                Move(f, bounds);
            }
            finally { st.Resizing = false; }
        }

        /// <summary>
        /// Animates an open popup getting taller or shorter (its bottom stays on the edge) as one picture in the
        /// outline window, so the two never come apart. <paramref name="content"/> is the popup drawn at the
        /// larger of the two heights; each frame shows its bottom rows. The popup itself is invisible meanwhile;
        /// <paramref name="done"/> gives it its new size, then it shows again. False when it can't (then resize it yourself).
        /// </summary>
        public static bool Morph(Form f, Bitmap content, int fromH, int toH, int ms, Action done)
        {
            if (f.IsDisposed || !f.Visible || !states.TryGetValue(f, out var st) || st.Shell == null || st.Shell.IsDisposed
                || !st.Shell.Visible || st.Proxying || st.Closing || st.Background != null) return false;
            EndMorph(f);
            int m = Inset(st.Radius), fl = st.Flare, w = f.Width, left = f.Left;
            void FrameAt(int h)
            {
                h = Math.Max(1, Math.Min(content.Height, h));
                var hole = new Rectangle(left, st.Edge - h, w, h);
                var frame = new Rectangle(hole.X - m - fl, hole.Y - m, w + (m + fl) * 2, h + m);
                using var pic = LauncherShell.Build(frame, hole, fl, st.Radius, null, Point.Empty, st.Theme.Background, st.Theme.Border, content,
                                                    holeSource: new Rectangle(0, content.Height - h, Math.Min(w, content.Width), h));
                st.Shell.Present(pic, frame.Location, st.Alpha);
            }
            try { FrameAt(fromH); }
            catch (Exception ex) { Log.Error("GrowOut.Morph", ex); return false; }
            st.Morphing = true;
            st.MorphDone = done;
            st.MorphOpacity = f.Opacity > 0.02 ? f.Opacity : st.TargetOpacity;
            f.Opacity = 0; // the picture has taken over
            Anim.Run(Key(f), ms, e =>
            {
                if (f.IsDisposed || !st.Morphing) return;
                try { FrameAt((int)Math.Round(Anim.Lerp(fromH, toH, e))); } catch { }
            }, () => EndMorph(f), Anim.OutCubic);
            return true;
        }

        public static bool IsMorphing(Form f) => states.TryGetValue(f, out var st) && st.Morphing;

        /// <summary>Ends a <see cref="Morph"/> now: the popup gets its new size and shows again.</summary>
        public static void EndMorph(Form f)
        {
            if (!states.TryGetValue(f, out var st) || !st.Morphing) return;
            Anim.Stop(Key(f));
            st.Morphing = false;
            var d = st.MorphDone; st.MorphDone = null;
            if (f.IsDisposed) return;
            // the popup gets its new size and is drawn while still invisible; the outline keeps showing the
            // last animation frame (which looks the same) until the popup is back on top of it
            st.HoldOutline = true;
            try { d?.Invoke(); } catch (Exception ex) { Log.Error("GrowOut.EndMorph", ex); }
            finally { st.HoldOutline = false; }
            if (!f.Visible) { f.Opacity = Shown(st.MorphOpacity); return; }
            f.Update();
            f.Opacity = Shown(st.MorphOpacity);
            Render(f);
        }

        static void Move(Form f, Rectangle b)
        {
            if (f.Bounds == b) return;
            if (!f.IsHandleCreated) { f.Bounds = b; return; }
            // without copying the old picture over: the popup draws itself fresh at once
            Native.SetWindowPos(f.Handle, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, 0x0004 | 0x0010 | 0x0100 /* NOZORDER | NOACTIVATE | NOCOPYBITS */);
            f.Invalidate();
            f.Update();
        }

        /// <summary>On its way out (clicking its button again should open it again, not close it twice).</summary>
        public static bool IsClosing(Form f) => states.TryGetValue(f, out var st) && st.Closing && Anim.IsRunning(Key(f));

        static void Render(Form f)
        {
            if (!states.TryGetValue(f, out var st) || st.Shell == null || st.Shell.IsDisposed || !f.Visible) return;
            RenderFor(f, st, f.Bounds);
        }

        static void RenderFor(Form f, State st, Rectangle fb)
        {
            int m = Inset(st.Radius), r = st.Radius, fl = st.Flare;
            int top = Math.Min(fb.Top - m, st.Edge - r * 2 - m); // stays a valid shape while still small
            var frame = new Rectangle(fb.Left - m - fl, top, fb.Width + (m + fl) * 2, st.Edge - top);
            var hole = Rectangle.Intersect(fb, new Rectangle(fb.Left, fb.Top, fb.Width, Math.Max(0, st.Edge - fb.Top)));
            Bitmap bg = null;
            if (st.Background != null)
            {
                // one picture for the whole final outline, reused for every animation frame
                var full = new Rectangle(fb.Left - m - fl, st.Edge - fb.Height - m, fb.Width + (m + fl) * 2, fb.Height + m);
                if (st.Bg == null || st.BgRect != full) { st.Bg?.Dispose(); st.Bg = st.Background(full); st.BgRect = full; }
                bg = st.Bg;
            }
            try { st.Shell.Render(frame, hole, fl, r, bg, st.BgRect.Location, st.Theme.Background, st.Theme.Border, st.Alpha); }
            catch (Exception ex) { Log.Error("GrowOut.Render", ex); }
        }

        public static void Hide(Form f)
        {
            if (states.TryGetValue(f, out var ms) && ms.Morphing) { ms.Morphing = false; ms.MorphDone = null; if (!f.IsDisposed) f.Opacity = Shown(ms.MorphOpacity); }
            Anim.Stop(Key(f));
            if (states.TryGetValue(f, out var ps) && ps.Proxying) { ps.Closing = false; FinishProxy(f, ps); }
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
