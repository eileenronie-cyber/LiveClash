using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 6: how the ledger reader takes short and long lines, splits the model note, orders the duplicates and
    /// picks the latest of several lines, which the second Stryker run showed no check pinned down. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationLedgerChecks
    {
        private const string P = "markers mutation ledger: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        // a clock so early that its tick count has 10 digits, where the clock of today has 18
        private static readonly DateTime Early = new DateTime(1, 1, 1, 0, 15, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            Widths(check);
            Latest(check);
            Split(check);
            Duplicates(check);
            Clocks(check);
            Presence(check);
        }

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string When(DateTime t) { return t.ToString("o", Inv); }
        private static string Row(params string[] fields) { return string.Join("\t", fields); }

        // ------------------------------------------------------------------ lines with too few or too many fields

        private static void Widths(Action<string, bool?> check)
        {
            var pend = LedgerExtras.PendingLine("k", T(1), "u", 1, MarkerStatus.Ignored, null, null);
            var closedByShort = LedgerExtras.Read(new[] { pend, Row("k", "PendingDone", When(T(2))) });
            check(P + "a three-field line (key, event, time) is read: a done line of that width closes the pending entry", closedByShort.Pending.Count == 0);
            var goneShort = LedgerExtras.Read(new[] { Row("k", "Gone", When(T(3))) });
            check(P + "...and a three-field Gone line is the latest Gone for its key", goneShort.GoneAt.ContainsKey("k") && goneShort.GoneAt["k"] == T(3));
            check(P + "a two-field line, a line with no readable time and a comment are skipped", LedgerExtras.Read(new[] { Row("k", "Gone"), Row("k", "Gone", "not a time"), "# k\tGone\t" + When(T(3)) }).GoneAt.Count == 0);

            var short4 = new[] { Row("k", "Assigned", When(T(1)), "u"), Row("k", "Reconciled", When(T(1)), "u"), Row("k", "Issue", When(T(1)), "u") };
            var none = LedgerExtras.Read(short4);
            check(P + "an Assigned, Reconciled or Issue line with no text field is skipped without a failure", none.Assigned.Count == 0 && none.Reconciled.Count == 0 && none.Issues.Count == 0);
            var long6 = LedgerExtras.Read(new[]
            {
                Row("k", "Assigned", When(T(1)), "u", "asmith", "extra"), Row("k", "Reconciled", When(T(1)), "u", "model Gone", "extra"), Row("k", "Issue", When(T(1)), "u", "JD-0001", "extra")
            });
            check(P + "...and the same lines with one field more are read, the extra field ignored", long6.Assigned["k"].Assignee == "asmith" && long6.Reconciled["k"].Source == "model" && long6.Reconciled["k"].Event == "Gone" && long6.Issues["k"].IssueNo == "JD-0001");
        }

        // ------------------------------------------------------------------ the latest of several lines

        private static void Latest(Action<string, bool?> check)
        {
            var g1 = Row("k", "Gone", When(T(1)), "u", "");
            var g2 = Row("k", "Gone", When(T(5)), "u", "");
            var g3 = Row("k", "Gone", When(T(3)), "u", "");
            check(P + "the latest Gone line for a key wins, whatever the file order",
                LedgerExtras.Read(new[] { g1, g2, g3 }).GoneAt["k"] == T(5) && LedgerExtras.Read(new[] { g2, g1, g3 }).GoneAt["k"] == T(5) && LedgerExtras.Read(new[] { g3, g1, g2 }).GoneAt["k"] == T(5)
                && LedgerExtras.Read(new[] { g1, g3, g2 }).GoneAt["k"] == T(5));
            var other = Row("j", "Gone", When(T(2)), "u", "");
            var two = LedgerExtras.Read(new[] { g2, other, g1 });
            check(P + "...and each key keeps its own", two.GoneAt["k"] == T(5) && two.GoneAt["j"] == T(2) && two.GoneAt.Count == 2);
        }

        // ------------------------------------------------------------------ the model note splits at its first space

        private static void Split(Action<string, bool?> check)
        {
            var plain = LedgerExtras.Read(new[] { Row("k", "Reconciled", When(T(1)), "u", "model Gone") }).Reconciled["k"];
            var noSpace = LedgerExtras.Read(new[] { Row("k", "Reconciled", When(T(1)), "u", "model") }).Reconciled["k"];
            var leading = LedgerExtras.Read(new[] { Row("k", "Reconciled", When(T(1)), "u", " Gone") }).Reconciled["k"];
            var more = LedgerExtras.Read(new[] { Row("k", "Reconciled", When(T(1)), "u", "model Gone for good") }).Reconciled["k"];
            check(P + "a model note splits at its first space into the source and the event; with no space it is all source and the event is empty",
                plain.Source == "model" && plain.Event == "Gone" && noSpace.Source == "model" && noSpace.Event == "" && more.Source == "model" && more.Event == "Gone for good");
            check(P + "...and a space in the first place gives an empty source and the rest as the event", leading.Source == "" && leading.Event == "Gone");
        }

        // ------------------------------------------------------------------ the duplicates, in the order of every field

        private static string DupLine(string key, string by, string kept, string removed, MarkerStatus status, string note, string assignee)
        {
            return LedgerExtras.DuplicateLine(key, T(1), by, kept, removed, status, note, assignee);
        }

        private static void Duplicates(Action<string, bool?> check)
        {
            var cases = new List<Tuple<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>>
            {
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("key", DupLine("a", "u", "K", "R", MarkerStatus.Pending, "n", "x"), DupLine("b", "u", "K", "R", MarkerStatus.Pending, "n", "x"), d => d.Key),
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("removed issue number", DupLine("k", "u", "K", "A", MarkerStatus.Pending, "n", "x"), DupLine("k", "u", "K", "B", MarkerStatus.Pending, "n", "x"), d => d.RemovedIssueNo),
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("kept issue number", DupLine("k", "u", "A", "R", MarkerStatus.Pending, "n", "x"), DupLine("k", "u", "B", "R", MarkerStatus.Pending, "n", "x"), d => d.KeptIssueNo),
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("status", DupLine("k", "u", "K", "R", MarkerStatus.Pending, "n", "x"), DupLine("k", "u", "K", "R", MarkerStatus.Ignored, "n", "x"), d => d.Status.ToString()),
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("note", DupLine("k", "u", "K", "R", MarkerStatus.Pending, "a", "x"), DupLine("k", "u", "K", "R", MarkerStatus.Pending, "b", "x"), d => d.Note),
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("assignee", DupLine("k", "u", "K", "R", MarkerStatus.Pending, "n", "a"), DupLine("k", "u", "K", "R", MarkerStatus.Pending, "n", "b"), d => d.AssignedTo),
                Tuple.Create<string, string, string, Func<LedgerExtras.DuplicateEntry, string>>("author", DupLine("k", "a", "K", "R", MarkerStatus.Pending, "n", "x"), DupLine("k", "b", "K", "R", MarkerStatus.Pending, "n", "x"), d => d.By)
            };
            foreach (var c in cases)
            {
                var forward = LedgerExtras.Read(new[] { c.Item2, c.Item3 }).Duplicates.Select(c.Item4).ToList();
                var backward = LedgerExtras.Read(new[] { c.Item3, c.Item2 }).Duplicates.Select(c.Item4).ToList();
                var expected = new[] { c.Item4(LedgerExtras.Read(new[] { c.Item2 }).Duplicates[0]), c.Item4(LedgerExtras.Read(new[] { c.Item3 }).Duplicates[0]) };
                check(P + "two duplicate notes that differ only in the " + c.Item1 + " come out with the smaller one first, whatever the file order",
                    forward.SequenceEqual(expected) && backward.SequenceEqual(expected) && expected[0] != expected[1]);
            }
            var early = LedgerExtras.DuplicateLine("k", Early, "u", "K", "R", MarkerStatus.Pending, "n", "x");
            var late = LedgerExtras.DuplicateLine("k", T(1), "u", "K", "R", MarkerStatus.Pending, "n", "x");
            check(P + "duplicate notes come out oldest first", LedgerExtras.Read(new[] { late, early }).Duplicates.Select(d => d.WhenUtc).SequenceEqual(new[] { Early, T(1) }));
        }

        // ------------------------------------------------------------------ the clock is fixed-width

        private static void Clocks(Action<string, bool?> check)
        {
            var oldAssign = LedgerExtras.AssignedLine("k", Early, "u", "old");
            var newAssign = LedgerExtras.AssignedLine("k", T(1), "u", "new");
            check(P + "of two assignments the later clock wins even when its tick count has more digits than the earlier one", LedgerExtras.Read(new[] { oldAssign, newAssign }).Assigned["k"].Assignee == "new" && LedgerExtras.Read(new[] { newAssign, oldAssign }).Assigned["k"].Assignee == "new");
            var oldRec = LedgerExtras.ReconciledLine("k", Early, "u", "old", "Gone");
            var newRec = LedgerExtras.ReconciledLine("k", T(1), "u", "new", "Gone");
            check(P + "...and so does the later model note", LedgerExtras.Read(new[] { oldRec, newRec }).Reconciled["k"].Source == "new" && LedgerExtras.Read(new[] { newRec, oldRec }).Reconciled["k"].Source == "new");
            var oldIssue = LedgerExtras.IssueLine("k", Early, "u", "JD-0001");
            var newIssue = LedgerExtras.IssueLine("k", T(1), "u", "JD-0000");
            check(P + "...and the later issue number, whatever its text", LedgerExtras.Read(new[] { oldIssue, newIssue }).Issues["k"].IssueNo == "JD-0000" && LedgerExtras.Read(new[] { newIssue, oldIssue }).Issues["k"].IssueNo == "JD-0000");

            var oldPend = LedgerExtras.PendingLine("k", Early, "early", 1, MarkerStatus.Ignored, null, null);
            var newPend = LedgerExtras.PendingLine("k", T(1), "late", 1, MarkerStatus.Ignored, null, null);
            check(P + "of two pending entries the later clock is the last, even with more digits in its tick count",
                LedgerExtras.Read(new[] { oldPend, newPend }).Pending["k"].By == "late" && LedgerExtras.Read(new[] { newPend, oldPend }).Pending["k"].By == "late");
            var nine = LedgerExtras.PendingLine("k", T(1), "nine", 9, MarkerStatus.Ignored, null, null);
            var ten = LedgerExtras.PendingLine("k", T(1), "ten", 10, MarkerStatus.Ignored, null, null);
            var merged = LedgerExtras.Read(new[] { nine, ten }).Pending["k"];
            check(P + "...and on one clock Seq 10 is after Seq 9, not before it", merged.SeqAtPending == 10 && merged.By == "ten" && LedgerExtras.Read(new[] { ten, nine }).Pending["k"].SeqAtPending == 10);
        }

        // ------------------------------------------------------------------ which of two pending entries is last when only a field is set or missing

        private static void Presence(Action<string, bool?> check)
        {
            // each pair: on the same clock and Seq, the entry that leaves a field unset is the last one; the author tells which is which
            Func<string, MarkerStatus?, string, string, string> line = (by, st, note, assignee) => LedgerExtras.PendingLine("k", T(1), by, 4, st, note, assignee);
            Func<string[], string> lastBy = lines => LedgerExtras.Read(lines).Pending["k"].By;
            var s1 = line("z", MarkerStatus.Ignored, "A", "a");
            var s2 = line("y", null, "A", "a");
            check(P + "on one clock and Seq the entry with no status is the last one (a status sorts before none)", lastBy(new[] { s1, s2 }) == "y" && lastBy(new[] { s2, s1 }) == "y");
            var n1 = line("z", MarkerStatus.Ignored, "}", "a");
            var n2 = line("y", MarkerStatus.Ignored, null, "a");
            check(P + "...and the entry with no note is the last one", lastBy(new[] { n1, n2 }) == "y" && lastBy(new[] { n2, n1 }) == "y");
            var a1 = line("z", MarkerStatus.Ignored, "n", "}");
            var a2 = line("y", MarkerStatus.Ignored, "n", null);
            check(P + "...and the entry with no assignee is the last one", lastBy(new[] { a1, a2 }) == "y" && lastBy(new[] { a2, a1 }) == "y");
        }
    }
}
