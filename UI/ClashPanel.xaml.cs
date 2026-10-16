using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LiveClash.Core;

// WPF has its own internal ElementId; the panel means Revit's.
using ElementId = Autodesk.Revit.DB.ElementId;

namespace LiveClash.UI
{
    public partial class ClashPanel : UserControl
    {
        private readonly List<ClashRowVm> _rows = new List<ClashRowVm>();

        // Column-header sort. Null = the natural grouping order (the default); a header
        // name here re-orders the groups by that column. "Found" defaults to newest-first.
        private string _sortCol;
        private bool _sortDesc;

        private ExternalEvent _showEvent;
        private ShowElementHandler _showHandler;
        private ExternalEvent _fullCheckEvent;
        private FullCheckHandler _fullCheckHandler;
        private ExternalEvent _recheckEvent;
        private RecheckHandler _recheckHandler;
        private ExternalEvent _filterEvent;
        private FilterHandler _filterHandler;
        private ExternalEvent _auditEvent;
        private AuditHandler _auditHandler;
        private ExternalEvent _focusEvent;
        private FocusHandler _focusHandler;
        private ExternalEvent _recordRecheckEvent;
        private RecordRecheckHandler _recordRecheckHandler;

        private bool _refreshQueued;

        /// <summary>
        /// Why the list is empty. An empty list means completely different things, and
        /// saying so is the difference between "the model is clean" and "the tool is
        /// broken" — a user drew a clash, changed the filter, saw nothing, and reasonably
        /// concluded live checking did not work. It cleared the results, and Live only ever
        /// checks what you EDIT, so nothing was going to reappear on its own.
        /// </summary>
        public enum EmptyReason
        {
            /// <summary>Nothing swept yet this session.</summary>
            NotCheckedYet,
            /// <summary>A full check ran and genuinely found nothing.</summary>
            CheckedAndClean,
            /// <summary>The filter changed, so previous results were discarded.</summary>
            FilterChanged,
            /// <summary>Audit drops changed, same situation.</summary>
            DropsChanged,
            /// <summary>The clearance tolerance changed, same situation.</summary>
            ClearanceChanged,
            /// <summary>The user pressed Clear.</summary>
            Cleared,
            /// <summary>The active model changed, so results and filter were reloaded for it.</summary>
            DocumentSwitched,
        }

        private EmptyReason _emptyReason = EmptyReason.NotCheckedYet;

        /// <summary>Tell the panel why the list is about to be empty. See EmptyReason.</summary>
        public void SetEmptyReason(EmptyReason reason)
        {
            _emptyReason = reason;

            // The results were just thrown away because a SETTING changed. A "what changed" baseline (or summary) taken under
            // the old settings would make the next full check report the settings' difference as the link's.
            if (reason == EmptyReason.FilterChanged || reason == EmptyReason.DropsChanged || reason == EmptyReason.ClearanceChanged)
                ClashChangeTracker.Reset();
            try
            {
                if (Dispatcher.CheckAccess()) UpdateEmptyBanner();
                else Dispatcher.BeginInvoke((Action)UpdateEmptyBanner);
            }
            catch { }
        }

        /// <summary>Show the banner only when there is nothing to look at, and say why.</summary>
        private void UpdateEmptyBanner()
        {
            if (EmptyBanner == null) return;

            if (_rows.Count > 0)
            {
                EmptyBanner.Visibility = Visibility.Collapsed;
                _bannerMode = BannerMode.Normal;               // the banner is hidden, and its button goes back to what it normally is
                EmptyAction.Content = "Run full check";
                return;
            }

            // The list is empty because of what was typed, not because there is nothing to show.
            if (_searchNotMatching > 0 && ClashSearch.IsActive(SearchQuery))
            {
                _bannerMode = BannerMode.Search;
                EmptyTitle.Text = "No clash matches \u201c" + SearchQuery.Trim() + "\u201d";
                EmptyDetail.Text = _searchNotMatching + " clash" + (_searchNotMatching == 1 ? " is" : "es are")
                                 + " listed, but none has every one of those words in an element name, category, model, set or note. "
                                 + "Clear the search to see them again.";
                EmptyAction.Content = "Clear search";
                EmptyAction.Visibility = Visibility.Visible;
                EmptyBanner.Visibility = Visibility.Visible;
                return;
            }
            // "Only new" is on and nothing is new: every clash listed was already there.
            if (_notNew > 0 && OnlyNewCheck.IsChecked == true && ClashChangeTracker.Current != null)
            {
                _bannerMode = BannerMode.OnlyNew;
                EmptyTitle.Text = "Nothing new";
                EmptyDetail.Text = "Every clash listed was already there before " + ClashChangeTracker.Current.LinkText
                                 + " changed. Show all to see them.";
                EmptyAction.Content = "Show all";
                EmptyAction.Visibility = Visibility.Visible;
                EmptyBanner.Visibility = Visibility.Visible;
                return;
            }
            _bannerMode = BannerMode.Normal;
            EmptyAction.Content = "Run full check";

            string title, detail;
            bool offerRun = true;

            switch (_emptyReason)
            {
                case EmptyReason.FilterChanged:
                    title = "Results cleared — the filter changed";
                    detail = "Clashes found under the old filter are not valid for the new one, "
                           + "so they were discarded. Live checking only looks at elements as you "
                           + "edit them, so anything modelled earlier will not reappear on its own.";
                    break;
                case EmptyReason.DropsChanged:
                    title = "Results cleared — the audit drops changed";
                    detail = "What counts as a real clash has changed, so the old results were "
                           + "discarded. Run a full check to re-evaluate the model.";
                    break;
                case EmptyReason.ClearanceChanged:
                    title = "Results cleared — the clearance changed";
                    detail = "Whether a near-miss counts as a clash depends on this setting, so the "
                           + "old results were discarded rather than left showing verdicts from the "
                           + "old tolerance. Run a full check to re-evaluate the model.";
                    break;
                case EmptyReason.DocumentSwitched:
                    title = "Results cleared — you switched model";
                    detail = "Clashes, filter and triage are per model, so this list now belongs "
                           + "to the model you just activated. Its own filter and decisions are "
                           + "loaded; sweep it to list its clashes.";
                    break;
                case EmptyReason.Cleared:
                    title = "Cleared";
                    detail = "Live checking will list new clashes as you model. To see what is "
                           + "already there, sweep the model.";
                    break;
                case EmptyReason.CheckedAndClean:
                    title = "No clashes found";
                    detail = "The last full check found nothing in the current filter. Live "
                           + "checking stays on and will list anything you introduce.";
                    offerRun = false;
                    break;
                default:
                    title = "No results yet";
                    detail = "Live checking lists clashes as you model, but it does not scan what "
                           + "is already there. Sweep the model once to start from a real baseline.";
                    break;
            }

            EmptyTitle.Text = title;
            EmptyDetail.Text = detail;
            EmptyAction.Visibility = offerRun ? Visibility.Visible : Visibility.Collapsed;
            EmptyBanner.Visibility = Visibility.Visible;
        }

        /// <summary>How long a just-fixed clash stays visible, struck through.</summary>
        private static readonly TimeSpan ResolvedWindow = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Drives the resolved rows off the list once their window expires. Nothing
        /// else would trigger a refresh — the store stops changing the moment the
        /// clash is gone, so without this the struck-through row would sit there.
        /// </summary>
        private DispatcherTimer _resolvedTimer;

        // ------------------------------------------------------------- what changed

        private bool _updatingChangeBar;

        /// <summary>The groups the user has opened, by the group's stable identity (its smallest member key).</summary>
        private readonly HashSet<string> _expanded = new HashSet<string>();

        private static string GroupKey(ClashGroup g)
        {
            if (g == null) return null;
            if (!string.IsNullOrEmpty(g.MinKey)) return g.MinKey;
            return g.First != null ? g.First.Key : null;
        }

        /// <summary>Show, fill or hide the "what changed" strip from the tracker's summary.</summary>
        public void RefreshWhatChanged()
        {
            try
            {
                if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshWhatChanged); return; }
                if (WhatChangedBar == null) return;

                var s = ClashChangeTracker.Current;
                // The NEW column is 36 px of a narrow panel: give it only while there is a summary to tag rows for.
                NewTagColumn.Width = s == null ? 0 : 36;
                _updatingChangeBar = true;
                try
                {
                    if (s == null)
                    {
                        WhatChangedBar.Visibility = Visibility.Collapsed;
                        OnlyNewCheck.IsChecked = false;          // the views belong to a summary that is gone
                        ShowClearedCheck.IsChecked = false;
                    }
                    else
                    {
                        WhatChangedText.Text = s.Headline;
                        WhatChangedCaveat.Text = s.Caveat;
                        WhatChangedCaveat.Visibility = string.IsNullOrEmpty(s.Caveat) ? Visibility.Collapsed : Visibility.Visible;
                        OnlyNewCheck.IsEnabled = s.Set.New.Count > 0 && !s.AllGone;
                        ShowClearedCheck.IsEnabled = s.Set.Cleared.Count > 0;
                        if (!OnlyNewCheck.IsEnabled) OnlyNewCheck.IsChecked = false;
                        if (!ShowClearedCheck.IsEnabled) ShowClearedCheck.IsChecked = false;
                        WhatChangedBar.Visibility = Visibility.Visible;
                    }
                }
                finally { _updatingChangeBar = false; }
            }
            catch (Exception ex)
            {
                Log.Warn("What-changed refresh failed", ex);
            }
        }

        private void ChangeView_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingChangeBar) return;
            Refresh();
        }

        private void WhatChangedDismiss_Click(object sender, RoutedEventArgs e)
        {
            ClashChangeTracker.DismissSummary();
        }

        // ------------------------------------------------------------- search

        /// <summary>Waits a moment after the last key before rebuilding the list, so typing is not a rebuild per letter.</summary>
        private DispatcherTimer _searchTimer;

        /// <summary>How many groups the search hid at the last rebuild, and what the empty banner is currently about.</summary>
        private int _searchNotMatching;
        private int _notNew;
        private enum BannerMode { Normal, Search, OnlyNew }
        private BannerMode _bannerMode = BannerMode.Normal;

        /// <summary>What is typed in the search box (empty when there is none).</summary>
        internal string SearchQuery { get { return SearchBox == null ? "" : (SearchBox.Text ?? ""); } }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            bool has = !string.IsNullOrEmpty(SearchQuery);
            SearchHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            SearchClear.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

            if (_searchTimer == null)
            {
                _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
                _searchTimer.Tick += delegate { _searchTimer.Stop(); Refresh(); };
            }
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && !string.IsNullOrEmpty(SearchQuery))
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
        }

        private void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        private void EmptyAction_Click(object sender, RoutedEventArgs e)
        {
            // When the search (or "only new") is what emptied the list, the way out is to undo it - not to sweep the model.
            if (_bannerMode == BannerMode.Search) { SearchBox.Text = ""; SearchBox.Focus(); return; }
            if (_bannerMode == BannerMode.OnlyNew) { OnlyNewCheck.IsChecked = false; return; }
            FullCheckButton_Click(sender, e);
        }

        /// <summary>
        /// Every string a row can be found by: what its row shows, and - for a group of several - every member's
        /// element names, categories, model and set, so a group is found by any one of the pairs inside it.
        /// </summary>
        internal static IEnumerable<string> SearchFields(ClashRowVm row, ClashGroup group)
        {
            yield return row.HostText;
            yield return row.OtherText;
            yield return row.ModelName;
            yield return row.SetText;
            yield return row.NoteText;
            yield return row.StatusText;
            yield return row.KindText;
            if (group == null) yield break;
            foreach (var m in group.Members)
            {
                yield return m.HostElementName;
                yield return m.HostCategory;
                yield return m.OtherElementName;
                yield return m.OtherCategory;
                yield return m.ModelName;
                yield return m.SetName;
            }
        }

        public ClashPanel()
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            // Rows carry a few colours chosen in code (the status text); rebuild them when the theme flips.
            ThemeManager.Changed += delegate { try { Dispatcher.BeginInvoke(new Action(Refresh)); } catch { } };

            // A summary appeared, changed or went: redraw the strip and the rows that carry its marks.
            ClashChangeTracker.Changed += delegate { try { Dispatcher.BeginInvoke(new Action(() => { RefreshWhatChanged(); Refresh(); })); } catch { } };

            ClashResultStore.Instance.Changed += OnStoreChanged;

            // Seeds the toggles and refreshes. The same call runs on every document
            // switch, so there is one definition of "make the controls say what the
            // settings say" rather than two that can drift apart.
            SyncToSettings();
        }

        /// <summary>
        /// Point the toggles at whatever settings are in force NOW.
        ///
        /// The panel is a session singleton; settings are per model. Seeding these in
        /// the constructor alone meant that after a document switch the boxes still
        /// showed the previous model's choices, while Refresh read
        /// ClashSettings.Current — so the display and the behaviour genuinely
        /// disagreed, and the box was the more believable of the two. This is the same
        /// desync SetLiveEnabled exists to prevent for the ribbon, arrived at from the
        /// other direction.
        ///
        /// SetLiveEnabled is deliberately NOT reused here: it drives the WRITE path
        /// (ApplyLive saves), and this is a read. Hence _syncing — moving a checkbox
        /// raises its Changed handler, and without the guard adopting a model would
        /// write its freshly-loaded settings straight back over themselves.
        /// </summary>
        public void SyncToSettings()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke((Action)SyncToSettings);
                    return;
                }

                var s = ClashSettings.Current;

                _syncing = true;
                try
                {
                    if (LiveCheck != null) LiveCheck.IsChecked = s.Enabled;
                    if (GroupCheck != null) GroupCheck.IsChecked = s.GroupResults;
                }
                finally { _syncing = false; }

                Refresh();
            }
            catch (Exception ex) { Log.Warn("Panel resync to settings failed", ex); }
        }

        /// <summary>
        /// True while SyncToSettings is moving the toggles, so their handlers know the
        /// change came from the settings rather than from the user and must not be
        /// written back. See SyncToSettings.
        /// </summary>
        private bool _syncing;

        /// <summary>
        /// ExternalEvents can only be created while Revit is in API context, so the
        /// application object calls this from OnStartup rather than the constructor.
        /// </summary>
        public void InitialiseEvents()
        {
            _showHandler = new ShowElementHandler();
            _showEvent = ExternalEvent.Create(_showHandler);

            _fullCheckHandler = new FullCheckHandler { Panel = this };
            _fullCheckEvent = ExternalEvent.Create(_fullCheckHandler);

            _recheckHandler = new RecheckHandler { Panel = this };
            _recheckEvent = ExternalEvent.Create(_recheckHandler);

            _filterHandler = new FilterHandler { Panel = this };
            _filterEvent = ExternalEvent.Create(_filterHandler);

            _auditHandler = new AuditHandler { Panel = this };
            _auditEvent = ExternalEvent.Create(_auditHandler);

            _focusHandler = new FocusHandler { Panel = this };
            _focusEvent = ExternalEvent.Create(_focusHandler);

            _recordRecheckHandler = new RecordRecheckHandler { Panel = this };
            _recordRecheckEvent = ExternalEvent.Create(_recordRecheckHandler);

            // Double-click a clash to FRAME it in 3D with a section box — the way to view a
            // pair that Selection Box cannot reach because a side lives in a link.
            ResultList.MouseDoubleClick += ResultList_MouseDoubleClick;
        }

        /// <summary>
        /// Frame the double-clicked clash(es) in the dedicated focus view. Single-click
        /// already selected them; this adds the geometric section box, which is what lets
        /// you see a link element that could never be Selection-Boxed.
        /// </summary>
        private void ResultList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_focusEvent == null) return;

            var targets = new List<ClashResult>();
            foreach (var item in ResultList.SelectedItems)
            {
                var row = item as ClashRowVm;
                if (row == null) continue;
                targets.AddRange(row.FocusTargets());
            }
            if (targets.Count == 0) return;

            _focusHandler.Targets = targets;
            _focusEvent.Raise();
            SetStatus("Framing the clash in 3D…");
        }

        private void StartResolvedTimer()
        {
            if (_resolvedTimer != null) return;
            _resolvedTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _resolvedTimer.Tick += delegate
            {
                if (!ClashResultStore.Instance.HasRecentlyResolved)
                {
                    _resolvedTimer.Stop();
                    _resolvedTimer = null;
                }
                Refresh();
            };
            _resolvedTimer.Start();
        }

        // ------------------------------------------------------------- store → UI

        private void OnStoreChanged(object sender, EventArgs e)
        {
            // The updater can fire many times inside one transaction. Coalesce into a
            // single refresh at background priority so the list never fights the modeller.
            if (_refreshQueued) return;
            _refreshQueued = true;

            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _refreshQueued = false;
                    Refresh();
                }));
            }
            catch
            {
                _refreshQueued = false;
            }
        }

        private void Refresh()
        {
            try
            {
                // The Set column only earns its place once more than one set can catch a
                // row. With a single set it would repeat that set's name down every row.
                SyncSetColumn();

                // Rebuilding the list throws away the user's selection and scroll
                // position. With background verification every 5 s plus live edits that
                // happens repeatedly while someone is working through the list, so both
                // are captured here — BEFORE anything is torn down — and put back after.
                var wasSelected = new HashSet<string>();
                foreach (var item in ResultList.SelectedItems)
                {
                    var sel = item as ClashRowVm;
                    if (sel != null) wasSelected.Add(sel.RowKey);
                }
                double scroll = ScrollOffset();

                var snapshot = ClashResultStore.Instance.Snapshot();
                var s = ClashSettings.Current;

                // Two seats comparing "are we running the same rules?" have nothing to compare
                // by eye otherwise — settings are per-model and never shown side by side.
                FilterButton.ToolTip = "Choose which categories are checked, and what they are "
                    + "checked against  —  rules " + RulesFingerprint.Of(s);

                // Grouping is a presentation concern, not a storage one — the store
                // stays the record of individual element pairs, and turning grouping
                // off costs nothing but a re-render. Distance 0 groups only exact
                // coincidences, which is effectively "off".
                var groups = ClashGrouper.Group(snapshot,
                    s.GroupResults ? s.GroupingDistanceFeet : 0.0);

                // Optional column sort — off by default, so the natural grouping order
                // shows until a header is clicked. Sorts the GROUPS; children stay under
                // their group.
                var groupList = new List<ClashGroup>(groups);
                if (_sortCol != null)
                {
                    var keyed = new List<KeyValuePair<IComparable, ClashGroup>>();
                    foreach (var g in groupList)
                        keyed.Add(new KeyValuePair<IComparable, ClashGroup>(SortKey(g, _sortCol), g));
                    keyed.Sort((a, b) => { int c = a.Key.CompareTo(b.Key); return _sortDesc ? -c : c; });
                    groupList.Clear();
                    foreach (var kv in keyed) groupList.Add(kv.Value);
                }

                // Which groups were open before the rebuild, so expansion survives a
                // refresh — otherwise every live edit would snap the list shut.
                // Read from the rows on screen: the state of what the user can see is what they last left it as.
                // Groups that are NOT on screen (hidden by the search, Only new, or Coordinated/Ignored) keep what they had -
                // reading only the visible rows used to forget them, so they came back shut.
                foreach (var r in _rows)
                {
                    if (r.IsPair || r.Group == null) continue;
                    var gk = GroupKey(r.Group);
                    if (gk == null) continue;
                    if (r.IsExpanded) _expanded.Add(gk); else _expanded.Remove(gk);
                }

                _rows.Clear();

                bool showAll = ShowAllCheck.IsChecked == true;
                int hidden = 0;

                // The search is read HERE, on every rebuild. This list is rebuilt on every live edit and every
                // background verification; a filter applied only when the text changed would be gone within seconds.
                string query = SearchQuery;
                bool searching = ClashSearch.IsActive(query);
                int notMatching = 0;

                // What changed after a link change: the rows it made new can be picked out, and the ones it cleared listed.
                var changes = ClashChangeTracker.Current;
                bool onlyNew = changes != null && OnlyNewCheck.IsChecked == true;
                bool showCleared = changes != null && ShowClearedCheck.IsChecked == true;
                int notNew = 0;
                foreach (var g in groupList)
                {
                    var head = new ClashRowVm(g);

                    // Coordinated and Ignored are decisions someone already made, so
                    // they are hidden — but a REOPENED one is a decision that no
                    // longer holds, and hiding that is exactly the silent burial the
                    // status system is meant to prevent.
                    var st = g.Status;
                    if (!showAll && !head.IsReopened
                        && (st == ClashStatusKind.Coordinated || st == ClashStatusKind.Ignored))
                    {
                        hidden++;
                        continue;
                    }

                    if (searching && !ClashSearch.Matches(query, SearchFields(head, g)))
                    {
                        notMatching++;
                        continue;
                    }

                    if (onlyNew && !head.IsNewSinceChange)
                    {
                        notNew++;
                        continue;
                    }

                    var headKey = GroupKey(g);
                    head.IsExpanded = headKey != null && _expanded.Contains(headKey);
                    _rows.Add(head);

                    // Children are laid out inline beneath their group rather than in a
                    // TreeView — see ClashRowVm for why.
                    if (head.IsExpanded && g.Count > 1)
                        foreach (var m in g.Members) _rows.Add(new ClashRowVm(g, m, false));
                }

                // Just-fixed clashes, struck through, until they age out.
                var resolved = ClashResultStore.Instance.RecentlyResolved(ResolvedWindow);
                if (resolved.Count > 0)
                {
                    foreach (var rg in ClashGrouper.Group(resolved,
                                s.GroupResults ? s.GroupingDistanceFeet : 0.0))
                    {
                        var rv = new ClashRowVm(rg, true);
                        if (searching && !ClashSearch.Matches(query, SearchFields(rv, rg))) { notMatching++; continue; }
                        _rows.Add(rv);
                    }

                    StartResolvedTimer();
                }

                // The clashes the change cleared, struck through, under the rest - for as long as the box is ticked.
                if (showCleared)
                {
                    foreach (var cg in ClashGrouper.Group(changes.Set.Cleared, s.GroupResults ? s.GroupingDistanceFeet : 0.0))
                    {
                        var cv = new ClashRowVm(cg, true);
                        if (searching && !ClashSearch.Matches(query, SearchFields(cv, cg))) { notMatching++; continue; }
                        _rows.Add(cv);
                    }
                }

                _searchNotMatching = notMatching;
                _notNew = notNew;

                // Forget the groups that are no longer in the store at all.
                var liveKeys = new HashSet<string>();
                foreach (var lg in groupList) { var lk = GroupKey(lg); if (lk != null) liveKeys.Add(lk); }
                _expanded.RemoveWhere(k => !liveKeys.Contains(k));

                ResultList.ItemsSource = null;
                ResultList.ItemsSource = _rows;

                RestoreView(wasSelected, scroll);

                UpdateEmptyBanner();

                int hard = 0, unver = 0;
                foreach (var r in snapshot)
                {
                    if (r.Severity == ClashSeverity.Hard) hard++;
                    else if (r.Severity == ClashSeverity.Unverified) unver++;
                }
                int clear = snapshot.Count - hard - unver;
                var counts = new List<string>();
                counts.Add(hard + " hard");
                if (unver > 0) counts.Add(unver + " unverified");
                if (clear > 0) counts.Add(clear + " clearance");
                CountText.Text = string.Join(", ", counts.ToArray());

                // Say what grouping actually bought — 208 rows collapsing to a few
                // dozen issues is the whole point, and it should be visible.
                var summary = new List<string>();
                if (s.GroupResults && snapshot.Count > groupList.Count)
                    summary.Add("\u2192 " + groupList.Count + " issue" + (groupList.Count == 1 ? "" : "s"));
                if (hidden > 0) summary.Add(hidden + " hidden");
                if (notMatching > 0) summary.Add(notMatching + " not matching");
                if (notNew > 0) summary.Add(notNew + " not new");
                GroupText.Text = string.Join(", ", summary.ToArray());

                int pending = DeferredQueue.PendingCount;
                PendingText.Text = pending > 0 ? "(" + pending + " queued)" : "";

                UpdateHeaderArrows();
            }
            catch (Exception ex)
            {
                Log.Warn("Panel refresh failed", ex);
            }
        }

        // ---------------------------------------------------------------- sorting

        /// <summary>The value a group sorts on for the active column.</summary>
        private static IComparable SortKey(ClashGroup g, string col)
        {
            var head = new ClashRowVm(g);
            switch (col)
            {
                case "Found":
                    var newest = DateTime.MinValue;
                    foreach (var m in g.Members) if (m.DetectedUtc > newest) newest = m.DetectedUtc;
                    return newest;
                case "Element":      return head.HostText ?? "";
                case "Clashes with": return head.OtherText ?? "";
                case "Type":         return head.KindText ?? "";
                case "Set":          return head.SetText ?? "";
                case "Model":        return head.ModelName ?? "";
                case "Status":       return head.StatusText ?? "";
                case "Note":         return head.NoteText ?? "";
                default:             return "";
            }
        }

        /// <summary>
        /// Click a column header to sort by it. Same column again flips direction;
        /// a new column starts ascending, except Found which starts newest-first — the
        /// point of a live tool is to see the clash you just made.
        /// </summary>
        private void Header_Click(object sender, RoutedEventArgs e)
        {
            var h = e.OriginalSource as System.Windows.Controls.GridViewColumnHeader;
            if (h == null) return;
            var col = (h.Content as string ?? "").TrimEnd(' ', '▲', '▼').TrimEnd();
            if (col.Length == 0) return;   // the dot / expander / count / arrow columns

            if (col == _sortCol) _sortDesc = !_sortDesc;
            else { _sortCol = col; _sortDesc = (col == "Found"); }
            Refresh();
        }

        /// <summary>
        /// Show the Set column only when there is more than one set — otherwise it just
        /// repeats the single set's name down the whole list. Removed from and re-inserted
        /// into the GridView in its original position (just before Model).
        /// </summary>
        private void SyncSetColumn()
        {
            var gv = ResultList.View as System.Windows.Controls.GridView;
            if (gv == null || SetColumn == null) return;

            bool wanted = ClashSettings.Current.Sets.Count > 1;
            bool present = gv.Columns.Contains(SetColumn);

            if (wanted && !present)
            {
                int idx = gv.Columns.Count;
                for (int i = 0; i < gv.Columns.Count; i++)
                {
                    var h = (gv.Columns[i].Header as string ?? "").TrimEnd(' ', '▲', '▼').TrimEnd();
                    if (h == "Model") { idx = i; break; }
                }
                gv.Columns.Insert(idx, SetColumn);
            }
            else if (!wanted && present)
            {
                gv.Columns.Remove(SetColumn);
            }
        }

        /// <summary>Show a ▲/▼ on the active column's header, plain text on the rest.</summary>
        private void UpdateHeaderArrows()
        {
            var gv = ResultList.View as System.Windows.Controls.GridView;
            if (gv == null) return;
            foreach (var c in gv.Columns)
            {
                var s = (c.Header as string ?? "").TrimEnd(' ', '▲', '▼').TrimEnd();
                if (s.Length == 0) continue;
                c.Header = (s == _sortCol) ? s + (_sortDesc ? "  ▼" : "  ▲") : s;
            }
        }

        /// <summary>
        /// True while selection is being put back after a rebuild, so the restore
        /// does not look like the user clicking and re-trigger a Revit selection.
        /// </summary>
        private bool _restoringView;

        private ScrollViewer _scroller;

        /// <summary>The ListView's own ScrollViewer, cached after the first walk.</summary>
        private ScrollViewer FindScroller()
        {
            if (_scroller != null) return _scroller;
            try
            {
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(ResultList);
                while (queue.Count > 0)
                {
                    var d = queue.Dequeue();
                    var sv = d as ScrollViewer;
                    if (sv != null) { _scroller = sv; return sv; }
                    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (int i = 0; i < n; i++)
                        queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                }
            }
            catch { }
            return null;
        }

        private double ScrollOffset()
        {
            var sv = FindScroller();
            return sv == null ? 0 : sv.VerticalOffset;
        }

        private void RestoreView(HashSet<string> selectedKeys, double scroll)
        {
            if (selectedKeys.Count == 0 && scroll <= 0) return;

            _restoringView = true;
            try
            {
                if (selectedKeys.Count > 0)
                    foreach (var row in _rows)
                        if (selectedKeys.Contains(row.RowKey)) ResultList.SelectedItems.Add(row);
            }
            catch { }
            finally { _restoringView = false; }

            if (scroll <= 0) return;

            // The rows have not been laid out yet, so the scroll viewer cannot be
            // moved until WPF has measured them.
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    var sv = FindScroller();
                    if (sv != null) sv.ScrollToVerticalOffset(scroll);
                }));
            }
            catch { }
        }

        public void SetStatus(string text)
        {
            try
            {
                if (Dispatcher.CheckAccess()) StatusText.Text = text;
                else Dispatcher.BeginInvoke(new Action(() => StatusText.Text = text));
            }
            catch { }
        }

        /// <summary>
        /// Show the estimated cost of a full check as a green / amber / red hint beside the
        /// button, so the size of the run is visible before it is launched. Fed by the
        /// scope guard (FullCheckHandler), the filter apply (FilterHandler) and a one-shot
        /// idle warm — the three moments the scope can change.
        /// </summary>
        public void UpdateScopeIndicator(ClashScopeEstimate scope)
        {
            if (scope == null) return;
            Action apply = delegate
            {
                if (ScopeIndicator == null) return;
                if (scope.SourceElements <= 0 || string.IsNullOrEmpty(scope.EstimatedText))
                {
                    ScopeIndicator.Text = "";
                    return;
                }
                ScopeIndicator.Text = "● full check " + scope.EstimatedText;
                // By resource KEY, not a brush: the palette differs light and dark, and a reference is
                // re-resolved when the theme changes while a fixed brush would stay the light one.
                ScopeIndicator.SetResourceReference(TextBlock.ForegroundProperty,
                    scope.Band == ScopeBand.Green ? "ScopeGreen" :
                    scope.Band == ScopeBand.Yellow ? "ScopeAmber" : "ScopeRed");
                ScopeIndicator.ToolTip = scope.Summary() + "  ·  "
                    + scope.Comparisons.ToString("N0") + " comparisons before parameter rules narrow it";
            };
            try
            {
                if (Dispatcher.CheckAccess()) apply();
                else Dispatcher.BeginInvoke(apply);
            }
            catch { }
        }

        // ------------------------------------------------------------- handlers

        private void LiveCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;   // the settings moved this, not the user
            ApplyLive(LiveCheck.IsChecked == true);
        }

        /// <summary>
        /// Turn live checking on or off. THE ONLY WAY IT HAPPENS — the ribbon's Pause /
        /// Resume button calls this too.
        ///
        /// It used to flip ClashSettings.Enabled itself, without saving and without
        /// touching this checkbox, so the same pause behaved differently depending on
        /// which control you used: from the ribbon it lasted exactly as long as the
        /// session (the bug the comment in ApplyLive describes, fixed here but not
        /// there), and the checkbox went on showing the opposite of what was true.
        /// </summary>
        public void SetLiveEnabled(bool on)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke((Action)(() => SetLiveEnabled(on)));
                    return;
                }

                // Moving the checkbox raises LiveCheck_Changed, which does the work.
                // Where it already agrees — or does not exist yet — do the work here, so
                // the setting can never sit out of step with the control.
                if (LiveCheck != null && (LiveCheck.IsChecked == true) != on) LiveCheck.IsChecked = on;
                else ApplyLive(on);
            }
            catch (Exception ex) { Log.Warn("Live toggle failed", ex); }
        }

        /// <summary>Whether live checking is on right now.</summary>
        public bool LiveEnabled { get { return ClashSettings.Current.Enabled; } }

        private void ApplyLive(bool on)
        {
            ClashSettings.Current.Enabled = on;

            // Persist it. This did not, and Enabled was not written by Save() either,
            // so pausing live checking lasted exactly as long as the session and came
            // back on at the next start with nothing to say it had.
            ClashSettings.Current.Save();

            SetStatus(on
                ? "Live checking on."
                : "Live checking paused — existing results kept.");
        }

        private void GroupCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;   // the settings moved this, not the user
            ClashSettings.Current.GroupResults = GroupCheck.IsChecked == true;
            ClashSettings.Current.Save();
            Refresh();
        }

        /// <summary>Opens or closes a group in place, without disturbing selection.</summary>
        private void Expander_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var row = fe == null ? null : fe.DataContext as ClashRowVm;
            if (row == null || !row.CanExpand) return;

            row.IsExpanded = !row.IsExpanded;
            Refresh();
            e.Handled = true;
        }

        private void ShowAllCheck_Changed(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        /// <summary>
        /// Opens the coordination record — pending / coordinated / ignored / resolved.
        /// Modeless, so it can sit beside the panel while you work, and it refreshes
        /// itself as decisions and resolutions land.
        ///
        /// Selection from it comes back through this panel's ExternalEvent — the
        /// window itself is not in Revit API context and must not call the API.
        /// </summary>
        private void ResolvedButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_recordWindow != null)
                {
                    _recordWindow.Activate();
                    return;
                }

                _recordWindow = new RecordWindow(
                    ids =>
                    {
                        if (_showEvent == null || ids == null || ids.Count == 0) return;
                        _showHandler.TargetIds = ids;
                        _showEvent.Raise();
                        SetStatus("Selected " + ids.Count + " element(s) from the record.");
                    },
                    // Re-test the decided pairs. Marshalled through an ExternalEvent
                    // because the record window is not API context.
                    () =>
                    {
                        if (_recordRecheckEvent == null) return;
                        SetStatus("Re-testing every decided pair…");
                        _recordRecheckEvent.Raise();
                    });

                new System.Windows.Interop.WindowInteropHelper(_recordWindow).Owner =
                    System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

                _recordWindow.Closed += delegate { _recordWindow = null; };
                _recordWindow.Show();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the record window", ex);
                SetStatus("Could not open the record — see " + Log.Path);
            }
        }

        private RecordWindow _recordWindow;

        private void RecheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recheckEvent == null) return;
            SetStatus("Rechecking the listed clashes…");
            _recheckEvent.Raise();
        }

        // ------------------------------------------------------------- link notice

        /// <summary>Keeps the notice's "N min ago" current without waiting for the next
        /// change — same pattern as _resolvedTimer, a background-priority tick.</summary>
        private DispatcherTimer _linkNoticeTimer;

        /// <summary>
        /// Show, update or hide the link-change notice from LinkWatch.Current. Called from
        /// LiveClashApp after a change is recorded or a model is adopted, and from
        /// FullCheckHandler once a completed full check makes the notice stale. Callers are
        /// Revit event handlers on the main thread, but not necessarily this control's own
        /// dispatcher call stack, so this is marshalled exactly like SetStatus.
        /// </summary>
        public void RefreshLinkNotice()
        {
            try
            {
                if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshLinkNotice); return; }
                if (LinkNoticeBar == null) return;

                var notice = LinkWatch.Current;
                if (notice == null)
                {
                    LinkNoticeBar.Visibility = Visibility.Collapsed;
                    if (_linkNoticeTimer != null) { _linkNoticeTimer.Stop(); _linkNoticeTimer = null; }
                    return;
                }

                LinkNoticeText.Text = notice.LinkName + " " + notice.VerbText + " " + notice.AgeText
                    + ". Results listed before then may be out of date.";
                LinkNoticeBar.Visibility = Visibility.Visible;

                if (_linkNoticeTimer == null)
                {
                    _linkNoticeTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromSeconds(15)
                    };
                    _linkNoticeTimer.Tick += delegate { RefreshLinkNotice(); };
                    _linkNoticeTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Link-notice refresh failed", ex);
            }
        }

        private void LinkNoticeDismiss_Click(object sender, RoutedEventArgs e)
        {
            LinkWatch.Clear();
            RefreshLinkNotice();
        }

        // ------------------------------------------------------------- status

        /// <summary>
        /// Attaches a reason to the selected rows without changing their status.
        /// A hidden row with no explanation is a decision nobody can audit later.
        /// </summary>
        private void Note_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rows = new List<ClashRowVm>();
                foreach (var item in ResultList.SelectedItems)
                {
                    var row = item as ClashRowVm;
                    if (row != null && !row.IsResolved) rows.Add(row);
                }
                if (rows.Count == 0) { SetStatus("Select an issue first."); return; }

                var win = new NoteWindow(
                    rows.Count == 1
                        ? "Why: " + rows[0].HostText + "  →  " + rows[0].OtherText
                        : "Note for " + rows.Count + " selected issues",
                    rows.Count == 1 ? rows[0].NoteText : "")
                { Owner = Window.GetWindow(this) };

                if (win.ShowDialog() != true) return;

                // SetNote, not Set: a group header's Status is the WORST status among its
                // members, and Set applies one kind to every key it is given - passing the
                // header's status here would downgrade already-decided members back to whatever
                // the worst one was, and silently drop the note for an undecided ("New") row
                // even though this method just told the user it was saved.
                foreach (var row in rows)
                    ClashStatusStore.Instance.SetNote(row.Keys(), win.Note);

                Refresh();
                SetStatus(string.IsNullOrEmpty(win.Note)
                    ? "Note cleared."
                    : "Note saved on " + rows.Count + " issue(s).");
            }
            catch (Exception ex)
            {
                Log.Warn("Editing the note failed", ex);
            }
        }

        /// <summary>
        /// Ask why, straight after a Coordinated or Ignored mark.
        ///
        /// Those two HIDE the row, so the note is the only thing that will explain the
        /// decision to whoever reads the record later — including you. Prompting here
        /// rather than making people remember the separate Note button is the difference
        /// between a record with reasons in it and one without.
        ///
        /// Dismissing the dialog is a valid answer: the decision already stands, and the
        /// note stays empty. Nothing is lost by saying nothing.
        /// </summary>
        private void PromptForReason(List<string> keys, ClashStatusKind kind, int issues)
        {
            try
            {
                var win = new NoteWindow(
                    "Why " + (kind == ClashStatusKind.Coordinated ? "coordinated" : "ignored") + "?  ("
                        + issues + " issue" + (issues == 1 ? "" : "s") + ")",
                    "")
                { Owner = Window.GetWindow(this) };

                if (win.ShowDialog() != true) return;          // no reason given; the mark stands
                if (string.IsNullOrWhiteSpace(win.Note)) return;

                ClashStatusStore.Instance.Set(keys, kind, win.Note);
            }
            catch (Exception ex)
            {
                // A failed prompt must not undo a decision that is already recorded.
                Log.Warn("Reason prompt failed; the status itself is saved", ex);
            }
        }

        private void MarkPending_Click(object sender, RoutedEventArgs e) { Mark(ClashStatusKind.Pending); }
        private void MarkCoordinated_Click(object sender, RoutedEventArgs e) { Mark(ClashStatusKind.Coordinated); }
        private void MarkIgnored_Click(object sender, RoutedEventArgs e) { Mark(ClashStatusKind.Ignored); }
        private void MarkNew_Click(object sender, RoutedEventArgs e) { Mark(ClashStatusKind.New); }

        /// <summary>
        /// Applies a decision to every selected row. Marking works on whole issues —
        /// one click accepts a pipe run through a wall rather than eleven.
        /// </summary>
        private void Mark(ClashStatusKind kind)
        {
            try
            {
                var keys = new List<string>();
                int issues = 0;
                foreach (var item in ResultList.SelectedItems)
                {
                    var row = item as ClashRowVm;
                    if (row == null || row.IsResolved) continue;   // nothing to decide about a fixed clash
                    keys.AddRange(row.Keys());
                    issues++;
                }

                if (keys.Count == 0) { SetStatus("Select an issue first."); return; }

                // THE DECISION IS RECORDED FIRST, before any dialog.
                //
                // Coordinated and Ignored hide the row, so six months later the only
                // thing explaining why is the note — which is why the note prompt
                // follows automatically for those two. But it must never GATE the
                // decision: if the write waited on the dialog, dismissing it would
                // silently discard the mark, and the user would have every reason to
                // believe it had been saved. Status first, reason second.
                ClashStatusStore.Instance.Set(keys, kind);

                if (kind == ClashStatusKind.Coordinated || kind == ClashStatusKind.Ignored)
                    PromptForReason(keys, kind, issues);

                Refresh();

                SetStatus(issues + " issue" + (issues == 1 ? "" : "s") + " ("
                    + keys.Count + " clash" + (keys.Count == 1 ? "" : "es") + ") marked "
                    + kind + (kind == ClashStatusKind.Coordinated || kind == ClashStatusKind.Ignored
                        ? " — hidden unless Show all is ticked." : "."));
            }
            catch (Exception ex)
            {
                Log.Warn("Marking status failed", ex);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClashResultStore.Instance.Clear();
            DeferredQueue.Clear(null);
            SetEmptyReason(EmptyReason.Cleared);
            SetStatus("Cleared.");
        }

        private void FullCheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fullCheckEvent == null) return;
            SetStatus("Running full check — Revit will be busy until it finishes…");
            _fullCheckEvent.Raise();
        }

        /// <summary>
        /// Selects on a single click, and only selects — no zoom, no view change.
        /// The user frames the clash themselves with Selection Box (BX).
        /// </summary>
        private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Putting selection back after a rebuild must not look like a click.
            if (_restoringView) return;
            if (_showEvent == null || ResultList.SelectedItems.Count == 0) return;

            // Multi-select is allowed, so gather every selected issue — selecting
            // three rows and hitting BX frames all three at once.
            var ids = new List<ElementId>();
            var seen = new HashSet<long>();
            bool anyLinked = false;
            string linkName = null;

            foreach (var item in ResultList.SelectedItems)
            {
                var row = item as ClashRowVm;
                if (row == null) continue;
                if (row.OtherIsLinked) { anyLinked = true; linkName = row.ModelName; }
                foreach (var id in row.SelectionIds())
                    if (seen.Add(IdUtil.Value(id))) ids.Add(id);
            }

            if (ids.Count == 0) return;

            _showHandler.TargetIds = ids;
            _showEvent.Raise();

            SetStatus(anyLinked
                ? "Selected " + ids.Count + " element(s) — the other side is in "
                    + linkName + " and cannot be selected from here."
                : "Selected " + ids.Count + " element(s) — Selection Box (BX) will frame the issue.");
        }

        /// <summary>
        /// The filter goes through an ExternalEvent rather than opening the window
        /// here: the parameter picker needs a catalog read out of the document, and a
        /// WPF click handler is not Revit API context. See FilterHandler.
        /// </summary>
        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_filterEvent == null) return;
            _filterEvent.Raise();
        }


        /// <summary>
        /// Write the register to CSV. No Revit API call here, so it runs straight off
        /// the click rather than through an ExternalEvent — the results are already
        /// in memory.
        /// </summary>
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                List<ClashGroup> groups;
                if (!PrepareExport(out groups)) return;

                var model = ClashSettings.ModelName ?? "LiveClash";
                // Captured ONCE, here, before the modal dialog blocks — the suggested file name and
                // the "Exported" column written into the file must agree on when this export
                // happened, not disagree by however long the user spends in the Save dialog.
                var stamp = CsvExport.StampNow(model);
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export the clash register",
                    Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                    DefaultExt = ".csv",
                    FileName = CsvStamp.FileName("clashes", model, stamp.Exported)
                };
                if (dlg.ShowDialog() != true) return;

                ExportTo(dlg.FileName, groups, model, stamp);
            }
            catch (Exception ex)
            {
                Log.Error("Export failed", ex);
                SetStatus("Export failed — see " + Log.Path);
            }
        }

        /// <summary>
        /// What would be exported, or false (with the panel saying why) when there is nothing. Split from
        /// the click handler, with <see cref="ExportTo"/>, so both halves can be driven without the system
        /// file dialog, which cannot be answered from a test.
        /// </summary>
        private bool PrepareExport(out List<ClashGroup> groups)
        {
            var s = ClashSettings.Current;
            groups = ClashGrouper.Group(ClashResultStore.Instance.Snapshot(),
                s.GroupResults ? s.GroupingDistanceFeet : 0.0);

            if (groups.Count == 0)
            {
                SetStatus("Nothing to export — run a full check first.");
                return false;
            }
            return true;
        }

        /// <summary>Everything after the file has been chosen: write it, and say what happened.</summary>
        /// <param name="path">Where to write the CSV.</param>
        /// <param name="groups">The issues to export.</param>
        /// <param name="model">The model the export is for.</param>
        /// <param name="stamp">Captured by the caller before the file dialog — see ExportButton_Click.</param>
        private void ExportTo(string path, List<ClashGroup> groups, string model, CsvStamp stamp)
        {
            try
            {
                // Stamped with where it came from: a register that leaves this machine - an ACC folder,
                // a meeting invite, pasted beside another modeller's - still says which model, when,
                // who, and under which rules. See CsvStamp.
                int n = CsvExport.WriteIssues(path, groups, model, stamp);
                Log.Info("Exported " + n + " issue(s) to " + path);
                SetStatus("Exported " + n + " issue(s) to " + path);
            }
            catch (Exception ex)
            {
                Log.Error("Export failed", ex);
                SetStatus("Export failed — see " + Log.Path);
            }
        }

        private void AuditButton_Click(object sender, RoutedEventArgs e)
        {
            if (_auditEvent == null) return;
            SetStatus("Auditing the model for provisional geometry…");
            _auditEvent.Raise();
        }
    }
}
