using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// A small read-only summary of the current clash register, built as a compact
    /// example of the four WPF pieces:
    ///   XAML layout         -- GlanceWindow.xaml
    ///   data binding        -- Rows / SeverityLine, bound in the XAML
    ///   two-way binding     -- the "Hard only" checkbox drives HardOnly, which rebuilds
    ///   a button handler    -- Refresh_Click, below
    ///
    /// The window is its own view-model: DataContext = this, and it raises
    /// PropertyChanged so bound controls update when a property changes. Read-only,
    /// so it can never alter a clash or the model.
    /// </summary>
    public partial class GlanceWindow : Window, INotifyPropertyChanged
    {
        /// <summary>One line of the list: a category pairing and how many times it clashed.</summary>
        public class GlanceRow
        {
            public string Label { get; set; }
            public int Count { get; set; }
        }

        /// <summary>The list the ItemsControl binds to. Observable, so add/clear repaints.</summary>
        public ObservableCollection<GlanceRow> Rows { get; } = new ObservableCollection<GlanceRow>();

        private string _severityLine = "";
        public string SeverityLine
        {
            get { return _severityLine; }
            private set { _severityLine = value; Raise("SeverityLine"); }
        }

        private bool _hardOnly;
        public bool HardOnly
        {
            get { return _hardOnly; }
            set { _hardOnly = value; Raise("HardOnly"); Rebuild(); }   // toggling re-filters live
        }

        public GlanceWindow()
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            WorkArea.Keep(this);
            DataContext = this;   // this is what makes {Binding Rows} read this.Rows
            Rebuild();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Rebuild(); }

        /// <summary>Read the register, aggregate, and push the result into the bound properties.</summary>
        private void Rebuild()
        {
            List<ClashResult> all;
            try { all = ClashResultStore.Instance.Snapshot(); }
            catch { all = new List<ClashResult>(); }

            int hard = all.Count(r => r.Severity == ClashSeverity.Hard);
            int clear = all.Count(r => r.Severity == ClashSeverity.Clearance);
            int unver = all.Count(r => r.Severity == ClashSeverity.Unverified);
            SeverityLine = all.Count + " clashes   ·   " + hard + " hard, "
                         + clear + " clearance, " + unver + " unverified";

            var shown = _hardOnly ? all.Where(r => r.Severity == ClashSeverity.Hard) : all;

            var grouped = shown
                .GroupBy(r => (r.HostCategory ?? "?") + "  vs  " + (r.OtherCategory ?? "?"))
                .Select(g => new GlanceRow { Label = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count);

            Rows.Clear();
            foreach (var row in grouped) Rows.Add(row);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
