using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LiveClash.Core
{
    /// <summary>
    /// Opening the clash schedule and showing the markers in the user's own Focus view (spec 8.6, 9). Neither is part of a Refresh. Show in
    /// Focus is ONE plain transaction and never a transaction group; it opens none when the workset is already shown.
    /// </summary>
    [RevitBound]
    public static class MarkerViews
    {
        public sealed class Result
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
        }

        public sealed class ScheduleLookup
        {
            public ViewSchedule Schedule { get; set; }
            public string Message { get; set; } = "";
        }

        private static readonly char[] NotInViewNames = @"\:{}[]|;<>?`~".ToCharArray();

        private static Result Ok(string message) { return new Result { Ok = true, Message = message }; }
        private static Result Fail(string message) { return new Result { Ok = false, Message = message }; }

        // ------------------------------------------------------------------ the schedule

        /// <summary>The schedule to open, or the words for why there is none yet.</summary>
        public static ScheduleLookup FindScheduleToOpen(Document doc)
        {
            var schedule = doc == null ? null : MarkerSchedule.Find(doc);
            return schedule == null
                ? new ScheduleLookup { Message = "There is no clash schedule in this model yet. Run Refresh first." }
                : new ScheduleLookup { Schedule = schedule };
        }

        /// <summary>Makes the schedule the active view. Revit does not allow that from every context, so the active view is read back and the answer says so.</summary>
        public static Result OpenSchedule(UIDocument uidoc)
        {
            if (uidoc == null) return Fail("Open the project first.");
            var found = FindScheduleToOpen(uidoc.Document);
            if (found.Schedule == null) return Fail(found.Message);
            Exception error = null;
            try { uidoc.ActiveView = found.Schedule; } catch (Exception ex) { error = ex; }
            bool shown = false;
            try { shown = uidoc.ActiveView != null && uidoc.ActiveView.Id == found.Schedule.Id; } catch { }
            if (!shown && error != null) Log.Warn("The clash schedule could not be opened", error);
            return shown ? Ok("Opened \"" + found.Schedule.Name + "\".") : Fail("Revit would not switch to the clash schedule just now. Open \"" + found.Schedule.Name + "\" from the Project Browser.");
        }

        // ------------------------------------------------------------------ the Focus view

        /// <summary>
        /// The name of this user's own Focus view. A copy of the rule in FocusHandler (which keeps it private): the shared name, plus the user's
        /// name in a workshared model, with characters Revit refuses in view names replaced.
        /// </summary>
        public static string FocusViewNameFor(Document doc)
        {
            string user = null;
            try { if (doc.IsWorkshared) user = doc.Application.Username; } catch { }
            if (string.IsNullOrWhiteSpace(user)) return FocusOutcome.ViewName;
            var clean = new StringBuilder();
            foreach (var ch in user.Trim()) clean.Append(Array.IndexOf(NotInViewNames, ch) >= 0 ? '_' : ch);
            return FocusOutcome.ViewName + " (" + clean + ")";
        }

        private static View3D FindFocusView(Document doc)
        {
            var mine = FocusViewNameFor(doc);
            View3D shared = null;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>())
            {
                if (v == null || v.IsTemplate) continue;
                if (string.Equals(v.Name, mine, StringComparison.Ordinal)) return v;
                if (shared == null && string.Equals(v.Name, FocusOutcome.ViewName, StringComparison.Ordinal)) shared = v;
            }
            return shared;
        }

        /// <summary>
        /// Shows the LiveClash workset in the user's own Focus view, so Highlight in Model and a double-click have a view to land in (P18).
        /// ONE plain transaction, no group. Nothing happens when it is already shown, or when there is no Focus view or workset yet.
        /// </summary>
        public static Result ShowInFocus(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsValidObject) return Fail("Open the project first.");
                var off = MarkerRefusals.Check(new MarkerEnv { SwitchedOff = !MarkerSwitch.IsOn });
                if (off != null) return Fail(off);
                if (!doc.IsWorkshared) return Ok("This model is not workshared, so the markers show in every view that shows Specialty Equipment.");
                var view = FindFocusView(doc);
                if (view == null) return Fail("Frame a clash first: that makes your Focus view. Then show the markers in it.");
                int ws = MarkerWorkset.Find(doc);
                if (ws < 0) return Fail("There is no LiveClash workset in this model yet. Run Refresh first.");
                var id = new WorksetId(ws);
                if (view.GetWorksetVisibility(id) == WorksetVisibility.Visible) return Ok("The markers are already shown in your Focus view.");

                if (MarkerOwnership.HasCentral(doc))
                {
                    string owner;
                    if (WorksharingUtils.GetCheckoutStatus(doc, view.Id, out owner) == CheckoutStatus.OwnedByOtherUser)
                        return Fail("Your Focus view is held by " + (string.IsNullOrWhiteSpace(owner) ? "another user" : owner) + ". Nothing was changed.");
                }

                var failures = new MarkerRefresh.Failures();
                try
                {
                    MarkerRefresh.InTransaction(doc, MarkerTx.ShowInFocus, failures, false, () => view.SetWorksetVisibility(id, WorksetVisibility.Visible));
                }
                catch (Exception ex)
                {
                    var first = failures.Errors.Count > 0 ? failures.Errors[0] : ex.Message;
                    return Fail(first.TrimEnd() + " Nothing was changed.");
                }
                return Ok("The markers are shown in your Focus view.");
            }
            catch (Exception ex)
            {
                Log.Warn("Show markers in Focus failed", ex);
                return Fail(ex.Message + " Nothing was changed.");
            }
        }
    }
}
