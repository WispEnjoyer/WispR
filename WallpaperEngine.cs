using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WispR
{
    /// <summary>
    /// Works together with Wallpaper Engine: finds its install and wallpapers (Steam Workshop and your own
    /// projects), reads which wallpaper is playing, and switches wallpapers through its official command
    /// line ("-control openWallpaper"), so animations and effects keep working.
    /// </summary>
    static class WallpaperEngine
    {
        public sealed class Project
        {
            public string Json;     // path of project.json (what Wallpaper Engine opens)
            public string Title;
            public string Preview;  // preview picture (jpg/png/gif)
            public string Kind;     // Video / Scene / Web / App
        }

        static string dir;
        static DateTime dirCheckedAt = DateTime.MinValue;
        static bool running;
        static DateTime runningCheckedAt = DateTime.MinValue;
        static string runningExe;
        static uint runningPid;
        static readonly object gate = new object(); // the launcher switches wallpapers on a worker thread

        static bool IsEngineExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string f = Path.GetFileName(path);
            return f.Equals("wallpaper64.exe", StringComparison.OrdinalIgnoreCase) || f.Equals("wallpaper32.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Wallpaper Engine's install folder, or null if it isn't installed.</summary>
        public static string Folder
        {
            get
            {
                lock (gate)
                {
                    if ((DateTime.Now - dirCheckedAt).TotalSeconds < 60) return dir;
                    dirCheckedAt = DateTime.Now;
                    dir = Detect();
                    return dir;
                }
            }
        }

        /// <summary>Is Wallpaper Engine running right now? (checked at most every 5 seconds)</summary>
        public static bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    // not running: listing processes is the costly part, so only look again every 30 s
                    if ((DateTime.Now - runningCheckedAt).TotalSeconds < (running ? 5 : 30)) return running;
                    runningCheckedAt = DateTime.Now;
                    // the process we found last time still there? (cheap — no process list needed)
                    if (runningPid != 0 && IsEngineExe(Native.GetProcessPath(runningPid))) return running = true;
                    running = false; runningPid = 0;
                    foreach (var name in new[] { "wallpaper64", "wallpaper32" })
                    {
                        Process[] ps;
                        try { ps = Process.GetProcessesByName(name); } catch { continue; }
                        foreach (var p in ps)
                        {
                            try
                            {
                                var path = Native.GetProcessPath((uint)p.Id);
                                if (IsEngineExe(path)) { running = true; runningExe = path; runningPid = (uint)p.Id; }
                            }
                            catch { }
                            finally { p.Dispose(); }
                        }
                    }
                    return running;
                }
            }
        }

        static string Detect()
        {
            if (IsRunning && runningExe != null) return Path.GetDirectoryName(runningExe);
            foreach (var lib in SteamLibraries())
            {
                var d = Path.Combine(lib, @"steamapps\common\wallpaper_engine");
                if (File.Exists(Path.Combine(d, "wallpaper64.exe")) || File.Exists(Path.Combine(d, "wallpaper32.exe"))) return d;
            }
            return null;
        }

        /// <summary>All Steam library folders (from Steam's own libraryfolders.vdf).</summary>
        static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (k?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\'));
            }
            catch { }
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (k?.GetValue("InstallPath") is string ip) roots.Add(ip);
            }
            catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (seen.Add(root)) yield return root;
                string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                string text = null;
                try { if (File.Exists(vdf)) text = File.ReadAllText(vdf); } catch { }
                if (text == null) continue;
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (seen.Add(lib)) yield return lib;
                }
            }
        }

        /// <summary>The program to send commands to (the running one if possible).</summary>
        static string Exe
        {
            get
            {
                var d = Folder;
                // only send commands to a running copy that is Steam's install (not any program with that name)
                if (IsRunning && runningExe != null && File.Exists(runningExe) &&
                    (runningExe.IndexOf(@"\steamapps\common\wallpaper_engine\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (d != null && string.Equals(Path.GetDirectoryName(runningExe), d, StringComparison.OrdinalIgnoreCase))))
                    return runningExe;
                if (d == null) return null;
                var x64 = Path.Combine(d, "wallpaper64.exe");
                return File.Exists(x64) && Environment.Is64BitOperatingSystem ? x64 : Path.Combine(d, "wallpaper32.exe");
            }
        }

        // ---------- wallpapers ----------

        /// <summary>Every Wallpaper Engine wallpaper on this PC: Workshop subscriptions and your own/default projects.</summary>
        public static List<Project> Projects(int max = 400)
        {
            // re-read at most every 30 s (the carousel asks each time it opens)
            lock (gate)
                if (projectsCache != null && (DateTime.Now - projectsAt).TotalSeconds < 30) return new List<Project>(projectsCache);
            var fresh = ReadProjects(max);
            lock (gate) { projectsCache = fresh; projectsAt = DateTime.Now; }
            return new List<Project>(fresh);
        }
        static List<Project> projectsCache;
        static DateTime projectsAt;

        static List<Project> ReadProjects(int max)
        {
            var list = new List<Project>();
            var d = Folder;
            if (d == null) return list;
            var folders = new List<string>();
            foreach (var lib in SteamLibraries())
                folders.Add(Path.Combine(lib, @"steamapps\workshop\content\431960"));
            folders.Add(Path.Combine(d, @"projects\myprojects"));
            folders.Add(Path.Combine(d, @"projects\defaultprojects"));

            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!System.IO.Directory.Exists(folder)) continue;
                string[] subs;
                try { subs = System.IO.Directory.GetDirectories(folder); } catch { continue; }
                foreach (var sub in subs)
                {
                    if (list.Count >= max) return list;
                    var p = Read(Path.Combine(sub, "project.json"));
                    if (p != null) list.Add(p);
                }
            }
            return list;
        }

        static Project Read(string json)
        {
            try
            {
                var fi = new FileInfo(json);
                if (!fi.Exists || fi.Length > 1024 * 1024) return null; // Workshop files are untrusted: keep them small
                string text = File.ReadAllText(json);
                string folder = Path.GetDirectoryName(json);
                string title = Field(text, "title");
                string preview = Field(text, "preview");
                string type = Field(text, "type");

                string previewPath = null;
                previewPath = SafeChild(folder, preview); // never follow it outside the wallpaper's own folder
                if (previewPath == null || !File.Exists(previewPath))
                    previewPath = new[] { "preview.jpg", "preview.png", "preview.gif" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists);
                return new Project
                {
                    Json = json,
                    Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(folder) : title.Trim(),
                    Preview = previewPath,
                    Kind = KindName(type),
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// <paramref name="rel"/> inside <paramref name="folder"/>, or null. A Workshop author controls
        /// project.json; an absolute or network path there (\\server\x.jpg) would make Windows contact
        /// that server with your login, and "..\" could reach other files.
        /// </summary>
        static string SafeChild(string folder, string rel)
        {
            if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel) || rel.IndexOf(':') >= 0 || rel.StartsWith(@"\\") || rel.StartsWith("//")) return null;
            try
            {
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(Path.Combine(root, rel));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch { return null; }
        }

        static string KindName(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "video": return "Video";
                case "scene": return "Scene";
                case "web": return "Web";
                case "application": return "App";
                default: return "Wallpaper";
            }
        }

        /// <summary>The first "name": "value" string field in a JSON text (enough for project.json / config.json).</summary>
        static string Field(string json, string name, int start = 0)
        {
            var rx = fieldRx.GetOrAdd(name, n => new Regex("\"" + Regex.Escape(n) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
                RegexOptions.Compiled, TimeSpan.FromMilliseconds(250)));
            Match m;
            try { m = rx.Match(json, start); } catch (RegexMatchTimeoutException) { return null; }
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> fieldRx =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>();

        // ---------- favourites ----------

        /// <summary>
        /// The wallpapers you marked as favourite (the heart) in Wallpaper Engine, read from its config.json.
        /// Wallpaper Engine doesn't document that file, so this reads every value under a key with
        /// "favorite" in its name and keeps whatever names a wallpaper: a Workshop ID or a path.
        /// Returns null if no favourites list was found at all.
        /// </summary>
        // keys are short: bounded so a huge single-line file can't make the search crawl
        static readonly Regex FavKey = new Regex("\"([^\"\\r\\n]{0,64}fav[^\"\\r\\n]{0,64})\"\\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
        static HashSet<string> favCache;
        static string favStamp;

        public static HashSet<string> Favorites()
        {
            var d = Folder;
            if (d == null) return null;
            // only re-read when Wallpaper Engine's settings file changed
            string stamp;
            try { stamp = File.GetLastWriteTimeUtc(Path.Combine(d, "config.json")).Ticks + "|" + d; } catch { stamp = null; }
            lock (gate) if (stamp != null && stamp == favStamp) return favCache;
            var result = ReadFavorites(d);
            lock (gate) { favCache = result; favStamp = stamp; }
            return result;
        }

        static HashSet<string> ReadFavorites(string d)
        {
            // config.json first, then any other settings file next to it
            var files = new List<string> { Path.Combine(d, "config.json") };
            try { files.AddRange(System.IO.Directory.GetFiles(d, "*.json").Where(f => !f.EndsWith("config.json", StringComparison.OrdinalIgnoreCase))); } catch { }
            foreach (var sub in new[] { "config", "settings", "data", "bin" })
                try { if (System.IO.Directory.Exists(Path.Combine(d, sub))) files.AddRange(System.IO.Directory.GetFiles(Path.Combine(d, sub), "*.json")); } catch { }

            HashSet<string> keys = null;
            var found = new List<string>();
            foreach (var file in files)
            {
            string text;
            try { if (new FileInfo(file).Length > 20 * 1024 * 1024) continue; text = File.ReadAllText(file); } catch { continue; }
            MatchCollection matches;
            try { matches = FavKey.Matches(text); _ = matches.Count; } catch (RegexMatchTimeoutException) { continue; }
            foreach (Match m in matches)
            {
                string value = ValueAt(text, m.Index + m.Length);
                if (value == null) continue;
                found.Add(Path.GetFileName(file) + ": \"" + m.Groups[1].Value + "\" = " + (value.Length > 400 ? value.Substring(0, 400) + "…" : value).Replace("\r", "").Replace("\n", " "));
                if (!value.StartsWith("[") && !value.StartsWith("{") && !value.StartsWith("\"")) continue; // true/false/numbers: a setting, not a list
                keys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // every string and bare number inside it
                foreach (Match v in Regex.Matches(value, "\"((?:\\\\.|[^\"\\\\])*)\"|\\b(\\d{6,})\\b"))
                {
                    string s = v.Groups[1].Success ? v.Groups[1].Value.Replace(@"\\", @"\").Replace(@"\/", "/") : v.Groups[2].Value;
                    if (s.Length == 0) continue;
                    keys.Add(s.Replace('/', '\\').TrimEnd('\\'));
                    // a path → also its folder name (= Workshop ID or project folder)
                    var parts = s.Replace('/', '\\').TrimEnd('\\').Split('\\');
                    for (int i = 0; i < parts.Length; i++)
                        if (Regex.IsMatch(parts[i], @"^\d{6,}$")) keys.Add(parts[i]);
                    if (parts.Length >= 2 && parts[parts.Length - 1].Contains(".")) keys.Add(parts[parts.Length - 2]);
                }
            }
            }
            WriteReport(d, files, found, keys);
            Log.Throttled("we-fav", found.Count == 0
                ? "Wallpaper Engine: no favourites entry found in " + string.Join(", ", files.Select(Path.GetFileName))
                : "Wallpaper Engine favourites — " + (keys?.Count ?? 0) + " names from: " + string.Join(" | ", found.Take(8)));
            return keys;
        }

        static DateTime reportAt = DateTime.MinValue;

        /// <summary>
        /// %APPDATA%\WispR\wallpaper-engine-favourites.txt: which settings files were searched and every
        /// "fav…" entry found, so it's easy to see where Wallpaper Engine keeps its hearts.
        /// </summary>
        static void WriteReport(string d, List<string> files, List<string> found, HashSet<string> keys)
        {
            if (reportAt != DateTime.MinValue) return; // a diagnostic: once per run is enough
            reportAt = DateTime.Now;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("WispR — Wallpaper Engine favourites report, " + DateTime.Now);
                sb.AppendLine("Wallpaper Engine folder: " + d);
                sb.AppendLine();
                sb.AppendLine("Files searched:");
                foreach (var f in files) { long len = 0; try { len = new FileInfo(f).Length; } catch { } sb.AppendLine("  " + f + "  (" + len + " bytes)"); }
                sb.AppendLine();
                sb.AppendLine("Entries with \"fav\" in their name (" + found.Count + "):");
                foreach (var f in found) sb.AppendLine("  " + f);
                sb.AppendLine();
                sb.AppendLine("Wallpaper names taken from them: " + (keys == null ? "none" : keys.Count + " — " + string.Join(", ", keys.Take(40))));
                sb.AppendLine();
                sb.AppendLine("Where each installed wallpaper's folder name (Workshop number) appears in those files:");
                var texts = new Dictionary<string, string>();
                foreach (var f in files) { try { if (new FileInfo(f).Length < 20 * 1024 * 1024) texts[f] = File.ReadAllText(f); } catch { } }
                foreach (var p in Projects())
                {
                    string id = Path.GetFileName(Path.GetDirectoryName(p.Json));
                    sb.AppendLine("  " + p.Title + "  [" + id + "]");
                    int shown = 0;
                    foreach (var kv in texts)
                    {
                        int at = 0;
                        while (shown < 6 && (at = kv.Value.IndexOf(id, at, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            int from = Math.Max(0, at - 160), to = Math.Min(kv.Value.Length, at + id.Length + 60);
                            sb.AppendLine("      " + Path.GetFileName(kv.Key) + " @" + at + ": …" + kv.Value.Substring(from, to - from).Replace("\r", "").Replace("\n", " ") + "…");
                            at += id.Length;
                            shown++;
                        }
                    }
                    if (shown == 0) sb.AppendLine("      (not mentioned)");
                }
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WispR");
                System.IO.Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "wallpaper-engine-favourites.txt"), sb.ToString());
            }
            catch { }
        }

        /// <summary>Is this wallpaper in the favourites list?</summary>
        public static bool IsFavorite(Project p, HashSet<string> favs)
        {
            if (favs == null || p == null) return false;
            string folder = Path.GetDirectoryName(p.Json);
            return favs.Contains(Path.GetFileName(folder)) || favs.Contains(folder) || favs.Contains(p.Json) ||
                   favs.Any(f => f.Length > 3 && f.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The raw JSON value starting at <paramref name="i"/> (string, number, array or object).</summary>
        static string ValueAt(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;
            int start = i, depth = 0;
            bool inStr = false;
            if (s[i] != '[' && s[i] != '{' && s[i] != '"')
            {
                while (i < s.Length && ",}]".IndexOf(s[i]) < 0) i++;
                return s.Substring(start, i - start);
            }
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') { inStr = false; if (depth == 0) return s.Substring(start, i - start + 1); } continue; }
                if (c == '"') inStr = true;
                else if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}') { if (--depth == 0) return s.Substring(start, i - start + 1); }
            }
            return null;
        }

        // ---------- current wallpaper ----------

        static string currentJson;
        static DateTime configStamp = DateTime.MinValue;

        /// <summary>project.json of the wallpaper Wallpaper Engine is showing (from its config.json), or null.</summary>
        public static string CurrentProject()
        {
            var d = Folder;
            if (d == null) return null;
            lock (gate) return CurrentProjectCore(d);
        }

        static string CurrentProjectCore(string d)
        {
            string config = Path.Combine(d, "config.json");
            try
            {
                var stamp = File.GetLastWriteTimeUtc(config);
                if (stamp == configStamp) return currentJson;
                configStamp = stamp;
                string text = File.ReadAllText(config);
                int at = text.IndexOf("\"selectedwallpapers\"", StringComparison.OrdinalIgnoreCase);
                string file = at >= 0 ? Field(text, "file", at) : null;
                currentJson = string.IsNullOrEmpty(file) ? null : Path.GetFullPath(file.Replace('/', '\\'));
                // a wallpaper opened as a plain file (e.g. a video): use its folder's project.json if it has one
                if (currentJson != null && !currentJson.EndsWith("project.json", StringComparison.OrdinalIgnoreCase))
                {
                    var pj = Path.Combine(Path.GetDirectoryName(currentJson), "project.json");
                    currentJson = File.Exists(pj) ? pj : null;
                }
            }
            catch { currentJson = null; }
            return currentJson;
        }

        /// <summary>The preview picture of the playing wallpaper — but only while Wallpaper Engine runs.</summary>
        public static string CurrentPreviewIfRunning()
        {
            if (!IsRunning) return null;
            var json = CurrentProject();
            if (json == null) return null;
            if (!string.Equals(json, previewFor, StringComparison.OrdinalIgnoreCase))
            {
                previewFor = json;
                previewPath = Read(json)?.Preview;
            }
            return previewPath != null && File.Exists(previewPath) ? previewPath : null;
        }
        static string previewFor, previewPath;

        static bool Control(string args)
        {
            var exe = Exe;
            if (exe == null || !File.Exists(exe)) return false;
            Process.Start(new ProcessStartInfo(exe, "-control " + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            })?.Dispose();
            return true;
        }

        /// <summary>Removes Wallpaper Engine's wallpaper from all screens so the Windows wallpaper shows.</summary>
        public static bool CloseWallpaper()
        {
            if (!IsRunning || !Control("closeWallpaper")) return false;
            string folder = Folder ?? "";
            lock (gate)
            {
                currentJson = null;
                try { configStamp = File.GetLastWriteTimeUtc(Path.Combine(folder, "config.json")); } catch { }
            }
            Log.Write("Wallpaper Engine: closed its wallpaper so the Windows wallpaper shows");
            return true;
        }

        /// <summary>Plays a wallpaper in Wallpaper Engine (starts it if needed).</summary>
        public static bool Open(string projectJson)
        {
            // only ever hand Wallpaper Engine a project.json path (and nothing that could break out of the quotes)
            if (string.IsNullOrEmpty(projectJson) || projectJson.IndexOf('"') >= 0 ||
                !projectJson.EndsWith("\\project.json", StringComparison.OrdinalIgnoreCase)) return false;
            var exe = Exe;
            if (!Control("openWallpaper -file \"" + projectJson + "\"")) return false;
            // Treat it as current straight away; Wallpaper Engine rewrites config.json a moment later and
            // the next read picks that up.
            lock (gate)
            {
                currentJson = Path.GetFullPath(projectJson);
                try { configStamp = File.GetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(exe), "config.json")); } catch { }
            }
            runningCheckedAt = DateTime.MinValue;
            Log.Write("Wallpaper Engine: opened " + projectJson);
            return true;
        }
    }
}
