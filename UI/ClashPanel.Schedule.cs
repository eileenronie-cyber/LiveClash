using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The panel's side of the clash schedule (spec 9): the "Clash schedule" button and its menu, the columns, the
    /// "By others" filter, Assign. All of it dark until something sets <see cref="ScheduleActions"/>: with none, the
    /// button and columns have no width and the rows read exactly as before. The text and rules are in the models in
    /// Core; the flows are <see cref="ScheduleController"/>'s. This only wires them to controls.
    /// </summary>
    public partial class ClashPanel
    {
        private const double IssueWidth = 78, AssignedWidth = 90, HeldByWidth = 80, GroupWidth = 110;

        private IClashScheduleActions _schedule;
        private ScheduleController _scheduleController;

        /// <summary>The Revit side of the clash schedule; null (the default) hides the whole feature.</summary>
        public IClashScheduleActions ScheduleActions
        {
            get { return _schedule; }
            set { AttachSchedule(value); }
        }

        private ScheduleSnapshot ScheduleSnapshotNow { get { return _schedule == null ? null : _schedule.Snapshot; } }

        private void AttachSchedule(IClashScheduleActions actions)
        {
            if (_schedule != null) _schedule.Changed -= OnScheduleChanged;
            _schedule = actions;
            if (actions == null)
            {
                _scheduleController = null;
                ClashRowVm.ScheduleSource = null;
            }
            else
            {
                _scheduleController = new ScheduleController(actions, new ScheduleDialogs(this));
                ClashRowVm.ScheduleSource = () => ScheduleSnapshotNow;
                actions.Changed += OnScheduleChanged;
            }
            ReloadSchedule();
        }

        // Raised on the UI thread by contract; marshalled anyway, because a late raise from a worker must not throw here.
        private void OnScheduleChanged()
        {
            try
            {
                if (Dispatcher.CheckAccess()) ReloadSchedule();
                else Dispatcher.BeginInvoke(new Action(ReloadSchedule));
            }
            catch (Exception ex)
            {
                Log.Warn("Clash schedule: the panel could not follow a change", ex);
            }
        }

        private void ReloadSchedule()
        {
            ApplyScheduleUi();
            Refresh();
        }

        /// <summary>The button text and chip, the columns that have a width, and the controls that exist at all.</summary>
        internal void ApplyScheduleUi()
        {
            bool on = _schedule != null;
            var snap = ScheduleSnapshotNow;
            var visible = on ? Visibility.Visible : Visibility.Collapsed;

            ScheduleButton.Visibility = visible;
            AssignButton.Visibility = visible;
            AssignMenuItem.Visibility = visible;

            var menu = ScheduleMenuModel.Build(snap);
            ScheduleButton.Content = menu.ButtonText + " ▾";
            ScheduleButton.ToolTip = menu.ButtonTooltip;

            var cols = RowScheduleText.Columns(on ? snap : null);
            SetWidth(IssueColumn, cols.IssueNo, IssueWidth);
            SetWidth(AssignedColumn, cols.Assigned, AssignedWidth);
            SetWidth(HeldByColumn, cols.HeldBy, HeldByWidth);
            SetWidth(ScheduleGroupColumn, cols.Group, GroupWidth);

            bool anyMarkers = on && snap != null && snap.HasMarkers;
            ByOthersCheck.Visibility = anyMarkers ? Visibility.Visible : Visibility.Collapsed;
            if (!anyMarkers) ByOthersCheck.IsChecked = false;

            UpdateScheduleSelectionUi();
        }

        // A column keeps the width the user gave it; it only appears at its default or goes to nothing.
        private static void SetWidth(GridViewColumn column, bool show, double defaultWidth)
        {
            if (!show) column.Width = 0;
            else if (column.Width <= 0 || double.IsNaN(column.Width)) column.Width = defaultWidth;
        }

        /// <summary>Assign needs a selection; Discard pending is offered only for rows that have a pending triage.</summary>
        private void UpdateScheduleSelectionUi()
        {
            if (_schedule == null) return;
            var keys = SelectedScheduleKeys();
            AssignButton.IsEnabled = keys.Count > 0;
            var snap = ScheduleSnapshotNow;
            bool anyPending = snap != null && keys.Any(k => snap.Row(k) != null && !string.IsNullOrWhiteSpace(snap.Row(k).PendingStatus));
            DiscardPendingMenuItem.Visibility = anyPending ? Visibility.Visible : Visibility.Collapsed;
        }

        private List<string> SelectedScheduleKeys()
        {
            var keys = new List<string>();
            foreach (var item in ResultList.SelectedItems)
            {
                var row = item as ClashRowVm;
                if (row != null && !row.IsResolved) keys.AddRange(row.Keys());
            }
            return keys.Distinct().ToList();
        }

        // ------------------------------------------------------------------ the menu

        /// <summary>Fills the menu from the model for the snapshot as it is now; the items it made, for a check to read.</summary>
        internal List<MenuItem> BuildScheduleMenu()
        {
            ScheduleMenu.Items.Clear();
            var made = new List<MenuItem>();
            foreach (var item in ScheduleMenuModel.Build(ScheduleSnapshotNow).Items)
            {
                if (item.SeparatorBefore) ScheduleMenu.Items.Add(new Separator());
                var mi = new MenuItem { Header = item.Text, IsEnabled = item.Enabled, Tag = item.Id };
                if (!string.IsNullOrEmpty(item.Tooltip))
                {
                    mi.ToolTip = item.Tooltip;
                    ToolTipService.SetShowOnDisabled(mi, true);
                }
                mi.Click += ScheduleItem_Click;
                ScheduleMenu.Items.Add(mi);
                made.Add(mi);
            }
            return made;
        }

        private void ScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            BuildScheduleMenu();
            ScheduleMenu.PlacementTarget = ScheduleButton;
            ScheduleMenu.Placement = PlacementMode.Bottom;
            ScheduleMenu.IsOpen = true;
        }

        private void ScheduleItem_Click(object sender, RoutedEventArgs e)
        {
            var c = _scheduleController;
            var id = (sender as MenuItem) == null ? null : (sender as MenuItem).Tag as string;
            if (c == null || id == null) return;
            switch (id)
            {
                case ScheduleMenuModel.Refresh: Start(c.Refresh()); break;
                case ScheduleMenuModel.Open: Start(c.Open()); break;
                case ScheduleMenuModel.Focus: Start(c.ShowInFocus()); break;
                case ScheduleMenuModel.RemoveResolved: Start(c.RemoveResolved()); break;
                case ScheduleMenuModel.RemoveAll: Start(c.RemoveAll()); break;
                case ScheduleMenuModel.RemoveParameters: Start(c.RemoveParameters()); break;
            }
        }

        // The controller answers every failure itself, so the task is not awaited here.
        private static void Start(System.Threading.Tasks.Task flow)
        {
            var ignored = flow;
        }

        // ------------------------------------------------------------------ triage on rows that have a marker (spec 6.7)

        private System.Threading.Tasks.Task _triage = System.Threading.Tasks.Task.CompletedTask;

        /// <summary>The keys whose triage goes through the marker; the rest keep today's ledger-only path. None with no schedule attached.</summary>
        private List<string> MarkerKeysOf(IEnumerable<string> keys)
        {
            return _schedule == null ? new List<string>() : RowScheduleText.MarkerKeys(ScheduleSnapshotNow, keys);
        }

        /// <summary>While the clash schedule is still loading for this model the panel cannot tell which rows have markers: no triage at all, with a message (review H1).</summary>
        private bool TriageLoading(bool note)
        {
            return _scheduleController != null && _scheduleController.RefuseTriage(note);
        }

        private static void WriteLedgerStatus(List<string> keys, ClashStatusKind kind, string reason)
        {
            if (keys.Count == 0) return;
            if (string.IsNullOrWhiteSpace(reason)) ClashStatusStore.Instance.Set(keys, kind);
            else ClashStatusStore.Instance.Set(keys, kind, reason);
        }

        // The marker path first; the rows without a marker only once it said yes, so a refusal leaves nothing half-written (delta review D-L2).
        private void SetStatusThenLedger(List<string> markerKeys, List<string> ledgerKeys, ClashStatusKind kind, string note)
        {
            var c = _scheduleController;
            if (c == null) { WriteLedgerStatus(ledgerKeys, kind, note); return; }
            _triage = TakeStatus(c, markerKeys, ledgerKeys, kind, note);
        }

        private async System.Threading.Tasks.Task TakeStatus(ScheduleController c, List<string> markerKeys, List<string> ledgerKeys, ClashStatusKind kind, string note)
        {
            if (!await c.SetStatus(markerKeys, kind, string.IsNullOrWhiteSpace(note) ? null : note.Trim())) return;
            if (ledgerKeys.Count == 0) return;
            WriteLedgerStatus(ledgerKeys, kind, note);
            Refresh();
        }

        // After the status's own flow: the reason prompt is modal, but a slow marker write must not make the note refused as "busy".
        private async void SetNoteThenLedger(List<string> markerKeys, List<string> ledgerKeys, string note)
        {
            var c = _scheduleController;
            if (c == null) { if (ledgerKeys.Count > 0) ClashStatusStore.Instance.SetNote(ledgerKeys, note); return; }
            await _triage;
            var answer = c.SetNote(markerKeys, note);
            _triage = answer;
            if (!await answer) return;
            if (ledgerKeys.Count == 0) return;
            ClashStatusStore.Instance.SetNote(ledgerKeys, note);
            Refresh();
        }
        // ------------------------------------------------------------------ Assign, pending, the filter

        private void Assign_Click(object sender, RoutedEventArgs e)
        {
            if (_scheduleController == null) return;
            var keys = SelectedScheduleKeys();
            if (keys.Count == 0) { SetStatus("Select an issue first."); return; }
            Start(_scheduleController.Assign(keys));
        }

        private async void DiscardPending_Click(object sender, RoutedEventArgs e)
        {
            var c = _scheduleController;
            var snap = ScheduleSnapshotNow;
            if (c == null || snap == null) return;
            foreach (var key in SelectedScheduleKeys())
            {
                var info = snap.Row(key);
                if (info == null || string.IsNullOrWhiteSpace(info.PendingStatus)) continue;
                await c.DiscardPending(key);
            }
        }

        private void ByOthers_Changed(object sender, RoutedEventArgs e)
        {
            Refresh();
        }
    }
}
