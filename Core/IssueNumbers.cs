using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiveClash.Core
{
    /// <summary>
    /// Quotable issue numbers ("JD-0042"): a per-user prefix and a per-user counter, unique across
    /// seats with no shared counter and no clock (spec 6.10). Pure, no Revit.
    /// </summary>
    public static class IssueNumbers
    {
        private static readonly Regex Parsed = new Regex("^(\\p{L}+[0-9]*)-([0-9]+)\\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Two or three letters from a username: "jdoe" gives "JD", "jane.quinn.doe" gives "JQD". The
        /// part before an @ is used; words are runs of letters; one word gives its first two letters;
        /// a name with no letters gives "XX".
        /// </summary>
        public static string PrefixOf(string userName)
        {
            string s = userName ?? "";
            int at = s.IndexOf('@');
            if (at >= 0) s = s.Substring(0, at);

            var words = new List<string>();
            int start = -1;
            for (int i = 0; i <= s.Length; i++)
            {
                bool letter = i < s.Length && char.IsLetter(s[i]);
                if (letter && start < 0) start = i;
                if (!letter && start >= 0) { words.Add(s.Substring(start, i - start)); start = -1; }
            }

            string prefix;
            if (words.Count == 0) prefix = "XX";
            else if (words.Count == 1) prefix = words[0].Length >= 2 ? words[0].Substring(0, 2) : words[0] + "X";
            else prefix = string.Concat(words.Take(3).Select(w => w.Substring(0, 1)));
            return prefix.ToUpperInvariant();
        }

        /// <summary>The prefix itself if free, else the first free "JD2", "JD3", ... (compared without case).</summary>
        public static string FreePrefix(string basePrefix, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken == null ? Array.Empty<string>() : taken.Where(t => t != null), StringComparer.OrdinalIgnoreCase);
            string b = basePrefix ?? "";
            if (!set.Contains(b)) return b;
            for (int n = 2; ; n++)
            {
                string candidate = b + n.ToString(CultureInfo.InvariantCulture);
                if (!set.Contains(candidate)) return candidate;
            }
        }

        /// <summary>"JD-0042"; the number widens past 9999 and never wraps. A negative number is written as 0.</summary>
        public static string Format(string prefix, int number)
        {
            return (prefix ?? "") + "-" + Math.Max(0, number).ToString("D4", CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string issueNo, out string prefix, out int number)
        {
            prefix = null;
            number = 0;
            if (string.IsNullOrEmpty(issueNo)) return false;
            var m = Parsed.Match(issueNo);
            if (!m.Success) return false;
            int n;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
            prefix = m.Groups[1].Value.ToUpperInvariant();
            number = n;
            return true;
        }

        /// <summary>One user's counter record: written only by that user.</summary>
        public sealed class Counter
        {
            public string Uid { get; set; }
            public string UserName { get; set; }
            public string Prefix { get; set; }
            public int Next { get; set; } = 1;
        }

        /// <summary>The next number of a counter, and the counter advances (it never wraps).</summary>
        public static string Take(Counter c)
        {
            if (c == null || string.IsNullOrEmpty(c.Prefix)) return null;
            string s = Format(c.Prefix, c.Next);
            if (c.Next < int.MaxValue) c.Next++;
            return s;
        }

        public sealed class PrefixMove
        {
            public string Uid { get; set; }
            public string UserName { get; set; }
            public string OldPrefix { get; set; }
            public string NewPrefix { get; set; }
            /// <summary>The counter record that moves. RenumberPlan keys by this object, not by Uid: records with no UniqueId (null and empty) share one key (DR3-L2).</summary>
            internal Counter Source { get; set; }
        }

        private static List<Counter> Usable(IEnumerable<Counter> counters)
        {
            return (counters ?? Array.Empty<Counter>()).Where(c => c != null && !string.IsNullOrEmpty(c.Prefix)).ToList();
        }

        /// <summary>
        /// Counter records that share a prefix (the one window the design cannot close): the lowest
        /// UniqueId (ordinal) keeps it, every higher one moves to the next free prefix. No clock is read,
        /// and the answer does not depend on the order the records are given in.
        /// </summary>
        public static List<PrefixMove> PrefixMoves(IEnumerable<Counter> counters)
        {
            var moves = new List<PrefixMove>();
            var all = Usable(counters);
            var taken = new HashSet<string>(all.Select(c => c.Prefix), StringComparer.OrdinalIgnoreCase);

            foreach (var group in all.GroupBy(c => c.Prefix.ToUpperInvariant(), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(c => c.Uid ?? "", StringComparer.Ordinal).ThenBy(c => c.UserName ?? "", StringComparer.Ordinal).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    string np = FreePrefix(group.Key, taken);
                    taken.Add(np);
                    moves.Add(new PrefixMove { Source = ordered[i], Uid = ordered[i].Uid, UserName = ordered[i].UserName, OldPrefix = group.Key, NewPrefix = np });
                }
            }
            return moves;
        }

        public sealed class IssueMarker
        {
            public string MarkerUid { get; set; }
            public string IssueNo { get; set; }
            public string ReportedBy { get; set; }
        }

        public sealed class Renumber
        {
            public string MarkerUid { get; set; }
            public string OldIssueNo { get; set; }
            public string NewIssueNo { get; set; }
        }

        /// <summary>
        /// Which markers change number after the prefix moves; only those numbered in the window. A
        /// moved user's markers on the old prefix keep their number under the new prefix. One user on two
        /// PCs (same name, same prefix) cannot be told apart by name, so only a number issued twice moves:
        /// the lowest marker UniqueId stays and the next takes the other counter's prefix.
        /// </summary>
        public static List<Renumber> RenumberPlan(IEnumerable<Counter> counters, IEnumerable<IssueMarker> markers)
        {
            var plan = new List<Renumber>();
            var all = Usable(counters);
            var moves = PrefixMoves(all);
            if (moves.Count == 0 || markers == null) return plan;

            var movedTo = new Dictionary<Counter, string>();   // by counter object: two records with no UniqueId must stay two (DR3-L2)
            foreach (var m in moves) movedTo[m.Source] = m.NewPrefix;

            var parsed = new List<Tuple<IssueMarker, string, int>>();
            foreach (var mk in markers)
            {
                string p = null; int n = 0;
                if (mk == null || mk.MarkerUid == null || mk.ReportedBy == null || !TryParse(mk.IssueNo, out p, out n)) continue;
                parsed.Add(Tuple.Create(mk, p, n));
            }

            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mv in moves)
            {
                string key = (mv.UserName ?? "").ToUpperInvariant() + "\u0001" + mv.OldPrefix;
                if (!done.Add(key)) continue;

                // The counters of this user on this prefix, the unmoved one first, then by UniqueId.
                var ofUser = all.Where(c => string.Equals(c.UserName ?? "", mv.UserName ?? "", StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(c.Prefix, mv.OldPrefix, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(c => movedTo.ContainsKey(c) ? 1 : 0)
                                .ThenBy(c => c.Uid ?? "", StringComparer.Ordinal).ToList();
                // Stryker disable once Statement : ofUser holds the counter of mv itself, so it is never empty
                if (ofUser.Count == 0) continue;
                Func<Counter, string> final = c => { string np; return movedTo.TryGetValue(c, out np) ? np : mv.OldPrefix; };

                var candidates = parsed.Where(t => string.Equals(t.Item1.ReportedBy, mv.UserName ?? "", StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(t.Item2, mv.OldPrefix, StringComparison.OrdinalIgnoreCase));
                foreach (var sameNumber in candidates.GroupBy(t => t.Item3))
                {
                    var byUid = sameNumber.OrderBy(t => t.Item1.MarkerUid, StringComparer.Ordinal).ToList();
                    for (int i = 0; i < byUid.Count; i++)
                    {
                        // More markers on this number than the user has counters: the surplus has no counter to move to and is left as found (known limitation, DR3 INFO).
                        // Sharing the last counter's prefix would have given two markers one NEW number.
                        if (i >= ofUser.Count) continue;
                        string target = final(ofUser[i]);
                        if (string.Equals(target, mv.OldPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        plan.Add(new Renumber { MarkerUid = byUid[i].Item1.MarkerUid, OldIssueNo = byUid[i].Item1.IssueNo, NewIssueNo = Format(target, sameNumber.Key) });
                    }
                }
            }
            return plan.OrderBy(r => r.MarkerUid, StringComparer.Ordinal).ToList();
        }
    }
}
