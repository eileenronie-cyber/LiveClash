using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, independent-review fix A: the open pending entries of a key are one merged entry (M1, ledger half),
    /// and the Refresh summary gets the decisions that differ from the model and the triage that was not applied (M3, data side).
    /// Revit-free, so both tiers run it. Predictions and results: tests/evidence/clash-schedule/REVIEW-FIX-A-LOG.md.
    /// </summary>
    public static class MarkerReviewAChecks
    {
        private const string P = "markers review A: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Merged(check);
            Orders(check);
            Fuzz(check);
            HeldFlow(check);
            Notes(check);
        }

        private static int[] Ints(params int[] items) { return items; }
        private static string[] Texts(params string[] items) { return items; }
        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string Key(int n) { return "k" + n; }
        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }
        private static string Pl(int minute, long seq, string by, MarkerStatus? status, string note, string assignee) { return LedgerExtras.PendingLine("k", T(minute), by, seq, status, note, assignee); }

        private static LedgerExtras.PendingEntry Open(params string[] lines)
        {
            LedgerExtras.PendingEntry e;
            LedgerExtras.Read(lines).Pending.TryGetValue("k", out e);
            return e;
        }

        private static string Text(LedgerExtras.PendingEntry e)
        {
            return e == null ? "none" : string.Join("|", new[] { e.SeqAtPending.ToString(Inv), e.Status.HasValue ? e.Status.Value.ToString() : "~", e.Note ?? "~", e.AssignedTo ?? "~", e.By, e.WhenUtc.Ticks.ToString(Inv) });
        }

        // ------------------------------------------------------------------ the merged entry

        private static void Merged(Action<string, bool?> check)
        {
            var both = Open(Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null), Pl(2, 3, "jdoe", null, "sleeve by others", null));
            check(P + "a status entry then a note entry (same Seq, later clock) for one key read as ONE entry with the status AND the note (M1)",
                both != null && both.Status == MarkerStatus.Ignored && both.Note == "sleeve by others" && both.AssignedTo == null && both.SeqAtPending == 3);

            var withAssignee = Open(Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null), Pl(2, 3, "jdoe", null, null, "asmith"));
            check(P + "an earlier status and a later assignee-only entry read as status + assignee, note unset", withAssignee != null && withAssignee.Status == MarkerStatus.Ignored && withAssignee.AssignedTo == "asmith" && withAssignee.Note == null);

            var latest = Open(Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null), Pl(2, 3, "jdoe", null, "mid", null), Pl(3, 3, "jdoe", MarkerStatus.Coordinated, null, null));
            check(P + "each field is latest-wins on its own clock: the status from the later of two status entries, the note from the entry between them",
                latest != null && latest.Status == MarkerStatus.Coordinated && latest.Note == "mid");

            var unassign = Open(Pl(1, 3, "jdoe", MarkerStatus.Pending, null, "asmith"), Pl(2, 3, "jdoe", null, null, ""));
            check(P + "a later unassign beats an earlier assignee and stays empty (not unset), beside the earlier status", unassign != null && unassign.AssignedTo == "" && unassign.Status == MarkerStatus.Pending);

            var only = Open(Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null));
            check(P + "a field no open entry set stays unset", only != null && only.Note == null && only.AssignedTo == null);

            var done = LedgerExtras.DoneLine("k", T(5), "jdoe");
            var closedWhole = LedgerExtras.Read(new[] { Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null), Pl(2, 3, "jdoe", null, "n", null), done }).Pending.ContainsKey("k");
            var between = Open(Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null), LedgerExtras.DoneLine("k", T(2), "jdoe"), Pl(3, 3, "jdoe", null, "after", null));
            check(P + "a PendingDone after every entry closes the merged entry whole; one between two entries leaves only what came after it",
                !closedWhole && between != null && between.Note == "after" && between.Status == null);

            var seqs = Open(Pl(1, 5, "adoe", MarkerStatus.Ignored, null, null), Pl(2, 6, "bdoe", null, "n", null));
            check(P + "the merged entry carries the latest entry's Seq, author and time (R1, R2)", seqs != null && seqs.SeqAtPending == 6 && seqs.By == "bdoe" && seqs.WhenUtc == T(2) && seqs.Status == MarkerStatus.Ignored && seqs.Note == "n");
        }

        private static IEnumerable<List<string>> Permutations(List<string> items)
        {
            if (items.Count <= 1) { yield return items.ToList(); yield break; }
            for (int i = 0; i < items.Count; i++)
            {
                var rest = items.Where((x, j) => j != i).ToList();
                foreach (var p in Permutations(rest))
                {
                    p.Insert(0, items[i]);
                    yield return p;
                }
            }
        }

        private static void Orders(Action<string, bool?> check)
        {
            var lines = new List<string>
            {
                Pl(1, 3, "jdoe", MarkerStatus.Ignored, null, null), Pl(2, 3, "jdoe", null, "first", null), Pl(3, 4, "kdoe", null, null, "asmith"),
                Pl(4, 4, "kdoe", MarkerStatus.Coordinated, null, null), Pl(5, 4, "jdoe", null, "second", null)
            };
            var texts = new HashSet<string>(Permutations(lines).Select(p => Text(Open(p.ToArray()))), StringComparer.Ordinal);
            var expected = Open(lines.ToArray());
            check(P + "any file order of five pending lines (120 orders) gives the same merged entry",
                texts.Count == 1 && expected != null && expected.Status == MarkerStatus.Coordinated && expected.Note == "second" && expected.AssignedTo == "asmith" && expected.SeqAtPending == 4);
        }

        private sealed class Entry
        {
            public int Minute;
            public long Seq;
            public string By;
            public MarkerStatus? Status;
            public string Note;
            public string Assignee;
        }

        private static Entry RandomEntry(Random r, int minute)
        {
            return new Entry
            {
                Minute = minute, Seq = 1 + r.Next(5), By = r.Next(2) == 0 ? "jdoe" : "kdoe",
                Status = r.Next(2) == 0 ? (MarkerStatus?)(r.Next(2) == 0 ? MarkerStatus.Ignored : MarkerStatus.Coordinated) : null,
                Note = r.Next(2) == 0 ? "n" + r.Next(3) : null, Assignee = r.Next(3) == 0 ? (r.Next(2) == 0 ? "" : "asmith") : null
            };
        }

        private static string LineOf(Entry e) { return Pl(e.Minute, e.Seq, e.By, e.Status, e.Note, e.Assignee); }

        private static void Fuzz(Action<string, bool?> check)
        {
            int wrong = 0, notClosed = 0, orderDiffers = 0, seen = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var r = new Random(seed);
                int n = 1 + r.Next(5);
                var minutes = Enumerable.Range(1, 30).OrderBy(x => r.Next()).Take(n).ToList();
                var entries = minutes.Select(m => RandomEntry(r, m)).ToList();
                int? doneAt = r.Next(2) == 0 ? (int?)(1 + r.Next(30)) : null;
                var lines = entries.Select(LineOf).ToList();
                if (doneAt.HasValue) lines.Add(LedgerExtras.DoneLine("k", T(doneAt.Value), "jdoe"));

                var open = entries.Where(e => !doneAt.HasValue || T(e.Minute) > T(doneAt.Value)).OrderBy(e => e.Minute).ToList();
                var view = LedgerExtras.Read(lines);
                if (open.Count == 0)
                {
                    if (view.Pending.ContainsKey("k")) wrong++;
                    continue;
                }
                seen++;
                var last = open[open.Count - 1];
                var status = open.LastOrDefault(e => e.Status.HasValue);
                var note = open.LastOrDefault(e => e.Note != null);
                var assignee = open.LastOrDefault(e => e.Assignee != null);
                string model = string.Join("|", new[]
                {
                    last.Seq.ToString(Inv), status == null ? "~" : status.Status.Value.ToString(), note == null ? "~" : note.Note, assignee == null ? "~" : assignee.Assignee, last.By, T(last.Minute).Ticks.ToString(Inv)
                });
                LedgerExtras.PendingEntry got;
                view.Pending.TryGetValue("k", out got);
                if (Text(got) != model) wrong++;

                if (got != null)
                {
                    var closing = PendingMap.DoneLines(new[] { new MarkerPlan.PendingOutcome { Triple = "t", Kind = PendingKind.Applied } }, new Dictionary<string, string>(StringComparer.Ordinal) { { "t", "k" } }, view, "jdoe", T(r.Next(0, 40)));
                    if (LedgerExtras.Read(lines.Concat(closing)).Pending.ContainsKey("k")) notClosed++;
                }
            }
            check(P + "fuzz (300 histories of 1 to 5 entries with random fields and clocks, and a random PendingDone): the merged entry equals one worked out independently", wrong == 0 && seen > 150);
            check(P + "...and the Done line the plan's outcome writes closes the merged entry whole, whatever this PC's clock says", notClosed == 0 && seen > 150);

            for (int seed = 1; seed <= 120; seed++)
            {
                var r = new Random(seed + 700);
                int n = 2 + r.Next(4);
                var entries = Enumerable.Range(0, n).Select(i => RandomEntry(r, 1 + r.Next(3))).ToList();
                var lines = entries.Select(LineOf).ToList();
                var shuffled = lines.OrderBy(x => r.Next()).ToList();
                if (Text(Open(lines.ToArray())) != Text(Open(shuffled.ToArray()))) orderDiffers++;
            }
            check(P + "...and entries with the same clock (ties) still merge the same way whatever the file order", orderDiffers == 0);
        }

        // ------------------------------------------------------------------ the held-marker flow

        private static RowBuilder.Source Src(int n)
        {
            return new RowBuilder.Source
            {
                Key = Key(n), HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, Severity = "Hard", HasPoint = true, X = n, Y = 0, Z = 0,
                SetName = "Set A", GroupKey = "", HostName = "Pipe " + n, HostCategory = "Pipes", OtherName = "Duct " + n, OtherCategory = "Ducts", ModelName = "Current model", Level = "Level 1",
                RuleClearanceMm = 25, RuleInsulated = false
            };
        }

        private static MarkerRecord Rec(int n, string issue, MarkerStatus status, string note, long seq)
        {
            return new MarkerRecord
            {
                Key = Key(n), HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, ClashId = ClashIdOf.Of("h" + n, "", "o" + n), IssueNo = issue, Status = status, Note = note,
                ReportedBy = "jdoe", ReportedAt = Today, Severity = "Hard", ClearanceMm = 25, SetName = "Set A", X = n, Y = 0, Z = 0, LastCheckedAt = Today, LastCheckedBy = "jdoe", Seq = seq
            };
        }

        private static MarkerPlan.Existing Ex(int n, MarkerRecord record, MarkerAccess access)
        {
            return new MarkerPlan.Existing { MarkerUid = "m" + n, Record = record, Access = access, HeldBy = access == MarkerAccess.Free ? null : "kdoe", ShownHash = RowBuilder.HashOf(RowBuilder.ShownOf(Src(n))) };
        }

        private sealed class Led
        {
            public List<string> Lines;
            public Dictionary<string, ClashStatusEntry> Decisions;
            public LedgerExtras.View Extras;
        }

        private static Led Ledger(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            var led = new Led { Lines = list, Decisions = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal), Extras = LedgerExtras.Read(list) };
            int e, l, r;
            ClashStatusReplay.Replay(list, led.Decisions, out e, out l, out r);
            return led;
        }

        private static MarkerPlanAssembler.Request Req(Led led, IEnumerable<int> clashes, IEnumerable<MarkerPlan.Existing> markers)
        {
            return new MarkerPlanAssembler.Request
            {
                Clashes = (clashes ?? Array.Empty<int>()).Select(n => new MarkerPlanAssembler.Clash { Source = Src(n), Retest = new RowBuilder.Retest { Verdict = PlanVerdict.Clashing, ClearanceMm = 25 }, Live = true }).ToList(),
                Decisions = led.Decisions, Extras = led.Extras, Markers = (markers ?? Array.Empty<MarkerPlan.Existing>()).ToList(), OwnCounter = new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 },
                Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            };
        }

        private static void HeldFlow(Action<string, bool?> check)
        {
            var led = Ledger(new[]
            {
                LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001"),
                LedgerExtras.PendingLine(Key(1), T(2), "jdoe", 1, MarkerStatus.Ignored, null, null),
                LedgerExtras.PendingLine(Key(1), T(3), "jdoe", 1, null, "sleeve by others", null)
            });
            var a = MarkerPlanAssembler.Assemble(Req(led, Ints(1), new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", 1), MarkerAccess.Free) }));
            var plan = MarkerPlan.Build(a.Input);
            var w = MarkerPlanAssembler.Lines(a, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var after = Ledger(led.Lines.Concat(w.Lines));
            var change = plan.Changes.Count == 1 ? plan.Changes[0] : null;
            check(P + "end to end: a status and a reason set on a held marker are BOTH applied when it is free again, one PendingDone closes them, and the ledger holds both (M1)",
                a.Input.Pending.Count == 1 && change != null && change.After.Status == MarkerStatus.Ignored && change.After.Note == "sleeve by others"
                && plan.Pending.Count == 1 && plan.Pending[0].Kind == PendingKind.Applied && w.PendingClosed == 1 && !after.Extras.Pending.ContainsKey(Key(1))
                && after.Decisions.ContainsKey(Key(1)) && after.Decisions[Key(1)].Kind == ClashStatusKind.Ignored && after.Decisions[Key(1)].Note == "sleeve by others");
        }

        // ------------------------------------------------------------------ the summary's notes

        private static MarkerPlanAssembler.Written WrittenFor(Led led, IEnumerable<int> clashes, IEnumerable<MarkerPlan.Existing> markers, MarkerPlan.Result plan, MarkerPlanAssembler.Outcome outcome)
        {
            var a = MarkerPlanAssembler.Assemble(Req(led, clashes, markers));
            return MarkerPlanAssembler.Lines(a, plan ?? new MarkerPlan.Result(), outcome ?? new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
        }

        private static string Dec(string key, string kind, DateTime when, string note)
        {
            return string.Join("\t", new[] { key, kind, when.ToString("o", Inv), "kdoe", note });
        }

        private static void Notes(Action<string, bool?> check)
        {
            var three = Ledger(new[] { Dec(Key(1), "Ignored", T(1), ""), Dec(Key(2), "Ignored", T(1), ""), Dec(Key(3), "Ignored", T(1), "") }
                .Concat(Enumerable.Range(1, 3).Select(n => LedgerExtras.IssueLine(Key(n), T(1), "kdoe", "JD-000" + n))));
            var model = Enumerable.Range(1, 3).Select(n => Ex(n, Rec(n, "JD-000" + n, MarkerStatus.Pending, "", 1), MarkerAccess.Free)).ToList();
            var w3 = WrittenFor(three, null, model, null, null);
            var one = Ledger(new[] { Dec(Key(1), "Ignored", T(1), ""), LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001") });
            var w1 = WrittenFor(one, null, model.Take(1), null, null);
            check(P + "decisions that differ from the model are counted and said in the notes, plural and singular (M3)",
                w3.Overridden == 3 && w3.Notes.SequenceEqual(Texts("3 of your decisions differ from the model; the model's value was kept."))
                && w1.Overridden == 1 && w1.Notes.SequenceEqual(Texts("1 of your decisions differs from the model; the model's value was kept.")));

            const string DroppedText = "Your pending triage was dropped: the marker is no longer there.";
            var pend = Ledger(new[]
            {
                LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001"), LedgerExtras.PendingLine(Key(1), T(2), "jdoe", 1, MarkerStatus.Ignored, "decided", null)
            });
            var changed = new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", 5), MarkerAccess.Free) };
            var a = MarkerPlanAssembler.Assemble(Req(pend, Ints(1), changed));
            var plan = MarkerPlan.Build(a.Input);
            var wd = MarkerPlanAssembler.Lines(a, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            check(P + "a triage the plan dropped (the marker's Seq moved) is in PendingNotApplied with the plan's own message, and in the notes",
                plan.Pending.Count == 1 && plan.Pending[0].Kind == PendingKind.Dropped && !string.IsNullOrEmpty(plan.Pending[0].Message)
                && wd.PendingNotApplied.Count == 1 && wd.PendingNotApplied[0].Text == plan.Pending[0].Message && wd.PendingNotApplied[0].Count == 1 && wd.Notes.Contains(plan.Pending[0].Message));

            var held = Ledger(new[]
            {
                LedgerExtras.IssueLine(Key(2), T(1), "kdoe", "JD-0002"), LedgerExtras.PendingLine(Key(2), T(2), "jdoe", 1, MarkerStatus.Ignored, null, null)
            });
            var ha = MarkerPlanAssembler.Assemble(Req(held, Ints(2), new[] { Ex(2, Rec(2, "JD-0002", MarkerStatus.New, "", 1), MarkerAccess.HeldByOther) }));
            var hplan = MarkerPlan.Build(ha.Input);
            var wh = MarkerPlanAssembler.Lines(ha, hplan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            check(P + "a triage that waits for the holder (Kept) is in PendingNotApplied with its message ('waits: held by ...')",
                hplan.Pending.Count == 1 && hplan.Pending[0].Kind == PendingKind.Kept && wh.PendingNotApplied.Count == 1 && wh.PendingNotApplied[0].Text == hplan.Pending[0].Message
                && wh.PendingNotApplied[0].Text.Contains("waits") && wh.PendingNotApplied[0].Text.Contains("kdoe"));

            var late = Ledger(new[] { LedgerExtras.PendingLine(Key(4), T(2), "jdoe", 1, MarkerStatus.Ignored, null, null), LedgerExtras.IssueLine(Key(4), T(1), "kdoe", "JD-0004") });
            var lateWritten = new MarkerPlan.Result();
            lateWritten.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(4), MarkerUid = "m4", Kind = PendingKind.Applied, Message = "Your pending triage was written to the marker." });
            var notWritten = new MarkerPlanAssembler.Outcome();
            notWritten.NotWritten.Add("m4");
            var wl = WrittenFor(late, null, new[] { Ex(4, Rec(4, "JD-0004", MarkerStatus.New, "", 1), MarkerAccess.Free) }, lateWritten, notWritten);
            const string LateText = "Your pending triage was not written: the marker was passed over at the last moment, so it stays open and is tried again.";
            check(P + "an applied triage whose marker was passed over at the last moment is in PendingNotApplied with its own text",
                wl.PendingNotApplied.Count == 1 && wl.PendingNotApplied[0].Text == LateText && wl.Notes.Contains(LateText));

            var twice = new MarkerPlan.Result();
            twice.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(5), Kind = PendingKind.Dropped, Message = DroppedText });
            twice.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(6), Kind = PendingKind.Dropped, Message = DroppedText });
            twice.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(7), Kind = PendingKind.Dropped, Message = "Another reason." });
            var wt = WrittenFor(Ledger(Array.Empty<string>()), null, null, twice, null);
            check(P + "triage with the same message is one reason with its count and one sentence with the count in brackets; reasons are in ordinal order",
                wt.PendingNotApplied.Count == 2 && wt.PendingNotApplied[0].Text == "Another reason." && wt.PendingNotApplied[0].Count == 1 && wt.PendingNotApplied[1].Text == DroppedText && wt.PendingNotApplied[1].Count == 2
                && wt.Notes.SequenceEqual(Texts("Another reason.", DroppedText + " (2)")));

            var quiet = new MarkerPlan.Result();
            quiet.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(8), MarkerUid = "m8", Kind = PendingKind.Applied, Message = "ok" });
            var wq = WrittenFor(Ledger(Array.Empty<string>()), null, null, quiet, null);
            var wf = WrittenFor(three, null, model, twice, new MarkerPlanAssembler.Outcome { Failed = true });
            check(P + "nothing differs and nothing waits: no notes; a Failed outcome has none either", wq.Notes.Count == 0 && wq.PendingNotApplied.Count == 0 && wf.Notes.Count == 0 && wf.PendingNotApplied.Count == 0 && wf.Overridden == 0);
        }
    }
}
