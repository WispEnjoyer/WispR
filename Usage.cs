using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WispR
{
    /// <summary>
    /// Learns what you launch. Two signals:
    ///  - how often / how recently each app is used
    ///  - which app you picked for what you typed (typing "c" then picking Chrome
    ///    teaches it that "c" means Chrome, without overriding a better match forever)
    /// Stored as plain text in %APPDATA%\WispR\usage.txt.
    /// </summary>
    sealed class Usage
    {
        sealed class Stat { public int Count; public long LastTicks; }

        readonly Dictionary<string, Stat> apps = new Dictionary<string, Stat>();
        readonly Dictionary<string, Dictionary<string, int>> picks = new Dictionary<string, Dictionary<string, int>>();
        readonly string file;

        public Usage()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WispR");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, "usage.txt");
            Load();
        }

        public int Count(AppEntry e) => apps.TryGetValue(e.Key, out var s) ? s.Count : 0;

        public long LastUsed(AppEntry e) => apps.TryGetValue(e.Key, out var s) ? s.LastTicks : 0;

        public int Boost(AppEntry e, string query)
        {
            int boost = 0;
            if (apps.TryGetValue(e.Key, out var s))
            {
                boost += Math.Min(160, (int)(45 * Math.Log(1 + s.Count)));
                double days = (DateTime.UtcNow - new DateTime(s.LastTicks, DateTimeKind.Utc)).TotalDays;
                if (days < 1) boost += 40; else if (days < 7) boost += 20;
            }
            if (query.Length > 0 && picks.TryGetValue(query, out var map) && map.TryGetValue(e.Key, out int n))
                boost += Math.Min(450, 250 + 60 * n);
            return boost;
        }

        public void Record(AppEntry e, string query)
        {
            if (!apps.TryGetValue(e.Key, out var s)) apps[e.Key] = s = new Stat();
            s.Count++;
            s.LastTicks = DateTime.UtcNow.Ticks;

            for (int i = 1; i <= Math.Min(query.Length, 12); i++)
            {
                var prefix = query.Substring(0, i);
                if (!picks.TryGetValue(prefix, out var map)) picks[prefix] = map = new Dictionary<string, int>();
                map.TryGetValue(e.Key, out int n);
                map[e.Key] = n + 1;
            }
            Save();
        }

        void Load()
        {
            try
            {
                if (!File.Exists(file)) return;
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length == 4 && p[0] == "U")
                        apps[p[1]] = new Stat { Count = int.Parse(p[2]), LastTicks = long.Parse(p[3]) };
                    else if (p.Length == 4 && p[0] == "Q")
                    {
                        if (!picks.TryGetValue(p[1], out var map)) picks[p[1]] = map = new Dictionary<string, int>();
                        map[p[2]] = int.Parse(p[3]);
                    }
                }
            }
            catch { /* corrupt file: start fresh */ }
        }

        void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in apps)
                    sb.Append("U\t").Append(Clean(kv.Key)).Append('\t').Append(kv.Value.Count).Append('\t').Append(kv.Value.LastTicks).Append('\n');
                foreach (var q in picks)
                    foreach (var kv in q.Value)
                        sb.Append("Q\t").Append(Clean(q.Key)).Append('\t').Append(Clean(kv.Key)).Append('\t').Append(kv.Value).Append('\n');
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
            }
            catch { /* not fatal */ }
        }

        static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
