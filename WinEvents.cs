using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WispR
{
    /// <summary>
    /// Tells WispR the moment a window comes to the front, is maximized / restored / minimized,
    /// resized, shown or closed (Windows "WinEvents"), so the bars can react right away instead of
    /// waiting for the next half-second check. Events arrive on the UI thread.
    /// </summary>
    sealed class WinEvents : IDisposable
    {
        public event Action<IntPtr> Changed;
        readonly WinEventProc proc;
        readonly List<IntPtr> hooks = new List<IntPtr>();

        public WinEvents()
        {
            proc = OnEvent;
            Hook(0x0003, 0x0003); // EVENT_SYSTEM_FOREGROUND
            Hook(0x0016, 0x0017); // EVENT_SYSTEM_MINIMIZESTART..MINIMIZEEND
            Hook(0x8001, 0x8003); // EVENT_OBJECT_DESTROY, SHOW, HIDE
            Hook(0x800B, 0x800B); // EVENT_OBJECT_LOCATIONCHANGE (maximize, restore, resize, fullscreen)
        }

        void Hook(uint min, uint max)
        {
            var h = SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0, 0x0000 /* OUTOFCONTEXT */ | 0x0002 /* SKIPOWNPROCESS */);
            if (h != IntPtr.Zero) hooks.Add(h);
        }

        void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */ || idChild != 0) return; // not a window itself (cursor, caret, …)
            if (ev != 0x0003 && (Native.GetWindowLong(hwnd, -16) & 0x40000000 /* WS_CHILD */) != 0) return; // controls inside apps don't matter
            try { Changed?.Invoke(hwnd); } catch (Exception ex) { Log.Error("WinEvents", ex); }
        }

        public void Dispose()
        {
            foreach (var h in hooks) UnhookWinEvent(h);
            hooks.Clear();
        }

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    }
}
