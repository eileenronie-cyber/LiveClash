using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 11e: the nine survivors the final Stryker run showed after the order text became injective (batch 11d A). Which of two
    /// rows for one pair the plan takes depends on where their texts differ first; each check below makes two rows differ only there and pins the winner in
    /// either list order. Also the order of the not-marked list, and the ten-digit padding of the ledger's length texts. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationRowChecks
    {
        private const string P = "markers mutation rows: ";
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        private static readonly string[] AD = { "a", "d" };
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            Flags(check);
            Lists(check);
            Missing(check);
            NotMarkedOrder(check);
            Padding(check);
        }

        private static MarkerPlan.Row Rw(string key)
        {
            return new MarkerPlan.Row
            {
                Key = key, HostUid = "h1", LinkUid = "", OtherUid = "o1", HostId = 101, OtherId = 201, Live = true, Verdict = PlanVerdict.Clashing, RuleClearanceMm = 25,
                Severity = "Hard", HasPoint = true, X = 1, SetName = "Set A"
            };
        }

        private static MarkerPlan.Result Build(params MarkerPlan.Row[] rows)
        {
            return MarkerPlan.Build(new MarkerPlan.Input
            {
                Rows = rows.ToList(), Counter = new IssueNumbers.Counter { Uid = "c-jd", UserName = "jdoe", Prefix = "JD", Next = 1 }, Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" }
            });
        }

        /// <summary>The same two rows listed both ways round give the answer asked for.</summary>
        private static void Either(Action<string, bool?> check, string name, MarkerPlan.Row first, MarkerPlan.Row second, Func<MarkerPlan.Result, bool> ok)
        {
            check(P + name + " (listed in the order first, second)", ok(Build(first, second)));
            check(P + name + " (listed in the order second, first)", ok(Build(second, first)));
        }

        // ------------------------------------------------------------------ 11e-1: the insulated flag is followed by the point flag

        private static void Flags(Action<string, bool?> check)
        {
            var insulatedNoPoint = Rw("k"); insulatedNoPoint.RuleInsulated = true; insulatedNoPoint.HasPoint = false;
            var plainWithPoint = Rw("k"); plainWithPoint.RuleInsulated = false; plainWithPoint.HasPoint = true; plainWithPoint.X = 2;
            Either(check, "two rows for one pair that differ in the insulated flag AND the point flag (insulated without a point, against not insulated with one): the plan comes from the one not insulated, with a point",
                insulatedNoPoint, plainWithPoint, plan => plan.Creations.Count == 1 && !plan.Creations[0].Record.Insulated && plan.Creations[0].Record.X == 2 && plan.NotMarked.Count == 0);
        }

        // ------------------------------------------------------------------ 11e-2: lists of the same length that differ at their first element

        private static void Lists(Action<string, bool?> check)
        {
            var ad = Rw("k"); ad.AlsoSets = new List<string> { "a", "d" };
            var bc = Rw("k"); bc.AlsoSets = new List<string> { "b", "c" };
            Either(check, "two rows for one pair that differ only in the other sets ({a,d} against {b,c}): the plan comes from {a,d}", ad, bc,
                plan => plan.Creations.Single().Record.AlsoSets.SequenceEqual(AD));

            var showAd = Rw("k"); showAd.Shown = new Dictionary<string, string> { { "a", "1" }, { "d", "1" } };
            var showBc = Rw("k"); showBc.Shown = new Dictionary<string, string> { { "b", "1" }, { "c", "1" } };
            Either(check, "two rows for one pair that differ only in the shown values (keys a and d against b and c): the plan comes from the keys a and d", showAd, showBc,
                plan => plan.Creations.Single().Shown.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(AD));
        }

        // ------------------------------------------------------------------ 11e-3: a missing shown map, a missing time on the seed

        private static MarkerPlan.Seed NewSeed(DateTimeOffset? at)
        {
            return new MarkerPlan.Seed { Status = MarkerStatus.New, Note = "", AssignedTo = "", By = "kdoe", At = at };
        }

        private static void Missing(Action<string, bool?> check)
        {
            var none = Rw("k"); none.Seed = NewSeed(null); none.Shown = null;
            var thousand = Rw("k"); thousand.Seed = NewSeed(null); thousand.Shown = Enumerable.Range(0, 1000).ToDictionary(i => "k" + i.ToString("D4", CultureInfo.InvariantCulture), i => "1");
            Either(check, "two rows for one pair with the same seed, one with no shown map and one with a thousand values (the count text is four digits long, longer than a status name): the plan comes from the one with the map (a missing map reads after any map)", none, thousand,
                plan => plan.Creations.Single().Shown != null);

            var noTime = Rw("k"); noTime.Seed = NewSeed(null);
            var shortTime = Rw("k"); shortTime.Seed = NewSeed(new DateTimeOffset(5, TimeSpan.Zero));
            Either(check, "two rows for one pair whose seeds differ only in the time (none, against 5 ticks after year 1): the plan comes from the one with no time", noTime, shortTime,
                plan => plan.Creations.Single().Record.ChangedAt == Today);
        }

        // ------------------------------------------------------------------ 11e-4: the not-marked list is ordered by key first

        private static void NotMarkedOrder(Action<string, bool?> check)
        {
            var noPosition = Rw("k1"); noPosition.HasPoint = false;                    // "no position"
            var noId = Rw("k2"); noId.HostUid = ""; noId.OtherUid = "";                // "no UniqueId", which sorts before "no position"
            Either(check, "two refused rows whose keys and reasons sort the other way round (k1 'no position', k2 'no UniqueId') are listed by key: k1 then k2", noPosition, noId,
                plan => plan.NotMarked.Count == 2 && plan.NotMarked[0].Key == "k1" && plan.NotMarked[1].Key == "k2");
        }

        // ------------------------------------------------------------------ 11e-5: the ledger's length text is padded to ten digits

        private static string AssignedOf(IEnumerable<string> lines)
        {
            var e = LedgerExtras.Read(lines).Assigned["k"];
            return e.Assignee + "/" + e.By;
        }

        private static void Padding(Action<string, bool?> check)
        {
            // Without the padding the length text is "12" + "11" + X + "0" for both: ("11" + X, "") and ("2", X + "0") with a ten-character X.
            const string x = "abcdefghij";
            var a1 = LedgerExtras.AssignedLine("k", T0, "", "11" + x);
            var a2 = LedgerExtras.AssignedLine("k", T0, x + "0", "2");
            check(P + "two assignments on one clock whose unpadded length texts would read alike ('11'+X with no author against '2' with X+'0' as author) keep the same one in either file order",
                AssignedOf(new[] { a1, a2 }) == AssignedOf(new[] { a2, a1 }));
        }
    }
}
