using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Shows what the audit found, lets the user NAME it, and turns a tick into a
    /// filter entry.
    ///
    /// The tick and the name are both the point. Every finding here was originally
    /// chased by hand and then typed into the filter as a keyword; doing the finding
    /// and the exclusion in one gesture means the reason an exclusion exists is never
    /// lost. Letting the label be edited means the reason is in the project's own
    /// words rather than mine.
    /// </summary>
    public partial class AuditWindow : Window
    {
        public class Row : INotifyPropertyChanged
        {
            public ProvisionalFinding Finding;

            public string CategoryName { get { return Finding.CategoryName; } }
            public string FamilyName { get { return Finding.FamilyName; } }
            public string TypeName { get { return Finding.TypeName; } }
            public int InstanceCount { get { return Finding.InstanceCount; } }
            public string Evidence { get { return Finding.Evidence; } }

            /// <summary>The link(s) this finding is in — blank for the host, so a linked
            /// opening/void is recognisable as coming from Architectural/Structural/etc.</summary>
            public string Model { get { return Finding.ModelsText; } }

            /// <summary>What the detector called it, kept so a rename can be undone.</summary>
            public string DetectedAs
            {
                get { return "Detected as: " + ProvisionalAudit.DefaultLabel(Finding.Kind); }
            }

            public string Label
            {
                get { return Finding.Label; }
                set
                {
                    var v = (value ?? "").Trim();
                    if (v.Length == 0 || v == Finding.Label) return;
                    Finding.Label = v;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs("Label"));
                    if (Renamed != null) Renamed(this, EventArgs.Empty);
                }
            }

            private bool _checked;
            public bool IsChecked
            {
                get { return _checked; }
                set
                {
                    if (_checked == value) return;
                    _checked = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs("IsChecked"));
                    if (Ticked != null) Ticked(this, EventArgs.Empty);
                }
            }

            /// <summary>
            /// Set once the user changes this tick, never by the initial state.
            /// Apply revokes a drop only for rows carrying this, so a row the search
            /// hid can never be silently un-dropped by Tick all / Tick none.
            /// </summary>
            public bool UserToggled;

            public event PropertyChangedEventHandler PropertyChanged;
            public event EventHandler Renamed;
            public event EventHandler Ticked;
        }

        private readonly List<Row> _all = new List<Row>();
        private readonly ObservableCollection<Row> _view = new ObservableCollection<Row>();

        /// <summary>Bound by the Why column's combo. Grows when the user invents a name.</summary>
        public ObservableCollection<string> LabelNames { get; private set; }

        /// <summary>Exact family+type pairs the user ticked. Applied by the caller in API context.</summary>
        public List<TypeKey> Dropped = new List<TypeKey>();

        /// <summary>
        /// Family names of findings the user UNticked that are currently dropped.
        /// Without this the window is one-way: it can add an exclusion but never take
        /// one back, so unticking would appear to do nothing.
        /// </summary>
        public List<TypeKey> Undropped = new List<TypeKey>();

        /// <summary>Exact pairs currently dropped.</summary>
        private List<TypeKey> _alreadyDroppedTypes = new List<TypeKey>();

        /// <summary>
        /// Legacy SUBSTRING drops from <c>ExcludeKeyword</c>. Still honoured so a row
        /// covered by one renders ticked rather than lying, but a tick never writes
        /// one any more -- see <see cref="TypeKey"/> for why.
        /// </summary>
        private List<string> _alreadyDropped = new List<string>();

        /// <summary>
        /// Is this finding covered by the live Drop list? Matched the same way the
        /// filter matches — substring, case-insensitive — so the window agrees with
        /// the engine rather than with its own idea of equality.
        /// </summary>
        private bool IsDropped(ProvisionalFinding f)
        {
            return f != null && (DroppedExactly(f) || DroppedByKeyword(f));
        }

        /// <summary>Covered by an exact family+type pair -- removable from this window.</summary>
        private bool DroppedExactly(ProvisionalFinding f)
        {
            if (f == null) return false;
            var fam = ParameterFilter.Norm(f.FamilyName);
            var typ = ParameterFilter.Norm(f.TypeName);
            foreach (var k in _alreadyDroppedTypes)
            {
                if (k == null || ParameterFilter.Norm(k.Family) != fam) continue;
                var kt = ParameterFilter.Norm(k.Type);
                if (kt.Length == 0 || kt == typ) return true;
            }
            return false;
        }

        /// <summary>
        /// Covered by a legacy substring keyword. Reported honestly as dropped, but
        /// unticking cannot lift it -- the keyword may cover rows this window never
        /// showed, so removing it here could silently un-hide them.
        /// </summary>
        private bool DroppedByKeyword(ProvisionalFinding f)
        {
            if (f == null) return false;
            var hay = ((f.FamilyName ?? "") + " " + (f.TypeName ?? "")).ToLowerInvariant();
            foreach (var k in _alreadyDropped)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (hay.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }


        /// <summary>
        /// DESIGN-TIME ONLY, and kept on purpose — see the note on FilterWindow's.
        /// </summary>
        public AuditWindow() : this(new List<ProvisionalFinding>(), null, null) { }

        public AuditWindow(List<ProvisionalFinding> findings, List<string> alreadyDropped,
                           List<TypeKey> alreadyDroppedTypes)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            WorkArea.Keep(this);
            DataContext = this;

            var store = AuditLabelStore.Instance;
            LabelNames = new ObservableCollection<string>(store.LabelNames);
            _alreadyDropped = alreadyDropped ?? new List<string>();
            _alreadyDroppedTypes = alreadyDroppedTypes ?? new List<TypeKey>();

            foreach (var f in findings ?? new List<ProvisionalFinding>())
            {
                if (string.IsNullOrEmpty(f.Label)) f.Label = ProvisionalAudit.DefaultLabel(f.Kind);

                // A tick means "this is dropped", so it has to start from what is
                // ACTUALLY dropped — not from what the label would default to. Reading
                // only the default meant that reopening the window after applying
                // showed everything unticked again, which looks exactly like the
                // settings never took.
                var row = new Row { Finding = f, IsChecked = IsDropped(f) || store.DropByDefault(f.Label) };
                row.Renamed += Row_Renamed;
                row.Ticked += delegate { row.UserToggled = true; UpdateSummary(); };
                _all.Add(row);
            }

            Grid.ItemsSource = _view;
            Refill();
        }

        /// <summary>
        /// A name the user typed that is not in the list becomes a real label, so the
        /// next row can pick it from the dropdown instead of retyping it.
        /// </summary>
        private void Row_Renamed(object sender, EventArgs e)
        {
            var row = sender as Row;
            if (row == null) return;

            var name = row.Label;
            if (string.IsNullOrEmpty(name)) return;

            bool known = false;
            foreach (var n in LabelNames)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { known = true; break; }

            if (!known)
            {
                AuditLabelStore.Instance.Ensure(name, DropDefault.IsChecked == true);
                LabelNames.Add(name);
            }

            UpdateSummary();
        }

        private void Refill()
        {
            var q = (Search == null ? "" : (Search.Text ?? "")).Trim();
            _view.Clear();
            foreach (var r in _all)
            {
                if (q.Length > 0)
                {
                    var hay = r.CategoryName + " " + r.FamilyName + " " + r.TypeName + " "
                            + r.Label + " " + r.Evidence;
                    if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                _view.Add(r);
            }
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (SummaryText == null) return;

            int ticked = 0, instances = 0;
            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _all)
            {
                if (r.IsChecked) { ticked++; instances += r.InstanceCount; }
                int n;
                labels[r.Label] = labels.TryGetValue(r.Label, out n) ? n + 1 : 1;
            }

            SummaryText.Text = _all.Count + " finding(s) under " + labels.Count + " label(s)"
                             + ", showing " + _view.Count
                             + "   ·   " + ticked + " ticked covering "
                             + instances.ToString("N0") + " element(s)";
        }

        private void Search_Changed(object sender, TextChangedEventArgs e) { Refill(); }

        private void All_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _view) r.IsChecked = true;
            UpdateSummary();
        }

        private void None_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _view) r.IsChecked = false;
            UpdateSummary();
        }

        /// <summary>
        /// Re-derive every tick, after renaming labels in bulk.
        ///
        /// This used to set the tick from the label default ALONE, which quietly
        /// undid real work: Placeholder and No connector default to false, so one
        /// click unticked every one of those rows even when the family was actively
        /// excluded — and because applying is two-way, the next Drop the ticked then
        /// REMOVED them from both filters. The rule has to be the same one the window
        /// opens with: a tick means dropped, and what is already dropped stays ticked.
        /// </summary>
        private void Retick_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit();
            var store = AuditLabelStore.Instance;
            foreach (var r in _all)
                r.IsChecked = IsDropped(r.Finding) || store.DropByDefault(r.Label);
            UpdateSummary();
        }

        /// <summary>
        /// Remember the labels without dropping anything. Naming what you are looking
        /// at is useful on its own — you may want the record and none of the filtering.
        /// </summary>
        private void SaveLabels_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit();
            StoreAssignments();
            AuditLabelStore.Instance.Save();
            MessageBox.Show(this, "Labels saved against this model.",
                "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Record every label that differs from what the detector guessed. Only the
        /// differences: storing the rest would freeze today's guess and stop an
        /// improved detector from ever being seen.
        /// </summary>
        private void StoreAssignments()
        {
            var store = AuditLabelStore.Instance;
            foreach (var r in _all)
            {
                var f = r.Finding;
                var byDetector = ProvisionalAudit.DefaultLabel(f.Kind);
                store.Assign(f.Category, f.FamilyName, f.TypeName,
                    string.Equals(f.Label, byDetector, StringComparison.OrdinalIgnoreCase) ? null : f.Label);
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // A combo still being edited has not written back yet.
            Grid.CommitEdit();

            // Read _all, never _view: a search must not silently discard ticks that
            // are scrolled out of sight.
            Dropped.Clear();
            Undropped.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unseen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stuck = new List<string>();
            var stuckSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int instances = 0;
            foreach (var r in _all)
            {
                var fam = r.FamilyName ?? "";
                var typ = r.TypeName ?? "";
                if (fam.Length == 0 && typ.Length == 0) continue;

                // Family AND type together. Keying on the family alone is what made one
                // tick on a Pre Action Valve also drop the fire water tanks and the pumps.
                var key = fam + (char)31 + typ;

                if (r.IsChecked)
                {
                    // Already held by a legacy keyword: nothing to add, and writing a
                    // pair too would duplicate a rule the user cannot see from here.
                    if (!DroppedByKeyword(r.Finding) && seen.Add(key))
                        Dropped.Add(new TypeKey { Family = fam, Type = typ });
                    instances += r.InstanceCount;
                }
                else if (DroppedExactly(r.Finding))
                {
                    // Unticked something held by a pair: that pair is ours to remove --
                    // but only if the user actually unticked it. Tick all / Tick none
                    // reach only the rows a search left visible, and this method reads
                    // _all, so without the flag every hidden row would read as a
                    // deliberate revocation.
                    if (r.UserToggled && unseen.Add(key))
                        Undropped.Add(new TypeKey { Family = fam, Type = typ });
                }
                else if (r.UserToggled && DroppedByKeyword(r.Finding))
                {
                    // Unticked, but held by a substring keyword that may also cover rows
                    // this window never listed. Removing it here could silently un-hide
                    // them, so report it rather than appear to do nothing.
                    if (stuckSeen.Add(key)) stuck.Add(fam + " : " + typ);
                }
            }

            // Pairs are per row, so a family with some types ticked and some not now
            // resolves correctly on its own; no family-level reconciliation is needed.
            if (stuck.Count > 0)
            {
                var nl = Environment.NewLine;
                var shown = stuck.GetRange(0, Math.Min(12, stuck.Count)).ToArray();
                MessageBox.Show(this,
                    "These " + stuck.Count + " row(s) were unticked, but a keyword rule is what "
                    + "drops them, not this window:" + nl + nl + "  "
                    + string.Join(nl + "  ", shown)
                    + (stuck.Count > 12 ? nl + "  ... and " + (stuck.Count - 12) + " more" : "")
                    + nl + nl
                    + "A keyword can cover families this audit never listed, so unticking here "
                    + "cannot lift it. Edit the Drop line in Filter to do that.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
            }


            StoreAssignments();
            AuditLabelStore.Instance.Save();

            if (Dropped.Count == 0 && Undropped.Count == 0)
            {
                MessageBox.Show(this,
                    "Labels saved. The Drop list already matches what is ticked, so nothing changed.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var what = new List<string>();
            if (Dropped.Count > 0)
                what.Add("drop " + Dropped.Count + " family+type(s), about "
                         + instances.ToString("N0") + " element(s)");
            if (Undropped.Count > 0)
                what.Add("stop dropping " + Undropped.Count + " family+type(s)");

            var answer = MessageBox.Show(this,
                "Update the Drop list?\n\n  " + string.Join("\n  ", what.ToArray())
                + "\n\nNothing is deleted from the model, and the Drop line stays editable in Filter….",
                "Live Clash", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            DialogResult = true;
            Close();
        }
    }
}
