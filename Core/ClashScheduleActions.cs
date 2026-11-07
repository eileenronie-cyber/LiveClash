using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LiveClash.Core
{
    // The clash schedule as the panel sees it (spec 9): what the panel may ask for, and the plain data that
    // comes back. Revit-free on purpose. The panel never reads Revit for the schedule: it reads the in-memory
    // snapshot, and the Revit side replaces that snapshot (and raises Changed) after it opens a model, reloads,
    // refreshes or reconciles. Writes are Task-returning calls the Revit side completes from its ExternalEvent.
    //
    // RULES FOR THE IMPLEMENTER
    //   * Complete every Task through a TaskCompletionSource created with RunContinuationsAsynchronously.
    //   * Never block on a Task from the UI (a check scans the schedule UI files for waits).
    //   * A refusal or a failure is a result (Ok = false, Message), not an exception. A Task that does fault is
    //     shown on the panel by the controller, so it is still safe, but it is not the contract.

    /// <summary>What the panel shows on one row about that row's marker. Everything optional; null reads as empty.</summary>
    public sealed class RowScheduleInfo
    {
        public bool HasMarker { get; set; }
        /// <summary>"JD-0042".</summary>
        public string IssueNo { get; set; }
        /// <summary>Numbers of duplicates merged into this marker (6.6); the panel's search finds the row by them too.</summary>
        public List<string> MergedIssueNos { get; set; } = new List<string>();
        public string AssignedTo { get; set; }
        /// <summary>Who holds the marker right now (workshared only).</summary>
        public string HeldBy { get; set; }
        /// <summary>"JD-0042 (+13)", empty for a pair alone.</summary>
        public string GroupLabel { get; set; }
        /// <summary>Who made the current decision on the marker.</summary>
        public string DecidedBy { get; set; }
        /// <summary>A triage the model has not taken yet (6.7): the status it asks for, and why it waits ("held by kdoe until they synchronize").</summary>
        public string PendingStatus { get; set; }
        public string PendingReason { get; set; }
    }

    /// <summary>Everything the split button, the menu and the grid's new columns need. In memory; never reads Revit.</summary>
    public sealed class ScheduleSnapshot
    {
        /// <summary>This seat's Revit user name.</summary>
        public string Me { get; set; }
        public bool Workshared { get; set; }
        /// <summary>True when this snapshot was read from a document (the session's), false for the empty one a panel starts with and after the document closes: until it is true the panel cannot know which rows have markers.</summary>
        public bool Ready { get; set; }
        /// <summary>Why the snapshot could not be read, when it is not ready because a read was refused or failed (delta review D-L3); null otherwise.</summary>
        public string LoadFailure { get; set; }
        public int MarkerCount { get; set; }
        /// <summary>How many markers differ from the panel (the chip).</summary>
        public int OutOfDate { get; set; }
        public int ResolvedCount { get; set; }
        public bool HasSchedule { get; set; }
        public bool ParametersPresent { get; set; }
        /// <summary>Why a write is refused right now (spec 7: read-only, closed workset, family document...), or null.</summary>
        public string RefusalReason { get; set; }
        /// <summary>The panel row key (ClashResult.Key) to what is known about that row's marker.</summary>
        public Dictionary<string, RowScheduleInfo> Rows { get; set; } = new Dictionary<string, RowScheduleInfo>();

        public bool HasMarkers { get { return MarkerCount > 0; } }
        public bool Refused { get { return !string.IsNullOrWhiteSpace(RefusalReason); } }

        public RowScheduleInfo Row(string rowKey)
        {
            RowScheduleInfo info = null;
            if (rowKey == null || Rows == null || !Rows.TryGetValue(rowKey, out info)) return null;
            return info;
        }
    }

    /// <summary>The Refresh plan as the dialog shows it: counts and reasons, no engine types. Built by the Revit side from the pure plan.</summary>
    public sealed class RefreshPlanView
    {
        /// <summary>Set when the Refresh may not run at all (spec 7); the dialog is not shown.</summary>
        public string Refusal { get; set; }
        /// <summary>A Create (no markers yet) always shows the dialog and names what it adds.</summary>
        public bool IsCreate { get; set; }
        /// <summary>The plan's own rule (6.2): more than 50 creations, parameters or workset to add, anything passed over, retention offered, 5,000 crossed.</summary>
        public bool NeedsDialog { get; set; }
        public bool NonWorkshared { get; set; }
        public int Clashes { get; set; }
        public int ExistingMarkers { get; set; }

        public int Create { get; set; }
        public string FirstIssueNo { get; set; }
        public string LastIssueNo { get; set; }
        public int Update { get; set; }
        public int Move { get; set; }
        public int Resolve { get; set; }
        public int Merge { get; set; }
        public int PendingApplied { get; set; }
        public int Unchanged { get; set; }
        public List<MarkerSummary.Reason> PassedOver { get; set; } = new List<MarkerSummary.Reason>();
        public List<MarkerSummary.Reason> NotRetested { get; set; } = new List<MarkerSummary.Reason>();

        public int RetentionCount { get; set; }
        public int RetentionDays { get; set; } = 30;

        public bool OverCap { get; set; }
        public int WouldCreate { get; set; }
        public int Cap { get; set; } = 2000;

        public bool OverWarn { get; set; }
        public int TotalMarkers { get; set; }
        /// <summary>Counts by status for the size warning (E7).</summary>
        public List<MarkerSummary.Reason> ByStatus { get; set; } = new List<MarkerSummary.Reason>();

        /// <summary>The first Create names what it adds to the model (parameters, workset, schedule, type).</summary>
        public List<string> FirstCreateAdds { get; set; } = new List<string>();
        /// <summary>Existing Specialty Equipment schedules and view filters that will show markers (M7).</summary>
        public List<string> ExistingSchedulesShowing { get; set; } = new List<string>();
    }

    /// <summary>What the user ticked in the plan dialog.</summary>
    public sealed class RefreshChoices
    {
        public bool RemoveRetention { get; set; }
        /// <summary>Over the cap: create only the first Cap markers (by severity).</summary>
        public bool CreateCapOnly { get; set; }
        /// <summary>Non-workshared Create: the user ticked "I understand markers show in views and on sheets".</summary>
        public bool ConfirmedNonWorkshared { get; set; }
    }

    public sealed class RefreshOutcome
    {
        public bool Ok { get; set; }
        /// <summary>The first error and "Nothing was changed." when a run was rolled back.</summary>
        public string Message { get; set; }
        /// <summary>Counts re-read from the model after the commit (C5), never taken from the plan.</summary>
        public MarkerSummary.Counts Counts { get; set; }
        public List<MarkerSummary.SetCounts> Sets { get; set; } = new List<MarkerSummary.SetCounts>();
        /// <summary>Ready sentences for the user, in order, from the assembler (builder A): decisions the model replaced, then each pending triage not applied (spec 6.3, 6.7: never silent). Empty when there is nothing to say.</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }

    public sealed class ActionResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }

        public static ActionResult Success(string message) { return new ActionResult { Ok = true, Message = message }; }
        public static ActionResult Fail(string message) { return new ActionResult { Ok = false, Message = message }; }
    }

    /// <summary>A name the Assign dialog offers, with why it is there ("created 41 of the A elements; owns Workset1").</summary>
    public sealed class AssignCandidate
    {
        public string Name { get; set; }
        public string Reason { get; set; }
    }

    public sealed class AssignOutcome
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public int Assigned { get; set; }
        /// <summary>Rows whose marker is held: kept as "pending to model" instead (6.7).</summary>
        public int Pending { get; set; }
    }

    /// <summary>What the panel may ask the Revit side to do for the clash schedule. Implemented by the Revit layer, faked in checks.</summary>
    public interface IClashScheduleActions
    {
        /// <summary>In memory; never touches Revit.</summary>
        ScheduleSnapshot Snapshot { get; }

        /// <summary>Raised on the UI thread after the snapshot was replaced.</summary>
        event Action Changed;

        /// <summary>Pre-flight with no transaction. A refusal comes back in <see cref="RefreshPlanView.Refusal"/>.</summary>
        Task<RefreshPlanView> PlanRefresh();

        /// <summary>One transaction; rolled back whole on any error.</summary>
        Task<RefreshOutcome> ApplyRefresh(RefreshChoices choices);

        Task<ActionResult> OpenSchedule();
        Task<ActionResult> ShowMarkersInFocus();
        Task<ActionResult> RemoveResolved();
        Task<ActionResult> RemoveAllMarkers();
        Task<ActionResult> RemoveParameters();

        /// <summary>Names to offer for these rows (browse, not type, C1), each with its reason.</summary>
        Task<List<AssignCandidate>> AssignCandidates(IReadOnlyList<string> rowKeys);

        /// <summary>"" or null unassigns. One transaction and one Undo; a held marker becomes a pending triage.</summary>
        Task<AssignOutcome> Assign(IReadOnlyList<string> rowKeys, string user);

        Task<ActionResult> DiscardPending(string rowKey);

        /// <summary>
        /// Triage on rows that have a marker (spec 6.7): the marker first, as one transaction and one Undo; a marker that is held
        /// becomes a pending triage; the ledger follows. The panel sends only the keys that have a marker.
        /// </summary>
        Task<ActionResult> SetStatus(IReadOnlyList<string> rowKeys, ClashStatusKind kind, string note);

        /// <summary>A note only, the status left as it is. An empty note clears it. Same route as <see cref="SetStatus"/>.</summary>
        Task<ActionResult> SetNote(IReadOnlyList<string> rowKeys, string note);
    }
}
