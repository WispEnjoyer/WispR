using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WispR
{
    sealed class TrayIcon
    {
        public IntPtr Hwnd;
        public uint Uid;
        public Guid Guid;
        public uint CallbackMessage;
        public uint Version;
        public Bitmap Icon;
        public string Tip = "";
        public bool Hidden;
        public string Key => Guid != Guid.Empty ? Guid.ToString() : Hwnd + ":" + Uid;
    }

    /// <summary>
    /// The notification area. Apps add tray icons by sending a message to the window of class
    /// "Shell_TrayWnd". We create our own window of that class, keep it first in line, read the
    /// icons, and pass every message on to Explorer too so its tray stays in sync (and is
    /// complete again when WispR exits).
    /// </summary>
    sealed class TrayService : IDisposable
    {
        delegate IntPtr WndProcFn(IntPtr h, uint msg, IntPtr w, IntPtr l);

        readonly WndProcFn proc;
        readonly uint taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        readonly List<TrayIcon> icons = new List<TrayIcon>();
        readonly Timer keeper = new Timer { Interval = 2000 };
        IntPtr hwnd;
        IntPtr explorerTray;          // to notice when Explorer restarts
        DateTime lastBroadcast = DateTime.MinValue;
        DateTime pendingBroadcastAt = DateTime.MaxValue;

        public IReadOnlyList<TrayIcon> Icons => icons;
        public event Action Changed;

        public TrayService()
        {
            proc = WndProc;
        }

        public void Start()
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = "Shell_TrayWnd",
            };
            RegisterClassEx(ref wc);
            hwnd = CreateWindowEx(0x8 | 0x80 /* TOPMOST | TOOLWINDOW */, "Shell_TrayWnd", "", 0x80000000 /* WS_POPUP */,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;
            BringToFront();
            explorerTray = ExplorerTray();

            // Ask every running app to announce its tray icons once. (Apps like Wallpaper Engine
            // treat this as "Explorer restarted" and reload, so it must never be repeated casually.)
            Broadcast();
            keeper.Tick += (s, e) => Keep();
            keeper.Start();
        }

        /// <summary>Apps find the tray with FindWindow, which returns the top-most match — so stay on top.</summary>
        void Keep()
        {
            if (hwnd == IntPtr.Zero) return;
            if (Native.FindWindow("Shell_TrayWnd", null) != hwnd) BringToFront();

            // Only if Explorer really restarted (its tray window is new) ask apps to re-add their
            // icons to us — a few seconds later, and at most once a minute.
            var ex = ExplorerTray();
            if (ex != IntPtr.Zero && ex != explorerTray)
            {
                explorerTray = ex;
                pendingBroadcastAt = DateTime.Now.AddSeconds(4);
            }
            if (DateTime.Now >= pendingBroadcastAt && (DateTime.Now - lastBroadcast).TotalSeconds > 60)
            {
                pendingBroadcastAt = DateTime.MaxValue;
                BringToFront();
                Broadcast();
            }

            // Remove icons whose app has gone away without saying goodbye.
            if (icons.RemoveAll(i => !Native.IsWindow(i.Hwnd)) > 0) Changed?.Invoke();
        }

        /// <summary>Some apps place their popups next to the "taskbar" window, so put ours where the bars are.</summary>
        public void SetRect(Rectangle r)
        {
            if (hwnd != IntPtr.Zero)
                Native.SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x4 | 0x10 /* NOZORDER | NOACTIVATE */);
        }

        void BringToFront() =>
            Native.SetWindowPos(hwnd, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE|NOMOVE|NOACTIVATE */);

        void Broadcast()
        {
            lastBroadcast = DateTime.Now;
            Native.SendNotifyMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, taskbarCreatedMsg, IntPtr.Zero, IntPtr.Zero);
        }

        IntPtr ExplorerTray()
        {
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
                if (h != hwnd && Native.GetPid(h) != ownPid) return h;
            return IntPtr.Zero;
        }

        IntPtr forwardTo; // Explorer's tray window, remembered between messages

        IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (msg == WM_COPYDATA)
                {
                    var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                    IntPtr result = IntPtr.Zero;
                    if (forwardTo == IntPtr.Zero || !Native.IsWindow(forwardTo)) forwardTo = ExplorerTray();
                    var explorer = forwardTo;
                    if (explorer != IntPtr.Zero) // keep Explorer informed (tray icons, app bars, icon positions)
                        Native.SendMessageTimeout(explorer, WM_COPYDATA, w, l, 2 /* ABORTIFHUNG */, 500, out result);

                    if (cds.dwData == (IntPtr)1 && cds.lpData != IntPtr.Zero && cds.cbData >= 8 + 24)
                    {
                        HandleNotifyIcon(cds.lpData, cds.cbData);
                        return (IntPtr)1;
                    }
                    return result;
                }
                if (msg == taskbarCreatedMsg)
                    return IntPtr.Zero; // includes our own broadcast — never answer it with another one
            }
            catch { /* never let a bad message take the tray down */ }
            return DefWindowProc(h, msg, w, l);
        }

        // TRAYNOTIFYDATA: DWORD signature, DWORD message, then a NOTIFYICONDATA with 32-bit handles.
        void HandleNotifyIcon(IntPtr p, int size)
        {
            int message = Marshal.ReadInt32(p, 4);
            IntPtr n = p + 8;
            int avail = size - 8;

            var hWnd = (IntPtr)Marshal.ReadInt32(n, 4);
            uint uid = (uint)Marshal.ReadInt32(n, 8);
            uint flags = (uint)Marshal.ReadInt32(n, 12);
            var guid = Guid.Empty;
            if ((flags & NIF_GUID) != 0 && avail >= 952)
            {
                var b = new byte[16];
                Marshal.Copy(n + 936, b, 0, 16);
                guid = new Guid(b);
            }

            var icon = icons.FirstOrDefault(i => guid != Guid.Empty ? i.Guid == guid : i.Hwnd == hWnd && i.Uid == uid);

            switch (message)
            {
                case NIM_ADD:
                case NIM_MODIFY:
                    if (icon == null)
                    {
                        if (message == NIM_MODIFY) return;
                        if (icons.Count >= 256) return; // a misbehaving app can't flood the tray
                        icon = new TrayIcon { Hwnd = hWnd, Uid = uid, Guid = guid };
                        icons.Add(icon);
                    }
                    if ((flags & NIF_MESSAGE) != 0) icon.CallbackMessage = (uint)Marshal.ReadInt32(n, 16);
                    if ((flags & NIF_ICON) != 0)
                    {
                        icon.Icon?.Dispose();
                        icon.Icon = CopyIconBitmap((IntPtr)Marshal.ReadInt32(n, 20));
                    }
                    if ((flags & NIF_TIP) != 0 && avail >= 280)
                    {
                        string tip = Marshal.PtrToStringUni(n + 24, 128);
                        int z = tip.IndexOf('\0');
                        icon.Tip = z >= 0 ? tip.Substring(0, z) : tip;
                    }
                    if ((flags & NIF_STATE) != 0 && avail >= 288)
                    {
                        uint state = (uint)Marshal.ReadInt32(n, 280), mask = (uint)Marshal.ReadInt32(n, 284);
                        if ((mask & NIS_HIDDEN) != 0) icon.Hidden = (state & NIS_HIDDEN) != 0;
                    }
                    break;
                case NIM_DELETE:
                    if (icon != null) { icon.Icon?.Dispose(); icons.Remove(icon); }
                    break;
                case NIM_SETVERSION:
                    if (icon != null && avail >= 804) icon.Version = (uint)Marshal.ReadInt32(n, 800);
                    return; // nothing visible changed
                default:
                    return;
            }
            // Apps that only update their tooltip (CPU meters, sync tools…) don't need a relayout:
            // the tooltip text is read when it's shown.
            if (message == NIM_MODIFY && (flags & (NIF_ICON | NIF_STATE)) == 0) return;
            Changed?.Invoke();
        }

        static Bitmap CopyIconBitmap(IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return null;
            IntPtr copy = Native.CopyIcon(hicon);
            if (copy == IntPtr.Zero) return null;
            try { using var ic = System.Drawing.Icon.FromHandle(copy); return ic.ToBitmap(); }
            catch { return null; }
            finally { Native.DestroyIcon(copy); }
        }

        /// <summary>Forwards a click on our copy of the icon to the app, the way Explorer does.</summary>
        public void Click(TrayIcon icon, MouseButtons button, bool doubleClick, Point screen)
        {
            if (!Native.IsWindow(icon.Hwnd) || icon.CallbackMessage == 0) return;
            Native.AllowSetForegroundWindow(Native.GetPid(icon.Hwnd)); // so the app can show its menu in front

            void Send(int mouseMsg)
            {
                if (icon.Version >= 4)
                    Native.SendNotifyMessage(icon.Hwnd, icon.CallbackMessage, Native.MakeLParam(screen.X, screen.Y), Native.MakeLParam(mouseMsg, (int)icon.Uid));
                else
                    Native.SendNotifyMessage(icon.Hwnd, icon.CallbackMessage, (IntPtr)icon.Uid, (IntPtr)mouseMsg);
            }

            switch (button)
            {
                case MouseButtons.Left:
                    if (doubleClick) { Send(0x203 /* LBUTTONDBLCLK */); Send(0x202); }
                    else { Send(0x201 /* LBUTTONDOWN */); Send(0x202 /* LBUTTONUP */); if (icon.Version >= 4) Send(0x400 /* NIN_SELECT */); }
                    break;
                case MouseButtons.Right:
                    Send(0x204 /* RBUTTONDOWN */); Send(0x205 /* RBUTTONUP */);
                    if (icon.Version >= 4) Send(0x7B /* WM_CONTEXTMENU */);
                    break;
                case MouseButtons.Middle:
                    Send(0x207); Send(0x208);
                    break;
            }
        }

        public void Dispose()
        {
            keeper.Stop();
            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
                UnregisterClass("Shell_TrayWnd", GetModuleHandle(null));
                // No broadcast here: every message was also passed to Explorer, so its tray is already complete.
            }
            foreach (var i in icons) i.Icon?.Dispose();
            icons.Clear();
        }

        // ---------- interop ----------

        const uint WM_COPYDATA = 0x4A;
        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_GUID = 0x20;
        const uint NIS_HIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string cls, IntPtr inst);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    }
}
