using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>What only the Revit side knows when it hands the plan to the dialog.</summary>
    public sealed class RefreshPlanContext
    {
        /// <summary>Why the refresh may not run at all (spec 7), or null.</summary>
        public string Refusal { get; set; }
        public bool NonWorkshared { get; set; }
        public List<string> FirstCreateAdds { get; set; } = new List<string>();
        public List<string> ExistingSchedulesShowing { get; set; } = new List<string>();
        /// <summary>Counts by status, for the size warning.</summary>
        public List<MarkerSummary.Reason> ByStatus { get; set; } = new List<MarkerSummary.Reason>();
    }

    /// <summary>
    /// Builds the dialog's <see cref="RefreshPlanView"/> from the pure plan. The counts come from the plan's own
    /// <c>ToCounts()</c>, so the dialog and the summary after the commit cannot disagree. Pure.
    /// </summary>
    public static class RefreshPlanFactory
    {
        private const string OverCapReason = "over the cap";

        public static RefreshPlanView From(MarkerPlan.Result plan, MarkerPlan.Options options, RefreshPlanContext context)
        {
            var view = new RefreshPlanView();
            if (context != null)
            {
                view.Refusal = context.Refusal;
                view.NonWorkshared = context.NonWorkshared;
                view.FirstCreateAdds = context.FirstCreateAdds ?? new List<string>();
                view.ExistingSchedulesShowing = context.ExistingSchedulesShowing ?? new List<string>();
                view.ByStatus = context.ByStatus ?? new List<MarkerSummary.Reason>();
            }
            if (options != null)
            {
                view.RetentionDays = options.RetentionDays;
                view.Cap = options.Cap;
            }
            if (plan == null) return view;

            var counts = plan.ToCounts();
            view.Clashes = counts.Clashes;
            view.ExistingMarkers = plan.ExistingMarkers;
            view.Create = counts.Created;
            view.Update = counts.Updated;
            view.Move = counts.Moved;
            view.Resolve = counts.Resolved;
            view.Merge = counts.Merged;
            view.PendingApplied = counts.PendingApplied;
            view.Unchanged = counts.Unchanged;
            view.PassedOver = counts.PassedOver;
            view.NotRetested = counts.NotRetested;
            view.RetentionCount = plan.RetentionOffers.Count;

            // Rows the planner could not mark (no position) are shown with the not-re-tested ones, so the counts add up and
            // nothing is hidden; rows left out over the cap are spoken for by the cap message.
            int unmarked = 0;
            foreach (var reason in counts.NotMarked.Where(r => string.CompareOrdinal(r.Text, 0, OverCapReason, 0, OverCapReason.Length) != 0))
            {
                view.NotRetested.Add(new MarkerSummary.Reason { Text = (string.IsNullOrWhiteSpace(reason.Text) ? "other" : reason.Text.Trim()) + " (not marked)", Count = reason.Count });
                unmarked += reason.Count;
            }

            view.IsCreate = plan.ExistingMarkers == 0 && plan.Creations.Count > 0;
            view.NeedsDialog = plan.NeedsDialog || unmarked > 0;
            view.OverCap = plan.OverCap;
            view.OverWarn = plan.OverWarn;
            view.TotalMarkers = plan.ExistingMarkers + plan.Creations.Count;
            view.WouldCreate = plan.Creations.Count
                + plan.NotMarked.Count(n => n.Reason != null && n.Reason.StartsWith(OverCapReason, StringComparison.Ordinal));

            // Stryker disable once String : unparsable text is ignored by Extreme either way
            var numbers = plan.Creations.Select(c => c.Record == null ? "" : c.Record.IssueNo).Where(n => n != null).ToList();
            view.FirstIssueNo = Extreme(numbers, true);
            view.LastIssueNo = Extreme(numbers, false);
            return view;
        }

        // The lowest or highest readable issue number (prefix ordinal, then number); "" when none reads.
        private static string Extreme(List<string> numbers, bool lowest)
        {
            var readable = new List<Tuple<string, int>>();
            foreach (var n in numbers)
            {
                string prefix; int number;
                if (IssueNumbers.TryParse(n, out prefix, out number)) readable.Add(Tuple.Create(prefix, number));
            }
            if (readable.Count == 0) return "";
            var ordered = readable.OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2);
            Tuple<string, int> pick;
            if (lowest)
            {
                // Stryker disable once Linq : an empty list returned before this line
                pick = ordered.First();
            }
            else pick = ordered.Last();
            return IssueNumbers.Format(pick.Item1, pick.Item2);
        }
    }
}
