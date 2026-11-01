using System;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The per-model files as one unit: the settings, the triage ledger, the resolved ledger and the
    /// audit labels, all keyed by the model's title.
    /// </summary>
    public static class ModelLedgers
    {
        /// <summary>
        /// The ACTIVE model was just saved under a new name (DocumentSavedAs). Called by the add-in's
        /// Save As handler, after its own active-document guard. <paramref name="from"/> is the document's key BEFORE the
        /// save (its original path, or the unsaved key captured in DocumentSavingAs).
        ///
        /// Each store CARRIES what it holds to the new name rather than loading the new name afresh.
        /// Loading afresh found no file for the new title, so the settings reset to the machine
        /// defaults (sets, rules and link exclusions gone, so narrower sources and missed clashes) and
        /// the triage and resolved record re-keyed to empty files (review 2026-10-31, M4; the half of
        /// 2026-09-27 R04 that was still open). The model is the same model; only its name changed.
        ///
        /// A file already at the new name (Save As over an existing file) is kept as .bak, never
        /// merged in. Audit labels follow only when they were loaded this session (they load when the
        /// audit runs); otherwise the new name starts from the defaults, as before.
        ///
        /// THE CARRY HAPPENS ONLY WHEN THE STORES ARE THE SAVED DOCUMENT'S (round 3b, <see cref="LedgerProvenance"/>). Each store
        /// remembers the key of the document it was last loaded for. Settings, triage and the resolved record follow the
        /// adopted document, and Adopt skips a family document, so with an active family document the stores are still the
        /// project's: carrying them would copy the project's records under the family's new name and point the session at it.
        /// Returns false when it refused, having touched nothing; the caller then treats the document as one that is not the
        /// session's (its queued deletions were already re-keyed and wait). The audit labels are judged on their own: they load
        /// when an audit runs, for whichever document was audited, and only follow if they are this document's.
        /// </summary>
        [RevitBound]
        public static bool FollowSaveAs(Document doc, string from)
        {
            var settings = LedgerProvenance.Decide(ClashSettings.LoadedForDocKey, from);
            var status = LedgerProvenance.Decide(ClashStatusStore.Instance.LoadedForDocKey, from);
            var resolved = LedgerProvenance.Decide(ResolvedLog.LoadedForDocKey, from);
            if (settings == LedgerProvenance.Verdict.Refuse || status == LedgerProvenance.Verdict.Refuse || resolved == LedgerProvenance.Verdict.Refuse)
            {
                Log.Info("Save As: the settings and ledgers are not this document's (they were loaded for another one, or its old key is unknown); nothing carried, nothing re-pointed.");
                return false;
            }

            Step("settings", () => ClashSettings.FollowSaveAs(doc));
            Step("clash status", () => ClashStatusStore.Instance.FollowSaveAs(doc));
            Step("resolved log", () => ResolvedLog.FollowSaveAs(doc));
            if (LedgerProvenance.Decide(AuditLabelStore.Instance.LoadedForDocKey, from) != LedgerProvenance.Verdict.Refuse)
                Step("audit labels", () => AuditLabelStore.Instance.FollowSaveAs(doc));
            return true;
        }

        /// <summary>One store at a time: a failure in one must not leave the others on the old name.</summary>
        private static void Step(string what, Action body)
        {
            try { body(); }
            catch (Exception ex) { Log.Warn("Save As: could not carry the " + what + " to the new name", ex); }
        }
    }
}
