using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>One category the filter can offer, as Revit itself names it.</summary>
    public class CategoryChoice
    {
        public BuiltInCategory Category;

        /// <summary>Revit's own display name — "Structural Framing", not "StructuralFraming".</summary>
        public string Name;

        /// <summary>
        /// How many instances the model holds. -1 when not counted.
        ///
        /// Revit reports 137 model categories, but plenty of them can never clash —
        /// Areas are 2D, Air Systems and Cable Tray Runs are groupings rather than
        /// geometry, Analysis Results is not geometry at all. Rather than curating a
        /// list by hand (which is exactly what this class replaced), the model is
        /// asked: a category with no elements is not worth offering.
        /// </summary>
        public int Count = -1;

        /// <summary>The model(s) that actually hold elements of this category — host and/or
        /// link names. Filled from the count pass, so it reflects real geometry, not just
        /// which document declares the category.</summary>
        public HashSet<string> Models = new HashSet<string>();

        /// <summary>Provenance for the category row, e.g. "Electrical, HVAC".</summary>
        public string ModelsText { get { return LinkContext.FormatModels(Models); } }
    }

    /// <summary>
    /// The categories available to filter on, read from the document rather than
    /// hardcoded.
    ///
    /// The first version shipped a fixed list of 31 categories chosen by hand, which
    /// meant anything not on that list simply could not be checked — no matter that
    /// Revit has it and the model uses it. Reading `doc.Settings.Categories` instead
    /// means the filter shows what this model actually contains, under Revit's own
    /// names, so it matches what you see in Visibility/Graphics.
    ///
    /// Model categories only: annotation, analytical and internal categories have no
    /// solid geometry to clash. Sub-categories are skipped — you filter on "Walls",
    /// not on each of its sub-parts, and an element reports its top-level category.
    ///
    /// Each candidate is proved usable by actually constructing the filter it will be
    /// used in. Revit rejects some categories there, and finding that out when a
    /// check runs — rather than when the list is built — would mean a filter that
    /// silently matches nothing.
    /// </summary>
    public static class CategoryCatalog
    {
        public static List<CategoryChoice> Build(Document doc)
        {
            var list = new List<CategoryChoice>();
            if (doc == null || !doc.IsValidObject) return Fallback();

            var defaults = new HashSet<BuiltInCategory>(ClashSettings.Selectable);
            var seen = new HashSet<int>();

            // Union categories across the host AND every loaded link, so a category that
            // only a linked discipline uses (Cable Tray in the electrical link, say) is
            // still offered. An element reports the same BuiltInCategory whatever document
            // it lives in, so the union is by BIC and de-dups naturally.
            foreach (var st in LinkContext.ScanDocuments(doc))
            {
                Categories cats;
                try { cats = st.Doc.Settings.Categories; }
                catch (Exception ex) { Log.Warn("Could not read a document's categories", ex); continue; }

                foreach (Category c in cats)
                {
                    if (c == null) continue;

                    try
                    {
                        // Sub-categories are reached through their parent; an element
                        // reports the top-level category, so filtering on a child would
                        // never match.
                        if (c.Parent != null) continue;
                        if (c.CategoryType != CategoryType.Model) continue;

                        var bic = BuiltInOf(c);
                        if (bic == BuiltInCategory.INVALID) continue;
                        if (!seen.Add((int)bic)) continue;

                        // Prove it can actually be used in the filter the engine builds.
                        try { var probe = new ElementMulticategoryFilter(new List<BuiltInCategory> { bic }); }
                        catch { continue; }

                        list.Add(new CategoryChoice
                        {
                            Category = bic,
                            Name = string.IsNullOrEmpty(c.Name) ? ClashSettings.DisplayName(bic) : c.Name
                        });
                    }
                    catch { /* a category that will not answer questions is not offerable */ }
                }
            }

            if (list.Count == 0) return Fallback();

            // Alphabetical, the way Revit lists them in Visibility/Graphics.
            list.Sort(delegate (CategoryChoice a, CategoryChoice b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        // ------------------------------------------------------------------ counts

        private static readonly object CountGate = new object();
        // ONE ENTRY PER SCAN KEY, not a single slot.
        //
        // This was `_countKey` plus one `_counts` — the exact shape ProvisionalAudit had
        // before F1, where a single slot alternating between two documents turned a hash
        // lookup into a full model scan and cost 1.7 s of a 3.0 s check.
        //
        // It never thrashed like F1 did, because nothing calls it alternately in a loop.
        // What it did do is lose the answer whenever the key changed: with two models
        // open — which is a supported way to work since document adoption landed —
        // switching between them evicted the counts, so the next Filter dialog paid a
        // fresh whole-model pass (~2.4 s on Snowdon, five documents, 85,206 elements).
        //
        // Keyed the same way as every other cache here now. The dictionary is dropped
        // wholesale by InvalidateCounts, so it cannot outlive the documents it describes.
        private static readonly Dictionary<string, Dictionary<int, int>> _countsByKey =
            new Dictionary<string, Dictionary<int, int>>();
        // Category id -> the model names that hold elements of it, filled by the same
        // pass and therefore keyed the same way. Keeping it in step with the counts is
        // the point: they are two halves of one answer, and a provenance map that
        // outlived its counts would label categories with the wrong models.
        private static readonly Dictionary<string, Dictionary<int, HashSet<string>>> _provenanceByKey =
            new Dictionary<string, Dictionary<int, HashSet<string>>>();

        /// <summary>Where a resumable pass has got to. The pass spans the host and every
        /// loaded link, one document at a time, so link-only categories get real counts.</summary>
        private static string _passKey;
        private static List<LinkContext.ScanTarget> _passDocs;
        private static int _passDocIx;
        private static FilteredElementIterator _passIt;
        private static Dictionary<int, int> _passCounts;
        private static Dictionary<int, HashSet<string>> _passProvenance;
        private static int _passSeen;

        /// <summary>
        /// Advance the count by at most <paramref name="budgetMs"/>, and say whether it
        /// finished.
        ///
        /// The whole pass is ~6.8 seconds on this model, which is fine to wait for once
        /// but NOT fine to do in one go on Revit's main thread — Idling runs there, so a
        /// single-shot warm would freeze the UI for exactly as long as it saves. Slicing
        /// it means the work happens in the gaps while the user is still orienting, and
        /// the filter dialog opens instantly instead of costing eight seconds the first
        /// time it is asked for.
        /// </summary>
        public static bool WarmStep(Document doc, int budgetMs)
        {
            if (doc == null || !doc.IsValidObject) return true;

            string key = KeyOf(doc);
            lock (CountGate)
                if (_countsByKey.ContainsKey(key)) return true;   // already have it

            try
            {
                if (_passKey != key || _passDocs == null)
                {
                    _passDocs = LinkContext.ScanDocuments(doc);
                    _passDocIx = 0;
                    _passIt = OpenIterator(_passDocs, ref _passDocIx);
                    _passCounts = new Dictionary<int, int>();
                    _passProvenance = new Dictionary<int, HashSet<string>>();
                    _passSeen = 0;
                    _passKey = key;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    if (_passIt == null || !_passIt.MoveNext())
                    {
                        // This document is exhausted — advance to the next one, if any.
                        _passDocIx++;
                        _passIt = OpenIterator(_passDocs, ref _passDocIx);
                        if (_passIt != null) continue;

                        // No documents left: the pass is complete.
                        lock (CountGate) { _countsByKey[key] = _passCounts; _provenanceByKey[key] = _passProvenance; }
                        Log.Info("Category counts warmed on open: " + _passCounts.Count
                                 + " categories from " + _passSeen + " element(s) across "
                                 + _passDocs.Count + " document(s) — the filter dialog no longer pays for this.");
                        _passDocs = null; _passIt = null; _passCounts = null; _passProvenance = null; _passKey = null;
                        return true;
                    }

                    // Attribute to the current document — but only LINKS carry provenance
                    // (host is the unlabelled default), so host-only categories stay untagged.
                    var cur = (_passDocIx >= 0 && _passDocIx < _passDocs.Count) ? _passDocs[_passDocIx] : null;
                    string model = (cur != null && !cur.IsHost) ? cur.ModelName : null;
                    Tally(_passIt.Current as Element, _passCounts, _passProvenance, model);
                    _passSeen++;

                    // Check the clock every so often rather than every element.
                    if ((_passSeen & 0x3FF) == 0 && sw.ElapsedMilliseconds >= budgetMs) return false;
                }
            }
            catch (Exception ex)
            {
                // Abandon rather than carry a half-built answer; the lazy path still works.
                _passDocs = null; _passIt = null; _passCounts = null; _passProvenance = null; _passKey = null;
                Log.Warn("Category count warm-up abandoned; it will build on first use", ex);
                return true;
            }
        }

        /// <summary>
        /// Reset an iterator over the document at <paramref name="ix"/>, skipping any that
        /// refuse a collector, until one opens or the list is exhausted (returns null).
        /// </summary>
        private static FilteredElementIterator OpenIterator(List<LinkContext.ScanTarget> docs, ref int ix)
        {
            while (docs != null && ix < docs.Count)
            {
                try
                {
                    var it = new FilteredElementCollector(docs[ix].Doc)
                        .WhereElementIsNotElementType()
                        .GetElementIterator();
                    it.Reset();
                    return it;
                }
                catch { ix++; }
            }
            return null;
        }

        private static string KeyOf(Document doc)
        {
            // Includes the loaded-link signature, so loading/unloading a link (or toggling
            // link checking) yields a new key and the counts rebuild rather than going stale.
            try { return LinkContext.ScanSignature(LinkContext.ScanDocuments(doc)); }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Instances per category, from ONE pass over the model.
        ///
        /// Per-category collectors would be the obvious approach and are unusable:
        /// constructing a FilteredElementCollector costs O(document) — ~185 ms on a
        /// 310,000-element model — so 137 of them is half a minute. One streamed pass
        /// is about six seconds, and it is cached, so the filter dialog pays it once
        /// per model rather than once per opening.
        /// </summary>
        public static Dictionary<int, int> Counts(Document doc, bool force)
        {
            if (doc == null || !doc.IsValidObject) return new Dictionary<int, int>();

            string key = KeyOf(doc);

            if (!force)
            {
                lock (CountGate)
                {
                    Dictionary<int, int> hit;
                    if (_countsByKey.TryGetValue(key, out hit)) return hit;
                }

                // A warm pass may be part-way through. Finishing it is far cheaper than
                // starting again, and it is the common case: the user clicked Filter
                // before the idle slices got there.
                if (_passKey == key && _passDocs != null)
                {
                    WarmStep(doc, int.MaxValue);
                    lock (CountGate)
                    {
                        Dictionary<int, int> hit;
                        if (_countsByKey.TryGetValue(key, out hit)) return hit;
                    }
                }
            }

            var counts = new Dictionary<int, int>();
            var provenance = new Dictionary<int, HashSet<string>>();
            foreach (var st in LinkContext.ScanDocuments(doc))
            {
                try
                {
                    var it = new FilteredElementCollector(st.Doc)
                        .WhereElementIsNotElementType()
                        .GetElementIterator();
                    it.Reset();
                    string prov = st.IsHost ? null : st.ModelName;
                    while (it.MoveNext()) Tally(it.Current as Element, counts, provenance, prov);
                }
                catch (Exception ex)
                {
                    Log.Warn("Counting elements per category failed for a document", ex);
                }
            }

            lock (CountGate) { _countsByKey[key] = counts; _provenanceByKey[key] = provenance; }
            return counts;
        }

        /// <summary>
        /// One element's contribution. Shared by the resumable pass and the direct one
        /// so the two can never disagree about what counts.
        /// </summary>
        private static void Tally(Element e, Dictionary<int, int> counts,
                                  Dictionary<int, HashSet<string>> provenance, string model)
        {
            if (e == null) return;
            {
                Category c;
                try { c = e.Category; } catch { return; }
                if (c == null) return;

                    // Count only what the engine would ACTUALLY check.
                    //
                    // "Has elements" is the wrong question: Material, Sheet and
                    // Project Information are all Element subclasses, and Piping
                    // Systems and Electrical Circuits are groupings — they have
                    // instances but no solid to clash, so counting them offered 27,333
                    // "Materials" as a clash category. The engine's own two tests are
                    // the honest filter, and using them here means the number shown is
                    // the number that can produce results.
                BoundingBoxXYZ bb;
                try { bb = e.get_BoundingBox(null); } catch { return; }
                if (bb == null) return;
                if (PairSuppressor.IsIgnorableElement(e)) return;

                int id;
                try { id = (int)IdUtil.Value(c.Id); } catch { return; }

                int n;
                counts[id] = counts.TryGetValue(id, out n) ? n + 1 : 1;

                if (provenance != null && !string.IsNullOrEmpty(model))
                {
                    HashSet<string> set;
                    if (!provenance.TryGetValue(id, out set)) { set = new HashSet<string>(); provenance[id] = set; }
                    set.Add(model);
                }
            }
        }

        /// <summary>The model provenance gathered by the count pass for THIS document.</summary>
        private static Dictionary<int, HashSet<string>> Provenance(Document doc, bool force)
        {
            Counts(doc, force);   // fills both halves for this key
            string key = KeyOf(doc);
            lock (CountGate)
            {
                Dictionary<int, HashSet<string>> hit;
                if (_provenanceByKey.TryGetValue(key, out hit) && hit != null) return hit;
            }
            return new Dictionary<int, HashSet<string>>();
        }

        /// <summary>
        /// Drop every cached count and provenance map.
        ///
        /// Keyed per scan now rather than a single slot, so entries no longer evict each
        /// other — which means something has to drop them. Called from
        /// ClashEngine.InvalidateAll alongside the other caches, so the whole set has one
        /// lifetime and none of them can outlive the documents they describe.
        /// </summary>
        public static void InvalidateCounts()
        {
            lock (CountGate) { _countsByKey.Clear(); _provenanceByKey.Clear(); }
        }

        /// <summary>Fills <see cref="CategoryChoice.Count"/> and <see cref="CategoryChoice.Models"/>.</summary>
        public static void ApplyCounts(Document doc, List<CategoryChoice> list, bool force)
        {
            if (list == null) return;
            var counts = Counts(doc, force);
            var prov = Provenance(doc, false);   // just computed by Counts above
            foreach (var c in list)
            {
                int n;
                c.Count = counts.TryGetValue((int)c.Category, out n) ? n : 0;

                HashSet<string> models;
                if (prov.TryGetValue((int)c.Category, out models))
                    foreach (var m in models) c.Models.Add(m);
            }
        }

        private static BuiltInCategory BuiltInOf(Category c)
        {
            try
            {
#if R24_PLUS
                return c.BuiltInCategory;
#else
                return (BuiltInCategory)c.Id.IntegerValue;
#endif
            }
            catch { return BuiltInCategory.INVALID; }
        }

        /// <summary>
        /// Used when there is no document to ask. The curated list is a poor
        /// substitute for the real thing, but it is better than an empty dialog.
        /// </summary>
        private static List<CategoryChoice> Fallback()
        {
            var list = new List<CategoryChoice>();
            foreach (var bic in ClashSettings.Selectable)
                list.Add(new CategoryChoice
                {
                    Category = bic,
                    Name = ClashSettings.DisplayName(bic)
                });
            list.Sort(delegate (CategoryChoice a, CategoryChoice b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }
    }
}
