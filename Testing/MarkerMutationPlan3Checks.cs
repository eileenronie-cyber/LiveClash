using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 5: which of two rows for one pair the plan uses (spec 6.2, D23: the first by the text of the whole row).
    /// Batch 1 showed the choice does not depend on the input order; this pins WHICH row it is, against an independent copy of the text rule.
    /// Revit-free, so both tiers run it. Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationPlan3Checks
    {
        private const string P = "markers mutation plan 3: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Winner(check);
        }

        /// <summary>
        /// The documented rule, written out again from its description: the fields in order; every field that can hold free text as its length (ten digits) and then
        /// itself, a missing one as the empty text; "~" for a missing label, hash, set list, group key, shown map and seed.
        /// </summary>
        private static string Oracle(MarkerPlan.Row r)
        {
            Func<string, string> L = x => (x ?? "").Length.ToString("D10", Inv) + (x ?? "");
            Func<string, string> Q = x => x == null ? "~" : L(x);
            Func<double, string> d = x => L(x.ToString("R", Inv));
            var s = r.Seed;
            var text = new System.Text.StringBuilder();
            text.Append(L(r.Key)).Append(L(r.Severity)).Append(L(r.Verdict.ToString())).Append(L(r.NotRetestedReason)).Append(r.Live ? "1" : "0").Append(d(r.RuleClearanceMm)).Append(r.RuleInsulated ? "1" : "0");
            text.Append(r.HasPoint ? "1" : "0").Append(d(r.X)).Append(d(r.Y)).Append(d(r.Z)).Append(L(r.SetName)).Append(Q(r.GroupLabel)).Append(Q(r.ShownHash));
            text.Append(L(r.HostUid)).Append(L(r.LinkUid)).Append(L(r.OtherUid)).Append(L(r.HostId.ToString(Inv))).Append(L(r.LinkId.ToString(Inv))).Append(L(r.OtherId.ToString(Inv)));
            if (r.AlsoSets == null) text.Append('~');
            else { text.Append(L(r.AlsoSets.Count.ToString(Inv))); foreach (var x in r.AlsoSets.OrderBy(x => x, StringComparer.Ordinal)) text.Append(L(x)); }
            text.Append(L(r.RememberedIssueNo)).Append(Q(r.GroupKey));
            if (r.Shown == null) text.Append('~');
            else { text.Append(L(r.Shown.Count.ToString(Inv))); foreach (var kv in r.Shown.OrderBy(kv => kv.Key, StringComparer.Ordinal)) text.Append(L(kv.Key)).Append(L(kv.Value)); }
            if (s == null) text.Append('~');
            else text.Append(L(s.Status.ToString())).Append(L(s.Note)).Append(L(s.AssignedTo)).Append(L(s.By)).Append(L(s.At.HasValue ? s.At.Value.UtcTicks.ToString(Inv) : ""));
            return text.ToString();
        }

        private const int NullVariants = 19;

        /// <summary>
        /// One missing value against a real one. The real text has to sort between the stand-in the rule might use for a missing value and the separator that follows
        /// it in the row text: the separator "|" (0x7C) sorts after every letter, so a real "A" cannot tell a missing text from the word "Stryker was here!" ("z" can, being
        /// above "S" and below "|"), and "}" sits between "|" and the "~" stand-in. A missing seed field and an empty one read the same, so they are not a pair.
        /// </summary>
        private static void SetNullField(MarkerPlan.Row r, int which, bool missing)
        {
            string c = "}";
            if (r.Seed == null && which >= 13) r.Seed = new MarkerPlan.Seed { Status = MarkerStatus.Ignored, Note = "a", AssignedTo = "b", By = "c", At = Today };
            switch (which)
            {
                case 0: case 1: r.GroupLabel = missing ? null : c; r.GroupKey = null; break;   // a label shows only where the row has no group key
                case 2: case 3: r.ShownHash = missing ? null : c; break;
                case 4: case 5: r.AlsoSets = missing ? null : new List<string> { c }; break;
                case 6: case 7: r.GroupKey = missing ? null : c; r.GroupLabel = "L"; break;   // a missing key keeps the label the row carries
                case 8: case 9: r.Shown = missing ? null : new Dictionary<string, string> { { c, "v" } }; break;
                case 10: r.RememberedIssueNo = missing ? null : "ZZ-0007"; break;   // a number that parses, or it changes nothing
                case 11: r.NotRetestedReason = missing ? null : "A"; break;
                case 12: r.SetName = missing ? null : "z"; break;
                case 13: r.Severity = missing ? null : "z"; break;
                case 14: r.Key = missing ? null : "z"; break;
                case 15: r.Seed.Note = missing ? null : "A"; break;
                case 16: r.Seed.AssignedTo = missing ? null : "A"; break;
                case 17: r.Seed.By = missing ? null : "A"; break;
                default: r.Seed.At = missing ? null : (DateTimeOffset?)Today.AddHours(-5); break;   // a time other than the plan's own today, or it changes nothing
            }
        }

        private static string Tri(MarkerPlan.Row r) { return ClashIdOf.Triple(r.HostUid, r.LinkUid, r.OtherUid); }

        private static MarkerPlan.Input Without(MarkerPlan.Input s, MarkerPlan.Row drop, MarkerPlan.Row add)
        {
            var c = MarkerMutationPlanChecks.Clone(s);
            c.Rows.Remove(drop);
            if (add != null) c.Rows.Add(add);
            return c;
        }

        private static void Winner(Action<string, bool?> check)
        {
            int cases = 0, wrongWinner = 0, orderDiffers = 0, mattered = 0, ties = 0;
            var covered = new HashSet<string>();
            for (int family = 0; family < 2; family++)
            {
                int total = (MarkerMutationPlanChecks.Variants + NullVariants) * 24;
                for (int seed = 1; seed <= total; seed++)
                {
                    var scene = MarkerMutationPlanChecks.Make(seed + (family == 0 ? 70000 : 80000));
                    var input = scene.Input;
                    if (input.Rows.Count == 0) continue;
                    if (family == 1)
                    {
                        // no marker, no pending: every row is a candidate for a new marker, so the row the plan uses is the row it makes the marker from
                        input.Markers.Clear();
                        input.Pending.Clear();
                        foreach (var r in input.Rows) { r.Live = true; r.Verdict = PlanVerdict.Clashing; r.HasPoint = true; }
                    }
                    int which = seed % (MarkerMutationPlanChecks.Variants + NullVariants);
                    bool isNull = which >= MarkerMutationPlanChecks.Variants;
                    // the three fields that decide whether a row can make a marker at all are left alone in the family without markers
                    if (family == 1 && !isNull && (which == 2 || which == 4 || which == 7)) continue;
                    MarkerPlan.Row baseRow = null;
                    for (int k = 0; k < input.Rows.Count && baseRow == null; k++)
                    {
                        var candidate = input.Rows[(seed + k) % input.Rows.Count];
                        bool hasMarker = input.Markers.Any(m => m.Record != null && ClashIdOf.Triple(m.Record.HostUid, m.Record.LinkUid, m.Record.OtherUid) == Tri(candidate));
                        if (family == 1 || hasMarker) baseRow = candidate;
                    }
                    if (baseRow == null) continue;
                    MarkerPlan.Row other;
                    if (isNull)
                    {
                        SetNullField(baseRow, which - MarkerMutationPlanChecks.Variants, false);
                        other = MarkerMutationPlanChecks.Clone(baseRow);
                        SetNullField(other, which - MarkerMutationPlanChecks.Variants, true);
                    }
                    else other = MarkerMutationPlanChecks.Vary(baseRow, which);
                    string ta = Oracle(baseRow), tb = Oracle(other);
                    if (string.Equals(ta, tb, StringComparison.Ordinal)) { ties++; continue; }
                    var winner = string.CompareOrdinal(ta, tb) < 0 ? baseRow : other;
                    var loser = ReferenceEquals(winner, baseRow) ? other : baseRow;

                    var first = MarkerMutationPlanChecks.Clone(input);
                    first.Rows.Add(other);
                    var second = MarkerMutationPlanChecks.Clone(input);
                    second.Rows.Insert(0, other);
                    var a = MarkerMutationPlanChecks.Canon(MarkerPlan.Build(first));
                    var b = MarkerMutationPlanChecks.Canon(MarkerPlan.Build(second));
                    var expected = MarkerMutationPlanChecks.Canon(MarkerPlan.Build(ReferenceEquals(winner, baseRow) ? input : Without(input, baseRow, other)));
                    var wrong = MarkerMutationPlanChecks.Canon(MarkerPlan.Build(ReferenceEquals(loser, baseRow) ? input : Without(input, baseRow, other)));
                    cases++;
                    covered.Add((isNull ? "n" : "v") + which);
                    if (a != b) orderDiffers++;
                    if (a != expected || b != expected) wrongWinner++;
                    if (expected != wrong) mattered++;
                }
            }
            check(P + "fuzz: of two rows for one pair that differ in one field (or in a missing value against an empty one) the plan uses the one whose whole-row text sorts first, whichever comes first in the list",
                wrongWinner == 0 && orderDiffers == 0 && cases > 600);
            check(P + "...and for most of those pairs the choice shows in the plan (so the check can tell the two rows apart)", mattered > 250 && covered.Count > 35 && ties < cases / 4);
        }
    }
}
