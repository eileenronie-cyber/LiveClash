using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    public sealed class PlanLine
    {
        public string Label { get; set; } = "";
        public int Count { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// The Refresh plan dialog's text, lines and buttons (spec 9, 6.2, 6.9, 7). Pure. It shows what the plan
    /// says: the cap and size-warning flags come from the plan itself, so the thresholds are tested where the
    /// plan is built.
    /// </summary>
    public sealed class RefreshPlanModel
    {
        private const int WarnAt = 5000;
        private const int DefaultCap = 2000;
        private const int DefaultDays = 30;

        private readonly int _writes;
        private readonly bool _overCap;

        private static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }
        private static int Pos(int n) { return n > 0 ? n : 0; }
        private static string T(string s) { return s == null ? "" : s.Trim(); }

        public RefreshPlanModel(RefreshPlanView view)
        {
            if (view == null)
            {
                Title = "Refresh clash markers";
                Footer = "One Undo.";
                return;
            }

            Title = view.IsCreate ? "Create clash markers" : "Refresh clash markers";
            Footer = view.NonWorkshared ? "One Undo." : "One Undo. Others see the changes after you Synchronize.";
            Refusal = T(view.Refusal);

            _writes = Pos(view.Create) + Pos(view.Update) + Pos(view.Move) + Pos(view.Resolve) + Pos(view.Merge) + Pos(view.PendingApplied);

            AddLine("Create", Pos(view.Create), CreateDetail(view));
            AddLine("Update", Pos(view.Update), "status, note, severity, set or group");
            AddLine("Move", Pos(view.Move), "moved to where the clash is now");
            AddLine("Resolved", Pos(view.Resolve), "re-tested here: no longer clash");
            AddLine("Merge", Pos(view.Merge), "two markers for one clash");
            AddLine("Pending", Pos(view.PendingApplied), "your triage, held until now: applied");
            Lines.Add(new PlanLine { Label = "Unchanged", Count = Pos(view.Unchanged), Detail = "re-tested, nothing to write" });
            var passed = Listed(view.PassedOver);
            AddLine("Passed over", passed.Sum(r => r.Count), ReasonText(passed));
            var notRetested = Listed(view.NotRetested);
            AddLine("Not re-tested", notRetested.Sum(r => r.Count), ReasonText(notRetested));

            if (view.RetentionCount > 0)
            {
                ShowRetention = true;
                int days = view.RetentionDays > 0 ? view.RetentionDays : DefaultDays;
                RetentionText = "Remove " + N(view.RetentionCount) + (view.RetentionCount == 1 ? " marker" : " markers")
                    + " resolved more than " + N(days) + " days ago";
            }

            if (_writes == 0 && !view.OverCap && Refusal.Length == 0) NothingNote = "Markers up to date (" + N(Pos(view.ExistingMarkers)) + ").";

            _overCap = view.OverCap;
            int cap = view.Cap > 0 ? view.Cap : DefaultCap;
            if (view.OverCap)
            {
                CapMessage = N(view.WouldCreate) + " markers would be created. Narrow the sets, or create them in two runs (Refresh again after this one).";
                PrimaryText = "Create " + N(cap) + " now";
            }

            if (view.OverWarn)
            {
                var status = Listed(view.ByStatus);
                Warning = "This model holds " + N(view.TotalMarkers) + " markers, more than " + N(WarnAt) + "."
                    + (status.Count > 0 ? " By status: " + string.Join(", ", status.Select(r => N(r.Count) + " " + r.Text)) + "." : "")
                    + " Consider Remove resolved, or narrower sets.";
            }

            if (view.IsCreate)
            {
                var adds = Clean(view.FirstCreateAdds);
                if (adds.Count > 0) { AddsHeading = "This adds to the model:"; AddsLines.AddRange(adds); }
                var showing = Clean(view.ExistingSchedulesShowing);
                if (showing.Count > 0)
                {
                    ShowingHeading = "These existing schedules and filters do not look for LiveClash markers and will show them:";
                    ShowingLines.AddRange(showing);
                }
            }
        }

        // Stryker disable once String : set by both constructor paths
        public string Title { get; private set; } = "";
        // Stryker disable once String : set by both constructor paths
        public string Footer { get; private set; } = "";
        public string Refusal { get; private set; } = "";
        public string CapMessage { get; private set; } = "";
        public string Warning { get; private set; } = "";
        /// <summary>"Markers up to date (N)." when there is nothing to write, so a dialog opened only to show what was passed over still says so.</summary>
        public string NothingNote { get; private set; } = "";
        public string AddsHeading { get; private set; } = "";
        public string ShowingHeading { get; private set; } = "";
        public string RetentionText { get; private set; } = "";
        public string PrimaryText { get; private set; } = "Apply";
        public string CancelText { get; private set; } = "Cancel";
        public List<PlanLine> Lines { get; private set; } = new List<PlanLine>();
        public List<string> AddsLines { get; private set; } = new List<string>();
        public List<string> ShowingLines { get; private set; } = new List<string>();
        public bool ShowRetention { get; private set; }
        /// <summary>Unticked by default (E9). Ticking it while no retention is offered changes nothing.</summary>
        public bool RemoveRetention { get; set; }

        public bool CanApply
        {
            get { return Refusal.Length == 0 && (_writes > 0 || _overCap || (ShowRetention && RemoveRetention)); }
        }

        public RefreshChoices Choices()
        {
            return new RefreshChoices { RemoveRetention = ShowRetention && RemoveRetention, CreateCapOnly = _overCap };
        }

        private void AddLine(string label, int count, string detail)
        {
            if (count > 0) Lines.Add(new PlanLine { Label = label, Count = count, Detail = detail });
        }

        private static string CreateDetail(RefreshPlanView v)
        {
            string first = T(v.FirstIssueNo), last = T(v.LastIssueNo);
            string noun = v.Create == 1 ? "new clash" : "new clashes";
            if (first.Length > 0 && last.Length > 0 && first != last) return noun + " (" + first + " to " + last + ")";
            if (first.Length > 0) return noun + " (" + first + ")";
            if (last.Length > 0) return noun + " (" + last + ")";
            return noun;
        }

        private static List<MarkerSummary.Reason> Listed(List<MarkerSummary.Reason> reasons)
        {
            return (reasons ?? new List<MarkerSummary.Reason>()).Where(r => r != null && r.Count > 0)
                .Select(r => new MarkerSummary.Reason { Text = string.IsNullOrWhiteSpace(r.Text) ? "other" : r.Text.Trim(), Count = r.Count })
                .OrderByDescending(r => r.Count).ThenBy(r => r.Text, StringComparer.Ordinal).ToList();
        }

        // One reason is the whole line ("link not loaded"); several each get their own count.
        private static string ReasonText(List<MarkerSummary.Reason> listed)
        {
            return listed.Count == 1 ? listed[0].Text : string.Join(", ", listed.Select(r => N(r.Count) + " " + r.Text));
        }

        private static List<string> Clean(List<string> lines)
        {
            return (lines ?? new List<string>()).Select(T).Where(s => s.Length > 0).ToList();
        }
    }
}
