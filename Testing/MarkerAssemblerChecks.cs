using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, the plan assembler: from what the Revit side collected to the plan's input, and from the plan's
    /// outcome to the ledger lines that follow it (spec 6.2, 6.3, 6.7, 6.10). Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/ASSEMBLER-LOG.md.
    /// </summary>
    public static class MarkerAssemblerChecks
    {
        private const string P = "markers assembler: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Rows(check);
            Pass(check);
            Safety(check);
            Hostile(check);
            Pending(check);
            Followed(check);
            Timing(check);
            EndToEnd(check);
            Fuzz(check);
        }

        // ------------------------------------------------------------------ helpers

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string Key(int n) { return "k" + n; }
        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }
        private static string Ev(string line) { return line.Split((char)9)[1]; }
        private static string[] Texts(params string[] items) { return items; }

        private static RowBuilder.Source Src(int n, string severity)
        {
            return new RowBuilder.Source
            {
                Key = Key(n), HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, Severity = severity,
                HasPoint = true, X = n, Y = 0, Z = 0, SetName = "Set A", GroupKey = "", HostName = "Pipe " + n, HostCategory = "Pipes",
                OtherName = "Duct " + n, OtherCategory = "Ducts", ModelName = "Current model", Level = "Level 1", RuleClearanceMm = 25, RuleInsulated = false
            };
        }

        private static RowBuilder.Source Src(int n) { return Src(n, "Hard"); }

        private static MarkerRecord Rec(int n, string issue, MarkerStatus status, string note, string assignee, long seq)
        {
            return new MarkerRecord
            {
                Key = Key(n), HostUid = "h" + n, LinkUid = "", OtherUid = "o" + n, HostId = 100 + n, OtherId = 200 + n, ClashId = ClashIdOf.Of("h" + n, "", "o" + n),
                IssueNo = issue, Status = status, Note = note, AssignedTo = assignee, ReportedBy = "jdoe", ReportedAt = Today, Severity = "Hard", ClearanceMm = 25,
                SetName = "Set A", X = n, Y = 0, Z = 0, LastCheckedAt = Today, LastCheckedBy = "jdoe", Seq = seq
            };
        }

        private static MarkerPlan.Existing Ex(int n, MarkerRecord record)
        {
            return new MarkerPlan.Existing { MarkerUid = "m" + n, Record = record, Access = MarkerAccess.Free, ShownHash = RowBuilder.HashOf(RowBuilder.ShownOf(Src(n))) };
        }

        private static MarkerAssemblerClash Live(int n) { return new MarkerAssemblerClash(Src(n), null, true); }

        private static RowBuilder.Retest Clashing() { return new RowBuilder.Retest { Verdict = PlanVerdict.Clashing, ClearanceMm = 25, Insulated = false }; }

        private static IssueNumbers.Counter Own() { return new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }; }

        private static MarkerPlan.Options Opt() { return new MarkerPlan.Options { Today = Today, UserName = "jdoe" }; }

        private static string Dec(string key, string kind, DateTime when, string by, string note)
        {
            return string.Join("\t", new[] { key, kind, when.ToString("o", Inv), by, note });
        }

        private static string Gone(string key, DateTime when)
        {
            return string.Join("\t", new[] { key, ClashStatusReplay.GoneEvent, when.ToString("o", Inv), "kdoe", "" });
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

        private static MarkerPlanAssembler.Request Req(Led led, IEnumerable<MarkerAssemblerClash> clashes, IEnumerable<MarkerPlan.Existing> markers)
        {
            return new MarkerPlanAssembler.Request
            {
                Clashes = (clashes ?? Array.Empty<MarkerAssemblerClash>()).Select(c => c.ToClash()).ToList(), Decisions = led.Decisions, Extras = led.Extras,
                Markers = (markers ?? Array.Empty<MarkerPlan.Existing>()).ToList(), OwnCounter = Own(), Options = Opt()
            };
        }

        private static List<string> Of(MarkerPlanAssembler.Written w, string ev)
        {
            return w.Lines.Where(l => Ev(l) == ev).ToList();
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }

        /// <summary>The markers as they stand after Apply did what <paramref name="o"/> says: the model the ledger must follow.</summary>
        private static List<MarkerPlan.Existing> Post(MarkerPlan.Input input, MarkerPlan.Result plan, MarkerPlanAssembler.Outcome o)
        {
            var removed = new HashSet<string>(plan.Removals.Where(r => !o.NotWritten.Contains(r.MarkerUid)).Select(r => r.MarkerUid), StringComparer.Ordinal);
            var changes = new Dictionary<string, MarkerPlan.Change>(StringComparer.Ordinal);
            foreach (var c in plan.Changes.Where(c => !o.NotWritten.Contains(c.MarkerUid))) changes[c.MarkerUid] = c;
            var post = new List<MarkerPlan.Existing>();
            foreach (var m in input.Markers.Where(m => m != null && !removed.Contains(m.MarkerUid)))
            {
                MarkerPlan.Change c;
                if (changes.TryGetValue(m.MarkerUid, out c))
                    post.Add(new MarkerPlan.Existing { MarkerUid = m.MarkerUid, Record = c.After, Access = m.Access, HeldBy = m.HeldBy, ShownHash = c.ShownHash ?? m.ShownHash });
                else post.Add(m);
            }
            int made = o.CreationsMade < 0 ? plan.Creations.Count : Math.Min(o.CreationsMade, plan.Creations.Count);
            foreach (var c in plan.Creations.Take(made))
                post.Add(new MarkerPlan.Existing { MarkerUid = "new-" + c.Triple, Record = c.Record, Access = MarkerAccess.Free, ShownHash = c.ShownHash });
            return post;
        }

        private static List<MarkerReconcile.ModelMarker> Models(IEnumerable<MarkerPlan.Existing> post)
        {
            return post.Where(m => m.Record != null && !string.IsNullOrEmpty(m.Record.Key)).OrderBy(m => m.MarkerUid, StringComparer.Ordinal).Select(m => new MarkerReconcile.ModelMarker
            {
                Key = m.Record.Key, Status = m.Record.Status, Note = m.Record.Note, AssignedTo = m.Record.AssignedTo,
                ChangedBy = m.Record.ChangedBy, ReportedBy = m.Record.ReportedBy, IssueNo = m.Record.IssueNo
            }).ToList();
        }

        // ------------------------------------------------------------------ assemble

        private static void Rows(Action<string, bool?> check)
        {
            var led = Ledger(new[]
            {
                Dec(Key(1), "Ignored", T(1), "kdoe", "not a clash"), LedgerExtras.AssignedLine(Key(1), T(2), "kdoe", "asmith"), LedgerExtras.IssueLine(Key(1), T(3), "kdoe", "KD-0007"),
                Dec(Key(3), "Coordinated", T(1), "kdoe", "sleeved"), Gone(Key(3), T(2)), LedgerExtras.IssueLine(Key(3), T(3), "kdoe", "KD-0009"),
                LedgerExtras.AssignedLine(Key(4), T(2), "kdoe", "asmith")
            });
            var a = MarkerPlanAssembler.Assemble(Req(led, new[] { Live(1), Live(2), Live(3), Live(4) }, null));
            var rows = a.Input.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
            MarkerPlan.Row r1 = rows.ContainsKey(Key(1)) ? rows[Key(1)] : null, r2 = rows.ContainsKey(Key(2)) ? rows[Key(2)] : null;
            MarkerPlan.Row r3 = rows.ContainsKey(Key(3)) ? rows[Key(3)] : null, r4 = rows.ContainsKey(Key(4)) ? rows[Key(4)] : null;

            check(P + "a live clash with a ledger decision, assignee and issue number becomes a row with a Seed, the assignee and the remembered number",
                a.Input.Rows.Count == 4 && r1 != null && r1.Live && r1.Seed != null && r1.Seed.Status == MarkerStatus.Ignored && r1.Seed.Note == "not a clash" && r1.Seed.AssignedTo == "asmith"
                && r1.Seed.By == "kdoe" && r1.Seed.At == new DateTimeOffset(T(1)) && r1.RememberedIssueNo == "KD-0007" && r1.Severity == "Hard"
                && r1.ShownHash == RowBuilder.HashOf(RowBuilder.ShownOf(Src(1))) && r1.Verdict == PlanVerdict.Clashing);
            check(P + "a clash with nothing in the ledger has no Seed and no remembered number", r2 != null && r2.Seed == null && string.IsNullOrEmpty(r2.RememberedIssueNo) && r2.Live);
            check(P + "a Gone decision gives no Seed, but its issue number is still remembered (D36)", r3 != null && r3.Seed == null && r3.RememberedIssueNo == "KD-0009");
            check(P + "an assignee alone gives a New Seed with the assignee and no time", r4 != null && r4.Seed != null && r4.Seed.Status == MarkerStatus.New && r4.Seed.AssignedTo == "asmith" && r4.Seed.At == null);

            var off = new RowBuilder.Source[] { Src(5) };
            off[0].AlsoSets = new List<string> { "B" };
            var back = MarkerPlanAssembler.Assemble(Req(Ledger(Array.Empty<string>()), new[] { new MarkerAssemblerClash(off[0], new RowBuilder.Retest { Verdict = PlanVerdict.NoLongerClashes, ClearanceMm = 99 }, false) }, null));
            var nl = back.Input.Rows.Count == 1 ? back.Input.Rows[0] : null;
            check(P + "a not-live clash is a not-live row, NotRetested 'not live on this seat', with no also-sets; its Retest is ignored (A3)",
                nl != null && !nl.Live && nl.Verdict == PlanVerdict.NotRetested && nl.NotRetestedReason == "not live on this seat" && nl.AlsoSets == null && nl.RuleClearanceMm == 25);

            var retest = new RowBuilder.Retest { Verdict = PlanVerdict.NoLongerClashes, ClearanceMm = 40, Insulated = true };
            var unreadable = new MarkerPlan.Existing { MarkerUid = "m6", Record = null, Access = MarkerAccess.Unreadable };
            var ra = MarkerPlanAssembler.Assemble(Req(Ledger(Array.Empty<string>()),
                new[] { new MarkerAssemblerClash(Src(4), retest, true), new MarkerAssemblerClash(Src(5), retest, true), new MarkerAssemblerClash(Src(6), retest, true) },
                new[] { Ex(4, Rec(4, "JD-0004", MarkerStatus.New, "", "", 1)), unreadable }));
            var rr = ra.Input.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
            check(P + "a Retest is used only when a readable marker has the same triple (A2): without one the row keeps the seat's rule and Clashing",
                rr.Count == 3 && rr[Key(4)].Verdict == PlanVerdict.NoLongerClashes && rr[Key(4)].RuleClearanceMm == 40 && rr[Key(4)].RuleInsulated
                && rr[Key(5)].Verdict == PlanVerdict.Clashing && rr[Key(5)].RuleClearanceMm == 25 && !rr[Key(5)].RuleInsulated
                && rr[Key(6)].Verdict == PlanVerdict.Clashing && rr[Key(6)].RuleClearanceMm == 25);
        }

        private static void Pass(Action<string, bool?> check)
        {
            var own = Own();
            var other = new IssueNumbers.Counter { Uid = "c-kd", UserName = "kdoe", Prefix = "KD", Next = 3 };
            var opt = new MarkerPlan.Options { Today = Today, UserName = "jdoe", Cap = 7 };
            var req = new MarkerPlanAssembler.Request
            {
                Clashes = new List<MarkerPlanAssembler.Clash> { null, new MarkerPlanAssembler.Clash { Source = null }, Live(1).ToClash() },
                Markers = new List<MarkerPlan.Existing> { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 1)), null },
                OwnCounter = own, AllCounters = new List<IssueNumbers.Counter> { own, null, other }, Options = opt
            };
            var a = MarkerPlanAssembler.Assemble(req);
            check(P + "the own counter, the options and the counters are passed through; null entries are dropped; the lists are copies",
                a.Input.Counter == own && a.Input.Options == opt && a.Input.AllCounters.Count == 2 && a.Input.AllCounters.Contains(other) && a.Input.Markers.Count == 1 && a.Input.Rows.Count == 1
                && !ReferenceEquals(a.Input.Markers, req.Markers) && !ReferenceEquals(a.Input.AllCounters, req.AllCounters));
            var def = MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request());
            check(P + "no options become default options (cap 2,000); an empty request gives an empty input", def.Input.Options != null && def.Input.Options.Cap == 2000 && def.Input.Rows.Count == 0 && def.Input.Markers.Count == 0 && def.Input.Pending.Count == 0);
        }

        private static void Safety(Action<string, bool?> check)
        {
            var empty = new MarkerPlan.Result();
            check(P + "null and half-empty requests never throw and give an empty input", NoThrow(() =>
            {
                MarkerPlanAssembler.Assemble(null);
                MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request { Clashes = null, Decisions = null, Extras = null, Markers = null, AllCounters = null, Options = null });
            }) && MarkerPlanAssembler.Assemble(null).Input.Rows.Count == 0);
            check(P + "Lines with a null assembly, plan, outcome or name never throws and gives no lines", NoThrow(() =>
            {
                MarkerPlanAssembler.Lines(null, null, null, null, T(1));
                MarkerPlanAssembler.Lines(MarkerPlanAssembler.Assemble(null), null, null, null, T(1));
                MarkerPlanAssembler.Lines(MarkerPlanAssembler.Assemble(null), empty, null, null, T(1));
            }) && MarkerPlanAssembler.Lines(null, empty, null, "u", T(1)).Lines.Count == 0);
            var led = Ledger(new[] { LedgerExtras.PendingLine(Key(1), T(2), "jdoe", 1, MarkerStatus.Ignored, null, null) });
            var a = MarkerPlanAssembler.Assemble(Req(led, new[] { Live(1) }, new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 1)) }));
            var plan = new MarkerPlan.Result();
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), MarkerUid = "m1", Kind = PendingKind.Applied });
            plan.Creations.Add(new MarkerPlan.Creation { Triple = Tri(2), Record = Rec(2, "JD-0002", MarkerStatus.New, "", "", 1) });
            check(P + "a Failed outcome gives no lines at all, whatever the plan holds", MarkerPlanAssembler.Lines(a, plan, new MarkerPlanAssembler.Outcome { Failed = true }, "jdoe", T(60)).Lines.Count == 0
                && MarkerPlanAssembler.Lines(a, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60)).Lines.Count > 0);
        }

        private static void Hostile(Action<string, bool?> check)
{
    // Hand-built inputs a careless caller could produce: nulls inside every list, markers with no UniqueId or no record, an assembly with no request.
    var noKey = Src(1);
    noKey.Key = null;
    var emptyKey = Src(2);
    emptyKey.Key = "";
    var a = MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request
    {
        Clashes = new List<MarkerPlanAssembler.Clash> { new MarkerPlanAssembler.Clash { Source = noKey }, new MarkerPlanAssembler.Clash { Source = emptyKey }, Live(3).ToClash() },
        Markers = new List<MarkerPlan.Existing> { new MarkerPlan.Existing { MarkerUid = null, Record = Rec(1, "JD-0001", MarkerStatus.New, "", "", 1) }, Ex(3, Rec(3, "JD-0003", MarkerStatus.New, "", "", 1)) },
        Decisions = new Dictionary<string, ClashStatusEntry>(), Extras = new LedgerExtras.View()
    });
    check(P + "hostile request: a clash with no key or an empty key still makes a row (without a ledger decision), and a marker with no UniqueId is dropped",
        a.Input.Rows.Count == 3 && a.Input.Markers.Count == 1 && a.Input.Rows.All(r => r.Seed == null));

    var plan = new MarkerPlan.Result();
    plan.Pending.Add(null);
    plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = null, Kind = PendingKind.Dropped });
    plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), MarkerUid = null, Kind = PendingKind.Applied });
    plan.Changes.Add(null);
    plan.Changes.Add(new MarkerPlan.Change { MarkerUid = null, After = Rec(1, "JD-0001", MarkerStatus.New, "", "", 1) });
    plan.Changes.Add(new MarkerPlan.Change { MarkerUid = "m1", After = null });
    plan.Removals.Add(null);
    plan.Removals.Add(new MarkerPlan.Removal { MarkerUid = null });
    plan.Creations.Add(null);
    plan.Creations.Add(new MarkerPlan.Creation { Triple = Tri(9), Record = null });
    plan.Creations.Add(new MarkerPlan.Creation { Triple = null, Record = Rec(9, "JD-0009", MarkerStatus.New, "", "", 1) });
    plan.LostDecisions.Add(null);
    plan.LostDecisions.Add(new MarkerPlan.LostDecision { KeptUid = null, RemovedUid = null });
    plan.LostDecisions.Add(new MarkerPlan.LostDecision { KeptUid = "m1", RemovedUid = null, Status = MarkerStatus.Ignored });

    var noKeyRecord = Rec(5, "JD-0005", MarkerStatus.New, "", "", 1);
    noKeyRecord.Key = "";
    var inputs = new MarkerPlan.Input();
    inputs.Markers.Add(null);
    inputs.Markers.Add(new MarkerPlan.Existing { MarkerUid = null, Record = Rec(4, "JD-0004", MarkerStatus.New, "", "", 1) });
    inputs.Markers.Add(new MarkerPlan.Existing { MarkerUid = "m9", Record = null });
    inputs.Markers.Add(new MarkerPlan.Existing { MarkerUid = "m5", Record = noKeyRecord });
    inputs.Markers.Add(Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 1)));
    var open = Ledger(new[] { LedgerExtras.PendingLine(Key(1), T(2), "kdoe", 1, MarkerStatus.Ignored, null, null) });
    var hostile = new MarkerPlanAssembler.Assembly { Request = null, Input = inputs, UnmatchedPendingKeys = new List<string>(), PendingKeyToTriple = new Dictionary<string, string> { { Key(1), Tri(1) } } };
    MarkerPlanAssembler.Written w = null;
    bool quiet = NoThrow(() => { w = MarkerPlanAssembler.Lines(hostile, plan, new MarkerPlanAssembler.Outcome { NotWritten = null, CreationsMade = 5 }, null, T(60)); });
    check(P + "hostile plan: nulls in every list, unwritable entries and markers with no record or key never throw, and what is valid is still done",
        quiet && w != null && w.PendingClosed == 1 && w.DuplicatesNoted == 1
        && NoThrow(() => MarkerPlanAssembler.Lines(new MarkerPlanAssembler.Assembly { Input = null }, plan, null, "u", T(60)))
        && NoThrow(() => MarkerPlanAssembler.Lines(MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request()), plan, null, "u", T(60)))
        && NoThrow(() => MarkerPlanAssembler.Lines(new MarkerPlanAssembler.Assembly { Input = new MarkerPlan.Input { Markers = null } }, plan, null, "u", T(60)))
        && open.Extras.Pending.Count == 1);
}

        // ------------------------------------------------------------------ pending and duplicates

        private static void Pending(Action<string, bool?> check)
        {
            var led = Ledger(new[]
            {
                LedgerExtras.PendingLine(Key(7), T(2), "kdoe", 1, MarkerStatus.Ignored, null, null),
                LedgerExtras.PendingLine(Key(8), T(2), "kdoe", 1, MarkerStatus.Ignored, null, null),
                LedgerExtras.PendingLine("old8", T(2), "kdoe", 1, MarkerStatus.Ignored, null, null),
                LedgerExtras.PendingLine("zzz", T(2), "kdoe", 1, MarkerStatus.Ignored, null, null)
            });
            var old8 = Rec(8, "JD-0008", MarkerStatus.New, "", "", 1);
            old8.Key = "old8";
            var a = MarkerPlanAssembler.Assemble(Req(led, new[] { Live(8) }, new[] { Ex(7, Rec(7, "JD-0007", MarkerStatus.New, "", "", 1)), Ex(8, old8) }));
            check(P + "an open pending is mapped by a marker's key and by a row's key; one with neither is returned unmatched and is not in the plan's input",
                a.Input.Pending.Count == 3 && a.Input.Pending.Count(p => p.Triple == Tri(7)) == 1 && a.Input.Pending.Count(p => p.Triple == Tri(8)) == 2
                && a.UnmatchedPendingKeys.SequenceEqual(Texts("zzz"))
                && a.PendingKeyToTriple.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Texts("k7", "k8", "old8")) && a.PendingKeyToTriple[Key(8)] == Tri(8));

            var open = new List<string>();
            for (int i = 1; i <= 4; i++) open.Add(LedgerExtras.PendingLine(Key(i), T(2), "kdoe", 1, MarkerStatus.Ignored, null, null));
            open.Add(LedgerExtras.PendingLine("zzz", T(2), "kdoe", 1, MarkerStatus.Ignored, null, null));
            var led2 = Ledger(open);
            var a2 = MarkerPlanAssembler.Assemble(Req(led2, null, Enumerable.Range(1, 4).Select(i => Ex(i, Rec(i, "JD-000" + i, MarkerStatus.New, "", "", 1)))));
            var plan = new MarkerPlan.Result();
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), MarkerUid = "m1", Kind = PendingKind.Applied });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(2), MarkerUid = "m2", Kind = PendingKind.Dropped });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(3), MarkerUid = "m3", Kind = PendingKind.Kept });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(4), MarkerUid = "m4", Kind = PendingKind.Applied });
            var outcome = new MarkerPlanAssembler.Outcome();
            outcome.NotWritten.Add("m4");
            var w = MarkerPlanAssembler.Lines(a2, plan, outcome, "jdoe", T(60));
            var dones = Of(w, LedgerExtras.DoneEvent).Select(l => l.Split((char)9)[0]).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var after = LedgerExtras.Read(open.Concat(w.Lines));
            check(P + "Applied and Dropped triage are closed under the entry's own key; Kept stays open; an Applied one whose marker was not written stays open (A6); unmatched keys are closed",
                dones.SequenceEqual(Texts("k1", "k2", "zzz")) && after.Pending.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Texts("k3", "k4")) && w.PendingClosed == 3);

            var led3 = Ledger(new[] { LedgerExtras.PendingLine(Key(8), T(2), "kdoe", 1, MarkerStatus.Ignored, null, null), LedgerExtras.PendingLine("old8", T(2), "kdoe", 1, MarkerStatus.Ignored, null, null) });
            var a3 = MarkerPlanAssembler.Assemble(Req(led3, new[] { Live(8) }, new[] { Ex(8, old8) }));
            var plan3 = new MarkerPlan.Result();
            plan3.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(8), MarkerUid = "m8", Kind = PendingKind.Dropped });
            var w3 = MarkerPlanAssembler.Lines(a3, plan3, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            check(P + "two ledger keys that mean one pair are both closed (a Done line under only one would leave the other open)",
                Of(w3, LedgerExtras.DoneEvent).Select(l => l.Split((char)9)[0]).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Texts("k8", "old8"))
                && LedgerExtras.Read(led3.Lines.Concat(w3.Lines)).Pending.Count == 0);

            var dupLed = Ledger(new[] { LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001") });
            var dupA = MarkerPlanAssembler.Assemble(Req(dupLed, null, new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 1)), new MarkerPlan.Existing { MarkerUid = "m2", Record = Rec(1, "KD-0003", MarkerStatus.Ignored, "not kept", "kdoe", 1) } }));
            var dupPlan = new MarkerPlan.Result();
            dupPlan.Removals.Add(new MarkerPlan.Removal { MarkerUid = "m2", Reason = "merged duplicate", KeptUid = "m1" });
            dupPlan.LostDecisions.Add(new MarkerPlan.LostDecision { RemovedUid = "m2", RemovedIssueNo = "KD-0003", KeptUid = "m1", KeptIssueNo = "JD-0001", Status = MarkerStatus.Ignored, Note = "not kept", AssignedTo = "kdoe", By = "kdoe" });
            var wd = MarkerPlanAssembler.Lines(dupA, dupPlan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var seen = LedgerExtras.Read(dupLed.Lines.Concat(wd.Lines)).Duplicates;
            var o2 = new MarkerPlanAssembler.Outcome(); o2.NotWritten.Add("m2");
            var o1 = new MarkerPlanAssembler.Outcome(); o1.NotWritten.Add("m1");
            check(P + "a decision a merge did not keep is recorded under the kept marker's key; nothing is recorded when either marker was not written (A8)",
                seen.Count == 1 && seen[0].Key == Key(1) && seen[0].KeptIssueNo == "JD-0001" && seen[0].RemovedIssueNo == "KD-0003" && seen[0].Status == MarkerStatus.Ignored && seen[0].Note == "not kept" && seen[0].AssignedTo == "kdoe"
                && wd.DuplicatesNoted == 1 && Of(MarkerPlanAssembler.Lines(dupA, dupPlan, o2, "jdoe", T(60)), LedgerExtras.DuplicateEvent).Count == 0
                && Of(MarkerPlanAssembler.Lines(dupA, dupPlan, o1, "jdoe", T(60)), LedgerExtras.DuplicateEvent).Count == 0);
        }

        // ------------------------------------------------------------------ the model followed

        private static void Followed(Action<string, bool?> check)
        {
            var led = Ledger(new[] { Dec(Key(2), "Ignored", T(1), "kdoe", "x"), LedgerExtras.IssueLine(Key(2), T(2), "kdoe", "KD-0007") });
            var req = Req(led, new[] { Live(1), Live(2) }, null);
            var a = MarkerPlanAssembler.Assemble(req);
            var plan = MarkerPlan.Build(a.Input);
            var w = MarkerPlanAssembler.Lines(a, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var after = Ledger(led.Lines.Concat(w.Lines));
            var statusEvents = Texts("New", "Pending", "Coordinated", "Ignored", ClashStatusReplay.GoneEvent);
            check(P + "a created marker's issue number reaches the ledger exactly once; a number the ledger already holds, and a decision it already holds, write nothing",
                plan.Creations.Count == 2 && plan.Creations[1].Record.IssueNo == "KD-0007"
                && Of(w, LedgerExtras.IssueEvent).Count(l => l.StartsWith(Key(1) + "\t", StringComparison.Ordinal)) == 1 && after.Extras.Issues[Key(1)].IssueNo == plan.Creations[0].Record.IssueNo
                && !w.Lines.Any(l => l.StartsWith(Key(2) + "\t", StringComparison.Ordinal)) && !w.Lines.Any(l => l.StartsWith(Key(1) + "\t", StringComparison.Ordinal) && statusEvents.Contains(Ev(l))));

            var some = new MarkerPlanAssembler.Outcome { CreationsMade = 1 };
            var w1 = MarkerPlanAssembler.Lines(a, plan, some, "jdoe", T(60));
            check(P + "only the first CreationsMade creations are followed", Of(w1, LedgerExtras.IssueEvent).Count == 1 && w1.Lines.All(l => l.StartsWith(Key(1) + "\t", StringComparison.Ordinal)));

            var held = Ledger(new[] { Dec(Key(1), "Ignored", T(1), "kdoe", "x"), LedgerExtras.IssueLine(Key(1), T(2), "kdoe", "JD-0001") });
            var ignored = Rec(1, "JD-0001", MarkerStatus.Ignored, "x", "", 1);
            var resolved = Rec(1, "JD-0001", MarkerStatus.Resolved, "x", "", 2);
            var ha = MarkerPlanAssembler.Assemble(Req(held, null, new[] { Ex(1, ignored) }));
            var resolve = new MarkerPlan.Result();
            resolve.Changes.Add(new MarkerPlan.Change { MarkerUid = "m1", Before = ignored, After = resolved, Resolved = true });
            var notWritten = new MarkerPlanAssembler.Outcome();
            notWritten.NotWritten.Add("m1");
            var noDecision = MarkerPlanAssembler.Assemble(Req(Ledger(new[] { LedgerExtras.IssueLine(Key(1), T(2), "kdoe", "JD-0001") }), null, new[] { Ex(1, ignored) }));
            check(P + "a Resolved change gives a Gone line only over a standing decision, and none when that change was not written",
                Of(MarkerPlanAssembler.Lines(ha, resolve, new MarkerPlanAssembler.Outcome(), "jdoe", T(60)), ClashStatusReplay.GoneEvent).Count == 1
                && Of(MarkerPlanAssembler.Lines(ha, resolve, notWritten, "jdoe", T(60)), ClashStatusReplay.GoneEvent).Count == 0
                && Of(MarkerPlanAssembler.Lines(noDecision, resolve, new MarkerPlanAssembler.Outcome(), "jdoe", T(60)), ClashStatusReplay.GoneEvent).Count == 0);

            var toCoordinated = Rec(1, "JD-0001", MarkerStatus.Coordinated, "y", "", 2);
            var change = new MarkerPlan.Result();
            change.Changes.Add(new MarkerPlan.Change { MarkerUid = "m1", Before = ignored, After = toCoordinated });
            check(P + "a change that was written is followed into the ledger; one that was not leaves the ledger following the old record",
                Of(MarkerPlanAssembler.Lines(ha, change, new MarkerPlanAssembler.Outcome(), "jdoe", T(60)), "Coordinated").Count == 1
                && MarkerPlanAssembler.Lines(ha, change, notWritten, "jdoe", T(60)).Lines.Count == 0);

            var assigned = Rec(1, "JD-0001", MarkerStatus.Ignored, "x", "asmith", 1);
            var assignedA = MarkerPlanAssembler.Assemble(Req(held, null, new[] { Ex(1, assigned) }));
            var remove = new MarkerPlan.Result();
            remove.Removals.Add(new MarkerPlan.Removal { MarkerUid = "m1", Reason = "retention" });
            var removedNotWritten = new MarkerPlanAssembler.Outcome();
            removedNotWritten.NotWritten.Add("m1");
            check(P + "a removed marker gives no line, and one whose removal was not written is still followed",
                MarkerPlanAssembler.Lines(assignedA, remove, new MarkerPlanAssembler.Outcome(), "jdoe", T(60)).Lines.Count == 0
                && Of(MarkerPlanAssembler.Lines(assignedA, remove, removedNotWritten, "jdoe", T(60)), LedgerExtras.AssignedEvent).Count == 1);

            var pend = Ledger(new[]
            {
                LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001"), LedgerExtras.PendingLine(Key(1), T(2), "kdoe", 1, MarkerStatus.Ignored, "n", null),
                LedgerExtras.IssueLine(Key(2), T(1), "kdoe", "JD-0002"), LedgerExtras.PendingLine(Key(2), T(2), "kdoe", 1, MarkerStatus.Ignored, "n", null)
            });
            var heldMarker = Ex(2, Rec(2, "JD-0002", MarkerStatus.New, "", "", 1));
            heldMarker.Access = MarkerAccess.HeldByOther;
            heldMarker.HeldBy = "kdoe";
            var pa = MarkerPlanAssembler.Assemble(Req(pend, new[] { new MarkerAssemblerClash(Src(1), Clashing(), true), new MarkerAssemblerClash(Src(2), Clashing(), true) }, new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 1)), heldMarker }));
            var pplan = MarkerPlan.Build(pa.Input);
            var pw = MarkerPlanAssembler.Lines(pa, pplan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var pafter = Ledger(pend.Lines.Concat(pw.Lines));
            check(P + "the decision a just-applied triage wrote is followed in the same round; a Kept pending key gets no line and stays open",
                pplan.Pending.Any(p => p.Triple == Tri(1) && p.Kind == PendingKind.Applied) && pplan.Pending.Any(p => p.Triple == Tri(2) && p.Kind == PendingKind.Kept)
                && pafter.Decisions.ContainsKey(Key(1)) && pafter.Decisions[Key(1)].Kind == ClashStatusKind.Ignored && !pafter.Extras.Pending.ContainsKey(Key(1))
                && pafter.Extras.Pending.ContainsKey(Key(2)) && !pw.Lines.Any(l => l.StartsWith(Key(2) + "\t", StringComparison.Ordinal)));
        }

        private static void Timing(Action<string, bool?> check)
        {
            var open = LedgerExtras.PendingLine(Key(1), T(300), "kdoe", 1, MarkerStatus.Ignored, null, null);
            var led = Ledger(new[] { open, LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001"), Dec(Key(5), "Coordinated", T(400), "kdoe", "late"), LedgerExtras.IssueLine(Key(5), T(1), "kdoe", "JD-0005") });
            var a = MarkerPlanAssembler.Assemble(Req(led, null, new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 1)), Ex(5, Rec(5, "JD-0005", MarkerStatus.Ignored, "late", "", 1)) }));
            var plan = new MarkerPlan.Result();
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), MarkerUid = "m1", Kind = PendingKind.Dropped });
            var w = MarkerPlanAssembler.Lines(a, plan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var done = Of(w, LedgerExtras.DoneEvent);
            var ign = Of(w, "Ignored");
            Func<string, DateTime> when = l => DateTime.Parse(l.Split((char)9)[2], Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var after = Ledger(led.Lines.Concat(w.Lines));
            check(P + "with this PC's clock behind the ledger, the lines are one tick after what they must outrank, so they still win (D30)",
                done.Count == 1 && when(done[0]) == T(300).AddTicks(1) && !after.Extras.Pending.ContainsKey(Key(1))
                && ign.Count == 1 && when(ign[0]) == T(400).AddTicks(1) && after.Decisions[Key(5)].Kind == ClashStatusKind.Ignored);
        }

        // ------------------------------------------------------------------ end to end

        private static MarkerAssemblerClash WithRetest(int n) { return new MarkerAssemblerClash(Src(n), Clashing(), true); }

        private static void EndToEnd(Action<string, bool?> check)
        {
            var led = Ledger(new[] { Dec(Key(2), "Coordinated", T(1), "kdoe", "sleeved"), LedgerExtras.AssignedLine(Key(3), T(2), "kdoe", "asmith") });
            var old = Rec(1, "JD-0001", MarkerStatus.New, "", "", 1);
            var a = MarkerPlanAssembler.Assemble(Req(led, new[] { WithRetest(1), Live(2), Live(3) }, new[] { Ex(1, old) }));
            var plan = MarkerPlan.Build(a.Input);
            var outcome = new MarkerPlanAssembler.Outcome();
            var w = MarkerPlanAssembler.Lines(a, plan, outcome, "jdoe", T(60));
            var led2 = Ledger(led.Lines.Concat(w.Lines));
            var req2 = Req(led2, new[] { WithRetest(1), WithRetest(2), WithRetest(3) }, Post(a.Input, plan, outcome));
            req2.OwnCounter = plan.CounterAfter ?? Own();
            var a2 = MarkerPlanAssembler.Assemble(req2);
            var plan2 = MarkerPlan.Build(a2.Input);
            var w2 = MarkerPlanAssembler.Lines(a2, plan2, new MarkerPlanAssembler.Outcome(), "jdoe", T(120));
            check(P + "end to end: assemble, plan, apply, lines, read back; a second Refresh plans nothing and writes no line",
                plan.Creations.Count == 2 && plan2.Creations.Count == 0 && plan2.Changes.Count == 0 && plan2.Removals.Count == 0 && w2.Lines.Count == 0
                && led2.Decisions.ContainsKey(Key(2)) && led2.Decisions[Key(2)].Kind == ClashStatusKind.Coordinated && led2.Extras.Assigned[Key(3)].Assignee == "asmith"
                && led2.Extras.Issues.Count == 3);

            var dled = Ledger(new[] { Dec(Key(1), "Ignored", T(1), "kdoe", "decided"), LedgerExtras.PendingLine(Key(1), T(2), "kdoe", 1, MarkerStatus.Ignored, "decided", null) });
            var da = MarkerPlanAssembler.Assemble(Req(dled, new[] { WithRetest(1) }, new[] { Ex(1, Rec(1, "JD-0001", MarkerStatus.New, "", "", 5)) }));
            var dplan = MarkerPlan.Build(da.Input);
            var dout = new MarkerPlanAssembler.Outcome();
            var dw = MarkerPlanAssembler.Lines(da, dplan, dout, "jdoe", T(60));
            var dafter = Ledger(dled.Lines.Concat(dw.Lines));
            check(P + "end to end: a pending triage on a marker that changed is dropped and closed, and the model's value stays in the ledger",
                dplan.Pending.Count == 1 && dplan.Pending[0].Kind == PendingKind.Dropped && !dafter.Extras.Pending.ContainsKey(Key(1))
                && (!dafter.Decisions.ContainsKey(Key(1)) || dafter.Decisions[Key(1)].Kind == ClashStatusKind.New) && dw.Overridden == 1);

            var first = Rec(1, "JD-0001", MarkerStatus.Ignored, "keep", "", 1);
            var second = Rec(1, "JD-0002", MarkerStatus.Coordinated, "lose", "asmith", 1);
            var mled = Ledger(new[] { LedgerExtras.IssueLine(Key(1), T(1), "kdoe", "JD-0001") });
            var mreq = Req(mled, new[] { WithRetest(1) }, new[] { Ex(1, first), new MarkerPlan.Existing { MarkerUid = "m1b", Record = second, Access = MarkerAccess.Free, ShownHash = Ex(1, first).ShownHash } });
            var ma = MarkerPlanAssembler.Assemble(mreq);
            var mplan = MarkerPlan.Build(ma.Input);
            var mw = MarkerPlanAssembler.Lines(ma, mplan, new MarkerPlanAssembler.Outcome(), "jdoe", T(60));
            var mseen = LedgerExtras.Read(mled.Lines.Concat(mw.Lines)).Duplicates;
            check(P + "end to end: two markers for one pair merge, and the decision that was not kept is in the Record",
                mplan.LostDecisions.Count == 1 && mseen.Count == mplan.LostDecisions.Count && mseen[0].Key == Key(1) && mw.DuplicatesNoted == 1);
        }

        // ------------------------------------------------------------------ fuzz

        private sealed class Case
        {
            public MarkerPlanAssembler.Request Req;
            public List<string> Lines;
            public DateTime Now;
            public int Seed;
        }

        private static Case Make(int seed)
        {
            var r = new Random(seed);
            var c = new Case { Seed = seed, Now = T(r.Next(0, 90)), Lines = new List<string>() };
            var clashes = new List<MarkerPlanAssembler.Clash>();
            var markers = new List<MarkerPlan.Existing>();
            var statuses = new[] { MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Reopened, MarkerStatus.Coordinated, MarkerStatus.Ignored, MarkerStatus.Resolved };
            var kinds = Texts("Pending", "Coordinated", "Ignored");
            string[] notes = { "", "x", "y" };
            string[] severities = { "Hard", "Clearance", "Unverified" };
            int pairs = 1 + r.Next(7);
            for (int i = 1; i <= pairs; i++)
            {
                int markersHere = r.Next(100) < 55 ? (r.Next(100) < 12 ? 2 : 1) : 0;
                if (r.Next(100) < 75)
                {
                    bool live = r.Next(100) < 85;
                    RowBuilder.Retest retest = null;
                    if (markersHere > 0 && live)
                    {
                        int v = r.Next(10);
                        retest = new RowBuilder.Retest
                        {
                            Verdict = v < 6 ? PlanVerdict.Clashing : v < 8 ? PlanVerdict.NoLongerClashes : PlanVerdict.NotRetested, Reason = "r", ClearanceMm = r.Next(100) < 85 ? 25 : 30
                        };
                    }
                    clashes.Add(new MarkerPlanAssembler.Clash { Source = Src(i, severities[r.Next(3)]), Retest = retest, Live = live });
                }
                for (int j = 0; j < markersHere; j++)
                {
                    var rec = Rec(i, (r.Next(100) < 85 ? "JD-" : "KD-") + (i * 10 + j).ToString("D4", Inv), statuses[r.Next(statuses.Length)], notes[r.Next(3)], r.Next(100) < 25 ? "asmith" : "", 1 + r.Next(3));
                    if (r.Next(100) < 15) rec.Key = "old" + i;
                    markers.Add(new MarkerPlan.Existing
                    {
                        MarkerUid = "m" + i + "_" + j, Record = rec, Access = r.Next(100) < 85 ? MarkerAccess.Free : MarkerAccess.HeldByOther, HeldBy = "kdoe",
                        ShownHash = r.Next(100) < 80 ? RowBuilder.HashOf(RowBuilder.ShownOf(Src(i))) : "stale"
                    });
                }
                if (r.Next(100) < 50)
                {
                    int t = 1 + r.Next(50);
                    string kind = r.Next(100) < 20 ? "New" : kinds[r.Next(3)];
                    c.Lines.Add(Dec(Key(i), kind, T(t), "kdoe", kind == "New" ? "n" : notes[r.Next(3)]));
                    if (r.Next(100) < 20) c.Lines.Add(Gone(Key(i), T(t + 1 + r.Next(5))));
                }
                if (r.Next(100) < 25) c.Lines.Add(LedgerExtras.AssignedLine(Key(i), T(1 + r.Next(50)), "kdoe", r.Next(2) == 0 ? "asmith" : ""));
                if (r.Next(100) < 25) c.Lines.Add(LedgerExtras.IssueLine(Key(i), T(1 + r.Next(50)), "kdoe", "KD-" + (900 + i).ToString("D4", Inv)));
                if (r.Next(100) < 25)
                {
                    MarkerStatus? st = r.Next(2) == 0 ? (MarkerStatus?)MarkerStatus.Ignored : null;
                    c.Lines.Add(LedgerExtras.PendingLine(r.Next(100) < 15 ? "ghost" + i : Key(i), T(1 + r.Next(50)), "kdoe", 1 + r.Next(3), st, r.Next(2) == 0 ? "n" : null, r.Next(3) == 0 ? "bsmith" : null));
                }
            }
            var own = Own();
            own.Next = 1 + r.Next(10);
            var counters = new List<IssueNumbers.Counter>();
            if (r.Next(100) < 40)
            {
                counters.Add(own);
                counters.Add(new IssueNumbers.Counter { Uid = "c-kd", UserName = "kdoe", Prefix = r.Next(2) == 0 ? "JD" : "KD", Next = 5 });
            }
            c.Req = new MarkerPlanAssembler.Request { Clashes = clashes, Markers = markers, OwnCounter = own, AllCounters = counters, Options = Opt() };
            return c;
        }

        private sealed class Run
        {
            public MarkerPlanAssembler.Assembly Asm;
            public MarkerPlan.Result Plan;
            public MarkerPlanAssembler.Outcome Out;
            public MarkerPlanAssembler.Written W;
            public Led Before;
            public Led After;
        }

        private static Run Go(Case c, bool shuffle, MarkerPlanAssembler.Outcome fixedOutcome)
        {
            var req = c.Req;
            var lines = c.Lines;
            if (shuffle)
            {
                var r = new Random(c.Seed + 1000);
                req = new MarkerPlanAssembler.Request
                {
                    Clashes = req.Clashes.OrderBy(x => r.Next()).ToList(), Markers = req.Markers.OrderBy(x => r.Next()).ToList(), OwnCounter = req.OwnCounter,
                    AllCounters = req.AllCounters.OrderBy(x => r.Next()).ToList(), Options = req.Options
                };
                lines = lines.OrderBy(x => r.Next()).ToList();
            }
            var led = Ledger(lines);
            req.Decisions = led.Decisions;
            req.Extras = led.Extras;
            var run = new Run { Before = led, Asm = MarkerPlanAssembler.Assemble(req) };
            run.Plan = MarkerPlan.Build(run.Asm.Input);
            run.Out = fixedOutcome ?? new MarkerPlanAssembler.Outcome();
            run.W = MarkerPlanAssembler.Lines(run.Asm, run.Plan, run.Out, "jdoe", c.Now);
            run.After = Ledger(lines.Concat(run.W.Lines));
            return run;
        }

        private static MarkerPlanAssembler.Outcome RandomOutcome(Case c, MarkerPlan.Result plan)
        {
            var r = new Random(c.Seed + 5000);
            var o = new MarkerPlanAssembler.Outcome { Failed = r.Next(100) < 5 };
            foreach (var uid in plan.Changes.Select(x => x.MarkerUid).Concat(plan.Removals.Select(x => x.MarkerUid)))
                if (r.Next(100) < 15) o.NotWritten.Add(uid);
            o.CreationsMade = r.Next(100) < 70 ? -1 : r.Next(plan.Creations.Count + 1);
            return o;
        }

        private static void Fuzz(Action<string, bool?> check)
        {
            int bad = 0, thrown = 0, again = 0, open = 0, dup = 0, shipped = 0, wide = 0, counts = 0, shuffled = 0, ran = 0;
            int postTotal = 0, lostSeen = 0, linesTotal = 0, pendingChecked = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var c = Make(seed);
                Run run = null;
                try
                {
                    var first = Go(c, false, null);
                    run = Go(c, false, RandomOutcome(c, first.Plan));
                }
                catch (Exception) { thrown++; continue; }
                ran++;
                if (run.Out.Failed)
                {
                    if (run.W.Lines.Count != 0) bad++;
                    continue;
                }

                var post = Post(run.Asm.Input, run.Plan, run.Out);
                postTotal += post.Count;
                lostSeen += run.Plan.LostDecisions.Count;
                linesTotal += run.W.Lines.Count;
                if (MarkerReconcile.Plan(Models(post), run.After.Decisions, run.After.Extras, c.Now).Lines.Count != 0) again++;

                foreach (var key in run.Before.Extras.Pending.Keys)
                {
                    string triple;
                    bool mapped = run.Asm.PendingKeyToTriple.TryGetValue(key, out triple);
                    bool stays;
                    if (!mapped) stays = false;
                    else
                    {
                        var o = run.Plan.Pending.FirstOrDefault(p => p.Triple == triple);
                        if (o == null) continue;
                        stays = o.Kind == PendingKind.Kept || (o.Kind == PendingKind.Applied && run.Out.NotWritten.Contains(o.MarkerUid));
                    }
                    pendingChecked++;
                    if (stays != run.After.Extras.Pending.ContainsKey(key)) { open++; break; }
                }

                var keyOf = post.Where(m => m.Record != null).GroupBy(m => m.MarkerUid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Record.Key, StringComparer.Ordinal);
                foreach (var l in run.Plan.LostDecisions)
                {
                    if (run.Out.NotWritten.Contains(l.KeptUid) || run.Out.NotWritten.Contains(l.RemovedUid)) continue;
                    string kept;
                    if (!keyOf.TryGetValue(l.KeptUid, out kept)) continue;
                    if (!run.After.Extras.Duplicates.Any(d => d.Key == kept && d.RemovedIssueNo == (l.RemovedIssueNo ?? "") && d.KeptIssueNo == (l.KeptIssueNo ?? "") && d.Status == l.Status)) { dup++; break; }
                }

                // The reader that SHIPPED (a verbatim copy, which drops a New decision that has a note by design) is only compared with itself:
                // with the new lines in the ledger it must drop and repair nothing, and read the same decisions and event count as with only the status lines.
                var statusEvents = new HashSet<string>(Texts("New", "Pending", "Coordinated", "Ignored", ClashStatusReplay.GoneEvent), StringComparer.Ordinal);
                var shippedAll = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
                var shippedStatus = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
                int e1, l1, r1, e2, l2, r2;
                ShippedClashStatusReplay.Replay(c.Lines.Concat(run.W.Lines), shippedAll, out e1, out l1, out r1);
                ShippedClashStatusReplay.Replay(c.Lines.Concat(run.W.Lines.Where(l => statusEvents.Contains(Ev(l)))), shippedStatus, out e2, out l2, out r2);
                bool same = l1 == 0 && r1 == 0 && e1 == e2 && shippedAll.Count == shippedStatus.Count && shippedAll.All(kv =>
                {
                    ClashStatusEntry other;
                    return shippedStatus.TryGetValue(kv.Key, out other) && other.Kind == kv.Value.Kind && (other.Note ?? "") == (kv.Value.Note ?? "") && other.Gone == kv.Value.Gone;
                });
                if (!same) shipped++;
                if (run.W.Lines.Any(l => l.Split((char)9).Length == 10)) wide++;

                if (run.W.PendingClosed != Of(run.W, LedgerExtras.DoneEvent).Count || run.W.DuplicatesNoted != Of(run.W, LedgerExtras.DuplicateEvent).Count) counts++;

                var other2 = Go(c, true, run.Out);
                if (!other2.W.Lines.SequenceEqual(run.W.Lines, StringComparer.Ordinal)) shuffled++;
            }
            check(P + "fuzz (300 histories): assembling, planning and writing the lines never throws, and a Failed outcome writes nothing", ran == 300 && thrown == 0 && bad == 0);
            check(P + "fuzz: after the lines, following the markers into the ledger again finds nothing to write (nothing is lost, nothing is left over)", again == 0 && ran == 300 && postTotal > 300);
            check(P + "fuzz: a pending triage is closed exactly when the plan applied it (and its marker was written), dropped it, or found no marker; Kept and unwritten ones stay open", open == 0 && ran == 300 && pendingChecked > 20);
            check(P + "fuzz: every decision a merge did not keep is recorded under the kept marker's key", dup == 0 && ran == 300 && lostSeen >= 5);
            check(P + "fuzz: the shipped reader (abe7dfd, verbatim copy) drops and repairs none of the lines and reads the same decisions as with only the status lines, and no line has ten fields", shipped == 0 && wide == 0 && ran == 300 && linesTotal > 300);
            check(P + "fuzz: the counts in the result match the lines written", counts == 0 && ran == 300 && linesTotal > 300);
            check(P + "fuzz: the same request with every list shuffled gives the same lines", shuffled == 0 && ran == 300 && linesTotal > 300);
        }

        /// <summary>A clash and its optional re-test, written in the checks; turned into the assembler's own type.</summary>
        private sealed class MarkerAssemblerClash
        {
            private readonly RowBuilder.Source _source;
            private readonly RowBuilder.Retest _retest;
            private readonly bool _live;

            public MarkerAssemblerClash(RowBuilder.Source source, RowBuilder.Retest retest, bool live)
            {
                _source = source; _retest = retest; _live = live;
            }

            public MarkerPlanAssembler.Clash ToClash()
            {
                return new MarkerPlanAssembler.Clash { Source = _source, Retest = _retest, Live = _live };
            }
        }
    }
}
