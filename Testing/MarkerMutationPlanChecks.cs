using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 1: properties of the Refresh plan that the first Stryker run showed were not pinned down: the order of
    /// every list in the result, the independence of the answer from the order the inputs came in, and the tie-break between two rows for one
    /// pair (spec 6.2, D23). Revit-free, so both tiers run it. Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationPlanChecks
    {
        private const string P = "markers mutation plan: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Properties(check);
            Ties(check);
            Collisions(check);
        }

        // ------------------------------------------------------------------ a random plan input

        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

        internal sealed class Scene
        {
            public MarkerPlan.Input Input;
            public List<int> RowPairs = new List<int>();
        }

        private static MarkerPlan.Row RowFor(Random r, int i)
        {
            string[] severities = { "Hard", "Clearance", "Unverified", "" };
            string[] sets = { "Set A", "Set B", "" };
            string[] groups = { "", "g1", "g2", null };
            int v = r.Next(10);
            var row = new MarkerPlan.Row
            {
                Key = "k" + i, HostUid = "h" + i, LinkUid = "", OtherUid = "o" + i, HostId = 100 + i, OtherId = 200 + i, Live = r.Next(100) < 85,
                Verdict = v < 6 ? PlanVerdict.Clashing : v < 8 ? PlanVerdict.NoLongerClashes : PlanVerdict.NotRetested, NotRetestedReason = r.Next(2) == 0 ? "r" : "",
                RuleClearanceMm = r.Next(100) < 90 ? 25 : 30, RuleInsulated = r.Next(100) < 10, Severity = severities[r.Next(4)], HasPoint = r.Next(100) < 90,
                X = i + (r.Next(3) == 0 ? 1 : 0), Y = r.Next(2), Z = 0, SetName = sets[r.Next(3)], GroupKey = groups[r.Next(4)],
                ShownHash = r.Next(100) < 70 ? "H" : "H2", Shown = new Dictionary<string, string> { { "LC Level", "L" + r.Next(2) } }
            };
            if (r.Next(100) < 40) row.AlsoSets = new List<string> { "Set B", "Set C" }.Take(r.Next(3)).ToList();
            if (r.Next(100) < 30) row.Seed = new MarkerPlan.Seed { Status = (MarkerStatus)r.Next(6), Note = r.Next(2) == 0 ? "n" : "", AssignedTo = r.Next(3) == 0 ? "asmith" : "", By = "kdoe", At = r.Next(2) == 0 ? (DateTimeOffset?)Today : null };
            if (r.Next(100) < 20) row.RememberedIssueNo = r.Next(2) == 0 ? "KD-0099" : "JD-0001";
            return row;
        }

        private static MarkerRecord RecordFor(Random r, int i, int j)
        {
            var access = r.Next(100) < 70 ? "jdoe" : "kdoe";
            var rec = new MarkerRecord
            {
                Key = r.Next(100) < 85 ? "k" + i : "old" + i, HostUid = "h" + i, LinkUid = "", OtherUid = "o" + i, HostId = 100 + i, OtherId = 200 + i, ClashId = ClashIdOf.Of("h" + i, "", "o" + i),
                IssueNo = (r.Next(100) < 80 ? "JD-" : "KD-") + (i * 10 + j).ToString("D4", Inv), Status = (MarkerStatus)r.Next(6), Note = r.Next(3) == 0 ? "x" : "", AssignedTo = r.Next(4) == 0 ? "asmith" : "",
                ReportedBy = access, ReportedAt = Today.AddDays(-r.Next(60)), ChangedBy = r.Next(2) == 0 ? "kdoe" : "", Severity = r.Next(2) == 0 ? "Hard" : "Clearance", ClearanceMm = 25,
                SetName = r.Next(2) == 0 ? "Set A" : "Set B", X = i, GroupLabel = r.Next(4) == 0 ? "JD-0001 (+1)" : "", Seq = 1 + r.Next(3), LastCheckedAt = Today, LastCheckedBy = "jdoe"
            };
            if (rec.Status == MarkerStatus.Resolved) { rec.ResolvedAt = Today.AddDays(-r.Next(60)); rec.ResolvedBy = "jdoe"; }
            return rec;
        }

        internal static Scene Make(int seed)
        {
            var r = new Random(seed);
            var scene = new Scene { Input = new MarkerPlan.Input() };
            scene.Input.Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe", RemoveRetention = r.Next(2) == 0, Cap = r.Next(100) < 15 ? 2 : 2000, DisabledSets = r.Next(4) == 0 ? new List<string> { "Set B" } : new List<string>() };
            scene.Input.Counter = new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 + r.Next(5) };
            if (r.Next(100) < 25) scene.Input.AllCounters = new List<IssueNumbers.Counter> { scene.Input.Counter, new IssueNumbers.Counter { Uid = "c-kd", UserName = "kdoe", Prefix = r.Next(2) == 0 ? "JD" : "KD", Next = 3 } };
            int pairs = 2 + r.Next(8);
            for (int i = 1; i <= pairs; i++)
            {
                if (r.Next(100) < 80) { scene.Input.Rows.Add(RowFor(r, i)); scene.RowPairs.Add(i); }
                int markers = r.Next(100) < 60 ? (r.Next(100) < 15 ? 2 : 1) : 0;
                for (int j = 0; j < markers; j++)
                {
                    int a = r.Next(100);
                    var existing = new MarkerPlan.Existing
                    {
                        MarkerUid = "m" + i + "_" + j, Record = RecordFor(r, i, j), Access = a < 72 ? MarkerAccess.Free : a < 82 ? MarkerAccess.HeldByOther : a < 88 ? MarkerAccess.ChangedInCentral : a < 93 ? MarkerAccess.InsideGroup : MarkerAccess.NewerFormat,
                        HeldBy = "kdoe", ShownHash = r.Next(100) < 75 ? "H" : "stale", OffPlace = r.Next(100) < 10, WrongWorkset = r.Next(100) < 10
                    };
                    if (existing.Access == MarkerAccess.NewerFormat && r.Next(2) == 0) existing.Record = null;
                    scene.Input.Markers.Add(existing);
                    if (r.Next(100) < 20 && existing.Record != null)
                        scene.Input.Pending.Add(new MarkerPlan.Pending
                        {
                            Triple = Tri(i), SeqAtPending = existing.Record.Seq + (r.Next(4) == 0 ? 1 : 0), Status = r.Next(2) == 0 ? (MarkerStatus?)MarkerStatus.Ignored : null, Note = r.Next(2) == 0 ? "p" : null,
                            AssignedTo = r.Next(3) == 0 ? "bsmith" : null, By = "kdoe"
                        });
                }
            }
            return scene;
        }

        // ------------------------------------------------------------------ a canonical text of a result

        private static string Rec(MarkerRecord m)
        {
            if (m == null) return "~";
            return string.Join(";", MarkerFormats.Write(m).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        }

        private static string Map(Dictionary<string, string> m)
        {
            return m == null ? "~" : string.Join(",", m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + kv.Value));
        }

        internal static string Canon(MarkerPlan.Result r)
        {
            var sb = new StringBuilder();
            foreach (var c in r.Creations) sb.Append("C|").Append(c.Triple).Append('|').Append(Rec(c.Record)).Append('|').Append(c.ShownHash).Append('|').Append(Map(c.Shown)).Append('|').Append(c.StatusOrder).Append('|').Append(c.IssueNoReplaced).Append('\n');
            foreach (var c in r.Changes)
                sb.Append("U|").Append(c.MarkerUid).Append('|').Append(Rec(c.Before)).Append('|').Append(Rec(c.After)).Append('|').Append(string.Join(",", c.Fields)).Append('|').Append(c.Resolved).Append(c.Reopened).Append(c.Moved).Append(c.PutBack).Append(c.WorksetFix).Append(c.Rekeyed)
                  .Append('|').Append(c.ShownHash).Append('|').Append(Map(c.Shown)).Append('|').Append(c.StatusOrder).Append('\n');
            foreach (var x in r.Removals) sb.Append("R|").Append(x.MarkerUid).Append('|').Append(x.Reason).Append('|').Append(x.KeptUid).Append('\n');
            foreach (var x in r.RetentionOffers) sb.Append("O|").Append(x.MarkerUid).Append('|').Append(x.Reason).Append('\n');
            foreach (var x in r.PassedOver) sb.Append("P|").Append(x.MarkerUid).Append('|').Append(x.Key).Append('|').Append(x.Reason).Append('\n');
            foreach (var x in r.NotRetested) sb.Append("N|").Append(x.MarkerUid).Append('|').Append(x.Key).Append('|').Append(x.Reason).Append('\n');
            foreach (var x in r.NotMarked) sb.Append("M|").Append(x.Key).Append('|').Append(x.Reason).Append('\n');
            foreach (var x in r.Pending) sb.Append("G|").Append(x.Triple).Append('|').Append(x.MarkerUid).Append('|').Append(x.Kind).Append('|').Append(x.Message).Append('|').Append(string.Join(",", x.NotApplied)).Append('\n');
            foreach (var x in r.LostDecisions) sb.Append("L|").Append(x.RemovedUid).Append('|').Append(x.RemovedIssueNo).Append('|').Append(x.KeptUid).Append('|').Append(x.KeptIssueNo).Append('|').Append(x.Status).Append('|').Append(x.Note).Append('|').Append(x.AssignedTo).Append('|').Append(x.By).Append('\n');
            foreach (var x in r.SetCounts) sb.Append("S|").Append(x.Name).Append('|').Append(x.InPanel).Append('|').Append(x.UpToDate).Append('|').Append(x.NotLive).Append('|').Append(x.SetOff).Append('|').Append(x.NotRetested).Append('\n');
            sb.Append("T|").Append(r.Unchanged).Append('|').Append(r.Merges).Append('|').Append(r.ExistingMarkers).Append('|').Append(r.NeedsDialog).Append('|').Append(r.OverCap).Append('|').Append(r.OverWarn).Append('\n');
            sb.Append("K|").Append(r.CounterAfter == null ? "~" : r.CounterAfter.Prefix + r.CounterAfter.Next).Append('|').Append(r.PrefixMove == null ? "~" : r.PrefixMove.OldPrefix + ">" + r.PrefixMove.NewPrefix).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ order of the lists, and independence from the input order

        private static bool Ascending<T>(IList<T> list, Func<T, string> key)
        {
            for (int i = 1; i < list.Count; i++) if (string.CompareOrdinal(key(list[i - 1]), key(list[i])) > 0) return false;
            return true;
        }

        private static void Properties(Action<string, bool?> check)
        {
            int unsorted = 0, differs = 0, withMany = 0, listsSeen = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var scene = Make(seed);
                var result = MarkerPlan.Build(scene.Input);

                bool sorted = Ascending(result.Changes, x => x.MarkerUid) && Ascending(result.Removals, x => x.MarkerUid) && Ascending(result.RetentionOffers, x => x.MarkerUid)
                    && Ascending(result.PassedOver, x => x.MarkerUid) && Ascending(result.NotRetested, x => x.MarkerUid) && Ascending(result.Pending, x => x.Triple)
                    && Ascending(result.LostDecisions, x => x.RemovedUid) && Ascending(result.NotMarked, x => (x.Key ?? "") + "\u0001" + x.Reason);
                if (!sorted) unsorted++;
                if (result.Changes.Count > 1 || result.PassedOver.Count > 1 || result.Removals.Count > 1 || result.Pending.Count > 1 || result.NotMarked.Count > 1 || result.RetentionOffers.Count > 1 || result.NotRetested.Count > 1) withMany++;
                listsSeen += (result.Changes.Count > 1 ? 1 : 0) + (result.PassedOver.Count > 1 ? 1 : 0) + (result.Removals.Count > 1 ? 1 : 0) + (result.Pending.Count > 1 ? 1 : 0) + (result.NotMarked.Count > 1 ? 1 : 0)
                    + (result.RetentionOffers.Count > 1 ? 1 : 0) + (result.NotRetested.Count > 1 ? 1 : 0) + (result.LostDecisions.Count > 1 ? 1 : 0);

                var shuffler = new Random(seed + 9000);
                var again = new MarkerPlan.Input
                {
                    Rows = scene.Input.Rows.OrderBy(x => shuffler.Next()).ToList(), Markers = scene.Input.Markers.OrderBy(x => shuffler.Next()).ToList(), Pending = scene.Input.Pending.OrderBy(x => shuffler.Next()).ToList(),
                    Counter = scene.Input.Counter, AllCounters = scene.Input.AllCounters.OrderBy(x => shuffler.Next()).ToList(), Options = scene.Input.Options
                };
                if (Canon(MarkerPlan.Build(again)) != Canon(result)) differs++;
            }
            check(P + "fuzz (300 plans): every list in the result is in its documented order (changes, removals, offers, passed over, not re-tested, pending and lost decisions by id, not marked by key then reason)",
                unsorted == 0 && withMany > 100 && listsSeen > 300);
            check(P + "...and the same inputs in any other order give exactly the same plan", differs == 0 && withMany > 100);
        }

        // ------------------------------------------------------------------ two rows for one pair

        internal static MarkerPlan.Row Clone(MarkerPlan.Row s)
        {
            return new MarkerPlan.Row
            {
                Key = s.Key, HostUid = s.HostUid, LinkUid = s.LinkUid, OtherUid = s.OtherUid, HostId = s.HostId, LinkId = s.LinkId, OtherId = s.OtherId, Live = s.Live, Verdict = s.Verdict,
                NotRetestedReason = s.NotRetestedReason, RuleClearanceMm = s.RuleClearanceMm, RuleInsulated = s.RuleInsulated, Severity = s.Severity, HasPoint = s.HasPoint, X = s.X, Y = s.Y, Z = s.Z,
                SetName = s.SetName, AlsoSets = s.AlsoSets == null ? null : new List<string>(s.AlsoSets), GroupLabel = s.GroupLabel, GroupKey = s.GroupKey, ShownHash = s.ShownHash,
                Shown = s.Shown == null ? null : new Dictionary<string, string>(s.Shown),
                Seed = s.Seed == null ? null : new MarkerPlan.Seed { Status = s.Seed.Status, Note = s.Seed.Note, AssignedTo = s.Seed.AssignedTo, By = s.Seed.By, At = s.Seed.At },
                RememberedIssueNo = s.RememberedIssueNo
            };
        }

        internal const int Variants = 29;

        /// <summary>The same row with exactly one field different. Which field is <paramref name="which"/>; each one is a field the tie-break between two rows has to look at.</summary>
        internal static MarkerPlan.Row Vary(MarkerPlan.Row b, int which)
        {
            var v = Clone(b);
            switch (which)
            {
                case 0: v.Key = b.Key + "x"; break;
                case 1: v.Severity = b.Severity == "Hard" ? "Clearance" : "Hard"; break;
                case 2: v.Verdict = b.Verdict == PlanVerdict.Clashing ? PlanVerdict.NoLongerClashes : PlanVerdict.Clashing; break;
                case 3: v.NotRetestedReason = (b.NotRetestedReason ?? "") + "z"; break;
                case 4: v.Live = !b.Live; break;
                case 5: v.RuleClearanceMm = b.RuleClearanceMm + 5; break;
                case 6: v.RuleInsulated = !b.RuleInsulated; break;
                case 7: v.HasPoint = !b.HasPoint; break;
                case 8: v.X = b.X + 1; break;
                case 9: v.Y = b.Y + 1; break;
                case 10: v.Z = b.Z + 1; break;
                case 11: v.SetName = (b.SetName ?? "") + "q"; break;
                case 12: v.GroupLabel = (b.GroupLabel ?? "") + "L"; break;
                case 13: v.ShownHash = (b.ShownHash ?? "") + "s"; break;
                case 14: v.HostUid = b.OtherUid; v.OtherUid = b.HostUid; break;
                case 15: v.LinkId = b.LinkId + 7; break;
                case 16: v.HostId = b.HostId + 3; break;
                case 17: v.OtherId = b.OtherId + 3; break;
                case 18: v.AlsoSets = new List<string> { "Set Z" }; break;
                case 19: v.RememberedIssueNo = (b.RememberedIssueNo ?? "") + "9"; break;
                case 20: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "b", By = "c", At = Today }; break;
                case 21: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "b", By = "c", At = Today.AddDays(1) }; break;
                case 22: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Coordinated, Note = "a", AssignedTo = "b", By = "c", At = Today }; break;
                case 23: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "d", AssignedTo = "b", By = "c", At = Today }; break;
                case 24: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "e", By = "c", At = Today }; break;
                case 25: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "b", By = "f", At = Today }; break;
                case 26: v.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = null, AssignedTo = "b", By = "c", At = null }; break;
                case 27: v.GroupKey = (b.GroupKey ?? "") + "g"; break;
                default: v.Shown = new Dictionary<string, string> { { "LC Level", "other" } }; break;
            }
            return v;
        }

        private static void Ties(Action<string, bool?> check)
        {
            int differs = 0, cases = 0, changing = 0;
            var covered = new HashSet<int>();
            for (int seed = 1; seed <= Variants * 12; seed++)
            {
                var scene = Make(seed + 20000);
                if (scene.Input.Rows.Count == 0) continue;
                int which = seed % Variants;
                var baseRow = scene.Input.Rows[seed % scene.Input.Rows.Count];
                var other = Vary(baseRow, which);
                var first = Clone(scene.Input);
                var second = Clone(scene.Input);
                first.Rows.Add(other);
                second.Rows.Insert(0, other);
                var a = Canon(MarkerPlan.Build(first));
                var b = Canon(MarkerPlan.Build(second));
                cases++;
                covered.Add(which);
                if (a != b) differs++;
                var alone = Canon(MarkerPlan.Build(scene.Input));
                var onlyOther = Clone(scene.Input);
                onlyOther.Rows.Remove(baseRow); onlyOther.Rows.Add(other);
                var aloneOther = Canon(MarkerPlan.Build(onlyOther));
                if (alone != aloneOther) changing++;   // the choice of the row matters for this pair, whichever row the rule picks
            }
            check(P + "fuzz: two rows for one pair that differ in exactly one field (every field the tie-break has to look at) give the same plan whichever comes first", differs == 0 && cases > 300 && covered.Count == Variants && changing > 50);
        }

        private static MarkerPlan.Row With(MarkerPlan.Row b, Action<MarkerPlan.Row> change)
        {
            var v = Clone(b);
            change(v);
            return v;
        }

        /// <summary>Rows whose neighbouring fields would run together if the text used to order them had no separator between the fields.</summary>
        private static void Collisions(Action<string, bool?> check)
        {
            var pairs = new List<Tuple<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>>
            {
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => { x.Key = "a"; x.Severity = "bc"; }, x => { x.Key = "ab"; x.Severity = "c"; }),
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => { x.SetName = "s"; x.GroupLabel = "tg"; }, x => { x.SetName = "st"; x.GroupLabel = "g"; }),
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => { x.GroupLabel = "g"; x.ShownHash = "hh"; }, x => { x.GroupLabel = "gh"; x.ShownHash = "h"; }),
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => { x.RememberedIssueNo = "KD-1"; x.GroupKey = "x"; }, x => { x.RememberedIssueNo = "KD-1x"; x.GroupKey = ""; }),
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => x.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "bc", By = "d" }, x => x.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "ab", AssignedTo = "c", By = "d" }),
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => x.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "b", By = "cd" }, x => x.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "bc", By = "d" }),
                Tuple.Create<Action<MarkerPlan.Row>, Action<MarkerPlan.Row>>(x => { x.Shown = new Dictionary<string, string> { { "a", "bc" } }; }, x => { x.Shown = new Dictionary<string, string> { { "ab", "c" } }; })
            };
            int differs = 0, cases = 0, changing = 0;
            for (int seed = 1; seed <= pairs.Count * 14; seed++)
            {
                var scene = Make(seed + 50000);
                if (scene.Input.Rows.Count == 0) continue;
                var pair = pairs[seed % pairs.Count];
                var baseRow = scene.Input.Rows[seed % scene.Input.Rows.Count];
                var a = With(baseRow, pair.Item1);
                var b = With(baseRow, pair.Item2);
                var one = Clone(scene.Input);
                var two = Clone(scene.Input);
                one.Rows.Remove(baseRow); two.Rows.Remove(baseRow);
                one.Rows.Add(a); one.Rows.Add(b);
                two.Rows.Add(b); two.Rows.Add(a);
                cases++;
                var first = Canon(MarkerPlan.Build(one));
                if (first != Canon(MarkerPlan.Build(two))) differs++;
                var onlyA = Clone(scene.Input);
                onlyA.Rows.Remove(baseRow); onlyA.Rows.Add(a);
                var onlyB = Clone(scene.Input);
                onlyB.Rows.Remove(baseRow); onlyB.Rows.Add(b);
                var aloneB = Canon(MarkerPlan.Build(onlyB));
                if (Canon(MarkerPlan.Build(onlyA)) != aloneB) changing++;   // the choice matters for this pair, whichever row the rule picks
            }
            check(P + "fuzz: rows for one pair whose neighbouring fields would run together without a separator (key and severity, set and label, label and hash, issue number and group key, seed note, assignee and author) still give one plan in either order",
                differs == 0 && cases > 80 && changing > 20);
        }

        internal static MarkerPlan.Input Clone(MarkerPlan.Input s)
        {
            return new MarkerPlan.Input
            {
                Rows = s.Rows.ToList(), Markers = s.Markers.ToList(), Pending = s.Pending.ToList(), Counter = s.Counter, AllCounters = s.AllCounters.ToList(), Options = s.Options
            };
        }
    }
}
