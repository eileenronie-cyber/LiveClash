using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 4: the guards, the orders and the defaults of the plan assembler that the first Stryker run showed no check
    /// pinned down. Revit-free, so both tiers run it. Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationAssemblerChecks
    {
        private const string P = "markers mutation assembler: ";
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Mapping(check);
            Rows(check);
            Written(check);
        }

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string[] Texts(params string[] items) { return items; }
        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

        private static RowBuilder.Source Src(int n)
        {
            return new RowBuilder.Source
            {
                Key = "k" + n, HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, Severity = "Hard", HasPoint = true, X = n, SetName = "Set A", GroupKey = "",
                HostName = "Pipe", HostCategory = "Pipes", OtherName = "Duct", OtherCategory = "Ducts", ModelName = "Current model", Level = "L1", RuleClearanceMm = 25
            };
        }

        private static MarkerRecord Rec(int n, string key)
        {
            return new MarkerRecord
            {
                Key = key, HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, ClashId = ClashIdOf.Of("h" + n, "", "o" + n), IssueNo = "JD-000" + n, ReportedBy = "jdoe",
                ReportedAt = Today, Severity = "Hard", ClearanceMm = 25, SetName = "Set A", X = n, Seq = 1
            };
        }

        private static MarkerPlan.Existing Ex(string uid, MarkerRecord r) { return new MarkerPlan.Existing { MarkerUid = uid, Record = r, Access = MarkerAccess.Free }; }

        private static LedgerExtras.View Pending(params string[] keys)
        {
            return LedgerExtras.Read(keys.Select(k => LedgerExtras.PendingLine(k, T(1), "jdoe", 1, MarkerStatus.Ignored, null, null)));
        }

        private static MarkerPlanAssembler.Assembly Assemble(IEnumerable<MarkerPlanAssembler.Clash> clashes, IEnumerable<MarkerPlan.Existing> markers, LedgerExtras.View extras)
        {
            return MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request
            {
                Clashes = (clashes ?? Array.Empty<MarkerPlanAssembler.Clash>()).ToList(), Markers = (markers ?? Array.Empty<MarkerPlan.Existing>()).ToList(), Extras = extras, Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            });
        }

        private static MarkerPlanAssembler.Clash Clash(RowBuilder.Source s) { return new MarkerPlanAssembler.Clash { Source = s }; }

        // ------------------------------------------------------------------ the join between ledger keys and triples

        private static void Mapping(Action<string, bool?> check)
        {
            var noKey = Rec(1, "");
            var halfTriple = Rec(2, "k2");
            halfTriple.OtherUid = "";
            var badClash = Src(3);
            badClash.OtherUid = "";
            var noKeyClash = Src(4);
            noKeyClash.Key = "";
            var a = Assemble(new[] { Clash(badClash), Clash(noKeyClash) }, new[] { Ex("m1", noKey), Ex("m2", halfTriple) }, Pending("", "k2", "k3", "k4"));
            check(P + "a ledger key reaches a triple only through a marker or a clash that has a key and a complete triple; any other open entry is unmatched",
                a.PendingKeyToTriple.Count == 0 && a.UnmatchedPendingKeys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Texts("", "k2", "k3", "k4")) && a.Input.Pending.Count == 0);

            var first = Rec(5, "kk");
            var second = Rec(6, "kk");
            var pair = Assemble(null, new[] { Ex("mB", second), Ex("mA", first) }, Pending("kk"));
            check(P + "two markers that carry one key: the later UniqueId decides which pair it means (a stable answer whatever the list order)", pair.PendingKeyToTriple["kk"] == Tri(6));
            var s1 = Src(7);
            s1.Key = "kc";
            var s2 = Src(8);
            s2.Key = "kc";
            var clashPair = Assemble(new[] { Clash(s2), Clash(s1) }, null, Pending("kc"));
            check(P + "...and two clashes that carry one key: the later one in key-then-triple order does, whatever the list order", clashPair.PendingKeyToTriple["kc"] == Tri(8) && clashPair.Input.Rows.Select(r => r.HostUid).SequenceEqual(Texts("h7", "h8")));
        }

        // ------------------------------------------------------------------ the rows

        private static void Rows(Action<string, bool?> check)
        {
            check(P + "a clash is live unless it says otherwise", new MarkerPlanAssembler.Clash().Live && Assemble(new[] { Clash(Src(1)) }, null, null).Input.Rows[0].Live);

            var retest = new RowBuilder.Retest { Verdict = PlanVerdict.NoLongerClashes, ClearanceMm = 40 };
            var notLive = new MarkerPlanAssembler.Clash { Source = Src(1), Retest = retest, Live = false };
            var rows = Assemble(new[] { notLive }, new[] { Ex("m1", Rec(1, "k1")) }, null).Input.Rows;
            check(P + "a re-test is used only when the clash is live AND a readable marker has its triple: a not-live clash with a marker keeps its not-live row", rows.Count == 1 && !rows[0].Live && rows[0].Verdict == PlanVerdict.NotRetested);

            var half = Src(2);
            half.OtherUid = "";
            var withRetest = new MarkerPlanAssembler.Clash { Source = half, Retest = retest };
            var halfRows = Assemble(new[] { withRetest }, new[] { Ex("m2", Rec(2, "k2")) }, null).Input.Rows;
            check(P + "...and a clash with an incomplete triple gets no re-test", halfRows.Count == 1 && halfRows[0].Verdict == PlanVerdict.Clashing);

            var ordered = Assemble(new[] { Clash(Src(3)), Clash(Src(1)), Clash(Src(2)) }, null, null).Input.Rows;
            var tieA = Src(4);
            tieA.Key = "same";
            var tieB = Src(5);
            tieB.Key = "same";
            var tieKeyless = Src(6);
            tieKeyless.Key = null;
            var tied = Assemble(new[] { Clash(tieB), Clash(tieKeyless), Clash(tieA), Clash(Src(1)) }, null, null).Input.Rows;
            check(P + "the rows come out by key, then triple, a missing key first",
                ordered.Select(r => r.Key).SequenceEqual(Texts("k1", "k2", "k3")) && tied.Select(r => r.HostUid).SequenceEqual(Texts("h6", "h1", "h4", "h5")));

            var onlyIssue = LedgerExtras.Read(new[] { LedgerExtras.IssueLine("k1", T(1), "kdoe", "JD-0007") });
            var withIssue = Assemble(new[] { Clash(Src(1)), Clash(Src(2)) }, null, onlyIssue).Input.Rows;
            check(P + "a clash with nothing in the ledger has no decision (not even an empty one); one with only an issue number remembers it and has no Seed",
                withIssue[1].RememberedIssueNo == null && withIssue[1].Seed == null && withIssue[0].RememberedIssueNo == "JD-0007" && withIssue[0].Seed == null);
        }

        // ------------------------------------------------------------------ the lines

        private static void Written(Action<string, bool?> check)
        {
            var view = LedgerExtras.Read(Texts("k1", "k1b", "k2").Select(k => LedgerExtras.PendingLine(k, T(1), "jdoe", 1, MarkerStatus.Ignored, null, null)));
            var asm = new MarkerPlanAssembler.Assembly { Request = new MarkerPlanAssembler.Request { Extras = view } };
            asm.PendingKeyToTriple["k2"] = Tri(2);
            asm.PendingKeyToTriple["k1b"] = Tri(1);
            asm.PendingKeyToTriple["k1"] = Tri(1);
            var plan = new MarkerPlan.Result();
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(2), Kind = PendingKind.Dropped });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), Kind = PendingKind.Dropped });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), Kind = PendingKind.Applied });
            var w = MarkerPlanAssembler.Lines(asm, plan, null, null, T(60));
            check(P + "Done lines come in the order of the triples, then of the keys under one triple, and a triple that appears twice in the plan closes its keys once; a missing author is an empty one",
                w.Lines.Select(l => l.Split((char)9)[0]).SequenceEqual(Texts("k1", "k1b", "k2")) && w.PendingClosed == 3 && w.Lines.All(l => l.Split((char)9)[3] == ""));

            const string Kept = "Your pending triage waits for the marker to be free.";
            const string Dropped = "Your pending triage was dropped.";
            var texts = new MarkerPlan.Result();
            texts.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), Kind = PendingKind.Kept, Message = null });
            texts.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(2), Kind = PendingKind.Dropped, Message = "" });
            texts.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(3), Kind = PendingKind.Kept, Message = "Held by someone." });
            var wt = MarkerPlanAssembler.Lines(new MarkerPlanAssembler.Assembly(), texts, null, "jdoe", T(60));
            check(P + "a not-applied triage with no message of its own gets a default text by kind (kept or dropped); one with a message keeps it, in ordinal order",
                wt.PendingNotApplied.Select(r => r.Text).SequenceEqual(Texts(Dropped, "Held by someone.", Kept).OrderBy(x => x, StringComparer.Ordinal)));

            var markers = Enumerable.Range(1, 3).Select(n => Src(n)).ToList();
            var request = new MarkerPlanAssembler.Request
            {
                Clashes = markers.Select(Clash).ToList(), Extras = new LedgerExtras.View(), OwnCounter = new IssueNumbers.Counter { Uid = "c", UserName = "jdoe", Prefix = "JD", Next = 1 }, Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            };
            var made = MarkerPlanAssembler.Assemble(request);
            var created = MarkerPlan.Build(made.Input);
            Func<int, int> issueLines = n => MarkerPlanAssembler.Lines(made, created, new MarkerPlanAssembler.Outcome { CreationsMade = n }, "jdoe", T(60)).Lines.Count(l => l.Split((char)9)[1] == LedgerExtras.IssueEvent);
            check(P + "only the first CreationsMade creations are followed: none for 0, one for 1, all for -1 or for more than there are", created.Creations.Count == 3 && issueLines(0) == 0 && issueLines(1) == 1 && issueLines(2) == 2 && issueLines(-1) == 3 && issueLines(9) == 3);
        }
    }
}
