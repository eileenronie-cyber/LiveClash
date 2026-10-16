using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// What one loaded link actually contains, per category — the data behind the
    /// per-link category control. The engine already checks link elements; this lets
    /// the user decide WHICH categories each link contributes, so a plumbing model
    /// coordinating against an architectural link can drop Furniture, Casework and
    /// Entourage from that link without touching what the structural link brings.
    /// </summary>
    public static class LinkScope
    {
        /// <summary>
        /// Element count per category for a link's document. One streamed pass, not one
        /// collector per category — see CategoryCatalog.Counts for why that matters.
        ///
        /// Every category the FILTER CAN OFFER, not a curated list. This used to restrict
        /// itself to ClashSettings.Selectable, a hand-picked 31 — but CategoryCatalog was
        /// deliberately changed to offer whatever the documents actually contain, so a
        /// target category outside that list could be checked and never excluded per link.
        /// The Links dialog can only scope what it can see.
        ///
        /// "Everything with geometry" is too wide the other way. Measured on Snowdon, it
        /// took the Architectural link from 11 categories to 57, the new arrivals being
        /// &lt;Sketch&gt;, &lt;Area Boundary&gt;, Cameras, Survey Point, Guide Grid and
        /// Scope Boxes — things that have a bounding box and cannot clash with anything.
        /// So the gate is CategoryCatalog.Build's, exactly: a top-level Model category the
        /// engine can actually put in an ElementMulticategoryFilter. What the dialog can
        /// scope then matches what the filter can select, which is the whole point.
        ///
        /// Per element it is still Tally's rule — a bounding box, and not ignorable —
        /// because "has elements" would offer 27,333 Materials as a clash category.
        /// </summary>
        public static Dictionary<BuiltInCategory, int> CountByCategory(Document linkDoc)
        {
            var counts = new Dictionary<BuiltInCategory, int>();
            if (linkDoc == null || !linkDoc.IsValidObject) return counts;

            var offerable = OfferableCategories(linkDoc);
            if (offerable.Count == 0) return counts;

            try
            {
                var it = new FilteredElementCollector(linkDoc)
                    .WhereElementIsNotElementType()
                    .GetElementIterator();
                it.Reset();
                while (it.MoveNext())
                {
                    var e = it.Current as Element;
                    if (e == null) continue;

                    Category cat;
                    try { cat = e.Category; } catch { continue; }
                    if (cat == null) continue;

                    int id;
                    try { id = (int)IdUtil.Value(cat.Id); } catch { continue; }
                    if (!offerable.Contains(id)) continue;

                    // Only geometry can clash — match the engine's own tests so the number
                    // shown is the number that can produce a result.
                    try { if (e.get_BoundingBox(null) == null) continue; } catch { continue; }
                    try { if (PairSuppressor.IsIgnorableElement(e)) continue; } catch { continue; }

                    var bic = (BuiltInCategory)id;
                    int n;
                    counts[bic] = counts.TryGetValue(bic, out n) ? n + 1 : 1;
                }
            }
            catch { /* a link that will not enumerate simply shows no counts */ }

            return counts;
        }

        /// <summary>
        /// The category ids the filter could actually select in this document — the same
        /// gate CategoryCatalog.Build applies, so the Links dialog and the category list
        /// never disagree about what exists.
        /// </summary>
        private static HashSet<int> OfferableCategories(Document d)
        {
            var ok = new HashSet<int>();
            Categories cats;
            try { cats = d.Settings.Categories; }
            catch { return ok; }

            foreach (Category c in cats)
            {
                if (c == null) continue;
                try
                {
                    // Sub-categories are reached through their parent; an element reports
                    // the top-level category.
                    if (c.Parent != null) continue;
                    if (c.CategoryType != CategoryType.Model) continue;

                    int id = (int)IdUtil.Value(c.Id);
                    var bic = (BuiltInCategory)id;

                    // Prove the engine could put it in a filter, exactly as the catalog does.
                    try { var probe = new ElementMulticategoryFilter(new List<BuiltInCategory> { bic }); }
                    catch { continue; }

                    ok.Add(id);
                }
                catch { /* a category that will not answer questions is not offerable */ }
            }
            return ok;
        }
    }
}
