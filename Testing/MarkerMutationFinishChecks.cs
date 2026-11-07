using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 11: the survivors of the third Stryker run that a true check can kill (a null assignee, a creation with no seed,
    /// the repair of issue numbers for a user who is not the counter's owner or has no name, the order of the rows of a panel row group, the search
    /// text, and a few texts and defaults). Revit-free, so both tiers run it. Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationFinishChecks
    {
        private const string P = "markers mutation finish: ";
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        private static readonly string[] AB = { "a", "b" };
        private static readonly string[] OneNo = { "JD-0001" };
        private static readonly string[] Ab = { "ab" };
        private static readonly string[] Brace = { "}" };
        private static readonly string[] Renumbered = { "m2>JD2-0005", "m3>JD3-0005" };
        private static readonly string[] ZY = { "Z", "Y" };

        public static void RunAll(Action<string, bool?> check)
        {
            NullAssignee(check);
            NoSeed(check);
            Owners(check);
            Group(check);
            Search(check);
            Defaults(check);
            Texts(check);
            Reasons(check);
            Separators(check);
            Linked(check);
            Blank(check);
            OneUser(check);
            AsGiven(check);
            Prefixes(check);
            RoundTrip(check);
        }

        private static string[] Texts_(params string[] items) { return items; }
        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

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

        private static MarkerPlan.Existing Ex(string uid, MarkerRecord r) { return new MarkerPlan.Existing { MarkerUid = uid, Record = r, Access = MarkerAccess.Free }; }

        private static MarkerPlan.Result Build(IEnumerable<MarkerPlan.Row> rows, IEnumerable<MarkerPlan.Existing> markers, string user, IssueNumbers.Counter counter, IEnumerable<IssueNumbers.Counter> all)
        {
            return MarkerPlan.Build(new MarkerPlan.Input
            {
                Rows = (rows ?? Array.Empty<MarkerPlan.Row>()).ToList(), Markers = (markers ?? Array.Empty<MarkerPlan.Existing>()).ToList(), Counter = counter,
                AllCounters = (all ?? Array.Empty<IssueNumbers.Counter>()).ToList(), Options = new MarkerPlan.Options { Today = Today, UserName = user }
            });
        }

        // ------------------------------------------------------------------ a duplicate whose assignee is missing, not empty

        private static void NullAssignee(Action<string, bool?> check)
        {
            var kept = Rec(1, "JD-0001", "jdoe"); kept.Note = "x";
            var dup = Rec(1, "JD-0002", "jdoe"); dup.Status = MarkerStatus.Reopened; dup.Note = ""; dup.AssignedTo = null; dup.ChangedBy = "kdoe";
            var plan = Build(null, new[] { Ex("m1", kept), Ex("m2", dup) }, "jdoe", new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }, null);
            check(P + "a removed duplicate with a re-test status, an empty note and a MISSING assignee had no decision of its own, so nothing of the kept marker's is reported lost", plan.Merges == 1 && plan.LostDecisions.Count == 0);
        }

        // ------------------------------------------------------------------ a new marker from a row with no seed

        private static void NoSeed(Action<string, bool?> check)
        {
            var made = Build(new[] { Rw(1) }, null, "jdoe", new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }, null).Creations[0].Record;
            check(P + "a new marker from a row with no seed has no note, no assignee, no author of a change and no change time", made.Note == "" && made.AssignedTo == "" && made.ChangedBy == "" && made.ChangedAt == null && made.Status == MarkerStatus.New);
        }

        // ------------------------------------------------------------------ issue numbers: whose markers are renumbered

        private static void Owners(Action<string, bool?> check)
        {
            var noUid = new IssueNumbers.Counter { Uid = null, UserName = "jdoe", Prefix = "JD", Next = 1 };
            var twin = new IssueNumbers.Counter { Uid = null, UserName = "kdoe", Prefix = "JD", Next = 3 };
            var unowned = Build(null, null, "jdoe", noUid, new[] { new IssueNumbers.Counter { Uid = null, UserName = "jdoe", Prefix = "JD", Next = 1 }, twin });
            check(P + "a user whose counter has no UniqueId is never taken for the loser of a prefix collision", unowned.PrefixMove == null && unowned.CounterAfter.Prefix == "JD");

            var losing = new IssueNumbers.Counter { Uid = "u-zz", UserName = "jdoe", Prefix = "JD", Next = 5 };
            var winner = new IssueNumbers.Counter { Uid = "u-aa", UserName = "kdoe", Prefix = "JD", Next = 3 };
            var reported = Rec(1, "JD-0004", "jdoe");
            var stranger = Build(null, new[] { Ex("m1", reported) }, "asmith", losing, new[] { winner, losing });
            check(P + "when the refreshing user is not the owner of the counter that moved, a marker reported by that owner is not renumbered by this user", stranger.PrefixMove != null && stranger.Changes.Count == 0);

            var mine = Rec(1, "KD-0005", null);
            var theirs = Rec(2, "KD-0005", "kdoe");
            var nameless = Build(null, new[] { Ex("m1", mine), Ex("m2", theirs) }, null, null, null);
            check(P + "with no user name a marker with no reporter is the user's own: it takes the counter's prefix on a number another marker holds",
                nameless.Changes.Count == 1 && nameless.Changes[0].MarkerUid == "m1" && nameless.Changes[0].After.IssueNo == "XX-0005");
        }

        // ------------------------------------------------------------------ the first member of a panel row group

        private static ScheduleSnapshot Snap(params Tuple<string, RowScheduleInfo>[] rows)
        {
            var s = new ScheduleSnapshot { Ready = true, Me = "jdoe", MarkerCount = rows.Length };
            foreach (var r in rows) { r.Item2.HasMarker = true; s.Rows[r.Item1] = r.Item2; }
            return s;
        }

        private static Tuple<string, RowScheduleInfo> Row(string key, string issue, string group, string assigned = null)
        {
            return Tuple.Create(key, new RowScheduleInfo { IssueNo = issue, GroupLabel = group, AssignedTo = assigned });
        }

        private static string GroupOf(Tuple<string, RowScheduleInfo> a, Tuple<string, RowScheduleInfo> b)
        {
            var s = Snap(a, b);
            string forward = RowScheduleText.ForKeys(s, new[] { a.Item1, b.Item1 }).Group;
            string backward = RowScheduleText.ForKeys(s, new[] { b.Item1, a.Item1 }).Group;
            return forward == backward ? forward : "(depends on the key order: " + forward + " / " + backward + ")";
        }

        private static void Group(Action<string, bool?> check)
        {
            check(P + "of the markers in one row the group label comes from the first by issue number: a readable number before other text, before none",
                GroupOf(Row("a", "zzz", "GA"), Row("b", "JD-0001", "GB")) == "GB" && GroupOf(Row("a", "", "GA", "a"), Row("b", "x", "GB", "b")) == "GB");
            check(P + "...other texts by ordinal order; two with no number by who they are assigned to",
                GroupOf(Row("a", "b", "GB"), Row("b", "a", "GA")) == "GA" && GroupOf(Row("a", "", "GB", "b"), Row("b", "", "GA", "a")) == "GA");
            check(P + "...and no label at all gives an empty group", RowScheduleText.ForKeys(Snap(Row("a", "JD-0001", null), Row("b", "JD-0002", "")), AB).Group == "");
        }

        private static void Search(Action<string, bool?> check)
        {
            var first = new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0001", MergedIssueNos = new List<string> { "", "JD-0009" }, AssignedTo = null };
            var second = new RowScheduleInfo { HasMarker = true, IssueNo = "", AssignedTo = "asmith" };
            var s = new ScheduleSnapshot { Ready = true, Me = "jdoe", MarkerCount = 2 };
            s.Rows["a"] = first; s.Rows["b"] = second;
            check(P + "the search text of a row is its issue numbers and assignees, empty ones left out, one space between", RowScheduleText.SearchTextOf(s, AB) == "JD-0001 JD-0009 asmith");
        }

        // ------------------------------------------------------------------ defaults

        private static void Defaults(Action<string, bool?> check)
        {
            check(P + "an access verdict nobody filled names no holder and gives no reason", new MarkerAccessVerdict().HeldBy == "" && new MarkerAccessVerdict().Reason == "");
            var model = new AssignModel(OneNo, null, null, null);
            model.OtherName = null;
            check(P + "an assign model whose typed name is set to nothing reads back an empty name", model.OtherName == "");
        }

        // ------------------------------------------------------------------ texts

        private static void Texts(Action<string, bool?> check)
        {
            var counts = new MarkerSummary.Counts { Clashes = 2, PassedOver = new List<MarkerSummary.Reason> { new MarkerSummary.Reason { Text = "A", Count = 1 }, new MarkerSummary.Reason { Text = null, Count = 1 } } };
            check(P + "the summary lists reasons of equal count with a missing text first (as 'other'), then by text", MarkerSummary.Describe(counts).Contains("(1 other, 1 A)"));
            check(P + "the line of a set that is off says how many markers were not re-tested, in one or several",
                MarkerSummary.SetLine(new MarkerSummary.SetCounts { Name = "Set A", SetOff = true, NotRetested = 1 }) == "Set A (set off on this seat): 1 marker not re-tested."
                && MarkerSummary.SetLine(new MarkerSummary.SetCounts { Name = "Set A", SetOff = true, NotRetested = 2 }) == "Set A (set off on this seat): 2 markers not re-tested.");

            var noKey = new RowBuilder.Source { Key = null, HostUid = "h1", OtherUid = "o9", HostId = 1, OtherId = 9 };
            var keyA = new RowBuilder.Source { Key = "A", HostUid = "h2", OtherUid = "o2", HostId = 2, OtherId = 2 };
            var assembly = MarkerPlanAssembler.Assemble(new MarkerPlanAssembler.Request
            {
                Clashes = new List<MarkerPlanAssembler.Clash> { new MarkerPlanAssembler.Clash { Source = keyA }, new MarkerPlanAssembler.Clash { Source = noKey } }, Extras = new LedgerExtras.View(),
                Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            });
            check(P + "the rows of the plan come in key order with a clash that has no key before one keyed 'A'", assembly.Input.Rows.Select(r => r.HostUid).SequenceEqual(Texts_("h1", "h2")));

            var plan = new MarkerPlan.Result();
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(1), Kind = PendingKind.Kept, Message = null });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(2), Kind = PendingKind.Kept, Message = "" });
            plan.Pending.Add(new MarkerPlan.PendingOutcome { Triple = Tri(3), Kind = PendingKind.Dropped, Message = "" });
            var written = MarkerPlanAssembler.Lines(new MarkerPlanAssembler.Assembly(), plan, null, "jdoe", new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc));
            var kept = written.PendingNotApplied.Where(r => r.Text.Contains("waits")).ToList();
            var dropped = written.PendingNotApplied.Where(r => r.Text.Contains("dropped")).ToList();
            check(P + "two kept triages and one dropped, none with a message of its own, are counted under their own default texts",
                written.PendingNotApplied.Count == 2 && kept.Count == 1 && kept[0].Count == 2 && dropped.Count == 1 && dropped[0].Count == 1);
        }

        // ------------------------------------------------------------------ batch 11b: rows for one pair that the text rule orders

        private static IssueNumbers.Counter Own() { return new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }; }

        private static string RowReason(MarkerPlan.Row a, MarkerPlan.Row b)
        {
            var marker = Ex("m1", Rec(1, "JD-0001", "jdoe"));
            string one = Build(new[] { a, b }, new[] { marker }, "jdoe", Own(), null).NotRetested.Select(x => x.Reason).SingleOrDefault();
            string two = Build(new[] { b, a }, new[] { marker }, "jdoe", Own(), null).NotRetested.Select(x => x.Reason).SingleOrDefault();
            return one == two ? one : "(depends on the order: " + one + " / " + two + ")";
        }

        private static void Reasons(Action<string, bool?> check)
        {
            var withReason = Rw(1); withReason.Verdict = PlanVerdict.NotRetested; withReason.NotRetestedReason = "z";
            var noReason = Rw(1); noReason.Verdict = PlanVerdict.NotRetested; noReason.NotRetestedReason = null;
            check(P + "of two re-test rows for one pair, one with a reason and one without, the plan reports the reason of the one whose text sorts first (the shorter, the one without: its default words)", RowReason(withReason, noReason) == "not re-tested");
        }

        private static MarkerPlan.Creation OneCreation(MarkerPlan.Row a, MarkerPlan.Row b, out bool sameEitherWay)
        {
            var first = Build(new[] { a, b }, null, "jdoe", Own(), null).Creations.Single();
            var second = Build(new[] { b, a }, null, "jdoe", Own(), null).Creations.Single();
            sameEitherWay = MarkerFormats.Write(first.Record).OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual(MarkerFormats.Write(second.Record).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                && string.Join(",", (first.Shown ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + kv.Value))
                == string.Join(",", (second.Shown ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + kv.Value));
            return first;
        }

        private static void Separators(Action<string, bool?> check)
        {
            bool same;
            var setsA = Rw(1); setsA.AlsoSets = new List<string> { "a", "b" };
            var setsB = Rw(1); setsB.AlsoSets = new List<string> { "ab" };
            var sets = OneCreation(setsA, setsB, out same);
            check(P + "two rows for one pair whose other-set lists would read alike if joined (a + b and ab) give one plan, the first by text (one list of one before one of two)", same && sets.Record.AlsoSets.SequenceEqual(Ab));

            var shownA = Rw(1); shownA.Shown = new Dictionary<string, string> { { "a", "1" }, { "b", "2" } };
            var shownB = Rw(1); shownB.Shown = new Dictionary<string, string> { { "a", "1b:2" } };
            var shown = OneCreation(shownA, shownB, out same);
            check(P + "...likewise display maps (a:1 + b:2 and a:1b:2)", same && shown.Shown.Count == 1 && shown.Shown["a"] == "1b:2");

            var when = new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
            var seedA = Rw(1); seedA.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "n", AssignedTo = "x", By = "c", At = when };
            var seedB = Rw(1); seedB.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "n", AssignedTo = "x", By = "c" + when.UtcTicks.ToString(CultureInfo.InvariantCulture), At = null };
            var seed = OneCreation(seedA, seedB, out same);
            check(P + "...and seeds whose author and time would read alike (author 'c' at a time, and author 'c' plus that time's digits at none)", same && seed.Record.ChangedBy == "c" && seed.Record.ChangedAt == when);

            var missingA = Rw(1); missingA.GroupKey = null; missingA.GroupLabel = "}";
            var missingB = Rw(1); missingB.GroupKey = null; missingB.GroupLabel = null;
            var label = OneCreation(missingA, missingB, out same);
            check(P + "a missing label sorts after a real one that starts above '|' (the '~' stand-in), so the row that carries the label wins", same && label.Record.GroupLabel == "}");

            var hashA = Rw(1); hashA.ShownHash = "}";
            var hashB = Rw(1); hashB.ShownHash = null;
            check(P + "...and the same for a display hash", OneCreation(hashA, hashB, out same).ShownHash == "}" && same);
            var keyA = Rw(1); keyA.GroupKey = "}"; keyA.GroupLabel = "L";
            var keyB = Rw(1); keyB.GroupKey = null; keyB.GroupLabel = "L";
            check(P + "...and for a group key (the row with the key wins, its single-member group clears the label)", OneCreation(keyA, keyB, out same).Record.GroupLabel == "" && same);
            var setA = Rw(1); setA.AlsoSets = new List<string> { "}" };
            var setB = Rw(1); setB.AlsoSets = null;
            check(P + "...and for a list of other sets", OneCreation(setA, setB, out same).Record.AlsoSets.SequenceEqual(Brace) && same);
            var mapA = Rw(1); mapA.Shown = new Dictionary<string, string> { { "}", "v" } };
            var mapB = Rw(1); mapB.Shown = null;
            check(P + "...and for a display map", OneCreation(mapA, mapB, out same).Shown.ContainsKey("}") && same);
        }

        // ------------------------------------------------------------------ a row with a link UniqueId, a copy with no pair id, one user under two names

        private static void Linked(Action<string, bool?> check)
        {
            var row = Rw(1); row.LinkUid = "lu";
            var made = Build(new[] { row }, null, "jdoe", Own(), null).Creations.Single().Record;
            check(P + "a new marker from a row that names a link keeps the link UniqueId", made.LinkUid == "lu" && made.HostUid == "h1" && made.OtherUid == "o1");
        }

        private static void Blank(Action<string, bool?> check)
        {
            var blank = new MarkerRecord { Key = null, HostUid = null, LinkUid = null, OtherUid = null, ClashId = null, IssueNo = null, Seq = 1 };
            var plan = Build(null, new[] { new MarkerPlan.Existing { MarkerUid = "m1", Record = blank, WrongWorkset = true } }, "jdoe", Own(), null);
            check(P + "the copy of a marker that names no pair has empty host and other UniqueIds", plan.Changes.Count == 1 && plan.Changes[0].After.HostUid == "" && plan.Changes[0].After.OtherUid == "");
        }

        private static void OneUser(Action<string, bool?> check)
        {
            var counters = new[]
            {
                new IssueNumbers.Counter { Uid = "u1", UserName = null, Prefix = "JD", Next = 1 }, new IssueNumbers.Counter { Uid = "u2", UserName = "", Prefix = "JD", Next = 1 },
                new IssueNumbers.Counter { Uid = "u3", UserName = null, Prefix = "JD", Next = 1 }
            };
            var markers = new[]
            {
                new IssueNumbers.IssueMarker { MarkerUid = "m1", IssueNo = "JD-0005", ReportedBy = "" }, new IssueNumbers.IssueMarker { MarkerUid = "m2", IssueNo = "JD-0005", ReportedBy = "" },
                new IssueNumbers.IssueMarker { MarkerUid = "m3", IssueNo = "JD-0005", ReportedBy = "" }
            };
            var plan = IssueNumbers.RenumberPlan(counters, markers);
            check(P + "counters of a user with no name and of one with an empty name are one user: each marker is renumbered once", plan.Select(r => r.MarkerUid + ">" + r.NewIssueNo).SequenceEqual(Renumbered));
        }

        private static void Prefixes(Action<string, bool?> check)
        {
            var plan = new MarkerPlan.Result();
            foreach (var no in new[] { "KD-0007", "JD-0003", "JD-0001" }) plan.Creations.Add(new MarkerPlan.Creation { Triple = "t" + no, Record = new MarkerRecord { IssueNo = no } });
            var view = RefreshPlanFactory.From(plan, new MarkerPlan.Options(), null);
            check(P + "the first and last new numbers of a plan with two prefixes are the lowest and highest by prefix, then number (a remembered number of another user beside the user's own)", view.FirstIssueNo == "JD-0001" && view.LastIssueNo == "KD-0007");
        }

        private static void AsGiven(Action<string, bool?> check)
        {
            var all = new List<ClashResult>();
            var index = new Dictionary<string, int>();
            ClashDedupe.Add(all, index, new ClashResult { Key = "k", SetName = "x", Severity = ClashSeverity.Hard, AlsoSets = new List<string> { "Z", "Y" } }, "Set A");
            check(P + "a first row is stored as it came: its list of other sets is not sorted or rewritten", all.Count == 1 && all[0].AlsoSets.SequenceEqual(ZY));
        }

        private static void RoundTrip(Action<string, bool?> check)
        {
            var rec = new MarkerRecord { Key = "k", HostUid = "h", OtherUid = "o", Status = MarkerStatus.Pending, X = 0.1 + 0.2, Y = 1.0 / 3.0, Z = 123456.789012345678, ClearanceMm = 25.123456789012345, Seq = 1 };
            var read = MarkerReader.Default.Read(MarkerFormats.Format1, MarkerFormats.Write(rec));
            check(P + "numbers that need 17 digits survive a write and a read exactly (on .NET Framework a default format would round them; the Revit 2024 run is what sees that)",
                read.Status == MarkerReadStatus.Ok && read.Record.X == rec.X && read.Record.Y == rec.Y && read.Record.Z == rec.Z && read.Record.ClearanceMm == rec.ClearanceMm);
        }
    }
}
