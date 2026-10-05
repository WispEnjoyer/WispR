using System;
using System.Linq;

namespace WispR
{
    /// <summary>
    /// "Smart" matching. Tiers, best first:
    ///   exact name  >  name prefix  >  word prefix ("code" → Visual Studio Code)
    ///   >  initials ("vsc", "ps")  >  all words ("vis cod")  >  substring
    ///   >  fuzzy subsequence ("chrm" → Chrome)  >  typo-tolerant ("spotfy", "fierfox")
    /// Learned usage is added on top by <see cref="Usage"/>.
    /// </summary>
    static class Matcher
    {
        public static string Normalize(string s) =>
            string.Join(" ", (s ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));

        /// <returns>Score, or -1 if no match.</returns>
        public static int Score(string q, AppEntry e)
        {
            string n = e.NameLower;
            if (n == q) return 1000;
            if (n.StartsWith(q, StringComparison.Ordinal)) return 900 - Math.Min(60, n.Length - q.Length);

            var tokens = q.Split(' ');
            if (tokens.Length == 1)
            {
                foreach (var w in e.Words)
                    if (w.StartsWith(q, StringComparison.Ordinal)) return 800 - Math.Min(60, n.Length - q.Length);
                if (q.Length >= 2 && e.Initials.StartsWith(q, StringComparison.Ordinal))
                    return 760 + (e.Initials.Length == q.Length ? 20 : 0);
                if (q.Length >= 2 && e.Initials.IndexOf(q, StringComparison.Ordinal) > 0)
                    return 740; // "ps" -> Windows PowerShell
            }
            else if (tokens.All(t => e.Words.Any(w => w.StartsWith(t, StringComparison.Ordinal))))
            {
                return 720;
            }

            int idx = n.IndexOf(q, StringComparison.Ordinal);
            if (idx >= 0) return 650 - Math.Min(50, idx);

            string qc = q.Replace(" ", "");
            int fuzzy = Subsequence(qc, n, e.WordStarts, preferWordStarts: true);
            if (fuzzy == 0) fuzzy = Subsequence(qc, n, e.WordStarts, preferWordStarts: false);
            if (fuzzy > 0) return 300 + fuzzy;

            if (qc.Length >= 4)
            {
                int allowed = qc.Length >= 7 ? 2 : 1;
                int best = int.MaxValue;
                foreach (var w in e.Words.Append(n.Replace(" ", "")))
                {
                    if (w.Length < 3) continue;
                    best = Math.Min(best, Distance(qc, w));
                    if (w.Length > qc.Length) best = Math.Min(best, Distance(qc, w.Substring(0, qc.Length)));
                }
                if (best <= allowed) return 260 - 60 * best;
            }
            return -1;
        }

        /// <summary>Push uninstallers, readmes, help links etc. down the list.</summary>
        public static int Penalty(AppEntry e)
        {
            foreach (var w in e.Words)
                if (w == "uninstall" || w == "uninstaller" || w == "readme" || w == "help" || w == "manual" ||
                    w == "documentation" || w == "website" || w == "license" || w == "changelog")
                    return -250;
            return 0;
        }

        // Characters of q appear in order in n. Rewards consecutive runs and word starts.
        static int Subsequence(string q, string n, bool[] wordStart, bool preferWordStarts)
        {
            if (q.Length < 2) return 0;
            int score = 0, pos = 0, last = -2;
            foreach (char c in q)
            {
                int plain = n.IndexOf(c, pos);
                if (plain < 0) return 0;
                int found = plain;
                if (preferWordStarts && plain != last + 1)
                {
                    for (int i = pos; i < n.Length; i++)
                        if (n[i] == c && i < wordStart.Length && wordStart[i]) { found = i; break; }
                }
                if (found == last + 1) score += 12;
                if (found < wordStart.Length && wordStart[found]) score += 18;
                score -= Math.Min(8, found - pos);
                last = found;
                pos = found + 1;
            }
            if (n[0] != q[0]) score -= 20; // matches that start where the name starts feel more natural
            return Math.Max(1, Math.Min(250, 100 + score));
        }

        // Optimal string alignment distance (Levenshtein + adjacent swaps).
        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }
    }
}
