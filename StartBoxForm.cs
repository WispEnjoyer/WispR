using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// The small box in the bottom-left corner: the Start button (opens the Start menu) and, right next
    /// to it, the power button (lock, sleep, sign out, restart, shut down).
    /// </summary>
    sealed class StartBoxForm : BarForm
    {
        int barHeight;
        Font glyphFont;

        public Action<Rectangle> OpenStartMenu;   // with the Start button's screen rectangle
        public Action BeforeLeaving;              // sign out / restart / shut down: put Windows' taskbar back first
        public Action<Point> BackgroundMenu;

        public StartBoxForm(Settings settings, Backdrop backdrop) : base(settings, backdrop)
        {
            Text = "WispR Start";
            _ = Handle;
        }

        public void SetHeight(int h) { barHeight = h; }

        public override void ApplyTheme()
        {
            glyphFont?.Dispose();
            glyphFont = new Font(GlyphFamily, 12f);
            base.ApplyTheme();
            RequestRelayout();
        }

        int IconPx => settings.TaskbarIconSize == "Small" ? S(23) : settings.TaskbarIconSize == "Large" ? S(34) : S(28);
        int ButtonPx => (settings.TaskbarIconSize == "Small" ? S(20) : settings.TaskbarIconSize == "Large" ? S(30) : S(24)) + S(16);

        public override Size Measure()
        {
            items.Clear();
            int h = barHeight > 0 ? barHeight : S(48);
            int bw = ButtonPx, top = (h - bw) / 2, x = S(4);
            var startRect = new Rectangle(x, top, bw, bw);
            items.Add(new BarItem
            {
                Bounds = startRect,
                Tooltip = () => "Start",
                Paint = (g, r, hv) => PaintStart(g, r),
                Click = (b, p, d) =>
                {
                    if (b == MouseButtons.Left) OpenStartMenu?.Invoke(RectangleToScreen(startRect));
                    else if (b == MouseButtons.Right) BackgroundMenu?.Invoke(p);
                },
            });
            x += bw + S(2);
            var powerRect = new Rectangle(x, top, bw, bw);
            items.Add(new BarItem
            {
                Bounds = powerRect,
                Tooltip = () => "Power",
                Paint = (g, r, hv) => DrawGlyph(g, "", glyphFont, r, T.Text),
                Click = (b, p, d) => { if (b == MouseButtons.Left) PowerMenu(RectangleToScreen(powerRect)); },
            });
            x += bw;
            return new Size(x + S(4), h);
        }

        void PaintStart(Graphics g, Rectangle r)
        {
            int sz = (int)(IconPx * 0.8f), gap = Math.Max(2, sz / 9), cell = (sz - gap) / 2;
            int x0 = r.X + (r.Width - sz) / 2, y0 = r.Y + (r.Height - sz) / 2;
            using var b = new SolidBrush(T.Accent);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    using (var p = Rounded(new Rectangle(x0 + j * (cell + gap), y0 + i * (cell + gap), cell, cell), Math.Max(1, cell / 5f)))
                        g.FillPath(b, p);
        }

        void PowerMenu(Rectangle screenRect)
        {
            var m = NewMenu();
            AddItem(m, "Lock", () => Do(() => LockWorkStation()));
            AddItem(m, "Sleep", () => Do(() => SetSuspendState(false, false, false)));
            AddItem(m, "Sign out", () => Do(() => { BeforeLeaving?.Invoke(); Shutdown("/l"); }));
            m.AddSeparator();
            AddItem(m, "Restart", () => Do(() => { BeforeLeaving?.Invoke(); Shutdown("/r /t 0"); }));
            AddItem(m, "Shut down", () => Do(() => { BeforeLeaving?.Invoke(); Shutdown("/s /t 0"); }));
            PopUp(m, new Point(screenRect.X + screenRect.Width / 2, screenRect.Y));
        }

        static void Do(Action a)
        {
            try { a(); }
            catch (Exception ex) { ShowError("power action", ex); }
        }

        static void Shutdown(string args) =>
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

        protected override void OnBackgroundRightClick(Point screen) => BackgroundMenu?.Invoke(screen);

        protected override void Dispose(bool disposing)
        {
            if (disposing) glyphFont?.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
