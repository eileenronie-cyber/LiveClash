using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Edge cases in the Revit-free code that MUTATION TESTING showed no check could see: Stryker.NET
    /// planted a defect, every check still passed, and the defect was a real behaviour (not a rewrite
    /// that changes nothing). Each check below pins one such behaviour. Grouped by area:
    ///
    ///   - the filter file reader: the shared s/t dispatcher, legacy lines without an index, field counts,
    ///     the index limit at its boundary, and the exact text Serialize writes;
    ///   - the triage ledger: short lines, a digit-leading note that is NOT damage, ties, a Gone at the
    ///     exact decision time, the merge path, and event counts;
    ///   - the resolved ledger: the line format, occurrence identity, unreadable or zoneless times,
    ///     a retraction without its own time, event counts;
    ///   - identity lines, the log (stack traces, the roll boundary, the default path), search quoting,
    ///     CSV file names, and the "what changed" wording and events.
    /// </summary>
    public static class EdgeCaseChecks
    {
        private const string P = "edge: ";

        // Fixed inputs as fields rather than array literals at the call (static analysis rule CA1861).
        private static readonly string[] SerializedExpected =
        {
            "skw=k/1", "sxkw=", "sparam=Type|Sys/Type", "spick=Either|P/1|v/1", "sxpick=Instance|X|x/1", "sonly=Either|O|",
            "srule=0|Either|IsEmpty|R/1|", "srule=1|Either|Is|R2|", "srule=2|Either|Contains|R3|a/b", "srule=2|Either|Contains|R3|",
            "sgroup=0|And", "sgrule=0|0|Either|IsNotEmpty|G/1|", "sgrule=0|1|Either|Is|G2|v/2"
        };
        private static readonly string[] OneJunkLine = { "x" };
        private static readonly string[] ZonelessFields = { "Z", "2026-09-08T10:00:00", "u", "1", "h", "c", "2", "o", "d", "m" };
        private static readonly string[] UnreadableFields = { "Y", "not a time", "u", "x", "h", "c", "y", "o", "d", "m" };
        private static readonly string[] NoClaimLines = { "identity=x", "data" };
        private static readonly string[] EmptyCommentLines = { "#", "   " };
        private static readonly string[] WallRow = { "Basic wall here" };
        private static readonly string[] DrywallRow = { "Drywall" };

        public static void RunAll(Action<string, bool?> check)
        {
            FilterFile(check);
            TriageLedger(check);
            ResolvedLedger(check);
            Identity(check);
            LogEdges(check);
            SearchAndCsv(check);
            WhatChanged(check);
        }

        // ------------------------------------------------------------------ filter file

        private static void FilterFile(Action<string, bool?> check)
        {
            var src = ParameterFilter.Default();
            var tgt = ParameterFilter.Default();
            bool cs = false, ct = false;
            bool rejected = !ParameterFilter.ApplyFilterLine(src, tgt, null, "x", ref cs, ref ct)
                         && !ParameterFilter.ApplyFilterLine(src, tgt, "", "x", ref cs, ref ct)
                         && !ParameterFilter.ApplyFilterLine(src, tgt, "s", "x", ref cs, ref ct)
                         && !ParameterFilter.ApplyFilterLine(src, tgt, "xkw", "x", ref cs, ref ct)
                         && !ParameterFilter.ApplyFilterLine(src, tgt, "sbogus", "x", ref cs, ref ct)
                         && !ParameterFilter.ApplyFilterLine(src, tgt, "group", "1", ref cs, ref ct);
            check(P + "filter file: a key that is not s/t plus a filter stem is not a filter line, and changes nothing",
                rejected && !cs && !ct && src.Parameters.Count == 2 && tgt.Parameters.Count == 2 && src.Keyword == "");

            bool h1 = ParameterFilter.ApplyFilterLine(src, tgt, "sparam", "Either|Mark", ref cs, ref ct);
            bool h2 = ParameterFilter.ApplyFilterLine(src, tgt, "sparam", "Type|Level", ref cs, ref ct);
            check(P + "filter file: the first source line clears the default parameters ONCE, so every saved one is kept",
                h1 && h2 && cs && !ct && src.Parameters.Count == 2 && src.Parameters[0].Name == "Mark" && src.Parameters[1].Name == "Level"
                && src.Parameters[1].Scope == ParamScope.Type && tgt.Parameters.Count == 2);
            bool h3 = ParameterFilter.ApplyFilterLine(src, tgt, "tkw", "a|b", ref cs, ref ct);
            check(P + "filter file: a target line goes to the target, clearing its defaults, and kw keeps its text verbatim",
                h3 && ct && tgt.Keyword == "a|b" && tgt.Parameters.Count == 0 && src.Keyword == "");
            ParameterFilter.ApplyFilterLine(src, tgt, "sxkw", "zone", ref cs, ref ct);
            check(P + "filter file: xkw sets the drop keyword", src.ExcludeKeyword == "zone" && src.Keyword == "");

            var legacy = new ParameterFilter();
            legacy.ApplyLine("rule", "Either|Contains|Description|chilled");
            legacy.ApplyLine("rule", "Either|Contains|DESCRIPTION|supply");
            legacy.ApplyLine("rule", "Either|DoesNotContain|Description|x");
            legacy.ApplyLine("rule", "Type|Contains|Description|y");
            check(P + "filter file: legacy lines with no index merge by scope + operator + parameter (any case) - they cannot be told apart",
                legacy.Rules.Count == 3 && legacy.Rules[0].Values.Count == 2 && legacy.Rules[0].Values[1] == "supply"
                && legacy.Rules[1].Op == RuleOp.DoesNotContain && legacy.Rules[2].Scope == ParamScope.Type);

            var short3 = new ParameterFilter();
            short3.ApplyLine("rule", "Either|IsEmpty|Mark");
            short3.ApplyLine("rule", "0|Either|Is");
            short3.ApplyLine("rule", "7|Either|Is");
            check(P + "filter file: a three-field legacy rule is valid; an indexed rule missing its parameter is dropped without padding",
                short3.Rules.Count == 1 && short3.Rules[0].Op == RuleOp.IsEmpty && short3.Rules[0].ParameterName == "Mark" && short3.Rules[0].Values.Count == 0);

            var vals = new ParameterFilter();
            vals.ApplyLine("rule", "0|Either|Is|Mark|");
            vals.ApplyLine("rule", "0|Either|Is|Mark|A1");
            vals.ApplyLine("rule", "1|Either|IsNotEmpty|Level");
            check(P + "filter file: an empty value line adds no value; a four-field indexed rule is a rule with no value",
                vals.Rules.Count == 2 && vals.Rules[0].Values.Count == 1 && vals.Rules[0].Values[0] == "A1" && vals.Rules[1].Op == RuleOp.IsNotEmpty);

            var bound = new ParameterFilter();
            bound.ApplyLine("rule", "1000|Either|Is|A|x");
            bool ruleAt1000 = bound.Rules.Count == 1001;
            bound.ApplyLine("group", "1000|And");
            bool groupAt1000 = bound.Groups.Count == 1001 && bound.Groups[1000].Join == RuleJoin.And;
            var bound2 = new ParameterFilter();
            bound2.ApplyLine("grule", "1000|1000|Either|Is|A|x");
            check(P + "filter file: index 1000 is the last one allowed, for rules, groups and group rules",
                ruleAt1000 && groupAt1000 && bound2.Groups.Count == 1001 && bound2.Groups[1000].Rules.Count == 1001);

            var g = new ParameterFilter();
            g.ApplyLine("grule", "0|0|Type|Contains|Mark|x");
            check(P + "filter file: a group rule creates its group, and the rule carries scope, operator and parameter",
                g.Groups.Count == 1 && g.Groups[0].Rules.Count == 1 && g.Groups[0].Rules[0].Scope == ParamScope.Type
                && g.Groups[0].Rules[0].Op == RuleOp.Contains && g.Groups[0].Rules[0].ParameterName == "Mark" && g.Groups[0].Rules[0].Values[0] == "x");

            var gl = new ParameterFilter();
            gl.ApplyLine("grule", "0|Either|Contains|Description|a");
            gl.ApplyLine("grule", "0|Either|Contains|description|b");
            gl.ApplyLine("grule", "0|Either|DoesNotContain|Description|c");
            gl.ApplyLine("grule", "0|Instance|Contains|Description|d");
            gl.ApplyLine("grule", "0|Either|IsEmpty|Mark");
            gl.ApplyLine("grule", "0|1|Either|Is");
            check(P + "filter file: legacy group rules merge the same way, a four-field one is valid, a short indexed one is dropped",
                gl.Groups.Count == 1 && gl.Groups[0].Rules.Count == 4 && gl.Groups[0].Rules[0].Values.Count == 2
                && gl.Groups[0].Rules[1].Op == RuleOp.DoesNotContain && gl.Groups[0].Rules[2].Scope == ParamScope.Instance
                && gl.Groups[0].Rules[3].Op == RuleOp.IsEmpty && gl.Groups[0].Rules[3].Values.Count == 0);

            var noPad = new ParameterFilter();
            noPad.ApplyLine("grule", "2|0|Either|Is");
            check(P + "filter file: a group rule too short to read leaves no empty groups behind", noPad.Groups.Count == 0);

            var picks = new ParameterFilter();
            picks.ApplyLine("pick", "Either|Mark");
            picks.ApplyLine("bogus", "Either|Mark|A");
            picks.ApplyLine("xtype", "Fam|Type");
            picks.ApplyLine("only", "Type|Level|L1");
            check(P + "filter file: a pick needs three fields, an unknown key is ignored, xtype and only read their fields",
                picks.Picked.Count == 0 && picks.Excluded.Count == 0 && picks.OnlyValues.Count == 1 && picks.OnlyValues[0].Scope == ParamScope.Type
                && picks.ExcludedTypes.Count == 1 && picks.ExcludedTypes[0].Family == "Fam" && picks.ExcludedTypes[0].Type == "Type");

            check(P + "filter file: a family+type key escapes '|' to '/', and null reads as an empty pair",
                ParameterFilter.SerializeTypeKey(null) == "|" && ParameterFilter.SerializeTypeKey(new TypeKey { Family = "a|b", Type = "c|d" }) == "a/b|c/d"
                && ParameterFilter.SerializeTypeKey(new TypeKey { Family = null, Type = null }) == "|"
                && ParameterFilter.ParseTypeKey(null) == null && ParameterFilter.ParseTypeKey("x") == null
                && ParameterFilter.ParseTypeKey("a|b").Family == "a" && ParameterFilter.ParseTypeKey("a|b").Type == "b");

            var w = new ParameterFilter { Keyword = "k|1", ExcludeKeyword = null };
            w.Parameters.Add(new ParamRef { Name = "Sys|Type", Scope = ParamScope.Type });
            w.Picked.Add(new ValuePick { ParameterName = "P|1", Value = "v|1" });
            w.Excluded.Add(new ValuePick { ParameterName = "X", Value = "x|1", Scope = ParamScope.Instance });
            w.OnlyValues.Add(new ValuePick { ParameterName = "O", Value = null });
            w.Rules.Add(new Rule { ParameterName = "R|1", Op = RuleOp.IsEmpty, Values = { "ignored" } });
            w.Rules.Add(new Rule { ParameterName = "R2", Op = RuleOp.Is });
            w.Rules.Add(new Rule { ParameterName = "R3", Op = RuleOp.Contains, Values = { "a|b", null } });
            w.Groups.Add(new RuleGroup { Join = RuleJoin.And, Rules = { new Rule { ParameterName = "G|1", Op = RuleOp.IsNotEmpty, Values = { "z" } }, new Rule { ParameterName = "G2", Op = RuleOp.Is, Values = { "v|2" } } } });
            var lines = string.Join("\n", w.Serialize("s"));
            check(P + "filter file: Serialize writes each kind exactly, escaping '|' to '/' and writing one line per value",
                lines == string.Join("\n", SerializedExpected));
        }

        // ------------------------------------------------------------------ triage ledger

        private static string T(params string[] f) { return string.Join(ClashStatusReplay.Sep.ToString(), f); }

        private static Dictionary<string, ClashStatusEntry> Status(out int events, params string[] lines)
        {
            var m = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int l, r;
            ClashStatusReplay.Replay(lines, m, out events, out l, out r);
            return m;
        }

        private static void TriageLedger(Action<string, bool?> check)
        {
            const string K = "1|0|2";
            const string T10 = "2026-09-08T10:00:00.0000000Z", T11 = "2026-09-08T11:00:00.0000000Z";
            int ev;

            check(P + "triage: an entry has detail when EITHER id is known",
                new ClashStatusEntry { OtherId = 5 }.HasDetail && new ClashStatusEntry { HostId = 5 }.HasDetail && !new ClashStatusEntry().HasDetail);

            var three = Status(out ev, T(K, "Pending", T10));
            check(P + "triage: a three-field line (key, event, time) is a decision with no author and no note",
                three.ContainsKey(K) && three[K].By == "" && three[K].Note == "" && ev == 1);
            var four = Status(out ev, T(K, "Pending", T10, "ann"));
            check(P + "triage: a four-field line has its author and no note", four[K].By == "ann" && four[K].Note == "");
            var five = Status(out ev, T(K, "Pending", T10, "ann", "see RFI 12"));
            check(P + "triage: the author and note are read from fields 4 and 5", five[K].By == "ann" && five[K].Note == "see RFI 12");

            var digitNote = Status(out ev, T(K, "Pending", T10, "ann", "12 sleeves agreed"));
            check(P + "triage: a normal line whose note starts with a digit is not mistaken for bug damage",
                digitNote[K].Note == "12 sleeves agreed" && !digitNote[K].HasDetail);
            var tenEmpty = Status(out ev, T(K, "Pending", T10, "ann", "", "a", "b", "c", "d", "e"));
            check(P + "triage: a ten-field line with an empty note is read without error and not 'repaired'",
                tenEmpty.ContainsKey(K) && tenEmpty[K].Note == "" && !tenEmpty[K].HasDetail);

            var allDigits = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int l2, rep;
            ClashStatusReplay.Replay(new[] { T(K, "Pending", T10, "ann", "118", "Cat", "5", "Other", "OC", "M") }, allDigits, out ev, out l2, out rep);
            check(P + "triage: a damaged line whose host name was empty repairs to the id alone",
                rep == 1 && allDigits[K].HostId == 100 && allDigits[K].HostName == "" && allDigits[K].HostCategory == "Cat");

            Status(out ev, T(K, "Pending", T10), T(K, "Gone", T11), T(K, "Gone", T11), T("9|0|9", "Gone", T10));
            check(P + "triage: every Gone line counts as an event, even with nothing to attach to", ev == 4);

            var tie = Status(out ev, T(K, "Pending", T10), T(K, "Ignored", T10));
            check(P + "triage: two decisions at the exact same time - the one later in the file wins", tie[K].Kind == ClashStatusKind.Ignored);
            var goneTie = Status(out ev, T(K, "Coordinated", T10), T(K, "Gone", T10));
            check(P + "triage: a Gone at the exact time of the decision resolves it", goneTie[K].Gone);
            var older = Status(out ev, T(K, "Coordinated", T11), T(K, "Pending", T10));
            check(P + "triage: an older decision read later still counts as an event", ev == 2 && older[K].Kind == ClashStatusKind.Coordinated);

            // The merge path: the dictionary already holds what an earlier replay loaded.
            var merged = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal)
            {
                { K, new ClashStatusEntry { Kind = ClashStatusKind.Pending, WhenUtc = new DateTime(2026, 9, 8, 9, 0, 0, DateTimeKind.Utc), HostId = 7, HostName = "Kept", ModelName = "M" } }
            };
            ClashStatusReplay.Replay(new[] { T(K, "Coordinated", T10) }, merged, out ev, out l2, out rep);
            check(P + "triage: merging a newer detail-less line into a loaded entry keeps the loaded entry's detail",
                merged[K].Kind == ClashStatusKind.Coordinated && merged[K].HostId == 7 && merged[K].HostName == "Kept");
            var mergedNewer = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal)
            {
                { K, new ClashStatusEntry { Kind = ClashStatusKind.Ignored, WhenUtc = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc) } }
            };
            ClashStatusReplay.Replay(new[] { T(K, "Coordinated", T10, "a", "", "3", "H", "C", "4", "O", "C", "M") }, mergedNewer, out ev, out l2, out rep);
            check(P + "triage: an older detailed line merged into a newer loaded entry lends it detail but not its decision",
                mergedNewer[K].Kind == ClashStatusKind.Ignored && mergedNewer[K].HostId == 3);
        }

        // ------------------------------------------------------------------ resolved ledger

        private static void ResolvedLedger(Action<string, bool?> check)
        {
            var when = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);
            var rec = new ResolvedRecord { Key = "1|0|2", WhenUtc = when, By = "b\tx", HostId = 1, HostName = "h", HostCategory = "c", OtherId = 2, OtherName = "o", OtherCategory = "d", ModelName = "m" };
            var res = ResolvedLogReplay.Line(rec, ResolvedLogReplay.Resolution, default(DateTime)).Split('\t');
            var ret = ResolvedLogReplay.Line(rec, ResolvedLogReplay.Retraction, when.AddHours(2)).Split('\t');
            check(P + "resolved: a line is twelve tab fields - key, time, cleaned author, ids and names, model, event, retraction time",
                res.Length == 12 && res[0] == "1|0|2" && res[1] == "2026-09-08T10:00:00.0000000Z" && res[2] == "b x" && res[3] == "1" && res[9] == "m"
                && res[10] == "Resolved" && res[11] == "" && ret[10] == "Retracted" && ret[11] == "2026-09-08T12:00:00.0000000Z");
            check(P + "resolved: no record writes nothing; a null key or event writes empty / Resolved",
                ResolvedLogReplay.Line(null, null, when) == ""
                && ResolvedLogReplay.Line(new ResolvedRecord { WhenUtc = when }, null, when).Split('\t')[0] == ""
                && ResolvedLogReplay.Line(new ResolvedRecord { WhenUtc = when }, null, when).Split('\t')[10] == "Resolved"
                && ResolvedLogReplay.Clean(null) == "" && ResolvedLogReplay.Clean("a\tb\r\nc") == "a b  c");

            Func<string[], List<ResolvedRecord>> replay = ls => { var into = new List<ResolvedRecord>(); int e, r; ResolvedLogReplay.Replay(ls, into, out e, out r); return into; };
            var a = new ResolvedRecord { Key = "A", WhenUtc = when };
            var b = new ResolvedRecord { Key = "B", WhenUtc = when };
            var both = replay(new[]
            {
                ResolvedLogReplay.Line(a, null, when), ResolvedLogReplay.Line(b, null, when),
                ResolvedLogReplay.Line(b, ResolvedLogReplay.Retraction, when.AddHours(1))
            });
            check(P + "resolved: a retraction withdraws the occurrence with ITS key, not another pair recorded at the same moment",
                both.Count == 2 && !both.First(x => x.Key == "A").Retracted && both.First(x => x.Key == "B").Retracted);

            var hdr = new List<ResolvedRecord>();
            int events, retracted;
            ResolvedLogReplay.Replay(null, hdr, out events, out retracted);
            ResolvedLogReplay.Replay(OneJunkLine, null, out events, out retracted);
            check(P + "resolved: no lines or no destination is a no-op", hdr.Count == 0 && events == 0 && retracted == 0);

            var odd = replay(new[]
            {
                "# a\tb\tc\td\te\tf\tg\th\ti\tj\tk",
                "too\tshort",
                "",
                null,
                string.Join("\t", ZonelessFields),
                string.Join("\t", UnreadableFields)
            });
            check(P + "resolved: comments (even with ten fields), short lines and blanks are skipped",
                odd.Count == 2 && odd.All(x => x.Key == "Z" || x.Key == "Y"));
            var z = odd.First(x => x.Key == "Z");
            var y = odd.First(x => x.Key == "Y");
            check(P + "resolved: a time with no zone is read as UTC; an unreadable time or id falls back instead of failing",
                z.WhenUtc.Kind == DateTimeKind.Utc && z.WhenUtc.Hour == 10 && y.WhenUtc == DateTime.MinValue && y.HostId == 0 && y.HostName == "h");

            var shortRetraction = string.Join("\t", new[] { "A", ResolvedLogReplay.Line(a, null, when).Split('\t')[1], "u", "0", "", "", "0", "", "", "", "Retracted" });
            var sr = new List<ResolvedRecord>();
            ResolvedLogReplay.Replay(new[] { ResolvedLogReplay.Line(a, null, when), shortRetraction, ResolvedLogReplay.Line(b, null, when) }, sr, out events, out retracted);
            check(P + "resolved: a retraction missing its own time uses the occurrence's; every line counts as an event",
                retracted == 1 && sr.First(x => x.Key == "A").RetractedUtc == when && events == 3);
        }

        // ------------------------------------------------------------------ identity

        private static void Identity(Action<string, bool?> check)
        {
            check(P + "identity: blank lines and non-comment lines are ignored; the last claim wins; spacing is tolerated",
                ModelIdentity.RecordedIn(new[] { null, "", "identity=notacomment", "#   identity= first ", "  # identity=second " }) == "second"
                && ModelIdentity.RecordedIn(NoClaimLines) == ""
                && ModelIdentity.RecordedIn(EmptyCommentLines) == "");

            var dir = Path.Combine(Path.GetTempPath(), "liveclash-edge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (Log.RedirectForTests(Path.Combine(dir, "log.txt")))
                {
                    var missing = ModelIdentity.ReadLines(Path.Combine(dir, "nope.txt"));
                    check(P + "identity: a file that does not exist reads as nothing, with no warning and no retry",
                        missing == null && Log.WarningCount == 0 && ModelIdentity.ReadRecorded(Path.Combine(dir, "nope.txt")) == "");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ log

        private static void LogEdges(Action<string, bool?> check)
        {
            check(P + "log: the real log lives in %LOCALAPPDATA%\\LiveClash\\liveclash.log",
                Log.Path.EndsWith(Path.Combine("LiveClash", "liveclash.log"), StringComparison.OrdinalIgnoreCase)
                && Log.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase));

            var dir = Path.Combine(Path.GetTempPath(), "liveclash-edge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "l.log");
            try
            {
                var handle = Log.RedirectForTests(file);
                Log.Warn("never thrown", new InvalidOperationException("no stack"));
                string thrownText;
                try { throw new ArgumentException("thrown"); }
                catch (Exception ex) { Log.Warn("was thrown", ex); }
                thrownText = File.ReadAllText(file);
                var lines = File.ReadAllLines(file);
                check(P + "log: an exception that was never thrown adds one line; a thrown one adds its stack trace too",
                    lines.Length >= 5 && lines[1] == "    InvalidOperationException: no stack" && lines[2].Contains("WARN   was thrown")
                    && lines[3] == "    ArgumentException: thrown" && lines[4].TrimStart().StartsWith("at ", StringComparison.Ordinal));
                check(P + "log: the session summary names its start time", Log.SessionSummary().Contains(" since " + DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));

                Log.MaxBytes = new FileInfo(file).Length;   // exactly at the limit: rolls
                Log.Info("next");
                bool rolledAtLimit = File.Exists(file + ".1") && !File.ReadAllText(file).Contains("never thrown");
                Log.MaxBytes = new FileInfo(file).Length + 1;   // one byte under: does not
                Log.Info("again");
                bool keptUnder = File.ReadAllText(file).Contains("next");
                check(P + "log: a file exactly at the size limit rolls; one byte under does not", rolledAtLimit && keptUnder);

                File.Delete(file);
                File.Delete(file + ".1");
                Log.MaxBytes = 1;
                Log.Info("first line of a fresh file");
                check(P + "log: with no file yet there is nothing to roll; the first line starts it",
                    File.Exists(file) && !File.Exists(file + ".1") && File.ReadAllText(file).Contains("first line"));

                handle.Dispose();
                using (Log.RedirectForTests(Path.Combine(dir, "inner.log")))
                {
                    Log.Error("inner");
                    handle.Dispose();   // a second dispose of the outer handle must not undo the inner redirect
                    check(P + "log: disposing a redirect twice restores once, never over a later redirect",
                        Log.Path == Path.Combine(dir, "inner.log") && Log.ErrorCount == 1);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ search and CSV

        private static void SearchAndCsv(Action<string, bool?> check)
        {
            check(P + "search: a quoted phrase keeps its inner spaces; an unquoted word is trimmed",
                ClashSearch.Terms("\" a b \"")[0] == " a b " && ClashSearch.Terms("  ab  ")[0] == "ab"
                && ClashSearch.Terms("x\"y z\"")[0] == "xy z");
            check(P + "search: a quoted phrase with its spaces matches only text that has those spaces",
                ClashSearch.Matches("\" wall \"", WallRow) && !ClashSearch.Matches("\" wall \"", DrywallRow));

            var when = new DateTime(2026, 10, 5, 7, 8, 0);
            check(P + "CSV file name: blank kind and model get defaults; control characters and Windows-forbidden ones become '_', spaces stay",
                CsvStamp.FileName("  ", null, when) == "LiveClash-export-20261005-0708.csv"
                && CsvStamp.FileName("record", "My Model\u001f:1", when) == "My Model__1-record-20261005-0708.csv"
                && CsvStamp.FileName(" issues ", " A ", when) == "A-issues-20261005-0708.csv");
        }

        // ------------------------------------------------------------------ what changed

        private static ClashResult Row(string key, string model, ClashSeverity s = ClashSeverity.Hard)
        {
            return new ClashResult { Key = key, ModelName = model, Severity = s, HostElementName = "h", OtherElementName = "o" };
        }

        private static void WhatChanged(Action<string, bool?> check)
        {
            check(P + "what changed: link names read as 'the link', one name, 'A and B', or a count",
                ClashChangeTracker.NamesText(null) == "the link" && ClashChangeTracker.NamesText(new List<string>()) == "the link"
                && ClashChangeTracker.NamesText(new List<string> { "STR" }) == "STR"
                && ClashChangeTracker.NamesText(new List<string> { "STR", "ARC" }) == "STR and ARC"
                && ClashChangeTracker.NamesText(new List<string> { "a", "b", "c" }) == "3 links");
            check(P + "what changed: plurals - 1 clash, 2 clashes, 2 meshes, 2 rows",
                ClashChangeTracker.Plural(1, "clash") == "1 clash" && ClashChangeTracker.Plural(2, "clash") == "2 clashes"
                && ClashChangeTracker.Plural(2, "mesh") == "2 meshes" && ClashChangeTracker.Plural(2, "row") == "2 rows" && ClashChangeTracker.Plural(0, "row") == "0 rows");

            var one = new List<string> { "STR" };
            var two = new List<string> { "STR", "ARC" };
            Func<ClashChangeSet, List<string>, bool, string> headline = (set, names, gone) =>
                new ClashChangeTracker.Summary { Set = set, LinkNames = names, AllGone = gone }.Headline;

            var cleared1 = new ClashChangeSet(); cleared1.Cleared.Add(Row("k", "STR"));
            var cleared2 = new ClashChangeSet(); cleared2.Cleared.Add(Row("k", "STR")); cleared2.Cleared.Add(Row("j", "ARC"));
            check(P + "what changed: an unloaded link says it is no longer there and how many clashes left with it",
                headline(new ClashChangeSet(), one, true) == "STR is no longer there: it had no clashes listed."
                && headline(cleared1, one, true) == "STR is no longer there: 1 clash with it left the list."
                && headline(cleared2, two, true) == "STR and ARC are no longer there: 2 clashes with them left the list.");

            var none = new ClashChangeSet();
            var inScope = new ClashChangeSet { BeforeInScope = 3, AfterInScope = 0 };
            var inScopeAfter = new ClashChangeSet { BeforeInScope = 0, AfterInScope = 2 };
            check(P + "what changed: 'no clashes listed' only when neither side had any; otherwise 'no change'",
                headline(none, one, false) == "Since STR changed: no clashes with it are listed, before or after."
                && headline(none, two, false) == "Since STR and ARC changed: no clashes with them are listed, before or after."
                && headline(inScope, one, false) == "Since STR changed: no change to the clashes with it."
                && headline(inScopeAfter, two, false) == "Since STR and ARC changed: no change to the clashes with them.");

            var mixed = new ClashChangeSet();
            mixed.New.Add(Row("n", "STR")); mixed.Cleared.Add(Row("c", "STR"));
            mixed.SeverityChanged.Add(new KeyValuePair<ClashResult, ClashResult>(Row("s", "STR"), Row("s", "STR", ClashSeverity.Clearance)));
            check(P + "what changed: new, cleared and severity changes are listed together",
                headline(mixed, one, false) == "Since STR changed: 1 new, 1 cleared, 1 changed severity.");

            var sev = new ClashChangeSet(); sev.SeverityChanged.Add(new KeyValuePair<ClashResult, ClashResult>(Row("s", "S"), Row("s", "S")));
            var clr = new ClashChangeSet(); clr.Cleared.Add(Row("c", "S"));
            check(P + "what changed: a set is empty only with no new, no cleared AND no severity change",
                new ClashChangeSet().IsEmpty && !sev.IsEmpty && !clr.IsEmpty && !mixed.IsEmpty && ClashChange.Norm(null) == "" && ClashChange.Norm("") == "");

            var sum = new ClashChangeTracker.Summary { Set = mixed, LinkNames = one };
            check(P + "what changed: IsNew needs a set and a key", !new ClashChangeTracker.Summary().IsNew("n") && !sum.IsNew(null)
                && new ClashChangeTracker.Summary { AllGone = true }.Caveat == "" && new ClashChangeTracker.Summary { AfterFullCheck = true }.Caveat == ""
                && new ClashChangeTracker.Summary().Caveat.Length > 0);

            // The tracker's events: raised exactly when its state changes. Global state, so saved and put back.
            var saved = ClashChangeTracker.Snapshot();
            int raised = 0;
            Action count = () => raised++;
            ClashChangeTracker.Changed += count;
            try
            {
                ClashChangeTracker.Restore(null);
                raised = 0;
                ClashChangeTracker.Reset();
                ClashChangeTracker.DismissSummary();
                ClashChangeTracker.Complete(Array.Empty<ClashResult>(), true);
                bool quietWhenEmpty = raised == 0 && ClashChangeTracker.Pending == null && ClashChangeTracker.Current == null;

                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "STR");
                bool noSource = raised == 0 && ClashChangeTracker.Pending == null;

                ClashChangeTracker.SnapshotSource = () => { throw new InvalidOperationException("store busy"); };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "STR");
                bool throwingSource = raised == 0 && ClashChangeTracker.Pending == null;
                check(P + "what changed: with nothing pending, no list source, or a source that throws, nothing happens and nothing is raised",
                    quietWhenEmpty && noSource && throwingSource);

                ClashChangeTracker.SnapshotSource = () => new List<ClashResult> { Row("k", "STR"), null };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, null);
                int afterFirst = raised;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, null);
                int afterSame = raised;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceMoved, "A link");
                check(P + "what changed: the first change raises once, an identical repeat raises nothing, a non-removal change raises again",
                    afterFirst == 1 && afterSame == 1 && raised == 2 && ClashChangeTracker.Pending.LinkNames.Count == 1
                    && ClashChangeTracker.Pending.LinkNames[0] == "A link" && !ClashChangeTracker.Pending.AllGone && ClashChangeTracker.Pending.Before.Count == 1);

                var copy = ClashChangeTracker.Pending.Copy();
                check(P + "what changed: a copied baseline keeps 'all gone' and its time", copy.AllGone == ClashChangeTracker.Pending.AllGone && copy.SinceUtc == ClashChangeTracker.Pending.SinceUtc && copy.SinceUtc != default(DateTime));

                ClashChangeTracker.Complete(Array.Empty<ClashResult>(), false);
                bool recheckKeeps = ClashChangeTracker.Pending != null && ClashChangeTracker.Current != null && raised == 3;
                ClashChangeTracker.Complete(Array.Empty<ClashResult>(), true);
                bool fullConsumes = ClashChangeTracker.Pending == null && ClashChangeTracker.Current != null && raised == 4;
                ClashChangeTracker.Complete(Array.Empty<ClashResult>(), true);
                bool fullClearsOld = ClashChangeTracker.Current == null && raised == 5;
                check(P + "what changed: a recheck keeps the baseline, a full check consumes it, and a later full check clears the old summary - one event each",
                    recheckKeeps && fullConsumes && fullClearsOld);

                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "STR");
                ClashChangeTracker.Complete(Array.Empty<ClashResult>(), false);
                int beforeDismiss = raised;
                ClashChangeTracker.DismissSummary();
                bool dismissed = ClashChangeTracker.Current == null && raised == beforeDismiss + 1;
                ClashChangeTracker.Reset();
                check(P + "what changed: dismiss raises once; reset clears a pending baseline and raises once",
                    dismissed && ClashChangeTracker.Pending == null && raised == beforeDismiss + 2);
            }
            finally
            {
                ClashChangeTracker.Changed -= count;
                ClashChangeTracker.Restore(saved);
            }
        }
    }
}
