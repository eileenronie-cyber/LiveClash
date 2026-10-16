using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>What differs between two clash lists: the pairs that are new, the pairs that are gone, and the pairs whose severity moved.</summary>
    public sealed class ClashChangeSet
    {
        /// <summary>In the later list only.</summary>
        public readonly List<ClashResult> New = new List<ClashResult>();

        /// <summary>In the earlier list only.</summary>
        public readonly List<ClashResult> Cleared = new List<ClashResult>();

        /// <summary>In both, but at a different severity: (earlier, later).</summary>
        public readonly List<KeyValuePair<ClashResult, ClashResult>> SeverityChanged = new List<KeyValuePair<ClashResult, ClashResult>>();

        /// <summary>In both, at the same severity.</summary>
        public int Unchanged;

        /// <summary>
        /// How many rows were considered on each side (after the model restriction). Both zero means the comparison saw NO
        /// clashes with the named link(s) either time - which is not the same as "nothing changed": it is also what a name
        /// that matches no row looks like.
        /// </summary>
        public int BeforeInScope, AfterInScope;

        /// <summary>The keys of <see cref="New"/>, for a row to ask "am I new?".</summary>
        public readonly HashSet<string> NewKeys = new HashSet<string>(StringComparer.Ordinal);

        public bool IsEmpty { get { return New.Count == 0 && Cleared.Count == 0 && SeverityChanged.Count == 0; } }
    }

    /// <summary>
    /// The comparison behind "what changed after a link reload": the clash list from before with the one after.
    ///
    /// By KEY (the same key the store uses, which is order-normalised for same-model pairs), never by position or by
    /// element name. Optionally restricted to the rows whose model is one of the given names - a reloaded link can only
    /// have changed clashes WITH that link, so the rest of the list is not the link's doing and would only add noise (a
    /// clash somebody introduced meanwhile by editing their own model is a different story).
    ///
    /// Pure: no Revit calls, so it is tested headless.
    /// </summary>
    public static class ClashChange
    {
        /// <summary>A model or link name as it is compared: without ".rvt", and case does not matter.</summary>
        public static string Norm(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return LinkWatch.TrimRvt(name.Trim());          // the one trim link names already go through
        }

        /// <summary>
        /// Compare <paramref name="before"/> with <paramref name="after"/>. When <paramref name="models"/> is not null, only
        /// rows whose ModelName is in it (compared by <see cref="Norm"/>, ignoring case) are considered on either side.
        /// A row with no key cannot be matched and is ignored; a key that appears twice counts once (the first one).
        /// </summary>
        public static ClashChangeSet Compare(IEnumerable<ClashResult> before, IEnumerable<ClashResult> after, ICollection<string> models)
        {
            var set = new ClashChangeSet();
            var was = Index(before, models);
            var now = Index(after, models);
            set.BeforeInScope = was.Count;
            set.AfterInScope = now.Count;

            foreach (var kv in now)
            {
                ClashResult old;
                if (!was.TryGetValue(kv.Key, out old))
                {
                    set.New.Add(kv.Value);
                    set.NewKeys.Add(kv.Key);
                }
                else if (old.Severity != kv.Value.Severity)
                    set.SeverityChanged.Add(new KeyValuePair<ClashResult, ClashResult>(old, kv.Value));
                else
                    set.Unchanged++;
            }
            foreach (var kv in was)
                if (!now.ContainsKey(kv.Key)) set.Cleared.Add(kv.Value);

            return set;
        }

        private static Dictionary<string, ClashResult> Index(IEnumerable<ClashResult> rows, ICollection<string> models)
        {
            var index = new Dictionary<string, ClashResult>(StringComparer.Ordinal);
            if (rows == null) return index;

            HashSet<string> wanted = null;
            if (models != null)
            {
                wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in models) wanted.Add(Norm(m));
            }

            foreach (var r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                if (wanted != null && !wanted.Contains(Norm(r.ModelName))) continue;
                if (!index.ContainsKey(r.Key)) index[r.Key] = r;
            }
            return index;
        }
    }
}
