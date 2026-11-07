using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Opens the filter window.
    ///
    /// It runs as an ExternalEvent rather than straight off the button click because
    /// the parameter picker needs a catalog of what parameters actually exist on this
    /// model — and reading the document requires Revit API context, which a WPF click
    /// handler is not. Gathering it here and handing a plain list to the dialog keeps
    /// every Revit call on the right side of that line.
    ///
    /// Applying the result also belongs here: rebuilding the updater's triggers
    /// touches UpdaterRegistry, which is API-context work too.
    /// </summary>
    public class FilterHandler : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }

        public void Execute(UIApplication app)
        {
            try
            {
                var total = Stopwatch.StartNew();
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc != null ? uidoc.Document : null;
                bool live = doc != null && doc.IsValidObject;

                var s = ClashSettings.Current;

                // Offer parameters from BOTH sides' categories, so the picker is the
                // same whichever list you are adding a condition to.
                var cats = new HashSet<BuiltInCategory>(s.SourceCategories);
                foreach (var c in s.TargetCategories) cats.Add(c);

                // U1: nothing here may take seconds. The whole-model category count (10-60 s cold), the parameter catalog and the
                // per-link counts used to run HERE, before the window, on Revit's main thread - Revit went grey. They are now steps of a
                // pump the window ticks while it is open (see BuildPump). What is needed to SHOW the window is only the category names and
                // the loaded-link list, and the link list is enumerated ONCE for the whole press (it was six times: a collector over the
                // link instances costs 250 ms even on a model with no links).
                List<LinkContext.ScanTarget> scanDocs = live ? LinkContext.ScanDocuments(doc) : null;

                List<CategoryChoice> categories = CategoryCatalog.Build(doc, scanDocs);   // names only: every Count is -1 = not counted yet
                long namesMs = total.ElapsedMilliseconds;

                var catalog = new List<ParameterInfo>();                                   // filled by the pump
                var linkCounts = new Dictionary<string, Dictionary<BuiltInCategory, int>>();   // filled by the pump

                Func<IEnumerable<BuiltInCategory>, IEnumerable<ParamRef>, bool, List<ValueChoice>> scan =
                    (cs, ps, force) => ValueScan.Collect(doc, cs, ps, force);

                var win = new FilterWindow(catalog, categories, live ? scan : null, live && doc.IsWorkshared, linkCounts);
                new System.Windows.Interop.WindowInteropHelper(win).Owner =
                    Process.GetCurrentProcess().MainWindowHandle;
                long builtMs = total.ElapsedMilliseconds;

                if (live) win.StartPrep(BuildPump(doc, win, cats, scanDocs, catalog, categories, linkCounts), true);

                bool rendered = false;
                win.ContentRendered += delegate
                {
                    if (rendered) return;
                    rendered = true;
                    Log.Info("Filter window: first render " + total.ElapsedMilliseconds + " ms after the press.");
                };
                Log.Info("Filter window: opening after " + builtMs + " ms (category names and scan set " + namesMs + " ms, construction "
                         + (builtMs - namesMs) + " ms); counts, parameters and link counts follow while it is open.");

                if (win.ShowDialog() != true) return;

                // An import replaces the whole filter rather than editing it in place.
                if (win.Imported != null)
                {
                    FilterPreset.ApplyTo(s, win.Imported);
                    Log.Info("Filter preset imported: " + s.SourceCategories.Count + " source / "
                             + s.TargetCategories.Count + " target categories.");
                }

                bool saved = s.Save();
                var notSaved = SaveNotice.After(saved, s.IsSuperseded);   // DL-e: a refused save is said, not hidden
                if (notSaved != null && s.IsSuperseded)
                {
                    // The edits went into the defaults a re-read replaced: nothing was applied, so nothing else changes.
                    if (Panel != null) Panel.SetStatus(notSaved);
                    return;
                }

                // We are in API context, so this can be done directly rather than
                // deferred to Idling.
                ClashUpdater.AddTriggers();

                // Drop the warm scan contexts. The signature now covers link scoping, so
                // a changed filter already produces a different key and this is belt and
                // braces — but the filter is exactly the thing that invalidates a context,
                // and leaving the cache to expire on its own TTL is how a stale scope
                // survives a deliberate change.
                ClashEngine.InvalidateAll();

                // Results found under the old filter are not necessarily valid under
                // the new one.
                LiveClashApp.ClearResultsForNewRules();   // the list and the held link rows (delta review 3, D3-2)

                // Say WHY the list just emptied, where the rows were - the status line
                // below scrolls away and this is the exact moment people conclude live
                // checking is broken.
                if (Panel != null) Panel.SetEmptyReason(ClashPanel.EmptyReason.FilterChanged);

                if (Panel != null)
                {
                    // The filter is the thing that changes the full-check scope — refresh
                    // the green/amber/red hint by the button while we are in API context.
                    try { Panel.UpdateScopeIndicator(ClashScopeEstimate.Measure(doc, s)); }
                    catch (System.Exception ex) { Log.Warn("Scope indicator update failed", ex); }

                    Panel.SetStatus((notSaved == null ? "" : notSaved + " ") + "Filter updated — "
                        + s.SourceCategories.Count + " source / " + s.TargetCategories.Count + " target categories"
                        + "  ·  sources: " + s.SourceFilter.Summary() + ", targets: " + s.TargetFilter.Summary()
                        + ". Run a full check or Recheck to re-evaluate.");
                }
            }
            catch (System.Exception ex)
            {
                Log.Error("Filter window failed", ex);
                if (Panel != null) Panel.SetStatus("Filter failed — see " + Log.Path);
            }
        }

        private static long ClockMs()
        {
            return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        }

        /// <summary>
        /// Wrap a pump step so its total time and slice count are logged once, when it finishes - plain Log.Info lines like the
        /// "Parameter catalog ... ms" ones that were always here, in Release too, so a slow press can be read from the log.
        /// </summary>
        private static Func<int, bool> Timed(Func<int, bool> step, Func<string> doneLine)
        {
            var sw = new Stopwatch();
            int slices = 0;
            return budget =>
            {
                sw.Start();
                slices++;
                try
                {
                    bool done = step(budget);
                    if (done) Log.Info(doneLine().Replace("{ms}", sw.ElapsedMilliseconds + " ms over " + slices + " slice(s)"));
                    return done;
                }
                catch (System.Exception ex)
                {
                    Log.Warn("Filter window: a step failed after " + sw.ElapsedMilliseconds + " ms over " + slices + " slice(s)", ex);
                    throw;
                }
                finally { sw.Stop(); }
            };
        }

        /// <summary>
        /// The work Filter used to do before its window showed, as steps the window ticks while it is open (U1). Each step is
        /// resumable and does about its budget; one Revit collector is the smallest piece (O(document), 165-250 ms on a
        /// 300,000-element model), so a tick can run a little over, never seconds. Order: the parameter picker first (about 2 s, and
        /// what the rule rows need), then the whole-model counts (the long one), then the per-link counts.
        /// </summary>
        private static WorkPump BuildPump(Document doc, FilterWindow win, HashSet<BuiltInCategory> cats,
                                          List<LinkContext.ScanTarget> scanDocs, List<ParameterInfo> catalog,
                                          List<CategoryChoice> categories, Dictionary<string, Dictionary<BuiltInCategory, int>> linkCounts)
        {
            var pump = new WorkPump(ClockMs);

            // 1. the parameter picker
            var builder = new ParameterCatalog.Builder(doc, cats, scanDocs);
            pump.Add("Loading parameters", Timed(budget =>
            {
                try
                {
                    bool done = builder.Step(budget);
                    win.ApplyParamBatch(builder.Names());
                    if (done)
                    {
                        catalog.Clear();
                        catalog.AddRange(builder.Result());
                        win.ParamsReady();   // rules picked while this loaded get their real scope (and a commit waiting for it goes on)
                    }
                    return done;
                }
                catch
                {
                    win.ParamsFailed();      // a failed step must not leave a commit waiting (or the window disabled) for a catalog that will never finish;
                                             // a rule picked meanwhile keeps no scope and is not committed (FilterWindow.FinishParamsIfPending)
                    throw;                   // the pump records it and the status line says so
                }
            },() => "Parameter catalog: " + catalog.Count + " parameter(s) from " + cats.Count + " categories in {ms}."),
            () => builder.Fraction);

            // 2. the counts. WarmStep is the same resumable pass the idle warm-up uses, so a half-done one is finished here and
            //    one this step leaves half-done (the window closed) is finished later by either.
            pump.Add("Counting elements", Timed(budget =>
            {
                if (!CategoryCatalog.WarmStep(doc, budget, scanDocs)) return false;
                if (CategoryCatalog.CountFellBack)
                {
                    // The sliced pass failed or lost a document: the answer below is the OLD one-shot count on this thread (as before U1, 10-60 s cold).
                    // Say so before it blocks, and log it.
                    Log.Warn("Filter window: counting without slices after an error; the window will not respond until it finishes.");
                    win.SetNoteNow("Counting without slices after an error - this can take a while.");
                }
                CategoryCatalog.ApplyCounts(doc, categories, false, scanDocs);   // served from the cache the pass just filed
                if (CategoryCatalog.CountWasPartial)
                {
                    // the fallback count is missing a document (one that could not be read, or a link lost meanwhile): shown, labelled, and not kept
                    Log.Warn("Filter window: not every model could be read for the counts; some may be low.");
                    win.SetNote("Not every model could be read, so some counts may be low.");
                }
                else win.SetNote(null);
                win.RefreshCategoryRows();
                return true;
            }, () =>
            {
                int withEls = 0;
                foreach (var c in categories) if (c.Count > 0) withEls++;
                return "Category catalog: " + categories.Count + " model categories, " + withEls + " with elements (counted in {ms}).";
            }), 
            () => CategoryCatalog.CountFraction, () => CategoryCatalog.CountDetail);

            // 3. the per-link counts for the Links… dialog, one link at a time
            List<LinkContext> links = null;
            int ix = 0;
            LinkScope.Counter current = null;
            pump.Add("Counting link elements", Timed(budget =>
            {
                if (links == null) links = LinkContext.CollectFrom(doc);
                var clock = Stopwatch.StartNew();
                while (ix < links.Count)
                {
                    if (current == null)
                    {
                        var l = links[ix];
                        if (l.Document == null || !l.Document.IsValidObject) { ix++; continue; }
                        current = new LinkScope.Counter(l.Document);
                    }
                    if (!current.Step((int)Math.Max(1, budget - clock.ElapsedMilliseconds))) return false;   // still on this link
                    linkCounts[links[ix].Name] = current.Counts;
                    current = null;
                    ix++;
                    if (clock.ElapsedMilliseconds >= budget && ix < links.Count) return false;
                }
                win.LinksReady();
                return true;
            }, () => linkCounts.Count > 0 ? "Link scope: counted " + linkCounts.Count + " link(s) in {ms}." : "Link scope: no loaded link ({ms})."),
            null);

            return pump;
        }

        public string GetName() { return "LiveClash.Filter"; }
    }
}
