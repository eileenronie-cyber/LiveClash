using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, delta review 3 (DR3-L1, L2, L3, L4): an unmatched pending key must not hold a marker back from the reconcile, two markers must
    /// not get one new issue number, the last issue number must be used, and a filtered self-test run must say so. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/2026-11-02-dr3-predictions.md and BUILD-LOG-DR3.md.
    /// </summary>
    public static class MarkerDr3Checks
    {
        private const string P = "markers dr3: ";
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            UnmatchedKeyDoesNotHoldBack(check);
            NoTwoMarkersShareANewNumber(check);
            RenumberNeverCreatesASharedNumber(check);
            LastIssueNumber(check);
            FilteredRunHeadline(check);
            FilteredRunFileName(check);
        }

        private static MarkerPlan.Row Rw(int n)
        {
            return new MarkerPlan.Row
            {
                Key = "k" + n, HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, Live = true, Verdict = PlanVerdict.Clashing, RuleClearanceMm = 25,
                Severity = "Hard", HasPoint = true, X = n, SetName = "Set A"
            };
        }

        private static MarkerRecord Rec(int n, string issue, string by)
        {
            return new MarkerRecord
            {
                Key = "k" + n, HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, ClashId = ClashIdOf.Of("h" + n, "", "o" + n), IssueNo = issue, ReportedBy = by,
                ReportedAt = Today, Severity = "Hard", ClearanceMm = 25, SetName = "Set A", X = n, Seq = 1, LastCheckedAt = Today, LastCheckedBy = "jdoe"
            };
        }

        private static IssueNumbers.Counter C(string uid, string user, string prefix, int next = 1) { return new IssueNumbers.Counter { Uid = uid, UserName = user, Prefix = prefix, Next = next }; }

        private static MarkerPlan.Result Build(IEnumerable<MarkerPlan.Row> rows, IEnumerable<MarkerPlan.Existing> markers, string user, IssueNumbers.Counter own, IEnumerable<IssueNumbers.Counter> all)
        {
            return MarkerPlan.Build(new MarkerPlan.Input
            {
                Rows = (rows ?? Array.Empty<MarkerPlan.Row>()).ToList(), Markers = (markers ?? Array.Empty<MarkerPlan.Existing>()).ToList(), Counter = own,
                AllCounters = (all ?? Array.Empty<IssueNumbers.Counter>()).ToList(), Options = new MarkerPlan.Options { Today = Today, UserName = user }
            });
        }

        // ------------------------------------------------------------------ L1: an unmatched pending key still holds its marker back

        /// <summary>
        /// DR3-L1. A marker whose record has a Key but an EMPTY triple (the reader accepts empty UniqueIds) never reaches the key-to-triple map, so an open
        /// pending entry under its key is "unmatched" and is closed with a done line. The reconcile skips every key that still has an open pending entry in
        /// the view it is given, so the key must be counted as closed there too, or the model's value reaches the ledger one Refresh late.
        /// </summary>
        private static void UnmatchedKeyDoesNotHoldBack(Action<string, bool?> check)
        {
            var damaged = new MarkerRecord
            {
                Key = "kU", HostUid = "", LinkUid = "", OtherUid = "", ClashId = "", IssueNo = "JD-0001", Status = MarkerStatus.Coordinated, Note = "agreed", AssignedTo = "",
                ReportedBy = "jdoe", ReportedAt = Today, ChangedBy = "jdoe", ChangedAt = Today, Severity = "Hard", ClearanceMm = 25, SetName = "Set A", Seq = 3,
                LastCheckedAt = Today, LastCheckedBy = "jdoe"
            };
            var marker = new MarkerPlan.Existing { MarkerUid = "mU", Record = damaged, Access = MarkerAccess.Free };
            var lines = new List<string> { LedgerExtras.PendingLine("kU", T0, "jdoe", 1, MarkerStatus.Pending, null, null) };
            var decisions = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int e, l, r;
            ClashStatusReplay.Replay(lines, decisions, out e, out l, out r);
            var request = new MarkerPlanAssembler.Request
            {
                Clashes = new List<MarkerPlanAssembler.Clash>(), Decisions = decisions, Extras = LedgerExtras.Read(lines), Markers = new List<MarkerPlan.Existing> { marker },
                OwnCounter = C("c-jd", "jdoe", "JD"), Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            };

            MarkerPlanAssembler.Written written = null;
            MarkerPlanAssembler.Assembly assembly = null;
            string failure = null;
            try
            {
                assembly = MarkerPlanAssembler.Assemble(request);
                var plan = MarkerPlan.Build(assembly.Input);
                written = MarkerPlanAssembler.Lines(assembly, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T0.AddMinutes(10));
            }
            catch (Exception ex) { failure = ex.GetType().Name; }

            bool unmatched = assembly != null && assembly.UnmatchedPendingKeys.Count == 1 && assembly.UnmatchedPendingKeys[0] == "kU";
            bool closedLine = written != null && written.Lines.Any(x => x.StartsWith("kU\t" + LedgerExtras.DoneEvent + "\t", StringComparison.Ordinal));
            check(P + "L1 control: an open pending entry under the key of a marker with an empty triple is unmatched, and its done line is written" + (failure != null ? " (" + failure + ")" : ""),
                failure == null && unmatched && closedLine && written.PendingClosed == 1);
            int decided = written == null ? -1 : written.ReconcileDecisions;
            check(P + "L1 the model's decision under that key reaches the ledger in the same Refresh, not one later (" + decided + " decision line(s) written; the pending entry is closed by the same step)",
                written != null && decided >= 1 && written.Lines.Any(x => x.StartsWith("kU\t", StringComparison.Ordinal) && x.Contains("\t" + ClashStatusKind.Coordinated + "\t")));
        }

        // ------------------------------------------------------------------ L2: two markers never get one new number

        /// <summary>
        /// DR3-L2. A user's counters on one prefix, with no more markers holding one number than there are counters: every marker that moves gets its own
        /// number. (With MORE markers than counters the surplus is left as found: INFO below.) The counters
        /// here include records with no UniqueId, which a damaged counter record can have; the old pin of the duplicate is replaced by this.
        /// </summary>
        private static void NoTwoMarkersShareANewNumber(Action<string, bool?> check)
        {
            var markers = new[]
            {
                new IssueNumbers.IssueMarker { MarkerUid = "m1", IssueNo = "JD-0001", ReportedBy = "jd" },
                new IssueNumbers.IssueMarker { MarkerUid = "m2", IssueNo = "JD-0001", ReportedBy = "jd" }
            };
            var counters = new[] { C(null, "jd", "JD"), C("", "jd", "JD"), C("A", "jd", "JD") };
            int bad = 0, total = 0;
            string worst = "";
            foreach (var order in Permutations(counters))
            {
                total++;
                var plan = IssueNumbers.RenumberPlan(order, markers);
                if (plan.Select(p => p.NewIssueNo).Distinct(StringComparer.Ordinal).Count() != plan.Count) { bad++; worst = string.Join(",", plan.Select(p => p.MarkerUid + ">" + p.NewIssueNo)); }
            }
            check(P + "L2 counters with no UniqueId, an empty one and one called A, in every order: two markers holding one number never leave the plan with one new number (" + bad + " of " + total + " orders give a duplicate" + (bad > 0 ? ", e.g. " + worst : "") + ")",
                bad == 0 && total == 6);

            var distinct = IssueNumbers.RenumberPlan(new[] { C("a", "jd", "JD"), C("b", "jd", "JD") }, markers);
            check(P + "L2 control: two counters with their own UniqueIds and two markers on one number: one marker stays, one takes JD2 (" + string.Join(",", distinct.Select(p => p.MarkerUid + ">" + p.NewIssueNo)) + ")",
                distinct.Count == 1 && distinct[0].MarkerUid == "m2" && distinct[0].NewIssueNo == "JD2-0001");

            // A moved counter with no UniqueId sorts before the user's moved counters that have one (the order PrefixMoves uses), in every input order.
            // ann's counter (no UniqueId) keeps JD; bob's no-UniqueId counter then moves first (JD2) and his counter "1d" second (JD3).
            var bobMarkers = new[]
            {
                new IssueNumbers.IssueMarker { MarkerUid = "m1", IssueNo = "JD-0001", ReportedBy = "bob" },
                new IssueNumbers.IssueMarker { MarkerUid = "m2", IssueNo = "JD-0001", ReportedBy = "bob" }
            };
            int orderBad = 0, orderTotal = 0;
            string orderWorst = "";
            foreach (var order in Permutations(new[] { C(null, "ann", "JD"), C(null, "bob", "JD"), C("1d", "bob", "JD") }))
            {
                orderTotal++;
                var p = IssueNumbers.RenumberPlan(order, bobMarkers);
                if (!(p.Count == 2 && p[0].MarkerUid == "m1" && p[0].NewIssueNo == "JD2-0001" && p[1].MarkerUid == "m2" && p[1].NewIssueNo == "JD3-0001"))
                { orderBad++; orderWorst = string.Join(",", p.Select(x => x.MarkerUid + ">" + x.NewIssueNo)); }
            }
            check(P + "L2 a moved counter with no UniqueId comes before the user's moved counters that have one: the first marker takes its prefix (JD2), the next takes the counter 1d's (JD3), in every input order ("
                + orderBad + " of " + orderTotal + " orders differ" + (orderBad > 0 ? ", e.g. " + orderWorst : "") + ")",
                orderBad == 0 && orderTotal == 6);
        }

        private static List<IssueNumbers.Counter[]> Permutations(IssueNumbers.Counter[] items)
        {
            var result = new List<IssueNumbers.Counter[]>();
            Permute(items.ToList(), new List<IssueNumbers.Counter>(), result);
            return result;
        }

        private static void Permute(List<IssueNumbers.Counter> rest, List<IssueNumbers.Counter> chosen, List<IssueNumbers.Counter[]> result)
        {
            if (rest.Count == 0) { result.Add(chosen.ToArray()); return; }
            for (int i = 0; i < rest.Count; i++)
            {
                var next = new List<IssueNumbers.Counter>(rest); next.RemoveAt(i);
                var picked = new List<IssueNumbers.Counter>(chosen) { rest[i] };
                Permute(next, picked, result);
            }
        }

        // ------------------------------------------------------------------ INFO of delta review 4: more markers than counters on one number

        private static Dictionary<string, int> HeldAfter(IssueNumbers.IssueMarker[] markers, List<IssueNumbers.Renumber> plan)
        {
            var held = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in markers)
            {
                var r = plan.FirstOrDefault(p => p.MarkerUid == m.MarkerUid);
                string no = r == null ? m.IssueNo : r.NewIssueNo;
                int n; held.TryGetValue(no, out n); held[no] = n + 1;
            }
            return held;
        }

        /// <summary>
        /// A renumber never CREATES a shared number, over ALL markers once the plan is applied (not only the plan's entries): every new number is held by
        /// exactly one marker afterwards, and no number is held by more markers after the plan than before it. Three markers on one number and two counters
        /// of the user are the case that gave m2 and m3 the same new number (RenumberPlan reused the last counter for the surplus).
        /// </summary>
        private static void RenumberNeverCreatesASharedNumber(Action<string, bool?> check)
        {
            Func<string, IssueNumbers.IssueMarker> on = uid => new IssueNumbers.IssueMarker { MarkerUid = uid, IssueNo = "JD-0001", ReportedBy = "jd" };
            var three = new[] { on("m1"), on("m2"), on("m3") };
            var counters = new[] { C("a", "jd", "JD"), C("b", "jd", "JD") };
            var plan = IssueNumbers.RenumberPlan(counters, three);
            var after = HeldAfter(three, plan);
            string shown = string.Join(",", plan.Select(p => p.MarkerUid + ">" + p.NewIssueNo));
            bool newOnesAlone = plan.All(p => after[p.NewIssueNo] == 1);
            bool noneMoreShared = after.All(kv => kv.Value <= (kv.Key == "JD-0001" ? 3 : 1));
            check(P + "INFO a renumber never creates a shared number: three markers on one number, two counters of the user (plan " + shown + "): every new number is held by one marker afterwards",
                newOnesAlone);
            check(P + "INFO ...and no number is held by more markers after the plan than before it (" + string.Join(", ", after.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " x" + kv.Value)) + ")", noneMoreShared);

            // The same property over the counters with no UniqueId, in every order, with two markers on the number.
            var two = new[] { on("m1"), on("m2") };
            int bad = 0, total = 0;
            foreach (var order in Permutations(new[] { C(null, "jd", "JD"), C("", "jd", "JD"), C("A", "jd", "JD") }))
            {
                total++;
                var p2 = IssueNumbers.RenumberPlan(order, two);
                var a2 = HeldAfter(two, p2);
                if (!(p2.All(p => a2[p.NewIssueNo] == 1) && a2.All(kv => kv.Value <= (kv.Key == "JD-0001" ? 2 : 1)))) bad++;
            }
            check(P + "INFO ...also for counters with no UniqueId, an empty one and one called A, in every order (" + bad + " of " + total + " orders create a shared number)", bad == 0 && total == 6);

            // KNOWN LIMITATION, not a feature (recorded for G2): a number already shared before the renumber is left as found.
            int leftover; after.TryGetValue("JD-0001", out leftover);
            check(P + "INFO known limitation, recorded for G2: of three markers on one number and two counters, RenumberPlan places one marker (" + shown + "); the third has no counter to move to and is left as found, so two markers still share JD-0001 ("
                + leftover + " hold it). Not a feature: a marker it cannot place keeps its number",
                plan.Count == 1 && plan[0].MarkerUid == "m2" && plan[0].NewIssueNo == "JD2-0001" && leftover == 2);
        }

        // ------------------------------------------------------------------ L3: the last number

        /// <summary>DR3-L3. When the counter stops at int.MaxValue because the number before it is held, the last number is free and is used.</summary>
        private static void LastIssueNumber(Action<string, bool?> check)
        {
            var held = new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = Rec(1, "JD-2147483646", "jdoe"), Access = MarkerAccess.Free } };
            var plan = Build(new[] { Rw(1), Rw(5) }, held, "jdoe", C("c-jd", "jdoe", "JD", int.MaxValue - 1), null);
            string made = plan.Creations.Count == 1 ? plan.Creations[0].Record.IssueNo : "(" + plan.Creations.Count + " creations)";
            check(P + "L3 a counter one below the limit whose next number is held uses the last number, int.MaxValue, for the next marker (" + made + ", not marked: " + plan.NotMarked.Count + ")",
                plan.Creations.Count == 1 && plan.Creations[0].Record.IssueNo == "JD-2147483647" && plan.NotMarked.Count == 0 && plan.CounterAfter.Next == int.MaxValue);

            var both = new[]
            {
                new MarkerPlan.Existing { MarkerUid = "m1", Record = Rec(1, "JD-2147483646", "jdoe"), Access = MarkerAccess.Free },
                new MarkerPlan.Existing { MarkerUid = "m5", Record = Rec(5, "JD-2147483647", "jdoe"), Access = MarkerAccess.Free }
            };
            var next = Build(new[] { Rw(1), Rw(5), Rw(6) }, both, "jdoe", plan.CounterAfter, null);
            check(P + "L3 control: with the last number held too, the next row is not marked and no number is issued twice (" + next.Creations.Count + " creations, not marked: " + next.NotMarked.Count + ")",
                next.Creations.Count == 0 && next.NotMarked.Count == 1 && next.NotMarked[0].Key == "k6" && next.NotMarked[0].Reason == "no free issue number");
        }

        // ------------------------------------------------------------------ L4: a filtered self-test run says so

        private static void FilteredRunHeadline(Action<string, bool?> check)
        {
            check(P + "L4 control: a full run's headline is the old text, byte for byte",
                RunHeadline.Of(0, 5, 1, null) == "ALL PASS  (5 passed, 0 failed, 1 skipped)" && RunHeadline.Of(2, 5, 1, null) == "2 FAILED  (5 passed, 2 failed, 1 skipped)"
                && RunHeadline.Of(0, 5, 1, "") == "ALL PASS  (5 passed, 0 failed, 1 skipped)");
            string pass = RunHeadline.Of(0, 3, 0, "markers-collect");
            string fail = RunHeadline.Of(1, 3, 0, "markers-collect");
            check(P + "L4 a run limited to one area names it in the headline (" + pass + ")", pass == "ALL PASS (areas: markers-collect)  (3 passed, 0 failed, 0 skipped)");
            check(P + "L4 ...also when it failed (" + fail + ")", fail == "1 FAILED (areas: markers-collect)  (3 passed, 1 failed, 0 skipped)");
            var count = new Regex(@"\((\d+) passed,\s*(\d+) failed,\s*(\d+) skipped\)");
            var verdict = new Regex(@"^(ALL PASS|\d+ FAILED)");
            check(P + "L4 ...and the in-Revit run scripts still read the counts and the verdict from it",
                count.IsMatch(pass) && count.Match(pass).Groups[1].Value == "3" && count.IsMatch(fail) && verdict.IsMatch(pass) && verdict.IsMatch(fail));
        }

        /// <summary>A SOURCE-STRUCTURE check of the in-Revit run script, which is not part of this repository, so here it always skips.</summary>
        private static void FilteredRunFileName(Action<string, bool?> check)
        {
            string root = R8LiveChecks.RepoRoot();
            string text = root == null ? null : R8LiveChecks.ReadSource(root, @"dev\in-revit-run-script");
            if (text == null)
            {
                check(P + "L4 a run with -Areas writes its result to another file than a full run (the in-Revit run script is not part of this repository)", (bool?)null);
                return;
            }
            string fn = R8LiveChecks.Between(text, "function Get-RunResultName", "\n}");
            check(P + "L4 the in-Revit run script names the result file in one function that adds -areas-<name> only when -Areas is given",
                fn != null && fn.Contains("run-job-{0}-{1}") && fn.Contains("-areas-") && fn.Contains("$AreasName"));
            check(P + "L4 ...and the result path is built with it",
                text.Contains("$resultPath = Join-Path $outDir (Get-RunResultName"));
        }
    }
}
