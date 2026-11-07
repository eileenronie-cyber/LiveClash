using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    /// <summary>
    /// What the Filter window shows while the work behind it is still arriving (U1). Pure, so the rules are checked headless
    /// (Testing\FilterFreezeLogicChecks.cs).
    /// </summary>
    public static class FilterPrep
    {
        /// <summary>
        /// Is a category row shown? This is the old inline rule of FilterWindow.Fill, pulled out so it can be pinned:
        ///   * the search box (trimmed, any case, any part of the name) hides what does not match, ticked or not;
        ///   * an UNKNOWN count (-1, not counted yet) is never hidden: under-reporting is the one failure that must not happen, and a
        ///     category must not vanish from the list because its count has not arrived;
        ///   * a KNOWN count of 0 is hidden unless the row is ticked (a setting the user made is never hidden) or "show empty" is on.
        /// </summary>
        public static bool CategoryVisible(int count, bool ticked, bool showEmpty, string name, string query)
        {
            string q = (query ?? "").Trim();
            if (q.Length > 0 && (name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (count < 0) return true;
            if (count == 0 && !ticked && !showEmpty) return false;
            return true;
        }

        /// <summary>
        /// The status line while the pump works: "Counting elements… 41%". The percent is rounded down and held at 99 until the work is
        /// done, so it never says 100% while something is still running. Empty when the work finished without error; a plain sentence
        /// saying data could not be loaded, and why, when it finished with one.
        /// </summary>
        public static string StatusText(string stepName, double fraction, bool done, string error, string detail = null)
        {
            // an error is shown as soon as it has happened, not only when the whole pump is over: a step that failed early (the parameter
            // list) must not be hidden behind a long later step (the count)
            string failed = string.IsNullOrEmpty(error) ? "" : "Some data could not be loaded (" + error + ").";
            if (done) return failed;
            // the epsilon: 0.57 * 100 is 56.99999999999999 in a double, and a person reads 57%
            int pct = (int)Math.Floor(Math.Max(0.0, Math.Min(1.0, fraction)) * 100.0 + 1e-9);
            if (pct > 99) pct = 99;
            string text = (stepName ?? "") + "… " + pct.ToString(CultureInfo.InvariantCulture) + "%";
            if (!string.IsNullOrWhiteSpace(detail)) text += "  (" + detail + ")";   // what has been counted so far: moves even when the percent cannot
            if (failed.Length > 0) text += "  " + failed;
            return text;
        }

        /// <summary>
        /// The scope a rule row gets when a parameter is picked. Scope decides where the value is read (Instance only; Type only; Either =
        /// instance, then type), so a wrong one silently changes what a rule matches.
        ///   * "Workset" is always Instance; a blank name is Either and nothing to wait for.
        ///   * The catalog is only trusted once it is COMPLETE: the blank instance shadow of a Type parameter ("Family Name") is dropped at the
        ///     end, so a partial catalog can name the wrong scope. While it is still loading the answer is Either and <paramref name="pending"/>
        ///     is true: the caller works the scope out again from the finished catalog.
        ///   * Complete: the first entry with that name (the catalog is sorted by name, then Instance before Type); not in it -> Either.
        /// </summary>
        public static ParamScope ScopeFor(string name, IEnumerable<KeyValuePair<string, ParamScope>> catalog, bool catalogComplete, out bool pending)
        {
            pending = false;
            if (string.IsNullOrWhiteSpace(name)) return ParamScope.Either;
            if (string.Equals(name, "Workset", StringComparison.OrdinalIgnoreCase)) return ParamScope.Instance;
            if (!catalogComplete) { pending = true; return ParamScope.Either; }
            if (catalog != null)
                foreach (var p in catalog)
                    if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return ParamScope.Either;
        }

        /// <summary>
        /// The names in <paramref name="incoming"/> that are not yet in <paramref name="existing"/>: case-insensitive, de-duplicated (the
        /// first spelling wins), blanks dropped, in incoming order. It only ever says what to ADD: a batch of parameter names must not
        /// remove or rewrite anything the user is already looking at or has picked.
        /// </summary>
        public static List<string> NamesToAdd(IEnumerable<string> existing, IEnumerable<string> incoming)
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null) foreach (var e in existing) if (e != null) have.Add(e);

            var add = new List<string>();
            if (incoming == null) return add;
            foreach (var n in incoming)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (have.Add(n)) add.Add(n);
            }
            return add;
        }
    }
}
