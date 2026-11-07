using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 2: the Refresh plan (spec 6.2 H1, 6.4 E3, 6.5, 6.6, 6.7, 6.9, 7; rules RF3, RF5,
    /// RF9, RF14, RF15, RF20 and TESTINGS additions A1-A7). Revit-free, so both tiers run it.
    /// Red-first: tests/evidence/clash-schedule/BUILD-LOG.md.
    /// </summary>
    public static class MarkerPlanChecks
    {
        private const string P = "markers plan: ";
        private static readonly int[] Clearances = { 0, 50, 100 };
        private static readonly int[] Orders = { 1, 2, 3, 4, 5, 6 };
        private static readonly DateTimeOffset Day0 = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));

        public static void RunAll(Action<string, bool?> check)
        {
            Steady(check);
            Absence(check);
            Verdicts(check);
            Creation(check);
            Access(check);
            Duplicates(check);
            PendingChecks(check);
            Retention(check);
            Limits(check);
            Carries(check);
            Groups(check);
            IssueNumberRepair(check);
            Fuzz(check);
        }

        // ------------------------------------------------------------------ builders

        private static MarkerPlan.Options Opts(int daysAfter = 0)
        {
            return new MarkerPlan.Options { Today = Day0.AddDays(daysAfter), UserName = "jdoe" };
        }

        private static MarkerPlan.Input NewInput(int daysAfter = 0)
        {
            return new MarkerPlan.Input
            {
                Options = Opts(daysAfter),
                Counter = new IssueNumbers.Counter { Uid = "c1", UserName = "jdoe", Prefix = "JD", Next = 1 }
            };
        }

        private static string Triple(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

        /// <summary>A marker with unique id m000u for the pair (h t, o t); the record is what a past Refresh wrote.</summary>
        private static MarkerPlan.Existing Mk(int uid, int tripleNo, Action<MarkerRecord> tweak = null, MarkerAccess access = MarkerAccess.Free)
        {
            var r = new MarkerRecord
            {
                Key = "h" + tripleNo + "|0|o" + tripleNo, HostUid = "h" + tripleNo, OtherUid = "o" + tripleNo,
                HostId = 100 + tripleNo, OtherId = 200 + tripleNo,
                ClashId = ClashIdOf.Of("h" + tripleNo, "", "o" + tripleNo), IssueNo = IssueNumbers.Format("JD", uid),
                Status = MarkerStatus.New, ReportedBy = "jdoe", ReportedAt = Day0.AddDays(-9),
                Severity = "Hard", SetName = "MEP vs STR", GroupLabel = "", X = 1 + tripleNo, Y = 2, Z = 3,
                LastCheckedAt = Day0.AddDays(-3), LastCheckedBy = "jdoe", Seq = 5
            };
            if (tweak != null) tweak(r);
            return new MarkerPlan.Existing { MarkerUid = "m" + uid.ToString("D4", CultureInfo.InvariantCulture), Record = r, Access = access, HeldBy = access == MarkerAccess.HeldByOther ? "kdoe" : null, ShownHash = "h" };
        }

        private static MarkerPlan.Existing Mk(int n, Action<MarkerRecord> tweak = null, MarkerAccess access = MarkerAccess.Free)
        {
            return Mk(n, n, tweak, access);
        }

        /// <summary>The row that says exactly what the marker already records: a re-test that found nothing new.</summary>
        private static MarkerPlan.Row RowOf(MarkerPlan.Existing e)
        {
            var r = e.Record;
            return new MarkerPlan.Row
            {
                Key = r.Key, HostUid = r.HostUid, LinkUid = r.LinkUid, OtherUid = r.OtherUid,
                HostId = r.HostId, LinkId = r.LinkId, OtherId = r.OtherId, Verdict = PlanVerdict.Clashing,
                RuleClearanceMm = r.ClearanceMm, RuleInsulated = r.Insulated, Severity = r.Severity,
                HasPoint = true, X = r.X, Y = r.Y, Z = r.Z, SetName = r.SetName, AlsoSets = new List<string>(r.AlsoSets),
                GroupLabel = r.GroupLabel, ShownHash = e.ShownHash
            };
        }

        /// <summary>A pair with no marker yet.</summary>
        private static MarkerPlan.Row NewRow(int n, string severity = "Hard", Action<MarkerPlan.Row> tweak = null)
        {
            var row = new MarkerPlan.Row
            {
                Key = "h" + n + "|0|o" + n, HostUid = "h" + n, OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n,
                Verdict = PlanVerdict.Clashing, Severity = severity, HasPoint = true, X = n, Y = 1, Z = 2,
                SetName = "MEP vs STR", AlsoSets = new List<string>(), GroupLabel = "", ShownHash = "h"
            };
            if (tweak != null) tweak(row);
            return row;
        }

        private static MarkerPlan.Input With(int count, int daysAfter = 0)
        {
            var input = NewInput(daysAfter);
            for (int i = 1; i <= count; i++)
            {
                var m = Mk(i);
                input.Markers.Add(m);
                input.Rows.Add(RowOf(m));
            }
            return input;
        }

        private static int Total(MarkerPlan.Result r)
        {
            return r.Creations.Count + r.Changes.Count + r.Removals.Count;
        }

        private static string Fp(MarkerPlan.Result r)
        {
            Func<MarkerRecord, string> rec = x => x == null ? "-" : string.Join(";", MarkerFormats.Write(x).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            var sb = new StringBuilder();
            foreach (var c in r.Creations) sb.Append("C|").Append(c.Triple).Append('|').Append(rec(c.Record)).Append('|').Append(c.IssueNoReplaced).Append('\n');
            foreach (var c in r.Changes.OrderBy(x => x.MarkerUid, StringComparer.Ordinal))
                sb.Append("U|").Append(c.MarkerUid).Append('|').Append(string.Join(",", c.Fields)).Append('|').Append(rec(c.After))
                  .Append('|').Append(c.Resolved).Append(c.Reopened).Append(c.Moved).Append(c.PutBack).Append(c.WorksetFix).Append(c.Rekeyed).Append('\n');
            foreach (var c in r.Removals.OrderBy(x => x.MarkerUid, StringComparer.Ordinal)) sb.Append("R|").Append(c.MarkerUid).Append('|').Append(c.Reason).Append('|').Append(c.KeptUid).Append('\n');
            foreach (var c in r.RetentionOffers.OrderBy(x => x.MarkerUid, StringComparer.Ordinal)) sb.Append("O|").Append(c.MarkerUid).Append('\n');
            foreach (var c in r.PassedOver.OrderBy(x => x.MarkerUid, StringComparer.Ordinal)) sb.Append("P|").Append(c.MarkerUid).Append('|').Append(c.Reason).Append('\n');
            foreach (var c in r.NotRetested.OrderBy(x => x.MarkerUid, StringComparer.Ordinal)) sb.Append("N|").Append(c.MarkerUid).Append('|').Append(c.Reason).Append('\n');
            foreach (var c in r.NotMarked.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append("M|").Append(c.Key).Append('|').Append(c.Reason).Append('\n');
            foreach (var c in r.Pending.OrderBy(x => x.Triple, StringComparer.Ordinal)) sb.Append("D|").Append(c.Triple).Append('|').Append(c.Kind).Append('\n');
            foreach (var c in r.LostDecisions.OrderBy(x => x.RemovedUid, StringComparer.Ordinal)) sb.Append("L|").Append(c.RemovedUid).Append('|').Append(c.KeptUid).Append('|').Append(c.Status).Append('\n');
            sb.Append("T|").Append(r.Unchanged).Append('|').Append(r.Merges).Append('|').Append(r.ExistingMarkers).Append('|').Append(r.NeedsDialog).Append(r.OverCap).Append(r.OverWarn);
            return sb.ToString();
        }

        /// <summary>The model after the plan was applied, with the same rows: what the next Refresh would see.</summary>
        private static MarkerPlan.Input Apply(MarkerPlan.Input input, MarkerPlan.Result result)
        {
            var next = new MarkerPlan.Input { Options = input.Options, Counter = result.CounterAfter ?? input.Counter, Rows = input.Rows };
            var removed = new HashSet<string>(result.Removals.Select(x => x.MarkerUid), StringComparer.Ordinal);
            var changed = result.Changes.ToDictionary(c => c.MarkerUid, c => c, StringComparer.Ordinal);
            var rowHash = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in input.Rows) rowHash[ClashIdOf.Triple(row.HostUid, row.LinkUid, row.OtherUid)] = row.ShownHash;
            foreach (var m in input.Markers)
            {
                if (removed.Contains(m.MarkerUid)) continue;
                MarkerPlan.Change c;
                if (changed.TryGetValue(m.MarkerUid, out c))
                    next.Markers.Add(new MarkerPlan.Existing { MarkerUid = m.MarkerUid, Record = c.After, Access = m.Access, HeldBy = m.HeldBy, ShownHash = c.ShownHash ?? m.ShownHash });
                else next.Markers.Add(m);
            }
            int k = 0;
            foreach (var c in result.Creations)
                next.Markers.Add(new MarkerPlan.Existing { MarkerUid = "n" + (k++).ToString("D5", CultureInfo.InvariantCulture), Record = c.Record, Access = MarkerAccess.Free, ShownHash = c.ShownHash });
            var closed = new HashSet<string>(result.Pending.Where(o => o.Kind != PendingKind.Kept).Select(o => o.Triple), StringComparer.Ordinal);
            next.Pending = input.Pending.Where(p => !closed.Contains(p.Triple)).ToList();
            return next;
        }

        private static bool Balanced(MarkerPlan.Result r)
        {
            var c = r.ToCounts();
            return MarkerSummary.Balanced(c) && c.Clashes == r.ExistingMarkers + r.Creations.Count + r.NotMarked.Count;
        }

        private static string[] A(params string[] items)
        {
            return items;
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ H1: write only what differs

        private static void Steady(Action<string, bool?> check)
        {
            bool allEmpty = true;
            foreach (var days in new[] { 0, 40, 400 })
            {
                var r = MarkerPlan.Build(With(100, days));
                allEmpty &= Total(r) == 0 && r.Unchanged == 100 && r.PassedOver.Count == 0 && r.NotRetested.Count == 0 && !r.NeedsDialog
                            && r.ToCounts().Unchanged == 100 && Balanced(r);
            }
            check(P + "H1: when nothing differs the plan is empty, on the day, 40 days later and 400 days later (RF14)", allEmpty);

            var input = With(100);
            input.Rows[36].Severity = "Clearance";
            var one = MarkerPlan.Build(input);
            var c = one.Changes.FirstOrDefault();
            check(P + "H1: one differing field plans one write, to that marker only, and the rest stay unchanged",
                one.Changes.Count == 1 && c != null && c.MarkerUid == "m0037" && one.Unchanged == 99 && one.Creations.Count == 0 && one.Removals.Count == 0
                && c.Fields.SequenceEqual(A("Severity")) && c.After.Severity == "Clearance");
            check(P + "H1: Last checked becomes Today and Seq goes up by one only on the marker that is written",
                c != null && c.After.LastCheckedAt == Day0 && c.After.LastCheckedBy == "jdoe" && c.After.Seq == 6 && c.Before.Seq == 5
                && c.After.ChangedBy == c.Before.ChangedBy && c.After.ChangedAt == c.Before.ChangedAt && c.After.IssueNo == c.Before.IssueNo && c.After.ClashId == c.Before.ClashId);
            check(P + "H1: the input records are not modified by planning", input.Markers[36].Record.Severity == "Hard" && input.Markers[36].Record.Seq == 5);

            var mixed = NewInput();
            var a = Mk(1); var b = Mk(2); var d = Mk(3); var e = Mk(4, x => x.ResolvedAt = Day0.AddDays(-60));
            e.Record.Status = MarkerStatus.Resolved;
            mixed.Markers.AddRange(new[] { a, b, d, e });
            var rb = RowOf(b); rb.Verdict = PlanVerdict.NoLongerClashes;
            var rd = RowOf(d); rd.Severity = "Unverified"; rd.SetName = "MEP vs MEP";
            var re = RowOf(e); re.Verdict = PlanVerdict.NoLongerClashes;
            mixed.Rows.AddRange(new[] { RowOf(a), rb, rd, re });
            mixed.Rows.Add(NewRow(9));
            var plan = MarkerPlan.Build(mixed);
            var again = MarkerPlan.Build(Apply(mixed, plan));
            check(P + "H1: after the plan is applied, planning again writes nothing (idempotent)",
                Total(plan) == 3 && Total(again) == 0 && again.Unchanged == 5 && Balanced(again));

            check(P + "H1: two runs on the same input give the same plan (Today only comes from the options)", Fp(MarkerPlan.Build(mixed)) == Fp(plan));
        }

        // ------------------------------------------------------------------ absence is never evidence

        private static void Absence(Action<string, bool?> check)
        {
            var input = NewInput();
            for (int i = 1; i <= 100; i++)
            {
                int n = i;
                var m = Mk(n, x =>
                {
                    x.Status = (MarkerStatus)(n % 6);
                    if (x.Status == MarkerStatus.Resolved) x.ResolvedAt = Day0.AddDays(-60);
                });
                input.Markers.Add(m);
            }
            var r = MarkerPlan.Build(input);
            int oldResolved = input.Markers.Count(m => m.Record.Status == MarkerStatus.Resolved);
            check(P + "absence is not evidence: no rows over 100 markers gives 0 writes, 0 resolves, 0 removals (A2)",
                Total(r) == 0 && r.Unchanged == 100 && r.ToCounts().Resolved == 0 && r.ToCounts().Removed == 0 && r.PassedOver.Count == 0 && Balanced(r));
            check(P + "absence: unticked, the old Resolved markers are only offered for retention", oldResolved > 10 && r.RetentionOffers.Count == oldResolved && r.Removals.Count == 0);

            input.Options.RemoveRetention = true;
            var ticked = MarkerPlan.Build(input);
            check(P + "absence: with retention ticked and no rows, exactly the old Resolved markers are removed and nothing else",
                ticked.Removals.Count == oldResolved && ticked.Changes.Count == 0 && ticked.Creations.Count == 0 && ticked.Removals.All(x => x.Reason == "retention"));
        }

        // ------------------------------------------------------------------ the verdict table and field rules

        private static MarkerPlan.Result One(MarkerPlan.Existing m, Action<MarkerPlan.Row> tweak, Action<MarkerPlan.Input> setup = null)
        {
            var input = NewInput();
            input.Markers.Add(m);
            var row = RowOf(m);
            if (tweak != null) tweak(row);
            input.Rows.Add(row);
            if (setup != null) setup(input);
            return MarkerPlan.Build(input);
        }

        private static void Verdicts(Action<string, bool?> check)
        {
            bool resolvedAll = true;
            foreach (var s in new[] { MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored, MarkerStatus.Reopened })
            {
                var m = Mk(1, x => { x.Status = s; x.ChangedBy = "kdoe"; });
                var r = One(m, row => row.Verdict = PlanVerdict.NoLongerClashes);
                var c = r.Changes.FirstOrDefault();
                resolvedAll &= r.Changes.Count == 1 && c.Resolved && c.After.Status == MarkerStatus.Resolved && c.After.ResolvedBy == "jdoe" && c.After.ResolvedAt == Day0
                               && c.After.ChangedBy == "jdoe" && c.After.ChangedAt == Day0 && c.Fields.SequenceEqual(A("Status"))
                               && r.ToCounts().Resolved == 1 && r.ToCounts().Updated == 0;
            }
            check(P + "verdict: no longer clashing, under the marker's own rule, resolves every prior status (RF3)", resolvedAll);
            var done = One(Mk(1, x => { x.Status = MarkerStatus.Resolved; x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-2); }), row => row.Verdict = PlanVerdict.NoLongerClashes);
            check(P + "verdict: an already Resolved marker that still does not clash is left alone", Total(done) == 0 && done.Unchanged == 1);

            bool keptAll = true;
            foreach (var s in new[] { MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored, MarkerStatus.Reopened, MarkerStatus.Resolved })
            {
                var m = Mk(1, x => { x.Status = s; if (s == MarkerStatus.Resolved) x.ResolvedAt = Day0.AddDays(-2); });
                var r = One(m, row => { row.Verdict = PlanVerdict.NotRetested; row.NotRetestedReason = "link not loaded"; row.Severity = "Clearance"; });
                keptAll &= Total(r) == 0 && r.NotRetested.Count == 1 && r.NotRetested[0].Reason == "link not loaded" && r.NotRetested[0].MarkerUid == "m0001" && r.Unchanged == 0
                           && r.ToCounts().NotRetested.Count == 1 && r.ToCounts().NotRetested[0].Count == 1 && Balanced(r);
            }
            check(P + "verdict: not re-tested (link not loaded, exception, Unverified) never resolves or writes, and names its reason", keptAll);

            var clearance = Mk(1, x => x.ClearanceMm = 100);
            var rule1 = One(clearance, row => { row.Verdict = PlanVerdict.NoLongerClashes; row.RuleClearanceMm = 0; });
            var rule2 = One(Mk(1, x => { x.ClearanceMm = 100; }), row => { row.Verdict = PlanVerdict.NoLongerClashes; row.RuleInsulated = true; });
            var rule3 = One(Mk(1, x => { x.Insulated = true; }), row => { row.Verdict = PlanVerdict.NoLongerClashes; row.RuleInsulated = false; });
            check(P + "rule differs: a smaller clearance or no insulation can never resolve a marker found under another rule (A1)",
                Total(rule1) == 0 && rule1.NotRetested.Count == 1 && rule1.NotRetested[0].Reason == "rule differs"
                && Total(rule2) == 0 && rule2.NotRetested[0].Reason == "rule differs"
                && Total(rule3) == 0 && rule3.NotRetested[0].Reason == "rule differs");
            var rule4 = One(Mk(1, x => x.ClearanceMm = 100), row => { row.Severity = "Clearance"; row.RuleClearanceMm = 150; });
            check(P + "rule differs: a clashing verdict under another rule changes nothing either (A1)", Total(rule4) == 0 && rule4.NotRetested.Count == 1 && rule4.Unchanged == 0);
            var rule5 = One(Mk(1, x => { x.ClearanceMm = 100; x.Insulated = true; }), row => row.Verdict = PlanVerdict.NoLongerClashes);
            check(P + "rule differs: a row carrying the marker's own rule (clearance 100, insulated) does resolve it", rule5.Changes.Count == 1 && rule5.Changes[0].Resolved && rule5.NotRetested.Count == 0);
            var rule6 = One(Mk(1, x => x.ClearanceMm = 100), row => { row.Verdict = PlanVerdict.NoLongerClashes; row.RuleClearanceMm = 100.0000001; });
            check(P + "rule differs: a clearance equal to within a millionth of a millimetre is the same rule", rule6.Changes.Count == 1 && rule6.Changes[0].Resolved);

            var reopened = One(Mk(1, x => { x.Status = MarkerStatus.Resolved; x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-4); }), null);
            var rc = reopened.Changes.FirstOrDefault();
            check(P + "verdict: a clash that comes back reopens a Resolved marker and clears who/when resolved",
                reopened.Changes.Count == 1 && rc.Reopened && rc.After.Status == MarkerStatus.Reopened && rc.After.ResolvedBy == "" && rc.After.ResolvedAt == null
                && rc.After.ChangedBy == "jdoe" && rc.After.ChangedAt == Day0 && reopened.ToCounts().Updated == 1 && reopened.ToCounts().Resolved == 0);

            var sev = One(Mk(1), row => row.Severity = "Unverified");
            check(P + "field: a changed severity writes only the severity", sev.Changes.Count == 1 && sev.Changes[0].Fields.SequenceEqual(A("Severity")) && sev.Changes[0].After.Severity == "Unverified");
            var noSev = One(Mk(1), row => row.Severity = "");
            check(P + "field: an unknown severity (empty) never overwrites", Total(noSev) == 0);

            var near = One(Mk(1), row => row.X += 0.0005);
            var far = One(Mk(1), row => { row.X += 0.01; row.Z -= 0.5; });
            var noPoint = One(Mk(1), row => { row.HasPoint = false; row.X = 999; });
            check(P + "field: a point inside 0.001 ft is the same point; beyond it the marker moves and the move is counted (D16)",
                Total(near) == 0 && far.Changes.Count == 1 && far.Changes[0].Moved && far.Changes[0].Fields.SequenceEqual(A("Point"))
                && Math.Abs(far.Changes[0].After.Z - 2.5) < 1e-9 && far.ToCounts().Moved == 1 && Total(noPoint) == 0);

            var set1 = One(Mk(1), row => row.SetName = "MEP vs MEP");
            var set2 = One(Mk(1), row => row.SetName = "");
            var set3 = One(Mk(1), row => row.SetName = null);
            check(P + "field: a renamed set rewrites only the set name; an empty or unknown set never overwrites (RF17, 4b)",
                set1.Changes.Count == 1 && set1.Changes[0].Fields.SequenceEqual(A("SetName")) && set1.Changes[0].After.SetName == "MEP vs MEP" && Total(set2) == 0 && Total(set3) == 0);

            var also1 = One(Mk(1, x => x.AlsoSets = new List<string> { "A", "B" }), row => row.AlsoSets = new List<string> { "B", "A" });
            var also2 = One(Mk(1, x => x.AlsoSets = new List<string> { "A", "B" }), row => row.AlsoSets = new List<string> { "A" });
            var also3 = One(Mk(1, x => x.AlsoSets = new List<string> { "A", "B" }), row => row.AlsoSets = null);
            check(P + "field: also-caught-by compares as a set (order does not matter); null means not computed",
                Total(also1) == 0 && also2.Changes.Count == 1 && also2.Changes[0].Fields.SequenceEqual(A("AlsoSets")) && Total(also3) == 0);

            var g1 = One(Mk(1, x => x.GroupLabel = "JD-0001 (+2)"), row => row.GroupLabel = null);
            var g2 = One(Mk(1, x => x.GroupLabel = "JD-0001 (+2)"), row => row.GroupLabel = "");
            var g3 = One(Mk(1, x => x.GroupLabel = "JD-0001 (+2)"), row => row.GroupLabel = "JD-0001 (+3)");
            var g4 = One(Mk(1, x => x.GroupLabel = "JD-0001 (+2)"), row => row.GroupLabel = "JD-0001 (+2)");
            check(P + "field: the group label is written only when it differs; null is not computed; empty means the group dissolved (RF20)",
                Total(g1) == 0 && Total(g4) == 0 && g2.Changes.Count == 1 && g2.Changes[0].After.GroupLabel == "" && g3.Changes.Count == 1 && g3.Changes[0].After.GroupLabel == "JD-0001 (+3)");

            var rekey = One(Mk(1), row => { row.HostId = 999; row.Key = "999|0|o1"; });
            var rc2 = rekey.Changes.FirstOrDefault();
            check(P + "re-key: new ElementIds with the same UniqueIds update the ids and key only; the clash id and number stay (RF9)",
                rekey.Changes.Count == 1 && rc2.Rekeyed && rc2.Fields.SequenceEqual(A("HostId", "Key")) && rc2.After.HostId == 999 && rc2.After.Key == "999|0|o1"
                && rc2.After.ClashId == rc2.Before.ClashId && rc2.After.IssueNo == rc2.Before.IssueNo);
            var unknownIds = One(Mk(1), row => { row.HostId = 0; row.OtherId = 0; row.Key = ""; });
            check(P + "re-key: a row with no ids (unknown) never rewrites them", Total(unknownIds) == 0);

            var swap = NewInput();
            var m1 = Mk(1); var m2 = Mk(2);
            swap.Markers.AddRange(new[] { m1, m2 });
            var s1 = RowOf(m1); var s2 = RowOf(m2);
            s1.HostId = m2.Record.HostId; s1.OtherId = m2.Record.OtherId; s2.HostId = m1.Record.HostId; s2.OtherId = m1.Record.OtherId;
            swap.Rows.AddRange(new[] { s2, s1 });
            var sw = MarkerPlan.Build(swap);
            check(P + "re-key: ids swapped between two pairs still match by UniqueId, never by ElementId (RF9)",
                sw.Changes.Count == 2 && sw.Changes.Single(c => c.MarkerUid == "m0001").After.HostId == 102 && sw.Changes.Single(c => c.MarkerUid == "m0002").After.HostId == 101 && sw.Creations.Count == 0);

            var hash = One(Mk(1), row => row.ShownHash = "other");
            check(P + "field: when the parameters no longer show what the row would write, the marker is rewritten from the record",
                hash.Changes.Count == 1 && hash.Changes[0].Fields.SequenceEqual(A("Parameters")) && hash.Changes[0].ShownHash == "other");

            var off = MarkerPlan.Build(Wrap(Mk(1), m => { m.OffPlace = true; }));
            var ws = MarkerPlan.Build(Wrap(Mk(1), m => { m.WrongWorkset = true; }));
            check(P + "field: a marker moved away is put back (counted as moved); one off its workset is moved back",
                off.Changes.Count == 1 && off.Changes[0].PutBack && off.Changes[0].Fields.SequenceEqual(A("Location")) && off.ToCounts().Moved == 1
                && ws.Changes.Count == 1 && ws.Changes[0].WorksetFix && ws.Changes[0].Fields.SequenceEqual(A("Workset")));
        }

        private static MarkerPlan.Input Wrap(MarkerPlan.Existing m, Action<MarkerPlan.Existing> tweak)
        {
            tweak(m);
            var input = NewInput();
            input.Markers.Add(m);
            input.Rows.Add(RowOf(m));
            return input;
        }

        // ------------------------------------------------------------------ creation

        private static void Creation(Action<string, bool?> check)
        {
            var input = NewInput();
            input.Rows.Add(NewRow(7, "Clearance", r => { r.RuleClearanceMm = 50; r.RuleInsulated = true; r.AlsoSets = new List<string> { "T" }; r.GroupLabel = "G"; r.SetName = "S"; r.ShownHash = "x"; r.X = 1.5; r.Y = 2.5; r.Z = 3.5; }));
            var one = MarkerPlan.Build(input);
            var rec = one.Creations.Count == 1 ? one.Creations[0].Record : new MarkerRecord();
            check(P + "create: a pair with no marker gets one, with the whole record of spec 5.2",
                one.Creations.Count == 1 && rec.Key == "h7|0|o7" && rec.HostUid == "h7" && rec.OtherUid == "o7" && rec.LinkUid == null | rec.LinkUid == ""
                && rec.HostId == 107 && rec.OtherId == 207 && rec.ClashId == ClashIdOf.Of("h7", "", "o7") && rec.IssueNo == "JD-0001" && rec.Status == MarkerStatus.New
                && rec.ReportedBy == "jdoe" && rec.ReportedAt == Day0 && rec.ChangedBy == "" && rec.Severity == "Clearance" && rec.Insulated && rec.ClearanceMm == 50
                && rec.SetName == "S" && rec.AlsoSets.SequenceEqual(A("T")) && rec.GroupLabel == "G" && rec.X == 1.5 && rec.Y == 2.5 && rec.Z == 3.5
                && rec.LastCheckedAt == Day0 && rec.LastCheckedBy == "jdoe" && rec.Seq == 1 && one.Creations[0].ShownHash == "x"
                && one.Creations[0].Triple == ClashIdOf.Triple("h7", "", "o7"));
            check(P + "create: the counts say one created and balance", one.ToCounts().Created == 1 && one.ToCounts().Clashes == 1 && Balanced(one));

            var seeded = NewInput();
            seeded.Rows.Add(NewRow(1, "Hard", r => r.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "n", AssignedTo = "asmith", By = "kdoe", At = Day0.AddDays(-2) }));
            seeded.Rows.Add(NewRow(2, "Hard", r => r.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Pending, Note = "x", By = "kdoe" }));
            var sr = MarkerPlan.Build(seeded);
            var s1 = sr.Creations.Count == 2 ? sr.Creations[0].Record : new MarkerRecord();
            var s2 = sr.Creations.Count == 2 ? sr.Creations[1].Record : new MarkerRecord();
            check(P + "create: a pair the ledger has a decision on starts with that decision, its author and its time",
                s1.Status == MarkerStatus.Ignored && s1.Note == "n" && s1.AssignedTo == "asmith" && s1.ChangedBy == "kdoe" && s1.ChangedAt == Day0.AddDays(-2)
                && s2.Status == MarkerStatus.Pending && s2.ChangedAt == Day0);

            var decided = NewInput();
            decided.Rows.Add(NewRow(1, "", r => { r.Live = false; r.Verdict = PlanVerdict.NotRetested; r.SetName = ""; r.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Coordinated, By = "kdoe" }; }));
            decided.Rows.Add(NewRow(2, "", r => { r.Live = false; r.Verdict = PlanVerdict.NotRetested; r.SetName = ""; }));
            var dr = MarkerPlan.Build(decided);
            check(P + "create: a decided pair not live on this seat gets a marker in (no set); with no decision it gets none (D14)",
                dr.Creations.Count == 1 && dr.Creations[0].Record.SetName == MarkerPlan.NoSetName && dr.Creations[0].Record.Status == MarkerStatus.Coordinated && dr.NotMarked.Count == 0);
            var liveNot = With(1);
            liveNot.Rows[0].Live = false; liveNot.Rows[0].Verdict = PlanVerdict.NotRetested; liveNot.Rows[0].Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored };
            var ln = MarkerPlan.Build(liveNot);
            check(P + "create: a decided pair that already has a marker is left to the marker (D14)", Total(ln) == 0 && ln.Unchanged == 1);

            var order = NewInput();
            order.Rows.Add(NewRow(5, "Unverified")); order.Rows.Add(NewRow(2, "Hard")); order.Rows.Add(NewRow(4, "Clearance"));
            order.Rows.Add(NewRow(1, "Hard")); order.Rows.Add(NewRow(3, "Clearance"));
            var orr = MarkerPlan.Build(order);
            check(P + "create: numbers go out by severity then key, JD-0001 upward with no gap, and the input counter is not touched (D17)",
                orr.Creations.Select(c => c.Record.HostUid + "=" + c.Record.IssueNo).SequenceEqual(A("h1=JD-0001", "h2=JD-0002", "h3=JD-0003", "h4=JD-0004", "h5=JD-0005"))
                && order.Counter.Next == 1 && orr.CounterAfter != null && orr.CounterAfter.Next == 6 && orr.CounterAfter.Prefix == "JD");
            var shuffled = NewInput();
            foreach (var row in order.Rows.AsEnumerable().Reverse()) shuffled.Rows.Add(row);
            check(P + "create: the same rows in another order give the same numbers", Fp(MarkerPlan.Build(shuffled)) == Fp(orr));

            bool capOk = true;
            foreach (var rows in new[] { 1999, 2000, 2001 })
            {
                var big = NewInput();
                for (int i = 1; i <= rows; i++) big.Rows.Add(NewRow(i));
                var br = MarkerPlan.Build(big);
                int expectedCreated = Math.Min(rows, 2000);
                capOk &= br.Creations.Count == expectedCreated && br.NotMarked.Count == rows - expectedCreated && br.OverCap == (rows > 2000)
                         && br.ToCounts().Created == expectedCreated && Balanced(br);
                if (rows == 2001)
                    capOk &= br.NotMarked.Count == 1 && br.NotMarked[0].Reason == "over the cap of 2,000" && br.ToCounts().NotMarked.Count == 1 && br.ToCounts().NotMarked[0].Count == 1
                             && br.ToCounts().NotMarked[0].Text == "over the cap of 2,000" && br.NeedsDialog;
            }
            check(P + "cap: 1,999 and 2,000 are all created; the 2,001st is counted and named, not dropped (A4)", capOk);
            var small = NewInput();
            small.Options.Cap = 3;
            for (int i = 1; i <= 5; i++) small.Rows.Add(NewRow(i, i <= 2 ? "Hard" : "Clearance"));
            var smr = MarkerPlan.Build(small);
            check(P + "cap: the ones left out are the last by severity and key, named with the cap in force",
                smr.Creations.Count == 3 && smr.NotMarked.Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(A("h4|0|o4", "h5|0|o5")) && smr.NotMarked[0].Reason == "over the cap of 3");

            var bad = NewInput();
            bad.Rows.Add(NewRow(1, "Hard", r => r.HostUid = ""));
            bad.Rows.Add(NewRow(2, "Hard", r => r.HasPoint = false));
            var br2 = MarkerPlan.Build(bad);
            check(P + "create: a pair with no UniqueId or no position is named as not marked, never made up (C4)",
                br2.Creations.Count == 0 && br2.NotMarked.Count == 2 && br2.NotMarked.Any(n => n.Reason == "no UniqueId") && br2.NotMarked.Any(n => n.Reason == "no position") && Balanced(br2));

            var remember = NewInput();
            remember.Rows.Add(NewRow(1, "Hard", r => r.RememberedIssueNo = "JD-0042"));
            var rr = MarkerPlan.Build(remember);
            check(P + "create: a number the ledger remembers is reused when free, and the counter is not spent",
                rr.Creations.Count == 1 && rr.Creations[0].Record.IssueNo == "JD-0042" && !rr.Creations[0].IssueNoReplaced && rr.CounterAfter != null && rr.CounterAfter.Next == 1);
            var taken = NewInput();
            taken.Markers.Add(Mk(5, 5, x => x.IssueNo = "JD-0042"));
            taken.Markers.Add(Mk(6, 6, x => x.MergedIssueNos = new List<string> { "JD-0043" }));
            taken.Rows.Add(NewRow(1, "Hard", r => r.RememberedIssueNo = "JD-0042"));
            taken.Rows.Add(NewRow(2, "Hard", r => r.RememberedIssueNo = "JD-0043"));
            taken.Rows.Add(NewRow(3, "Hard", r => r.RememberedIssueNo = "garbage"));
            var tr = MarkerPlan.Build(taken);
            check(P + "create: a remembered number already held (as a number or a merged one) or unreadable is replaced by a new one, and said so",
                tr.Creations.Count == 3 && tr.Creations[0].Record.IssueNo == "JD-0001" && tr.Creations[0].IssueNoReplaced && tr.Creations[1].Record.IssueNo == "JD-0002" && tr.Creations[1].IssueNoReplaced
                && tr.Creations[2].Record.IssueNo == "JD-0003" && !tr.Creations[2].IssueNoReplaced);

            var none = NewInput();
            none.Rows.Add(NewRow(1, "Hard", r => r.Verdict = PlanVerdict.NoLongerClashes));
            none.Rows.Add(NewRow(2, "Hard", r => { r.Verdict = PlanVerdict.NotRetested; r.NotRetestedReason = "link not loaded"; }));
            var nr = MarkerPlan.Build(none);
            check(P + "create: a pair that no longer clashes, or could not be re-tested, with no marker gets none and is not counted", Total(nr) == 0 && nr.NotMarked.Count == 0 && nr.ToCounts().Clashes == 0);

            var coll = NewInput();
            coll.Options.Hash = s => "deadbeef";
            coll.Markers.Add(Mk(1, 1, x => x.ClashId = "LC-deadbeef"));
            coll.Rows.Add(NewRow(2));
            var cr = MarkerPlan.Build(coll);
            check(P + "create: a new pair whose clash id collides with an existing one takes -2 and the existing marker keeps its id (RF9, M3)",
                cr.Creations.Count == 1 && cr.Creations[0].Record.ClashId == "LC-deadbeef-2" && cr.Changes.Count == 0);
            var coll2 = NewInput();
            coll2.Markers.Add(Mk(1, 1, x => x.ClashId = "LC-deadbeef"));
            coll2.Markers.Add(Mk(2, 2, x => x.ClashId = "LC-deadbeef"));
            foreach (var m in coll2.Markers) coll2.Rows.Add(RowOf(m));
            var c2 = MarkerPlan.Build(coll2);
            check(P + "collision: two existing markers on one hash: the higher UniqueId is rewritten to -2",
                c2.Changes.Count == 1 && c2.Changes[0].MarkerUid == "m0002" && c2.Changes[0].After.ClashId == "LC-deadbeef-2" && c2.Changes[0].Fields.SequenceEqual(A("ClashId")));
        }

        // ------------------------------------------------------------------ pass over

        private static void Access(Action<string, bool?> check)
        {
            var reasons = new Dictionary<MarkerAccess, string>
            {
                { MarkerAccess.HeldByOther, "held by kdoe" }, { MarkerAccess.ChangedInCentral, "changed in central" }, { MarkerAccess.InsideGroup, "inside a group" },
                { MarkerAccess.NewerFormat, "written by a newer LiveClash" }, { MarkerAccess.Unreadable, "unreadable record" }
            };
            foreach (var kv in reasons.OrderBy(k => (int)k.Key))
            {
                var access = kv.Key;
                var m = Mk(1, null, access);
                if (access == MarkerAccess.NewerFormat || access == MarkerAccess.Unreadable) m.Record = null;
                var input = NewInput();
                input.Markers.Add(m);
                if (m.Record != null) { var row = RowOf(m); row.Severity = "Clearance"; input.Rows.Add(row); }
                var r = MarkerPlan.Build(input);
                check(P + "pass over: " + access + " is named (\"" + kv.Value + "\"), never written, and counted",
                    Total(r) == 0 && r.PassedOver.Count == 1 && r.PassedOver[0].Reason == kv.Value && r.PassedOver[0].MarkerUid == "m0001" && r.Unchanged == 0
                    && r.ToCounts().PassedOver.Count == 1 && r.ToCounts().PassedOver[0].Text == kv.Value && Balanced(r));
            }

            var held = With(2);
            held.Markers[0].Access = MarkerAccess.HeldByOther; held.Markers[0].HeldBy = "kdoe";
            var hr = MarkerPlan.Build(held);
            check(P + "pass over: a held marker with nothing to write is Unchanged and not named (nothing is borrowed)", Total(hr) == 0 && hr.PassedOver.Count == 0 && hr.Unchanged == 2 && !hr.NeedsDialog);

            var res = With(1);
            res.Markers[0].Access = MarkerAccess.HeldByOther; res.Markers[0].HeldBy = "kdoe";
            res.Rows[0].Verdict = PlanVerdict.NoLongerClashes;
            var rr = MarkerPlan.Build(res);
            check(P + "pass over: a clash that stopped clashing is never marked Resolved on a held marker (E3)", Total(rr) == 0 && rr.PassedOver.Count == 1 && rr.ToCounts().Resolved == 0);

            var anon = With(1);
            anon.Markers[0].Access = MarkerAccess.HeldByOther; anon.Markers[0].HeldBy = null; anon.Rows[0].Severity = "Clearance";
            check(P + "pass over: a holder with no name is \"held by another user\"", MarkerPlan.Build(anon).PassedOver.Select(p => p.Reason).SequenceEqual(A("held by another user")));

            var newer = NewInput();
            newer.Markers.Add(Mk(1, null, MarkerAccess.NewerFormat));
            newer.Markers[0].Record = null;
            newer.Options.RemoveRetention = true;
            var nr = MarkerPlan.Build(newer);
            check(P + "pass over: a newer-format marker is passed over even with no rows and with retention ticked, and offered nothing", nr.PassedOver.Count == 1 && Total(nr) == 0 && nr.RetentionOffers.Count == 0);
        }

        // ------------------------------------------------------------------ duplicates

        private static MarkerPlan.Input Dup(Action<MarkerRecord> keptTweak, Action<MarkerRecord> otherTweak, MarkerAccess keptAccess = MarkerAccess.Free, MarkerAccess otherAccess = MarkerAccess.Free)
        {
            var input = NewInput();
            var kept = Mk(1, 1, keptTweak, keptAccess);
            var other = Mk(2, 1, x => { x.IssueNo = "KD-0003"; x.MergedIssueNos = new List<string> { "KD-0001" }; if (otherTweak != null) otherTweak(x); }, otherAccess);
            input.Markers.Add(other); input.Markers.Add(kept);
            input.Rows.Add(RowOf(kept));
            return input;
        }

        private static void Duplicates(Action<string, bool?> check)
        {
            var r = MarkerPlan.Build(Dup(null, null));
            var c = r.Changes.FirstOrDefault();
            check(P + "merge: two markers of one pair: the lower UniqueId is kept, the other removed, its numbers kept in MergedIssueNos (6.6)",
                r.Removals.Count == 1 && r.Removals[0].MarkerUid == "m0002" && r.Removals[0].KeptUid == "m0001" && r.Removals[0].Reason == "merged duplicate" && r.Merges == 1
                && c != null && c.MarkerUid == "m0001" && c.Fields.SequenceEqual(A("MergedIssueNos")) && c.After.MergedIssueNos.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(A("KD-0001", "KD-0003"))
                && r.ToCounts().Merged == 1 && r.ToCounts().Removed == 1 && r.ToCounts().Clashes == 2 && Balanced(r));

            var moved = MarkerPlan.Build(Dup(null, x => { x.Status = MarkerStatus.Ignored; x.Note = "n"; x.AssignedTo = "asmith"; x.ChangedBy = "kdoe"; x.ChangedAt = Day0.AddDays(-1); }));
            var mc = moved.Changes.FirstOrDefault();
            check(P + "merge: a decision on only one of the two goes on the kept marker, and nothing is lost",
                mc != null && mc.After.Status == MarkerStatus.Ignored && mc.After.Note == "n" && mc.After.AssignedTo == "asmith" && mc.After.ChangedBy == "kdoe" && mc.After.ChangedAt == Day0.AddDays(-1)
                && moved.LostDecisions.Count == 0 && moved.ToCounts().LostDecisions == 0);

            var lost = MarkerPlan.Build(Dup(x => x.Status = MarkerStatus.Pending, x => { x.Status = MarkerStatus.Ignored; x.Note = "no"; x.ChangedBy = "kdoe"; }));
            var lc = lost.Changes.FirstOrDefault();
            check(P + "merge: two different decisions: the kept one stays, the other is listed in the result AND in the counts (A6)",
                lost.Removals.Count == 1 && lc.After.Status == MarkerStatus.Pending && lost.LostDecisions.Count == 1 && lost.LostDecisions[0].RemovedUid == "m0002" && lost.LostDecisions[0].KeptUid == "m0001"
                && lost.LostDecisions[0].Status == MarkerStatus.Ignored && lost.LostDecisions[0].Note == "no" && lost.LostDecisions[0].By == "kdoe" && lost.LostDecisions[0].RemovedIssueNo == "KD-0003"
                && lost.ToCounts().LostDecisions == 1);

            var same = MarkerPlan.Build(Dup(x => { x.Status = MarkerStatus.Ignored; x.Note = "n"; }, x => { x.Status = MarkerStatus.Ignored; x.Note = "n"; }));
            check(P + "merge: the same decision on both loses nothing", same.LostDecisions.Count == 0 && same.Removals.Count == 1);

            var three = NewInput();
            var a = Mk(1, 1); var b = Mk(2, 1, x => x.IssueNo = "KD-0002"); var d = Mk(3, 1, x => x.IssueNo = "KD-0003");
            three.Rows.Add(RowOf(a));
            bool sameAll = true; string first = null; bool kept1 = true;
            foreach (var perm in MarkerIdentityChecks.Permutations(new[] { a, b, d }))
            {
                var inp = NewInput();
                inp.Markers.AddRange(perm);
                inp.Rows.Add(RowOf(a));
                var pr = MarkerPlan.Build(inp);
                kept1 &= pr.Removals.Count == 2 && pr.Removals.All(x => x.KeptUid == "m0001") && pr.Merges == 2;
                var f = Fp(pr);
                if (first == null) first = f; else sameAll &= f == first;
            }
            check(P + "merge: three markers of one pair give the same plan in all 6 input orders; the lowest UniqueId is always kept (RF5)", sameAll && kept1);

            var clockA = Dup(null, null);
            var clockB = Dup(null, null);
            clockA.Markers[0].Record.ReportedAt = Day0.AddYears(-3); clockA.Markers[1].Record.ReportedAt = Day0.AddYears(3);
            clockB.Markers[0].Record.ReportedAt = Day0.AddYears(3); clockB.Markers[1].Record.ReportedAt = Day0.AddYears(-3);
            check(P + "merge: no clock decides which one is kept",
                MarkerPlan.Build(clockA).Removals.Select(x => x.MarkerUid).SequenceEqual(A("m0002")) && MarkerPlan.Build(clockB).Removals.Select(x => x.MarkerUid).SequenceEqual(A("m0002")));

            var sys1 = MarkerPlan.Build(Dup(null, x => { x.Status = MarkerStatus.Resolved; x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-2); }));
            var sys2 = MarkerPlan.Build(Dup(x => x.Status = MarkerStatus.Ignored, x => { x.Status = MarkerStatus.Reopened; x.ChangedBy = "kdoe"; }));
            var sys3 = MarkerPlan.Build(Dup(x => { x.Status = MarkerStatus.Resolved; x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-2); }, x => { x.Status = MarkerStatus.Ignored; x.Note = "n"; x.ChangedBy = "kdoe"; }));
            var sc1 = sys1.Changes.FirstOrDefault(); var sc2 = sys2.Changes.FirstOrDefault(); var sc3 = sys3.Changes.FirstOrDefault();
            check(P + "merge: a Resolved or Reopened duplicate is a re-test result, not a decision: it is not copied and loses nothing (D22)",
                sc1 != null && sc1.After.Status == MarkerStatus.New && sc1.After.ResolvedBy == "" && sys1.LostDecisions.Count == 0 && sys1.Removals.Count == 1
                && sc2 != null && sc2.After.Status == MarkerStatus.Ignored && sys2.LostDecisions.Count == 0 && sys2.Removals.Count == 1);
            check(P + "merge: a human decision on the duplicate replaces a Resolved kept status, and clears who/when resolved (D22)",
                sc3 != null && sc3.After.Status == MarkerStatus.Ignored && sc3.After.Note == "n" && sc3.After.ResolvedBy == "" && sc3.After.ResolvedAt == null && sys3.LostDecisions.Count == 0);

            var tie = NewInput();
            var tm = Mk(1);
            tie.Markers.Add(tm);
            var t1 = RowOf(tm); t1.SetName = "A"; var t2 = RowOf(tm); t2.SetName = "B";
            var tieB = NewInput();
            tieB.Markers.Add(Mk(1));
            tie.Rows.AddRange(new[] { t1, t2 }); tieB.Rows.AddRange(new[] { t2, t1 });
            check(P + "rows: two rows for one pair that tie on key and severity still give one answer, whatever their order (A7)", Fp(MarkerPlan.Build(tie)) == Fp(MarkerPlan.Build(tieB)));

            var heldDup = MarkerPlan.Build(Dup(null, null, MarkerAccess.Free, MarkerAccess.HeldByOther));
            var heldKept = MarkerPlan.Build(Dup(null, null, MarkerAccess.HeldByOther, MarkerAccess.Free));
            check(P + "merge: a held duplicate is not removed and nothing is absorbed; if the kept one is held the duplicate waits too (D19)",
                heldDup.Removals.Count == 0 && heldDup.Merges == 0 && heldDup.Changes.Count == 0 && heldDup.PassedOver.Count == 1 && heldDup.PassedOver[0].Reason == "duplicate: held by kdoe"
                && heldKept.Removals.Count == 0 && heldKept.PassedOver.Count == 1 && heldKept.PassedOver[0].MarkerUid == "m0002" && heldKept.PassedOver[0].Reason == "duplicate: kept marker held by kdoe" && Balanced(heldDup) && Balanced(heldKept));
        }

        // ------------------------------------------------------------------ pending to model

        private static MarkerPlan.Pending Pend(int tripleNo, long seq, Action<MarkerPlan.Pending> tweak = null)
        {
            var p = new MarkerPlan.Pending { Triple = Triple(tripleNo), SeqAtPending = seq, Status = MarkerStatus.Ignored, Note = "n", By = "jdoe" };
            if (tweak != null) tweak(p);
            return p;
        }

        private static void PendingChecks(Action<string, bool?> check)
        {
            var inp = With(1);
            inp.Pending.Add(Pend(1, 5, p => p.AssignedTo = "asmith"));
            var ok = MarkerPlan.Build(inp);
            var c = ok.Changes.FirstOrDefault();
            check(P + "pending: the marker is free and its Seq is unchanged: the held triage is written, by its author, and closed (6.7)",
                c != null && c.After.Status == MarkerStatus.Ignored && c.After.Note == "n" && c.After.AssignedTo == "asmith" && c.After.ChangedBy == "jdoe" && c.After.ChangedAt == Day0
                && c.Fields.SequenceEqual(A("AssignedTo", "Note", "Status")) && ok.Pending.Count == 1 && ok.Pending[0].Kind == PendingKind.Applied
                && ok.ToCounts().PendingApplied == 1 && ok.ToCounts().Updated == 1 && Balanced(ok));

            var onlyAssign = With(1);
            onlyAssign.Pending.Add(new MarkerPlan.Pending { Triple = Triple(1), SeqAtPending = 5, AssignedTo = "asmith", By = "kdoe" });
            var oa = MarkerPlan.Build(onlyAssign).Changes.FirstOrDefault();
            check(P + "pending: only the fields the triage set are written (an assignment leaves status and note)", oa != null && oa.After.Status == MarkerStatus.New && oa.After.Note == "" && oa.After.AssignedTo == "asmith" && oa.After.ChangedBy == "kdoe");

            var changed = With(1);
            changed.Markers[0].Record.Seq = 6;
            changed.Pending.Add(Pend(1, 5));
            var dr = MarkerPlan.Build(changed);
            check(P + "pending: the marker was written by someone else since (Seq changed): the model wins, the triage is dropped and said so (6.7)",
                dr.Changes.Count == 0 && dr.Pending.Count == 1 && dr.Pending[0].Kind == PendingKind.Dropped && dr.Pending[0].Message.Contains("model wins") && dr.ToCounts().PendingApplied == 0);

            var held = With(1);
            held.Markers[0].Access = MarkerAccess.HeldByOther; held.Markers[0].HeldBy = "kdoe";
            held.Pending.Add(Pend(1, 5));
            var hr = MarkerPlan.Build(held);
            check(P + "pending: a held marker never gets the triage; it stays pending (not dropped), says why, and the marker is passed over (A5)",
                hr.Changes.Count == 0 && hr.Pending.Count == 1 && hr.Pending[0].Kind == PendingKind.Kept && hr.Pending[0].Message.Contains("held by kdoe") && hr.PassedOver.Count == 1 && hr.PassedOver[0].Reason == "held by kdoe");

            bool keptAll = true;
            foreach (var acc in new[] { MarkerAccess.ChangedInCentral, MarkerAccess.InsideGroup })
            {
                var w = With(1);
                w.Markers[0].Access = acc;
                w.Pending.Add(Pend(1, 5));
                var wr = MarkerPlan.Build(w);
                keptAll &= wr.Pending.Count == 1 && wr.Pending[0].Kind == PendingKind.Kept && wr.Changes.Count == 0 && wr.PassedOver.Count == 1;
            }
            check(P + "pending: changed in central and inside a group also keep it pending", keptAll);

            var gone = NewInput();
            gone.Pending.Add(Pend(9, 5));
            var gr = MarkerPlan.Build(gone);
            check(P + "pending: a triage for a marker that is no longer there is dropped and said so",
                gr.Pending.Count == 1 && gr.Pending[0].Kind == PendingKind.Dropped && gr.Pending[0].Message.Contains("no longer") && gr.Changes.Count == 0);

            var withResolve = With(1);
            withResolve.Rows[0].Verdict = PlanVerdict.NoLongerClashes;
            withResolve.Pending.Add(Pend(1, 5));
            var wr2 = MarkerPlan.Build(withResolve);
            check(P + "pending: with a no-longer-clashing verdict in the same Refresh, Resolved wins and the note still goes on",
                wr2.Changes.Count == 1 && wr2.Changes[0].After.Status == MarkerStatus.Resolved && wr2.Changes[0].After.Note == "n" && wr2.Changes[0].Resolved && wr2.Pending[0].Kind == PendingKind.Applied);

            var notRetested = With(1);
            notRetested.Rows[0].Verdict = PlanVerdict.NotRetested; notRetested.Rows[0].NotRetestedReason = "link not loaded";
            notRetested.Pending.Add(Pend(1, 5));
            var nr = MarkerPlan.Build(notRetested);
            check(P + "pending: a marker that could not be re-tested still gets its held triage", nr.Changes.Count == 1 && nr.Changes[0].After.Status == MarkerStatus.Ignored && nr.Pending[0].Kind == PendingKind.Applied && nr.NotRetested.Count == 0);
        }

        // ------------------------------------------------------------------ retention

        private static MarkerPlan.Input OldResolved(int count, int ageDays, Action<MarkerPlan.Existing> tweak = null, bool ticked = true)
        {
            var input = NewInput();
            input.Options.RemoveRetention = ticked;
            for (int i = 1; i <= count; i++)
            {
                var m = Mk(i, x => { x.Status = MarkerStatus.Resolved; x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-ageDays); });
                if (tweak != null) tweak(m);
                input.Markers.Add(m);
                if (m.Record != null) { var row = RowOf(m); row.Verdict = PlanVerdict.NoLongerClashes; input.Rows.Add(row); }
            }
            return input;
        }

        private static void Retention(Action<string, bool?> check)
        {
            var offered = MarkerPlan.Build(OldResolved(4, 40, null, false));
            check(P + "retention: unticked, old Resolved markers are offered and none is removed (E9)", offered.RetentionOffers.Count == 4 && offered.Removals.Count == 0 && offered.NeedsDialog && offered.Unchanged == 4);
            var ticked = MarkerPlan.Build(OldResolved(4, 40));
            check(P + "retention: ticked, they are removed, counted, and the counts balance",
                ticked.Removals.Count == 4 && ticked.Removals.All(x => x.Reason == "retention") && ticked.ToCounts().Removed == 4 && ticked.Unchanged == 0 && Balanced(ticked));

            bool boundary = true;
            foreach (var kv in new Dictionary<int, bool> { { 29, false }, { 30, false }, { 31, true } })
            {
                var r = MarkerPlan.Build(OldResolved(1, kv.Key));
                boundary &= (r.Removals.Count == 1) == kv.Value && (r.RetentionOffers.Count == 1) == kv.Value;
            }
            var justOver = OldResolved(1, 30); justOver.Markers[0].Record.ResolvedAt = Day0.AddDays(-30).AddMinutes(-1);
            boundary &= MarkerPlan.Build(justOver).Removals.Count == 1;
            var noDate = OldResolved(1, 90); noDate.Markers[0].Record.ResolvedAt = null;
            boundary &= MarkerPlan.Build(noDate).Removals.Count == 0;
            check(P + "retention: 29 and exactly 30 days stay, 31 days (and 30 days and a minute) go, no date stays (D18)", boundary);

            bool notOthers = true;
            foreach (var s in new[] { MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored, MarkerStatus.Reopened })
            {
                var input = OldResolved(1, 90);
                input.Markers[0].Record.Status = s;
                input.Rows[0].Verdict = PlanVerdict.NotRetested;
                var r = MarkerPlan.Build(input);
                notOthers &= r.Removals.Count == 0 && r.RetentionOffers.Count == 0;
            }
            check(P + "retention never removes Ignored, Coordinated, Pending, New or Reopened markers, however old (A3)", notOthers);

            bool notPassed = true;
            foreach (var acc in new[] { MarkerAccess.HeldByOther, MarkerAccess.ChangedInCentral, MarkerAccess.InsideGroup, MarkerAccess.NewerFormat, MarkerAccess.Unreadable })
            {
                var input = OldResolved(1, 90, m => { m.Access = acc; if (acc == MarkerAccess.NewerFormat || acc == MarkerAccess.Unreadable) m.Record = null; });
                var r = MarkerPlan.Build(input);
                notPassed &= r.Removals.Count == 0 && r.RetentionOffers.Count == 0;
            }
            check(P + "retention never touches a marker that is held, changed in central, in a group, in a newer format or unreadable (A3)", notPassed);

            var pending = OldResolved(1, 90);
            pending.Pending.Add(Pend(1, 5));
            var pr = MarkerPlan.Build(pending);
            check(P + "retention never removes a marker with an open pending triage (A3)", pr.Removals.Count == 0 && pr.RetentionOffers.Count == 0);

            var back = OldResolved(1, 90);
            back.Rows[0].Verdict = PlanVerdict.Clashing;
            var bk = MarkerPlan.Build(back);
            check(P + "retention never removes a marker whose pair clashes again; it is reopened instead", bk.Removals.Count == 0 && bk.Changes.Count == 1 && bk.Changes[0].Reopened);

            var dup = OldResolved(1, 90);
            var copy = Mk(2, 1, x => { x.Status = MarkerStatus.Resolved; x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-90); });
            dup.Markers.Add(copy);
            var dr = MarkerPlan.Build(dup);
            check(P + "retention: a merged duplicate is removed once, as a merge, and the kept marker is removed as retention", dr.Removals.Count == 2 && dr.Removals.Count(x => x.Reason == "merged duplicate") == 1 && dr.Removals.Count(x => x.Reason == "retention") == 1 && Balanced(dr));
        }

        // ------------------------------------------------------------------ dialog, warnings, per-set counts, nulls

        private static void Limits(Action<string, bool?> check)
        {
            var first = NewInput();
            first.Rows.Add(NewRow(1));
            var many = With(3);
            for (int i = 0; i < 50; i++) many.Rows.Add(NewRow(100 + i));
            var tooMany = With(3);
            for (int i = 0; i < 51; i++) tooMany.Rows.Add(NewRow(100 + i));
            var passed = With(1);
            passed.Markers[0].Access = MarkerAccess.HeldByOther; passed.Rows[0].Severity = "Clearance";
            var setup = With(2); setup.Options.SetupNeeded = true;
            var steady = With(2);
            check(P + "dialog: the first Create asks; 50 new markers on an existing set do not, 51 do; a pass-over, a retention offer and a set-up do; a steady refresh does not",
                MarkerPlan.Build(first).NeedsDialog && !MarkerPlan.Build(many).NeedsDialog && MarkerPlan.Build(tooMany).NeedsDialog && MarkerPlan.Build(passed).NeedsDialog
                && MarkerPlan.Build(OldResolved(1, 90, null, false)).NeedsDialog && MarkerPlan.Build(setup).NeedsDialog && !MarkerPlan.Build(steady).NeedsDialog);

            bool warnOk = true;
            foreach (var add in new[] { 999, 1000, 1001 })
            {
                var big = NewInput();
                for (int i = 1; i <= 4000; i++) big.Markers.Add(Mk(i, x => { }));
                for (int i = 1; i <= 4000; i++) big.Rows.Add(RowOf(big.Markers[i - 1]));
                for (int i = 0; i < add; i++) big.Rows.Add(NewRow(5000 + i));
                var r = MarkerPlan.Build(big);
                warnOk &= r.OverWarn == (add > 1000) && r.NeedsDialog == (add > 1000 || add > 50) && r.ExistingMarkers == 4000;
            }
            check(P + "warning: at 4,999 and 5,000 markers in the model there is no warning; at 5,001 there is", warnOk);

            var sets = NewInput();
            var a1 = Mk(1, x => x.SetName = "A"); var a2 = Mk(2, x => x.SetName = "A"); var a3 = Mk(3, x => x.SetName = "A"); var b1 = Mk(4, x => x.SetName = "B");
            sets.Markers.AddRange(new[] { a1, a2, a3, b1 });
            sets.Rows.AddRange(new[] { RowOf(a1), RowOf(a2) });
            sets.Rows.Add(NewRow(9, "Hard", r => r.SetName = "A"));
            sets.Options.DisabledSets.Add("B");
            var sr = MarkerPlan.Build(sets);
            var sa = sr.SetCounts.FirstOrDefault(s => s.Name == "A"); var sb = sr.SetCounts.FirstOrDefault(s => s.Name == "B");
            check(P + "per set: panel rows, markers up to date and 'not live on this seat' for set A; set B (off on this seat) says its markers were not re-tested (4b.3)",
                sa != null && sb != null && sa.InPanel == 3 && sa.UpToDate == 4 && sa.NotLive == 1 && !sa.SetOff && sb.SetOff && sb.NotRetested == 1 && sb.InPanel == 0
                && sr.SetCounts.Select(s => s.Name).SequenceEqual(A("A", "B")));

            check(P + "null and empty inputs never throw", NoThrow(() =>
            {
                MarkerPlan.Build(null);
                MarkerPlan.Build(new MarkerPlan.Input { Rows = null, Markers = null, Pending = null, Options = null, Counter = null });
                var weird = NewInput();
                weird.Rows.Add(null); weird.Markers.Add(null); weird.Pending.Add(null);
                weird.Rows.Add(new MarkerPlan.Row()); weird.Markers.Add(new MarkerPlan.Existing()); weird.Pending.Add(new MarkerPlan.Pending());
                weird.Markers.Add(new MarkerPlan.Existing { MarkerUid = "x", Record = new MarkerRecord() });
                MarkerPlan.Build(weird).ToCounts();
            }));
        }

        // ------------------------------------------------------------------ issue-number collisions between users (slice 3h)

        private static IssueNumbers.Counter Ctr(string uid, string user, string prefix, int next)
        {
            return new IssueNumbers.Counter { Uid = uid, UserName = user, Prefix = prefix, Next = next };
        }

        private static MarkerPlan.Existing Owned(int uid, string user, string issueNo, Action<MarkerRecord> tweak = null, MarkerAccess access = MarkerAccess.Free)
        {
            return Mk(uid, uid, r => { r.ReportedBy = user; r.IssueNo = issueNo; if (tweak != null) tweak(r); }, access);
        }

        private static IssueNumbers.Counter CopyOf(IssueNumbers.Counter c)
        {
            return Ctr(c.Uid, c.UserName, c.Prefix, c.Next);
        }

        /// <summary>One user's Refresh over the shared model: rows mirror the markers, every counter is visible.</summary>
        private static MarkerPlan.Result RefreshAs(string user, List<IssueNumbers.Counter> counters, List<MarkerPlan.Existing> markers, Action<MarkerPlan.Input> setup = null)
        {
            var input = NewInput();
            input.Options.UserName = user;
            var own = counters.First(c => c.UserName == user);
            input.Counter = CopyOf(own);
            input.AllCounters = counters.Select(CopyOf).ToList();
            foreach (var m in markers)
            {
                input.Markers.Add(m);
                if (m.Record != null) input.Rows.Add(RowOf(m));
            }
            if (setup != null) setup(input);
            return MarkerPlan.Build(input);
        }

        /// <summary>The model after the user's plan is applied: changed records replaced, the user's counter taken from CounterAfter.</summary>
        private static List<MarkerPlan.Existing> ApplyNumbers(List<MarkerPlan.Existing> markers, MarkerPlan.Result r, List<IssueNumbers.Counter> counters, string user)
        {
            var changed = r.Changes.ToDictionary(c => c.MarkerUid, c => c.After, StringComparer.Ordinal);
            var next = markers.Select(m =>
            {
                MarkerRecord after;
                return changed.TryGetValue(m.MarkerUid, out after)
                    ? new MarkerPlan.Existing { MarkerUid = m.MarkerUid, Record = after, Access = m.Access, HeldBy = m.HeldBy, ShownHash = m.ShownHash }
                    : m;
            }).ToList();
            var idx = counters.FindIndex(c => c.UserName == user);
            if (idx >= 0 && r.CounterAfter != null) counters[idx] = CopyOf(r.CounterAfter);
            return next;
        }

        private static List<string> Numbers(List<MarkerPlan.Existing> markers)
        {
            return markers.Where(m => m.Record != null).Select(m => m.Record.IssueNo).ToList();
        }

        private static bool Distinct(List<MarkerPlan.Existing> markers)
        {
            var n = Numbers(markers);
            return n.Distinct(StringComparer.OrdinalIgnoreCase).Count() == n.Count;
        }

        private static List<MarkerPlan.Existing> TwoUsers()
        {
            return new List<MarkerPlan.Existing>
            {
                Owned(1, "jdoe", "JD-0001"), Owned(2, "jdoe", "JD-0002"), Owned(3, "jane.doe", "JD-0001"), Owned(4, "jane.doe", "JD-0002")
            };
        }

        private static void IssueNumberRepair(Action<string, bool?> check)
        {
            Func<List<IssueNumbers.Counter>> counters = () => new List<IssueNumbers.Counter> { Ctr("c1", "jdoe", "JD", 3), Ctr("c2", "jane.doe", "JD", 3) };

            var jane = RefreshAs("jane.doe", counters(), TwoUsers());
            var j3 = jane.Changes.FirstOrDefault(c => c.MarkerUid == "m0003"); var j4 = jane.Changes.FirstOrDefault(c => c.MarkerUid == "m0004");
            check(P + "numbers: the user whose counter lost the prefix collision renumbers her own markers (JD-0001 to JD2-0001), keeps the old number in MergedIssueNos, moves her counter, and touches nobody else's",
                jane.Changes.Count == 2 && j3 != null && j4 != null && j3.After.IssueNo == "JD2-0001" && j4.After.IssueNo == "JD2-0002"
                && j3.After.MergedIssueNos.SequenceEqual(A("JD-0001")) && j4.After.MergedIssueNos.SequenceEqual(A("JD-0002"))
                && j3.Fields.SequenceEqual(A("IssueNo", "MergedIssueNos")) && jane.CounterAfter.Prefix == "JD2" && jane.CounterAfter.Next == 3
                && jane.PrefixMove != null && jane.PrefixMove.Uid == "c2" && jane.PrefixMove.OldPrefix == "JD" && jane.PrefixMove.NewPrefix == "JD2");

            var jdoe = RefreshAs("jdoe", counters(), TwoUsers());
            check(P + "numbers: the user who keeps the prefix changes nothing, and her counter stays JD", jdoe.Changes.Count == 0 && jdoe.CounterAfter.Prefix == "JD" && jdoe.PrefixMove == null);

            var cs = counters();
            var afterJane = ApplyNumbers(TwoUsers(), jane, cs, "jane.doe");
            var again = RefreshAs("jdoe", cs, afterJane);
            var again2 = RefreshAs("jane.doe", cs, afterJane);
            var originals = new Dictionary<string, string> { { "m0001", "JD-0001" }, { "m0002", "JD-0002" }, { "m0003", "JD-0001" }, { "m0004", "JD-0002" } };
            bool findable = afterJane.All(m => originals[m.MarkerUid] == m.Record.IssueNo || m.Record.MergedIssueNos.Contains(originals[m.MarkerUid]));
            check(P + "numbers: after her Refresh every number in the model is distinct, every old number is still found (IssueNo or MergedIssueNos), and a Refresh by either user then plans nothing",
                Distinct(afterJane) && findable && again.Changes.Count == 0 && again2.Changes.Count == 0);

            var csA = counters(); var csB = counters();
            var a1 = ApplyNumbers(TwoUsers(), RefreshAs("jdoe", csA, TwoUsers()), csA, "jdoe");
            var a2 = ApplyNumbers(a1, RefreshAs("jane.doe", csA, a1), csA, "jane.doe");
            var b1 = ApplyNumbers(TwoUsers(), RefreshAs("jane.doe", csB, TwoUsers()), csB, "jane.doe");
            var b2 = ApplyNumbers(b1, RefreshAs("jdoe", csB, b1), csB, "jdoe");
            check(P + "numbers: whichever user refreshes first, the final numbers are the same",
                Distinct(a2) && Distinct(b2) && Numbers(a2).SequenceEqual(Numbers(b2)));

            var withNew = RefreshAs("jane.doe", counters(), TwoUsers(), inp => inp.Rows.Add(NewRow(7)));
            check(P + "numbers: a marker the mover creates in the same Refresh takes the moved prefix (JD2-0003)",
                withNew.Creations.Count == 1 && withNew.Creations[0].Record.IssueNo == "JD2-0003" && withNew.CounterAfter.Next == 4);

            var w8c = new List<IssueNumbers.Counter> { Ctr("c1", "jdoe", "JD", 3), Ctr("c2", "jdoe", "JD", 3) };
            var w8m = new List<MarkerPlan.Existing> { Owned(1, "jdoe", "JD-0001"), Owned(2, "jdoe", "JD-0001"), Owned(3, "jdoe", "JD-0002") };
            var w8Input = NewInput(); w8Input.Options.UserName = "jdoe"; w8Input.Counter = Ctr("c2", "jdoe", "JD", 3); w8Input.AllCounters = w8c;
            foreach (var m in w8m) { w8Input.Markers.Add(m); w8Input.Rows.Add(RowOf(m)); }
            var w8 = MarkerPlan.Build(w8Input);
            check(P + "numbers: one user on two PCs (two counters, one name): only the higher-UniqueId marker of the duplicated number moves",
                w8.Changes.Count == 1 && w8.Changes[0].MarkerUid == "m0002" && w8.Changes[0].After.IssueNo == "JD2-0001" && w8.CounterAfter.Prefix == "JD2");

            var heldModel = TwoUsers();
            heldModel[2] = Owned(3, "jane.doe", "JD-0001", null, MarkerAccess.HeldByOther);
            var csH = counters();
            var held1 = RefreshAs("jane.doe", csH, heldModel);
            var afterHeld = ApplyNumbers(heldModel, held1, csH, "jane.doe");
            afterHeld[2] = Owned(3, "jane.doe", "JD-0001");                 // the hold is gone by the next Refresh
            var held2 = RefreshAs("jane.doe", csH, afterHeld);
            var afterHeld2 = ApplyNumbers(afterHeld, held2, csH, "jane.doe");
            check(P + "numbers: a held marker among the targets is passed over while the free ones are renumbered and the counter moves; the next Refresh renames it by the residual rule (D43), and the numbers end distinct",
                held1.Changes.Count == 1 && held1.Changes[0].MarkerUid == "m0004" && held1.PassedOver.Count == 1 && held1.PassedOver[0].MarkerUid == "m0003" && held1.CounterAfter.Prefix == "JD2"
                && held2.Changes.Count == 1 && held2.Changes[0].MarkerUid == "m0003" && held2.Changes[0].After.IssueNo == "JD2-0001" && Distinct(afterHeld2));

            var reversed = new List<IssueNumbers.Counter> { Ctr("c9", "jdoe", "JD", 3), Ctr("c2", "jane.doe", "JD", 3) };
            var jdoeMoves = RefreshAs("jdoe", reversed, TwoUsers());
            check(P + "numbers: another user's markers are never renumbered, whoever moves: with jdoe's counter the higher one, jdoe renumbers only jdoe's two markers",
                jdoeMoves.Changes.Select(c => c.MarkerUid).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(A("m0001", "m0002")) && jdoeMoves.CounterAfter.Prefix == "JD2");

            var noAll = NewInput(); noAll.Options.UserName = "jane.doe";
            foreach (var m in TwoUsers()) { noAll.Markers.Add(m); noAll.Rows.Add(RowOf(m)); }
            check(P + "numbers: without the list of counters the plan does what it did before (no renumbering)", MarkerPlan.Build(noAll).Changes.Count == 0);

            var grouped = TwoUsers();
            var gInput = NewInput(); gInput.Options.UserName = "jane.doe"; gInput.Counter = Ctr("c2", "jane.doe", "JD", 3); gInput.AllCounters = counters();
            foreach (var m in grouped) { gInput.Markers.Add(m); var row = RowOf(m); if (m.MarkerUid == "m0003" || m.MarkerUid == "m0004") row.GroupKey = "g"; gInput.Rows.Add(row); }
            var gr = MarkerPlan.Build(gInput);
            check(P + "numbers: a group label is built from the renumbered numbers (JD2-0001 (+1))",
                gr.Changes.Count == 2 && gr.Changes.All(c => c.After.GroupLabel == "JD2-0001 (+1)" && c.Fields.Contains("GroupLabel")));

            var otherPrefix = new List<MarkerPlan.Existing> { Owned(1, "jdoe", "JD-0001"), Owned(3, "jane.doe", "KJ-0005") };
            var op = RefreshAs("jane.doe", counters(), otherPrefix);
            check(P + "numbers: a marker on another prefix with a number nobody else holds is left alone (the residual rule needs a duplicate)", op.Changes.Count == 0 || op.Changes.All(c => c.MarkerUid != "m0003"));

            var rnd = new Random(20261106);
            int notDistinct = 0, lost = 0, touched = 0;
            string[] names = { "jdoe", "jdan", "jdee", "j.dunn" };
            for (int i = 0; i < 300; i++)
            {
                int users = rnd.Next(2, 5);
                var picked = names.OrderBy(_ => rnd.Next()).Take(users).ToList();
                var cs2 = new List<IssueNumbers.Counter>();
                var model = new List<MarkerPlan.Existing>();
                var original = new Dictionary<string, string>(StringComparer.Ordinal);
                int uid = 0;
                for (int u = 0; u < picked.Count; u++)
                {
                    int n = rnd.Next(0, 5);
                    cs2.Add(Ctr("c" + rnd.Next(10, 99) + u, picked[u], "JD", n + 1));
                    for (int k = 1; k <= n; k++)
                    {
                        var m = Owned(++uid, picked[u], IssueNumbers.Format("JD", k));
                        model.Add(m); original[m.MarkerUid] = m.Record.IssueNo;
                    }
                }
                for (int round = 0; round < 2; round++)
                    foreach (var user in picked.OrderBy(_ => rnd.Next()))
                    {
                        var r = RefreshAs(user, cs2, model);
                        if (r.Changes.Any(c => !string.Equals(model.First(m => m.MarkerUid == c.MarkerUid).Record.ReportedBy, user, StringComparison.OrdinalIgnoreCase))) touched++;
                        model = ApplyNumbers(model, r, cs2, user);
                    }
                if (!Distinct(model)) notDistinct++;
                if (model.Any(m => original[m.MarkerUid] != m.Record.IssueNo && !m.Record.MergedIssueNos.Contains(original[m.MarkerUid]))) lost++;
            }
            check(P + "numbers: fuzz, 300 cases of 2 to 4 same-initials users refreshing in random order, twice: no number repeats at the end (seed 20261106)", notDistinct == 0);
            check(P + "numbers: fuzz: every marker's original number is still its number or in its MergedIssueNos, and no Refresh renumbered another user's marker", lost == 0 && touched == 0);
        }

        // ------------------------------------------------------------------ group labels the plan computes (slice 3d)

        private static MarkerPlan.Row InGroup(MarkerPlan.Row row, string key)
        {
            row.GroupKey = key;
            return row;
        }

        private static void Groups(Action<string, bool?> check)
        {
            var fresh = NewInput();
            for (int i = 1; i <= 3; i++) fresh.Rows.Add(NewRow(i, "Hard", r => r.GroupKey = "g"));
            fresh.Rows.Add(NewRow(4, "Hard", r => r.GroupKey = ""));
            var f = MarkerPlan.Build(fresh);
            check(P + "group label: three new markers in one group are labelled with the lowest number and how many others (JD-0001 (+2)); a pair alone has none",
                f.Creations.Count == 4 && f.Creations.Take(3).All(c => c.Record.GroupLabel == "JD-0001 (+2)") && f.Creations[3].Record.GroupLabel == "");

            var joined = NewInput();
            var m5 = Mk(5); var m9 = Mk(9);
            joined.Markers.AddRange(new[] { m5, m9 });
            joined.Rows.Add(InGroup(RowOf(m5), "g")); joined.Rows.Add(InGroup(RowOf(m9), "g")); joined.Rows.Add(NewRow(1, "Hard", r => r.GroupKey = "g"));
            var j = MarkerPlan.Build(joined);
            check(P + "group label: a new marker joining two existing ones takes the lowest number, and the existing markers get the new label (only the label is written)",
                j.Creations.Count == 1 && j.Creations[0].Record.IssueNo == "JD-0001" && j.Creations[0].Record.GroupLabel == "JD-0001 (+2)"
                && j.Changes.Count == 2 && j.Changes.All(c => c.Fields.SequenceEqual(A("GroupLabel")) && c.After.GroupLabel == "JD-0001 (+2)"));

            var steady = NewInput();
            var s5 = Mk(5, x => x.GroupLabel = "JD-0005 (+1)"); var s9 = Mk(9, x => x.GroupLabel = "JD-0005 (+1)"); var s7 = Mk(7);
            steady.Markers.AddRange(new[] { s5, s9, s7 });
            steady.Rows.Add(InGroup(RowOf(s5), "g")); steady.Rows.Add(InGroup(RowOf(s9), "g")); steady.Rows.Add(InGroup(RowOf(s7), ""));
            var st = MarkerPlan.Build(steady);
            check(P + "group label: labels that are already right write nothing (RF20)", Total(st) == 0 && st.Unchanged == 3);

            var split = NewInput();
            var p5 = Mk(5, x => x.GroupLabel = "JD-0005 (+1)"); var p9 = Mk(9, x => x.GroupLabel = "JD-0005 (+1)"); var p7 = Mk(7);
            split.Markers.AddRange(new[] { p5, p9, p7 });
            split.Rows.Add(InGroup(RowOf(p5), "g")); split.Rows.Add(InGroup(RowOf(p9), "")); split.Rows.Add(InGroup(RowOf(p7), ""));
            var sp = MarkerPlan.Build(split);
            check(P + "group label: when a member leaves, the group and the leaver are both relabelled (to nothing) and nobody else is written",
                sp.Changes.Count == 2 && sp.Changes.All(c => c.After.GroupLabel == "" && c.Fields.SequenceEqual(A("GroupLabel"))) && sp.Changes.Select(c => c.MarkerUid).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(A("m0005", "m0009")));

            var fallback = NewInput();
            var fm = Mk(1);
            fallback.Markers.Add(fm);
            var fr = RowOf(fm); fr.GroupKey = null; fr.GroupLabel = "JD-0001 (+3)";
            fallback.Rows.Add(fr);
            var fb = MarkerPlan.Build(fallback);
            check(P + "group label: a row with no group key uses the label it carries, as before", fb.Changes.Count == 1 && fb.Changes[0].After.GroupLabel == "JD-0001 (+3)");

            var capped = NewInput();
            capped.Options.Cap = 2;
            for (int i = 1; i <= 3; i++) capped.Rows.Add(NewRow(i, "Hard", r => r.GroupKey = "g"));
            var cp = MarkerPlan.Build(capped);
            check(P + "group label: a pair left out by the cap is not a member of its group", cp.Creations.Count == 2 && cp.NotMarked.Count == 1 && cp.Creations.All(c => c.Record.GroupLabel == "JD-0001 (+1)"));

            var heldGroup = NewInput();
            var h5 = Mk(5, null, MarkerAccess.HeldByOther); var h9 = Mk(9);
            heldGroup.Markers.AddRange(new[] { h5, h9 });
            heldGroup.Rows.Add(InGroup(RowOf(h5), "g")); heldGroup.Rows.Add(InGroup(RowOf(h9), "g"));
            var hg = MarkerPlan.Build(heldGroup);
            check(P + "group label: a held marker counts in its group; its own label write is passed over, the free one is written",
                hg.Changes.Count == 1 && hg.Changes[0].MarkerUid == "m0009" && hg.Changes[0].After.GroupLabel == "JD-0005 (+1)" && hg.PassedOver.Count == 1 && hg.PassedOver[0].Reason == "held by kdoe");

            var unretested = NewInput();
            var u5 = Mk(5, x => x.GroupLabel = "JD-0005 (+1)"); var u9 = Mk(9, x => x.GroupLabel = "JD-0005 (+1)");
            unretested.Markers.AddRange(new[] { u5, u9 });
            var ru9 = InGroup(RowOf(u9), "g"); ru9.Verdict = PlanVerdict.NotRetested; ru9.NotRetestedReason = "link not loaded";
            unretested.Rows.Add(InGroup(RowOf(u5), "g")); unretested.Rows.Add(ru9);
            var un = MarkerPlan.Build(unretested);
            check(P + "group label: a pair that could not be re-tested is not a member, and is not rewritten; the other is labelled alone",
                un.Changes.Count == 1 && un.Changes[0].MarkerUid == "m0005" && un.Changes[0].After.GroupLabel == "" && un.NotRetested.Count == 1);
        }

        // ------------------------------------------------------------------ what the plan carries for the executor (slice 2b)

        private static bool SameMap(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            return a != null && b != null && !ReferenceEquals(a, b) && a.Count == b.Count && a.All(kv => { string v; return b.TryGetValue(kv.Key, out v) && v == kv.Value; });
        }

        private static Dictionary<string, string> Display(string name)
        {
            return new Dictionary<string, string> { { "A name", name }, { "Level", "L1" } };
        }

        private static void Carries(Action<string, bool?> check)
        {
            var statuses = new[] { MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Reopened, MarkerStatus.Coordinated, MarkerStatus.Ignored, MarkerStatus.Resolved };
            check(P + "status order: 1 New, 2 Pending, 3 Reopened, 4 Coordinated, 5 Ignored, 6 Resolved, all different (the schedule's sort)",
                statuses.Select(MarkerStatusText.Order).SequenceEqual(Orders));

            var input = NewInput();
            input.Rows.Add(NewRow(1, "Hard", r => r.Shown = Display("Pipe: 100 [5]")));
            input.Rows.Add(NewRow(2, "Hard", r => r.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored }));
            var made = MarkerPlan.Build(input);
            var c1 = made.Creations.Count == 2 ? made.Creations[0] : null;
            var c2 = made.Creations.Count == 2 ? made.Creations[1] : null;
            check(P + "carries: a creation carries a copy of its row's display values and the order of its status (New 1, a seeded Ignored 5)",
                c1 != null && SameMap(c1.Shown, input.Rows[0].Shown) && c1.StatusOrder == 1 && c2.Shown == null && c2.StatusOrder == 5);

            var resolve = One(Mk(1), row => { row.Verdict = PlanVerdict.NoLongerClashes; row.Shown = Display("x"); });
            var reopen = One(Mk(1, x => { x.Status = MarkerStatus.Resolved; x.ResolvedAt = Day0.AddDays(-1); }), row => row.Shown = Display("y"));
            var pending = With(1);
            pending.Pending.Add(Pend(1, 5));
            var pend = MarkerPlan.Build(pending);
            var rs = resolve.Changes.FirstOrDefault(); var ro = reopen.Changes.FirstOrDefault(); var pe = pend.Changes.FirstOrDefault();
            check(P + "carries: an update carries a copy of the row's display values and the order of the NEW status (resolve 6, reopen 3, pending-applied 5)",
                rs != null && ro != null && pe != null && rs.StatusOrder == 6 && SameMap(rs.Shown, Display("x")) && ro.StatusOrder == 3 && SameMap(ro.Shown, Display("y")) && pe.StatusOrder == 5);

            var merge = MarkerPlan.Build(Dup(null, null));
            var mc = merge.Changes.FirstOrDefault();
            check(P + "carries: an update made without a row (a merge, a collision rewrite) carries no display values and still the status order",
                mc != null && mc.Shown == null && mc.StatusOrder == 1);

            var rnd = new Random(20261102);
            int bad = 0;
            for (int i = 0; i < 300; i++)
            {
                var random = RandomInput(rnd, 5000);
                int k = 0;
                foreach (var row in random.Rows) row.Shown = Display("r" + (k++));
                var r = MarkerPlan.Build(random);
                var byTriple = random.Rows.GroupBy(x => ClashIdOf.Triple(x.HostUid, x.LinkUid, x.OtherUid)).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var c in r.Creations)
                {
                    List<MarkerPlan.Row> rows;
                    bool fromRow = byTriple.TryGetValue(c.Triple, out rows) && rows.Any(x => SameMap(c.Shown, x.Shown));
                    if (c.StatusOrder != MarkerStatusText.Order(c.Record.Status) || !fromRow) bad++;
                }
                foreach (var c in r.Changes)
                {
                    List<MarkerPlan.Row> rows;
                    bool ok = c.Shown == null || (byTriple.TryGetValue(ClashIdOf.Triple(c.After.HostUid, c.After.LinkUid, c.After.OtherUid), out rows) && rows.Any(x => SameMap(c.Shown, x.Shown)));
                    if (c.StatusOrder != MarkerStatusText.Order(c.After.Status) || !ok) bad++;
                }
            }
            check(P + "carries: fuzz, 300 inputs: every creation and update has the order of its record's status and display values taken from its own row", bad == 0);
        }

        // ------------------------------------------------------------------ property fuzz

        private static MarkerPlan.Input RandomInput(Random rnd, int cap)
        {
            var input = NewInput();
            input.Options.Cap = cap;
            input.Options.RemoveRetention = rnd.Next(2) == 0;
            int n = rnd.Next(0, 26);
            var pendTriples = new HashSet<int>();
            for (int i = 1; i <= n; i++)
            {
                int tripleNo = rnd.Next(10) == 0 && i > 1 ? rnd.Next(1, i) : i;
                var access = rnd.Next(5) == 0 ? (MarkerAccess)rnd.Next(1, 6) : MarkerAccess.Free;
                var status = (MarkerStatus)rnd.Next(6);
                int ageDays = rnd.Next(0, 90);
                var m = Mk(i, tripleNo, x =>
                {
                    x.Status = status; x.ClearanceMm = Clearances[rnd.Next(3)]; x.Insulated = rnd.Next(4) == 0;
                    x.Severity = A("Hard", "Clearance", "Unverified")[rnd.Next(3)]; x.Seq = rnd.Next(1, 9);
                    if (status == MarkerStatus.Resolved) { x.ResolvedBy = "kdoe"; x.ResolvedAt = Day0.AddDays(-ageDays); }
                    if (rnd.Next(4) == 0) { x.Note = "n"; x.AssignedTo = "asmith"; }
                }, access);
                if (access == MarkerAccess.NewerFormat || access == MarkerAccess.Unreadable) m.Record = null;
                m.OffPlace = rnd.Next(12) == 0; m.WrongWorkset = rnd.Next(12) == 0; if (rnd.Next(10) == 0) m.ShownHash = "old";
                input.Markers.Add(m);
                if (m.Record != null && rnd.Next(10) < 7)
                {
                    var row = RowOf(m);
                    int v = rnd.Next(20);
                    if (v < 3) row.Verdict = PlanVerdict.NoLongerClashes; else if (v < 6) { row.Verdict = PlanVerdict.NotRetested; row.NotRetestedReason = "link not loaded"; }
                    if (rnd.Next(10) == 0) row.RuleClearanceMm += 25;
                    if (rnd.Next(5) == 0) row.Severity = A("Hard", "Clearance", "Unverified", "")[rnd.Next(4)];
                    if (rnd.Next(5) == 0) row.X += rnd.Next(2) == 0 ? 0.0004 : 0.05;
                    if (rnd.Next(7) == 0) row.SetName = A("A", "B", "", null)[rnd.Next(4)];
                    if (rnd.Next(7) == 0) row.GroupLabel = A("JD-0001 (+1)", "", null)[rnd.Next(3)];
                    if (rnd.Next(7) == 0) row.AlsoSets = rnd.Next(2) == 0 ? null : new List<string> { "T2", "T1" };
                    if (rnd.Next(10) == 0) { row.HostId += 7; row.Key = "k" + i; }
                    if (rnd.Next(10) == 0) row.ShownHash = "new";
                    input.Rows.Add(row);
                }
                if (m.Record != null && rnd.Next(5) == 0 && pendTriples.Add(tripleNo))
                    input.Pending.Add(Pend(tripleNo, rnd.Next(2) == 0 ? m.Record.Seq : m.Record.Seq - 1, p => { if (rnd.Next(2) == 0) p.Status = null; p.AssignedTo = rnd.Next(2) == 0 ? "asmith" : null; }));
            }
            int extra = rnd.Next(0, 9);
            for (int i = 0; i < extra; i++)
            {
                int kind = rnd.Next(10);
                var row = NewRow(100 + i, A("Hard", "Clearance", "Unverified")[rnd.Next(3)]);
                if (kind == 0) row.HostUid = "";
                else if (kind == 1) row.HasPoint = false;
                else if (kind == 2) row.Verdict = PlanVerdict.NoLongerClashes;
                else if (kind == 3) { row.Live = false; row.Verdict = PlanVerdict.NotRetested; row.SetName = ""; row.Seed = rnd.Next(2) == 0 ? new MarkerPlan.Seed { Status = MarkerStatus.Ignored, By = "kdoe" } : null; }
                else if (kind == 4) row.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Pending, Note = "x" };
                else if (kind == 5) row.RememberedIssueNo = "JD-" + rnd.Next(1, 30).ToString("D4", CultureInfo.InvariantCulture);
                input.Rows.Add(row);
            }
            if (rnd.Next(6) == 0) input.Pending.Add(Pend(500, 1));
            return input;
        }

        private static void Fuzz(Action<string, bool?> check)
        {
            var rnd = new Random(20261101);
            string whyIdem = "", whyOrder = "", whyRemoval = "";
            int thrown = 0, unbalanced = 0, wroteNonFree = 0, notIdem = 0, notOrder = 0, badChange = 0, badRemoval = 0, badPending = 0;
            const int cases = 300;
            for (int i = 0; i < cases; i++)
            {
                var input = RandomInput(rnd, i % 3 == 0 ? 4 : 5000);
                MarkerPlan.Result r;
                try { r = MarkerPlan.Build(input); }
                catch (Exception) { thrown++; continue; }

                if (!Balanced(r) || r.ExistingMarkers != input.Markers.Count(m => m != null && m.MarkerUid != null)) unbalanced++;
                var access = input.Markers.Where(m => m != null && m.MarkerUid != null).ToDictionary(m => m.MarkerUid, m => m.Access, StringComparer.Ordinal);
                if (r.Changes.Any(c => access[c.MarkerUid] != MarkerAccess.Free) || r.Removals.Any(x => access[x.MarkerUid] != MarkerAccess.Free)) wroteNonFree++;

                foreach (var c in r.Changes)
                {
                    bool differs = !string.Equals(string.Join("\n", MarkerFormats.Write(c.After).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + k.Value)),
                                                  string.Join("\n", MarkerFormats.Write(c.Before).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + k.Value)), StringComparison.Ordinal);
                    if (!(c.After.LastCheckedAt == Day0 && c.After.Seq == c.Before.Seq + 1 && differs && c.Fields.Count > 0)) badChange++;
                }
                var byUid = input.Markers.Where(m => m != null && m.MarkerUid != null).ToDictionary(m => m.MarkerUid, m => m, StringComparer.Ordinal);
                foreach (var x in r.Removals)
                {
                    var m = byUid[x.MarkerUid];
                    bool ok = x.Reason == "merged duplicate" || (input.Options.RemoveRetention && m.Record != null && m.Record.Status == MarkerStatus.Resolved && m.Record.ResolvedAt.HasValue
                                                                  && Day0 - m.Record.ResolvedAt.Value > TimeSpan.FromDays(30) && !input.Pending.Any(p => p.Triple == ClashIdOf.Triple(m.Record.HostUid, m.Record.LinkUid, m.Record.OtherUid)));
                    if (!ok) { badRemoval++; if (whyRemoval.Length == 0) whyRemoval = "case " + i + " " + x.MarkerUid + " " + x.Reason + " " + (m.Record == null ? "null" : m.Record.Status + " " + m.Record.ResolvedAt); }
                }
                if (r.Pending.Any(o => o.Kind == PendingKind.Applied && access.ContainsKey(o.MarkerUid ?? "") && access[o.MarkerUid] != MarkerAccess.Free)) badPending++;

                if (i % 3 != 0)
                {
                    var next = MarkerPlan.Build(Apply(input, r));
                    // A pending triage keeps retention waiting for one Refresh, so a retention removal of a pair that had one may follow.
                    var delayed = new HashSet<string>(input.Pending.Select(p => p.Triple), StringComparer.Ordinal);
                    var nextByUid = Apply(input, r).Markers.Where(m => m.MarkerUid != null).ToDictionary(m => m.MarkerUid, m => m, StringComparer.Ordinal);
                    int lateRetention = next.Removals.Count(x => x.Reason == "retention" && nextByUid.ContainsKey(x.MarkerUid) && delayed.Contains(ClashIdOf.Triple(nextByUid[x.MarkerUid].Record.HostUid, nextByUid[x.MarkerUid].Record.LinkUid, nextByUid[x.MarkerUid].Record.OtherUid)));
                    if (next.Creations.Count + next.Changes.Count + next.Removals.Count - lateRetention != 0)
                    {
                        notIdem++;
                        if (whyIdem.Length == 0) whyIdem = "case " + i + " creations " + next.Creations.Count + " changes " + string.Join(",", next.Changes.Select(c => c.MarkerUid + ":" + string.Join("+", c.Fields))) + " removals " + string.Join(",", next.Removals.Select(x => x.MarkerUid + ":" + x.Reason));
                    }
                }

                var shuffled = new MarkerPlan.Input { Options = input.Options, Counter = input.Counter };
                shuffled.Rows = input.Rows.OrderBy(_ => rnd.Next()).ToList();
                shuffled.Markers = input.Markers.OrderBy(_ => rnd.Next()).ToList();
                shuffled.Pending = input.Pending.OrderBy(_ => rnd.Next()).ToList();
                if (Fp(MarkerPlan.Build(shuffled)) != Fp(r))
                {
                    notOrder++;
                    if (whyOrder.Length == 0)
                    {
                        var a = Fp(r).Split((char)10); var b = Fp(MarkerPlan.Build(shuffled)).Split((char)10);
                        whyOrder = "case " + i + " " + string.Join(" / ", a.Except(b).Take(2)) + " <> " + string.Join(" / ", b.Except(a).Take(2));
                    }
                }
            }
            check(P + "fuzz: " + cases + " random models never throw (seed 20261101)", thrown == 0);
            check(P + "fuzz: the counts balance every time and equal the markers plus the creation attempts", unbalanced == 0);
            check(P + "fuzz: no write or removal is ever planned for a marker that is not free (RF2)", wroteNonFree == 0 && badPending == 0);
            check(P + "fuzz: every write changes something, sets Last checked to Today and raises Seq by one, and only written markers are touched (RF14)", badChange == 0);
            check(P + "fuzz: a removal is always a merge or a ticked, old, resolved, pending-free retention" + (badRemoval == 0 ? "" : " [" + whyRemoval + "]"), badRemoval == 0);
            check(P + "fuzz: after the plan is applied, planning again writes nothing (idempotent)" + (notIdem == 0 ? "" : " [" + whyIdem + "]"), notIdem == 0);
            check(P + "fuzz: the plan does not depend on the order of rows, markers and pending triages (A7)" + (notOrder == 0 ? "" : " [" + whyOrder + "]"), notOrder == 0);
        }
    }
}
