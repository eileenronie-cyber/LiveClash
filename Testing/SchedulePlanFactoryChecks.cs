using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B5: the adapter from builder A's plan (<see cref="MarkerPlan.Result"/>) to the plan
    /// dialog's view. Revit-free. Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class SchedulePlanFactoryChecks
    {
        private const string P = "schedule plan view: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Creates(check);
            Counts(check);
            Flags(check);
            Context(check);
            Real(check);
            Unmarked(check);
        }

        private static List<string> One(string item) { return new List<string> { item }; }

        private static MarkerPlan.Creation Create(string issue)
        {
            return new MarkerPlan.Creation { Triple = "t-" + issue, Record = new MarkerRecord { IssueNo = issue } };
        }

        private static MarkerPlan.Result Plan(params string[] issues)
        {
            var r = new MarkerPlan.Result();
            foreach (var i in issues) r.Creations.Add(Create(i));
            return r;
        }

        private static void Creates(Action<string, bool?> check)
        {
            var v = RefreshPlanFactory.From(Plan("JD-0009", "JD-0010", "JD-0008"), new MarkerPlan.Options(), null);
            check(P + "create: the first and last issue numbers are the lowest and highest, whatever the order", v.Create == 3 && v.FirstIssueNo == "JD-0008" && v.LastIssueNo == "JD-0010");
            check(P + "...a create is a first create: no existing markers and something to create", v.IsCreate);
            var existing = Plan("JD-0001");
            existing.ExistingMarkers = 140;
            check(P + "with markers already there it is not a create", !RefreshPlanFactory.From(existing, new MarkerPlan.Options(), null).IsCreate);
            check(P + "nothing to create is not a create either", !RefreshPlanFactory.From(new MarkerPlan.Result(), new MarkerPlan.Options(), null).IsCreate);
            var junk = RefreshPlanFactory.From(Plan("zeta", "JD-0004", "", "JD-0002"), new MarkerPlan.Options(), null);
            check(P + "unreadable issue numbers are skipped for the first and last", junk.FirstIssueNo == "JD-0002" && junk.LastIssueNo == "JD-0004");
            var none = RefreshPlanFactory.From(Plan("zeta", ""), new MarkerPlan.Options(), null);
            check(P + "...and with none readable there is no range", none.FirstIssueNo == "" && none.LastIssueNo == "" && none.Create == 2);
        }

        private static void Counts(Action<string, bool?> check)
        {
            var r = new MarkerPlan.Result { ExistingMarkers = 800, Unchanged = 786, Merges = 1 };
            r.Changes.Add(new MarkerPlan.Change { MarkerUid = "a" });
            r.Changes.Add(new MarkerPlan.Change { MarkerUid = "b" });
            r.Changes.Add(new MarkerPlan.Change { MarkerUid = "c", Resolved = true });
            r.Changes.Add(new MarkerPlan.Change { MarkerUid = "d", Moved = true });
            r.Changes.Add(new MarkerPlan.Change { MarkerUid = "e", PutBack = true });
            r.Pending.Add(new MarkerPlan.PendingOutcome { Kind = PendingKind.Applied });
            r.PassedOver.Add(new MarkerPlan.Reasoned { Key = "k1", Reason = "held by kdoe" });
            r.PassedOver.Add(new MarkerPlan.Reasoned { Key = "k2", Reason = "held by kdoe" });
            r.PassedOver.Add(new MarkerPlan.Reasoned { Key = "k3", Reason = "changed in central" });
            r.NotRetested.Add(new MarkerPlan.Reasoned { Key = "k4", Reason = "link not loaded" });
            var v = RefreshPlanFactory.From(r, new MarkerPlan.Options(), null);
            check(P + "the counts are the plan's own: update (all not resolved), resolve, move (moved or put back), merge, pending, unchanged",
                v.Update == 4 && v.Resolve == 1 && v.Move == 2 && v.Merge == 1 && v.PendingApplied == 1 && v.Unchanged == 786 && v.ExistingMarkers == 800);
            check(P + "...clashes is existing markers plus creations plus rows left unmarked", v.Clashes == r.ToCounts().Clashes && v.Clashes == 800);
            check(P + "...the passed-over and not-re-tested reasons are carried with their counts",
                v.PassedOver.Count == 2 && v.PassedOver.Any(x => x.Text == "held by kdoe" && x.Count == 2) && v.PassedOver.Any(x => x.Text == "changed in central" && x.Count == 1)
                && v.NotRetested.Count == 1 && v.NotRetested[0].Text == "link not loaded" && v.NotRetested[0].Count == 1);
        }

        private static void Flags(Action<string, bool?> check)
        {
            var r = new MarkerPlan.Result();
            r.RetentionOffers.Add(new MarkerPlan.Removal { MarkerUid = "a", Reason = "retention" });
            r.RetentionOffers.Add(new MarkerPlan.Removal { MarkerUid = "b", Reason = "retention" });
            var v = RefreshPlanFactory.From(r, new MarkerPlan.Options { RetentionDays = 45 }, null);
            check(P + "retention offers and their days are carried", v.RetentionCount == 2 && v.RetentionDays == 45);

            var cap = Plan("JD-0001", "JD-0002");
            cap.OverCap = true;
            for (int i = 0; i < 3; i++) cap.NotMarked.Add(new MarkerPlan.Reasoned { Key = "n" + i, Reason = "over the cap of 2 " });
            cap.NotMarked.Add(new MarkerPlan.Reasoned { Key = "p", Reason = "no position" });
            cap.NotMarked.Add(new MarkerPlan.Reasoned { Key = "q", Reason = null });
            var c = RefreshPlanFactory.From(cap, new MarkerPlan.Options { Cap = 2 }, null);
            check(P + "over the cap: the would-create count is the creations plus the rows left out over the cap, not the unmarked others",
                c.OverCap && c.Cap == 2 && c.WouldCreate == 5);
            check(P + "not over the cap: the would-create count is just the creations", RefreshPlanFactory.From(Plan("JD-0001"), new MarkerPlan.Options(), null).WouldCreate == 1);

            var warn = Plan("JD-0001", "JD-0002");
            warn.ExistingMarkers = 5000; warn.OverWarn = true; warn.NeedsDialog = true;
            var w = RefreshPlanFactory.From(warn, new MarkerPlan.Options(), null);
            check(P + "the size warning flag and the total (existing plus creations) are carried", w.OverWarn && w.TotalMarkers == 5002);
            check(P + "the dialog flag comes from the plan, both ways", w.NeedsDialog && !RefreshPlanFactory.From(new MarkerPlan.Result(), new MarkerPlan.Options(), null).NeedsDialog);
        }

        private static void Context(Action<string, bool?> check)
        {
            var ctx = new RefreshPlanContext
            {
                Refusal = "This model is read-only.", NonWorkshared = true,
                FirstCreateAdds = { "the LiveClash workset" }, ExistingSchedulesShowing = { "Specialty Equipment Schedule" },
                ByStatus = { new MarkerSummary.Reason { Text = "New", Count = 3 } }
            };
            var v = RefreshPlanFactory.From(new MarkerPlan.Result(), new MarkerPlan.Options(), ctx);
            check(P + "the Revit side's context is carried: refusal, non-workshared, the first-create lines, the counts by status",
                v.Refusal == "This model is read-only." && v.NonWorkshared && v.FirstCreateAdds.SequenceEqual(One("the LiveClash workset"))
                && v.ExistingSchedulesShowing.SequenceEqual(One("Specialty Equipment Schedule")) && v.ByStatus.Count == 1 && v.ByStatus[0].Count == 3);

            // Branch coverage, added at green: lists the Revit side left null, and creations with no record or no number.
            var nulls = new RefreshPlanContext { FirstCreateAdds = null, ExistingSchedulesShowing = null, ByStatus = null };
            var n = RefreshPlanFactory.From(new MarkerPlan.Result(), new MarkerPlan.Options(), nulls);
            check(P + "lists left null by the Revit side read as empty", n.FirstCreateAdds.Count == 0 && n.ExistingSchedulesShowing.Count == 0 && n.ByStatus.Count == 0);
            var odd = new MarkerPlan.Result();
            odd.Creations.Add(new MarkerPlan.Creation { Triple = "a", Record = null });
            odd.Creations.Add(new MarkerPlan.Creation { Triple = "b", Record = new MarkerRecord { IssueNo = null } });
            odd.Creations.Add(Create("JD-0005"));
            var o = RefreshPlanFactory.From(odd, new MarkerPlan.Options(), null);
            check(P + "a creation with no record or no issue number is counted but never gives a number", o.Create == 3 && o.FirstIssueNo == "JD-0005" && o.LastIssueNo == "JD-0005");

            bool ok = true;
            try
            {
                var a = RefreshPlanFactory.From(null, null, null);
                var b = RefreshPlanFactory.From(new MarkerPlan.Result(), null, ctx);
                ok = a != null && b != null && a.Create == 0 && b.Cap == 2000 && b.RetentionDays == 30;
            }
            catch (Exception) { ok = false; }
            check(P + "a null plan, options or context gives a usable view without throwing", ok);
        }

        // ------------------------------------------------------------------ rows the planner could not mark (B7)

        private static int Sum(List<MarkerSummary.Reason> reasons) { return reasons.Sum(r => r.Count); }

        private static MarkerPlan.Reasoned NotMarked(string reason, int n) { return new MarkerPlan.Reasoned { Key = "u" + n, Reason = reason }; }

        private static bool Balanced(RefreshPlanView v)
        {
            return v.Clashes == v.Create + v.Update + v.Resolve + v.Unchanged + Sum(v.PassedOver) + Sum(v.NotRetested) + Math.Max(0, v.WouldCreate - v.Create);
        }

        private static void Unmarked(Action<string, bool?> check)
        {
            var r = new MarkerPlan.Result();
            r.NotMarked.Add(NotMarked("no position", 1));
            r.NotMarked.Add(NotMarked("no position", 2));
            var v = RefreshPlanFactory.From(r, new MarkerPlan.Options(), null);
            check(P + "rows with no position are in the not-re-tested list as \"no position (not marked)\", with their count",
                v.NotRetested.Count == 1 && v.NotRetested[0].Text == "no position (not marked)" && v.NotRetested[0].Count == 2);

            var cap = Plan("JD-0001");
            cap.OverCap = true;
            cap.NotMarked.Add(NotMarked("over the cap of 1", 1));
            cap.NotMarked.Add(NotMarked("over the cap of 1", 2));
            var c = RefreshPlanFactory.From(cap, new MarkerPlan.Options { Cap = 1 }, null);
            check(P + "rows left out over the cap are not listed there (the cap message speaks for them)", c.NotRetested.Count == 0 && c.WouldCreate == 3);

            check(P + "a plan with only unmarked rows needs the dialog", v.NeedsDialog);
            var quiet = new MarkerPlan.Result { ExistingMarkers = 5, Unchanged = 5 };
            check(P + "...and a plan with none leaves the planner's flag alone (off here, and on when the planner set it)",
                !RefreshPlanFactory.From(quiet, new MarkerPlan.Options(), null).NeedsDialog && RefreshPlanFactory.From(new MarkerPlan.Result { NeedsDialog = true }, new MarkerPlan.Options(), null).NeedsDialog);

            // 10 existing markers: 7 unchanged, 1 updated, 1 resolved, 1 passed over; 2 new; 2 with no position; 3 over the cap.
            var h = Plan("JD-0011", "JD-0012");
            h.ExistingMarkers = 10; h.Unchanged = 7; h.OverCap = true;
            h.Changes.Add(new MarkerPlan.Change { MarkerUid = "u" });
            h.Changes.Add(new MarkerPlan.Change { MarkerUid = "r", Resolved = true });
            h.PassedOver.Add(new MarkerPlan.Reasoned { Key = "p", Reason = "held by kdoe" });
            h.NotMarked.Add(NotMarked("no position", 1));
            h.NotMarked.Add(NotMarked("no position", 2));
            for (int i = 0; i < 3; i++) h.NotMarked.Add(NotMarked("over the cap of 2", 10 + i));
            var hv = RefreshPlanFactory.From(h, new MarkerPlan.Options { Cap = 2 }, null);
            check(P + "the counts balance on a plan with existing markers, unmarked rows and rows over the cap (17 clashes)", hv.Clashes == 17 && Balanced(hv));

            var input = new MarkerPlan.Input { Counter = new IssueNumbers.Counter { Uid = "c1", UserName = "jdoe", Prefix = "JD", Next = 1 }, Options = new MarkerPlan.Options { Today = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), UserName = "jdoe" } };
            for (int i = 1; i <= 3; i++)
            {
                input.Rows.Add(new MarkerPlan.Row
                {
                    Key = "k" + i, HostUid = "h" + i, OtherUid = "o" + i, HostId = i, OtherId = 100 + i, Verdict = PlanVerdict.Clashing, Severity = "Hard",
                    HasPoint = i != 3, X = i, SetName = "MEP vs STR"
                });
            }
            var real = RefreshPlanFactory.From(MarkerPlan.Build(input), input.Options, null);
            var dialog = new RefreshPlanModel(real);
            var line = dialog.Lines.FirstOrDefault(l => l.Label == "Not re-tested");
            check(P + "a real plan (two clashes with a position, one without) balances, and the dialog shows \"1 no position (not marked)\"",
                real.Clashes == 3 && Balanced(real) && line != null && line.Count == 1 && line.Detail == "no position (not marked)");

            var mixed = new MarkerPlan.Result();
            mixed.NotMarked.Add(NotMarked("no position", 1));
            mixed.NotMarked.Add(NotMarked("no position", 2));
            mixed.NotMarked.Add(NotMarked("no level", 3));
            mixed.NotMarked.Add(NotMarked(null, 4));
            var mv = RefreshPlanFactory.From(mixed, new MarkerPlan.Options(), null);
            check(P + "several reasons are grouped with their counts, and no reason reads \"other (not marked)\"",
                mv.NotRetested.Count == 3 && mv.NotRetested.Any(x => x.Text == "no position (not marked)" && x.Count == 2)
                && mv.NotRetested.Any(x => x.Text == "no level (not marked)" && x.Count == 1) && mv.NotRetested.Any(x => x.Text == "other (not marked)" && x.Count == 1));

            var both = new MarkerPlan.Result { ExistingMarkers = 1 };
            both.NotRetested.Add(new MarkerPlan.Reasoned { Key = "k", Reason = "link not loaded" });
            both.NotMarked.Add(NotMarked("no position", 1));
            var bv = RefreshPlanFactory.From(both, new MarkerPlan.Options(), null);
            check(P + "they come with the real not-re-tested reasons, not instead of them",
                Sum(bv.NotRetested) == 2 && bv.NotRetested.Any(x => x.Text == "link not loaded") && bv.NotRetested.Any(x => x.Text == "no position (not marked)"));
        }

        // A plan from the real planner, through the dialog's model: the two halves agree.
        private static void Real(Action<string, bool?> check)
        {
            var input = new MarkerPlan.Input { Counter = new IssueNumbers.Counter { Uid = "c1", UserName = "jdoe", Prefix = "JD", Next = 1 }, Options = new MarkerPlan.Options { Today = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), UserName = "jdoe" } };
            for (int i = 1; i <= 2; i++)
            {
                input.Rows.Add(new MarkerPlan.Row
                {
                    Key = "k" + i, HostUid = "h" + i, OtherUid = "o" + i, HostId = i, OtherId = 100 + i, Verdict = PlanVerdict.Clashing, Severity = "Hard",
                    HasPoint = true, X = i, SetName = "MEP vs STR", RuleClearanceMm = 0
                });
            }
            var plan = MarkerPlan.Build(input);
            var view = RefreshPlanFactory.From(plan, input.Options, null);
            check(P + "a real plan for two live clashes and no markers is a create of two", view.IsCreate && view.Create == 2 && view.ExistingMarkers == 0 && view.Clashes == 2 && view.NeedsDialog);
            var line = new RefreshPlanModel(view).Lines.FirstOrDefault(l => l.Label == "Create");
            check(P + "...and the dialog's create line reads \"2 new clashes\" with the first and last numbers",
                line != null && line.Count == 2 && line.Detail.StartsWith("new clashes (", StringComparison.Ordinal) && line.Detail.Contains(" to "));
        }
    }
}
