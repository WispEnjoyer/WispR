using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WispR
{
    sealed class TimerItem
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Title = "";
        public TimeSpan Length;
        public DateTime EndUtc;          // when it rings (while running)
        public bool Paused;
        public TimeSpan LeftWhenPaused;
        public bool Done;                // rang, not dismissed yet

        public TimeSpan Left => Done ? TimeSpan.Zero : Paused ? LeftWhenPaused : (EndUtc - DateTime.UtcNow < TimeSpan.Zero ? TimeSpan.Zero : EndUtc - DateTime.UtcNow);
        public float Progress => Length.Ticks <= 0 ? 1 : Math.Max(0, Math.Min(1, 1f - (float)(Left.Ticks / (double)Length.Ticks)));
        public string DisplayTitle => Title.Length > 0 ? Title : "Timer";
    }

    /// <summary>
    /// The timers started from the launcher ("set timer 10m", "set timer "Brot backen" 10m"). They're
    /// saved, so they keep running across restarts of WispR; when one ends it rings until it's ticked off.
    /// </summary>
    static class Timers
    {
        public static readonly List<TimerItem> Items = new List<TimerItem>();
        public static event Action Changed;
        public static event Action<TimerItem> Rang;

        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WispR", "timers.txt");

        public static TimerItem Start(string title, TimeSpan length)
        {
            var t = new TimerItem { Title = (title ?? "").Trim(), Length = length, EndUtc = DateTime.UtcNow + length };
            Items.Add(t);
            Log.Write("Timer started: " + t.DisplayTitle + ", " + Describe(length) + ".");
            Save(); Changed?.Invoke();
            return t;
        }

        public static void Cancel(TimerItem t) { Items.Remove(t); Save(); Changed?.Invoke(); }

        public static void Dismiss(TimerItem t) => Cancel(t);

        public static void TogglePause(TimerItem t)
        {
            if (t.Done) return;
            if (t.Paused) { t.EndUtc = DateTime.UtcNow + t.LeftWhenPaused; t.Paused = false; }
            else { t.LeftWhenPaused = t.Left; t.Paused = true; }
            Save(); Changed?.Invoke();
        }

        public static void AddTime(TimerItem t, TimeSpan extra)
        {
            if (t.Done) { t.Done = false; t.Paused = false; t.EndUtc = DateTime.UtcNow + extra; t.Length = extra; }
            else if (t.Paused) { t.LeftWhenPaused += extra; t.Length += extra; }
            else { t.EndUtc += extra; t.Length += extra; }
            Save(); Changed?.Invoke();
        }

        /// <summary>Marks timers that have run out as done (and rings for them). Call regularly.</summary>
        public static void Check()
        {
            bool any = false;
            foreach (var t in Items)
                if (!t.Done && !t.Paused && DateTime.UtcNow >= t.EndUtc)
                {
                    t.Done = true; any = true;
                    Log.Write("Timer done: " + t.DisplayTitle + ".");
                    Rang?.Invoke(t);
                }
            if (any) { Save(); Changed?.Invoke(); }
        }

        public static bool AnyRinging => Items.Any(t => t.Done);

        /// <summary>The timer to show when collapsed: one that rang first, otherwise the one ending soonest.</summary>
        public static TimerItem Primary =>
            Items.FirstOrDefault(t => t.Done) ?? Items.Where(t => !t.Paused).OrderBy(t => t.EndUtc).FirstOrDefault() ?? Items.FirstOrDefault();

        // ---------- understanding "set timer …" ----------

        static readonly Regex Trigger = new Regex(@"^\s*(?:set\s+(?:a\s+)?|start\s+(?:a\s+)?|stell(?:e)?\s+(?:einen\s+)?)?(?:timer|wecker)\b\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Quoted = new Regex("[\"“„”«»'](.+?)[\"“”«»']");
        static readonly Regex Part = new Regex(
            @"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*(hours|hour|hrs|hr|h|stunden|stunde|std|minutes|minute|minuten|mins|min|m|seconds|second|secs|sec|sekunden|sekunde|sek|s)?(?![a-z\u00E4\u00F6\u00FC])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Clock = new Regex(@"\b(\d{1,2}):(\d{2})(?::(\d{2}))?\b");

        /// <summary>
        /// "set timer 10m", "timer 1h30m", "set timer "Brot backen" 10m", "timer 10 min pizza", "timer 1:30".
        /// Returns false when the text isn't about a timer at all; <paramref name="length"/> is zero when it is,
        /// but no time was given yet.
        /// </summary>
        public static bool TryParse(string text, out string title, out TimeSpan length)
        {
            title = ""; length = TimeSpan.Zero;
            var m = Trigger.Match(text ?? "");
            if (!m.Success) return false;
            string rest = m.Groups[1].Value;

            var q = Quoted.Match(rest);
            if (q.Success) { title = q.Groups[1].Value.Trim(); rest = rest.Remove(q.Index, q.Length); }

            var c = Clock.Match(rest);
            if (c.Success)
            {
                int a = int.Parse(c.Groups[1].Value), b = int.Parse(c.Groups[2].Value);
                length = c.Groups[3].Success ? new TimeSpan(a, b, int.Parse(c.Groups[3].Value)) : new TimeSpan(0, a, b); // h:mm:ss or m:ss
                rest = rest.Remove(c.Index, c.Length);
            }
            else
            {
                var used = new List<(int, int)>();
                foreach (Match p in Part.Matches(rest))
                {
                    if (!double.TryParse(p.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
                    string unit = p.Groups[2].Value.ToLowerInvariant();
                    if (unit.StartsWith("h") || unit.StartsWith("std") || unit.StartsWith("stunde")) length += TimeSpan.FromHours(v);
                    else if (unit.StartsWith("s")) length += TimeSpan.FromSeconds(v);
                    else length += TimeSpan.FromMinutes(v); // "m", "min…" or a bare number
                    used.Add((p.Index, p.Length));
                }
                for (int i = used.Count - 1; i >= 0; i--) rest = rest.Remove(used[i].Item1, used[i].Item2);
            }
            // whatever words are left (without quotes) make the title: "timer 10m pizza"
            if (title.Length == 0)
            {
                string words = Regex.Replace(rest, @"\b(for|und|and|für)\b", " ", RegexOptions.IgnoreCase);
                title = Regex.Replace(words, @"\s+", " ").Trim(' ', ',', '-', ':');
            }
            if (length > TimeSpan.FromHours(24)) length = TimeSpan.FromHours(24);
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            return true;
        }

        /// <summary>"10 min", "1 h 30 min", "45 s".</summary>
        public static string Describe(TimeSpan t)
        {
            var parts = new List<string>();
            if (t.TotalHours >= 1) parts.Add((int)t.TotalHours + " h");
            if (t.Minutes > 0) parts.Add(t.Minutes + " min");
            if (t.Seconds > 0 || parts.Count == 0) parts.Add(t.Seconds + " s");
            return string.Join(" ", parts);
        }

        /// <summary>Time left as a clock: "9:42", "1:05:00".</summary>
        public static string Clock2(TimeSpan t)
        {
            var r = TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));
            return r.TotalHours >= 1 ? ((int)r.TotalHours) + ":" + r.Minutes.ToString("00") + ":" + r.Seconds.ToString("00")
                                     : r.Minutes + ":" + r.Seconds.ToString("00");
        }

        // ---------- saving ----------

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                foreach (var t in Items)
                    sb.Append(t.Id).Append('|').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(t.Title))).Append('|')
                      .Append(t.Length.Ticks).Append('|').Append(t.EndUtc.Ticks).Append('|').Append(t.Paused ? 1 : 0).Append('|')
                      .Append(t.LeftWhenPaused.Ticks).Append('|').Append(t.Done ? 1 : 0).Append('\n');
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception ex) { Log.Error("Timers.Save", ex); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var f = line.Split('|');
                    if (f.Length < 7) continue;
                    var t = new TimerItem
                    {
                        Id = f[0],
                        Title = Encoding.UTF8.GetString(Convert.FromBase64String(f[1])),
                        Length = new TimeSpan(long.Parse(f[2])),
                        EndUtc = new DateTime(long.Parse(f[3]), DateTimeKind.Utc),
                        Paused = f[4] == "1",
                        LeftWhenPaused = new TimeSpan(long.Parse(f[5])),
                        Done = f[6] == "1",
                    };
                    // one that ran out long ago while WispR wasn't running: drop it quietly
                    if (!t.Paused && DateTime.UtcNow - t.EndUtc > TimeSpan.FromHours(6)) continue;
                    Items.Add(t);
                }
            }
            catch (Exception ex) { Log.Error("Timers.Load", ex); }
        }
    }
}
