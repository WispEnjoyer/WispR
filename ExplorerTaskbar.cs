using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Hides the Windows taskbar while ours is on, and puts it back exactly as it was.
    /// The original auto-hide state is saved in settings first, so even after a crash the next
    /// start (or turning the taskbar off) restores it.
    /// </summary>
    static class ExplorerTaskbar
    {
        const int ABS_AUTOHIDE = 0x1;
        static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        public static IEnumerable<IntPtr> TaskbarWindows()
        {
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr h = IntPtr.Zero;
                while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                    if (Native.GetPid(h) != ownPid) yield return h;
            }
        }

        public static void Hide(Settings st)
        {
            if (st.ExplorerAutoHideOriginal < 0)
            {
                st.ExplorerAutoHideOriginal = GetState();
                st.Save();
            }
            SetState(ABS_AUTOHIDE); // stops Windows reserving screen space for its bar
            EnsureHidden();
        }

        /// <summary>Explorer sometimes shows its bar again (e.g. after Ctrl+Esc or a restart).</summary>
        public static void EnsureHidden()
        {
            foreach (var h in TaskbarWindows())
                if (Native.IsWindowVisible(h)) Native.ShowWindow(h, 0 /* SW_HIDE */);
        }

        public static void Restore(Settings st)
        {
            if (st.ExplorerAutoHideOriginal >= 0)
            {
                SetState(st.ExplorerAutoHideOriginal);
                st.ExplorerAutoHideOriginal = -1;
                st.Save();
            }
            foreach (var h in TaskbarWindows()) Native.ShowWindow(h, 5 /* SW_SHOW */);
        }

        static int GetState()
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return (int)SHAppBarMessage(4 /* ABM_GETSTATE */, ref abd);
        }

        static void SetState(int state)
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), lParam = (IntPtr)state };
            foreach (var h in TaskbarWindows()) { abd.hWnd = h; break; }
            SHAppBarMessage(10 /* ABM_SETSTATE */, ref abd);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge;
            public Native.RECT rc; public IntPtr lParam;
        }

        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    }

    /// <summary>
    /// Reserves a strip at the screen edge so maximized windows stop at our taskbar. The reserving
    /// window is a real window covering the strip (never drawn, click-through): Explorer recalculates
    /// the usable screen area from app bars from time to time, and a hidden, zero-sized window could
    /// silently drop out of that calculation. It also re-registers when Explorer restarts.
    /// </summary>
    sealed class AppBarReservation : NativeWindow, IDisposable
    {
        readonly uint callbackMsg = Native.RegisterWindowMessage("WispRAppBar");
        readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        bool registered, wanted;
        Rectangle screen;
        uint edge;      // ABE_LEFT 0, ABE_TOP 1, ABE_RIGHT 2, ABE_BOTTOM 3
        int thickness;

        public AppBarReservation()
        {
            CreateHandle(new CreateParams
            {
                Style = unchecked((int)0x80000000),            // WS_POPUP
                ExStyle = 0x80 | 0x08000000 | 0x20 | 0x80000,   // TOOLWINDOW | NOACTIVATE | TRANSPARENT | LAYERED (no content → never drawn)
            });
        }

        Rectangle Strip => edge switch
        {
            0 => new Rectangle(screen.Left, screen.Top, thickness, screen.Height),
            1 => new Rectangle(screen.Left, screen.Top, screen.Width, thickness),
            2 => new Rectangle(screen.Right - thickness, screen.Top, thickness, screen.Height),
            _ => new Rectangle(screen.Left, screen.Bottom - thickness, screen.Width, thickness),
        };

        public void Set(Rectangle screenBounds, bool atBottom, int size) => Set(screenBounds, atBottom ? 3u : 1u, size);

        /// <summary>Reserves <paramref name="size"/> pixels along one edge of the screen (Windows keeps windows out of it).</summary>
        public void Set(Rectangle screenBounds, uint screenEdge, int size)
        {
            screen = screenBounds; edge = screenEdge; thickness = size; wanted = true;
            var strip = Strip;
            // visible, bottom of the z-order, exactly over the strip
            Native.SetWindowPos(Handle, (IntPtr)1 /* HWND_BOTTOM */, strip.X, strip.Y, strip.Width, strip.Height, 0x10 | 0x40 /* NOACTIVATE | SHOWWINDOW */);

            var abd = New();
            if (!registered)
            {
                abd.uCallbackMessage = callbackMsg;
                ExplorerTaskbar.SHAppBarMessage(0 /* ABM_NEW */, ref abd);
                registered = true;
            }
            abd.uEdge = edge;
            abd.rc = new Native.RECT { Left = strip.Left, Top = strip.Top, Right = strip.Right, Bottom = strip.Bottom };
            ExplorerTaskbar.SHAppBarMessage(2 /* ABM_QUERYPOS */, ref abd);
            switch (edge)
            {
                case 0: abd.rc.Right = abd.rc.Left + thickness; break;
                case 1: abd.rc.Bottom = abd.rc.Top + thickness; break;
                case 2: abd.rc.Left = abd.rc.Right - thickness; break;
                default: abd.rc.Top = abd.rc.Bottom - thickness; break;
            }
            ExplorerTaskbar.SHAppBarMessage(3 /* ABM_SETPOS */, ref abd);
        }

        /// <summary>Registers again from scratch (Explorer forgets app bars when it restarts).</summary>
        public void Renew()
        {
            if (!wanted) return;
            var abd = New();
            ExplorerTaskbar.SHAppBarMessage(1 /* ABM_REMOVE */, ref abd);
            registered = false;
            Set(screen, edge, thickness);
        }

        public void Remove()
        {
            wanted = false;
            Native.ShowWindow(Handle, 0 /* SW_HIDE */);
            if (!registered) return;
            var abd = New();
            ExplorerTaskbar.SHAppBarMessage(1 /* ABM_REMOVE */, ref abd);
            registered = false;
        }

        ExplorerTaskbar.APPBARDATA New() =>
            new ExplorerTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<ExplorerTaskbar.APPBARDATA>(), hWnd = Handle };

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == callbackMsg)
            {
                // ABN_POSCHANGED: Windows is recalculating the reserved edges and expects every app bar to
                // confirm its place. Not answering let Windows drop the strip now and then, so maximized
                // windows grew under the bars until the watchdog forced the space back — over and over.
                // Answered a moment later (once per burst) so it can never turn into a loop.
                if (m.WParam.ToInt32() == 1 && wanted && registered) ScheduleConfirm();
                return;
            }
            if (m.Msg == taskbarCreated && wanted)
            {
                Log.Throttled("appbar-renew", "Taskbar-created signal: registering the reserved strip again.");
                Renew();
                return;
            }
            if (m.Msg == 0x21 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; return; }
            base.WndProc(ref m);
        }

        System.Windows.Forms.Timer confirmTimer;
        DateTime lastConfirm = DateTime.MinValue;
        int confirmsThisSecond;
        DateTime confirmWindow = DateTime.MinValue;

        void ScheduleConfirm()
        {
            if (confirmTimer == null)
            {
                confirmTimer = new System.Windows.Forms.Timer { Interval = 40 };
                confirmTimer.Tick += (o, e) =>
                {
                    confirmTimer.Stop();
                    if (!wanted || !registered) return;
                    var now = DateTime.Now;
                    if ((now - confirmWindow).TotalSeconds >= 1) { confirmWindow = now; confirmsThisSecond = 0; }
                    if (++confirmsThisSecond > 4) return; // something keeps poking: don't take part in a storm
                    lastConfirm = now;
                    Set(screen, edge, thickness);
                };
            }
            if (!confirmTimer.Enabled) confirmTimer.Start();
        }

        public void Dispose()
        {
            confirmTimer?.Dispose();
            Remove();
            DestroyHandle();
        }
    }
}
