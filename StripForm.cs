using System;
using System.Drawing;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// A full-width band behind the taskbar and system box, shown while a window is maximized,
    /// so the space reserved for the bars doesn't show bits of desktop around them.
    /// </summary>
    sealed class StripForm : BarForm
    {
        public Action<Point> RightClick;

        public StripForm(Settings settings, Backdrop backdrop) : base(settings, backdrop)
        {
            Text = "WispR Strip";
            _ = Handle;
        }

        protected override bool RoundCorners => false;

        public override Size Measure() => Size;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool bottom = settings.TaskbarEdge != "Top";
            using var pen = new Pen(T.Border);
            int y = bottom ? 0 : Height - 1;
            e.Graphics.DrawLine(pen, 0, y, Width, y); // hairline towards the windows
        }

        protected override void OnBackgroundRightClick(Point screen) => RightClick?.Invoke(screen);
    }
}
