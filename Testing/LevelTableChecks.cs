using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule at scale: <see cref="LevelTable"/> (the levels read once, a binary search per clash) gives the answer the old per-clash
    /// linear lookup gave, for every point, and its edges are the old edges. Revit-free: shared with the headless runner.
    /// </summary>
    internal static class LevelTableChecks
    {
        private const string P = "level table: ";

        /// <summary>The OLD definition, as it was written in RowAdapter.LevelAt: the highest level at or below z + 1e-6, the first listed on a tie.</summary>
        private static string Old(IList<KeyValuePair<double, string>> levels, double z)
        {
            var hits = levels.Where(l => l.Key <= z + 1e-6).OrderByDescending(l => l.Key).ToList();
            return hits.Count == 0 ? "" : (hits[0].Value ?? "");
        }

        private static KeyValuePair<double, string> L(double e, string n) { return new KeyValuePair<double, string>(e, n); }

        internal static void RunAll(Action<string, bool?> check)
        {
            var rnd = new Random(20261102);
            int mismatches = 0, probes = 0;
            string firstMismatch = "";
            for (int set = 0; set < 300; set++)
            {
                var levels = new List<KeyValuePair<double, string>>();
                int n = rnd.Next(0, 13);
                for (int i = 0; i < n; i++)
                {
                    // whole and half feet, with duplicates and near-equal values (within the tolerance) on purpose
                    double e = rnd.Next(-6, 40) * 0.5 + (rnd.Next(0, 4) == 0 ? 1e-7 : 0.0);
                    levels.Add(L(e, rnd.Next(0, 9) == 0 ? null : "Level " + rnd.Next(0, 99)));
                }
                var table = new LevelTable(levels);
                for (int k = 0; k < 40; k++)
                {
                    double z = rnd.Next(-10, 90) * 0.25 + (rnd.Next(0, 3) == 0 ? -1e-6 : rnd.Next(0, 3) == 0 ? 5e-7 : 0.0);
                    probes++;
                    string want = Old(levels, z), got = table.At(z);
                    if (want != got) { mismatches++; if (firstMismatch.Length == 0) firstMismatch = "z=" + z + " want '" + want + "' got '" + got + "'"; }
                }
            }
            check(P + "for 300 random level sets and " + probes + " points it gives the old linear scan's answer" + (mismatches == 0 ? "" : " (" + mismatches + " differ, first " + firstMismatch + ")"), mismatches == 0);

            var three = new LevelTable(new[] { L(0.0, "Ground"), L(10.0, "First"), L(20.0, "Second") });
            check(P + "below every level it says nothing", three.At(-0.5) == "" && new LevelTable(new List<KeyValuePair<double, string>>()).At(5.0) == "");
            check(P + "exactly at a level it is that level", three.At(10.0) == "First" && three.At(0.0) == "Ground" && three.At(20.0) == "Second");
            check(P + "within the tolerance under a level it is that level, further under it is the one below",
                three.At(10.0 - 1e-7) == "First" && three.At(10.0 - 2e-6) == "Ground");
            check(P + "exactly the tolerance under a level (z + 1e-6 is the elevation) it is still that level, as the old scan said",
                new LevelTable(new[] { L(17.0, "L17"), L(10.0, "L10") }).At(16.999999) == "L17" && Old(new[] { L(17.0, "L17"), L(10.0, "L10") }, 16.999999) == "L17");
            check(P + "between levels it is the one below, above the top it is the top", three.At(15.0) == "First" && three.At(500.0) == "Second");
            check(P + "two levels at one elevation give the one listed first; a level with no name gives an empty name",
                new LevelTable(new[] { L(5.0, "A"), L(5.0, "B") }).At(6.0) == "A" && new LevelTable(new[] { L(5.0, null) }).At(6.0) == "");
            check(P + "the order the levels are listed in does not matter",
                new LevelTable(new[] { L(20.0, "Second"), L(0.0, "Ground"), L(10.0, "First") }).At(12.0) == "First");
        }
    }
}
