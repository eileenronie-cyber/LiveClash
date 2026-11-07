using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 7: the plan's defaults for missing texts, the repair of issue numbers, the choice among several pending triages,
    /// the decisions a merge keeps, the per-set counts and the words of the pending messages. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationPlan4Checks
    {
        private const string P = "markers mutation planner: ";
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Defaults(check);
            Access(check);
            Clone(check);
            NoIds(check);
            Repair(check);
            Numbers(check);
            Creation(check);
            Pending(check);
            Merging(check);
            Flags(check);
            SetCounts(check);
            Finish(check);
        }

        // ------------------------------------------------------------------ builders

        private static string[] Texts(params string[] items) { return items; }
        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

        private static MarkerPlan.Row Rw(int n)
        {
            return new MarkerPlan.Row
            {
                Key = "k" + n, HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, Live = true, Verdict = PlanVerdict.Clashing, RuleClearanceMm = 25,
                Severity = "Hard", HasPoint = true, X = n, SetName = "Set A"
            };
        }

        private static MarkerRecord Rec(int n, string issue)
        {
            return new MarkerRecord
            {
                Key = "k" + n, HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, ClashId = ClashIdOf.Of("h" + n, "", "o" + n), IssueNo = issue, ReportedBy = "jdoe",
                ReportedAt = Today, Severity = "Hard", ClearanceMm = 25, SetName = "Set A", X = n, Seq = 1, LastCheckedAt = Today, LastCheckedBy = "jdoe"
            };
        }

        private static MarkerPlan.Existing Ex(string uid, MarkerRecord r, MarkerAccess access = MarkerAccess.Free)
        {
            return new MarkerPlan.Existing { MarkerUid = uid, Record = r, Access = access };
        }

        private static IssueNumbers.Counter Own() { return new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }; }

        private static MarkerPlan.Result Build(IEnumerable<MarkerPlan.Row> rows, IEnumerable<MarkerPlan.Existing> markers, IEnumerable<MarkerPlan.Pending> pending = null, IssueNumbers.Counter counter = null, IEnumerable<IssueNumbers.Counter> all = null, Action<MarkerPlan.Options> options = null)
        {
            var input = new MarkerPlan.Input
            {
                Rows = (rows ?? Array.Empty<MarkerPlan.Row>()).ToList(), Markers = (markers ?? Array.Empty<MarkerPlan.Existing>()).ToList(), Pending = (pending ?? Array.Empty<MarkerPlan.Pending>()).ToList(),
                Counter = counter ?? Own(), AllCounters = (all ?? Array.Empty<IssueNumbers.Counter>()).ToList(), Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            };
            if (options != null) options(input.Options);
            return MarkerPlan.Build(input);
        }

        // ------------------------------------------------------------------ no user, no counter, a counter of another name

        private static void Defaults(Action<string, bool?> check)
        {
            var none = MarkerPlan.Build(new MarkerPlan.Input { Rows = new List<MarkerPlan.Row> { Rw(1) }, Options = new MarkerPlan.Options { Today = Today, UserName = null }, Counter = null });
            var rec = none.Creations[0].Record;
            check(P + "no user name and no counter: the marker is reported by nobody, the number takes the prefix of no name, and the counter written back has no owner",
                rec.ReportedBy == "" && rec.LastCheckedBy == "" && rec.IssueNo == "XX-0001" && none.CounterAfter.Uid == "" && none.CounterAfter.UserName == "" && none.CounterAfter.Prefix == "XX" && none.CounterAfter.Next == 2);
            var other = Build(new[] { Rw(1) }, null, null, new IssueNumbers.Counter { Uid = "u-9", UserName = "kdoe", Prefix = "KD", Next = 7 });
            check(P + "a counter that is given keeps its owner, its name and its prefix and moves on by one",
                other.Creations[0].Record.IssueNo == "KD-0007" && other.CounterAfter.Uid == "u-9" && other.CounterAfter.UserName == "kdoe" && other.CounterAfter.Prefix == "KD" && other.CounterAfter.Next == 8);
            var bare = Build(new[] { Rw(1) }, null, null, new IssueNumbers.Counter { Uid = "u-9", UserName = "kdoe", Prefix = "", Next = 3 });
            check(P + "...and one with no prefix takes the refreshing user's", bare.Creations[0].Record.IssueNo == "JD-0003" && bare.CounterAfter.Prefix == "JD");
        }

        // ------------------------------------------------------------------ why a marker was passed over

        private static void Access(Action<string, bool?> check)
        {
            var hurt = Rec(1, "JD-0001");
            var unreadable = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = hurt, Access = MarkerAccess.Unreadable, OffPlace = true } });
            check(P + "a marker the user cannot read that would otherwise change is passed over as an unreadable record", unreadable.PassedOver.Count == 1 && unreadable.PassedOver[0].Reason == "unreadable record" && unreadable.Changes.Count == 0);
            var bareHeld = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m2", Record = null, Access = MarkerAccess.Free }, new MarkerPlan.Existing { MarkerUid = "m3", Record = null, Access = MarkerAccess.HeldByOther } });
            check(P + "...and a marker with no record is 'unreadable record' when free and 'held by another user' when held with no name", bareHeld.PassedOver.Select(x => x.Reason).SequenceEqual(Texts("unreadable record", "held by another user")));
            var dupes = Build(null, new[] { Ex("m1", Rec(1, "JD-0001")), new MarkerPlan.Existing { MarkerUid = "m2", Record = Rec(1, "JD-0002"), Access = MarkerAccess.Unreadable } });
            check(P + "a duplicate that cannot be read is passed over with that reason and kept", dupes.PassedOver.Count == 1 && dupes.PassedOver[0].Reason == "duplicate: unreadable record" && dupes.Removals.Count == 0);
        }

        // ------------------------------------------------------------------ the copy the plan edits

        private static bool SameFields(MarkerRecord a, MarkerRecord b)
        {
            return a.Key == b.Key && a.HostUid == b.HostUid && a.LinkUid == b.LinkUid && a.OtherUid == b.OtherUid && a.ClashId == b.ClashId && a.IssueNo == b.IssueNo && a.Status == b.Status && a.Note == b.Note
                && a.AssignedTo == b.AssignedTo && a.ReportedBy == b.ReportedBy && a.ChangedBy == b.ChangedBy && a.Severity == b.Severity && a.SetName == b.SetName && a.GroupLabel == b.GroupLabel
                && a.ResolvedBy == b.ResolvedBy && a.ChangedAt == b.ChangedAt && a.ResolvedAt == b.ResolvedAt && a.ReportedAt == b.ReportedAt;
        }

        private static void Clone(Action<string, bool?> check)
        {
            var full = Rec(1, "JD-0001");
            full.LinkUid = "lu"; full.Note = "note"; full.AssignedTo = "asmith"; full.ChangedBy = "kdoe"; full.ChangedAt = Today; full.Status = MarkerStatus.Coordinated; full.GroupLabel = "JD-0001 (+1)";
            full.ResolvedBy = "rb"; full.ResolvedAt = Today.AddDays(-1);
            full.ClashId = ClashIdOf.Of("h1", "lu", "o1");
            var kept = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = full, WrongWorkset = true } });
            check(P + "a marker the plan edits keeps every text it had (the copy carries them all)", kept.Changes.Count == 1 && SameFields(kept.Changes[0].Before, kept.Changes[0].After) && kept.Changes[0].Fields.SequenceEqual(Texts("Workset")));

            var blank = new MarkerRecord { Key = null, HostUid = "h1", LinkUid = null, OtherUid = "o1", ClashId = null, IssueNo = null, Note = null, AssignedTo = null, ReportedBy = null, ChangedBy = null, Severity = null, SetName = null, GroupLabel = null, ResolvedBy = null, LastCheckedBy = null, Seq = 1 };
            var nothing = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = blank, WrongWorkset = true } });
            var after = nothing.Changes[0].After;
            check(P + "...and a marker with no text at all gets empty texts in the copy, never a placeholder",
                after.Key == "" && after.LinkUid == "" && after.ClashId == "" && after.IssueNo == "" && after.Note == "" && after.AssignedTo == "" && after.ReportedBy == "" && after.ChangedBy == "" && after.Severity == "" && after.SetName == ""
                && after.GroupLabel == "" && after.ResolvedBy == "" && after.HostUid == "h1" && after.OtherUid == "o1");
        }

        // ------------------------------------------------------------------ markers that name no pair

        private static void NoIds(Action<string, bool?> check)
        {
            var a = Rec(1, "JD-0001"); a.HostUid = ""; a.OtherUid = ""; a.ClashId = "";
            var b = Rec(2, "JD-0002"); b.HostUid = ""; b.OtherUid = ""; b.ClashId = "";
            var plan = Build(null, new[] { Ex("m1", a), Ex("m2", b) });
            check(P + "two markers that name no pair are not duplicates of each other and are not merged", plan.Merges == 0 && plan.Removals.Count == 0 && plan.PassedOver.Count == 0);
        }

        // ------------------------------------------------------------------ issue numbers repaired

        private static void Repair(Action<string, bool?> check)
        {
            Func<int, string, string, MarkerPlan.Existing> m = (n, uid, issue) => Ex(uid, WithBy(Rec(n, issue), n == 1 || n == 2 || n == 5 ? "jdoe" : "kdoe"));
            // m1 and m2 are the refreshing user's, on two old prefixes; m3 and m4 are another user's with the same two numbers; m5 and m6 hold a number that is no issue number at all
            var markers = new[] { m(1, "m1", "KD-0005"), m(2, "m2", "XX-0005"), m(3, "m3", "KD-0005"), m(4, "m4", "XX-0005"), m(5, "m5", "weird"), m(6, "m6", "weird") };
            var forward = Build(null, markers);
            var backward = Build(null, Enumerable.Reverse(markers));
            check(P + "an own marker on another prefix whose number another marker also holds takes the counter's prefix, the lowest marker first; the next one that would take the same number stays",
                forward.Changes.Count == 1 && forward.Changes[0].MarkerUid == "m1" && forward.Changes[0].After.IssueNo == "JD-0005" && forward.Changes[0].After.MergedIssueNos.SequenceEqual(Texts("KD-0005"))
                && backward.Changes.Count == 1 && backward.Changes[0].MarkerUid == "m1");
            check(P + "...and a number that is no issue number is left alone", !forward.Changes.Any(c => c.MarkerUid == "m5" || c.MarkerUid == "m6"));

            // the refreshing user's counter loses a prefix he shares with another user's: his markers on it take the new prefix with the same number
            var mine = WithBy(Rec(1, "JD-0004"), "jdoe");
            var already = WithBy(Rec(2, "JD-0005"), "jdoe");
            already.MergedIssueNos.Add("JD-0005");
            var losing = new IssueNumbers.Counter { Uid = "u-zz", UserName = "jdoe", Prefix = "JD", Next = 5 };
            var winner = new IssueNumbers.Counter { Uid = "u-aa", UserName = "kdoe", Prefix = "JD", Next = 3 };
            var missingOwn = Build(null, new[] { Ex("m1", mine), Ex("m2", already) }, null, losing, new[] { winner });
            check(P + "the user's own counter is added to the list of counters when it is missing, so a prefix collision is found",
                missingOwn.PrefixMove != null && missingOwn.PrefixMove.Uid == "u-zz" && missingOwn.PrefixMove.OldPrefix == "JD" && missingOwn.PrefixMove.NewPrefix == "JD2" && missingOwn.CounterAfter.Prefix == "JD2");
            var c1 = missingOwn.Changes.Single(c => c.MarkerUid == "m1");
            var c2 = missingOwn.Changes.Single(c => c.MarkerUid == "m2");
            check(P + "...his markers on the old prefix keep their number under the new one and remember the old; an old number already remembered is not added twice",
                c1.After.IssueNo == "JD2-0004" && c1.After.MergedIssueNos.SequenceEqual(Texts("JD-0004")) && c2.After.IssueNo == "JD2-0005" && c2.After.MergedIssueNos.SequenceEqual(Texts("JD-0005")));
            var listed = Build(null, new[] { Ex("m1", mine) }, null, losing, new IssueNumbers.Counter[] { winner, null, losing });
            check(P + "...and a counter list with a missing entry and the user's own counter already in it gives the same answer without a failure", listed.PrefixMove != null && listed.PrefixMove.NewPrefix == "JD2" && listed.Changes.Single().After.IssueNo == "JD2-0004");
            var alone = Build(null, new[] { Ex("m1", mine) }, null, losing, new[] { losing });
            check(P + "a list with only the user's own counter finds no collision", alone.PrefixMove == null && alone.Changes.Count == 0);
        }

        private static MarkerRecord WithBy(MarkerRecord r, string by)
        {
            r.ReportedBy = by;
            return r;
        }

        // ------------------------------------------------------------------ numbers handed out

        private static void Numbers(Action<string, bool?> check)
        {
            var held = new[] { Ex("m1", Rec(1, "JD-0001")), Ex("m2", Rec(2, "JD-0002")) };
            var next = Build(new[] { Rw(1), Rw(2), Rw(3) }, held);
            check(P + "a new marker never takes a number a marker already holds: the counter skips past them", next.Creations.Count == 1 && next.Creations[0].Record.IssueNo == "JD-0003" && next.CounterAfter.Next == 4);

            var a = Rw(1); a.RememberedIssueNo = "KD-0007";
            var b = Rw(2); b.RememberedIssueNo = "KD-0007";
            var twice = Build(new[] { b, a }, null);
            var first = twice.Creations.Single(c => c.Triple == Tri(1));
            var second = twice.Creations.Single(c => c.Triple == Tri(2));
            check(P + "a remembered number that two new markers ask for goes to the first in order; the second gets a fresh number and is marked as replaced",
                first.Record.IssueNo == "KD-0007" && !first.IssueNoReplaced && second.Record.IssueNo == "JD-0001" && second.IssueNoReplaced);
        }

        // ------------------------------------------------------------------ the new marker

        private static void Creation(Action<string, bool?> check)
        {
            var bare = new MarkerPlan.Row
            {
                Key = null, HostUid = "h1", LinkUid = null, OtherUid = "o1", Live = true, Verdict = PlanVerdict.Clashing, HasPoint = true, Severity = null, SetName = null, GroupLabel = null,
                AlsoSets = new List<string> { "Set C", "Set B", "Set B" }, Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = null, AssignedTo = null, By = null, At = null }
            };
            var made = Build(new[] { bare }, null).Creations[0].Record;
            check(P + "a new marker from a row with no texts has empty texts, the set '(no set)', its seed's time as today, and its other sets once each in order",
                made.Key == "" && made.LinkUid == "" && made.Severity == "" && made.GroupLabel == "" && made.Note == "" && made.AssignedTo == "" && made.ChangedBy == "" && made.SetName == MarkerPlan.NoSetName
                && made.ChangedAt == Today && made.Status == MarkerStatus.Ignored && made.AlsoSets.SequenceEqual(Texts("Set B", "Set C")));

            var rows = new List<MarkerPlan.Row>();
            string[] sev = { "", "Unverified", "Clearance", "Hard" };
            for (int i = 0; i < 4; i++) { var r = Rw(10 + i); r.Severity = sev[i]; r.Key = "k"; rows.Add(r); }
            var ordered = Build(rows, null).Creations;
            check(P + "new markers are numbered by severity: hard, clearance, unverified, then any other", ordered.Select(c => c.Triple).SequenceEqual(new[] { Tri(13), Tri(12), Tri(11), Tri(10) }) && ordered.Select(c => c.Record.IssueNo).SequenceEqual(Texts("JD-0001", "JD-0002", "JD-0003", "JD-0004")));

            var keyless = Rw(30); keyless.Key = null;
            var keyed = Rw(20); keyed.Key = "A";
            var byKey = Build(new[] { keyed, keyless }, null).Creations;
            check(P + "...then by key, a missing key first, then by pair", byKey.Select(c => c.Triple).SequenceEqual(new[] { Tri(30), Tri(20) }));

            var liveInGroup = Rw(40); liveInGroup.GroupKey = "g";
            var decided = Rw(41); decided.GroupKey = "g"; decided.Live = false; decided.GroupLabel = "OLD (+1)"; decided.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, By = "kdoe", At = Today };
            var partners = Build(new[] { liveInGroup, decided }, null).Creations;
            check(P + "a decided row that is not live keeps the group label it carries, whatever group the live rows form", partners.Single(c => c.Triple == Tri(41)).Record.GroupLabel == "OLD (+1)");
        }

        // ------------------------------------------------------------------ several pending triages for one marker

        private static void Pending(Action<string, bool?> check)
        {
            Func<long, string, string, string, MarkerPlan.Pending> pend = (seq, by, note, assignee) => new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = seq, By = by, Note = note, AssignedTo = assignee };
            Func<MarkerPlan.Pending[], string> winner = list =>
            {
                var rec = Rec(1, "JD-0001"); rec.Seq = 2;
                var plan = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = rec, WrongWorkset = true } }, list);
                var c = plan.Changes.Single().After;
                return c.Note + "/" + c.AssignedTo + "/" + c.ChangedBy;
            };
            var older = pend(1, "u", "old", null); var newer = pend(2, "u", "new", null);
            check(P + "of two pending triages for one marker the one made at the higher Seq is used, whatever the list order", winner(new[] { older, newer }) == "new//u" && winner(new[] { newer, older }) == "new//u");
            var byA = pend(2, "a", "from a", null); var byB = pend(2, "b", "from b", null);
            check(P + "...at one Seq the author that sorts first", winner(new[] { byA, byB }) == "from a//a" && winner(new[] { byB, byA }) == "from a//a");
            var noBy = pend(2, null, "from nobody", null); var withBy = pend(2, "A", "from A", null);
            check(P + "...a missing author before any name", winner(new[] { noBy, withBy }) == "from nobody//jdoe" && winner(new[] { withBy, noBy }) == "from nobody//jdoe");
            var noteA = pend(2, "u", "a", null); var noteB = pend(2, "u", "b", null);
            check(P + "...at one author the note that sorts first", winner(new[] { noteA, noteB }) == "a//u" && winner(new[] { noteB, noteA }) == "a//u");
            var noNote = pend(2, "u", null, "ax"); var aNote = pend(2, "u", "A", "ay");
            check(P + "...a missing note before any text", winner(new[] { noNote, aNote }) == "/ax/u" && winner(new[] { aNote, noNote }) == "/ax/u");
        }

        // ------------------------------------------------------------------ the decisions a merge keeps and loses

        private static void Merging(Action<string, bool?> check)
        {
            Func<string, MarkerStatus, string, string, string, string, MarkerPlan.Existing> dupe = (uid, status, note, assignee, changedBy, reportedBy) =>
            {
                var r = Rec(1, "JD-000" + uid[1]); r.Status = status; r.Note = note; r.AssignedTo = assignee; r.ChangedBy = changedBy; r.ReportedBy = reportedBy;
                return Ex(uid, r);
            };
            var sameA = dupe("m2", MarkerStatus.Coordinated, null, null, "", "jdoe");
            var sameB = dupe("m1", MarkerStatus.Coordinated, "", "", "", "jdoe");
            check(P + "two duplicates that made the same decision, one with a missing note and assignee and one with empty ones, lose nothing", Build(null, new[] { sameA, sameB }).LostDecisions.Count == 0);

            var keptDecision = dupe("m1", MarkerStatus.Ignored, "a", "", "", "jdoe");
            var lostDecision = dupe("m2", MarkerStatus.Pending, null, null, "", null);
            var lost = Build(null, new[] { keptDecision, lostDecision }).LostDecisions;
            check(P + "a different decision on the removed duplicate is reported with empty texts for what it did not have, and no author when it had no name",
                lost.Count == 1 && lost[0].RemovedUid == "m2" && lost[0].KeptUid == "m1" && lost[0].Status == MarkerStatus.Pending && lost[0].Note == "" && lost[0].AssignedTo == "" && lost[0].By == "");
            var changedBy = dupe("m3", MarkerStatus.Pending, "b", "x", "kdoe", "asmith");
            check(P + "...and with the author of its last change when it had one", Build(null, new[] { keptDecision, changedBy }).LostDecisions.Single().By == "kdoe");

            var withNote = dupe("m1", MarkerStatus.New, "x", "", "", "jdoe");
            var reopened = dupe("m2", MarkerStatus.Reopened, "", "", "kdoe", "jdoe");
            check(P + "a removed duplicate with no decision of its own (a re-test status, an empty note, an empty assignee) loses nothing of the kept marker's", Build(null, new[] { withNote, reopened }).LostDecisions.Count == 0);
        }

        // ------------------------------------------------------------------ flags of a change

        private static void Flags(Action<string, bool?> check)
        {
            var rec = Rec(1, "JD-0001");
            var bare = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = rec, WrongWorkset = true } }).Changes.Single();
            check(P + "a change made only to put a marker in its workset says nothing else: not resolved, not reopened, not moved, not re-keyed",
                bare.WorksetFix && !bare.Resolved && !bare.Reopened && !bare.Moved && !bare.Rekeyed && !bare.PutBack);

            var held = Rec(2, "JD-0002"); held.Note = "same"; held.Seq = 3;
            var pending = new MarkerPlan.Pending { Triple = Tri(2), SeqAtPending = 3, Note = "same", By = "kdoe" };
            var noop = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m2", Record = held, WrongWorkset = true } }, new[] { pending }).Changes.Single();
            check(P + "a pending triage that says what the marker already says changes nobody's name on it", noop.After.ChangedBy == noop.Before.ChangedBy && noop.After.ChangedAt == noop.Before.ChangedAt && noop.Fields.SequenceEqual(Texts("Workset")));

            var seq5 = Rec(3, "JD-0003"); seq5.Seq = 5;
            var partial = new MarkerPlan.Pending { Triple = Tri(3), SeqAtPending = 5, Status = MarkerStatus.Ignored, StatusSeq = 5, Note = "n", NoteSeq = 4, AssignedTo = "a", AssignedSeq = 3, By = "kdoe" };
            var outcome = Build(null, new[] { Ex("m3", seq5) }, new[] { partial }).Pending.Single();
            check(P + "a pending triage of which only the status is current says which parts were left out, in the order status, note, assignee",
                outcome.Kind == PendingKind.Applied && outcome.NotApplied.SequenceEqual(Texts("note", "assignee"))
                && outcome.Message == "Part of your pending triage was not applied: the note, assignee changed under you (the model wins); the rest was written to the marker.");
            var one = new MarkerPlan.Pending { Triple = Tri(3), SeqAtPending = 5, Status = MarkerStatus.Ignored, StatusSeq = 5, Note = "n", NoteSeq = 4, By = "kdoe" };
            check(P + "...and with one part left out the message names it alone",
                Build(null, new[] { Ex("m3", seq5) }, new[] { one }).Pending.Single().Message == "Part of your pending triage was not applied: the note changed under you (the model wins); the rest was written to the marker.");
        }

        // ------------------------------------------------------------------ the per-set counts

        private static void SetCounts(Action<string, bool?> check)
        {
            var notRetested = Rw(1); notRetested.Verdict = PlanVerdict.NotRetested;
            var off = Build(new[] { notRetested }, new[] { Ex("m1", Rec(1, "JD-0001")) }, null, null, null, o => o.DisabledSets = new List<string> { "Set A" });
            var offCount = off.SetCounts.Single(s => s.Name == "Set A");
            check(P + "a marker in a set that is off, whose pair was not re-tested, is counted under 'not re-tested' and not as up to date", offCount.SetOff && offCount.NotRetested == 1 && offCount.UpToDate == 0);
            var on = Build(new[] { notRetested }, new[] { Ex("m1", Rec(1, "JD-0001")) });
            check(P + "...and in a set that is on it adds nothing there", on.SetCounts.Single(s => s.Name == "Set A").NotRetested == 0 && !on.SetCounts.Single(s => s.Name == "Set A").SetOff);

            var held = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = Rec(1, "JD-0001"), Access = MarkerAccess.HeldByOther, HeldBy = "kdoe", OffPlace = true } }, null, null, null, o => o.DisabledSets = new List<string> { "Set A" });
            check(P + "a marker passed over in a set that is off is counted under 'not re-tested'", held.SetCounts.Single(s => s.Name == "Set A").NotRetested == 1);

            var old = Rec(1, "JD-0001"); old.Status = MarkerStatus.Resolved; old.ResolvedAt = Today.AddDays(-90); old.ResolvedBy = "jdoe";
            var removed = Build(null, new[] { Ex("m1", old) }, null, null, null, o => o.RemoveRetention = true);
            check(P + "a marker removed by retention is counted nowhere", removed.Removals.Count == 1 && removed.SetCounts.Count == 0);

            var moved = Rw(1); moved.SetName = "Set B";
            var move = Build(new[] { moved }, new[] { Ex("m1", Rec(1, "JD-0001")) });
            check(P + "a marker that changes set is counted under the new set only", move.SetCounts.Select(s => s.Name).SequenceEqual(Texts("Set B")) && move.SetCounts[0].UpToDate == 1 && move.SetCounts[0].InPanel == 1);

            var decided = Rw(2); decided.Live = false; decided.SetName = "Set Z"; decided.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, By = "kdoe", At = Today };
            var made = Build(new[] { decided }, null);
            check(P + "a marker made from a decided pair that is not live is counted up to date in its set though nothing is in the panel", made.SetCounts.Count == 1 && made.SetCounts[0].Name == "Set Z" && made.SetCounts[0].UpToDate == 1 && made.SetCounts[0].InPanel == 0);

            var noPoint = Rw(3); noPoint.HasPoint = false; noPoint.SetName = "Set Y";
            var seen = Build(new[] { noPoint }, null);
            check(P + "a live clash that gets no marker still counts in its set's panel", seen.SetCounts.Single().Name == "Set Y" && seen.SetCounts.Single().InPanel == 1 && seen.SetCounts.Single().UpToDate == 0);
        }

        // ------------------------------------------------------------------ the end of the plan

        private static void Finish(Action<string, bool?> check)
        {
            var empty = Build(null, null);
            check(P + "nothing in and nothing out needs no dialog", !empty.NeedsDialog && empty.Creations.Count == 0 && empty.ExistingMarkers == 0);

            var unlit = Rw(1); unlit.Key = null; unlit.HostUid = null;
            var noPosition = Rw(2); noPosition.Key = "A"; noPosition.HasPoint = false;
            var noId = Rw(3); noId.Key = "A"; noId.HostUid = null;
            var notMarked = Build(new[] { noPosition, noId, unlit }, null).NotMarked;
            check(P + "the clashes that get no marker are listed by key (a missing key first) and then by reason",
                notMarked.Select(x => (x.Key ?? "~") + "/" + x.Reason).SequenceEqual(Texts("~/no UniqueId", "A/no UniqueId", "A/no position")));

            var reasons = new MarkerPlan.Result();
            reasons.PassedOver.Add(new MarkerPlan.Reasoned { MarkerUid = "m1", Reason = "b reason" });
            reasons.PassedOver.Add(new MarkerPlan.Reasoned { MarkerUid = "m2", Reason = "a reason" });
            reasons.PassedOver.Add(new MarkerPlan.Reasoned { MarkerUid = "m3", Reason = "b reason" });
            var counts = reasons.ToCounts();
            check(P + "the summary counts group the reasons by text in ordinal order", counts.PassedOver.Select(r => r.Text + "=" + r.Count).SequenceEqual(Texts("a reason=1", "b reason=2")));
        }
    }
}
