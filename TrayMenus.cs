using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Shows tray icons' right-click menus in WispR's own style (growing out of the bar).
    ///
    /// Apps draw their own tray menus, so this works in two steps:
    ///  1. When you right-click an icon, the app opens its standard Windows menu. WispR reads its
    ///     items (text, enabled, checked, one level of submenus), closes it at once, and shows them as
    ///     its own menu instead.
    ///  2. When you pick an item, WispR asks the app for its menu again, invisibly, and selects that
    ///     item in it — so the app gets the click exactly as if you'd used its own menu.
    /// Only standard Windows menus are restyled; apps that draw their own menus keep them.
    /// </summary>
    sealed class TrayMenus : IDisposable
    {
        sealed class Item
        {
            public string Text;
            public bool Separator, Enabled, Checked;
            public int Pos;
            public List<Item> Sub;
        }

        sealed class Pending
        {
            public TrayIcon Icon;
            public uint Pid;
            public DateTime At;
            public Point Screen;
            public int[] Path;   // null: read the menu (step 1); otherwise: select this item (step 2)
            public int Level;    // which part of the path we're at (submenus)
        }

        readonly TrayService tray;
        readonly Func<BarMenu> newMenu;
        readonly Action<BarMenu, Point> showMenu;
        readonly WinEventProc proc;
        readonly IntPtr hook;
        Pending pending;

        public TrayMenus(TrayService tray, Func<BarMenu> newMenu, Action<BarMenu, Point> showMenu)
        {
            this.tray = tray; this.newMenu = newMenu; this.showMenu = showMenu;
            proc = OnEvent;
            hook = SetWinEventHook(0x0006, 0x0006 /* EVENT_SYSTEM_MENUPOPUPSTART */, IntPtr.Zero, proc, 0, 0, 0x0002 /* SKIPOWNPROCESS */);
        }

        /// <summary>Call right before passing a right-click on a tray icon to its app.</summary>
        public void Expect(TrayIcon icon, Point screen)
        {
            pending = new Pending { Icon = icon, Pid = Native.GetPid(icon.Hwnd), At = DateTime.Now, Screen = screen };
        }

        void OnEvent(IntPtr h, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            var p = pending;
            if (p == null || hwnd == IntPtr.Zero) return;
            if ((DateTime.Now - p.At).TotalSeconds > 3) { pending = null; return; }
            if (Native.GetPid(hwnd) != p.Pid) return;
            if (Native.GetClass(hwnd) != "#32768") return; // only standard Windows menus
            try
            {
                if (p.Path == null) Capture(hwnd, p); else Replay(hwnd, p);
            }
            catch (Exception ex) { Log.Error("TrayMenus", ex); pending = null; }
        }

        // ---------- step 1: read the app's menu and show ours ----------

        void Capture(IntPtr hwnd, Pending p)
        {
            IntPtr hmenu = Send(hwnd, MN_GETHMENU, IntPtr.Zero);
            var items = hmenu == IntPtr.Zero ? null : Read(hmenu, 0);
            pending = null;
            if (items == null || items.Count == 0)
            {
                Log.Throttled("traymenu-native", "Tray menu of an app isn't a standard menu — showing its own.");
                return; // leave the app's own menu as it is
            }
            OffScreen(hwnd);
            Cancel(hwnd);
            var pid = p.Pid; var icon = p.Icon; var screen = p.Screen;

            var m = newMenu();
            void Add(Item it, int[] path, string indent)
            {
                if (it.Separator) { m.AddSeparator(); return; }
                if (it.Sub != null)
                {
                    m.AddItem(indent + it.Text, null, enabled: false, bold: true);
                    foreach (var s in it.Sub) Add(s, new[] { path[0], s.Pos }, indent + "    ");
                    m.AddSeparator();
                    return;
                }
                string label = indent + (it.Checked ? "✓  " : "") + it.Text;
                m.AddItem(label, it.Enabled ? () => Choose(icon, pid, screen, path) : (Action)null, it.Enabled);
            }
            foreach (var it in items) Add(it, new[] { it.Pos }, "");
            showMenu(m, screen);
        }

        /// <summary>Items of a menu, or null if it can't be shown faithfully (custom-drawn, built on demand…).</summary>
        static List<Item> Read(IntPtr hmenu, int depth)
        {
            int n = GetMenuItemCount(hmenu);
            if (n <= 0 || n > 80) return null;
            var list = new List<Item>();
            var sb = new StringBuilder(512);
            for (int i = 0; i < n; i++)
            {
                uint state = GetMenuState(hmenu, (uint)i, MF_BYPOSITION);
                if (state == 0xFFFFFFFF) return null;
                if ((state & MF_SEPARATOR) != 0) { list.Add(new Item { Separator = true, Pos = i }); continue; }
                if ((state & MF_OWNERDRAW) != 0) return null; // drawn by the app: we can't show it
                sb.Clear();
                GetMenuString(hmenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
                string text = Clean(sb.ToString());
                if (text.Length == 0) return null;
                var it = new Item
                {
                    Text = text, Pos = i,
                    Enabled = (state & (MF_GRAYED | MF_DISABLED)) == 0,
                    Checked = (state & MF_CHECKED) != 0,
                };
                if ((state & MF_POPUP) != 0)
                {
                    if (depth > 0) return null; // one level of submenus is enough for tray menus
                    it.Sub = Read(GetSubMenu(hmenu, i), depth + 1);
                    if (it.Sub == null || it.Sub.Count == 0) return null; // filled in only when opened: keep the app's menu
                }
                list.Add(it);
            }
            // drop leading/trailing/double separators
            while (list.Count > 0 && list[0].Separator) list.RemoveAt(0);
            while (list.Count > 0 && list[list.Count - 1].Separator) list.RemoveAt(list.Count - 1);
            return list;
        }

        /// <summary>"&amp;Open\tCtrl+O" → "Open".</summary>
        static string Clean(string s)
        {
            int tab = s.IndexOf('\t');
            if (tab >= 0) s = s.Substring(0, tab);
            return s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
        }

        // ---------- step 2: pick the item in the app's (invisible) menu ----------

        void Choose(TrayIcon icon, uint pid, Point screen, int[] path)
        {
            if (!Native.IsWindow(icon.Hwnd)) return;
            pending = new Pending { Icon = icon, Pid = pid, At = DateTime.Now, Screen = screen, Path = path };
            tray.Click(icon, MouseButtons.Right, false, screen); // the app opens its menu again; OnEvent picks the item
        }

        void Replay(IntPtr hwnd, Pending p)
        {
            OffScreen(hwnd);
            int pos = p.Path[p.Level];
            Send(hwnd, MN_SELECTITEM, (IntPtr)pos);
            if (p.Level < p.Path.Length - 1)
            {
                p.Level++;
                p.At = DateTime.Now;
                Native.PostMessage(hwnd, 0x100 /* WM_KEYDOWN */, (IntPtr)0x27 /* VK_RIGHT: open the submenu */, IntPtr.Zero);
                return; // the submenu opens as another menu window → OnEvent again
            }
            pending = null;
            Native.PostMessage(hwnd, 0x100 /* WM_KEYDOWN */, (IntPtr)0x0D /* VK_RETURN */, IntPtr.Zero);
        }

        /// <summary>A message to the app's menu, never waiting long (the app could be busy).</summary>
        static IntPtr Send(IntPtr hwnd, uint msg, IntPtr w)
        {
            Native.SendMessageTimeout(hwnd, msg, w, IntPtr.Zero, 0x2 /* ABORTIFHUNG */, 300, out IntPtr r);
            return r;
        }

        static void OffScreen(IntPtr hwnd) =>
            Native.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, 0x1 | 0x4 | 0x10 /* NOSIZE | NOZORDER | NOACTIVATE */);

        static void Cancel(IntPtr hwnd)
        {
            Native.PostMessage(hwnd, 0x100 /* WM_KEYDOWN */, (IntPtr)0x1B /* VK_ESCAPE */, IntPtr.Zero);
            Native.PostMessage(hwnd, 0x101 /* WM_KEYUP */, (IntPtr)0x1B, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        }

        const uint MN_GETHMENU = 0x01E1, MN_SELECTITEM = 0x01E5;
        const uint MF_BYPOSITION = 0x400, MF_GRAYED = 0x1, MF_DISABLED = 0x2, MF_CHECKED = 0x8, MF_POPUP = 0x10,
                   MF_OWNERDRAW = 0x100, MF_SEPARATOR = 0x800;

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hmenu);
        [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr hmenu, uint item, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr hmenu, uint item, StringBuilder text, int max, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr hmenu, int pos);
    }
}
