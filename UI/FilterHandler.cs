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
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc != null ? uidoc.Document : null;

                var s = ClashSettings.Current;

                // Offer parameters from BOTH sides' categories, so the picker is the
                // same whichever list you are adding a condition to.
                var cats = new HashSet<BuiltInCategory>(s.SourceCategories);
                foreach (var c in s.TargetCategories) cats.Add(c);

                List<ParameterInfo> catalog = null;
                if (doc != null && doc.IsValidObject)
                {
                    var sw = Stopwatch.StartNew();
                    catalog = ParameterCatalog.Build(doc, cats);
                    sw.Stop();
                    Log.Info("Parameter catalog: " + catalog.Count + " parameter(s) from "
                             + cats.Count + " categories in " + sw.ElapsedMilliseconds + " ms.");
                }

                Func<IEnumerable<BuiltInCategory>, IEnumerable<ParamRef>, bool, List<ValueChoice>> scan =
                    (cs, ps, force) => ValueScan.Collect(doc, cs, ps, force);

                List<CategoryChoice> categories = CategoryCatalog.Build(doc);
                if (doc != null && doc.IsValidObject)
                {
                    var csw = Stopwatch.StartNew();
                    CategoryCatalog.ApplyCounts(doc, categories, false);
                    csw.Stop();
                    int withEls = 0;
                    foreach (var c in categories) if (c.Count > 0) withEls++;
                    Log.Info("Category catalog: " + categories.Count + " model categories, "
                             + withEls + " with elements (counted in " + csw.ElapsedMilliseconds + " ms).");
                }

                // Per-link element counts for the Links… dialog. Read here because it is
                // document work; the window itself must not touch the API.
                var linkCounts = new Dictionary<string, Dictionary<BuiltInCategory, int>>();
                if (doc != null && doc.IsValidObject)
                {
                    var lsw = Stopwatch.StartNew();
                    foreach (var link in LinkContext.CollectFrom(doc))
                        if (link.Document != null && link.Document.IsValidObject)
                            linkCounts[link.Name] = LinkScope.CountByCategory(link.Document);
                    lsw.Stop();
                    if (linkCounts.Count > 0)
                        Log.Info("Link scope: counted " + linkCounts.Count + " link(s) in " + lsw.ElapsedMilliseconds + " ms.");
                }

                var win = new FilterWindow(catalog, categories,
                    doc != null && doc.IsValidObject ? scan : null,
                    doc != null && doc.IsValidObject && doc.IsWorkshared,
                    linkCounts);
                new System.Windows.Interop.WindowInteropHelper(win).Owner =
                    Process.GetCurrentProcess().MainWindowHandle;

                if (win.ShowDialog() != true) return;

                // An import replaces the whole filter rather than editing it in place.
                if (win.Imported != null)
                {
                    FilterPreset.ApplyTo(s, win.Imported);
                    Log.Info("Filter preset imported: " + s.SourceCategories.Count + " source / "
                             + s.TargetCategories.Count + " target categories.");
                }

                s.Save();

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
                ClashResultStore.Instance.Clear();

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

                    Panel.SetStatus("Filter updated — "
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

        public string GetName() { return "LiveClash.Filter"; }
    }
}
