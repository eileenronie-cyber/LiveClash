using System;

namespace LiveClash.Core
{
    /// <summary>Why an element left the model, as far as DocumentChanged can tell (mapped from Revit's UndoOperation by LiveClashApp).</summary>
    public enum ChangeOrigin
    {
        /// <summary>A committed transaction (a user's own deletion).</summary>
        Committed,
        /// <summary>An undo: an element that vanishes was CREATED by the transaction that was undone.</summary>
        Undone,
        /// <summary>A redo: an element that vanishes is a deletion that is being done again.</summary>
        Redone,
        /// <summary>A transaction or a whole group was rolled back: an element that vanishes was created inside it.</summary>
        RolledBack
    }

    /// <summary>
    /// When is a vanished element a FIX (round 8, M-2 / P9-1)? A deletion is. An element that vanished because its creation was undone or rolled back
    /// was never deleted by the user: the pair it clashed in never existed in a saved state, and recording it as fixed at the save made the
    /// permanent record claim a fix for a clash nobody had. Pure, so the rule is asserted headlessly.
    /// </summary>
    public static class UndoPolicy
    {
        /// <summary>A deletion (committed, or redone) is a fix; an element that vanished because its creation was undone or rolled back is not.</summary>
        public static bool DeletedElementIsAFix(ChangeOrigin origin)
        {
            return origin == ChangeOrigin.Committed || origin == ChangeOrigin.Redone;
        }
    }
}