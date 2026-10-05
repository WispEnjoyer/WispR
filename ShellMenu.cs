using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WispR
{
    /// <summary>
    /// Wraps the real Windows Explorer context menu for a shell item (an app in
    /// shell:AppsFolder or a shortcut file). Used for "Pin to taskbar" — which Windows
    /// only exposes through its own menu — and for "More Windows options…".
    /// </summary>
    sealed class ShellMenu : IDisposable
    {
        const uint First = 1, Last = 0x7FFF;
        IContextMenu cm;
        IntPtr hmenu;

        ShellMenu() { }

        public static ShellMenu TryCreate(string parsingName, bool extended = false)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            IShellItem item = null;
            try
            {
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItem).GUID, out item);
                item.BindToHandler(IntPtr.Zero, BHID_SFUIObject, typeof(IContextMenu).GUID, out IntPtr p);
                var m = new ShellMenu { cm = (IContextMenu)Marshal.GetObjectForIUnknown(p) };
                Marshal.Release(p);
                m.hmenu = CreatePopupMenu();
                m.cm.QueryContextMenu(m.hmenu, 0, First, Last, CMF_NORMAL | (extended ? CMF_EXTENDEDVERBS : 0));
                return m;
            }
            catch { return null; }
            finally { if (item != null) Marshal.ReleaseComObject(item); }
        }

        /// <summary>Finds a top-level command by canonical verb or by its (localized) menu text.</summary>
        public int Find(string[] verbs, string[] texts)
        {
            int count = GetMenuItemCount(hmenu);
            for (int i = 0; i < count; i++)
            {
                uint id = GetMenuItemID(hmenu, i);
                if (id == uint.MaxValue || id < First || id > Last) continue;

                string verb = GetVerb(id - First);
                if (verb != null)
                    foreach (var v in verbs)
                        if (string.Equals(verb, v, StringComparison.OrdinalIgnoreCase)) return (int)id;

                var sb = new StringBuilder(256);
                GetMenuString(hmenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
                string text = sb.ToString().Replace("&", "").Trim().ToLowerInvariant();
                foreach (var t in texts)
                    if (text == t) return (int)id;
            }
            return 0;
        }

        string GetVerb(uint offset)
        {
            IntPtr buf = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.WriteInt16(buf, 0);
                if (cm.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buf, 256) != 0) return null;
                return Marshal.PtrToStringUni(buf);
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Shows the native menu; returns the chosen command id or 0.</summary>
        public int Track(IntPtr owner, Point screen) =>
            (int)TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, owner, IntPtr.Zero);

        public void Invoke(int id, IntPtr owner, Point screen)
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = owner,
                lpVerb = (IntPtr)(id - First),
                lpVerbW = (IntPtr)(id - First),
                nShow = 1, // SW_SHOWNORMAL
                ptInvoke = new POINT { x = screen.X, y = screen.Y },
            };
            IntPtr p = Marshal.AllocHGlobal(info.cbSize);
            try
            {
                Marshal.StructureToPtr(info, p, false);
                int hr = cm.InvokeCommand(p);
                if (hr < 0 && hr != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(hr); // ignore "cancelled"
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Forwards owner-draw messages so submenus like "Send to" / "Open with" populate.</summary>
        public bool HandleMenuMessage(ref Message m)
        {
            int msg = m.Msg;
            bool menuMsg = msg == WM_INITMENUPOPUP || msg == WM_MENUCHAR ||
                           ((msg == WM_DRAWITEM || msg == WM_MEASUREITEM) && m.WParam == IntPtr.Zero);
            if (!menuMsg) return false;
            try
            {
                if (cm is IContextMenu3 cm3)
                {
                    cm3.HandleMenuMsg2((uint)msg, m.WParam, m.LParam, out IntPtr result);
                    m.Result = result;
                    return true;
                }
                if (cm is IContextMenu2 cm2)
                {
                    cm2.HandleMenuMsg((uint)msg, m.WParam, m.LParam);
                    m.Result = IntPtr.Zero;
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void Dispose()
        {
            if (hmenu != IntPtr.Zero) { DestroyMenu(hmenu); hmenu = IntPtr.Zero; }
            if (cm != null) { Marshal.ReleaseComObject(cm); cm = null; }
        }

        // ---------- interop ----------

        const uint CMF_NORMAL = 0, CMF_EXTENDEDVERBS = 0x100;
        const uint GCS_VERBW = 4, MF_BYPOSITION = 0x400;
        const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
        const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
        const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
        static readonly Guid BHID_SFUIObject = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize; public uint fMask; public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
            public int nShow; public uint dwHotKey; public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdn, out IntPtr name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem psi, uint hint, out int order);
        }

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, int cchMax);
        }

        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, int cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, int cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr hMenu, int pos);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr hMenu, uint id, StringBuilder s, int max, uint flags);
        [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    }

    /// <summary>Taskbar pinning via Windows' own "Pin to taskbar" command.</summary>
    static class TaskbarPin
    {
        public enum State { Unavailable, CanPin, CanUnpin }

        static readonly string[] PinVerbs = { "taskbarpin", "pintotaskbar" };
        static readonly string[] UnpinVerbs = { "taskbarunpin", "unpinfromtaskbar" };
        static readonly string[] PinTexts = { "pin to taskbar", "an taskleiste anheften" };
        static readonly string[] UnpinTexts = { "unpin from taskbar", "von taskleiste lösen" };

        public static State GetState(AppEntry e)
        {
            foreach (var source in Sources(e))
            {
                using var m = ShellMenu.TryCreate(source);
                if (m == null) continue;
                if (m.Find(UnpinVerbs, UnpinTexts) != 0) return State.CanUnpin;
                if (m.Find(PinVerbs, PinTexts) != 0) return State.CanPin;
            }
            return State.Unavailable;
        }

        /// <returns>false if Windows didn't offer the command.</returns>
        public static bool Toggle(AppEntry e, IntPtr owner)
        {
            foreach (var source in Sources(e))
            {
                using var m = ShellMenu.TryCreate(source);
                if (m == null) continue;
                int id = m.Find(UnpinVerbs, UnpinTexts);
                if (id == 0) id = m.Find(PinVerbs, PinTexts);
                if (id == 0) continue;
                m.Invoke(id, owner, Cursor.Position);
                return true;
            }
            return false;
        }

        static string[] Sources(AppEntry e) =>
            new[] { e.ParsingName != null ? @"shell:AppsFolder\" + e.ParsingName : null, e.LnkPath };
    }
}
