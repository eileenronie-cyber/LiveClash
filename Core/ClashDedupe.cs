using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// The cross-set merge of one pair's rows (it used to live in ClashEngine.AddDeduped, a Revit-bound file; moved
    /// here, with the same stamping rule, so it can be tested headless and by mutation). Pure.
    ///
    /// Stamping (unchanged): the first-seen set stamps the row, EXCEPT that a Hard row from a later set replaces a
    /// non-Hard one (a soft row must not bury a hard one). New (spec 4b.2): the other sets that also caught the
    /// pair are recorded in <see cref="ClashResult.AlsoSets"/> instead of being dropped.
    /// </summary>
    public static class ClashDedupe
    {
        public static void Add(List<ClashResult> all, Dictionary<string, int> index, ClashResult r, string setName)
        {
            if (string.IsNullOrEmpty(r.Key)) { r.SetName = setName; all.Add(r); return; }

            int at;
            if (!index.TryGetValue(r.Key, out at))
            {
                r.SetName = setName;
                index[r.Key] = all.Count;
                all.Add(r);
                return;
            }

            var kept = all[at];
            if (r.Severity == ClashSeverity.Hard && kept.Severity != ClashSeverity.Hard)
            {
                // The replaced row's set, and the sets it already listed, are now "also caught by" on the new row.
                r.AlsoSets = Others(setName, r.AlsoSets, kept.AlsoSets, new[] { kept.SetName });
                r.SetName = setName;
                all[at] = r;
            }
            else
            {
                kept.AlsoSets = Others(kept.SetName, kept.AlsoSets, new[] { setName });
            }
        }

        /// <summary>The distinct, non-empty names in the lists, without the stamping set, ordinal-sorted; null when none (no list is created for a pair only one set caught).</summary>
        private static List<string> Others(string stamping, params IEnumerable<string>[] lists)
        {
            var names = lists.Where(l => l != null).SelectMany(l => l)
                .Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, stamping, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            return names.Count == 0 ? null : names;
        }
    }
}
