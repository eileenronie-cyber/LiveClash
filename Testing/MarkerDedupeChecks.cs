using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 3b and 3c: the cross-set merge records "also caught by" (spec 4b.2, RF17) and the
    /// marker identity predicate (spec 5.1, RF12). Revit-free, so both tiers run it. Red-first: BUILD-LOG.md.
    /// </summary>
    public static class MarkerDedupeChecks
    {
        private const string D = "markers dedupe: ";
        private const string I = "markers identity: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Stamping(check);
            AlsoCaught(check);
            Combinations(check);
            Identity(check);
        }

        private static ClashResult Row(string key, ClashSeverity sev, params string[] also)
        {
            var r = new ClashResult { Key = key, Severity = sev };
            if (also.Length > 0) r.AlsoSets = also.ToList();
            return r;
        }

        private static List<ClashResult> Merge(params Tuple<string, ClashSeverity, string>[] feeds)
        {
            var all = new List<ClashResult>();
            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in feeds) ClashDedupe.Add(all, idx, Row(f.Item1, f.Item2), f.Item3);
            return all;
        }

        private static Tuple<string, ClashSeverity, string> F(string key, ClashSeverity sev, string set)
        {
            return Tuple.Create(key, sev, set);
        }

        private static string[] Names(params string[] items)
        {
            return items;
        }

        private static List<string> AlsoOf(ClashResult r)
        {
            return r.AlsoSets ?? new List<string>();
        }

        // ------------------------------------------------------------------ the stamping rule, as before

        private static void Stamping(Action<string, bool?> check)
        {
            var one = Merge(F("1|0|2", ClashSeverity.Clearance, "A"));
            check(D + "a new pair is added and stamped with its set", one.Count == 1 && one[0].SetName == "A");

            var equal = Merge(F("3|0|4", ClashSeverity.Hard, "First"), F("3|0|4", ClashSeverity.Hard, "Second"));
            check(D + "equal severity keeps the first-seen row, so the lowest-indexed set stamps it", equal.Count == 1 && equal[0].SetName == "First");

            var upgrade = Merge(F("1|0|2", ClashSeverity.Clearance, "A"), F("1|0|2", ClashSeverity.Hard, "B"));
            check(D + "a later Hard replaces an earlier Clearance and is stamped with the set that found it", upgrade.Count == 1 && upgrade[0].Severity == ClashSeverity.Hard && upgrade[0].SetName == "B");

            var keep = Merge(F("1|0|2", ClashSeverity.Hard, "B"), F("1|0|2", ClashSeverity.Clearance, "C"));
            check(D + "a later Clearance does not bury an earlier Hard", keep.Count == 1 && keep[0].Severity == ClashSeverity.Hard && keep[0].SetName == "B");

            var nokey = Merge(F(null, ClashSeverity.Hard, "A"), F("", ClashSeverity.Hard, "B"), F(null, ClashSeverity.Clearance, "C"));
            check(D + "rows with no key are never merged, each stamped with its own set", nokey.Count == 3 && nokey[0].SetName == "A" && nokey[1].SetName == "B" && nokey[2].SetName == "C");

            var idxCheck = new Dictionary<string, int>(StringComparer.Ordinal);
            var all = new List<ClashResult>();
            ClashDedupe.Add(all, idxCheck, Row("a", ClashSeverity.Clearance), "A");
            ClashDedupe.Add(all, idxCheck, Row("b", ClashSeverity.Clearance), "A");
            ClashDedupe.Add(all, idxCheck, Row("a", ClashSeverity.Hard), "B");
            check(D + "a replaced row keeps its place in the list and in the index", all.Count == 2 && all[0].Key == "a" && all[0].SetName == "B" && idxCheck["a"] == 0 && idxCheck["b"] == 1);
        }

        // ------------------------------------------------------------------ also caught by

        private static void AlsoCaught(Action<string, bool?> check)
        {
            var second = Merge(F("k", ClashSeverity.Clearance, "A"), F("k", ClashSeverity.Clearance, "B"));
            check(D + "a later set that catches the same pair is recorded as also caught by, not dropped (RF17)", second.Count == 1 && second[0].SetName == "A" && AlsoOf(second[0]).SequenceEqual(Names("B")));

            var upgrade = Merge(F("k", ClashSeverity.Clearance, "A"), F("k", ClashSeverity.Hard, "B"));
            check(D + "a Hard row that replaces a soft one records the set it replaced", upgrade[0].SetName == "B" && AlsoOf(upgrade[0]).SequenceEqual(Names("A")));

            var all = new List<ClashResult>();
            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            ClashDedupe.Add(all, idx, Row("k", ClashSeverity.Clearance), "A");
            ClashDedupe.Add(all, idx, Row("k", ClashSeverity.Clearance), "C");
            ClashDedupe.Add(all, idx, Row("k", ClashSeverity.Hard), "B");
            check(D + "the replaced row's own also-sets travel with it: A stamped, C also; then Hard B replaces A and lists A and C", all[0].SetName == "B" && AlsoOf(all[0]).SequenceEqual(Names("A", "C")));

            var same = Merge(F("k", ClashSeverity.Clearance, "A"), F("k", ClashSeverity.Clearance, "A"), F("k", ClashSeverity.Hard, "A"));
            check(D + "the same set twice records nothing, even when its second row is Hard", same.Count == 1 && AlsoOf(same[0]).Count == 0 && same[0].SetName == "A" && same[0].Severity == ClashSeverity.Hard);

            var empty = Merge(F("k", ClashSeverity.Clearance, "A"), F("k", ClashSeverity.Clearance, ""), F("k", ClashSeverity.Clearance, null));
            check(D + "an empty or missing set name is never recorded", AlsoOf(empty[0]).Count == 0);

            var order = Merge(F("k", ClashSeverity.Clearance, "A"), F("k", ClashSeverity.Clearance, "D"), F("k", ClashSeverity.Clearance, "C"), F("k", ClashSeverity.Clearance, "B"), F("k", ClashSeverity.Clearance, "C"));
            check(D + "also-caught-by is sorted, distinct and never the stamping set (D33)", AlsoOf(order[0]).SequenceEqual(Names("B", "C", "D")));

            var src = new ClashResult { Key = "k", SetName = "A", AlsoSets = new List<string> { "B", "C" } };
            var clone = src.Clone();
            clone.AlsoSets.Add("D");
            check(D + "Clone copies also-caught-by, not the same list", clone.AlsoSets.SequenceEqual(Names("B", "C", "D")) && src.AlsoSets.SequenceEqual(Names("B", "C")));
        }

        private static void Combinations(Action<string, bool?> check)
        {
            // Three sets A, B, C in index order, each absent, Clearance or Hard for one pair: 27 cases (the empty one has no pair).
            string[] sets = { "A", "B", "C" };
            int bad = 0, cases = 0;
            for (int code = 1; code < 27; code++)
            {
                var sev = new ClashSeverity?[3];
                int c = code;
                for (int s = 0; s < 3; s++) { int d = c % 3; c /= 3; sev[s] = d == 0 ? (ClashSeverity?)null : d == 1 ? ClashSeverity.Clearance : ClashSeverity.Hard; }
                if (sev.All(x => !x.HasValue)) continue;
                cases++;

                var all = new List<ClashResult>();
                var idx = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int s = 0; s < 3; s++) if (sev[s].HasValue) ClashDedupe.Add(all, idx, Row("k", sev[s].Value), sets[s]);

                // The reference, written differently: the stamping set is the first Hard one if any, else the first one; the others are listed.
                var catching = Enumerable.Range(0, 3).Where(s => sev[s].HasValue).ToList();
                var hard = catching.Where(s => sev[s] == ClashSeverity.Hard).ToList();
                int stamp = hard.Count > 0 ? hard[0] : catching[0];
                var expectedAlso = catching.Where(s => s != stamp).Select(s => sets[s]).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (all.Count != 1 || all[0].SetName != sets[stamp] || !AlsoOf(all[0]).SequenceEqual(expectedAlso) || (all[0].Severity == ClashSeverity.Hard) != (hard.Count > 0)) bad++;
            }
            check(D + "all " + cases + " combinations of three sets catching a pair (absent, Clearance or Hard each) stamp and list as an independent reference says", cases == 26 && bad == 0);
        }

        // ------------------------------------------------------------------ identity

        private static void Identity(Action<string, bool?> check)
        {
            check(I + "LiveClash, marker/1 and a record is a marker", MarkerIdentity.IsMarker(MarkerFormats.ApplicationId, MarkerFormats.ApplicationDataId, true));
            check(I + "no record, a wrong application id or a wrong data id is not a marker (a copy of the shape by another tool, or an older format)",
                !MarkerIdentity.IsMarker(MarkerFormats.ApplicationId, MarkerFormats.ApplicationDataId, false) && !MarkerIdentity.IsMarker("OtherApp", MarkerFormats.ApplicationDataId, true)
                && !MarkerIdentity.IsMarker(MarkerFormats.ApplicationId, "marker/2", true));
            check(I + "the match is exact: case, spaces and prefixes do not count",
                !MarkerIdentity.IsMarker("liveclash", MarkerFormats.ApplicationDataId, true) && !MarkerIdentity.IsMarker(" LiveClash", MarkerFormats.ApplicationDataId, true)
                && !MarkerIdentity.IsMarker(MarkerFormats.ApplicationId, "Marker/1", true) && !MarkerIdentity.IsMarker(MarkerFormats.ApplicationId, "marker/1 ", true));
            check(I + "null and empty ids are not a marker and never throw", !MarkerIdentity.IsMarker(null, null, true) && !MarkerIdentity.IsMarker("", "", true) && !MarkerIdentity.IsMarker(MarkerFormats.ApplicationId, null, true));
            check(I + "a hand-typed clash id on someone's Specialty Equipment is not a marker: identity is the two ids and the record, nothing else (RF12)",
                !MarkerIdentity.IsMarker(null, null, false) && !MarkerIdentity.IsMarker("", MarkerFormats.ApplicationDataId, false));
            check(I + "the engine's test is wider: anything LiveClash made (its ApplicationId) is ours, with or without a record; another application's, empty and null are not",
                MarkerIdentity.IsOurs(MarkerFormats.ApplicationId) && !MarkerIdentity.IsOurs("liveclash") && !MarkerIdentity.IsOurs("SomeOtherApp") && !MarkerIdentity.IsOurs("") && !MarkerIdentity.IsOurs(null));
            check(I + "the constants it matches are the spec's", MarkerFormats.ApplicationId == "LiveClash" && MarkerFormats.ApplicationDataId == "marker/1");
        }
    }
}
