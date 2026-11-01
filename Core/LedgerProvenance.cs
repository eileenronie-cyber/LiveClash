using System;

namespace LiveClash.Core
{
    /// <summary>
    /// Whose records the stores hold, at the moment a Save As would carry them to a new name (round 3b of the
    /// 2026-10-31 review).
    ///
    /// Settings, triage and the resolved record follow the ADOPTED document. Adopt skips a family document and a linked one,
    /// and a new project may become active without reaching Adopt, so the document being saved can be a different one from the
    /// stores' document: Edit Family, then Save As, with the project's records loaded. The carry used to copy them under the
    /// family's new name and point the session at it. The stores now remember the document key they were last loaded for, and the
    /// carry happens only when that is the document being saved. Revit-free, so the rule is asserted headlessly.
    /// </summary>
    public static class LedgerProvenance
    {
        public enum Verdict
        {
            /// <summary>The stores are the saved document's: carry them to its new name.</summary>
            Carry,
            /// <summary>The stores have loaded nothing yet: there is nothing to carry; load the new name as an ordinary load.</summary>
            LoadFresh,
            /// <summary>The stores are another document's (or this one cannot be told): carry nothing, re-point nothing.</summary>
            Refuse
        }

        /// <param name="loadedFor">The document key the stores were last loaded for (ClashEngine.DocKey), or null if never.</param>
        /// <param name="from">The saved document's key BEFORE the save: its original path, or the unsaved key captured in DocumentSavingAs.</param>
        public static Verdict Decide(string loadedFor, string from)
        {
            // Without the old key nothing can be shown, and a carry on a guess is the failure this exists to stop.
            if (string.IsNullOrWhiteSpace(from)) return Verdict.Refuse;
            if (string.IsNullOrWhiteSpace(loadedFor)) return Verdict.LoadFresh;
            return string.Equals(loadedFor.Trim(), from.Trim(), StringComparison.OrdinalIgnoreCase) ? Verdict.Carry : Verdict.Refuse;
        }
    }
}
