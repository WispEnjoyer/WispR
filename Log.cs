using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WispR
{
    /// <summary>
    /// A small diagnostic log at %APPDATA%\WispR\log.txt (kept under ~1 MB). Records errors and
    /// what the watchdog had to repair, so problems can be understood without guessing.
    /// </summary>
    static class Log
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, DateTime> lastSeen = new Dictionary<string, DateTime>();

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WispR", "log.txt");

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Redact(message) + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* logging must never cause trouble */ }
        }

        // Privacy: paths under your user folder contain your account name — the log never shows it.
        static readonly string home = SafeHome();
        static string SafeHome()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'); } catch { return ""; }
        }

        /// <summary>Replaces your user folder (which contains your account name) with %USERPROFILE%.</summary>
        public static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s) || home.Length < 4) return s;
            int i;
            while ((i = s.IndexOf(home, StringComparison.OrdinalIgnoreCase)) >= 0)
                s = s.Substring(0, i) + "%USERPROFILE%" + s.Substring(i + home.Length);
            string slash = home.Replace('\\', '/');
            while ((i = s.IndexOf(slash, StringComparison.OrdinalIgnoreCase)) >= 0)
                s = s.Substring(0, i) + "%USERPROFILE%" + s.Substring(i + slash.Length);
            return s;
        }

        /// <summary>Like <see cref="Write"/>, but the same <paramref name="key"/> is logged at most once a minute.</summary>
        public static void Throttled(string key, string message)
        {
            lock (gate)
            {
                if (lastSeen.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 60) return;
                lastSeen[key] = DateTime.Now;
            }
            Write(message);
        }

        public static void Error(string where, Exception ex) =>
            Throttled("err:" + where + ":" + ex.GetType().Name, "ERROR in " + where + ": " + ex.GetType().Name + ": " + ex.Message +
                Environment.NewLine + "    " + (ex.StackTrace ?? "").Replace(Environment.NewLine, Environment.NewLine + "    "));

        /// <summary>GDI / USER handle counts — a leak of these makes windows draw blank.</summary>
        public static string Resources()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return "GDI objects " + GetGuiResources(p.Handle, 0) + ", USER objects " + GetGuiResources(p.Handle, 1) +
                       ", memory " + (p.PrivateMemorySize64 / (1024 * 1024)) + " MB";
            }
            catch { return "resources unknown"; }
        }

        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
