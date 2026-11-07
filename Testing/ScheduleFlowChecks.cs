using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B3: the flows of <see cref="ScheduleController"/> against a fake Revit side and
    /// fake dialogs (spec 6.2, 6.7, 6.9, 7, 9). Revit-free. Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class ScheduleFlowChecks
    {
        private static string P = "schedule flow: ";

        // The fakes complete at once (the awaits never suspend) in the first pass and on another thread in the second,
        // so both paths through every await in the controller are run. Added at green: the first pass alone left the
        // compiler's "not yet complete" branch of each await uncovered.
        private static bool _async;

        private static Task<T> Tk<T>(T value) { return _async ? Task.Run(() => value) : Task.FromResult(value); }

        private static List<string> S(params string[] items) { return items.ToList(); }

        public static void RunAll(Action<string, bool?> check)
        {
            // The controller's awaits resume on the caller's SynchronizationContext. In a Revit self-test that is the UI thread's, which is blocked
            // right here in Task.Wait: every async flow waited out its whole timeout (about 5 s each, 307 s in all) and was then read before it had
            // finished. With no context the continuations run on the pool and the wait ends when the flow does (SyncContextChecks proves it).
            var saved = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Pass(check, false);
                Pass(check, true);
            }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(saved); }
        }

        private static void Pass(Action<string, bool?> check, bool later)
        {
            _async = later;
            P = later ? "schedule flow (async): " : "schedule flow: ";
            try
            {
                Refusals(check);
                Dialogs(check);
                Outcomes(check);
                Busy(check);
                Removals(check);
                Assigns(check);
                Misc(check);
                Edges(check);
                Triage(check);
                Review(check);
                TriageAnswer(check);
                Loading(check);
                Refused(check);
                AssignGate(check);
                LoadFailure(check);
            }
            finally { _async = false; P = "schedule flow: "; }
        }

        // ------------------------------------------------------------------ fakes

        private sealed class FakeActions : IClashScheduleActions
        {
            public ScheduleSnapshot Snapshot { get; set; } = new ScheduleSnapshot { Me = "jdoe", Workshared = true, Ready = true, MarkerCount = 140, ResolvedCount = 8, HasSchedule = true, ParametersPresent = true };
            public event Action Changed { add { } remove { } }
            public readonly List<string> Calls = new List<string>();
            public RefreshChoices LastChoices;
            public IReadOnlyList<string> LastKeys;
            public string LastUser, LastKey, LastNote;
            public ClashStatusKind LastKind;
            public Func<Task<ActionResult>> OnSetStatus = () => Tk(ActionResult.Success("Set 2 clashes to Ignored."));
            public Func<Task<ActionResult>> OnSetNote = () => Tk(ActionResult.Success("Note saved on 2 clashes."));

            public Func<Task<RefreshPlanView>> OnPlan = () => Tk(Plan());
            public Func<RefreshChoices, Task<RefreshOutcome>> OnApply = c => Tk(Good());
            public Func<Task<ActionResult>> OnOpen = () => Tk(ActionResult.Success(null));
            public Func<Task<ActionResult>> OnFocus = () => Tk(ActionResult.Success(null));
            public Func<Task<ActionResult>> OnRemoveResolved = () => Tk(ActionResult.Success("Removed 8 resolved markers."));
            public Func<Task<ActionResult>> OnRemoveAll = () => Tk(ActionResult.Success("Removed 140 markers."));
            public Func<Task<ActionResult>> OnRemoveParameters = () => Tk(ActionResult.Success("Removed the LiveClash parameters."));
            public Func<Task<List<AssignCandidate>>> OnCandidates = () => Tk(new List<AssignCandidate> { new AssignCandidate { Name = "asmith", Reason = "seen" }, new AssignCandidate { Name = "kdoe", Reason = "seen" } });
            public Func<Task<AssignOutcome>> OnAssign = () => Tk(new AssignOutcome { Ok = true, Assigned = 2 });
            public Func<Task<ActionResult>> OnDiscard = () => Tk(ActionResult.Success("Pending discarded."));

            public Task<RefreshPlanView> PlanRefresh() { Calls.Add("plan"); return OnPlan(); }
            public Task<RefreshOutcome> ApplyRefresh(RefreshChoices choices) { Calls.Add("apply"); LastChoices = choices; return OnApply(choices); }
            public Task<ActionResult> OpenSchedule() { Calls.Add("open"); return OnOpen(); }
            public Task<ActionResult> ShowMarkersInFocus() { Calls.Add("focus"); return OnFocus(); }
            public Task<ActionResult> RemoveResolved() { Calls.Add("removeResolved"); return OnRemoveResolved(); }
            public Task<ActionResult> RemoveAllMarkers() { Calls.Add("removeAll"); return OnRemoveAll(); }
            public Task<ActionResult> RemoveParameters() { Calls.Add("removeParameters"); return OnRemoveParameters(); }
            public Task<List<AssignCandidate>> AssignCandidates(IReadOnlyList<string> rowKeys) { Calls.Add("candidates"); return OnCandidates(); }
            public Task<AssignOutcome> Assign(IReadOnlyList<string> rowKeys, string user) { Calls.Add("assign"); LastKeys = rowKeys; LastUser = user; return OnAssign(); }
            public Task<ActionResult> DiscardPending(string rowKey) { Calls.Add("discard"); LastKey = rowKey; return OnDiscard(); }
            public Task<ActionResult> SetStatus(IReadOnlyList<string> rowKeys, ClashStatusKind kind, string note) { Calls.Add("setStatus"); LastKeys = rowKeys; LastKind = kind; LastNote = note; return OnSetStatus(); }
            public Task<ActionResult> SetNote(IReadOnlyList<string> rowKeys, string note) { Calls.Add("setNote"); LastKeys = rowKeys; LastNote = note; return OnSetNote(); }
        }

        private sealed class FakeDialogs : IScheduleDialogs
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Statuses = new List<string>();
            public readonly List<string> Messages = new List<string>();
            public Func<RefreshPlanModel, bool> OnPlan = m => true;
            public Func<ConfirmModel, bool> OnConfirm = m => { m.Ticked = true; return true; };
            public Func<AssignModel, AssignResult> OnAssign = m => { m.SelectCandidate("asmith"); return m.Result(); };
            public RefreshPlanModel LastPlan;
            public ConfirmModel LastConfirm;
            public AssignModel LastAssign;

            public bool ShowPlan(RefreshPlanModel model) { Calls.Add("plan"); LastPlan = model; return OnPlan(model); }
            public bool Confirm(ConfirmModel model) { Calls.Add("confirm:" + model.Kind); LastConfirm = model; return OnConfirm(model); }
            public AssignResult ShowAssign(AssignModel model) { Calls.Add("assign"); LastAssign = model; return OnAssign(model); }
            public void Status(string text) { Calls.Add("status"); Statuses.Add(text); }
            public void Message(string text) { Calls.Add("message"); Messages.Add(text); }
        }

        private static RefreshPlanView Plan(Action<RefreshPlanView> f = null)
        {
            var v = new RefreshPlanView { Clashes = 10, ExistingMarkers = 140, Unchanged = 140 };
            if (f != null) f(v);
            return v;
        }

        private static RefreshOutcome Good(Action<RefreshOutcome> f = null)
        {
            var o = new RefreshOutcome { Ok = true, Counts = new MarkerSummary.Counts { Clashes = 5, Created = 5 } };
            o.Sets.Add(new MarkerSummary.SetCounts { Name = "MEP vs STR", InPanel = 5, UpToDate = 5 });
            if (f != null) f(o);
            return o;
        }

        private static ScheduleController Make(FakeActions a, FakeDialogs d) { return new ScheduleController(a, d); }

        /// <summary>How long a check waits for a flow to finish before it gives up on it (a check about the waiting itself shortens it).</summary>
        // Thread-static (DR3-L5): a worker that is abandoned after a timed-out Join keeps its own wait and counts, so nothing it does can reach the checks that run next.
        // An override with a default, NOT `[ThreadStatic] ... = 5000`: an initialiser of a thread-static field runs on the first thread only, and every other thread would read 0.
        [ThreadStatic] private static int _waitOverride;
        internal static int WaitMs { get { return _waitOverride > 0 ? _waitOverride : 5000; } set { _waitOverride = value; } }

        private static void Run(Task t) { t.Wait(WaitMs); }

        private static bool Has(IEnumerable<string> list, string text) { return list.Any(s => s == text); }

        private static bool Contains(IEnumerable<string> list, string part) { return list.Any(s => s != null && s.Contains(part)); }

        // ------------------------------------------------------------------ refusals and the quiet outcomes

        private static void Refusals(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot.RefusalReason = "This model is read-only.";
            var c = Make(a, d);
            Run(c.Refresh());
            check(P + "a refusal in the snapshot: a message with the reason and no plan asked", a.Calls.Count == 0 && Has(d.Messages, "This model is read-only.") && d.Statuses.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "nothing to write and no dialog needed: \"Markers up to date (140).\", no dialog, no apply",
                Has(d.Statuses, "Markers up to date (140).") && !d.Calls.Contains("plan") && !a.Calls.Contains("apply"));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Clashes = 0; v.ExistingMarkers = 0; v.Unchanged = 0; }));
            Run(Make(a, d).Refresh());
            check(P + "no clashes and no markers: \"No clashes to put in a schedule.\" and no apply", Has(d.Statuses, "No clashes to put in a schedule.") && !a.Calls.Contains("apply") && !d.Calls.Contains("plan"));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Refusal = "The LiveClash workset is closed. Open it (Manage Worksets) and try again."; v.Update = 3; v.NeedsDialog = true; }));
            Run(Make(a, d).Refresh());
            check(P + "the plan's own refusal: a message, no dialog, no apply", Contains(d.Messages, "workset is closed") && !d.Calls.Contains("plan") && !a.Calls.Contains("apply"));
        }

        // ------------------------------------------------------------------ the plan dialog and what it sends

        private static void Dialogs(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.IsCreate = true; v.Create = 6; v.Clashes = 6; v.ExistingMarkers = 0; v.Unchanged = 0; }));
            d.OnPlan = m => false;
            Run(Make(a, d).Refresh());
            check(P + "a create always shows the plan dialog; cancelled, it applies nothing and says nothing changed",
                d.Calls.SequenceEqual(S("plan", "status")) && !a.Calls.Contains("apply") && Has(d.Statuses, "Cancelled. Nothing was changed."));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 4; v.NeedsDialog = true; }));
            Run(Make(a, d).Refresh());
            check(P + "accepted: apply once and the status is the summary then one line per set",
                a.Calls.Count(x => x == "apply") == 1
                && d.Statuses.SequenceEqual(S("Clash markers: 5 clashes. 5 up to date (5 created). MEP vs STR: 5 in your panel, 5 markers up to date.")));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.RetentionCount = 5; v.NeedsDialog = true; }));
            Run(Make(a, d).Refresh());
            bool unticked = a.LastChoices != null && !a.LastChoices.RemoveRetention;
            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.RetentionCount = 5; v.NeedsDialog = true; }));
            d.OnPlan = m => { m.RemoveRetention = true; return true; };
            Run(Make(a, d).Refresh());
            check(P + "retention is unticked by default and reaches the apply only when ticked in the dialog", unticked && a.LastChoices != null && a.LastChoices.RemoveRetention);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.IsCreate = true; v.OverCap = true; v.WouldCreate = 2350; v.Create = 2000; v.NeedsDialog = true; }));
            Run(Make(a, d).Refresh());
            bool capSent = a.LastChoices != null && a.LastChoices.CreateCapOnly && d.LastPlan != null && d.LastPlan.PrimaryText == "Create 2,000 now";
            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.IsCreate = true; v.OverCap = true; v.WouldCreate = 2350; v.Create = 2000; v.NeedsDialog = true; }));
            d.OnPlan = m => false;
            Run(Make(a, d).Refresh());
            check(P + "over the cap: accepting sends the cap only, cancelling calls nothing", capSent && !a.Calls.Contains("apply"));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.IsCreate = true; v.NonWorkshared = true; v.Create = 3; }));
            d.OnConfirm = m => false;
            Run(Make(a, d).Refresh());
            check(P + "a non-workshared create asks its confirmation first; cancelled, no plan dialog and no apply",
                d.Calls.SequenceEqual(S("confirm:CreateNonWorkshared", "status")) && !a.Calls.Contains("apply"));
            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.IsCreate = true; v.NonWorkshared = true; v.Create = 3; }));
            Run(Make(a, d).Refresh());
            check(P + "...accepted, the plan dialog follows and the apply carries the confirmation",
                d.Calls.SequenceEqual(S("confirm:CreateNonWorkshared", "plan", "status")) && a.LastChoices != null && a.LastChoices.ConfirmedNonWorkshared);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.NonWorkshared = true; v.Update = 1; v.NeedsDialog = true; }));
            Run(Make(a, d).Refresh());
            check(P + "a refresh (not a create) in a model that is not workshared asks no confirmation", !d.Calls.Any(x => x.StartsWith("confirm", StringComparison.Ordinal)) && a.Calls.Contains("apply"));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.NeedsDialog = true; v.PassedOver.Add(new MarkerSummary.Reason { Text = "held by kdoe", Count = 9 }); }));
            d.OnPlan = m => false;
            Run(Make(a, d).Refresh());
            check(P + "only passed-over markers: the dialog is shown, cannot apply, and closing it applies nothing",
                d.Calls.Contains("plan") && d.LastPlan != null && !d.LastPlan.CanApply && !a.Calls.Contains("apply"));
        }

        // ------------------------------------------------------------------ outcomes and failures

        private static void Outcomes(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = false, Message = "Duct 123: Revit refused the change." });
            Run(Make(a, d).Refresh());
            check(P + "a failed apply: the message holds the reason and \"Nothing was changed.\", and no status", Has(d.Messages, "Duct 123: Revit refused the change. Nothing was changed.") && d.Statuses.Count == 0);
            a.OnApply = c => Tk(new RefreshOutcome { Ok = false, Message = "Revit rolled it back. Nothing was changed." });
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "...not added twice when the text already says it", Has(d.Messages, "Revit rolled it back. Nothing was changed."));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = false });
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "...and a failure with no text still says so", Has(d.Messages, "The refresh failed. Nothing was changed."));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => { throw new InvalidOperationException("no document"); };
            var c1 = Make(a, d);
            Run(c1.Refresh());
            check(P + "a plan that throws is a message, not an exception, and the controller is free again",
                Contains(d.Messages, "no document") && Contains(d.Messages, "could not finish") && !c1.Busy);
            var f = new TaskCompletionSource<RefreshPlanView>();
            f.SetException(new InvalidOperationException("faulted plan"));
            a.OnPlan = () => f.Task;
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "...so is a plan that faults", Contains(d.Messages, "faulted plan"));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => { throw new InvalidOperationException("apply broke"); };
            var c2 = Make(a, d);
            Run(c2.Refresh());
            bool first = Contains(d.Messages, "apply broke") && !c2.Busy;
            a.OnApply = c => Tk(Good());
            d.Messages.Clear();
            Run(c2.Refresh());
            check(P + "an apply that throws is a message; the next refresh works", first && d.Messages.Count == 0 && d.Statuses.Count == 1);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(Good(o => o.Counts.Clashes = 6));
            Run(Make(a, d).Refresh());
            bool warned = d.Statuses.Count == 1 && d.Statuses[0].EndsWith("The counts do not add up; please report this.", StringComparison.Ordinal);
            a.OnApply = c => Tk(Good());
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "an unbalanced outcome says so in its status; a balanced one does not", warned && !d.Statuses[0].Contains("do not add up"));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk<RefreshPlanView>(null);
            Run(Make(a, d).Refresh());
            bool nullPlan = Has(d.Messages, "The clash schedule did not answer.");
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk<RefreshOutcome>(null);
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            bool nullOutcome = Has(d.Messages, "The clash schedule did not answer.");
            a = new FakeActions(); d = new FakeDialogs();
            a.OnOpen = () => Tk<ActionResult>(null);
            Run(Make(a, d).Open());
            check(P + "an action that answers with nothing is a message, not a throw", nullPlan && nullOutcome && Has(d.Messages, "The clash schedule did not answer."));
        }

        private static void Busy(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            var hold = new TaskCompletionSource<RefreshPlanView>();
            a.OnPlan = () => hold.Task;
            var c = Make(a, d);
            var first = c.Refresh();
            bool busy = c.Busy;
            var second = c.Refresh();
            Run(second);
            bool refused = Has(d.Messages, "Another clash schedule action is still running.") && a.Calls.Count(x => x == "plan") == 1;
            Run(c.Open());
            bool otherRefused = d.Messages.Count == 2 && !a.Calls.Contains("open");
            hold.SetResult(Plan());
            Run(first);
            bool freed = !c.Busy;
            Run(c.Open());
            check(P + "one flow at a time: a second click is refused with a message and no second call, other actions too", busy && refused && otherRefused);
            check(P + "...and once the first completes the controller is free again", freed && a.Calls.Contains("open"));
        }

        // ------------------------------------------------------------------ removals

        private static void Removals(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot.RefusalReason = "This model is read-only.";
            Run(Make(a, d).RemoveResolved());
            bool refused = Has(d.Messages, "This model is read-only.") && d.Calls.Count == 1;
            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.ResolvedCount = 0;
            Run(Make(a, d).RemoveResolved());
            bool none = Has(d.Messages, "No markers are resolved.") && !d.Calls.Any(x => x.StartsWith("confirm", StringComparison.Ordinal));
            a = new FakeActions(); d = new FakeDialogs();
            d.OnConfirm = m => false;
            Run(Make(a, d).RemoveResolved());
            bool cancelled = !a.Calls.Contains("removeResolved") && Has(d.Statuses, "Cancelled. Nothing was changed.");
            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).RemoveResolved());
            bool done = a.Calls.Count(x => x == "removeResolved") == 1 && Has(d.Statuses, "Removed 8 resolved markers.");
            check(P + "remove resolved: a refusal and zero resolved are messages; cancel calls nothing; accepting calls once", refused && none && cancelled && done);
            check(P + "...the confirmation names the snapshot's count", d.LastConfirm != null && Contains(d.LastConfirm.Lines, "8 resolved markers will be deleted from the model."));

            a = new FakeActions(); d = new FakeDialogs();
            d.OnConfirm = m => true;
            Run(Make(a, d).RemoveResolved());
            check(P + "a dialog that says yes with the box unticked is not obeyed", !a.Calls.Contains("removeResolved") && Has(d.Statuses, "Cancelled. Nothing was changed."));

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).RemoveAll());
            bool all = a.Calls.Count(x => x == "removeAll") == 1 && d.LastConfirm != null && Contains(d.LastConfirm.Lines, "All 140 markers") && Has(d.Statuses, "Removed 140 markers.");
            a = new FakeActions(); d = new FakeDialogs();
            a.OnRemoveAll = () => Tk(ActionResult.Fail("kdoe holds 3 markers. Nothing was changed."));
            Run(Make(a, d).RemoveAll());
            check(P + "remove all: the confirmation names the count, one call, a good result is a status and a failed one a message", all && Has(d.Messages, "kdoe holds 3 markers. Nothing was changed.") && d.Statuses.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).RemoveParameters());
            bool markers = Has(d.Messages, "Remove the markers first.") && !a.Calls.Contains("removeParameters");
            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.MarkerCount = 0; a.Snapshot.ParametersPresent = false;
            Run(Make(a, d).RemoveParameters());
            bool noParams = Has(d.Messages, "There are no LiveClash parameters to remove.") && !a.Calls.Contains("removeParameters");
            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.MarkerCount = 0;
            Run(Make(a, d).RemoveParameters());
            check(P + "remove parameters: refused while a marker is left and when there are none; accepted, one call",
                markers && noParams && a.Calls.Count(x => x == "removeParameters") == 1 && d.LastConfirm.Kind == ConfirmKind.RemoveParameters);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnOpen = () => Tk(ActionResult.Fail("The schedule is missing. Refresh makes it again."));
            a.OnFocus = () => Tk(ActionResult.Fail("No Focus view yet."));
            var c = Make(a, d);
            Run(c.Open()); Run(c.ShowInFocus());
            bool failed = d.Messages.SequenceEqual(S("The schedule is missing. Refresh makes it again.", "No Focus view yet."));
            a = new FakeActions(); d = new FakeDialogs();
            var c3 = Make(a, d);
            Run(c3.Open()); Run(c3.ShowInFocus());
            check(P + "open and focus: a failure is a message, a plain success makes no noise", failed && d.Messages.Count == 0 && d.Statuses.Count == 0 && a.Calls.SequenceEqual(S("open", "focus")));
        }

        // ------------------------------------------------------------------ assign

        private static List<string> Keys(params string[] k) { return k.ToList(); }

        private static void Assigns(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            var c = Make(a, d);
            Run(c.Assign(null)); Run(c.Assign(Keys()));
            check(P + "assign with no rows: a message each time and no call", d.Messages.Count == 2 && Contains(d.Messages, "Select at least one clash.") && a.Calls.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            d.OnAssign = m => null;
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "assign cancelled: no Assign call, and it says nothing changed", !a.Calls.Contains("assign") && Has(d.Statuses, "Cancelled. Nothing was changed."));

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).Assign(Keys("k1", "k2")));
            bool picked = a.LastUser == "asmith" && a.LastKeys.SequenceEqual(S("k1", "k2"));
            a = new FakeActions(); d = new FakeDialogs();
            d.OnAssign = m => { m.SelectUnassign(); return m.Result(); };
            Run(Make(a, d).Assign(Keys("k1")));
            bool unassigned = a.Calls.Contains("assign") && a.LastUser == "";
            a = new FakeActions(); d = new FakeDialogs();
            d.OnAssign = m => { m.OtherName = "newhire"; return m.Result(); };
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "assign: a candidate, unassign (\"\") and a typed name that was not seen all reach the Revit side", picked && unassigned && a.LastUser == "newhire");

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Rows["k1"] = new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0042", HeldBy = "kdoe" };
            a.Snapshot.Rows["k2"] = new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0043" };
            Run(Make(a, d).Assign(Keys("k1", "k2")));
            check(P + "the Assign dialog gets the issue numbers, the names, this user and a pending line for a held marker",
                d.LastAssign != null && d.LastAssign.Heading == "Assign JD-0042 (and 1 more)" && d.LastAssign.PendingLines.SequenceEqual(S("JD-0042: held by kdoe"))
                && d.LastAssign.Visible.Select(v => v.Name).SequenceEqual(S("asmith", "jdoe", "kdoe")));
            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Workshared = false;
            a.Snapshot.Rows["k1"] = new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0042", HeldBy = "kdoe" };
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "...and none in a model that is not workshared", d.LastAssign != null && d.LastAssign.PendingLines.Count == 0);

            string Say(AssignOutcome o, string user)
            {
                var aa = new FakeActions(); var dd = new FakeDialogs();
                aa.OnAssign = () => Tk(o);
                dd.OnAssign = m => user == "" ? Unassign(m) : Pick(m, user);
                Run(Make(aa, dd).Assign(Keys("k1")));
                return dd.Statuses.Count > 0 ? dd.Statuses[0] : (dd.Messages.Count > 0 ? "MSG:" + dd.Messages[0] : null);
            }
            check(P + "assign wording: assigned", Say(new AssignOutcome { Ok = true, Assigned = 3 }, "asmith") == "Assigned 3 clashes to asmith.");
            check(P + "...one clash, singular", Say(new AssignOutcome { Ok = true, Assigned = 1 }, "asmith") == "Assigned 1 clash to asmith.");
            check(P + "...assigned and pending", Say(new AssignOutcome { Ok = true, Assigned = 2, Pending = 1 }, "asmith") == "Assigned 2 clashes to asmith; 1 clash is pending (another user holds its marker).");
            check(P + "...only pending", Say(new AssignOutcome { Ok = true, Pending = 2 }, "asmith") == "2 clashes are pending (another user holds the markers); nothing was written to the model.");
            check(P + "...nothing", Say(new AssignOutcome { Ok = true }, "asmith") == "Nothing was assigned.");
            check(P + "...unassigned", Say(new AssignOutcome { Ok = true, Assigned = 2 }, "") == "Unassigned 2 clashes.");
            check(P + "...a failed assign is a message with its text", Say(new AssignOutcome { Ok = false, Message = "Revit refused." }, "asmith") == "MSG:Revit refused.");
            check(P + "...and with no text a default", Say(new AssignOutcome { Ok = false }, "asmith") == "MSG:The assignment failed. Nothing was changed.");

            a = new FakeActions(); d = new FakeDialogs();
            a.OnCandidates = () => { throw new InvalidOperationException("no names"); };
            var c1 = Make(a, d);
            Run(c1.Assign(Keys("k1")));
            bool fault1 = Contains(d.Messages, "no names") && !a.Calls.Contains("assign") && !c1.Busy;
            a = new FakeActions(); d = new FakeDialogs();
            a.OnAssign = () => { throw new InvalidOperationException("assign broke"); };
            var c2 = Make(a, d);
            Run(c2.Assign(Keys("k1")));
            check(P + "assign failures (candidates, assign) are messages and the controller is free again", fault1 && Contains(d.Messages, "assign broke") && !c2.Busy);

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).DiscardPending("k9"));
            bool discarded = a.LastKey == "k9" && Has(d.Statuses, "Pending discarded.");
            a = new FakeActions(); d = new FakeDialogs();
            a.OnDiscard = () => Tk(ActionResult.Fail("It was already applied."));
            Run(Make(a, d).DiscardPending("k9"));
            check(P + "discard pending: one call with the key; a failure is a message", discarded && Has(d.Messages, "It was already applied."));
        }

        private static AssignResult Pick(AssignModel m, string name) { m.SelectCandidate(name); return m.Result(); }
        private static AssignResult Unassign(AssignModel m) { m.SelectUnassign(); return m.Result(); }

        // ------------------------------------------------------------------ construction and cleanliness

        // Branch coverage, added at green: the paths the coverage report showed unrun (a null snapshot, blank texts, a
        // null assign answer, and the cancel and refusal paths of remove all and remove parameters).
        private static void Edges(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot = null;
            Run(Make(a, d).Refresh());
            check(P + "a missing snapshot is read as an empty one (no refusal)", Has(d.Statuses, "Markers up to date (140).") && d.Messages.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnOpen = () => Tk(ActionResult.Fail(null));
            Run(Make(a, d).Open());
            check(P + "a failed action with no text still says nothing was changed", Has(d.Messages, "The action failed. Nothing was changed."));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Message = " Done by the Revit side. " });
            Run(Make(a, d).Refresh());
            bool text = Has(d.Statuses, "Done by the Revit side.");
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true });
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "an outcome with no counts shows its own text, or \"Done.\"", text && Has(d.Statuses, "Done."));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Counts = new MarkerSummary.Counts { Clashes = 2, Unchanged = 2 }, Sets = null });
            Run(Make(a, d).Refresh());
            check(P + "an outcome with no set lines shows the summary alone", d.Statuses.SequenceEqual(S("Clash markers: 2 clashes. 2 up to date (2 unchanged).")));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnAssign = () => Tk<AssignOutcome>(null);
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "an assign that answers with nothing is a message", Has(d.Messages, "The clash schedule did not answer."));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.MarkerCount = 0;
            Run(Make(a, d).RemoveAll());
            bool none = Has(d.Messages, "There are no markers to remove.") && !d.Calls.Any(x => x.StartsWith("confirm", StringComparison.Ordinal));
            a = new FakeActions(); d = new FakeDialogs();
            d.OnConfirm = m => false;
            Run(Make(a, d).RemoveAll());
            check(P + "remove all: no markers is a message; a cancelled confirmation calls nothing", none && !a.Calls.Contains("removeAll") && Has(d.Statuses, "Cancelled. Nothing was changed."));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.MarkerCount = 0;
            a.Snapshot.RefusalReason = "This model is read-only.";
            Run(Make(a, d).RemoveParameters());
            bool refused = Has(d.Messages, "This model is read-only.") && d.Calls.Count == 1;
            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.MarkerCount = 0;
            d.OnConfirm = m => false;
            Run(Make(a, d).RemoveParameters());
            check(P + "remove parameters: a refusal is a message; a cancelled confirmation calls nothing", refused && !a.Calls.Contains("removeParameters") && Has(d.Statuses, "Cancelled. Nothing was changed."));
        }

        // B6: triage on rows that have a marker goes through the Revit side.
        private static void Triage(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            Run(Make(a, d).SetStatus(Keys("k1", "k2"), ClashStatusKind.Ignored, "agreed sleeve"));
            check(P + "set status: one call with the keys, the kind and the note", a.Calls.SequenceEqual(S("setStatus")) && a.LastKeys != null && a.LastKeys.SequenceEqual(S("k1", "k2")) && a.LastKind == ClashStatusKind.Ignored && a.LastNote == "agreed sleeve");
            check(P + "...a good answer with text is the status line", d.Statuses.SequenceEqual(S("Set 2 clashes to Ignored.")) && d.Messages.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnSetStatus = () => Tk(ActionResult.Success(null));
            Run(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Pending, null));
            check(P + "...a good answer with no text makes no noise", d.Statuses.Count == 0 && d.Messages.Count == 0 && a.Calls.Contains("setStatus"));

            a = new FakeActions(); d = new FakeDialogs();
            var c0 = Make(a, d);
            Run(c0.SetStatus(null, ClashStatusKind.Pending, null)); Run(c0.SetStatus(Keys(), ClashStatusKind.Pending, null));
            check(P + "set status with no rows: \"Select an issue first.\" each time and no call", d.Messages.SequenceEqual(S("Select an issue first.", "Select an issue first.")) && a.Calls.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.RefusalReason = "This model is read-only.";
            Run(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Pending, null));
            check(P + "set status in a model that refuses writes: the reason is the message and nothing is called", d.Messages.SequenceEqual(S("This model is read-only. Your decision was NOT recorded.")) && a.Calls.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnSetStatus = () => Tk(ActionResult.Fail("kdoe holds the marker. Nothing was changed."));
            Run(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Pending, null));
            bool failed = d.Messages.SequenceEqual(S("kdoe holds the marker. Nothing was changed. Your decision was NOT recorded.")) && d.Statuses.Count == 0;
            a.OnSetStatus = () => Tk<ActionResult>(null);
            d = new FakeDialogs();
            Run(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Pending, null));
            bool none = Has(d.Messages, "The clash schedule did not answer. Your decision was NOT recorded.");
            a.OnSetStatus = () => { throw new InvalidOperationException("marker write broke"); };
            d = new FakeDialogs();
            var c1 = Make(a, d);
            Run(c1.SetStatus(Keys("k1"), ClashStatusKind.Pending, null));
            check(P + "set status: a failed answer, an empty answer and a throw are messages that say the decision was NOT recorded, and the controller is free after", failed && none && Contains(d.Messages, "marker write broke") && Contains(d.Messages, "Your decision was NOT recorded.") && !c1.Busy);

            a = new FakeActions(); d = new FakeDialogs();
            var hold = new TaskCompletionSource<ActionResult>();
            a.OnSetStatus = () => hold.Task;
            var c2 = Make(a, d);
            var first = c2.SetStatus(Keys("k1"), ClashStatusKind.Pending, null);
            Run(c2.SetNote(Keys("k1"), "x"));
            bool refused = Has(d.Messages, "Another clash schedule action is still running; try again. Your note was NOT recorded.") && !a.Calls.Contains("setNote");
            hold.SetResult(ActionResult.Success(null));
            Run(first);
            check(P + "set status and set note: one flow at a time", refused && !c2.Busy);

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).SetNote(Keys("k1", "k2"), "agreed sleeve"));
            bool note = a.Calls.SequenceEqual(S("setNote")) && a.LastKeys != null && a.LastKeys.SequenceEqual(S("k1", "k2")) && a.LastNote == "agreed sleeve" && d.Statuses.SequenceEqual(S("Note saved on 2 clashes."));
            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).SetNote(Keys("k1"), ""));
            check(P + "set note: one call with the keys and the note; an empty note (clearing it) is allowed", note && a.Calls.Contains("setNote") && a.LastNote == "");

            a = new FakeActions(); d = new FakeDialogs();
            var c3 = Make(a, d);
            Run(c3.SetNote(null, "x"));
            bool noRows = Has(d.Messages, "Select an issue first.") && a.Calls.Count == 0;
            a.Snapshot.RefusalReason = "This model is read-only.";
            Run(c3.SetNote(Keys("k1"), "x"));
            bool refusal = Has(d.Messages, "This model is read-only. Your note was NOT recorded.") && a.Calls.Count == 0;
            a.Snapshot.RefusalReason = null;
            a.OnSetNote = () => Tk(ActionResult.Fail("Held by kdoe."));
            Run(c3.SetNote(Keys("k1"), "x"));
            bool fail = Has(d.Messages, "Held by kdoe. Your note was NOT recorded.");
            a.OnSetNote = () => Tk<ActionResult>(null);
            Run(c3.SetNote(Keys("k1"), "x"));
            check(P + "set note: no rows, a refusal, a failed answer and an empty answer behave like set status", noRows && refusal && fail && Has(d.Messages, "The clash schedule did not answer. Your note was NOT recorded.") && !c3.Busy);
        }

        // Delta review D-L3: a snapshot that is not Ready because its read failed says why, not "still loading".
        private static void LoadFailure(Action<string, bool?> check)
        {
            const string why = "The clash markers could not be read for this model. boom";
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot.Ready = false; a.Snapshot.LoadFailure = why;
            var c = Make(a, d);
            bool st = c.RefuseTriage(false);
            bool nt = c.RefuseTriage(true);
            Run(c.Assign(Keys("k1")));
            bool s = Answer(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Ignored, "x"));
            check(P + "not Ready because the read failed: the failure is shown, not \"still loading\", for triage, a note, assign and the controller's own flows; nothing called",
                st && nt && !s && a.Calls.Count == 0
                && d.Messages.SequenceEqual(S(why + " Your decision was NOT recorded.", why + " Your note was NOT recorded.", why + " Your assignment was NOT recorded.", why + " Your decision was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false; a.Snapshot.LoadFailure = "   ";
            bool blank = Make(a, d).RefuseTriage(false);
            a.Snapshot.LoadFailure = null;
            bool none = Make(a, d).RefuseTriage(false);
            check(P + "...a blank or missing failure still says it is loading", blank && none && d.Messages.SequenceEqual(S(StillLoading + " Your decision was NOT recorded.", StillLoading + " Your decision was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.LoadFailure = why;   // Ready: a stale text is ignored
            check(P + "...a Ready snapshot ignores a stale failure text", !Make(a, d).RefuseTriage(false) && d.Messages.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false; a.Snapshot.LoadFailure = why; a.Snapshot.RefusalReason = "This model is read-only.";
            Make(a, d).RefuseTriage(false);
            check(P + "...and the failure wins over a refusal reason", d.Messages.SequenceEqual(S(why + " Your decision was NOT recorded.")));
        }

        // Delta review D-I5: Assign is gated like triage (not Ready, or refused: nothing is asked and nothing is shown).
        private static void AssignGate(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot.Ready = false;
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "assign while the snapshot is not Ready: the loading message and \"Your assignment was NOT recorded.\", no names asked, no dialog",
                d.Messages.SequenceEqual(S(StillLoading + " Your assignment was NOT recorded.")) && a.Calls.Count == 0 && !d.Calls.Contains("assign"));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.RefusalReason = "This model is read-only.";
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "assign in a Ready but refused snapshot: the reason and \"Your assignment was NOT recorded.\", nothing asked or shown",
                d.Messages.SequenceEqual(S("This model is read-only. Your assignment was NOT recorded.")) && a.Calls.Count == 0 && !d.Calls.Contains("assign"));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false; a.Snapshot.RefusalReason = "This model is read-only.";
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "...not Ready and refused: loading first", d.Messages.SequenceEqual(S(StillLoading + " Your assignment was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            Run(Make(a, d).Assign(Keys("k1")));
            check(P + "assign in a Ready, accepting snapshot goes on as before (names asked, dialog shown, assign called)", a.Calls.SequenceEqual(S("candidates", "assign")) && d.Calls.Contains("assign") && d.Messages.Count == 0);
        }

        // Delta review D-H1: a Ready but REFUSED snapshot (read-only, workset closed, set-up held) refuses all triage too.
        private static void Refused(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot.RefusalReason = "This model is read-only.";
            var c = Make(a, d);
            bool st = c.RefuseTriage(false);
            bool nt = c.RefuseTriage(true);
            check(P + "a Ready but refused snapshot refuses all triage: the reason, then \"Your decision was NOT recorded.\" (or the note wording)",
                st && nt && a.Calls.Count == 0 && d.Messages.SequenceEqual(S("This model is read-only. Your decision was NOT recorded.", "This model is read-only. Your note was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false; a.Snapshot.RefusalReason = "This model is read-only.";
            bool both = Make(a, d).RefuseTriage(false);
            check(P + "...a snapshot that is not Ready and refused says it is loading first", both && d.Messages.SequenceEqual(S(StillLoading + " Your decision was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            bool open = Make(a, d).RefuseTriage(false);
            a.Snapshot.RefusalReason = "   ";
            bool blank = Make(a, d).RefuseTriage(true);
            check(P + "a Ready snapshot with no refusal (or a blank one) lets triage through, saying nothing", !open && !blank && d.Messages.Count == 0);
        }

        // Review H1 (second half): no triage while the snapshot is not Ready for the active document.
        private const string StillLoading = "The clash schedule is still loading for this model; try again in a moment.";

        private static void Loading(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.Snapshot.Ready = false;
            bool s = Answer(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Ignored, "x"));
            check(P + "set status while the snapshot is not Ready: refused, nothing called, and the message says the decision was NOT recorded",
                !s && a.Calls.Count == 0 && d.Messages.SequenceEqual(S(StillLoading + " Your decision was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false;
            bool n = Answer(Make(a, d).SetNote(Keys("k1"), "x"));
            check(P + "set note while the snapshot is not Ready: the same, \"Your note was NOT recorded.\"",
                !n && a.Calls.Count == 0 && d.Messages.SequenceEqual(S(StillLoading + " Your note was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            bool ok = Answer(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Ignored, "x")) && Answer(Make(a, d).SetNote(Keys("k1"), "x"));
            check(P + "a Ready snapshot goes through as before", ok && a.Calls.SequenceEqual(S("setStatus", "setNote")) && d.Messages.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false; a.Snapshot.RefusalReason = "This model is read-only.";
            Answer(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Ignored, null));
            check(P + "...and loading is said before a refusal reason (a snapshot that is not Ready may be another model's)", d.Messages.SequenceEqual(S(StillLoading + " Your decision was NOT recorded.")));

            a = new FakeActions(); d = new FakeDialogs();
            a.Snapshot.Ready = false;
            var c = Make(a, d);
            bool st = c.RefuseTriage(false);
            bool nt = c.RefuseTriage(true);
            a.Snapshot = null;
            bool none = c.RefuseTriage(false);
            check(P + "the panel's gate refuses when the snapshot is not Ready or missing, with the status or the note wording",
                st && nt && none && d.Messages.SequenceEqual(S(StillLoading + " Your decision was NOT recorded.", StillLoading + " Your note was NOT recorded.", StillLoading + " Your decision was NOT recorded.")) && a.Calls.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            check(P + "...and lets everything through, saying nothing, when Ready", !Make(a, d).RefuseTriage(false) && !Make(a, d).RefuseTriage(true) && d.Messages.Count == 0);
        }

        // Review L4: the panel needs to know whether the marker path took a triage, so it can keep the decision on the ledger path when it did not.
        [ThreadStatic] private static int _unfinished;
        [ThreadStatic] private static int _faulted;

        // For SyncContextChecks (DR3-L5): a worker that is abandoned after a timed-out Join must not reach the counts the next checks read.
        internal static int UnfinishedCountForTest { get { return _unfinished; } }
        internal static void CountUnfinishedForTest() { _unfinished++; }
        internal static void ResetCountsForTest() { _unfinished = 0; _faulted = 0; }

        /// <summary>The flow's answer: true or false, or null when it never finished within <see cref="WaitMs"/> or faulted (both counted, so a line can name them).</summary>
        private static bool? Answer3(Task<bool> t)
        {
            try { t.Wait(WaitMs); } catch (AggregateException) { }   // a faulted flow is read below, not thrown here
            if (!t.IsCompleted) { _unfinished++; return null; }
            if (t.IsFaulted || t.IsCanceled) { _faulted++; return null; }
            return t.Result;
        }

        /// <summary>For the lines that expect true: a flow that did not finish or faulted is not true.</summary>
        private static bool Answer(Task<bool> t) { return Answer3(t) == true; }

        private static void TriageAnswer(Action<string, bool?> check)
        {
            _unfinished = 0; _faulted = 0;
            var blank = new FakeActions { OnSetStatus = () => Tk(ActionResult.Fail(null)) };
            var db = new FakeDialogs();
            Run(Make(blank, db).SetStatus(Keys("k1"), ClashStatusKind.Pending, null));
            check(P + "set status: a failed answer with no text still says what happened, and that the decision was NOT recorded",
                db.Messages.SequenceEqual(S("The action failed. Nothing was changed. Your decision was NOT recorded.")));

            var a = new FakeActions(); var d = new FakeDialogs();
            bool s1 = Answer(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Ignored, "x"));
            bool n1 = Answer(Make(a, d).SetNote(Keys("k1"), "x"));
            a.OnSetStatus = () => Tk(ActionResult.Success(null));
            a.OnSetNote = () => Tk(ActionResult.Success(null));
            bool s2 = Answer(Make(a, d).SetStatus(Keys("k1"), ClashStatusKind.Ignored, null));
            bool n2 = Answer(Make(a, d).SetNote(Keys("k1"), ""));
            check(P + "a good answer, with or without text, returns true for both set status and set note", s1 && n1 && s2 && n2);

            var none = new FakeActions(); var dn = new FakeDialogs();
            bool noRows = Answer3(Make(none, dn).SetStatus(null, ClashStatusKind.Pending, null)) == false && Answer3(Make(none, dn).SetNote(Keys(), "x")) == false;
            var ro = new FakeActions { Snapshot = new ScheduleSnapshot { Ready = true, MarkerCount = 3, RefusalReason = "This model is read-only." } };
            bool refused = Answer3(Make(ro, new FakeDialogs()).SetStatus(Keys("k1"), ClashStatusKind.Pending, null)) == false && Answer3(Make(ro, new FakeDialogs()).SetNote(Keys("k1"), "x")) == false;
            var bad = new FakeActions { OnSetStatus = () => Tk(ActionResult.Fail("No.")), OnSetNote = () => Tk(ActionResult.Fail("No.")) };
            bool failed = Answer3(Make(bad, new FakeDialogs()).SetStatus(Keys("k1"), ClashStatusKind.Pending, null)) == false && Answer3(Make(bad, new FakeDialogs()).SetNote(Keys("k1"), "x")) == false;
            var empty = new FakeActions { OnSetStatus = () => Tk<ActionResult>(null), OnSetNote = () => Tk<ActionResult>(null) };
            bool nothing = Answer3(Make(empty, new FakeDialogs()).SetStatus(Keys("k1"), ClashStatusKind.Pending, null)) == false && Answer3(Make(empty, new FakeDialogs()).SetNote(Keys("k1"), "x")) == false;
            var boom = new FakeActions { OnSetStatus = () => { throw new InvalidOperationException("boom"); }, OnSetNote = () => { throw new InvalidOperationException("boom"); } };
            bool threw = Answer3(Make(boom, new FakeDialogs()).SetStatus(Keys("k1"), ClashStatusKind.Pending, null)) == false && Answer3(Make(boom, new FakeDialogs()).SetNote(Keys("k1"), "x")) == false;

            var busy = new FakeActions();
            var hold = new TaskCompletionSource<ActionResult>();
            busy.OnSetStatus = () => hold.Task;
            var c = Make(busy, new FakeDialogs());
            var first = c.SetStatus(Keys("k1"), ClashStatusKind.Pending, null);
            bool whileBusy = Answer3(c.SetNote(Keys("k1"), "x")) == false;
            hold.SetResult(ActionResult.Success(null));
            Run(first);
            check(P + "no rows, a refusal, a failed answer, an empty answer, a throw and \"another action is running\" all return false (the decision then stays on the ledger path)",
                noRows && refused && failed && nothing && threw && whileBusy && first.IsCompleted && !first.IsFaulted && first.Result);
            check(P + "every flow in that case finished and none faulted (" + _unfinished + " unfinished, " + _faulted + " faulted)", _unfinished == 0 && _faulted == 0);
        }

        // Review fix B (M2, M3): what a Refresh says about merges, removals, decisions the model replaced and pendings not applied.
        private static MarkerSummary.Counts Consistent()
        {
            // 12 clashes: 8 up to date (6 unchanged + 1 updated + 1 created), 2 removed, 1 passed over, 1 not marked.
            var c = new MarkerSummary.Counts { Clashes = 12, Unchanged = 6, Updated = 1, Created = 1, Removed = 2, Merged = 1, LostDecisions = 1 };
            c.PassedOver.Add(new MarkerSummary.Reason { Text = "held by kdoe", Count = 1 });
            c.NotMarked.Add(new MarkerSummary.Reason { Text = "no position", Count = 1 });
            return c;
        }

        private static void Review(Action<string, bool?> check)
        {
            var a = new FakeActions(); var d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Counts = Consistent() });
            Run(Make(a, d).Refresh());
            var status = d.Statuses.Count == 1 ? d.Statuses[0] : "";
            check(P + "a refresh with merges, removals and unmarked rows reports them: \"2 removed\", \"1 merge decision not kept (see the Record)\", \"1 not marked (no position)\"",
                status.Contains("2 removed") && status.Contains("1 merge decision not kept (see the Record)") && status.Contains("1 not marked (no position)"));
            check(P + "...and does not say the counts do not add up", status.Length > 0 && !status.Contains("do not add up") && d.Messages.Count == 0);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome
            {
                Ok = true, Counts = Consistent(),
                Notes = { "2 of your decisions differ from the model; the model's value was kept.", "  ", null, "Your pending triage on JD-0042 was not applied: kdoe changed it first (2)." }
            });
            Run(Make(a, d).Refresh());
            check(P + "the notes are shown verbatim, in order, in ONE message after the status line (blank ones dropped)",
                d.Calls.Where(x => x == "status" || x == "message").SequenceEqual(S("status", "message")) && d.Messages.Count == 1
                && d.Messages[0].Split('\n').SequenceEqual(S("2 of your decisions differ from the model; the model's value was kept.", "Your pending triage on JD-0042 was not applied: kdoe changed it first (2).")));

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Counts = Consistent(), Notes = { "Only one note." } });
            Run(Make(a, d).Refresh());
            check(P + "one note is a one-line message", d.Messages.SequenceEqual(S("Only one note.")) && d.Statuses.Count == 1);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Counts = Consistent() });
            Run(Make(a, d).Refresh());
            bool quiet = d.Messages.Count == 0 && d.Statuses.Count == 1;
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Counts = Consistent(), Notes = null });
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            bool nulls = d.Messages.Count == 0 && d.Statuses.Count == 1;
            a.OnApply = c => Tk(new RefreshOutcome { Ok = true, Counts = Consistent(), Notes = { " ", null } });
            d = new FakeDialogs();
            Run(Make(a, d).Refresh());
            check(P + "no notes (an empty list, no list, or only blanks): no message at all", quiet && nulls && d.Messages.Count == 0 && d.Statuses.Count == 1);

            a = new FakeActions(); d = new FakeDialogs();
            a.OnPlan = () => Tk(Plan(v => { v.Update = 1; v.NeedsDialog = true; }));
            a.OnApply = c => Tk(new RefreshOutcome { Ok = false, Message = "Revit refused.", Notes = { "x" } });
            Run(Make(a, d).Refresh());
            check(P + "a failed refresh shows its failure and none of the notes", d.Messages.SequenceEqual(S("Revit refused. Nothing was changed.")) && d.Statuses.Count == 0);
        }

        private static void Misc(Action<string, bool?> check)
        {
            bool nullActions = false, nullDialogs = false;
            try { Make(null, new FakeDialogs()); } catch (ArgumentNullException) { nullActions = true; } catch (Exception) { }
            try { Make(new FakeActions(), null); } catch (ArgumentNullException) { nullDialogs = true; } catch (Exception) { }
            check(P + "constructing with no actions or no dialogs throws ArgumentNullException", nullActions && nullDialogs);

            var a = new FakeActions(); var d = new FakeDialogs();
            var c = Make(a, d);
            d.OnPlan = m => false;
            a.OnPlan = () => Tk(Plan(v => { v.IsCreate = true; v.Create = 1; }));
            Run(c.Refresh());
            bool cancel = !c.Busy;
            a.Snapshot.RefusalReason = "No.";
            Run(c.Refresh()); Run(c.RemoveAll()); Run(c.Assign(Keys("k1")));
            bool refusal = !c.Busy;
            a.Snapshot.RefusalReason = null;
            a.OnOpen = () => { throw new InvalidOperationException("boom"); };
            Run(c.Open());
            bool fault = !c.Busy;
            Run(c.ShowInFocus());
            check(P + "every path leaves the controller free: cancel, refusal, fault and success", cancel && refusal && fault && !c.Busy);
        }
    }
}
