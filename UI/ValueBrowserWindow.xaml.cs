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
    /// The Browse list: distinct parameter values found in the model, with counts,
    /// ticked to build an exact-match set.
    ///
    /// This is the half that makes a parameter filter usable. You rarely know the
    /// exact string to type, but you recognise it at once in a list — and the count
    /// tells you whether you picked the value covering 900 elements or the one
    /// covering three that somebody mistyped.
    /// </summary>
    public partial class ValueBrowserWindow : Window
    {
        public class Row : INotifyPropertyChanged
        {
            public ValueChoice Choice;
            public string Display { get { return Choice.Display; } }

            public string Detail
            {
                get
                {
                    var d = Choice.ParamLabel + "  ·  " + Choice.Count + " element" + (Choice.Count == 1 ? "" : "s");
                    var m = Choice.ModelsText;
                    if (!string.IsNullOrEmpty(m)) d += "  ·  in " + m;
                    return d;
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
                    if (Changed != null) Changed();
                }
            }

            public Action Changed;
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<Row> _all = new List<Row>();
        private readonly ObservableCollection<Row> _shown = new ObservableCollection<Row>();

        /// <summary>Values the user ticked. Empty unless they pressed Add picked.</summary>
        public List<ValuePick> Result { get; private set; }

        /// <summary>Re-runs the scan against the model. Null when unavailable.</summary>
        private readonly Func<bool, List<ValueChoice>> _rescan;

        public ValueBrowserWindow(List<ValueChoice> choices, List<ValuePick> alreadyPicked)
            : this(choices, alreadyPicked, null) { }

        public ValueBrowserWindow(List<ValueChoice> choices, List<ValuePick> alreadyPicked,
                                  Func<bool, List<ValueChoice>> rescan)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            WorkArea.Keep(this);
            Result = new List<ValuePick>();
            _rescan = rescan;

            var already = new HashSet<string>();
            if (alreadyPicked != null)
                foreach (var p in alreadyPicked)
                    already.Add(p.Scope + "::" + (p.ParameterName ?? "").ToLowerInvariant()
                                + "::" + ParameterFilter.Norm(p.Value));

            if (choices != null)
            {
                foreach (var c in choices)
                {
                    var row = new Row { Choice = c };
                    row.Changed = UpdateSummary;
                    // Pre-tick what is already in the set, so Browse reads as editing
                    // the picks rather than only ever adding to them.
                    row.IsChecked = already.Contains(
                        c.Scope + "::" + c.ParameterName.ToLowerInvariant()
                        + "::" + ParameterFilter.Norm(c.Display));
                    _all.Add(row);
                }
            }

            ValueList.ItemsSource = _shown;
            ApplyFilter();
        }

        /// <summary>
        /// Reads the model again. The list is cached between openings because a
        /// whole-model scan is seconds, not milliseconds — but the model does move
        /// underneath it, so refreshing has to be available on demand.
        /// Ticked values are preserved across the rescan.
        /// </summary>
        private void Rescan_Click(object sender, RoutedEventArgs e)
        {
            if (_rescan == null) { RescanButton.IsEnabled = false; return; }

            var keep = new HashSet<string>();
            foreach (var r in _all)
                if (r.IsChecked)
                    keep.Add(r.Choice.Scope + "::" + r.Choice.ParameterName.ToLowerInvariant()
                             + "::" + ParameterFilter.Norm(r.Choice.Display));

            List<ValueChoice> fresh;
            try { fresh = _rescan(true); }
            catch (Exception ex) { Log.Warn("Rescan failed", ex); return; }

            _all.Clear();
            foreach (var c in fresh)
            {
                var row = new Row { Choice = c };
                row.Changed = UpdateSummary;
                row.IsChecked = keep.Contains(
                    c.Scope + "::" + c.ParameterName.ToLowerInvariant()
                    + "::" + ParameterFilter.Norm(c.Display));
                _all.Add(row);
            }
            ApplyFilter();
        }

        private void Search_Changed(object sender, TextChangedEventArgs e) { ApplyFilter(); }

        private void ApplyFilter()
        {
            var q = SearchBox == null ? "" : (SearchBox.Text ?? "").Trim();
            _shown.Clear();
            foreach (var r in _all)
            {
                if (q.Length > 0
                    && r.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && r.Choice.ParameterName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _shown.Add(r);
            }
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int ticked = 0;
            foreach (var r in _all) if (r.IsChecked) ticked++;
            if (SummaryText != null)
                SummaryText.Text = ticked + " ticked  ·  showing " + _shown.Count + " of " + _all.Count;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Result = new List<ValuePick>();
            foreach (var r in _all)
            {
                if (!r.IsChecked) continue;
                Result.Add(new ValuePick
                {
                    ParameterName = r.Choice.ParameterName,
                    Scope = r.Choice.Scope,
                    Value = r.Choice.Display
                });
            }
            DialogResult = true;
            Close();
        }
    }
}
