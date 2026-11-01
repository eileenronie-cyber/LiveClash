using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The one place the panel's handlers ask "is this the document the list belongs to?".
    ///
    /// The result store, the ledgers and the audit labels belong to the ADOPTED document (LiveClashApp.Adopt). The
    /// idle, updater and DocumentChanged paths were guarded for that in the H1 fix, but the panel's own buttons took
    /// whatever <c>ActiveUIDocument</c> was. With a family document active (Edit Family; the panel is a dockable pane,
    /// so it stays usable there) a Recheck or Full Check judged every listed project pair against the family, found
    /// none of their elements, and recorded every one as fixed in the project's ledgers (second-pass review
    /// 2026-10-31, SP1).
    /// </summary>
    internal static class HandlerGuard
    {

        /// <summary>The document a handler acts on: the active one (null when there is none).</summary>
        internal static Document Pick(UIDocument uidoc)
        {
            return uidoc != null ? uidoc.Document : null;
        }

        /// <summary>
        /// True when the settings were read again since the list was built, after the reset (LiveClashApp.HandlePendingRulesReload): the handler
        /// returns at once, and the user presses the button again on a list found under the rules now in force. Without it a Recheck or Full Check
        /// re-tests the old rows under the new rules and RECORDS the ones that no longer clash as fixed, with nothing in the model changed (delta
        /// check of D3-1, DC-1). The retry runs first, so a swap that is due at the click lands here and not in the handler's own first read.
        /// Call it right after <see cref="Refuses"/>, before anything is read or snapshotted.
        /// </summary>
        internal static bool SettingsReloaded()
        {
            // The call sits before the handler's own try, so a throw from the retry must not leave Execute (delta review 4, DR4-2). It does not
            // refuse on a throw: a retry that keeps failing would make the button useless for good. A throw that REPEATS is met again by the
            // handler's own first read of the settings, inside its try, which logs it. One that does NOT repeat (the lines are handed over before
            // they are parsed, so the file is no longer marked unreadable) leaves the session on the defaults with no reset, and this log line is
            // the only trace (delta check of DR4, DC4-4).
            try { ClashSettings.RetryUnreadable(); }
            catch (Exception ex) { Log.Warn("The settings retry failed before a panel button ran; carrying on", ex); }
            return LiveClashApp.HandlePendingRulesReload();
        }

        /// <summary>
        /// True when <paramref name="doc"/> is NOT the session's document, after saying so on the panel: the handler
        /// returns at once, a refusal and never a partial run. <paramref name="action"/> completes "Open the project
        /// to ...".
        /// </summary>
        internal static bool Refuses(Document doc, ClashPanel panel, string action)
        {
            if (LiveClashApp.IsSessionDocument(doc)) return false;

            Log.Info("Panel button refused: the open document is not the session's (" + action + ").");
            if (panel != null) panel.SetStatus(SessionGuard.Refusal(action));
            return true;
        }
    }
}
