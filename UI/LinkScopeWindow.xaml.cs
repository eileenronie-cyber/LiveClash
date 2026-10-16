using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Autodesk.Revit.DB;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Per-link control for ONE clash set: which links it checks against, and which
    /// categories each of those links contributes. Opened from the Filter window, so the
    /// choice is part of the set and is saved with it. The per-link element counts are
    /// read in API context before the Filter window opens and handed in here.
    /// </summary>
    public partial class LinkScopeWindow : Window
    {
        /// <summary>One link's row: an include gate plus the categories it holds.</summary>
        public class LinkVm : INotifyPropertyChanged
        {
            /// <summary>Bindable — WPF cannot bind to a field.</summary>
            public string Name { get; set; }

            public ObservableCollection<CatVm> Categories { get; } = new ObservableCollection<CatVm>();

            /// <summary>Categories excluded for this link that the list is not showing (the
            /// link holds none of them now). Preserved untouched so toggling never drops them.</summary>
            public HashSet<BuiltInCategory> HiddenExclusions = new HashSet<BuiltInCategory>();

            private bool _include = true;
            /// <summary>The gate: check this link at all in this set.</summary>
            public bool Include
            {
                get { return _include; }
                set
                {
                    if (_include == value) return;
                    _include = value;
                    Fire("Include");
                    Fire("HeaderSuffix");
                    if (Changed != null) Changed();
                }
            }

            private bool _expanded;
            public bool Expanded
            {
                get { return _expanded; }
                set { _expanded = value; Fire("Expanded"); }
            }

            public string HeaderSuffix
            {
                get
                {
                    if (!Include) return "  (not checked in this set)";
                    int on = 0, total = Categories.Count;
                    foreach (var c in Categories) if (c.Collect) on++;
                    return total == 0 ? "  (no checkable elements)"
                        : on == total ? "  (all " + total + " categories)"
                        : "  (" + on + " of " + total + " categories)";
                }
            }

            /// <summary>Raised when anything on this link changes, so the window can re-summarise.</summary>
            public System.Action Changed;

            public void RefreshHeader() { Fire("HeaderSuffix"); }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Fire(string n)
            {
                var h = PropertyChanged;
                if (h != null) h(this, new PropertyChangedEventArgs(n));
            }
        }

        public class CatVm : INotifyPropertyChanged
        {
            public BuiltInCategory Category;
            public string DisplayName;
            public int Count;
            public LinkVm Owner;

            public string Label { get { return DisplayName + "   (" + Count.ToString("N0") + ")"; } }

            private bool _collect = true;
            public bool Collect
            {
                get { return _collect; }
                set
                {
                    if (_collect == value) return;
                    _collect = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs("Collect"));
                    if (Owner != null)
                    {
                        Owner.RefreshHeader();
                        if (Owner.Changed != null) Owner.Changed();
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<LinkVm> _links = new List<LinkVm>();

        /// <summary>Set on OK: link name -> categories to EXCLUDE from it in this set.</summary>
        public Dictionary<string, HashSet<BuiltInCategory>> Result { get; private set; }

        /// <summary>Set on OK: links this set does not check against at all.</summary>
        public HashSet<string> DisabledResult { get; private set; }

        public bool Committed { get; private set; }

        /// <param name="links">Per loaded link: name, element counts by category, the
        /// categories currently excluded, and whether the set checks it at all.</param>
        /// <param name="setName">The set being edited, for the header.</param>
        public LinkScopeWindow(List<LinkScopeInput> links, string setName)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            WorkArea.Keep(this);

            if (!string.IsNullOrEmpty(setName))
                Title = "Live Clash — links for \"" + setName + "\"";

            foreach (var input in links ?? new List<LinkScopeInput>())
            {
                var lvm = new LinkVm { Name = input.Name };
                lvm.Changed = UpdateSummary;
                var shown = new HashSet<BuiltInCategory>();

                // Biggest contributors first — those are what you reach for.
                var ordered = new List<KeyValuePair<BuiltInCategory, int>>(input.CountByCategory);
                ordered.Sort(delegate (KeyValuePair<BuiltInCategory, int> a, KeyValuePair<BuiltInCategory, int> b)
                {
                    int c = b.Value.CompareTo(a.Value);
                    return c != 0 ? c : string.CompareOrdinal(
                        ClashSettings.DisplayName(a.Key), ClashSettings.DisplayName(b.Key));
                });

                foreach (var kv in ordered)
                {
                    if (kv.Value <= 0) continue;
                    shown.Add(kv.Key);
                    var cvm = new CatVm
                    {
                        Category = kv.Key,
                        DisplayName = ClashSettings.DisplayName(kv.Key),
                        Count = kv.Value,
                        Owner = lvm,
                    };
                    cvm.Collect = input.Excluded == null || !input.Excluded.Contains(kv.Key);
                    lvm.Categories.Add(cvm);
                }

                if (input.Excluded != null)
                    foreach (var c in input.Excluded)
                        if (!shown.Contains(c)) lvm.HiddenExclusions.Add(c);

                lvm.Include = !input.Disabled;
                lvm.Expanded = lvm.Include && lvm.Categories.Count > 0 && HasExclusion(lvm);
                _links.Add(lvm);
            }

            LinkList.ItemsSource = _links;
            UpdateSummary();
        }

        private static bool HasExclusion(LinkVm l)
        {
            if (l.HiddenExclusions.Count > 0) return true;
            foreach (var c in l.Categories) if (!c.Collect) return true;
            return false;
        }

        private void LinkAll_Click(object sender, RoutedEventArgs e)
        {
            var l = ((FrameworkElement)sender).Tag as LinkVm;
            if (l == null) return;
            foreach (var c in l.Categories) c.Collect = true;
            l.RefreshHeader();
            UpdateSummary();
        }

        private void LinkNone_Click(object sender, RoutedEventArgs e)
        {
            var l = ((FrameworkElement)sender).Tag as LinkVm;
            if (l == null) return;
            foreach (var c in l.Categories) c.Collect = false;
            l.RefreshHeader();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (SummaryText == null) return;

            int off = 0, linksWithExcl = 0, totalExcl = 0;
            foreach (var l in _links)
            {
                if (!l.Include) { off++; continue; }
                int n = l.HiddenExclusions.Count;
                foreach (var c in l.Categories) if (!c.Collect) n++;
                if (n > 0) { linksWithExcl++; totalExcl += n; }
            }

            var bits = new List<string>();
            bits.Add(_links.Count + " link(s)");
            if (off > 0) bits.Add(off + " not checked");
            bits.Add(totalExcl == 0
                ? "every category collected"
                : totalExcl + " category exclusion(s) across " + linksWithExcl + " link(s)");
            SummaryText.Text = string.Join(" — ", bits.ToArray());
            SummaryText.ToolTip = SummaryText.Text;   // the footer trims a long summary; this is the whole of it
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Result = new Dictionary<string, HashSet<BuiltInCategory>>();
            DisabledResult = new HashSet<string>();

            foreach (var l in _links)
            {
                if (!l.Include) { DisabledResult.Add(l.Name); }

                // Keep category exclusions even for a link that is switched off, so
                // switching it back on restores the narrowing rather than resetting it.
                var excl = new HashSet<BuiltInCategory>(l.HiddenExclusions);
                foreach (var c in l.Categories) if (!c.Collect) excl.Add(c.Category);
                if (excl.Count > 0) Result[l.Name] = excl;
            }

            Committed = true;
            DialogResult = true;
            Close();
        }

        /// <summary>What the caller gathers per link and passes in.</summary>
        public class LinkScopeInput
        {
            public string Name;
            public Dictionary<BuiltInCategory, int> CountByCategory;
            public HashSet<BuiltInCategory> Excluded;
            public bool Disabled;
        }
    }
}
