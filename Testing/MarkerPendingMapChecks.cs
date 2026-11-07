using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 3g: the join between the ledger's keys and the plan's triples for a pending triage (spec 6.7),
    /// and the lines that close it. Revit-free, so both tiers run it. Red-first: BUILD-LOG.md.
    /// </summary>
    public static class MarkerPendingMapChecks
    {
        private const string P = "markers pending map: ";
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            Mapping(check);
            Closing(check);
            History(check);
            EndToEnd(check);
            Fuzz(check);
        }

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }

        private static string Triple(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

        private static Dictionary<string, string> Keys(params int[] ns)
        {
            return ns.ToDictionary(n => "k" + n, n => Triple(n), StringComparer.Ordinal);
        }

        private static Dictionary<string, string> Back(Dictionary<string, string> map)
        {
            return map.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
        }

        private static string[] Texts(params string[] items)
        {
            return items;
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }

        private static void Mapping(Action<string, bool?> check)
        {
            var view = LedgerExtras.Read(new[]
            {
                LedgerExtras.PendingLine("k1", T(2), "jdoe", 7, MarkerStatus.Ignored, null, null),
                LedgerExtras.PendingLine("k2", T(3), "kdoe", 4, null, "a note", ""),
                LedgerExtras.PendingLine("k9", T(4), "jdoe", 1, MarkerStatus.Pending, null, "asmith")
            });
            var mapped = PendingMap.Build(view, Keys(1, 2));
            var p1 = mapped.Pending.FirstOrDefault(x => x.Triple == Triple(1));
            var p2 = mapped.Pending.FirstOrDefault(x => x.Triple == Triple(2));
            check(P + "open entries become plan pendings by triple, with the marker Seq, author and only the fields the triage set (unset stays null, an unassign stays empty)",
                mapped.Pending.Count == 2 && p1 != null && p2 != null && p1.SeqAtPending == 7 && p1.Status == MarkerStatus.Ignored && p1.Note == null && p1.AssignedTo == null && p1.By == "jdoe"
                && p2.SeqAtPending == 4 && p2.Status == null && p2.Note == "a note" && p2.AssignedTo == "" && p2.By == "kdoe");
            check(P + "an entry whose key has no triple is returned as unmatched, not as a pending", mapped.UnmatchedKeys.SequenceEqual(Texts("k9")));
            var noTriple = PendingMap.Build(view, new Dictionary<string, string> { { "k1", "" }, { "k2", null } });
            check(P + "a key mapped to an empty triple is unmatched too", noTriple.Pending.Count == 0 && noTriple.UnmatchedKeys.SequenceEqual(Texts("k1", "k2", "k9")));
            var noLedger = PendingMap.DoneLines(new[] { Out(1, PendingKind.Applied) }, Back(Keys(1)), null, "jdoe", T(60));
            var oddLost = PendingMap.DuplicateLines(new MarkerPlan.LostDecision[] { null, new MarkerPlan.LostDecision { KeptUid = null } }, new Dictionary<string, string>(), "jdoe", T(60));
            check(P + "with no ledger view a Done line is written at now; a null lost decision, or one with no kept marker, is skipped",
                noLedger.Count == 1 && noLedger[0].Split((char)9)[2] == T(60).ToString("o", CultureInfo.InvariantCulture) && oddLost.Count == 0);
            check(P + "null and empty inputs never throw", NoThrow(() =>
            {
                PendingMap.Build(null, null);
                PendingMap.DoneLines(null, null, null, null, T(1));
                PendingMap.UnmatchedDoneLines(null, null, null, T(1));
                PendingMap.DuplicateLines(null, null, null, T(1));
                PendingMap.DoneLines(new MarkerPlan.PendingOutcome[] { null }, new Dictionary<string, string>(), new LedgerExtras.View(), "u", T(1));
            }) && PendingMap.Build(null, null).Pending.Count == 0);
        }

        private static MarkerPlan.PendingOutcome Out(int n, PendingKind kind)
        {
            return new MarkerPlan.PendingOutcome { Triple = Triple(n), Kind = kind, Message = "m" };
        }

        private static void Closing(Action<string, bool?> check)
        {
            var open = new[] { LedgerExtras.PendingLine("k1", T(2), "jdoe", 1, MarkerStatus.Ignored, null, null), LedgerExtras.PendingLine("k2", T(2), "jdoe", 1, MarkerStatus.Ignored, null, null),
                               LedgerExtras.PendingLine("k3", T(2), "jdoe", 1, MarkerStatus.Ignored, null, null) };
            var view = LedgerExtras.Read(open);
            var keys = Keys(1, 2, 3);
            var lines = PendingMap.DoneLines(new[] { Out(1, PendingKind.Applied), Out(2, PendingKind.Dropped), Out(3, PendingKind.Kept) }, Back(keys), view, "jdoe", T(60));
            var after = LedgerExtras.Read(open.Concat(lines));
            check(P + "Applied and Dropped outcomes get a PendingDone line each, a Kept one none, and reading the ledger back leaves only the Kept one open",
                lines.Count == 2 && lines.All(l => l.Split('\t')[1] == "PendingDone") && after.Pending.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Texts("k3")));

            var late = LedgerExtras.Read(new[] { LedgerExtras.PendingLine("k1", T(300), "jdoe", 1, MarkerStatus.Ignored, null, null) });
            var behind = PendingMap.DoneLines(new[] { Out(1, PendingKind.Applied) }, Back(keys), late, "jdoe", T(60));
            var behindWhen = behind.Count == 1 ? DateTime.Parse(behind[0].Split('\t')[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : DateTime.MinValue;
            check(P + "this PC's clock behind the pending entry: the Done line is one tick after it, so it still closes the entry (D30)",
                behind.Count == 1 && behindWhen == T(300).AddTicks(1)
                && LedgerExtras.Read(new[] { LedgerExtras.PendingLine("k1", T(300), "jdoe", 1, MarkerStatus.Ignored, null, null) }.Concat(behind)).Pending.Count == 0);
            var normal = PendingMap.DoneLines(new[] { Out(1, PendingKind.Dropped) }, Back(keys), view, "jdoe", T(60));
            check(P + "with the clock ahead, the Done line is at this PC's now", normal.Count == 1 && normal[0].Split('\t')[2] == T(60).ToString("o", CultureInfo.InvariantCulture) && normal[0].Split('\t')[0] == "k1" && normal[0].Split('\t')[3] == "jdoe");

            var skip = PendingMap.DoneLines(new[] { Out(9, PendingKind.Dropped) }, Back(keys), view, "jdoe", T(60));
            check(P + "an outcome whose triple has no ledger key is skipped", skip.Count == 0);
            var unmatched = PendingMap.UnmatchedDoneLines(Texts("k1", "k2", "nope"), view, "jdoe", T(60));
            check(P + "unmatched keys get Done lines that close them (D40); a key with no open entry still gets one at now",
                unmatched.Count == 3 && LedgerExtras.Read(open.Concat(unmatched)).Pending.Keys.SequenceEqual(Texts("k3")));
        }

        private static void History(Action<string, bool?> check)
        {
            var lost = new[]
            {
                new MarkerPlan.LostDecision { RemovedUid = "m2", RemovedIssueNo = "KD-0003", KeptUid = "m1", KeptIssueNo = "JD-0001", Status = MarkerStatus.Ignored, Note = "not kept", AssignedTo = "kdoe", By = "kdoe" },
                new MarkerPlan.LostDecision { RemovedUid = "m5", RemovedIssueNo = "KD-0009", KeptUid = "m4", KeptIssueNo = "JD-0004", Status = MarkerStatus.Pending, Note = "", AssignedTo = "", By = "" }
            };
            var lines = PendingMap.DuplicateLines(lost, new Dictionary<string, string> { { "m1", "k1" }, { "m4", "k4" } }, "jdoe", T(60));
            var read = LedgerExtras.Read(lines).Duplicates;
            check(P + "each lost decision becomes a DuplicateNotKept history line under the kept marker's key, and reads back",
                lines.Count == 2 && read.Count == 2 && read[0].Key == "k1" && read[0].RemovedIssueNo == "KD-0003" && read[0].KeptIssueNo == "JD-0001" && read[0].Status == MarkerStatus.Ignored
                && read[0].Note == "not kept" && read[0].AssignedTo == "kdoe" && read[1].Key == "k4");
            check(P + "a lost decision whose kept marker has no key is skipped",
                PendingMap.DuplicateLines(lost, new Dictionary<string, string> { { "m1", "k1" } }, "jdoe", T(60)).Count == 1 && PendingMap.DuplicateLines(lost, new Dictionary<string, string>(), "jdoe", T(60)).Count == 0);
        }

        // ---- the plan's own outcomes drive the lines

        private static MarkerPlan.Result Refresh(long markerSeq, MarkerAccess access, LedgerExtras.View view, Dictionary<string, string> keys)
        {
            var record = new MarkerRecord { Key = "k1", HostUid = "h1", OtherUid = "o1", HostId = 101, OtherId = 201, ClashId = ClashIdOf.Of("h1", "", "o1"), IssueNo = "JD-0001", Severity = "Hard", Seq = markerSeq, SetName = "S", ReportedBy = "jdoe", LastCheckedAt = new DateTimeOffset(T(0)) };
            var input = new MarkerPlan.Input
            {
                Options = new MarkerPlan.Options { Today = new DateTimeOffset(T(60)), UserName = "jdoe" },
                Counter = new IssueNumbers.Counter { Uid = "c1", UserName = "jdoe", Prefix = "JD", Next = 5 }
            };
            input.Markers.Add(new MarkerPlan.Existing { MarkerUid = "m1", Record = record, Access = access, HeldBy = "kdoe", ShownHash = "h" });
            input.Pending.AddRange(PendingMap.Build(view, keys).Pending);
            return MarkerPlan.Build(input);
        }

        private static void EndToEnd(Action<string, bool?> check)
        {
            var keys = Keys(1);
            var ledger = new[] { LedgerExtras.PendingLine("k1", T(2), "jdoe", 5, MarkerStatus.Ignored, "n", null) };
            var view = LedgerExtras.Read(ledger);

            var applied = Refresh(5, MarkerAccess.Free, view, keys);
            var appliedLines = PendingMap.DoneLines(applied.Pending, Back(keys), view, "jdoe", T(60));
            check(P + "with the marker's Seq unchanged the plan applies the triage and the Done line closes it", applied.Pending.Count == 1 && applied.Pending[0].Kind == PendingKind.Applied
                && LedgerExtras.Read(ledger.Concat(appliedLines)).Pending.Count == 0);

            var dropped = Refresh(6, MarkerAccess.Free, view, keys);
            var droppedLines = PendingMap.DoneLines(dropped.Pending, Back(keys), view, "jdoe", T(60));
            check(P + "with the marker's Seq changed the plan drops it and the Done line closes it", dropped.Pending.Count == 1 && dropped.Pending[0].Kind == PendingKind.Dropped
                && LedgerExtras.Read(ledger.Concat(droppedLines)).Pending.Count == 0);

            var held = Refresh(5, MarkerAccess.HeldByOther, view, keys);
            var heldLines = PendingMap.DoneLines(held.Pending, Back(keys), view, "jdoe", T(60));
            check(P + "a held marker keeps the triage: no Done line, still open after the Refresh", held.Pending.Count == 1 && held.Pending[0].Kind == PendingKind.Kept && heldLines.Count == 0
                && LedgerExtras.Read(ledger.Concat(heldLines)).Pending.ContainsKey("k1"));
        }

        private static void Fuzz(Action<string, bool?> check)
        {
            var rnd = new Random(20261105);
            int open = 0, thrown = 0, kept = 0;
            for (int i = 0; i < 300; i++)
            {
                try
                {
                    int n = rnd.Next(1, 6);
                    var ledger = new List<string>();
                    var outcomes = new List<MarkerPlan.PendingOutcome>();
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int k = 1; k <= n; k++)
                    {
                        ledger.Add(LedgerExtras.PendingLine("k" + k, T(rnd.Next(0, 400)), "u", rnd.Next(1, 5), MarkerStatus.Ignored, null, null));
                        map["k" + k] = Triple(k);
                        outcomes.Add(Out(k, (PendingKind)rnd.Next(3)));
                    }
                    var view = LedgerExtras.Read(ledger);
                    var lines = PendingMap.DoneLines(outcomes, Back(map), view, "u", T(rnd.Next(0, 200)));
                    var after = LedgerExtras.Read(ledger.Concat(lines));
                    foreach (var o in outcomes)
                    {
                        string key = Back(map)[o.Triple];
                        bool isOpen = after.Pending.ContainsKey(key);
                        if (o.Kind == PendingKind.Kept) { if (!isOpen) kept++; }
                        else if (isOpen) open++;
                    }
                }
                catch (Exception) { thrown++; }
            }
            check(P + "fuzz: 300 ledgers with clocks ahead and behind never throw (seed 20261105)", thrown == 0);
            check(P + "fuzz: after the Done lines are appended no Applied or Dropped pending is open, and every Kept one still is", open == 0 && kept == 0);
        }
    }
}
