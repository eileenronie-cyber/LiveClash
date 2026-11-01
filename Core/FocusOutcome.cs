using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// What the panel says after Focus. Pure, so the wording rules can be asserted headless:
    /// the in-Revit half (FocusHandler) only reports what happened to its transaction.
    /// </summary>
    public static class FocusOutcome
    {
        /// <summary>The dedicated 3D view Focus frames clashes in.</summary>
        public const string ViewName = "LiveClash — Clash Focus";

        /// <summary>What one Focus click did.</summary>
        public sealed class Result
        {
            /// <summary>The transaction's status was Committed.</summary>
            public bool Committed { get; set; }
            /// <summary>The errors Revit raised, which rolled the change back.</summary>
            public List<string> Refused { get; } = new List<string>();
            /// <summary>An exception's message, when something threw.</summary>
            public string Failure { get; set; }
            /// <summary>Where the log is, for the failure message.</summary>
            public string LogPath { get; set; }
            /// <summary>At least one side was in a link.</summary>
            public bool AnyLink { get; set; }
            /// <summary>The name of the view that was framed (it differs per user in a workshared model); null means the plain name.</summary>
            public string ViewName { get; set; }
            /// <summary>The focus view is not the active view after the switch: the user is still in their own view.</summary>
            public bool NotSwitched { get; set; }
            /// <summary>Every side of every clash was selected.</summary>
            public bool Selected { get; set; }
            /// <summary>The active view could not be read after the switch, so whether Revit switched to the focus view is not known (DL-h).</summary>
            public bool ActiveUnknown { get; set; }
            /// <summary>This refusal made Focus pass the view over: the next Focus uses a new view, and the message says so (a refusal that rolled the change back only).</summary>
            public bool PassedOver { get; set; }
        }

        /// <summary>Which view is active after the switch: the focus view, another one, or one that could not be read.</summary>
        public enum ActiveState { Focus, Other, Unknown }

        /// <summary>
        /// Is the focus view the active one? <paramref name="readActiveId"/> reads the active view's id: a throw, or no
        /// active view at all, is Unknown (DL-h), never "yes".
        /// </summary>
        public static ActiveState Classify(System.Func<long?> readActiveId, long focusViewId)
        {
            long? active;
            try { active = readActiveId(); }
            catch { return ActiveState.Unknown; }
            if (active == null) return ActiveState.Unknown;
            return active.Value == focusViewId ? ActiveState.Focus : ActiveState.Other;
        }

        /// <summary>The log line for an active view that could not be read. Never names the view.</summary>
        public static string UnreadableLog(string exceptionType, string exceptionMessage)
        {
            return "Focus: could not read the active view after the switch ("
                 + (string.IsNullOrEmpty(exceptionType)
                    ? "it is not set"
                    : "reading it threw " + exceptionType + ": " + exceptionMessage)
                 + "), so whether Revit switched to the focus view is not known.";
        }

        /// <summary>
        /// "Framed" only for a committed change. A refusal or a throw rolled everything back, so
        /// it says so, and names what Revit said, rather than reporting a box that is not there.
        /// The selection is claimed only when one was made: a clash whose elements are gone (or
        /// sit in an unloaded link) still frames, but there is nothing to select.
        /// </summary>
        public static string Describe(Result r)
        {
            if (r == null) return "";
            if (r.Failure != null)
                return r.Committed
                    ? "Framed the clash in \"" + View(r) + "\", but then: " + r.Failure + ". Details in " + r.LogPath + "."
                    : "Could not frame the clash — " + r.Failure + ". Nothing was changed; details in " + r.LogPath + ".";

            if (!r.Committed)
            {
                var refused = r.Refused ?? new List<string>();
                if (refused.Count == 0)
                    return "Could not frame the clash — Revit rolled the change back. Nothing was changed." + PassedOverNote(r);
                return "Could not frame the clash — Revit refused the change: " + refused[0].Trim().TrimEnd('.')
                     + (refused.Count > 1 ? " (and " + (refused.Count - 1) + " more)" : "")
                     + ". Nothing was changed." + PassedOverNote(r);
            }

            // The box is set, but the active view could not be read, so whether Revit switched is not known (DL-h): say that, not "Framed" alone.
            if (r.ActiveUnknown)
                return "Framed the clash in \"" + View(r) + "\" with a section box. Could not read which view is active, so could not confirm "
                     + "that Revit switched to it: if you are still in your own view, open \"" + View(r) + "\" from the Project Browser (3D Views) to see it."
                     + (r.Selected ? " Both sides are selected."
                                   : " Not every side could be selected (an element is gone, or its link is not loaded).");

            // The box is set, but the user is still in their own view (Revit did not switch): say so and name the view to open.
            if (r.NotSwitched)
                return "Framed the clash in \"" + View(r) + "\" with a section box, but Revit did not switch to it: "
                     + "you are still in your own view. Open \"" + View(r) + "\" from the Project Browser (3D Views) to see it."
                     + (r.Selected ? " Both sides are selected."
                                   : " Not every side could be selected (an element is gone, or its link is not loaded).");

            if (!r.Selected)
                return "Framed the clash in \"" + View(r) + "\" with a section box. Not every side could be "
                     + "selected (an element is gone, or its link is not loaded), so the box shows where the clash is.";

            return "Framed the clash in \"" + View(r) + "\" with a section box"
                 + (r.AnyLink ? " (link geometry included)." : ".")
                 + " Both sides are selected; Selection Box (BX) works here too.";
        }

        /// <summary>The plain sentence on the refusal that made Focus pass the view over: the user's next Focus will not fail the same way.</summary>
        private static string PassedOverNote(Result r)
        {
            return r.PassedOver ? " Next time Focus makes a new view." : "";
        }

        /// <summary>
        /// The view the message names: the one that was actually framed. In a workshared model each user
        /// has their own ("... (user)"), and a rename Revit refused leaves "3D View n"; naming the plain
        /// name then points at a view that does not exist. No name given means the plain name.
        /// </summary>
        private static string View(Result r)
        {
            return string.IsNullOrEmpty(r.ViewName) ? ViewName : r.ViewName;
        }

        /// <summary>
        /// The log line for a focus view that did not become the active view. It carries the exception the
        /// setter threw, or says it threw nothing, and never names the view: in a workshared model the name
        /// carries the user's name, and the log is collected.
        /// </summary>
        public static string SwitchLog(string exceptionType, string exceptionMessage)
        {
            return "Focus: the focus view did not become the active view ("
                 + (string.IsNullOrEmpty(exceptionType)
                    ? "setting the active view threw nothing, and the active view did not change"
                    : "setting the active view threw " + exceptionType + ": " + exceptionMessage)
                 + ").";
        }
    }

    /// <summary>
    /// Focus views Revit refused to edit, per document: passed over by the next click instead of reused (a view that is not
    /// borrowed but cannot be edited would otherwise roll every click back).
    /// </summary>
    public static class FocusRefusals
    {
        /// <summary>How many views per document are passed over at most. Each one passed over means one more view made.</summary>
        public const int MaxPerDocument = 3;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, HashSet<long>> Refused = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<long>> Struck = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

        /// <summary>
        /// A rolled-back Focus on this view. Returns true when the view is now passed over: it takes TWO refusals in a row, because one failure
        /// may be a one-off (a view that really cannot be edited fails again, a one-off error does not). A success on the view in between
        /// (<see cref="NoteSuccess"/>) forgives the first. Bounded like <see cref="Note"/>.
        /// </summary>
        public static bool NoteFailure(string docKey, long viewId)
        {
            if (docKey == null) return false;
            lock (Gate)
            {
                if (IsRefused(docKey, viewId)) return true;
                HashSet<long> struck;
                if (!Struck.TryGetValue(docKey, out struck)) { struck = new HashSet<long>(); Struck[docKey] = struck; }
                if (struck.Add(viewId)) return false;      // the first refusal: it may be a one-off
                struck.Remove(viewId);
                Note(docKey, viewId);                        // the second in a row: pass the view over
                return IsRefused(docKey, viewId);
            }
        }

        /// <summary>A Focus on this view committed: an earlier single refusal is forgiven.</summary>
        public static void NoteSuccess(string docKey, long viewId)
        {
            if (docKey == null) return;
            lock (Gate)
            {
                HashSet<long> struck;
                if (Struck.TryGetValue(docKey, out struck)) struck.Remove(viewId);
            }
        }

        /// <summary>Remember that Revit refused to edit this view in this document.</summary>
        public static void Note(string docKey, long viewId)
        {
            if (docKey == null) return;
            lock (Gate)
            {
                HashSet<long> set;
                if (!Refused.TryGetValue(docKey, out set)) { set = new HashSet<long>(); Refused[docKey] = set; }
                if (set.Count < MaxPerDocument) set.Add(viewId);   // bounded: a run of unrelated failures must not make a new view each time
            }
        }

        /// <summary>Did Revit refuse to edit this view in this document earlier this session?</summary>
        public static bool IsRefused(string docKey, long viewId)
        {
            if (docKey == null) return false;
            lock (Gate)
            {
                HashSet<long> set;
                return Refused.TryGetValue(docKey, out set) && set.Contains(viewId);
            }
        }

        /// <summary>Let go of a document's refusals (it closed, or the user may have fixed the cause).</summary>
        public static void Forget(string docKey)
        {
            if (docKey == null) return;
            lock (Gate) { Refused.Remove(docKey); Struck.Remove(docKey); }
        }
    }
}
