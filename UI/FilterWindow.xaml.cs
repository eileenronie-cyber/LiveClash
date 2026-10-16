using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Chooses what each side of the check covers: categories, plus optional
    /// narrowing by keyword or by values picked from what the model actually
    /// contains.
    ///
    /// Modelled on the Federated Clash set builder. Keywords and picked values are
    /// OR'd, not ANDed — you either recognise the value in the Browse list, or you
    /// type a substring for a one-off. Nothing here touches the Revit API: the
    /// catalog is gathered beforehand, and the value scan is handed in as a delegate
    /// that the caller runs in API context.
    /// </summary>
    public partial class FilterWindow : Window
    {
        public class CategoryVm : INotifyPropertyChanged
        {
            public BuiltInCategory Category { get; set; }
            public string Name { get; set; }

            /// <summary>-1 when the model was not counted.</summary>
            public int Count { get; set; }

            /// <summary>The link(s) this category's elements come from, if any. Host is the
            /// unlabelled default, so a host-only category leaves this empty.</summary>
            public string ModelsText { get; set; }

            /// <summary>"Walls  (1,234)" — the count is what tells you whether a category is
            /// worth ticking at all — plus the link name(s) when a link contributes, so a
            /// discipline living only in a link ("Electrical Fixtures (538) — Electrical")
            /// is recognisable.</summary>
            public string Label
            {
                get
                {
                    var s = Count >= 0 ? Name + "   (" + Count.ToString("N0") + ")" : Name;
                    if (!string.IsNullOrEmpty(ModelsText)) s += "   — " + ModelsText;
                    return s;
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
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>Every offered category. The ObservableCollections below are the
        /// SEARCH-FILTERED views of these — ticks live here, so saving and counting
        /// must read these and never the view, or a search would silently discard
        /// everything scrolled out of sight.</summary>
        private readonly List<CategoryVm> _allSources = new List<CategoryVm>();
        private readonly List<CategoryVm> _allTargets = new List<CategoryVm>();

        private readonly ObservableCollection<CategoryVm> _sources = new ObservableCollection<CategoryVm>();
        private readonly ObservableCollection<CategoryVm> _targets = new ObservableCollection<CategoryVm>();

        // Point at the ACTIVE draft's filters; reassigned by ShowDraft when the user
        // switches sets. Not readonly any more precisely because the window now edits
        // several sets, one at a time.
        private ParameterFilter _srcFilter;
        private ParameterFilter _tgtFilter;

        private readonly List<ParameterInfo> _catalog;

        // ------------------------------------------------------------ clash-set drafts
        //
        // The window edits an in-window CLONE of every set, not the live Sets list. All
        // switching, renaming and rule editing happens on these drafts; only OK writes
        // them back to ClashSettings.Current. That is what keeps Cancel a true discard
        // even after the user has flipped between sets and edited several of them.

        private class SetDraft : INotifyPropertyChanged
        {
            private string _name = "";
            private bool _enabled = true;
            private bool _active;

            public string Name { get { return _name; } set { _name = value ?? ""; Raise("Name"); } }
            public bool Enabled { get { return _enabled; } set { _enabled = value; Raise("Enabled"); } }

            /// <summary>Highlights this set's chip. Exactly one draft is active.</summary>
            public bool IsActive { get { return _active; } set { _active = value; Raise("IsActive"); } }

            /// <summary>This set's clearance override, in feet. Null = inherit the model
            /// default — see ClashSet.ClearanceFeet, which this mirrors exactly.</summary>
            public double? ClearanceFeet;

            /// <summary>Mirrors ClashSet.InsulationAware exactly.</summary>
            public bool InsulationAware;

            public readonly HashSet<BuiltInCategory> SourceCats = new HashSet<BuiltInCategory>();
            public readonly HashSet<BuiltInCategory> TargetCats = new HashSet<BuiltInCategory>();
            public ParameterFilter SrcFilter;
            public ParameterFilter TgtFilter;

            /// <summary>Per-link control for this set — edited by the Links… dialog and
            /// written back on OK, so it saves and exports with the set.</summary>
            public readonly Dictionary<string, HashSet<BuiltInCategory>> LinkExclusions
                = new Dictionary<string, HashSet<BuiltInCategory>>();
            public readonly HashSet<string> DisabledLinks = new HashSet<string>();

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string p) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(p)); }
        }

        private readonly ObservableCollection<SetDraft> _drafts = new ObservableCollection<SetDraft>();
        private int _activeDraft;
        private bool _loadingDraft;   // suppresses the Enabled checkbox handler during ShowDraft

        // The model's limit, not the window's. It used to be declared here, which made it
        // a rule about the New Set button rather than about the tool: a settings file or
        // an imported preset naming more simply installed more. Enforced in
        // ClashSettings.FromLines now; this greys out the button to match.
        private const int MaxSets = ClashSettings.MaxSets;

        /// <summary>
        /// Runs the distinct-value scan. Supplied by the caller because it reads the
        /// document, and this window is not the right place to do that.
        /// </summary>
        private readonly Func<IEnumerable<BuiltInCategory>, IEnumerable<ParamRef>, bool, List<ValueChoice>> _scan;
        private readonly bool _isWorkshared;

        /// <summary>Loaded link name -> its element count per category, read in API context
        /// by FilterHandler. Empty when the model has no loaded links.</summary>
        private readonly Dictionary<string, Dictionary<BuiltInCategory, int>> _linkCounts;


        /// <summary>
        /// DESIGN-TIME ONLY, and kept on purpose. Nothing calls it at runtime, so it
        /// reads as dead — but the XAML designer cannot render a window that has no
        /// parameterless constructor, and losing that costs more than the two lines.
        /// </summary>
        public FilterWindow() : this(null, null, null) { }

        public FilterWindow(
            List<ParameterInfo> catalog,
            List<CategoryChoice> categories,
            Func<IEnumerable<BuiltInCategory>, IEnumerable<ParamRef>, bool, List<ValueChoice>> scan,
            bool isWorkshared = true,
            Dictionary<string, Dictionary<BuiltInCategory, int>> linkCounts = null)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            WorkArea.Keep(this);

            // Fill the scroll area when the window is taller than the columns' minimum, scroll when it is not.
            ColumnsScroll.SizeChanged += delegate { ColumnsGrid.Height = ColumnsScroll.ActualHeight; };
            _catalog = catalog;
            _scan = scan;
            _isWorkshared = isWorkshared;
            _linkCounts = linkCounts ?? new Dictionary<string, Dictionary<BuiltInCategory, int>>();

            var s = ClashSettings.Current;

            // One in-window draft per live set. Everything the window edits lives on
            // these clones until OK.
            foreach (var set in s.Sets) _drafts.Add(DraftFrom(set));
            if (_drafts.Count == 0) _drafts.Add(DraftFrom(new ClashSet()));
            _activeDraft = (s.ActiveSet >= 0 && s.ActiveSet < _drafts.Count) ? s.ActiveSet : 0;

            // Categories come from the DOCUMENT, under Revit's own names, so the list
            // matches Visibility/Graphics rather than a hand-picked subset. The category
            // ROWS are shared across every set — only the tick STATE differs per draft,
            // applied by ShowDraft.
            if (categories == null || categories.Count == 0)
            {
                categories = new List<CategoryChoice>();
                foreach (var c in ClashSettings.Selectable)
                    categories.Add(new CategoryChoice { Category = c, Name = ClashSettings.DisplayName(c) });
            }

            foreach (var c in categories)
            {
                _allSources.Add(new CategoryVm { Category = c.Category, Name = c.Name, Count = c.Count, ModelsText = c.ModelsText });
                _allTargets.Add(new CategoryVm { Category = c.Category, Name = c.Name, Count = c.Count, ModelsText = c.ModelsText });
            }
            SourceList.ItemsSource = _sources;
            TargetList.ItemsSource = _targets;

            FillParamNames();
            SourceRuleList.ItemsSource = _sourceRules;
            TargetRuleList.ItemsSource = _targetRules;
            SourceGroupList.ItemsSource = _sourceGroups;
            TargetGroupList.ItemsSource = _targetGroups;
            SourceAuditList.ItemsSource = _sourceAudit;
            TargetAuditList.ItemsSource = _targetAudit;

            SetStrip.ItemsSource = _drafts;

            // Loads ticks, filters, rule rows, reads-as, summary and the strip highlight
            // for the active set. Everything the old constructor did inline now runs here,
            // so it can run again on every set switch.
            ShowDraft(_activeDraft);
        }

        /// <summary>An editable in-window copy of one live set.</summary>
        private SetDraft DraftFrom(ClashSet set)
        {
            var d = new SetDraft
            {
                Name = set.Name, Enabled = set.Enabled,
                ClearanceFeet = set.ClearanceFeet, InsulationAware = set.InsulationAware
            };
            foreach (var c in set.SourceCategories) d.SourceCats.Add(c);
            foreach (var c in set.TargetCategories) d.TargetCats.Add(c);
            d.SrcFilter = Clone(set.SourceFilter);
            d.TgtFilter = Clone(set.TargetFilter);
            foreach (var kv in set.LinkExclusions)
                d.LinkExclusions[kv.Key] = new HashSet<BuiltInCategory>(kv.Value);
            foreach (var ln in set.DisabledLinks) d.DisabledLinks.Add(ln);
            return d;
        }

        /// <summary>
        /// Bind the visible controls to draft <paramref name="i"/>: tick its categories,
        /// point the filters at its clones, rebuild the rule rows, and light its chip.
        /// The inverse of <see cref="CommitVisibleToDraft"/>.
        /// </summary>
        private void ShowDraft(int i)
        {
            if (i < 0 || i >= _drafts.Count) return;
            _loadingDraft = true;
            try
            {
                _activeDraft = i;
                var d = _drafts[i];
                _srcFilter = d.SrcFilter;
                _tgtFilter = d.TgtFilter;

                foreach (var v in _allSources) v.IsChecked = d.SourceCats.Contains(v.Category);
                foreach (var v in _allTargets) v.IsChecked = d.TargetCats.Contains(v.Category);

                BuildRuleRows(true);
                BuildRuleRows(false);
                FilterCategoryLists();          // rebuilds the views and calls UpdateSummary
                RefreshReadsAs(true);
                RefreshReadsAs(false);

                for (int k = 0; k < _drafts.Count; k++) _drafts[k].IsActive = (k == i);
                if (SetEnabledCheck != null) SetEnabledCheck.IsChecked = d.Enabled;
                if (SetInsulationCheck != null) SetInsulationCheck.IsChecked = d.InsulationAware;
                if (SetClearanceCombo != null) SetClearanceCombo.SelectedIndex = ClearanceComboIndex(d.ClearanceFeet);
                UpdateSetButtons();
                UpdateLinksButton();
            }
            finally { _loadingDraft = false; }
        }

        /// <summary>Fold the visible edits back into the active draft, before switching
        /// away from it or on OK. The inverse of <see cref="ShowDraft"/>.</summary>
        private void CommitVisibleToDraft()
        {
            if (_activeDraft < 0 || _activeDraft >= _drafts.Count) return;
            var d = _drafts[_activeDraft];
            d.SourceCats.Clear();
            foreach (var v in _allSources) if (v.IsChecked) d.SourceCats.Add(v.Category);
            d.TargetCats.Clear();
            foreach (var v in _allTargets) if (v.IsChecked) d.TargetCats.Add(v.Category);
            WriteRulesBack(true);   // writes into _srcFilter, which IS d.SrcFilter
            WriteRulesBack(false);
        }

        private void UpdateSetButtons()
        {
            if (NewSetBtn != null) NewSetBtn.IsEnabled = _drafts.Count < MaxSets;
            if (DeleteSetBtn != null) DeleteSetBtn.IsEnabled = _drafts.Count > 1;
        }

        // ------------------------------------------------------------ set-strip handlers

        private void SetChip_Click(object sender, RoutedEventArgs e)
        {
            var d = ((FrameworkElement)sender).DataContext as SetDraft;
            if (d == null) return;
            int i = _drafts.IndexOf(d);
            if (i < 0 || i == _activeDraft) return;
            CommitVisibleToDraft();
            ShowDraft(i);
        }

        private void NewSet_Click(object sender, RoutedEventArgs e)
        {
            if (_drafts.Count >= MaxSets) return;
            CommitVisibleToDraft();

            var name = PromptName("New clash set", "Set " + (_drafts.Count + 1));
            if (name == null) return;

            var d = new SetDraft
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Set " + (_drafts.Count + 1) : name.Trim(),
                Enabled = true
            };
            // A fresh set starts wide — every category on both sides — matching the tool's
            // "catch everything, then narrow" default. The user tightens it from here.
            foreach (var c in ClashSettings.Selectable) { d.SourceCats.Add(c); d.TargetCats.Add(c); }
            d.SrcFilter = ParameterFilter.Default();
            d.TgtFilter = ParameterFilter.Default();

            _drafts.Add(d);
            ShowDraft(_drafts.Count - 1);
        }

        private void RenameSet_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDraft < 0 || _activeDraft >= _drafts.Count) return;
            var d = _drafts[_activeDraft];
            var name = PromptName("Rename clash set", d.Name);
            if (name == null) return;
            if (!string.IsNullOrWhiteSpace(name)) d.Name = name.Trim();
        }

        private void DeleteSet_Click(object sender, RoutedEventArgs e)
        {
            if (_drafts.Count <= 1) return;   // always keep at least one set
            var d = _drafts[_activeDraft];
            var answer = MessageBox.Show(this,
                "Delete clash set \"" + d.Name + "\"?\n\nIts categories and rules are removed. "
                + "Other sets are untouched.",
                "Live Clash", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            int idx = _activeDraft;
            _drafts.RemoveAt(idx);
            // ShowDraft (not CommitVisibleToDraft) — the visible controls still hold the
            // deleted set's data, and we must NOT fold that back into its neighbour.
            ShowDraft(idx >= _drafts.Count ? _drafts.Count - 1 : idx);
        }

        private void SetEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingDraft) return;
            if (_activeDraft < 0 || _activeDraft >= _drafts.Count) return;
            _drafts[_activeDraft].Enabled = SetEnabledCheck.IsChecked == true;
        }

        private void SetInsulation_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingDraft) return;
            if (_activeDraft < 0 || _activeDraft >= _drafts.Count) return;
            _drafts[_activeDraft].InsulationAware = SetInsulationCheck.IsChecked == true;
        }

        private void SetClearance_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingDraft) return;
            if (_activeDraft < 0 || _activeDraft >= _drafts.Count) return;
            _drafts[_activeDraft].ClearanceFeet = ClearanceForComboIndex(SetClearanceCombo.SelectedIndex);
        }

        /// <summary>mm→feet the same way Commands.SettingsCommand's ribbon dialog does —
        /// UnitUtils, not a hand-rolled constant, so a hand-edited value that happens to
        /// land off the four fixed choices reads back as Inherit rather than a near-miss
        /// silently snapping to the wrong one.</summary>
        private static int ClearanceComboIndex(double? feet)
        {
            if (!feet.HasValue) return 0;
            double mm = UnitUtils.ConvertFromInternalUnits(feet.Value, UnitTypeId.Millimeters);
            if (mm <= 0.01) return 1;
            if (Math.Abs(mm - 25) < 0.5) return 2;
            if (Math.Abs(mm - 50) < 0.5) return 3;
            if (Math.Abs(mm - 100) < 0.5) return 4;
            return 0;
        }

        private static double? ClearanceForComboIndex(int index)
        {
            switch (index)
            {
                case 1: return 0.0;
                case 2: return UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters);
                case 3: return UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
                case 4: return UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                default: return null;
            }
        }

        /// <summary>
        /// A tiny modal name prompt — WPF has no built-in InputBox, and both New and
        /// Rename need one. Returns the typed text, or null if cancelled.
        /// </summary>
        private string PromptName(string title, string initial)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                FontFamily = this.FontFamily,
                FontSize = 12
            };
            // The same look as the window it was opened from: its resources (the theme, light or dark) and the
            // shared button and text-box styles. Built in code, it had a white background and stock controls.
            dlg.Resources.MergedDictionaries.Add(this.Resources);
            dlg.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Bg");
            dlg.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Text");
            ThemeManager.Attach(dlg);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Name",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMut"),
                Margin = new Thickness(0, 0, 0, 4)
            });
            var tb = new TextBox
            {
                Text = initial ?? "",
                Height = 27,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(7, 0, 7, 0),
                Style = (Style)FindResource("LcTextBox")
            };
            panel.Children.Add(tb);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            string result = null;
            var ok = new Button { Content = "OK", Width = 78, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 6, 0), Style = (Style)FindResource("LcPrimaryButton") };
            ok.Click += (a, b) => { result = tb.Text; dlg.DialogResult = true; };
            var cancel = new Button { Content = "Cancel", Width = 78, Height = 28, IsCancel = true, Style = (Style)FindResource("LcButton") };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            panel.Children.Add(btns);

            dlg.Content = panel;
            dlg.Loaded += (a, b) => { tb.Focus(); tb.SelectAll(); };
            return dlg.ShowDialog() == true ? result : null;
        }

        /// <summary>
        /// Reconcile this window's edits with whatever changed underneath it.
        ///
        /// The window works on CLONES taken when it opened, and used to hand those
        /// clones straight back -- so it wrote a stale snapshot over the live filter.
        /// Anything added in the meantime was silently reverted. The audit's Drop list
        /// is exactly that: it writes family names into ExcludeKeyword on both filters,
        /// and a subsequent OK here would wipe them, which is why ticked audit rows
        /// came back unticked.
        ///
        /// So commit the user's DELTA rather than their snapshot: start from what is
        /// live now, subtract only what they deliberately deleted in this window, then
        /// add what they typed.
        /// </summary>
        private static ParameterFilter Clone(ParameterFilter f)
        {
            var c = new ParameterFilter();
            if (f == null) return c;
            c.Keyword = f.Keyword;
            c.ExcludeKeyword = f.ExcludeKeyword;
            foreach (var p in f.Parameters) c.Parameters.Add(new ParamRef { Name = p.Name, Scope = p.Scope });
            foreach (var v in f.Picked)
                c.Picked.Add(new ValuePick { ParameterName = v.ParameterName, Scope = v.Scope, Value = v.Value });
            foreach (var v in f.Excluded)
                c.Excluded.Add(new ValuePick { ParameterName = v.ParameterName, Scope = v.Scope, Value = v.Value });
            foreach (var v in f.ExcludedTypes)
                c.ExcludedTypes.Add(new TypeKey { Family = v.Family, Type = v.Type });
            foreach (var v in f.OnlyValues)
                c.OnlyValues.Add(new ValuePick { ParameterName = v.ParameterName, Scope = v.Scope, Value = v.Value });
            foreach (var r in f.Rules)
            {
                var nr = new Rule { ParameterName = r.ParameterName, Scope = r.Scope, Op = r.Op };
                foreach (var v in r.Values) nr.Values.Add(v);
                c.Rules.Add(nr);
            }
            foreach (var g in f.Groups)
            {
                var ng = new RuleGroup { Join = g.Join };
                foreach (var r in g.Rules)
                {
                    var nr = new Rule { ParameterName = r.ParameterName, Scope = r.Scope, Op = r.Op };
                    foreach (var v in r.Values) nr.Values.Add(v);
                    ng.Rules.Add(nr);
                }
                c.Groups.Add(ng);
            }
            return c;
        }

        private ParameterFilter FilterFor(bool source) { return source ? _srcFilter : _tgtFilter; }

        private List<BuiltInCategory> TickedCategories(bool source)
        {
            var list = new List<BuiltInCategory>();
            foreach (var v in source ? _allSources : _allTargets) if (v.IsChecked) list.Add(v.Category);
            return list;
        }

        // ------------------------------------------------------------ searching

        private void CategorySearch_Changed(object sender, TextChangedEventArgs e) { FilterCategoryLists(); }
        private void CategorySearch_Changed2(object sender, RoutedEventArgs e) { FilterCategoryLists(); }

        /// <summary>
        /// Label the Links button with what the ACTIVE set actually does with the loaded
        /// links, so the state is readable without opening the dialog — the whole reason
        /// it sits beside the target scope rather than in the footer.
        /// </summary>
        private void UpdateLinksButton()
        {
            if (LinksButton == null) return;

            int total = _linkCounts == null ? 0 : _linkCounts.Count;
            if (total == 0)
            {
                LinksButton.Content = "Links… (none loaded)";
                LinksButton.IsEnabled = false;
                return;
            }

            LinksButton.IsEnabled = true;
            var d = _drafts[_activeDraft];

            int off = 0, narrowed = 0;
            foreach (var kv in _linkCounts)
            {
                if (d.DisabledLinks.Contains(kv.Key)) { off++; continue; }
                HashSet<BuiltInCategory> excl;
                if (d.LinkExclusions.TryGetValue(kv.Key, out excl) && excl.Count > 0) narrowed++;
            }

            var bits = new List<string> { total + (total == 1 ? " link" : " links") };
            if (off > 0) bits.Add(off + " off");
            if (narrowed > 0) bits.Add(narrowed + " narrowed");
            LinksButton.Content = "Links… (" + string.Join(", ", bits.ToArray()) + ")";
        }

        /// <summary>
        /// Per-link control for the ACTIVE set. Lives here rather than on the panel so the
        /// choice belongs to the set, is saved with it, and travels in a preset.
        /// </summary>
        private void Links_Click(object sender, RoutedEventArgs e)
        {
            if (_linkCounts == null || _linkCounts.Count == 0)
            {
                MessageBox.Show(this,
                    "No loaded links in this model, so there is nothing to scope.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var d = _drafts[_activeDraft];

            var inputs = new List<LinkScopeWindow.LinkScopeInput>();
            foreach (var kv in _linkCounts)
            {
                HashSet<BuiltInCategory> excl;
                d.LinkExclusions.TryGetValue(kv.Key, out excl);
                inputs.Add(new LinkScopeWindow.LinkScopeInput
                {
                    Name = kv.Key,
                    CountByCategory = kv.Value,
                    Excluded = excl == null ? null : new HashSet<BuiltInCategory>(excl),
                    Disabled = d.DisabledLinks.Contains(kv.Key),
                });
            }

            var win = new LinkScopeWindow(inputs, d.Name) { Owner = this };
            if (win.ShowDialog() != true || !win.Committed) return;

            // Only a link actually SHOWN in the dialog (present in _linkCounts) was ever a
            // choice the user could make or leave alone -- a currently-unloaded link never
            // appears in _linkCounts (its document is null, so nothing can be counted) and
            // so can never come back in win.Result/DisabledResult either. Clearing both
            // dictionaries outright used to wipe that link's saved exclusions/disabled flag
            // the moment this dialog was opened and OK'd, even though the user never saw it.
            // Removing only the links that WERE shown, before re-adding from the dialog's
            // result, leaves an unloaded link's settings untouched. Found reviewing the
            // whole codebase, 2026-09-27.
            foreach (var name in _linkCounts.Keys)
            {
                d.LinkExclusions.Remove(name);
                d.DisabledLinks.Remove(name);
            }
            foreach (var kv in win.Result) d.LinkExclusions[kv.Key] = kv.Value;
            foreach (var ln in win.DisabledResult) d.DisabledLinks.Add(ln);

            UpdateLinksButton();

            UpdateSummary();
        }

        private void FilterCategoryLists()
        {
            Fill(_sources, _allSources, SourceCatSearch, SourceShowEmpty.IsChecked == true);
            Fill(_targets, _allTargets, TargetCatSearch, TargetShowEmpty.IsChecked == true);
            UpdateSummary();
        }

        private static void Fill(ObservableCollection<CategoryVm> view, List<CategoryVm> all,
                                TextBox search, bool showEmpty)
        {
            var q = search == null ? "" : (search.Text ?? "").Trim();
            view.Clear();
            foreach (var v in all)
            {
                // A category with nothing in it cannot clash. Ticked ones are always
                // shown regardless — hiding a setting the user already made would
                // mean silently dropping it on OK.
                if (!showEmpty && v.Count == 0 && !v.IsChecked) continue;
                if (q.Length > 0 && v.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                view.Add(v);
            }
        }

        // ------------------------------------------------------------ parameters

        // ---------------------------------------------------------------- browse

        /// <summary>
        /// Pick values from what the model actually holds. The same browser serves
        /// Keep and Drop -- the only difference is which list it fills.
        /// </summary>
        private bool IsWorkshared() { return _isWorkshared; }

        /// <summary>Clears one line only. Keep and Drop clear independently, so
        /// wiping a stale exclusion cannot also throw away the inclusion.</summary>
        private void MirrorSourceToTarget_Click(object sender, RoutedEventArgs e) { Mirror(true); }
        private void MirrorTargetToSource_Click(object sender, RoutedEventArgs e) { Mirror(false); }

        /// <summary>
        /// Copy one side's narrow-down rules onto the other.
        ///
        /// A rule set on one panel only is the single most convincing way to make this
        /// tool look broken. Excluding ten worksets on the target side while the source
        /// side had no workset rule at all left 1,186 source elements on excluded
        /// worksets still being checked, and 132 of them reached the register -- which
        /// reads exactly like a filter that leaks, because from the outside it is one.
        ///
        /// Categories are deliberately NOT copied. The two sides check different things
        /// by design -- three pipe categories against thirty-four target categories is a
        /// normal setup -- and the existing "Match targets" button already handles that
        /// case for anyone who wants it.
        /// </summary>
        private void Mirror(bool fromSource)
        {
            var from = RulesFor(fromSource);
            var to = RulesFor(!fromSource);

            int editable = from.Count;
            int groupN = GroupsFor(fromSource).Count;
            var answer = MessageBox.Show(this,
                "Copy " + editable + " rule(s)"
                + (groupN > 0 ? " and " + groupN + " group(s)" : "")
                + " from the " + (fromSource ? "sources" : "targets")
                + " side onto the " + (fromSource ? "targets" : "sources") + " side?"
                + Environment.NewLine + Environment.NewLine
                + "This replaces the other side's rules and groups. Categories are left alone.",
                "Live Clash", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            to.Clear();

            int insert = 0;
            foreach (var r in from)
            {
                var nr = new RuleRowVm { ParameterName = r.ParameterName, Scope = r.Scope };
                nr.OperatorLabel = r.OperatorLabel;
                foreach (var v in r.Values) nr.Values.Add(v);
                nr.ValuesChanged();
                to.Insert(insert++, nr);
            }

            // Groups too, or the two sides silently disagree after a copy.
            var toG = GroupsFor(!fromSource);
            toG.Clear();
            foreach (var g in GroupsFor(fromSource))
            {
                var ng = new GroupVm { Join = g.Join };
                foreach (var r in g.Rules)
                {
                    var nr = new RuleRowVm { ParameterName = r.ParameterName, Scope = r.Scope };
                    nr.OperatorLabel = r.OperatorLabel;
                    foreach (var v in r.Values) nr.Values.Add(v);
                    nr.ValuesChanged();
                    ng.Rules.Add(nr);
                }
                toG.Add(ng);
            }

            RefreshReadsAs(!fromSource);
            UpdateSummary();
        }

        // ------------------------------------------------------------ categories

        private static void SetAll(IEnumerable<CategoryVm> list, bool on)
        {
            foreach (var v in list) v.IsChecked = on;
        }

        private void SourceAll_Click(object sender, RoutedEventArgs e) { SetAll(_sources, true); UpdateSummary(); }
        private void SourceNone_Click(object sender, RoutedEventArgs e) { SetAll(_sources, false); UpdateSummary(); }
        private void TargetAll_Click(object sender, RoutedEventArgs e) { SetAll(_targets, true); UpdateSummary(); }
        private void TargetNone_Click(object sender, RoutedEventArgs e) { SetAll(_targets, false); UpdateSummary(); }

        /// <summary>Mirror the target ticks onto the sources, paired by CATEGORY —
        /// index pairing breaks the moment either list is search-filtered.</summary>
        private void SourceMineOnly_Click(object sender, RoutedEventArgs e)
        {
            var wanted = new HashSet<int>();
            foreach (var t in _allTargets) if (t.IsChecked) wanted.Add((int)t.Category);
            foreach (var s in _allSources) s.IsChecked = wanted.Contains((int)s.Category);
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int ns = 0, nt = 0;
            foreach (var v in _allSources) if (v.IsChecked) ns++;
            foreach (var v in _allTargets) if (v.IsChecked) nt++;
            if (SummaryText == null || _srcFilter == null || _tgtFilter == null) return;
            SummaryText.Text = "sources: " + ns + " categories, " + _srcFilter.Summary()
                             + "   ·   targets: " + nt + " categories, " + _tgtFilter.Summary();
            SummaryText.ToolTip = SummaryText.Text;   // the footer trims a long summary; this is the whole of it
        }

        // ------------------------------------------------------------- presets

        /// <summary>
        /// Write what is on screen, not what is saved — exporting should capture the
        /// filter you are looking at, including edits you have not pressed OK on.
        /// </summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            string path;
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export the filter",
                    Filter = "Live Clash filter (*" + FilterPreset.Extension + ")|*" + FilterPreset.Extension
                           + "|All files (*.*)|*.*",
                    DefaultExt = FilterPreset.Extension,
                    FileName = (ClashSettings.ModelName ?? "LiveClash") + "-filter" + FilterPreset.Extension
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }
            catch (Exception ex) { ReportExportFailure(ex); return; }

            ExportTo(path);
        }

        /// <summary>
        /// Everything after the file has been chosen. Split from the click handler so the part
        /// that decides what happens to a file can be driven without the system file dialog,
        /// which cannot be answered from a test.
        /// </summary>
        private void ExportTo(string path)
        {
            try
            {
                FilterPreset.Export(path, Staged(), ClashSettings.ModelName);
                MessageBox.Show(this, "Filter exported to\n\n" + path,
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { ReportExportFailure(ex); }
        }

        private void ReportExportFailure(Exception ex)
        {
            Log.Error("Filter export failed", ex);
            MessageBox.Show(this, "Could not export: " + ex.Message,
                "Live Clash", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            string path;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import a filter",
                    Filter = "Live Clash filter (*" + FilterPreset.Extension + ")|*" + FilterPreset.Extension
                           + "|All files (*.*)|*.*",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }
            catch (Exception ex) { ReportImportFailure(ex); return; }

            ImportFile(path);
        }

        /// <summary>
        /// Everything after the file has been chosen: read it, say what it will do, and only on
        /// OK stage it. Split from the click handler so this decision - which replaces the whole
        /// filter - can be driven without the system file dialog.
        /// </summary>
        private void ImportFile(string path)
        {
            try
            {
                ClashSettings preset;
                var sum = FilterPreset.Read(path, out preset);
                if (!sum.Valid)
                {
                    MessageBox.Show(this, sum.Problem, "Live Clash",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Say what it will do before it does it. A preset replaces the whole
                // filter, and losing an afternoon of tuning to a mis-click would be a
                // poor trade for one dialog.
                var answer = MessageBox.Show(this,
                    "Replace the current filter with this preset?\n\n"
                    // A preset now carries every set, so say how many are arriving —
                    // "3 sets" replacing your one is a different decision from "a filter".
                    + (sum.Sets > 1
                        ? "  sets    : " + sum.Sets + ", replacing all " + _drafts.Count
                            + " of yours (counts below are its active set)\n"
                        : "")
                    // Said out loud, because it is a difference between what the sender
                    // has and what the importer will get. A preset from a newer or
                    // hand-edited source can name more sets than this tool holds.
                    + (sum.DeclaredSets > sum.Sets
                        ? "  NOTE    : the file names " + sum.DeclaredSets + " sets; only the first "
                            + sum.Sets + " will be imported (the limit is "
                            + ClashSettings.MaxSets + ")\n"
                        : "")
                    + "  sources : " + sum.SourceCategories + " categories, " + sum.SourceRules + "\n"
                    + "  targets : " + sum.TargetCategories + " categories, " + sum.TargetRules + "\n"
                    + (sum.AuditDrops > 0 ? "  audit   : " + sum.AuditDrops + " family+type drop(s), merged into every set\n" : "")
                    + (string.IsNullOrEmpty(sum.ExportedFrom) ? "" : "\n  from    : " + sum.ExportedFrom + "\n")
                    + "\nClash results and review decisions are untouched.",
                    "Live Clash", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;

                _pending = preset;
                DialogResult = true;
                Close();
            }
            catch (Exception ex) { ReportImportFailure(ex); }
        }

        private void ReportImportFailure(Exception ex)
        {
            Log.Error("Filter import failed", ex);
            MessageBox.Show(this, "Could not import: " + ex.Message,
                "Live Clash", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Set when an import was accepted; the caller applies it.</summary>
        private ClashSettings _pending;
        public ClashSettings Imported { get { return _pending; } }

        /// <summary>The filter as the window currently shows it.</summary>
        private void AddSourceRule_Click(object sender, RoutedEventArgs e) { AddRule(true); }
        private void AddTargetRule_Click(object sender, RoutedEventArgs e) { AddRule(false); }

        private void AddRule(bool source)
        {
            var vm = new RuleRowVm();
            vm.ParameterName = ParamNames.Count > 0 ? ParamNames[0] : "";
            vm.Scope = ScopeForParam(vm.ParameterName);
            vm.OperatorLabel = "is one of";
            RulesFor(source).Add(vm);
            RefreshReadsAs(source);
            UpdateSummary();
        }

        private void RemoveRule_Click(object sender, RoutedEventArgs e)
        {
            var vm = ((FrameworkElement)sender).DataContext as RuleRowVm;
            if (vm == null) return;
            bool source; GroupVm group;
            if (!Locate(vm, out source, out group)) return;
            if (group != null) group.Rules.Remove(vm);
            else RulesFor(source).Remove(vm);
            RefreshReadsAs(source);
            UpdateSummary();
        }

        // ------------------------------------------------------------------ OR groups

        private void AddSourceGroup_Click(object sender, RoutedEventArgs e) { AddGroup(true); }
        private void AddTargetGroup_Click(object sender, RoutedEventArgs e) { AddGroup(false); }

        private void AddGroup(bool source)
        {
            var g = new GroupVm();
            g.Rules.Add(NewRuleRow());   // seed with one rule so the box is usable
            GroupsFor(source).Add(g);
            RefreshReadsAs(source);
            UpdateSummary();
        }

        private void AddGroupRule_Click(object sender, RoutedEventArgs e)
        {
            var g = ((FrameworkElement)sender).DataContext as GroupVm;
            if (g == null) return;
            g.Rules.Add(NewRuleRow());
            RefreshReadsAs(_sourceGroups.Contains(g));
            UpdateSummary();
        }

        private void RemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            var g = ((FrameworkElement)sender).DataContext as GroupVm;
            if (g == null) return;
            bool source = _sourceGroups.Contains(g);
            GroupsFor(source).Remove(g);
            RefreshReadsAs(source);
            UpdateSummary();
        }

        private RuleRowVm NewRuleRow()
        {
            var vm = new RuleRowVm();
            vm.ParameterName = ParamNames.Count > 0 ? ParamNames[0] : "";
            vm.Scope = ScopeForParam(vm.ParameterName);
            vm.OperatorLabel = "is one of";
            return vm;
        }

        private void RuleParam_Changed(object sender, SelectionChangedEventArgs e)
        {
            var vm = ((FrameworkElement)sender).DataContext as RuleRowVm;
            if (vm == null) return;
            vm.Scope = ScopeForParam(vm.ParameterName);
        }

        private void BrowseRule_Click(object sender, RoutedEventArgs e)
        {
            var vm = ((FrameworkElement)sender).DataContext as RuleRowVm;
            if (vm == null) return;
            bool source; GroupVm grp;
            if (!Locate(vm, out source, out grp)) return;

            if (_scan == null)
            {
                MessageBox.Show(this, "Browsing needs an open model.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(vm.ParameterName))
            {
                MessageBox.Show(this, "Pick a parameter for this rule first.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var cats = TickedCategories(source);
            if (cats.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one category on this side first.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pref = new List<ParamRef> { new ParamRef { Name = vm.ParameterName, Scope = vm.Scope } };
            Func<bool, List<ValueChoice>> run = force =>
            {
                try
                {
                    System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                    return _scan(cats, pref, force);
                }
                finally { System.Windows.Input.Mouse.OverrideCursor = null; }
            };

            var choices = run(false);
            if (choices == null || choices.Count == 0)
            {
                MessageBox.Show(this, "No values found for that parameter in the ticked categories.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var seed = new List<ValuePick>();
            foreach (var v in vm.Values) seed.Add(new ValuePick { ParameterName = vm.ParameterName, Scope = vm.Scope, Value = v });

            var win = new ValueBrowserWindow(choices, seed, run) { Owner = this };
            if (win.ShowDialog() != true) return;

            vm.Values.Clear();
            foreach (var r in win.Result) vm.Values.Add(r.Value);
            vm.ValuesChanged();

            if (vm.Operator == RuleOp.Is
                && string.Equals(vm.ParameterName, "Workset", StringComparison.OrdinalIgnoreCase)
                && !IsWorkshared())
                MessageBox.Show(this,
                    "This model is not workshared, so no element has a Workset value. An "
                    + "\"is\" Workset rule will keep nothing.",
                    "Live Clash", MessageBoxButton.OK, MessageBoxImage.Warning);

            RefreshReadsAs(source);
            UpdateSummary();
        }

        private void RefreshReadsAs(bool source)
        {
            var box = source ? SourceReadsAs : TargetReadsAs;
            if (box == null) return;
            var rows = RulesFor(source);
            var groups = GroupsFor(source);
            int audit = AuditFor(source).Count;

            // The "AND" connector only makes sense between flat rules and a group.
            var andRow = source ? SourceAndRow : TargetAndRow;
            if (andRow != null)
                andRow.Visibility = (rows.Count > 0 && groups.Count > 0)
                    ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            if (rows.Count == 0 && groups.Count == 0 && audit == 0)
            { box.Text = "keeps everything in the ticked categories"; return; }

            var parts = new List<string>();
            foreach (var r in rows) parts.Add(RuleReadable(r));
            foreach (var g in groups) parts.Add(GroupReadable(g));
            if (audit > 0) parts.Add(audit + " audit drop" + (audit == 1 ? "" : "s"));
            box.Text = "keep where " + string.Join("  AND  ", parts.ToArray());
        }

        private static string GroupReadable(GroupVm g)
        {
            var inner = new List<string>();
            foreach (var r in g.Rules)
                if (!string.IsNullOrWhiteSpace(r.ParameterName)) inner.Add(RuleReadable(r));
            if (inner.Count == 0) return "( )";
            var joiner = g.Join == RuleJoin.Or ? "  OR  " : "  AND  ";
            return "(" + string.Join(joiner, inner.ToArray()) + ")";
        }

        private static string RuleReadable(RuleRowVm r)
        {
            if (r.Operator == RuleOp.IsEmpty || r.Operator == RuleOp.IsNotEmpty)
                return r.ParameterName + " " + r.OperatorLabel;
            var vals = new List<string>(r.Values);
            var shown = vals.Count > 3
                ? string.Join(", ", vals.GetRange(0, 3).ToArray()) + ", +" + (vals.Count - 3)
                : string.Join(", ", vals.ToArray());
            return r.ParameterName + " " + r.OperatorLabel + " {" + shown + "}";
        }

        /// <summary>
        /// Rebuild a settings object's Sets list from the drafts. The draft filter objects
        /// are handed straight over — they are already private clones this window owns.
        ///
        /// Shared by OK and by Export. It used to exist only inside Ok_Click, and Export
        /// built its own single-set object instead, which is how a three-set matrix
        /// exported as one set even after FilterPreset learned to carry all of them.
        /// </summary>
        private void FillSetsFromDrafts(ClashSettings s)
        {
            s.Sets.Clear();
            foreach (var d in _drafts)
            {
                var set = new ClashSet
                {
                    Name = d.Name, Enabled = d.Enabled,
                    ClearanceFeet = d.ClearanceFeet, InsulationAware = d.InsulationAware
                };
                set.SourceCategories.Clear();
                foreach (var c in d.SourceCats) set.SourceCategories.Add(c);
                set.TargetCategories.Clear();
                foreach (var c in d.TargetCats) set.TargetCategories.Add(c);
                set.SourceFilter = d.SrcFilter;
                set.TargetFilter = d.TgtFilter;
                set.LinkExclusions.Clear();
                foreach (var kv in d.LinkExclusions)
                    set.LinkExclusions[kv.Key] = new HashSet<BuiltInCategory>(kv.Value);
                set.DisabledLinks.Clear();
                foreach (var ln in d.DisabledLinks) set.DisabledLinks.Add(ln);
                s.Sets.Add(set);
            }
            s.ActiveSet = (_activeDraft >= 0 && _activeDraft < s.Sets.Count) ? _activeDraft : 0;
        }

        /// <summary>
        /// What is on screen, as a settings object — EVERY set, not just the one being
        /// looked at. Exporting a three-set matrix has to export three sets; carrying only
        /// the active one is what FilterPreset was fixed to stop doing, and this is the
        /// caller that was still doing it.
        /// </summary>
        private ClashSettings Staged()
        {
            // Fold the visible ticks and rules into the active draft first, so an edit you
            // have not pressed OK on still exports.
            CommitVisibleToDraft();

            var staged = new ClashSettings();
            FillSetsFromDrafts(staged);

            var live = ClashSettings.Current;
            staged.ClearanceFeet = live.ClearanceFeet;
            staged.SuppressConnectedPairs = live.SuppressConnectedPairs;
            staged.CheckHostVsHost = live.CheckHostVsHost;
            staged.CheckHostVsLinks = live.CheckHostVsLinks;
            // Audit drops are global, not part of the edited set — carry the live ones so an
            // Export includes them (a preset must reproduce the result it came from).
            staged.MergeAuditDrops(live.AuditDrops);
            return staged;
        }

        // ------------------------------------------------------------ rule rows (new)

        /// <summary>
        /// One editable row of the rule list: a parameter, an operator, and its values.
        /// Audit rows (family+type pairs) are shown locked -- they are edited in the
        /// Audit window, not here.
        /// </summary>
        public class RuleRowVm : INotifyPropertyChanged
        {
            private string _param = "";
            private string _opLabel = "is one of";

            public ParamScope Scope = ParamScope.Either;
            public ObservableCollection<string> Values { get; } = new ObservableCollection<string>();

            public string ParameterName
            {
                get { return _param; }
                set { _param = value ?? ""; Raise("ParameterName"); }
            }

            public string OperatorLabel
            {
                get { return _opLabel; }
                set { _opLabel = value ?? "is one of"; Raise("OperatorLabel"); Raise("NeedsValues"); Raise("BrowseVis"); }
            }

            public bool NeedsValues
            {
                get { return _opLabel != "is empty" && _opLabel != "is not empty"; }
            }
            public string ValuesText { get { return string.Join(", ", Values); } }
            public void ValuesChanged() { Raise("ValuesText"); }

            // Converter-free visibility, so the XAML needs no resources.
            //
            // CanEdit / EditVis / LockVis are gone with IsAudit. They dated from when
            // audit drops lived inside the filter as locked rows; drops became global,
            // nothing ever set IsAudit again, and so CanEdit was permanently true,
            // EditVis permanently Visible and the lock glyph permanently Collapsed.
            public System.Windows.Visibility BrowseVis
            { get { return NeedsValues ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed; } }

            public RuleOp Operator
            {
                get { return LabelToOp(_opLabel); }
                set { OperatorLabel = OpToLabel(value); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string p) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(p)); }
        }

        /// <summary>
        /// A bracketed OR/AND set of rule rows. Bound to one box in the UI; ANDed with
        /// the flat rules when written back, which is what gives "(A OR B) AND C".
        /// </summary>
        public class GroupVm : INotifyPropertyChanged
        {
            private string _joinLabel = "any";
            public ObservableCollection<RuleRowVm> Rules { get; } = new ObservableCollection<RuleRowVm>();

            /// <summary>"any" = OR, "all" = AND. Bound to the group's combo.</summary>
            public string JoinLabel
            {
                get { return _joinLabel; }
                set { _joinLabel = value == "all" ? "all" : "any"; Raise("JoinLabel"); }
            }

            public RuleJoin Join
            {
                get { return _joinLabel == "all" ? RuleJoin.And : RuleJoin.Or; }
                set { JoinLabel = value == RuleJoin.And ? "all" : "any"; }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string p) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(p)); }
        }

        public string[] JoinOptions { get { return new[] { "any", "all" }; } }

        public static readonly string[] OperatorLabels =
            { "is one of", "is not", "contains", "does not contain", "is empty", "is not empty" };

        private static string OpToLabel(RuleOp op)
        {
            switch (op)
            {
                case RuleOp.IsNot: return "is not";
                case RuleOp.Contains: return "contains";
                case RuleOp.DoesNotContain: return "does not contain";
                case RuleOp.IsEmpty: return "is empty";
                case RuleOp.IsNotEmpty: return "is not empty";
                default: return "is one of";
            }
        }
        private static RuleOp LabelToOp(string label)
        {
            switch (label)
            {
                case "is not": return RuleOp.IsNot;
                case "contains": return RuleOp.Contains;
                case "does not contain": return RuleOp.DoesNotContain;
                case "is empty": return RuleOp.IsEmpty;
                case "is not empty": return RuleOp.IsNotEmpty;
                default: return RuleOp.Is;
            }
        }

        private readonly ObservableCollection<RuleRowVm> _sourceRules = new ObservableCollection<RuleRowVm>();
        private readonly ObservableCollection<RuleRowVm> _targetRules = new ObservableCollection<RuleRowVm>();
        private ObservableCollection<RuleRowVm> RulesFor(bool source) { return source ? _sourceRules : _targetRules; }

        private readonly ObservableCollection<GroupVm> _sourceGroups = new ObservableCollection<GroupVm>();
        private readonly ObservableCollection<GroupVm> _targetGroups = new ObservableCollection<GroupVm>();
        private ObservableCollection<GroupVm> GroupsFor(bool source) { return source ? _sourceGroups : _targetGroups; }

        /// <summary>
        /// A fingerprint of one side's rule editor taken right after <see cref="BuildRuleRows"/>
        /// loads it — compared in <see cref="WriteRulesBack"/> to tell "the user reviewed and
        /// changed nothing" from "the user edited this side" (found reviewing the whole
        /// codebase, 2026-09-26: see WriteRulesBack for the bug this guards against).
        /// </summary>
        private string _sourceRulesBaseline, _targetRulesBaseline;

        private string RuleEditorSignature(bool source)
        {
            var sb = new StringBuilder();
            foreach (var vm in RulesFor(source)) AppendRuleSignature(sb, vm);
            sb.Append("|G");
            foreach (var g in GroupsFor(source))
            {
                sb.Append('[').Append(g.Join);
                foreach (var vm in g.Rules) AppendRuleSignature(sb, vm);
                sb.Append(']');
            }
            return sb.ToString();
        }

        private static void AppendRuleSignature(StringBuilder sb, RuleRowVm vm)
        {
            sb.Append('|').Append(vm.ParameterName).Append('|').Append(vm.Scope).Append('|').Append(vm.Operator);
            foreach (var v in vm.Values) sb.Append(',').Append(v);
        }

        /// <summary>
        /// Find where a rule row lives: which side, and which group (null = flat list).
        /// A row's handlers cannot assume it is in the flat list any more.
        /// </summary>
        private bool Locate(RuleRowVm vm, out bool source, out GroupVm group)
        {
            source = false; group = null;
            if (_sourceRules.Contains(vm)) { source = true; return true; }
            if (_targetRules.Contains(vm)) { source = false; return true; }
            foreach (var g in _sourceGroups) if (g.Rules.Contains(vm)) { source = true; group = g; return true; }
            foreach (var g in _targetGroups) if (g.Rules.Contains(vm)) { source = false; group = g; return true; }
            return false;
        }

        private readonly ObservableCollection<string> _sourceAudit = new ObservableCollection<string>();
        private readonly ObservableCollection<string> _targetAudit = new ObservableCollection<string>();
        private ObservableCollection<string> AuditFor(bool source) { return source ? _sourceAudit : _targetAudit; }

        /// <summary>Parameter names offered in a rule row's picker, from the catalog.</summary>
        public ObservableCollection<string> ParamNames { get; } = new ObservableCollection<string>();
        public string[] OperatorOptions { get { return OperatorLabels; } }

        private void FillParamNames()
        {
            ParamNames.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_catalog != null)
                foreach (var p in _catalog)
                    if (!string.IsNullOrEmpty(p.Name) && seen.Add(p.Name)) ParamNames.Add(p.Name);
            // Workset is always meaningful and is not always in the parameter catalog.
            if (seen.Add("Workset")) ParamNames.Insert(0, "Workset");
        }

        private ParamScope ScopeForParam(string name)
        {
            if (string.Equals(name, "Workset", StringComparison.OrdinalIgnoreCase)) return ParamScope.Instance;
            if (_catalog != null)
                foreach (var p in _catalog)
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Scope;
            return ParamScope.Either;
        }

        /// <summary>Convert one side's filter into editable rule rows + locked audit rows.</summary>
        private void BuildRuleRows(bool source)
        {
            var rows = RulesFor(source);
            rows.Clear();

            var f = FilterFor(source);
            // RulesForDisplay shows f.Rules when present and only migrates a legacy
            // filter -- never straight to TryToRules, which ignored f.Rules and let the
            // window blank (then wipe) a new-format filter. See its doc comment.
            var rules = f.RulesForDisplay();
            foreach (var r in rules)
            {
                if (!string.IsNullOrEmpty(r.ParameterName) && !ParamNames.Contains(r.ParameterName))
                    ParamNames.Add(r.ParameterName);
                var vm = new RuleRowVm { ParameterName = r.ParameterName, Scope = r.Scope };
                vm.Operator = r.Op;
                foreach (var v in r.Values) vm.Values.Add(v);
                rows.Add(vm);
            }

            // OR/AND groups, each rendered as its own box.
            var groups = GroupsFor(source);
            groups.Clear();
            foreach (var g in f.Groups)
            {
                var gvm = new GroupVm { Join = g.Join };
                foreach (var r in g.Rules)
                {
                    if (!string.IsNullOrEmpty(r.ParameterName) && !ParamNames.Contains(r.ParameterName))
                        ParamNames.Add(r.ParameterName);
                    var vm = new RuleRowVm { ParameterName = r.ParameterName, Scope = r.Scope };
                    vm.Operator = r.Op;
                    foreach (var v in r.Values) vm.Values.Add(v);
                    gvm.Rules.Add(vm);
                }
                groups.Add(gvm);
            }

            // Audit family+type drops are isolated: a collapsed expander, not rows in
            // the editable list, so they stop drowning the rules the user can change.
            // They are GLOBAL now (ClashSettings.AuditDrops) — applied to every set, both
            // sides — so both the source and target expander show that one list, and it
            // is managed in the Audit window, not here.
            var audit = AuditFor(source);
            audit.Clear();
            foreach (var t in ClashSettings.Current.AuditDrops) audit.Add(t.ToString());
            SetAuditSummary(source);

            var baseline = RuleEditorSignature(source);
            if (source) _sourceRulesBaseline = baseline; else _targetRulesBaseline = baseline;
        }

        private void SetAuditSummary(bool source)
        {
            var exp = source ? SourceAuditExpander : TargetAuditExpander;
            if (exp == null) return;
            int n = AuditFor(source).Count;
            exp.Header = n + " audit drop" + (n == 1 ? "" : "s") + " — managed in the Audit window";
            exp.Visibility = n > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        /// <summary>Write the editable rule rows back onto one side's filter.</summary>
        private void WriteRulesBack(bool source)
        {
            var f = FilterFor(source);

            // A side still running on the legacy Keyword/Picked/Excluded/OnlyValues fields
            // does not always migrate to Rules exactly — an OR across DIFFERENT parameters
            // (keyword-or-pick, or picks on more than one parameter) has no flat-AND
            // equivalent (see ParameterFilter.TryToRules) and the rule editor can show it
            // incompletely, even empty. Below, WriteRulesBack unconditionally clears the
            // legacy fields and replaces them with whatever the editor currently shows —
            // which used to run even when the user opened this side, changed nothing, and
            // clicked OK, silently discarding a filter the editor could not fully display
            // and leaving the set matching every element in its categories. Skip the
            // destructive rewrite entirely when nothing was edited since BuildRuleRows
            // loaded it: the legacy fields already work (ParameterFilter.Matches falls
            // back to them whenever Rules and Groups are both empty), so there is nothing
            // to gain and a real filter to lose. Found reviewing the whole codebase, 2026-09-27.
            if (f.Rules.Count == 0 && f.Groups.Count == 0)
            {
                var baseline = source ? _sourceRulesBaseline : _targetRulesBaseline;
                if (baseline != null && baseline == RuleEditorSignature(source)) return;
            }

            // Rules become the single representation. Clear the legacy include/exclude/
            // only fields so they cannot double-apply; ExcludedTypes (the audit's) is
            // left exactly as it was -- the locked rows are display only.
            f.Keyword = "";
            f.ExcludeKeyword = "";
            f.Picked.Clear();
            f.Excluded.Clear();
            f.OnlyValues.Clear();

            f.Rules.Clear();
            foreach (var vm in RulesFor(source))
            {
                if (string.IsNullOrWhiteSpace(vm.ParameterName)) continue;
                var r = new Rule
                {
                    ParameterName = vm.ParameterName,
                    Scope = vm.Scope,
                    Op = vm.Operator
                };
                foreach (var v in vm.Values) r.Values.Add(v);
                f.Rules.Add(r);
            }

            f.Groups.Clear();
            foreach (var gvm in GroupsFor(source))
            {
                var g = new RuleGroup { Join = gvm.Join };
                foreach (var vm in gvm.Rules)
                {
                    if (string.IsNullOrWhiteSpace(vm.ParameterName)) continue;
                    var r = new Rule { ParameterName = vm.ParameterName, Scope = vm.Scope, Op = vm.Operator };
                    foreach (var v in vm.Values) r.Values.Add(v);
                    g.Rules.Add(r);
                }
                if (g.Rules.Count > 0) f.Groups.Add(g);   // an empty group narrows nothing; drop it
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            CommitVisibleToDraft();

            // Every ENABLED set must be a real pair — both sides non-empty — or it can
            // never report anything and the empty side is almost always a mistake. A
            // disabled set is allowed to be half-finished; it is not being run.
            for (int i = 0; i < _drafts.Count; i++)
            {
                var d = _drafts[i];
                if (!d.Enabled) continue;
                if (d.SourceCats.Count == 0 || d.TargetCats.Count == 0)
                {
                    ShowDraft(i);
                    MessageBox.Show(this,
                        "Set \"" + d.Name + "\" has an empty "
                        + (d.SourceCats.Count == 0 ? "sources" : "targets")
                        + " side. A clash is a pair, so an enabled set needs at least one "
                        + "category on each side. Add one, or switch the set off.",
                        "Live Clash", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var s = ClashSettings.Current;
            FillSetsFromDrafts(s);

            DialogResult = true;
            Close();
        }
    }
}
