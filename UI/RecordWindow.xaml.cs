using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using LiveClash.Core;
using Microsoft.Win32;

using Color = System.Windows.Media.Color;
using ElementId = Autodesk.Revit.DB.ElementId;

namespace LiveClash.UI
{
    /// <summary>
    /// The unified coordination record — Pending / Coordinated / Ignored decisions plus
    /// Resolved pairs, filterable by state. Modeless, self-refreshing. Selecting a row
    /// selects the elements through the panel's ExternalEvent (this window is not API
    /// context). Fed by <see cref="ClashRecord"/>.
    /// </summary>
    public partial class RecordWindow : Window
    {
        public class Row
        {
            public ClashRecordRow Rec;

            public string StateText { get { return Rec.StateText; } }
            public string WhenText { get { return Rec.WhenText; } }
            public string HostText { get { return Rec.HostText; } }
            public string OtherText { get { return Rec.OtherText; } }
            public string ModelName { get { return Rec.ModelName; } }
            public string By { get { return Rec.By; } }
            public string Note { get { return Rec.Note; } }

            /// <summary>
            /// The day this decision was made, as a heading: "Today", "Yesterday",
            /// then "Fri 29 Aug". Grouping on this is what turns a flat ledger into
            /// "what did we decide since the last coordination meeting".
            /// </summary>
            public string WhenDay
            {
                get
                {
                    if (Rec.WhenUtc == DateTime.MinValue) return "No date recorded";
                    var d = Rec.WhenUtc.ToLocalTime().Date;
                    var today = DateTime.Today;
                    if (d == today) return "Today";
                    if (d == today.AddDays(-1)) return "Yesterday";
                    return d.ToString("ddd d MMM");
                }
            }

            /// <summary>
            /// SORT KEY for the group, not shown. Grouping on the LABEL would order the
            /// headings alphabetically - "Fri 29 Aug" above "Today" - which is the same
            /// trap as sorting the formatted WhenText string. Ticks descending puts the
            /// newest day first and keeps "Today" and "Yesterday" in real order.
            /// </summary>
            public long WhenDayKey
            {
                get
                {
                    return Rec.WhenUtc == DateTime.MinValue
                        ? long.MinValue
                        : -Rec.WhenUtc.ToLocalTime().Date.Ticks;
                }
            }

            /// <summary>"3 hrs ago", "12 days ago" - what a Pending row needs more than
            /// a date. A decision three weeks old is a different thing from this
            /// morning's, and the date alone does not say so at a glance.</summary>
            public string AgeText
            {
                get
                {
                    if (Rec.WhenUtc == DateTime.MinValue) return "";
                    var span = DateTime.UtcNow - Rec.WhenUtc;
                    if (span.TotalMinutes < 1) return "just now";
                    if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
                    if (span.TotalHours < 24) return (int)span.TotalHours + " hr ago";
                    int days = (int)span.TotalDays;
                    return days == 1 ? "1 day ago" : days + " days ago";
                }
            }

            public Brush Dot
            {
                get
                {
                    switch (Rec.State)
                    {
                        case ClashRecordState.Pending:     return Amber;
                        case ClashRecordState.Coordinated: return Blue;
                        case ClashRecordState.Ignored:     return Grey;
                        default: return Rec.StillClashing ? Red : Green;
                    }
                }
            }

            private static readonly Brush Amber = F(0xC9, 0x7A, 0x0A);
            private static readonly Brush Blue  = F(0x2E, 0x6F, 0xB8);
            private static readonly Brush Grey  = F(0x8A, 0x8F, 0x98);
            private static readonly Brush Green = F(0x2A, 0x9D, 0x5C);
            private static readonly Brush Red   = F(0xD1, 0x3B, 0x3B);

            private static Brush F(byte r, byte g, byte b)
            {
                var br = new SolidColorBrush(Color.FromRgb(r, g, b));
                br.Freeze();
                return br;
            }
        }

        private readonly List<Row> _all = new List<Row>();
        private readonly ObservableCollection<Row> _shown = new ObservableCollection<Row>();

        /// <summary>The model and rules <see cref="_all"/> was actually built under, captured at
        /// <see cref="Reload"/> time — not whatever is live when Export is later clicked. This
        /// window is modeless and only reloads on ClashStatusStore/ResolvedLog.Changed, never on a
        /// document switch, so the active document (and its settings) can already have moved on to
        /// a different model by export time; stamping with the live state would claim rows that
        /// are still the OLD model's came from the new one.</summary>
        private CsvStamp _loadedStamp;
        private readonly Action<List<ElementId>> _select;

        private readonly Action _recheck;

        public RecordWindow(Action<List<ElementId>> select, Action recheck = null)
        {
            InitializeComponent();
            ThemeManager.Attach(this);

            // 1140 wide suits the columns, but a laptop screen or a scaled display can be narrower, and
            // a window taller or wider than the work area pushes its Close button off-screen. Fitted to
            // the monitor it is actually on, not the primary one.
            WorkArea.Keep(this);

            _select = select;
            _recheck = recheck;
            if (RecheckButton != null) RecheckButton.IsEnabled = recheck != null;

            StateFilter.Items.Add("Everything");
            StateFilter.Items.Add("Pending");
            StateFilter.Items.Add("Coordinated");
            StateFilter.Items.Add("Ignored");
            StateFilter.Items.Add("Resolved");
            StateFilter.SelectedIndex = 0;

            // Group by day. The view groups on WhenDayKey so the headings order by real
            // date; the header template shows WhenDay, which is the readable label.
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_shown);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("WhenDayKey"));

            RecordList.ItemsSource = _shown;
            Reload();

            ClashStatusStore.Instance.Changed += OnChanged;
            ResolvedLog.Changed += OnChanged;
            Closed += delegate
            {
                ClashStatusStore.Instance.Changed -= OnChanged;
                ResolvedLog.Changed -= OnChanged;
            };
        }

        private void OnChanged(object sender, EventArgs e)
        {
            try { Dispatcher.BeginInvoke(new Action(delegate { Reload(); })); }
            catch { }
        }

        private void Reload()
        {
            // Captured here, WITH the rows, so an export later says what THESE rows actually came
            // from even if the active document has since moved to a different model.
            _loadedStamp = CsvExport.StampNow(ClashSettings.ModelName);

            _all.Clear();
            foreach (var r in ClashRecord.Build()) _all.Add(new Row { Rec = r });
            ApplyFilter();
        }

        private void Search_Changed(object sender, TextChangedEventArgs e) { ApplyFilter(); }
        private void Filter_Changed(object sender, RoutedEventArgs e) { ApplyFilter(); }

        private void ApplyFilter()
        {
            if (_shown == null) return;

            var q = SearchBox == null ? "" : (SearchBox.Text ?? "").Trim();
            int sel = StateFilter == null ? 0 : StateFilter.SelectedIndex;

            _shown.Clear();
            foreach (var r in _all)
            {
                if (sel == 1 && r.Rec.State != ClashRecordState.Pending) continue;
                if (sel == 2 && r.Rec.State != ClashRecordState.Coordinated) continue;
                if (sel == 3 && r.Rec.State != ClashRecordState.Ignored) continue;
                if (sel == 4 && r.Rec.State != ClashRecordState.Resolved) continue;

                if (q.Length > 0
                    && r.HostText.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && r.OtherText.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && (r.ModelName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && (r.Note ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                _shown.Add(r);
            }

            // Newest first, and by the real timestamp rather than the formatted string.
            // The view groups on WhenDayKey, and a CollectionView groups in the order it
            // meets items - so the underlying order is what decides whether "Today" sits
            // above "Fri 29 Aug" or below it.
            var ordered = _shown.OrderBy(r => r.WhenDayKey)
                                .ThenByDescending(r => r.Rec.WhenUtc)
                                .ToList();
            _shown.Clear();
            foreach (var r in ordered) _shown.Add(r);

            var t = ClashRecord.Tally(RecOf(_all));
            SummaryText.Text =
                t[ClashRecordState.Pending] + " pending  ·  "
                + t[ClashRecordState.Coordinated] + " coordinated  ·  "
                + t[ClashRecordState.Ignored] + " ignored  ·  "
                + t[ClashRecordState.Resolved] + " resolved"
                + (_shown.Count != _all.Count ? "   (showing " + _shown.Count + ")" : "");
        }

        private static List<ClashRecordRow> RecOf(List<Row> rows)
        {
            var list = new List<ClashRecordRow>(rows.Count);
            foreach (var r in rows) list.Add(r.Rec);
            return list;
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_select == null) return;

            var ids = new List<ElementId>();
            var seen = new HashSet<long>();
            foreach (var item in RecordList.SelectedItems)
            {
                var row = item as Row;
                if (row == null) continue;

                if (row.Rec.HostId != 0 && seen.Add(row.Rec.HostId)) ids.Add(IdUtil.From(row.Rec.HostId));

                // Only host-document elements can be selected; a link hit's OtherId is an
                // id in the linked document and means nothing here.
                bool linked = !string.IsNullOrEmpty(row.Rec.ModelName)
                              && row.Rec.ModelName != "Current model";
                if (!linked && row.Rec.OtherId != 0 && seen.Add(row.Rec.OtherId))
                    ids.Add(IdUtil.From(row.Rec.OtherId));
            }

            if (ids.Count > 0) _select(ids);
        }

        /// <summary>
        /// Closes the window. Needed because this one is MODELESS: IsCancel on its own
        /// only closes a window shown with ShowDialog, by setting DialogResult, and this
        /// is opened with Show(). The button looked wired and did nothing.
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); }
            catch (Exception ex) { Log.Warn("Closing the record window failed", ex); }
        }

        /// <summary>
        /// Re-test everything in THIS window, which is a different set from the panel's
        /// Recheck: the panel re-tests what currently clashes, this re-tests what somebody
        /// decided about — including pairs that dropped out of the live results long ago
        /// and that nothing else ever looks at again.
        ///
        /// The window refreshes itself: the store raises Changed when anything moves, and
        /// this window already listens for that.
        /// </summary>
        private void Recheck_Click(object sender, RoutedEventArgs e)
        {
            if (_recheck == null) return;
            try
            {
                RecheckButton.IsEnabled = false;
                SummaryText.Text = "Re-testing every decided pair…";
                _recheck();
            }
            catch (Exception ex) { Log.Warn("Record recheck could not start", ex); }
            finally
            {
                // The pass runs on Revit's thread; re-enable once it has had a chance to
                // finish rather than leaving the button dead if it fails early.
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += delegate { t.Stop(); RecheckButton.IsEnabled = true; };
                t.Start();
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            string path;
            // Model and rules come from _loadedStamp — what _all was actually built under (see
            // Reload) — but Exported/By are genuinely "now": captured ONCE, here, before the modal
            // dialog blocks, so the suggested file name and the stamp written into the file agree
            // on when, rather than disagreeing by however long the user spends in the dialog.
            var stamp = new CsvStamp
            {
                Model = _loadedStamp != null ? _loadedStamp.Model : ClashSettings.ModelName,
                Rules = _loadedStamp != null ? _loadedStamp.Rules : null,
                Exported = DateTime.Now,
                By = RevitUser.Current
            };
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "CSV (*.csv)|*.csv",
                    // The model first, as the panel's register already does, so a shared folder of
                    // several modellers' records sorts by model rather than all reading "clash-record".
                    FileName = CsvStamp.FileName("clash-record", stamp.Model, stamp.Exported),
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }
            catch (Exception ex)
            {
                Log.Warn("Record export failed", ex);
                SummaryText.Text = "Export failed — see " + Log.Path;
                return;
            }

            ExportTo(path, stamp);
        }

        /// <summary>Everything after the file has been chosen; split from the click handler so it can be
        /// driven without the system file dialog.</summary>
        /// <param name="path">Where to write the CSV.</param>
        /// <param name="stamp">Captured by the caller before the file dialog — see Export_Click.</param>
        private void ExportTo(string path, CsvStamp stamp)
        {
            try
            {
                int n = CsvExport.WriteRecord(path, RecOf(_all), stamp);
                SummaryText.Text = "Exported " + n + " record row(s) to " + path;
            }
            catch (Exception ex)
            {
                Log.Warn("Record export failed", ex);
                SummaryText.Text = "Export failed — see " + Log.Path;
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = ClashStatusStore.Instance.CurrentFile;
                if (string.IsNullOrEmpty(path)) path = ResolvedLog.CurrentFile;
                if (string.IsNullOrEmpty(path)) return;
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the record folder", ex);
            }
        }
    }
}
