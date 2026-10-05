using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Low-level keyboard hook that detects a lone tap of the Windows key.
    /// Win+anything (Win+E, Win+L, Win+Shift+S …) still works normally;
    /// only a tap on its own is redirected to our launcher.
    /// The hook runs on its own thread so a busy UI can never stall typing.
    /// </summary>
    sealed class KeyboardHook : IDisposable
    {
        public event Action WinTapped;
        /// <summary>Second tap within <see cref="DoubleTapMs"/> of the first.</summary>
        public event Action WinDoubleTapped;
        public volatile bool Enabled = true;
        public volatile int DoubleTapMs = 350;
        int lastTap;

        IntPtr hookId = IntPtr.Zero;
        LowLevelKeyboardProc proc; // keep a reference so the GC doesn't collect the delegate
        Thread thread;
        bool winDown, comboUsed;

        public void Start()
        {
            var ready = new ManualResetEventSlim();
            Exception error = null;
            thread = new Thread(() =>
            {
                proc = HookCallback;
                hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                if (hookId == IntPtr.Zero) error = new Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                if (error == null) Application.Run(); // message loop for hook callbacks
            })
            { IsBackground = true, Name = "KeyboardHook" };
            thread.Start();
            ready.Wait();
            if (error != null) throw error;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (k.dwExtraInfo == OurMarker) return CallNextHookEx(hookId, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool isWin = k.vkCode == VK_LWIN || k.vkCode == VK_RWIN;

                if (isWin)
                {
                    if (down && !winDown) { winDown = true; comboUsed = false; }
                    else if (up && winDown)
                    {
                        winDown = false;
                        if (!comboUsed && Enabled)
                        {
                            // Swallow the real key-up, then inject a harmless "mask" key followed by
                            // the Win key-up. Windows sees Win+<mask> and therefore doesn't open Start.
                            SendMaskedWinUp((ushort)k.vkCode);
                            int now = Environment.TickCount;
                            if (lastTap != 0 && unchecked(now - lastTap) <= DoubleTapMs)
                            {
                                lastTap = 0;
                                WinDoubleTapped?.Invoke();
                            }
                            else
                            {
                                lastTap = now == 0 ? 1 : now;
                                WinTapped?.Invoke();
                            }
                            return (IntPtr)1;
                        }
                    }
                }
                else if (down && winDown)
                {
                    comboUsed = true;
                }
                else if (down)
                {
                    lastTap = 0; // typing between taps breaks a double tap
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        static void SendMaskedWinUp(ushort winVk)
        {
            var inputs = new[]
            {
                Key(VK_MASK, 0),
                Key(VK_MASK, KEYEVENTF_KEYUP),
                Key(winVk, KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        static INPUT Key(ushort vk, uint flags) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = OurMarker } }
        };

        public void Dispose()
        {
            if (hookId != IntPtr.Zero) { UnhookWindowsHookEx(hookId); hookId = IntPtr.Zero; }
        }

        // ---- Win32 ----
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const ushort VK_MASK = 0xE8; // unassigned virtual key, used purely as a mask
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        static readonly IntPtr OurMarker = Native.InputMarker;

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    }
}
