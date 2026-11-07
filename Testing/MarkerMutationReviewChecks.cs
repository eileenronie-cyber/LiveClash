using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 11d (B, C, D): the cases the fresh reader of the Stryker survivors found nothing checked: a user's first Refresh while
    /// others' counters exist, three more survivors it showed to be killable, and the issue-number limit. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationReviewChecks
    {
        private const string P = "markers mutation review: ";
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            FirstRefresh(check);
            Renumbering(check);
            Reader(check);
            Limit(check);
            Clearance(check);
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

        // ------------------------------------------------------------------ B: a user's first Refresh in a model where others have counters

        private static void FirstRefresh(Action<string, bool?> check)
        {
            MarkerPlan.Result plan = null;
            string failure = null;
            try { plan = Build(new[] { Rw(1) }, null, "jdoe", null, new[] { C("u1", "other", "OT") }); }
            catch (Exception ex) { failure = ex.GetType().Name; }
            check(P + "a user with no counter of their own yet, in a model where another user's counter exists, gets a plan (no failure)", failure == null && plan != null);
            check(P + "...the new marker takes the prefix of the user's name, and the counter written back has no owner yet",
                plan != null && plan.Creations.Count == 1 && plan.Creations[0].Record.IssueNo == "JD-0001" && plan.CounterAfter.Prefix == "JD" && plan.CounterAfter.Uid == "" && plan.CounterAfter.Next == 2 && plan.PrefixMove == null);
        }

        // ------------------------------------------------------------------ C: renumbering after a move

        private static void Renumbering(Action<string, bool?> check)
        {
            var all = new[] { C("a", "x", "JD"), C("b", "jd", "JD"), C("c", "y", "AB"), C("d", "jd", "AB") };
            var marker = new MarkerPlan.Existing { MarkerUid = "m1", Record = Rec(1, "AB-0005", "jd"), Access = MarkerAccess.Free };
            var plan = Build(null, new[] { marker }, "jd", C("b", "jd", "JD"), all);
            check(P + "the refreshing user's own counter moves JD to JD2; a marker of theirs on another prefix whose counter was moved by the same rule (AB to AB2, another PC) is not renumbered by this user",
                plan.PrefixMove != null && plan.PrefixMove.NewPrefix == "JD2" && plan.Changes.Count == 0);

            // The old pin here (counters with no UniqueId, an empty one and one called A: both markers took JD2-0001) asserted a duplicate issue number. Delta review 3 (DR3-L2) replaced it:
            // MarkerDr3Checks.L2 asserts the invariant (no two markers leave one plan with one new number), and the fix keys RenumberPlan by counter object, not by Uid.
        }

        // ------------------------------------------------------------------ C: a format that says "read" and gives no record

        private sealed class LyingFormat : IMarkerFormat
        {
            public Guid Schema { get; } = new Guid("1f1f1f1f-1f1f-1f1f-1f1f-1f1f1f1f1f1f");
            public int Version { get { return 1; } }
            public bool TryRead(IDictionary<string, string> fields, out MarkerRecord record) { record = null; return true; }
        }

        private static void Reader(Action<string, bool?> check)
        {
            var lying = new LyingFormat();
            var result = new MarkerReader(lying).Read(lying.Schema, new Dictionary<string, string> { { "Status", "New" } });
            check(P + "a format that answers 'read' but gives no record is unreadable (Junk), not Ok with no record", result.Status == MarkerReadStatus.Junk && result.Record == null);
        }

        // ------------------------------------------------------------------ E: the text of a number in the order must be the round-trip text (it differs on .NET Framework 4.8)

        private static void Clearance(Action<string, bool?> check)
        {
            var longer = Rw(1); longer.RuleClearanceMm = 25.400000000000002;
            var shorter = Rw(1); shorter.RuleClearanceMm = 25.45;
            double one = Build(new[] { longer, shorter }, null, "jdoe", C("c-jd", "jdoe", "JD"), null).Creations.Single().Record.ClearanceMm;
            double two = Build(new[] { shorter, longer }, null, "jdoe", C("c-jd", "jdoe", "JD"), null).Creations.Single().Record.ClearanceMm;
            check(P + "two rows for one pair that differ only in a clearance written in 17 digits (25.400000000000002) and one in 4 (25.45) give the same plan in either order, taken from the row whose round-trip text is the shorter (on .NET Framework 4.8 the default format would put the other first)",
                one == 25.45 && two == 25.45);
        }

        // ------------------------------------------------------------------ D: the issue-number limit

        private static void Limit(Action<string, bool?> check)
        {
            var held = new[]
            {
                new MarkerPlan.Existing { MarkerUid = "m1", Record = Rec(1, "JD-2147483646", "jdoe"), Access = MarkerAccess.Free },
                new MarkerPlan.Existing { MarkerUid = "m2", Record = Rec(2, "JD-2147483647", "jdoe"), Access = MarkerAccess.Free }
            };
            var plan = Build(new[] { Rw(1), Rw(2), Rw(5) }, held, "jdoe", C("c-jd", "jdoe", "JD", int.MaxValue - 1), null);
            check(P + "a counter at the limit of the numbers whose next two are held makes no marker, never one with a number a marker already holds",
                plan.Creations.Count == 0 && plan.NotMarked.Count == 1 && plan.NotMarked[0].Key == "k5" && plan.NotMarked[0].Reason == "no free issue number");
        }
    }
}
