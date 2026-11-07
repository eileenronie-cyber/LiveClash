using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace LiveClash.Core
{
    /// <summary>The windows and messages the flows need. Implemented by the WPF layer, faked in checks.</summary>
    public interface IScheduleDialogs
    {
        /// <summary>The Refresh plan dialog; true when the user pressed the primary button.</summary>
        bool ShowPlan(RefreshPlanModel model);
        /// <summary>A confirmation; true when the user pressed OK (the controller still requires the box ticked).</summary>
        bool Confirm(ConfirmModel model);
        /// <summary>The Assign dialog; null when cancelled.</summary>
        AssignResult ShowAssign(AssignModel model);
        /// <summary>The panel's status line.</summary>
        void Status(string text);
        /// <summary>A refusal or an error the user must see.</summary>
        void Message(string text);
    }

    /// <summary>
    /// The clash schedule flows (spec 6.2, 6.7, 6.9, 7, 9) between the panel and the Revit side. Pure.
    ///
    /// One flow at a time. A refusal, a cancel or a failure is a message or a status, never an exception, and
    /// stops the flow before its next step. Nothing here blocks on a Task: every flow awaits, and the panel
    /// resumes on its own thread.
    /// </summary>
    public sealed class ScheduleController
    {
        private const string NoAnswer = "The clash schedule did not answer.";
        private const string NothingChanged = "Nothing was changed.";
        private const string DecisionNotRecorded = "Your decision was NOT recorded.";
        private const string NoteNotRecorded = "Your note was NOT recorded.";
        private const string AssignNotRecorded = "Your assignment was NOT recorded.";
        private const string StillLoading = "The clash schedule is still loading for this model; try again in a moment.";

        private readonly IClashScheduleActions _actions;
        private readonly IScheduleDialogs _dialogs;
        private bool _busy;

        public ScheduleController(IClashScheduleActions actions, IScheduleDialogs dialogs)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        }

        /// <summary>True while a flow is running; the panel can grey the menu with it.</summary>
        public bool Busy { get { return _busy; } }

        public Task Refresh() { return Guard(RefreshFlow); }
        public Task Open() { return Guard(async () => Report(await _actions.OpenSchedule())); }
        public Task ShowInFocus() { return Guard(async () => Report(await _actions.ShowMarkersInFocus())); }
        public Task RemoveResolved() { return Guard(RemoveResolvedFlow); }
        public Task RemoveAll() { return Guard(RemoveAllFlow); }
        public Task RemoveParameters() { return Guard(RemoveParametersFlow); }
        public Task Assign(IReadOnlyList<string> rowKeys) { return Guard(() => AssignFlow(rowKeys)); }
        public Task DiscardPending(string rowKey) { return Guard(async () => Report(await _actions.DiscardPending(rowKey))); }
        /// <summary>True when the Revit side took the triage; false for a refusal, a failure or a busy controller: then nothing was written on the marker side.</summary>
        public Task<bool> SetStatus(IReadOnlyList<string> rowKeys, ClashStatusKind kind, string note) { return GuardAnswer(() => TriageFlow(rowKeys, true, kind, note), DecisionNotRecorded); }
        public Task<bool> SetNote(IReadOnlyList<string> rowKeys, string note) { return GuardAnswer(() => TriageFlow(rowKeys, false, ClashStatusKind.New, note), NoteNotRecorded); }

        private async Task Guard(Func<Task> flow)
        {
            if (_busy)
            {
                _dialogs.Message("Another clash schedule action is still running.");
                return;
            }
            _busy = true;
            try { await flow(); }
            catch (Exception ex) { _dialogs.Message("The clash schedule could not finish: " + ex.Message); }
            finally { _busy = false; }
        }

        // Guard for a flow with a yes or no answer (a refused, busy or failed flow answers no).
        // The decision is written nowhere when the answer is no (a marked row's ledger line would be overwritten by the marker at the next Refresh),
        // so every no says so: what went wrong, then that it was NOT recorded.
        /// <summary>Shows why triage is refused (the clash schedule still loading for this model, or the snapshot refusing writes) and answers true; false when triage may go on.</summary>
        public bool RefuseTriage(bool note)
        {
            return RefuseWrite(note ? NoteNotRecorded : DecisionNotRecorded);
        }

        // The one gate: not Ready (the snapshot may be another model's), or refused (read-only, workset closed, set-up held: the markers may not
        // even be loaded, so a ledger-only write for a marked row would be replaced by the marker at the next Refresh, delta review D-H1).
        private bool RefuseWrite(string notRecorded)
        {
            var snap = Snap();
            // Not Ready because the read failed or was refused: say why (delta review D-L3), or the wait would look endless.
            if (!snap.Ready)
            {
                _dialogs.Message((string.IsNullOrWhiteSpace(snap.LoadFailure) ? StillLoading : snap.LoadFailure.Trim()) + " " + notRecorded);
                return true;
            }
            if (snap.Refused) { _dialogs.Message(snap.RefusalReason.Trim() + " " + notRecorded); return true; }
            return false;
        }

        private async Task<bool> GuardAnswer(Func<Task<bool>> flow, string notRecorded)
        {
            if (_busy)
            {
                _dialogs.Message("Another clash schedule action is still running; try again. " + notRecorded);
                return false;
            }
            _busy = true;
            try { return await flow(); }
            catch (Exception ex)
            {
                _dialogs.Message("The clash schedule could not finish: " + ex.Message + " " + notRecorded);
                return false;
            }
            finally { _busy = false; }
        }

        private ScheduleSnapshot Snap() { return _actions.Snapshot ?? new ScheduleSnapshot(); }

        private bool Refused(ScheduleSnapshot s)
        {
            if (!s.Refused) return false;
            _dialogs.Message(s.RefusalReason.Trim());
            return true;
        }

        private void Cancelled() { _dialogs.Status("Cancelled. " + NothingChanged); }

        private bool Ask(ConfirmModel model) { return _dialogs.Confirm(model) && model.CanOk; }

        private void Report(ActionResult r)
        {
            if (r == null) { _dialogs.Message(NoAnswer); return; }
            if (!r.Ok) { _dialogs.Message(string.IsNullOrWhiteSpace(r.Message) ? "The action failed. " + NothingChanged : r.Message.Trim()); return; }
            if (!string.IsNullOrWhiteSpace(r.Message)) _dialogs.Status(r.Message.Trim());
        }

        private static int Pos(int n) { return n > 0 ? n : 0; }

        // ------------------------------------------------------------------ refresh / create

        private async Task RefreshFlow()
        {
            if (Refused(Snap())) return;

            var plan = await _actions.PlanRefresh();
            if (plan == null) { _dialogs.Message(NoAnswer); return; }
            if (!string.IsNullOrWhiteSpace(plan.Refusal)) { _dialogs.Message(plan.Refusal.Trim()); return; }

            int writes = Pos(plan.Create) + Pos(plan.Update) + Pos(plan.Move) + Pos(plan.Resolve) + Pos(plan.Merge) + Pos(plan.PendingApplied);
            if (plan.Clashes <= 0 && plan.ExistingMarkers <= 0 && writes == 0)
            {
                _dialogs.Status(MarkerSummary.Describe(new MarkerSummary.Counts()));
                return;
            }
            bool dialog = plan.IsCreate || plan.NeedsDialog || plan.OverCap || plan.RetentionCount > 0;
            if (!dialog && writes == 0)
            {
                _dialogs.Status(MarkerSummary.NothingToDo(plan.ExistingMarkers));
                return;
            }

            bool confirmedNonWorkshared = false;
            if (plan.IsCreate && plan.NonWorkshared)
            {
                if (!Ask(ConfirmModel.CreateNonWorkshared())) { Cancelled(); return; }
                confirmedNonWorkshared = true;
            }

            var model = new RefreshPlanModel(plan);
            if (dialog && !(_dialogs.ShowPlan(model) && model.CanApply)) { Cancelled(); return; }

            var choices = model.Choices();
            choices.ConfirmedNonWorkshared = confirmedNonWorkshared;
            var outcome = await _actions.ApplyRefresh(choices);
            if (outcome == null) { _dialogs.Message(NoAnswer); return; }
            if (!outcome.Ok) { _dialogs.Message(Failure(outcome.Message, "The refresh failed.")); return; }
            _dialogs.Status(Summary(outcome));
            var notes = Notes(outcome);
            if (notes.Count > 0) _dialogs.Message(string.Join("\n", notes));
        }

        // A rolled-back refresh always says nothing was changed (spec 7).
        private static string Failure(string message, string fallback)
        {
            var text = string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
            return text.Contains(NothingChanged) ? text : text + " " + NothingChanged;
        }

        // Never silent (spec 6.3, 6.7): the decisions the model replaced and the pending triage that was not applied, in the assembler's own
        // words. A message, not the status bar, which is one trimmed line.
        private static List<string> Notes(RefreshOutcome o)
        {
            return (o.Notes ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        }
        private static string Summary(RefreshOutcome o)
        {
            if (o.Counts == null) return string.IsNullOrWhiteSpace(o.Message) ? "Done." : o.Message.Trim();
            var parts = new List<string> { MarkerSummary.Describe(o.Counts) };
            parts.AddRange((o.Sets ?? new List<MarkerSummary.SetCounts>()).Select(MarkerSummary.SetLine).Where(s => s.Length > 0));
            if (!MarkerSummary.Balanced(o.Counts)) parts.Add("The counts do not add up; please report this.");
            return string.Join(" ", parts);
        }

        // ------------------------------------------------------------------ removals

        private async Task RemoveResolvedFlow()
        {
            var s = Snap();
            if (Refused(s)) return;
            if (s.ResolvedCount <= 0) { _dialogs.Message("No markers are resolved."); return; }
            if (!Ask(ConfirmModel.RemoveResolved(s.ResolvedCount))) { Cancelled(); return; }
            Report(await _actions.RemoveResolved());
        }

        private async Task RemoveAllFlow()
        {
            var s = Snap();
            if (Refused(s)) return;
            if (s.MarkerCount <= 0) { _dialogs.Message("There are no markers to remove."); return; }
            if (!Ask(ConfirmModel.RemoveAll(s.MarkerCount))) { Cancelled(); return; }
            Report(await _actions.RemoveAllMarkers());
        }

        private async Task RemoveParametersFlow()
        {
            var s = Snap();
            if (Refused(s)) return;
            if (s.HasMarkers) { _dialogs.Message("Remove the markers first."); return; }
            if (!s.ParametersPresent) { _dialogs.Message("There are no LiveClash parameters to remove."); return; }
            if (!Ask(ConfirmModel.RemoveParameters())) { Cancelled(); return; }
            Report(await _actions.RemoveParameters());
        }

        // ------------------------------------------------------------------ triage on rows that have a marker

        // One flow for both: a status with its note, or a note alone (the kind is then not used).
        private async Task<bool> TriageFlow(IReadOnlyList<string> rowKeys, bool withStatus, ClashStatusKind kind, string note)
        {
            if (rowKeys == null || rowKeys.Count == 0) { _dialogs.Message("Select an issue first."); return false; }
            var notRecorded = withStatus ? DecisionNotRecorded : NoteNotRecorded;
            // Not Ready (it may even be another model's snapshot) or refused: refuse before anything else, so no decision lands on the wrong
            // path (review H1, delta review D-H1).
            if (RefuseTriage(!withStatus)) return false;
            var answer = withStatus ? await _actions.SetStatus(rowKeys, kind, note) : await _actions.SetNote(rowKeys, note);
            if (answer == null) { _dialogs.Message(NoAnswer + " " + notRecorded); return false; }
            if (!answer.Ok)
            {
                _dialogs.Message((string.IsNullOrWhiteSpace(answer.Message) ? "The action failed. " + NothingChanged : answer.Message.Trim()) + " " + notRecorded);
                return false;
            }
            if (!string.IsNullOrWhiteSpace(answer.Message)) _dialogs.Status(answer.Message.Trim());
            return true;
        }

        // ------------------------------------------------------------------ assign

        private async Task AssignFlow(IReadOnlyList<string> rowKeys)
        {
            if (rowKeys == null || rowKeys.Count == 0) { _dialogs.Message("Select at least one clash."); return; }
            if (RefuseWrite(AssignNotRecorded)) return;   // same gate as triage (delta review D-I5): nothing asked, no dialog
            var s = Snap();

            var candidates = await _actions.AssignCandidates(rowKeys);
            var cells = rowKeys.Select(k => RowScheduleText.For(s, k)).ToList();
            var pending = cells.Where(c => c.HeldBy.Length > 0).Select(c => (c.IssueNo.Length > 0 ? c.IssueNo : "a clash") + ": held by " + c.HeldBy).ToList();
            var model = new AssignModel(cells.Select(c => c.IssueNo).ToList(), candidates, s.Me, pending);

            var picked = _dialogs.ShowAssign(model);
            if (picked == null) { Cancelled(); return; }

            var outcome = await _actions.Assign(rowKeys, picked.Unassign ? "" : picked.User);
            if (outcome == null) { _dialogs.Message(NoAnswer); return; }
            if (!outcome.Ok) { _dialogs.Message(string.IsNullOrWhiteSpace(outcome.Message) ? "The assignment failed. " + NothingChanged : outcome.Message.Trim()); return; }
            _dialogs.Status(AssignText(outcome, picked));
        }

        private static string Clashes(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " clash" : " clashes");
        }

        private static string AssignText(AssignOutcome o, AssignResult picked)
        {
            int done = Pos(o.Assigned), waiting = Pos(o.Pending);
            string verb = picked.Unassign ? "Unassigned " : "Assigned ";
            string tail = picked.Unassign ? "" : " to " + picked.User;
            string pend = Clashes(waiting) + (waiting == 1 ? " is pending (another user holds its marker)" : " are pending (another user holds the markers)");
            if (done > 0 && waiting == 0) return verb + Clashes(done) + tail + ".";
            if (done > 0) return verb + Clashes(done) + tail + "; " + pend + ".";
            if (waiting > 0) return pend + "; nothing was written to the model.";
            return "Nothing was assigned.";
        }
    }
}
