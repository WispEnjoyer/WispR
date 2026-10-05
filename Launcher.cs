using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WispR
{
    static class Launcher
    {
        public static void LaunchApp(AppEntry e, bool admin)
        {
            if (admin)
            {
                string target = e.LnkPath ?? ResolveDesktopPath(e.ParsingName);
                if (target != null) { Start(target, null, "runas"); return; }
            }
            if (e.ParsingName != null) Start(@"shell:AppsFolder\" + e.ParsingName, null, null);
            else Start(e.LnkPath, null, null);
        }

        public static bool CanElevate(AppEntry e) => e.LnkPath != null || ResolveDesktopPath(e.ParsingName) != null;

        public static void RunCommand(string text, bool admin)
        {
            text = Environment.ExpandEnvironmentVariables(text.Trim());
            string file = text, args = null;
            bool whole = Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme != "file" && text.Contains("://")
                         || File.Exists(text) || Directory.Exists(text);
            if (!whole)
            {
                var m = Regex.Match(text, "^\"([^\"]+)\"\\s*(.*)$");
                if (m.Success) { file = m.Groups[1].Value; args = m.Groups[2].Value; }
                else
                {
                    int sp = text.IndexOf(' ');
                    if (sp > 0) { file = text.Substring(0, sp); args = text.Substring(sp + 1); }
                }
            }
            Start(file, args, admin ? "runas" : null);
        }

        public static void Start(string file, string args, string verb)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            if (verb != null) psi.Verb = verb;
            Process.Start(psi)?.Dispose();
        }

        /// <summary>Opens Explorer with the file selected (or the folder opened).</summary>
        public static void ShowInExplorer(string path)
        {
            if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"")?.Dispose();
            else Process.Start("explorer.exe", "\"" + path + "\"")?.Dispose();
        }

        /// <summary>Opens Explorer where the user can right-click → Pin to taskbar.</summary>
        public static void RevealForPinning(AppEntry e)
        {
            if (e.LnkPath != null && File.Exists(e.LnkPath)) ShowInExplorer(e.LnkPath);
            else Process.Start("explorer.exe", "shell:AppsFolder")?.Dispose();
        }

        /// <summary>Where the app actually lives: its .exe for desktop apps, the package folder for Store apps.</summary>
        public static string GetInstallLocation(AppEntry e)
        {
            string p = ResolveDesktopPath(e.ParsingName);
            if (p != null) return p;

            if (e.LnkPath != null && e.LnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string t = ResolveShortcut(e.LnkPath);
                if (!string.IsNullOrEmpty(t) && (File.Exists(t) || Directory.Exists(t))) return t;
            }

            if (e.IsStoreApp) return StoreAppFolder(e.ParsingName);
            return null;
        }

        static string ResolveShortcut(string lnk)
        {
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                string target = sc.TargetPath;
                Marshal.FinalReleaseComObject(sc);
                return Environment.ExpandEnvironmentVariables(target ?? "");
            }
            catch { return null; }
            finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }

        // AUMID "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" -> package root from the AppModel repository.
        static string StoreAppFolder(string aumid)
        {
            try
            {
                string family = aumid.Split('!')[0];
                int us = family.LastIndexOf('_');
                if (us <= 0) return null;
                string name = family.Substring(0, us), publisher = family.Substring(us + 1);
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                if (key == null) return null;
                var full = key.GetSubKeyNames()
                    .Where(k => k.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) &&
                                k.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(k => k, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (full == null) return null;
                using var pkg = key.OpenSubKey(full);
                var root = pkg?.GetValue("PackageRootFolder") as string;
                return string.IsNullOrEmpty(root) ? null : root;
            }
            catch { return null; }
        }

        // AppsFolder ids for desktop apps look like "{6D809377-...}\Notepad++\notepad++.exe" (a known-folder GUID + path).
        static string ResolveDesktopPath(string parsingName)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            if (File.Exists(parsingName)) return parsingName;
            var m = Regex.Match(parsingName, @"^\{([0-9A-Fa-f\-]{36})\}\\(.+)$");
            if (!m.Success) return null;
            try
            {
                if (SHGetKnownFolderPath(new Guid(m.Groups[1].Value), 0, IntPtr.Zero, out IntPtr p) != 0) return null;
                string root = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                string full = Path.Combine(root, m.Groups[2].Value);
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    }
}
