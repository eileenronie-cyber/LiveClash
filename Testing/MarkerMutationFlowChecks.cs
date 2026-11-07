using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 10: the controller's flows at their edges (an answer that is missing, a negative count, a refusal, a cancel,
    /// an exception), against a fake Revit side and fake dialogs of this file's own. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationFlowChecks
    {
        private const string P = "markers mutation flow: ";
        private const string NoAnswer = "The clash schedule did not answer.";
        private static readonly string[] One = { "k1" };
        private static readonly string[] Two = { "k1", "k2" };

        public static void RunAll(Action<string, bool?> check)
        {
            Answers(check);
            Counts(check);
            Dialogs(check);
            Assigning(check);
            Failures(check);
        }

        // ------------------------------------------------------------------ fakes

        private sealed class Actions : IClashScheduleActions
        {
            public ScheduleSnapshot Snapshot { get; set; } = new ScheduleSnapshot { Me = "jdoe", Workshared = true, Ready = true, MarkerCount = 140, ResolvedCount = 8, HasSchedule = true, ParametersPresent = true };
            public event Action Changed { add { } remove { } }
            public readonly List<string> Calls = new List<string>();
            public RefreshChoices LastChoices;
            public string LastUser = "(none)";

            public Func<Task<ActionResult>> OnOpen = () => Task.FromResult(ActionResult.Success(null));
            public Func<Task<RefreshPlanView>> OnPlan = () => Task.FromResult(new RefreshPlanView { Clashes = 10, ExistingMarkers = 140, Unchanged = 140 });
            public Func<Task<RefreshOutcome>> OnApply = () => Task.FromResult(Good());
            public Func<Task<AssignOutcome>> OnAssign = () => Task.FromResult(new AssignOutcome { Ok = true, Assigned = 2 });
            public Func<Task<ActionResult>> OnSetStatus = () => Task.FromResult(ActionResult.Success(null));

            public Task<RefreshPlanView> PlanRefresh() { Calls.Add("plan"); return OnPlan(); }
            public Task<RefreshOutcome> ApplyRefresh(RefreshChoices choices) { Calls.Add("apply"); LastChoices = choices; return OnApply(); }
            public Task<ActionResult> OpenSchedule() { Calls.Add("open"); return OnOpen(); }
            public Task<ActionResult> ShowMarkersInFocus() { Calls.Add("focus"); return OnOpen(); }
            public Task<ActionResult> RemoveResolved() { Calls.Add("removeResolved"); return OnOpen(); }
            public Task<ActionResult> RemoveAllMarkers() { Calls.Add("removeAll"); return OnOpen(); }
            public Task<ActionResult> RemoveParameters() { Calls.Add("removeParameters"); return OnOpen(); }
            public Task<List<AssignCandidate>> AssignCandidates(IReadOnlyList<string> rowKeys) { Calls.Add("candidates"); return Task.FromResult(new List<AssignCandidate> { new AssignCandidate { Name = "asmith", Reason = "seen" } }); }
            public Task<AssignOutcome> Assign(IReadOnlyList<string> rowKeys, string user) { Calls.Add("assign"); LastUser = user; return OnAssign(); }
            public Task<ActionResult> DiscardPending(string rowKey) { Calls.Add("discard"); return OnOpen(); }
            public Task<ActionResult> SetStatus(IReadOnlyList<string> rowKeys, ClashStatusKind kind, string note) { Calls.Add("setStatus"); return OnSetStatus(); }
            public Task<ActionResult> SetNote(IReadOnlyList<string> rowKeys, string note) { Calls.Add("setNote"); return OnSetStatus(); }
        }

        private sealed class Screen : IScheduleDialogs
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Statuses = new List<string>();
            public readonly List<string> Messages = new List<string>();
            public Func<AssignModel, AssignResult> OnAssign = m => { m.SelectCandidate("asmith"); return m.Result(); };
            public AssignModel LastAssign;

            public bool ShowPlan(RefreshPlanModel model) { Calls.Add("plan"); return true; }
            public bool Confirm(ConfirmModel model) { Calls.Add("confirm"); model.Ticked = true; return true; }
            public AssignResult ShowAssign(AssignModel model) { Calls.Add("assign"); LastAssign = model; return OnAssign(model); }
            public void Status(string text) { Calls.Add("status"); Statuses.Add(text); }
            public void Message(string text) { Calls.Add("message"); Messages.Add(text); }
        }

        private static RefreshOutcome Good()
        {
            var o = new RefreshOutcome { Ok = true, Counts = new MarkerSummary.Counts { Clashes = 5, Created = 5 } };
            o.Sets.Add(new MarkerSummary.SetCounts { Name = "MEP vs STR", InPanel = 5, UpToDate = 5 });
            return o;
        }

        private static void Run(Task t) { t.Wait(5000); }
        private static string[] Texts(params string[] items) { return items; }

        // ------------------------------------------------------------------ the Revit side gave no answer

        private static void Answers(Action<string, bool?> check)
        {
            var a = new Actions(); var d = new Screen();
            a.OnOpen = () => Task.FromResult<ActionResult>(null);
            Run(new ScheduleController(a, d).Open());
            check(P + "an action that answers nothing says so once and does not fail", d.Messages.SequenceEqual(Texts(NoAnswer)) && d.Statuses.Count == 0);

            a = new Actions(); d = new Screen();
            a.OnPlan = () => Task.FromResult<RefreshPlanView>(null);
            Run(new ScheduleController(a, d).Refresh());
            check(P + "a Refresh whose plan is missing says so once and goes no further", d.Messages.SequenceEqual(Texts(NoAnswer)) && !a.Calls.Contains("apply") && !d.Calls.Contains("plan"));

            a = new Actions(); d = new Screen();
            a.OnApply = () => Task.FromResult<RefreshOutcome>(null);
            a.OnPlan = () => Task.FromResult(new RefreshPlanView { Clashes = 10, ExistingMarkers = 140, Update = 2 });
            Run(new ScheduleController(a, d).Refresh());
            check(P + "...and one whose applied outcome is missing says so once and shows no summary", d.Messages.SequenceEqual(Texts(NoAnswer)) && d.Statuses.Count == 0);
        }

        // ------------------------------------------------------------------ what counts as something to write

        private static void Counts(Action<string, bool?> check)
        {
            Func<Action<RefreshPlanView>, Actions> refresh = shape =>
            {
                var a = new Actions(); var d = new Screen();
                a.OnPlan = () => { var v = new RefreshPlanView { Clashes = 0, ExistingMarkers = 0 }; shape(v); return Task.FromResult(v); };
                Run(new ScheduleController(a, d).Refresh());
                a.Calls.Add("status:" + string.Join("|", d.Statuses));
                a.Calls.Add("dialog:" + d.Calls.Count(c => c == "plan"));
                return a;
            };

            var negative = refresh(v => v.Update = -3);
            check(P + "a negative count is not a write: nothing to put in a schedule", negative.Calls.Contains("status:No clashes to put in a schedule.") && !negative.Calls.Contains("apply"));

            check(P + "two kinds of write whose counts would cancel out if one were taken away from the other still write (create and update, update and move, move and resolve, resolve and merge, merge and pending)",
                refresh(v => { v.Create = 1; v.Update = 1; }).Calls.Contains("apply") && refresh(v => { v.Update = 1; v.Move = 1; }).Calls.Contains("apply") && refresh(v => { v.Move = 1; v.Resolve = 1; }).Calls.Contains("apply")
                && refresh(v => { v.Resolve = 1; v.Merge = 1; }).Calls.Contains("apply") && refresh(v => { v.Merge = 1; v.PendingApplied = 1; }).Calls.Contains("apply"));

            var held = refresh(v => { v.ExistingMarkers = 5; v.Unchanged = 5; });
            check(P + "no clashes but markers in the model: they are up to date, not 'no clashes'", held.Calls.Contains("status:Markers up to date (5).") && !held.Calls.Any(c => c.StartsWith("status:No clashes", StringComparison.Ordinal)));

            var empty = refresh(v => { });
            check(P + "no clashes and no markers: one status and the flow ends", empty.Calls.Contains("status:No clashes to put in a schedule.") && !empty.Calls.Contains("apply"));

            var retention = refresh(v => { v.Clashes = 5; v.ExistingMarkers = 5; v.Unchanged = 5; v.RetentionCount = 1; });
            check(P + "markers offered for removal by retention open the plan dialog even when nothing else is to be written", retention.Calls.Contains("dialog:1"));

            var a2 = new Actions(); var d2 = new Screen();
            a2.OnPlan = () => Task.FromResult(new RefreshPlanView { Clashes = 10, ExistingMarkers = 140, Update = 2 });
            Run(new ScheduleController(a2, d2).Refresh());
            check(P + "an ordinary refresh does not claim the non-workshared confirmation", a2.LastChoices != null && !a2.LastChoices.ConfirmedNonWorkshared);

            var a3 = new Actions(); var d3 = new Screen();
            var outcome = Good();
            outcome.Sets.Add(new MarkerSummary.SetCounts { Name = null });
            a3.OnApply = () => Task.FromResult(outcome);
            a3.OnPlan = () => Task.FromResult(new RefreshPlanView { Clashes = 10, ExistingMarkers = 140, Update = 2 });
            Run(new ScheduleController(a3, d3).Refresh());
            string expected = MarkerSummary.Describe(outcome.Counts) + " " + MarkerSummary.SetLine(outcome.Sets[0]);
            check(P + "the summary lists each set that has something to say and skips one that has nothing", d3.Statuses.SequenceEqual(Texts(expected)) && MarkerSummary.SetLine(outcome.Sets[1]) == "");
        }

        // ------------------------------------------------------------------ refusals

        private static void Dialogs(Action<string, bool?> check)
        {
            var a = new Actions(); var d = new Screen();
            a.Snapshot.RefusalReason = " This model is read-only. ";
            Run(new ScheduleController(a, d).RemoveAll());
            check(P + "remove all in a model that refuses writes shows the reason once and asks nothing", d.Messages.SequenceEqual(Texts("This model is read-only.")) && !d.Calls.Contains("confirm") && !a.Calls.Contains("removeAll"));
        }

        // ------------------------------------------------------------------ assigning

        private static void Assigning(Action<string, bool?> check)
        {
            var a = new Actions(); var d = new Screen();
            a.Snapshot.Rows["k1"] = new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0001", HeldBy = "kdoe" };
            a.Snapshot.Rows["k2"] = new RowScheduleInfo { HasMarker = true, IssueNo = null, HeldBy = "asmith" };
            d.OnAssign = m => null;
            Run(new ScheduleController(a, d).Assign(Two));
            check(P + "the assign dialog is told which clashes wait for another user, by issue number or as 'a clash'", d.LastAssign.PendingLines.SequenceEqual(Texts("JD-0001: held by kdoe", "a clash: held by asmith")));
            check(P + "a cancelled assign says nothing was changed and assigns nobody", d.Statuses.SequenceEqual(Texts("Cancelled. Nothing was changed.")) && d.Messages.Count == 0 && !a.Calls.Contains("assign"));

            a = new Actions(); d = new Screen();
            a.OnAssign = () => Task.FromResult<AssignOutcome>(null);
            Run(new ScheduleController(a, d).Assign(One));
            check(P + "an assign that is not answered says so once", d.Messages.SequenceEqual(Texts(NoAnswer)) && d.Statuses.Count == 0);

            a = new Actions(); d = new Screen();
            d.OnAssign = m => new AssignResult { Unassign = true, User = "ignored" };
            Run(new ScheduleController(a, d).Assign(One));
            check(P + "an unassign sends no name even if the dialog's result carries one", a.LastUser == "");

            a = new Actions(); d = new Screen();
            a.OnAssign = () => Task.FromResult(new AssignOutcome { Ok = true, Assigned = -2, Pending = 0 });
            Run(new ScheduleController(a, d).Assign(One));
            check(P + "a negative count of assigned clashes reads as none", d.Statuses.SequenceEqual(Texts("Nothing was assigned.")));
        }

        // ------------------------------------------------------------------ failures

        private static void Failures(Action<string, bool?> check)
        {
            var a = new Actions(); var d = new Screen();
            a.OnOpen = () => { throw new InvalidOperationException("boom"); };
            Run(new ScheduleController(a, d).Open());
            check(P + "an action that fails says what failed, in these words", d.Messages.SequenceEqual(Texts("The clash schedule could not finish: boom")));

            a = new Actions(); d = new Screen();
            a.OnSetStatus = () => { throw new InvalidOperationException("boom"); };
            var task = new ScheduleController(a, d).SetStatus(One, ClashStatusKind.Ignored, "n");
            Run(task);
            check(P + "a triage that fails says what failed and that the decision was not recorded, and answers no", !task.Result && d.Messages.SequenceEqual(Texts("The clash schedule could not finish: boom Your decision was NOT recorded.")));
        }
    }
}
