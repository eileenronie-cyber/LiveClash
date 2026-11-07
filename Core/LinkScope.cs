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
            var c = new Counter(linkDoc);
            c.Step(int.MaxValue);
            return c.Counts;
        }

        /// <summary>
        /// <see cref="CountByCategory"/> a slice at a time (U1): the pass over a link is the same one streamed walk, resumable, so the
        /// Filter window can count each link in the gaps instead of before it opens. On a big link the one-shot pass is seconds
        /// of a grey Revit (1.06 s for four links on Snowdon; far more on the 16-document model).
        /// </summary>
        public sealed class Counter
        {
            private readonly Dictionary<BuiltInCategory, int> _counts = new Dictionary<BuiltInCategory, int>();
            private readonly HashSet<int> _offerable;
            private readonly SlicedPass<Document, Element> _pass;

            public Counter(Document linkDoc)
            {
                _offerable = new HashSet<int>();
                if (linkDoc == null || !linkDoc.IsValidObject) return;
                _offerable = OfferableCategories(linkDoc);
                if (_offerable.Count == 0) return;

                _pass = new SlicedPass<Document, Element>("link", new List<Document> { linkDoc },
                    d => new ElementWalk(d), (ix, e) => Visit(e),
                    () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency, 1024);
            }

            /// <summary>Per category, so far. A link that will not enumerate simply shows the counts it reached.</summary>
            public Dictionary<BuiltInCategory, int> Counts { get { return _counts; } }

            /// <summary>Advance by about <paramref name="budgetMs"/>. True when this link is finished.</summary>
            public bool Step(int budgetMs)
            {
                return _pass == null || _pass.Step(budgetMs);
            }

            private void Visit(Element e)
            {
                if (e == null) return;

                Category cat;
                try { cat = e.Category; } catch { return; }
                if (cat == null) return;

                int id;
                try { id = (int)IdUtil.Value(cat.Id); } catch { return; }
                if (!_offerable.Contains(id)) return;

                // Only geometry can clash — match the engine's own tests so the number
                // shown is the number that can produce a result.
                try { if (e.get_BoundingBox(null) == null) return; } catch { return; }
                try { if (PairSuppressor.IsIgnorableElement(e)) return; } catch { return; }

                var bic = (BuiltInCategory)id;
                int n;
                _counts[bic] = _counts.TryGetValue(bic, out n) ? n + 1 : 1;
            }
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
