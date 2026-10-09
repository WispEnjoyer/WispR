using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WispR
{
    /// <summary>
    /// LumenR, the anime/movie player add-on. Both apps work on their own; when both are there they
    /// talk through two small files:
    /// <list type="bullet">
    /// <item>%APPDATA%\LumenR\status.json (written by LumenR): how to start it, what's playing, and what's
    /// next in the shows you're watching. Its presence is how WispR knows LumenR is installed.</item>
    /// <item>%APPDATA%\WispR\theme.json (written by WispR, <see cref="ThemeExport"/>): WispR's colours, worked
    /// out from the wallpaper, so LumenR looks the same.</item>
    /// </list>
    /// WispR starts LumenR with --play / --open &lt;show&gt; or --show; a running LumenR takes the command over.
    /// </summary>
    static class LumenR
    {
        public sealed class Show
        {
            public string Key = "", Title = "", Category = "", Label = "", Poster = "";
            public int Watched, Total;
            public float Progress;      // resume point in the next episode (0–1)
        }

        public sealed class Now
        {
            public string Key = "", Title = "", Episode = "", EpisodeTitle = "", Poster = "";
            public double Pos, Dur;
        }

        public sealed class State
        {
            public string[] Launch = new string[0];
            public int Pid;
            public bool Running;
            public Now Now;
            public List<Show> Continue = new List<Show>();
            public DateTime WrittenUtc;
        }

        static string StatusPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumenR", "status.json");

        static State cached;
        static DateTime cachedStamp;
        static DateTime checkedAt = DateTime.MinValue;

        /// <summary>What LumenR last reported, or null when it isn't installed (re-read only when the file changed).</summary>
        public static State Read()
        {
            if ((DateTime.UtcNow - checkedAt).TotalMilliseconds < 700) return cached;
            checkedAt = DateTime.UtcNow;
            try
            {
                var fi = new FileInfo(StatusPath);
                if (!fi.Exists || fi.Length > 2 * 1024 * 1024) { cached = null; return null; }
                if (cached != null && fi.LastWriteTimeUtc == cachedStamp) { RefreshRunning(cached); return cached; }
                var st = Parse(File.ReadAllText(fi.FullName, Encoding.UTF8));
                if (st != null) st.WrittenUtc = fi.LastWriteTimeUtc;
                if (st != null && !File.Exists(LaunchExe(st))) st = null; // moved or deleted since
                cached = st; cachedStamp = fi.LastWriteTimeUtc;
                if (st != null) RefreshRunning(st);
                return st;
            }
            catch (Exception ex) { Log.Throttled("lumenr-read", "LumenR: couldn't read its status (" + ex.Message + ")"); cached = null; return null; }
        }

        public static bool Installed => Read() != null;

        /// <summary>Something is playing in LumenR right now (it reports every few seconds while it does).</summary>
        public static Now Playing
        {
            get
            {
                var st = Read();
                if (st?.Now == null || !st.Running) return null;
                return (DateTime.UtcNow - st.WrittenUtc).TotalSeconds < 20 ? st.Now : null;
            }
        }

        static void RefreshRunning(State st)
        {
            if (!st.Running) return;
            try { using var p = Process.GetProcessById(st.Pid); if (p.HasExited) st.Running = false; }
            catch { st.Running = false; } // it closed (or crashed) without saying so
        }

        static string LaunchExe(State st) => st.Launch.Length > 0 ? st.Launch[0] : null;

        static State Parse(string text)
        {
            if (!(MiniJson.Parse(text) is Dictionary<string, object> d)) return null;
            if (Str(d, "app") != "LumenR") return null;
            var st = new State
            {
                Pid = (int)Num(d, "pid"),
                Running = d.TryGetValue("running", out var r) && r is bool b && b,
                Launch = (d.TryGetValue("launch", out var l) && l is List<object> ll ? ll.OfType<string>().Take(8).ToArray() : new string[0]),
            };
            // only ever start a program file (python.exe/pythonw.exe with main.py, or LumenR.exe)
            if (st.Launch.Length == 0 || !st.Launch[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || st.Launch.Any(a => a.IndexOf('"') >= 0))
                return null;
            if (d.TryGetValue("now", out var n) && n is Dictionary<string, object> nd)
                st.Now = new Now
                {
                    Key = Str(nd, "key"), Title = Str(nd, "title"), Episode = Str(nd, "episode"),
                    EpisodeTitle = Str(nd, "episode_title"), Poster = Str(nd, "poster"), Pos = Num(nd, "pos"), Dur = Num(nd, "dur"),
                };
            if (d.TryGetValue("continue", out var c) && c is List<object> cl)
                foreach (var o in cl.Take(20))
                    if (o is Dictionary<string, object> cd)
                        st.Continue.Add(new Show
                        {
                            Key = Str(cd, "key"), Title = Str(cd, "title"), Category = Str(cd, "category"), Label = Str(cd, "label"),
                            Poster = Str(cd, "poster"), Watched = (int)Num(cd, "watched"), Total = (int)Num(cd, "total"),
                            Progress = (float)Math.Max(0, Math.Min(1, Num(cd, "progress"))),
                        });
            return st;
        }

        static string Str(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v is string s ? (s.Length > 500 ? s.Substring(0, 500) : s) : "";
        static double Num(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v is double x && !double.IsNaN(x) && !double.IsInfinity(x) ? x : 0;

        // ---------- telling LumenR what to do ----------

        public static bool PlayNext(string key) => Start(new[] { "--play", key });
        public static bool OpenShow(string key) => Start(new[] { "--open", key });
        public static bool Open() => Start(new[] { "--show" });

        static bool Start(string[] extra)
        {
            var st = Read();
            if (st == null) return false;
            try
            {
                var args = st.Launch.Skip(1).Concat(extra).Select(Quote);
                var psi = new ProcessStartInfo(st.Launch[0], string.Join(" ", args))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(st.Launch.Length > 1 && File.Exists(st.Launch[1]) ? st.Launch[1] : st.Launch[0]),
                };
                Process.Start(psi)?.Dispose();
                Log.Write("LumenR: " + string.Join(" ", extra.Take(1)) + (extra.Length > 1 ? " (a show)" : ""));
                return true;
            }
            catch (Exception ex) { Log.Error("LumenR.Start", ex); return false; }
        }

        /// <summary>One command-line argument, quoted the way Windows programs split them.</summary>
        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in a)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { sb.Append('\\', slashes * 2 + 1); slashes = 0; sb.Append('"'); continue; }
                sb.Append('\\', slashes); slashes = 0; sb.Append(ch);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }
    }

    /// <summary>Shares WispR's colours (worked out from the wallpaper) in %APPDATA%\WispR\theme.json, for add-ons like LumenR.</summary>
    static class ThemeExport
    {
        static string last;

        public static void Write(Theme t)
        {
            if (t == null) return;
            try
            {
                string H(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
                var sb = new StringBuilder();
                sb.Append("{\n  \"app\": \"WispR\",\n  \"version\": 1,\n");
                sb.Append("  \"pid\": ").Append(Process.GetCurrentProcess().Id).Append(",\n");
                sb.Append("  \"light\": ").Append(t.IsLight ? "true" : "false").Append(",\n");
                sb.Append("  \"corner\": ").Append(Ui.Corner).Append(",\n");
                foreach (var (key, _) in Theme.ColorSlots)
                    sb.Append("  \"").Append(key).Append("\": \"").Append(H(t.Get(key))).Append("\",\n");
                sb.Length -= 2;
                sb.Append("\n}\n");
                string json = sb.ToString();
                if (json == last) return;
                last = json;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WispR");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "theme.json"), tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Throttled("theme-export", "Couldn't share the colours (" + ex.Message + ")"); }
        }
    }

    /// <summary>A small JSON reader: objects become dictionaries, arrays lists, numbers doubles.</summary>
    static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            try { var v = Value(s, ref i, 0); return v; }
            catch { return null; }
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 32) throw new FormatException("too deep");
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("end");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = String(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException(":");
                    d[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    char n = s[i++];
                    if (n == '}') return d;
                    if (n != ',') throw new FormatException(",");
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    char n = s[i++];
                    if (n == ']') return l;
                    if (n != ',') throw new FormatException(",");
                }
            }
            if (c == '"') return String(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string String(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("\"");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }
    }
}
