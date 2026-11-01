using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// When can the reverse check (a changed TARGET against the SOURCES near it) find nothing the forward check does not (round 8, review L3)?
    /// Only when every source near a target is also a target that the forward check of the changed element reaches, and every target can be
    /// checked as a source: the source and target category sets are EQUAL and neither parameter filter excludes anything. Then the second
    /// spatial index and the whole reverse pass for that set are wasted. A looser rule (targets a subset of the sources) is NOT safe: a source
    /// that is not a target, or a target that fails the source filter, would be missed. Categories are passed as ints so the headless project
    /// never loads the Revit enum. Pure.
    /// </summary>
    public static class ReverseCheckPolicy
    {
        public static bool CanFindNothing(ICollection<int> sourceCategories, ICollection<int> targetCategories, bool sourceFilterEmpty, bool targetFilterEmpty)
        {
            if (sourceCategories == null || targetCategories == null) return false;
            if (sourceCategories.Count == 0 || targetCategories.Count == 0) return false;
            if (!sourceFilterEmpty || !targetFilterEmpty) return false;
            var s = new HashSet<int>(sourceCategories);
            var t = new HashSet<int>(targetCategories);
            return s.SetEquals(t);
        }
    }
}
