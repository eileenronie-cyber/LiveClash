using System;

namespace LiveClash.Core
{
    /// <summary>
    /// Which document the live and idle paths may act on: the session's own, and nothing else.
    ///
    /// The result store, the ledgers and the deferred queue all belong to the ADOPTED document (see
    /// LiveClashApp.Adopt). Auto-verify, the deferred drain, the undo/redo branch and the live updater
    /// used to act on whichever document was active or raised the event. With a family document
    /// active (Edit Family), auto-verify judged the project's rows against the family, found none of
    /// their elements, called every one Resolved and took the list off the panel in about 40 seconds
    /// (review 2026-10-31, H1). Pure, so the rule is asserted headlessly.
    /// </summary>
    public static class SessionGuard
    {
        /// <summary>
        /// Can this KIND of document be the session's at all? A project can; a family (Edit Family, Edit
        /// In-Place) and a link's document never can. No key is compared, so it holds at DocumentSavedAs, when
        /// the path that is the key has just changed.
        /// </summary>
        public static bool CanOwnSession(bool isFamily, bool isLinked)
        {
            return !isFamily && !isLinked;
        }

        /// <summary>
        /// Should a document that has just been OPENED become the session's? Only a project the user is looking
        /// at. A project another add-in opens through the API with no view (a batch run, a background reader) is
        /// not: adopting it moved the adopted key off the user's own model, and every live path requires that key, so
        /// the model the user was editing silently stopped being checked until a view switch (second pass
        /// 2026-10-31, SP3). Such a document is adopted later, by the view switch, if the user goes to it.
        /// </summary>
        /// <param name="isFamily">The document is a family (Edit Family, Edit In-Place).</param>
        /// <param name="isLinked">The document is a link's.</param>
        /// <param name="isActive">The document is the one the user has active in the Revit window.</param>
        public static bool ShouldAdoptOnOpen(bool isFamily, bool isLinked, bool isActive)
        {
            return CanOwnSession(isFamily, isLinked) && isActive;
        }

        /// <summary>
        /// Are these the same object? Both non-null and the same reference, or Equal (Revit overrides Document.Equals, and a fresh wrapper for the
        /// same document is not the same reference).
        /// </summary>
        public static bool IsSameObject(object a, object b)
        {
            if (a == null || b == null) return false;
            return ReferenceEquals(a, b) || a.Equals(b);
        }

        /// <param name="isFamily">The document is a family (Edit Family, Edit In-Place).</param>
        /// <param name="isLinked">The document is a link's.</param>
        /// <param name="docKey">The document's key (ClashEngine.DocKey).</param>
        /// <param name="adoptedKey">The adopted document's key, or null before the first adoption.</param>
        public static bool ShouldActOn(bool isFamily, bool isLinked, string docKey, string adoptedKey)
        {
            if (!CanOwnSession(isFamily, isLinked)) return false;
            if (string.IsNullOrEmpty(docKey)) return false;

            // Nothing adopted yet: the first project document of a session, before its first view is up.
            // Its rows are the only ones there can be, and going quiet would be the dangerous direction.
            if (string.IsNullOrEmpty(adoptedKey)) return true;

            return string.Equals(docKey, adoptedKey, StringComparison.Ordinal);
        }

        /// <summary>
        /// Should the session FOLLOW a document whose key has moved under it (round 8, fix B)? A workshared model opened Detached from
        /// Central gets a new path just after its first view was activated and adopted, so the adopted key is the old path and nothing
        /// re-adopts it until the user's next view switch: in between the live updater is silent, an undo is ignored and Focus, Recheck and
        /// Full Check refuse. True only for a project the user has ACTIVE whose key differs from the adopted one, when it is the adopted
        /// document itself or no open document still holds the adopted key. A document that is not active (a background open) and another
        /// project whose key is simply not the adopted one (a view switch adopts it) never qualify, so SP3 still holds.
        /// </summary>
        /// <param name="isFamily">The document is a family (Edit Family, Edit In-Place).</param>
        /// <param name="isLinked">The document is a link's.</param>
        /// <param name="isActive">The document is the one the user has active in the Revit window.</param>
        /// <param name="isTheAdoptedDocument">The same document the session adopted (object or creation GUID).</param>
        /// <param name="adoptedKeyStillHeld">Some open document still has the adopted key as its key.</param>
        /// <param name="docKey">The document's key now.</param>
        /// <param name="adoptedKey">The adopted key, or null before the first adoption.</param>
        public static bool ShouldFollowPathChange(bool isFamily, bool isLinked, bool isActive, bool isTheAdoptedDocument,
            bool adoptedKeyStillHeld, string docKey, string adoptedKey)
        {
            if (!CanOwnSession(isFamily, isLinked)) return false;
            if (!isActive) return false;   // a background open is never followed (SP3)

            // Nothing adopted yet, or no key to compare: adoption is Adopt's own business, not a path change.
            if (string.IsNullOrEmpty(docKey) || string.IsNullOrEmpty(adoptedKey)) return false;
            if (string.Equals(docKey, adoptedKey, StringComparison.Ordinal)) return false;

            // The key moved. It moved under the adopted document (or that document is gone: nobody holds the key). A different project
            // whose key is simply not the adopted one, with the adopted key still held, is a view switch, which ViewActivated adopts.
            return isTheAdoptedDocument || !adoptedKeyStillHeld;
        }

        /// <summary>
        /// What the panel says when one of its buttons is pressed while the document that is open is not the
        /// session's (second-pass review 2026-10-31, SP1). <paramref name="action"/> completes "Open the project to
        /// ..." ("recheck", "run a full check"). Says that nothing was changed, because a refusal is the whole
        /// answer: never a partial run.
        /// </summary>
        public static string Refusal(string action)
        {
            return "Open the project" + (string.IsNullOrEmpty(action) ? "" : " to " + action)
                 + ": this document (a family being edited, or another model) is not the one Live Clash lists clashes for."
                 + " Nothing was changed.";
        }
    }
}
