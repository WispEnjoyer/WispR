using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WispR
{
    sealed class AppWindow
    {
        public IntPtr Handle;
        public string Title;
        public uint Pid;
        public string ExePath;   // lower-case full path
        public string Aumid;     // AppUserModelID if the window has one
        public bool Flashing;
        public string GroupKey => Aumid != null ? "aumid:" + Aumid.ToLowerInvariant() : "exe:" + (ExePath ?? Pid.ToString());
    }

    /// <summary>Keeps the list of windows that belong on a taskbar (same rules as Alt+Tab).</summary>
    sealed class WindowTracker : IDisposable
    {
        readonly Dictionary<IntPtr, AppWindow> known = new Dictionary<IntPtr, AppWindow>();
        readonly Dictionary<uint, string> exeByPid = new Dictionary<uint, string>();
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        readonly ShellHook hook;
        string lastSignature = "";
        string lastTitles;
        /// <summary>Only window titles changed (nothing else).</summary>
        public event Action TitlesChanged;
        public DateTime LastRefresh { get; private set; }

        public List<AppWindow> Windows { get; private set; } = new List<AppWindow>();
        public IntPtr Foreground { get; private set; }
        public event Action Changed;

        public WindowTracker()
        {
            hook = new ShellHook(this);
        }

        public void Refresh()
        {
            var order = new List<AppWindow>();
            var seen = new HashSet<IntPtr>();
            Native.EnumWindows((h, _) =>
            {
                if (IsTaskWindow(h))
                {
                    seen.Add(h);
                    if (!known.TryGetValue(h, out var w))
                    {
                        uint pid = Native.GetPid(h);
                        if (!exeByPid.TryGetValue(pid, out var exe))
                            exeByPid[pid] = exe = Native.GetProcessPath(pid)?.ToLowerInvariant();
                        w = new AppWindow { Handle = h, Pid = pid, ExePath = exe, Aumid = Native.GetAppUserModelId(h) };
                        known[h] = w;
                    }
                    w.Title = Native.GetText(h);
                }
                return true;
            }, IntPtr.Zero);

            foreach (var gone in known.Keys.Where(h => !seen.Contains(h)).ToList()) known.Remove(gone);
            if (exeByPid.Count > 500) exeByPid.Clear();

            // Keep a stable order: windows stay where they were, new ones go to the end.
            foreach (var w in Windows) if (seen.Contains(w.Handle)) order.Add(known[w.Handle]);
            var placed = new HashSet<IntPtr>(order.Select(w => w.Handle));
            foreach (var h in seen) if (placed.Add(h)) order.Add(known[h]);

            Windows = order;
            Foreground = Native.GetForegroundWindow();
            foreach (var w in Windows) if (w.Handle == Foreground) w.Flashing = false;

            LastRefresh = DateTime.Now;
            // Titles change constantly (players, downloads, tab counters) but rarely change the taskbar,
            // so they're reported separately from changes to which windows exist / are active / flash.
            string structure = Foreground + ";" + string.Join(";", Windows.Select(w => w.Handle + "|" + w.Flashing));
            string titles = string.Join("\n", Windows.Select(w => w.Title));
            if (structure != lastSignature)
            {
                lastSignature = structure;
                lastTitles = titles;
                Changed?.Invoke();
            }
            else if (titles != lastTitles)
            {
                lastTitles = titles;
                TitlesChanged?.Invoke();
            }
        }

        bool IsTaskWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLong(h, -20 /* GWL_EXSTYLE */);
            bool appWindow = (ex & 0x40000 /* WS_EX_APPWINDOW */) != 0;
            if (!appWindow)
            {
                if ((ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return false;
                if ((ex & 0x08000000 /* WS_EX_NOACTIVATE */) != 0) return false;
                if (Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
            }
            if (Native.IsCloaked(h)) return false; // other virtual desktops, suspended Store apps
            if (Native.GetWindowTextLength(h) == 0) return false;
            if (Native.GetPid(h) == ownPid) return false;
            switch (Native.GetClass(h))
            {
                case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":
                case "Windows.UI.Core.CoreWindow":
                    return false;
            }
            return true;
        }

        /// <summary>Best available icon for a window's app.</summary>
        public static Bitmap LoadIcon(AppWindow w, int size)
        {
            Bitmap bmp = null;
            if (w.Aumid != null) bmp = IconLoader.Load(@"shell:AppsFolder\" + w.Aumid, size);
            if (bmp == null && w.ExePath != null && !w.ExePath.EndsWith("applicationframehost.exe"))
                bmp = IconLoader.Load(w.ExePath, size);
            return bmp ?? WindowIcon(w.Handle);
        }

        static Bitmap WindowIcon(IntPtr h)
        {
            foreach (int type in new[] { 1, 2, 0 }) // ICON_BIG, ICON_SMALL2, ICON_SMALL
            {
                Native.SendMessageTimeout(h, 0x7F /* WM_GETICON */, (IntPtr)type, IntPtr.Zero, 2 /* SMTO_ABORTIFHUNG */, 50, out IntPtr icon);
                if (icon != IntPtr.Zero) return ToBitmap(icon);
            }
            foreach (int idx in new[] { -14, -34 }) // GCLP_HICON, GCLP_HICONSM
            {
                var icon = Native.GetClassLongPtr(h, idx);
                if (icon != IntPtr.Zero) return ToBitmap(icon);
            }
            return null;
        }

        static Bitmap ToBitmap(IntPtr hicon)
        {
            try { using var ic = Icon.FromHandle(hicon); return ic.ToBitmap(); }
            catch { return null; }
        }

        public void Dispose() => hook.Dispose();

        /// <summary>Instant notifications (window created/destroyed/activated/flashing).</summary>
        sealed class ShellHook : NativeWindow, IDisposable
        {
            readonly WindowTracker owner;
            readonly uint shellMsg;

            public ShellHook(WindowTracker owner)
            {
                this.owner = owner;
                CreateHandle(new CreateParams());
                shellMsg = Native.RegisterWindowMessage("SHELLHOOK");
                Native.RegisterShellHookWindow(Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == shellMsg)
                {
                    int code = m.WParam.ToInt32();
                    if (code == 0x8006 /* HSHELL_FLASH */ && owner.known.TryGetValue(m.LParam, out var w)) w.Flashing = true;
                    owner.Refresh();
                    return;
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    Native.DeregisterShellHookWindow(Handle);
                    DestroyHandle();
                }
            }
        }
    }
}
