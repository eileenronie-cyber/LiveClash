using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 11d (A): the texts that order two entries must tell two different field lists apart, whatever the free text holds
    /// (a set name, an assignee, a Revit user name, a note, a display value can all hold a '|'). Before the change they joined the fields with '|', so
    /// different field lists could give one text and the entry kept depended on the file or list order. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationOrderChecks
    {
        private const string P = "markers mutation order: ";
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Ledger(check);
            Rows(check);
        }

        // ------------------------------------------------------------------ ledger lines on one clock whose joined texts would read alike

        private static string AssignedOf(IEnumerable<string> lines)
        {
            var e = LedgerExtras.Read(lines).Assigned["k"];
            return e.Assignee + "/" + e.By;
        }

        private static void Ledger(Action<string, bool?> check)
        {
            var a1 = LedgerExtras.AssignedLine("k", T0, "y|z", "x");
            var a2 = LedgerExtras.AssignedLine("k", T0, "z", "x|y");
            check(P + "two assignments on one clock whose assignee and author hold a '|' between them (x + y|z against x|y + z) keep the same one in either file order", AssignedOf(new[] { a1, a2 }) == AssignedOf(new[] { a2, a1 }));

            var r1 = LedgerExtras.ReconciledLine("k", T0, "u", "a|b", "c");
            var r2 = LedgerExtras.ReconciledLine("k", T0, "u", "a", "b|c");
            Func<IEnumerable<string>, string> reconciled = lines => { var e = LedgerExtras.Read(lines).Reconciled["k"]; return e.Source + "/" + e.Event; };
            check(P + "two model notes on one clock whose source and event hold a '|' (a|b + c against a + b|c) keep the same one in either file order", reconciled(new[] { r1, r2 }) == reconciled(new[] { r2, r1 }));

            var p1 = LedgerExtras.PendingLine("k", T0, "d", 5, MarkerStatus.Ignored, "a", "b|c");
            var p2 = LedgerExtras.PendingLine("k", T0, "d", 5, MarkerStatus.Ignored, "a|b", "c");
            Func<IEnumerable<string>, string> pending = lines => { var e = LedgerExtras.Read(lines).Pending["k"]; return e.Note + "/" + e.AssignedTo + "/" + e.By; };
            check(P + "two pending entries on one clock and Seq whose note and assignee hold a '|' between them keep the same merged answer in either file order", pending(new[] { p1, p2 }) == pending(new[] { p2, p1 }));
        }

        // ------------------------------------------------------------------ two rows for one pair whose joined texts would read alike

        private static MarkerPlan.Row Rw(string key, string severity)
        {
            return new MarkerPlan.Row
            {
                Key = key, HostUid = "h1", LinkUid = "", OtherUid = "o1", HostId = 101, OtherId = 201, Live = true, Verdict = PlanVerdict.Clashing, RuleClearanceMm = 25,
                Severity = severity, HasPoint = true, X = 1, SetName = "Set A", GroupKey = null
            };
        }

        private static string PlanOf(params MarkerPlan.Row[] rows)
        {
            var plan = MarkerPlan.Build(new MarkerPlan.Input
            {
                Rows = rows.ToList(), Counter = new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }, Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            });
            var rec = plan.Creations.Single().Record;
            return rec.Key + "/" + rec.Severity + "/" + rec.SetName + "/" + rec.GroupLabel;
        }

        private static void Rows(Action<string, bool?> check)
        {
            var keyed = Rw("a|b", "c");
            var severe = Rw("a", "b|c");
            check(P + "two rows for one pair whose key and severity hold a '|' between them (a|b + c against a + b|c) give the same plan in either list order", PlanOf(keyed, severe) == PlanOf(severe, keyed));

            var setA = Rw("k", "Hard"); setA.SetName = "s|t"; setA.GroupLabel = "u";
            var setB = Rw("k", "Hard"); setB.SetName = "s"; setB.GroupLabel = "t|u";
            check(P + "...and rows whose set name and label do (s|t + u against s + t|u)", PlanOf(setA, setB) == PlanOf(setB, setA));
        }
    }
}
