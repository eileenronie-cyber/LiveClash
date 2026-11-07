using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, delta-review finding D-L4: the merged pending entry keeps the Seq of each field, and the plan applies a field
    /// only while the marker is still at the Seq that field was set at. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/DELTA-L4-LOG.md.
    /// </summary>
    public static class MarkerDeltaL4Checks
    {
        private const string P = "markers delta L4: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Reader(check);
            Planner(check);
            Assembler(check);
            Fuzz(check);
        }

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string[] Texts(params string[] items) { return items; }
        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }
        private static string Pl(int minute, long seq, MarkerStatus? status, string note, string assignee) { return LedgerExtras.PendingLine("k", T(minute), "jdoe", seq, status, note, assignee); }

        private static LedgerExtras.PendingEntry Open(params string[] lines)
        {
            LedgerExtras.PendingEntry e;
            LedgerExtras.Read(lines).Pending.TryGetValue("k", out e);
            return e;
        }

        // ------------------------------------------------------------------ the reader and the join

        private static void Reader(Action<string, bool?> check)
        {
            var merged = Open(Pl(1, 5, MarkerStatus.Ignored, null, null), Pl(2, 6, null, "sleeve", null));
            check(P + "a status entry at Seq 5 then a note entry at Seq 6 read as one entry: SeqAtPending 6, StatusSeq 5, NoteSeq 6, no AssignedSeq",
                merged != null && merged.SeqAtPending == 6 && merged.StatusSeq == 5 && merged.NoteSeq == 6 && merged.AssignedSeq == null && merged.Status == MarkerStatus.Ignored && merged.Note == "sleeve");

            var single = Open(Pl(1, 4, null, "n", ""));
            check(P + "a single entry carries its Seq on each field it set and nothing on the others (an unassign counts as set)",
                single != null && single.StatusSeq == null && single.NoteSeq == 4 && single.AssignedSeq == 4);

            var later = Open(Pl(1, 5, MarkerStatus.Ignored, null, null), Pl(2, 6, null, "n", null), Pl(3, 7, MarkerStatus.Coordinated, null, null));
            check(P + "of two status entries (Seq 5, then 7) the merged StatusSeq is the later one's; the note keeps its own", later != null && later.StatusSeq == 7 && later.NoteSeq == 6 && later.SeqAtPending == 7);

            var view = LedgerExtras.Read(new[] { Pl(1, 5, MarkerStatus.Ignored, null, null), Pl(2, 6, null, "sleeve", "asmith") });
            var mapped = PendingMap.Build(view, new Dictionary<string, string>(StringComparer.Ordinal) { { "k", Tri(1) } });
            var p = mapped.Pending.Count == 1 ? mapped.Pending[0] : null;
            check(P + "PendingMap.Build carries the three Seqs into the plan's Pending", p != null && p.SeqAtPending == 6 && p.StatusSeq == 5 && p.NoteSeq == 6 && p.AssignedSeq == 6);
        }

        // ------------------------------------------------------------------ the planner

        private static MarkerRecord Rec(long seq, MarkerStatus status, string note, string assignee)
        {
            return new MarkerRecord
            {
                Key = "k1", HostUid = "h1", LinkUid = "", OtherUid = "o1", HostId = 101, OtherId = 201, ClashId = ClashIdOf.Of("h1", "", "o1"), IssueNo = "JD-0001", Status = status, Note = note,
                AssignedTo = assignee, ReportedBy = "jdoe", ReportedAt = Today, Severity = "Hard", ClearanceMm = 25, SetName = "Set A", X = 1, LastCheckedAt = Today, LastCheckedBy = "jdoe", Seq = seq
            };
        }

        private static MarkerPlan.Result Plan(MarkerRecord marker, MarkerPlan.Pending pending)
        {
            var input = new MarkerPlan.Input { Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" } };
            input.Markers.Add(new MarkerPlan.Existing { MarkerUid = "m1", Record = marker, Access = MarkerAccess.Free });
            input.Pending.Add(pending);
            return MarkerPlan.Build(input);
        }

        private static void Planner(Action<string, bool?> check)
        {
            var mixed = new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = 6, Status = MarkerStatus.Ignored, StatusSeq = 5, Note = "sleeve", NoteSeq = 6, By = "jdoe" };
            var r = Plan(Rec(6, MarkerStatus.Coordinated, "", ""), mixed);
            var change = r.Changes.Count == 1 ? r.Changes[0] : null;
            var outcome = r.Pending.Count == 1 ? r.Pending[0] : null;
            check(P + "D-L4: marker at Seq 6 (Coordinated), pending {Ignored at 5, note at 6}: the note is written, the colleague's Coordinated stays, and the outcome is Applied naming the status as not applied",
                change != null && change.After.Status == MarkerStatus.Coordinated && change.After.Note == "sleeve" && outcome != null && outcome.Kind == PendingKind.Applied
                && outcome.NotApplied.SequenceEqual(Texts("status")) && outcome.Message.Contains("status"));

            var stale = Plan(Rec(7, MarkerStatus.Coordinated, "", ""), mixed);
            check(P + "every field stale: the whole triage is Dropped with the existing message, and the marker is unchanged",
                stale.Changes.Count == 0 && stale.Pending.Count == 1 && stale.Pending[0].Kind == PendingKind.Dropped && stale.Pending[0].Message.Contains("changed by someone else first"));

            var current = new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = 3, Status = MarkerStatus.Ignored, StatusSeq = 3, Note = "n", NoteSeq = 3, By = "jdoe" };
            var all = Plan(Rec(3, MarkerStatus.New, "", ""), current);
            check(P + "every field current: Applied, both written, nothing left out",
                all.Changes.Count == 1 && all.Changes[0].After.Status == MarkerStatus.Ignored && all.Changes[0].After.Note == "n" && all.Pending[0].Kind == PendingKind.Applied && all.Pending[0].NotApplied.Count == 0);

            var plain = new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = 4, Status = MarkerStatus.Ignored, By = "jdoe" };
            var plainOk = Plan(Rec(4, MarkerStatus.New, "", ""), plain);
            var plainStale = Plan(Rec(5, MarkerStatus.New, "", ""), plain);
            check(P + "a plan Pending with no per-field Seq behaves as before: applied at an equal Seq, dropped otherwise",
                plainOk.Changes.Count == 1 && plainOk.Pending[0].Kind == PendingKind.Applied && plainStale.Changes.Count == 0 && plainStale.Pending[0].Kind == PendingKind.Dropped);

            var empty = new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = 4, By = "jdoe" };
            var emptyOk = Plan(Rec(4, MarkerStatus.New, "", ""), empty);
            var emptyStale = Plan(Rec(5, MarkerStatus.New, "", ""), empty);
            check(P + "an entry that set no field: Applied with no change at an equal Seq, Dropped otherwise",
                emptyOk.Changes.Count == 0 && emptyOk.Pending[0].Kind == PendingKind.Applied && emptyStale.Pending[0].Kind == PendingKind.Dropped);
        }

        // ------------------------------------------------------------------ the assembler

        private static MarkerPlanAssembler.Written Written(MarkerPlan.Result plan, MarkerPlanAssembler.Outcome outcome)
        {
            var a = MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request { Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" } });
            return MarkerPlanAssembler.Lines(a, plan, outcome, "jdoe", T(60));
        }

        private static void Assembler(Action<string, bool?> check)
        {
            const string Partial = "Part of your pending triage was not applied: the status (the model wins).";
            var partly = new MarkerPlan.Result();
            var o = new MarkerPlan.PendingOutcome { Triple = Tri(1), MarkerUid = "m1", Kind = PendingKind.Applied, Message = Partial };
            o.NotApplied.Add("status");
            partly.Pending.Add(o);
            var full = new MarkerPlan.Result();
            full.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(2), MarkerUid = "m2", Kind = PendingKind.Applied, Message = "ok" });
            var notWritten = new MarkerPlanAssembler.Outcome();
            notWritten.NotWritten.Add("m1");
            var wp = Written(partly, null);
            var wf = Written(full, null);
            var wn = Written(partly, notWritten);
            check(P + "a partly applied outcome is in PendingNotApplied and Notes with its message; a fully applied one is not; a partly applied one whose marker was not written has the not-written text only",
                wp.PendingNotApplied.Count == 1 && wp.PendingNotApplied[0].Text == Partial && wp.Notes.Contains(Partial) && wf.PendingNotApplied.Count == 0 && wf.Notes.Count == 0
                && wn.PendingNotApplied.Count == 1 && wn.PendingNotApplied[0].Text.Contains("not written") && !wn.Notes.Contains(Partial));

            var lines = new List<string>
            {
                LedgerExtras.IssueLine("k1", T(1), "kdoe", "JD-0001"),
                LedgerExtras.PendingLine("k1", T(2), "jdoe", 5, MarkerStatus.Ignored, null, null),
                LedgerExtras.PendingLine("k1", T(3), "jdoe", 6, null, "sleeve by others", null)
            };
            var decisions = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int e, l, rp;
            ClashStatusReplay.Replay(lines, decisions, out e, out l, out rp);
            var marker = new MarkerPlan.Existing { MarkerUid = "m1", Record = Rec(6, MarkerStatus.Coordinated, "", ""), Access = MarkerAccess.Free };
            var req = new MarkerPlanAssembler.Request
            {
                Decisions = decisions, Extras = LedgerExtras.Read(lines), Markers = new List<MarkerPlan.Existing> { marker },
                OwnCounter = new IssueNumbers.Counter { Uid = "c", UserName = "jdoe", Prefix = "JD", Next = 5 }, Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            };
            var asm = MarkerPlanAssembler.Assemble(req);
            var plan = MarkerPlan.Build(asm.Input);
            var w = MarkerPlanAssembler.Lines(asm, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var after = LedgerExtras.Read(lines.Concat(w.Lines));
            var afterDecisions = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            ClashStatusReplay.Replay(lines.Concat(w.Lines), afterDecisions, out e, out l, out rp);
            check(P + "end to end through the ledger: status at Seq 5, a colleague's change (Seq 6), a note at Seq 6: the marker keeps the colleague's status, the note is written, one Done line closes the entry and the ledger follows the model",
                plan.Changes.Count == 1 && plan.Changes[0].After.Status == MarkerStatus.Coordinated && plan.Changes[0].After.Note == "sleeve by others" && plan.Pending.Count == 1 && plan.Pending[0].NotApplied.SequenceEqual(Texts("status"))
                && w.PendingClosed == 1 && !after.Pending.ContainsKey("k1") && w.PendingNotApplied.Count == 1 && afterDecisions.ContainsKey("k1") && afterDecisions["k1"].Kind == ClashStatusKind.Coordinated);
        }

        // ------------------------------------------------------------------ fuzz

        private static void Fuzz(Action<string, bool?> check)
        {
            int wrong = 0, seen = 0, partial = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var r = new Random(seed);
                long marker = 3 + r.Next(3);
                var rec = Rec(marker, MarkerStatus.Coordinated, "m", "m");
                var pend = new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = 3 + r.Next(3), By = "jdoe" };
                bool hasStatus = r.Next(100) < 60, hasNote = r.Next(100) < 60, hasAssignee = r.Next(100) < 60;
                if (hasStatus) { pend.Status = MarkerStatus.Ignored; if (r.Next(4) != 0) pend.StatusSeq = 3 + r.Next(3); }
                if (hasNote) { pend.Note = "p"; if (r.Next(4) != 0) pend.NoteSeq = 3 + r.Next(3); }
                if (hasAssignee) { pend.AssignedTo = "p"; if (r.Next(4) != 0) pend.AssignedSeq = 3 + r.Next(3); }

                var result = Plan(rec, pend);
                var outcome = result.Pending.Count == 1 ? result.Pending[0] : null;
                var change = result.Changes.Count == 1 ? result.Changes[0] : null;
                if (outcome == null) { wrong++; continue; }
                seen++;

                bool statusOk = hasStatus && (pend.StatusSeq ?? pend.SeqAtPending) == marker;
                bool noteOk = hasNote && (pend.NoteSeq ?? pend.SeqAtPending) == marker;
                bool assignOk = hasAssignee && (pend.AssignedSeq ?? pend.SeqAtPending) == marker;
                var left = new List<string>();
                if (hasStatus && !statusOk) left.Add("status");
                if (hasNote && !noteOk) left.Add("note");
                if (hasAssignee && !assignOk) left.Add("assignee");
                bool anySet = hasStatus || hasNote || hasAssignee;
                bool anyApplied = statusOk || noteOk || assignOk;
                PendingKind kind = anySet ? (anyApplied ? PendingKind.Applied : PendingKind.Dropped) : (pend.SeqAtPending == marker ? PendingKind.Applied : PendingKind.Dropped);
                if (kind == PendingKind.Applied && left.Count > 0) partial++;

                bool recordRight = true;
                if (anyApplied)
                {
                    recordRight = change != null && change.After.Status == (statusOk ? MarkerStatus.Ignored : MarkerStatus.Coordinated) && change.After.Note == (noteOk ? "p" : "m") && change.After.AssignedTo == (assignOk ? "p" : "m");
                }
                else recordRight = change == null;
                bool listRight = kind == PendingKind.Applied ? outcome.NotApplied.SequenceEqual(left) : outcome.NotApplied.Count == 0;
                if (outcome.Kind != kind || !recordRight || !listRight) wrong++;
            }
            check(P + "fuzz (300 cases): a field is applied exactly when its Seq equals the marker's (none set: the entry's Seq decides); the outcome and the record equal one worked out independently", wrong == 0 && seen == 300 && partial > 20);
        }
    }
}
