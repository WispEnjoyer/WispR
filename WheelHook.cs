using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Mouse-wheel input for our windows that never take focus (Start menu, system box).
    /// Windows sends wheel events to the focused window, so ours wouldn't get them; this
    /// catches wheel turns over a given window directly. Only installed while needed.
    /// </summary>
    sealed class WheelHook : IDisposable
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        readonly Control target;
        readonly Action<int, Point> onWheel; // (delta, screen point)
        readonly HookProc proc;
        IntPtr hook;

        public WheelHook(Control target, Action<int, Point> onWheel)
        {
            this.target = target;
            this.onWheel = onWheel;
            proc = Callback;
        }

        public void Install()
        {
            if (hook != IntPtr.Zero) return;
            hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, GetModuleHandle(null), 0);
        }

        public void Uninstall()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam == (IntPtr)0x20A /* WM_MOUSEWHEEL */)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.x, info.y);
                if (target.Visible && !target.IsDisposed && target.Bounds.Contains(pt))
                {
                    int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    target.BeginInvoke((Action)(() => onWheel(delta, pt))); // keep the hook itself instant
                    return (IntPtr)1; // handled: don't also scroll the window underneath
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose() => Uninstall();

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    }
}
