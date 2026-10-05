using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>The bottom-left box: live CPU and RAM use, plus a Task Manager button.</summary>
    sealed class PerfBoxForm : BarForm
    {
        readonly Font labelFont = new Font("Segoe UI", 7.5f);
        readonly Font valueFont = new Font("Segoe UI Semibold", 9f);
        Font glyphFont;
        int barHeight;
        System.Threading.Thread poller;
        volatile bool stopPolling;

        // latest readings
        float cpu = -1, ram = -1;
        double ramUsedGb, ramTotalGb;
        readonly float[] cpuHistory = new float[30], ramHistory = new float[30]; // last 30 s, for the little graphs
        int historyCount, ramCount;

        public Action HideBox;

        public PerfBoxForm(Settings settings, Backdrop backdrop) : base(settings, backdrop)
        {
            Text = "WispR Performance";
            _ = Handle;
            StartPolling();
        }

        public void SetHeight(int h) { barHeight = h; }

        public override void ApplyTheme()
        {
            glyphFont?.Dispose();
            glyphFont = new Font(GlyphFamily, 11.5f);
            base.ApplyTheme();
            RequestRelayout();
        }

        // ---------- readings (background thread) ----------

        void StartPolling()
        {
            poller = new System.Threading.Thread(() =>
            {
                long lastIdle = 0, lastTotal = 0;
                while (!stopPolling)
                {
                    try
                    {
                        float c = -1;
                        if (GetSystemTimes(out long idle, out long kernel, out long user))
                        {
                            long total = kernel + user; // kernel time includes idle time
                            if (lastTotal > 0 && total > lastTotal)
                                c = 100f * (1f - (float)(idle - lastIdle) / (total - lastTotal));
                            lastIdle = idle; lastTotal = total;
                        }
                        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                        bool memOk = GlobalMemoryStatusEx(ref mem);
                        float r = memOk ? mem.dwMemoryLoad : -1;
                        double totalGb = mem.ullTotalPhys / 1073741824.0, usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke((Action)(() => Apply(c, r, usedGb, totalGb)));
                    }
                    catch { }
                    System.Threading.Thread.Sleep(1000);
                }
            })
            { IsBackground = true, Name = "PerfStatus" };
            poller.Start();
        }

        void Apply(float c, float r, double usedGb, double totalGb)
        {
            if (c >= 0)
            {
                cpu = Math.Max(0, Math.Min(100, c));
                Array.Copy(cpuHistory, 1, cpuHistory, 0, cpuHistory.Length - 1);
                cpuHistory[cpuHistory.Length - 1] = cpu;
                historyCount = Math.Min(cpuHistory.Length, historyCount + 1);
            }
            ram = r; ramUsedGb = usedGb; ramTotalGb = totalGb;
            if (r >= 0)
            {
                Array.Copy(ramHistory, 1, ramHistory, 0, ramHistory.Length - 1);
                ramHistory[ramHistory.Length - 1] = r;
                ramCount = Math.Min(ramHistory.Length, ramCount + 1);
            }
            Invalidate();
        }

        // ---------- layout ----------

        public override Size Measure()
        {
            items.Clear();
            int h = barHeight > 0 ? barHeight : S(48);
            int pad = S(4), x = pad, cell = h - S(8), top = (h - cell) / 2;

            void Add(int width, Func<string> tip, Action<Graphics, Rectangle, bool> paint)
            {
                items.Add(new BarItem
                {
                    Bounds = new Rectangle(x, top, width, cell),
                    Tooltip = tip,
                    Paint = paint,
                    Click = (b, p, d) =>
                    {
                        if (b == MouseButtons.Left) OpenTaskManager();
                        else if (b == MouseButtons.Right) ShowPerfMenu(p);
                    },
                });
                x += width + S(2);
            }

            Add(S(28), () => "Task Manager", (g, r, hv) => DrawGlyph(g, "", glyphFont, r, T.Text));
            Add(S(74), () => cpu < 0 ? "CPU" : "CPU " + Math.Round(cpu) + "%\nGraph: last 30 seconds · click for Task Manager",
                (g, r, hv) => PaintMeter(g, r, "CPU", cpu, cpuHistory, historyCount));
            Add(S(74), () => ram < 0 ? "Memory" : "Memory " + Math.Round(ram) + "%  ·  " + ramUsedGb.ToString("0.0") + " of " + ramTotalGb.ToString("0.0") + " GB in use\nClick for Task Manager",
                (g, r, hv) => PaintMeter(g, r, "RAM", ram, ramHistory, ramCount));

            return new Size(x - S(2) + pad, h);
        }

        /// <summary>Label + percentage on top, a mini graph (CPU) in the middle, a load bar at the bottom.</summary>
        void PaintMeter(Graphics g, Rectangle r, string label, float value, float[] hist, int count)
        {
            var inner = Rectangle.Inflate(r, -S(6), -S(4));
            var color = LoadColor(value);
            var row = new Rectangle(inner.X, inner.Y, inner.Width, valueFont.Height);
            TextRenderer.DrawText(g, label, labelFont, row, T.SubText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value < 0 ? "\u2013" : Math.Round(value) + "%", valueFont, row, T.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var track = new Rectangle(inner.X, inner.Bottom - S(4), inner.Width, S(4));
            using (var p = Rounded(track, track.Height / 2f)) using (var b = new SolidBrush(T.Border)) g.FillPath(b, p);
            if (value < 0) return;

            if (count > 1)
            {
                var area = new Rectangle(inner.X, row.Bottom + S(1), inner.Width, track.Y - row.Bottom - S(3));
                if (area.Height >= S(3))
                {
                    int n = count, len = hist.Length;
                    var pts = new PointF[n];
                    for (int i = 0; i < n; i++)
                        pts[i] = new PointF(area.Right - (n - 1 - i) * area.Width / (float)(len - 1),
                                            area.Bottom - area.Height * hist[len - n + i] / 100f);
                    using var pen = new Pen(Color.FromArgb(150, color), Math.Max(1f, s));
                    g.DrawLines(pen, pts);
                }
            }

            int w = Math.Max(track.Height, (int)(track.Width * value / 100f));
            using (var p = Rounded(new Rectangle(track.X, track.Y, w, track.Height), track.Height / 2f))
            using (var b = new SolidBrush(color))
                g.FillPath(b, p);
        }

        Color LoadColor(float v)
        {
            if (v >= 85) return T.IsLight ? Color.FromArgb(220, 38, 38) : Color.FromArgb(248, 113, 113);   // red
            if (v >= 60) return T.IsLight ? Color.FromArgb(217, 119, 6) : Color.FromArgb(251, 191, 36);    // amber
            return T.Accent;
        }

        static void OpenTaskManager()
        {
            try { Launcher.Start("taskmgr.exe", null, null); } catch { }
        }

        void ShowPerfMenu(Point pt)
        {
            var m = NewMenu();
            AddItem(m, "Task Manager", OpenTaskManager);
            AddItem(m, "Resource Monitor", () => Launcher.Start("resmon.exe", null, null));
            m.AddSeparator();
            AddItem(m, "Hide this box", () => HideBox?.Invoke());
            PopUp(m, pt);
        }

        protected override void OnBackgroundRightClick(Point screen) => ShowPerfMenu(screen);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { stopPolling = true; labelFont.Dispose(); valueFont.Dispose(); glyphFont?.Dispose(); }
            base.Dispose(disposing);
        }

        // ---------- Win32 ----------

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    }
}
