using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Seeded fuzz and property checks over the text formats and ledgers: thousands of generated
    /// inputs, each checked for a PROPERTY rather than a hand-picked answer.
    ///
    ///   - filter files: whatever a filter holds, writing it and reading it back gives a filter that
    ///     keeps exactly the same elements, and a second round trip changes nothing; random junk
    ///     lines never throw and never break the filter;
    ///   - the triage and resolved ledgers: the same history in a SHUFFLED order replays to the same
    ///     answer. That is the ledgers' whole contract - two seats append to one file, interleaved -
    ///     and the hand-written orderings in StatusReplayChecks/ResolvedReplayChecks can only try a few;
    ///   - CSV quoting: every field parses back to what was meant, and never starts a formula;
    ///   - panel search: a row holding every search term always matches.
    ///
    /// SEEDED, so a failure reproduces exactly: the failing check names its seed and case number.
    /// The checklist calls this "fuzz testing with random or unusual inputs".
    /// </summary>
    public static class FuzzChecks
    {
        private const string P = "fuzz: ";
        private const int Seed = 20261005;

        // Deliberately awkward: separators of other formats, quotes, a formula lead, non-ASCII.
        // Not '|', tab or newline: the filter and ledger formats escape or forbid those by design
        // (covered by their own checks), so a property over them would be testing the escape, not the reader.
        private static readonly string[] Pool = { "", "x", "chilled water", "Hot", "HOT ", "\u00e9\u2192", "a=b", "1,5", "\"q\"", "-100mm", "# not a comment", "s.kw" };
        private static readonly string[] Names = { "System Type", "Description", "Mark", "Workset" };

        public static void RunAll(Action<string, bool?> check)
        {
            FilterRoundTrip(check);
            FilterJunk(check);
            StatusReplayOrder(check);
            ResolvedReplayOrder(check);
            CsvQuoting(check);
            SearchTerms(check);
        }

        private static string Pick(Random r, string[] a) { return a[r.Next(a.Length)]; }

        // ------------------------------------------------------------------ filters

        private sealed class Vals : ParameterFilter.IValueSource
        {
            public readonly Dictionary<string, string> V = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Family = "", Type = "";
            public string Read(string name, ParamScope scope) { string s; return V.TryGetValue(name ?? "", out s) ? s : null; }
            public bool TypeExcluded(List<TypeKey> drops) { return ParameterFilter.TypeNamesExcluded(Family, Type, drops); }
        }

        private static ParamScope Scope(Random r) { return (ParamScope)r.Next(3); }

        private static ParameterFilter RandomFilter(Random r)
        {
            var f = new ParameterFilter();
            if (r.Next(3) == 0) f.Keyword = string.Join(",", Enumerable.Range(0, r.Next(3)).Select(_ => Pick(r, Pool)));
            if (r.Next(3) == 0) f.ExcludeKeyword = string.Join(",", Enumerable.Range(0, r.Next(3)).Select(_ => Pick(r, Pool)));
            for (int i = r.Next(3); i > 0; i--) f.Parameters.Add(new ParamRef { Name = Pick(r, Names), Scope = Scope(r) });
            for (int i = r.Next(3); i > 0; i--) f.Picked.Add(new ValuePick { ParameterName = Pick(r, Names), Scope = Scope(r), Value = Pick(r, Pool) });
            for (int i = r.Next(2); i > 0; i--) f.Excluded.Add(new ValuePick { ParameterName = Pick(r, Names), Scope = Scope(r), Value = Pick(r, Pool) });
            for (int i = r.Next(2); i > 0; i--) f.OnlyValues.Add(new ValuePick { ParameterName = Pick(r, Names), Scope = Scope(r), Value = Pick(r, Pool) });
            for (int i = r.Next(2); i > 0; i--) f.ExcludedTypes.Add(new TypeKey { Family = Pick(r, Pool), Type = Pick(r, Pool) });
            for (int i = r.Next(4); i > 0; i--) f.Rules.Add(RandomRule(r));
            for (int g = r.Next(3); g > 0; g--)
            {
                var grp = new RuleGroup { Join = r.Next(2) == 0 ? RuleJoin.Or : RuleJoin.And };
                for (int i = r.Next(3); i > 0; i--) grp.Rules.Add(RandomRule(r));
                f.Groups.Add(grp);
            }
            return f;
        }

        private static Rule RandomRule(Random r)
        {
            var rule = new Rule { ParameterName = Pick(r, Names), Scope = Scope(r), Op = (RuleOp)r.Next(6) };
            for (int i = r.Next(3); i > 0; i--) rule.Values.Add(Pick(r, Pool));
            return rule;
        }

        private static Vals RandomElement(Random r)
        {
            var v = new Vals { Family = Pick(r, Pool), Type = Pick(r, Pool) };
            foreach (var n in Names) if (r.Next(4) != 0) v.V[n] = Pick(r, Pool);
            return v;
        }

        /// <summary>Read serialized lines back the way the settings file does: key before the first '=', minus the prefix.</summary>
        private static ParameterFilter Read(List<string> lines, string prefix)
        {
            var f = new ParameterFilter();
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                f.ApplyLine(line.Substring(prefix.Length, eq - prefix.Length), line.Substring(eq + 1));
            }
            return f;
        }

        private static void FilterRoundTrip(Action<string, bool?> check)
        {
            var r = new Random(Seed);
            string firstMismatch = null, firstUnstable = null;
            int cases = 400, elements = 25;
            for (int c = 0; c < cases && firstMismatch == null && firstUnstable == null; c++)
            {
                var f = RandomFilter(r);
                var s1 = f.Serialize("s");
                var back = Read(s1, "s");
                var s2 = back.Serialize("s");
                var s3 = Read(s2, "s").Serialize("s");
                if (!s2.SequenceEqual(s3)) firstUnstable = "case " + c + ": " + string.Join(" / ", s2);
                for (int e = 0; e < elements; e++)
                {
                    var el = RandomElement(r);
                    if (f.Matches(el) != back.Matches(el)) { firstMismatch = "case " + c + ", element " + e + ": " + string.Join(" / ", s1); break; }
                }
            }
            check(P + "a filter written and read back keeps exactly the same elements (" + cases + " random filters x " + elements + " elements, seed " + Seed + ")"
                  + (firstMismatch == null ? "" : " - FIRST MISMATCH " + firstMismatch), firstMismatch == null);
            check(P + "a second write-read round trip changes nothing" + (firstUnstable == null ? "" : " - " + firstUnstable), firstUnstable == null);
        }

        private static void FilterJunk(Action<string, bool?> check)
        {
            var r = new Random(Seed + 1);
            var keys = new[] { "kw", "xkw", "param", "pick", "xpick", "only", "xtype", "rule", "group", "grule", "bogus", "" };
            var parts = new[] { "", "0", "1", "-1", "999", "1001", "99999999999", "Either", "Type", "Instance", "Is", "IsNot", "Contains", "And", "Or", "x", "\u00e9", "=" };
            string threw = null;
            int lines = 0;
            for (int c = 0; c < 300 && threw == null; c++)
            {
                var f = RandomFilter(r);
                try
                {
                    for (int i = 0; i < 12; i++)
                    {
                        var value = string.Join("|", Enumerable.Range(0, r.Next(7)).Select(_ => Pick(r, parts)));
                        f.ApplyLine(Pick(r, keys), r.Next(20) == 0 ? null : value);
                        lines++;
                    }
                    f.Matches(RandomElement(r));
                    f.Serialize("t");
                    f.Summary();
                    f.RulesForDisplay();
                }
                catch (Exception ex) { threw = "case " + c + ": " + ex.GetType().Name + " " + ex.Message; }
            }
            check(P + "random junk filter lines (" + lines + ") never throw, and the filter still matches, saves and describes itself"
                  + (threw == null ? "" : " - " + threw), threw == null);
            check(P + "a junk line can never pad a filter past its index limit",
                Enumerable.Range(0, 1).All(_ =>
                {
                    var f = new ParameterFilter();
                    f.ApplyLine("rule", "1001|Either|Is|A|x");
                    f.ApplyLine("group", "1001|Or");
                    f.ApplyLine("grule", "0|1001|Either|Is|A|x");
                    return f.Rules.Count == 0 && f.Groups.Count == 0;
                }));
        }

        // ------------------------------------------------------------------ ledgers

        private static List<T> Shuffled<T>(Random r, List<T> list)
        {
            var copy = new List<T>(list);
            for (int i = copy.Count - 1; i > 0; i--) { int j = r.Next(i + 1); var t = copy[i]; copy[i] = copy[j]; copy[j] = t; }
            return copy;
        }

        private static string Iso(DateTime t) { return t.ToString("o", CultureInfo.InvariantCulture); }

        private static string StatusState(Dictionary<string, ClashStatusEntry> m)
        {
            var sb = new StringBuilder();
            foreach (var k in m.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var e = m[k];
                sb.Append(k).Append('=').Append(e.Kind).Append('@').Append(Iso(e.WhenUtc)).Append(' ').Append(e.By).Append(' ').Append(e.Note)
                  .Append(e.Gone ? " gone@" + Iso(e.GoneUtc) : "")
                  .Append(" detail:").Append(e.HostId).Append('/').Append(e.HostName).Append('/').Append(e.OtherId).Append('/').Append(e.ModelName).Append(';');
            }
            return sb.ToString();
        }

        private static void StatusReplayOrder(Action<string, bool?> check)
        {
            var r = new Random(Seed + 2);
            var keys = new[] { "100|0|200", "300|0|400", "5|9|6" };
            var events = new[] { "Pending", "Coordinated", "Ignored", "New", "Gone" };
            var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
            string first = null;
            int histories = 600;
            for (int h = 0; h < histories && first == null; h++)
            {
                // Distinct times: two decisions at the same instant genuinely have no order, so a tie is not a fair test.
                var minutes = Shuffled(r, Enumerable.Range(0, 60).ToList());
                var lines = new List<string>();
                int n = 1 + r.Next(12);
                for (int i = 0; i < n; i++)
                {
                    var ev = Pick(r, events);
                    var head = new List<string> { Pick(r, keys), ev, Iso(t0.AddMinutes(minutes[i])), "user" + r.Next(3), r.Next(3) == 0 ? "note " + i : "" };
                    if (ev != "Gone" && r.Next(2) == 0)
                        head.AddRange(new[] { (1000 + i).ToString(CultureInfo.InvariantCulture), "Host " + i, "Pipes", (2000 + i).ToString(CultureInfo.InvariantCulture), "Other " + i, "Walls", "M" + i });
                    lines.Add(string.Join(ClashStatusReplay.Sep.ToString(), head));
                }

                var a = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
                var b = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
                int e1, l1, r1, e2, l2, r2;
                ClashStatusReplay.Replay(lines, a, out e1, out l1, out r1);
                ClashStatusReplay.Replay(Shuffled(r, lines), b, out e2, out l2, out r2);
                var sa = StatusState(a);
                var sb = StatusState(b);
                if (sa != sb || e1 != e2) first = "history " + h + ": " + sa + "  VS shuffled  " + sb;
            }
            check(P + "the triage ledger replays to the same decisions, gones AND element detail in any order (" + histories + " random histories, seed " + Seed + ")"
                  + (first == null ? "" : " - FIRST DIFFERENCE " + first), first == null);
        }

        private static void ResolvedReplayOrder(Action<string, bool?> check)
        {
            var r = new Random(Seed + 3);
            var keys = new[] { "100|0|200", "300|0|400", "5|9|6" };
            var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
            string first = null;
            int histories = 600;
            for (int h = 0; h < histories && first == null; h++)
            {
                var minutes = Shuffled(r, Enumerable.Range(0, 120).ToList());
                int mi = 0;
                var occurrences = new List<ResolvedRecord>();
                var lines = new List<string>();
                for (int i = 1 + r.Next(6); i > 0; i--)
                {
                    var rec = new ResolvedRecord { Key = Pick(r, keys), WhenUtc = t0.AddMinutes(minutes[mi++]), By = "u", HostId = 1, OtherId = 2, HostName = "h", OtherName = "o", HostCategory = "c", OtherCategory = "d", ModelName = "m" };
                    occurrences.Add(rec);
                    lines.Add(ResolvedLogReplay.Line(rec, ResolvedLogReplay.Resolution, default(DateTime)));
                }
                for (int i = r.Next(5); i > 0; i--)
                {
                    // Mostly retract a real occurrence; sometimes one that never existed.
                    var target = r.Next(4) == 0
                        ? new ResolvedRecord { Key = Pick(r, keys), WhenUtc = t0.AddMinutes(minutes[mi++]) }
                        : occurrences[r.Next(occurrences.Count)];
                    lines.Add(ResolvedLogReplay.Line(target, ResolvedLogReplay.Retraction, t0.AddDays(1).AddMinutes(minutes[mi++])));
                }

                Func<List<string>, string> state = ls =>
                {
                    var into = new List<ResolvedRecord>();
                    int ev, ret;
                    ResolvedLogReplay.Replay(ls, into, out ev, out ret);
                    return ev + "/" + ret + ":" + string.Join(";", into.Select(x => x.Key + "@" + Iso(x.WhenUtc) + (x.Retracted ? " retracted@" + Iso(x.RetractedUtc) : ""))
                                                              .OrderBy(x => x, StringComparer.Ordinal));
                };
                var sa = state(lines);
                var sb = state(Shuffled(r, lines));
                if (sa != sb) first = "history " + h + ": " + sa + "  VS shuffled  " + sb;
            }
            check(P + "the resolved ledger replays to the same entries and withdrawals in any order (" + histories + " random histories, seed " + Seed + ")"
                  + (first == null ? "" : " - FIRST DIFFERENCE " + first), first == null);
        }

        // ------------------------------------------------------------------ CSV and search

        private static readonly string[] AnyRow = { "anything" };

        /// <summary>Does Excel read a cell starting with this character as a formula?</summary>
        private static bool IsFormulaLead(char c) { return c == '=' || c == '+' || c == '-' || c == '@'; }

        /// <summary>One RFC 4180 field back to its text.</summary>
        private static string Unquote(string field)
        {
            if (field.Length >= 2 && field[0] == '"' && field[field.Length - 1] == '"')
                return field.Substring(1, field.Length - 2).Replace("\"\"", "\"");
            return field;
        }

        private static void CsvQuoting(Action<string, bool?> check)
        {
            var r = new Random(Seed + 4);
            var alphabet = "ab ,\"=+-@\r\n\u00e9;'".ToCharArray();
            string bad = null;
            for (int c = 0; c < 3000 && bad == null; c++)
            {
                var sb = new StringBuilder();
                for (int i = r.Next(8); i > 0; i--) sb.Append(alphabet[r.Next(alphabet.Length)]);
                var s = sb.ToString();
                var q = CsvText.Q(s);

                var meant = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                if (meant.Length > 0 && IsFormulaLead(meant[0])) meant = "'" + meant;

                bool ok = q.IndexOf('\r') < 0 && q.IndexOf('\n') < 0                         // one physical line
                          && Unquote(q) == meant                                              // reads back to what was meant
                          && (q.Length == 0 || !IsFormulaLead(Unquote(q)[0]))                  // never a formula
                          && ((q.IndexOf(',') < 0 && q.IndexOf('"') < 0) || (q[0] == '"' && q[q.Length - 1] == '"'));   // comma or quote -> quoted
                if (!ok) bad = "case " + c + ": [" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "] -> [" + q + "]";
            }
            check(P + "CSV quoting: 3000 random fields each stay one line, read back to what was meant, and never start a formula"
                  + (bad == null ? "" : " - " + bad), bad == null);
        }

        private static void SearchTerms(Action<string, bool?> check)
        {
            var r = new Random(Seed + 5);
            var alphabet = "aB 1\"-.(*)\u00e9".ToCharArray();
            string bad = null;
            for (int c = 0; c < 3000 && bad == null; c++)
            {
                var sb = new StringBuilder();
                for (int i = r.Next(12); i > 0; i--) sb.Append(alphabet[r.Next(alphabet.Length)]);
                var q = sb.ToString();
                try
                {
                    var terms = ClashSearch.Terms(q);
                    var row = new[] { "unrelated", string.Join(" ", terms).ToUpperInvariant() };
                    if (!ClashSearch.Matches(q, row)) bad = "case " + c + ": query [" + q + "] did not match a row holding all its terms";
                    else if (!ClashSearch.IsActive(q) && !ClashSearch.Matches(q, AnyRow)) bad = "case " + c + ": an inactive query dropped a row";
                }
                catch (Exception ex) { bad = "case " + c + ": [" + q + "] threw " + ex.GetType().Name; }
            }
            check(P + "panel search: 3000 random queries never throw, and a row holding every term (in any case) always matches"
                  + (bad == null ? "" : " - " + bad), bad == null);
        }
    }
}
