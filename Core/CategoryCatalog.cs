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
        public static List<CategoryChoice> Build(Document doc, List<LinkContext.ScanTarget> scanDocs = null)
        {
            var list = new List<CategoryChoice>();
            if (doc == null || !doc.IsValidObject) return Fallback();

            var defaults = new HashSet<BuiltInCategory>(ClashSettings.Selectable);
            var seen = new HashSet<int>();

            // Union categories across the host AND every loaded link, so a category that
            // only a linked discipline uses (Cable Tray in the electrical link, say) is
            // still offered. An element reports the same BuiltInCategory whatever document
            // it lives in, so the union is by BIC and de-dups naturally.
            foreach (var st in scanDocs ?? LinkContext.ScanDocuments(doc))
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

        /// <summary>One category count pass's answer: instances per category, and the models that hold them.</summary>
        private sealed class Tallies
        {
            public readonly Dictionary<int, int> Counts = new Dictionary<int, int>();
            // Category id -> the model names that hold elements of it, filled by the same pass and therefore filed the same way.
            // Keeping it in step with the counts is the point: they are two halves of one answer, and a provenance map that
            // outlived its counts would label categories with the wrong models.
            public readonly Dictionary<int, HashSet<string>> Provenance = new Dictionary<int, HashSet<string>>();
        }

        // ONE ENTRY PER SCAN SIGNATURE, not a single slot (see SignedCache).
        //
        // This was `_countKey` plus one `_counts` - the exact shape ProvisionalAudit had before F1, where a single slot
        // alternating between two documents turned a hash lookup into a full model scan and cost 1.7 s of a 3.0 s check.
        // It never thrashed like F1 did, because nothing calls it alternately in a loop. What it did do is lose the answer
        // whenever the key changed: with two models open - a supported way to work since document adoption landed -
        // switching between them evicted the counts, so the next Filter dialog paid a fresh whole-model pass (~2.4 s on
        // Snowdon, five documents, 85,206 elements).
        //
        // U1 (2026-11-07): the key used to be recomputed on EVERY call, and computing it enumerates the host's link instances
        // (a collector, 250-380 ms on testproject even with no links), so each "12 ms" idle slice cost ~270 ms. The signature
        // is now taken ONCE per pass, from the very list the pass walks (SlicedPass.Signature), and the pass's answer is filed
        // under it. A link loaded or unloaded since is a different signature, so it can never be served the wrong set's counts.
        private static readonly SignedCache<Tallies> _cache = new SignedCache<Tallies>();

        /// <summary>The resumable pass, and what it needs to be continued or filed. Spans the host and every loaded link, one
        /// document at a time, so link-only categories get real counts.</summary>
        private static SlicedPass<LinkContext.ScanTarget, Element> _pass;
        private static Tallies _passTallies;
        private static Document _passHost;
        private static List<LinkContext.ScanTarget> _passDocs;

        private static long ClockMs() { return System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency; }

        private static SlicedPass<LinkContext.ScanTarget, Element> NewPass(string signature, List<LinkContext.ScanTarget> docs, Tallies into, int checkEvery)
        {
            return new SlicedPass<LinkContext.ScanTarget, Element>(
                signature, docs,
                st => new ElementWalk(st.Doc),
                (ix, e) =>
                {
                    // Attribute to the current document - but only LINKS carry provenance (host is the unlabelled default),
                    // so host-only categories stay untagged.
                    var cur = (ix >= 0 && ix < docs.Count) ? docs[ix] : null;
                    string model = (cur != null && !cur.IsHost) ? cur.ModelName : null;
                    Tally(e, into.Counts, into.Provenance, model);
                },
                ClockMs, checkEvery);
        }

        private static void DropPass()
        {
            var p = _pass;
            _pass = null; _passTallies = null; _passHost = null; _passDocs = null;
            if (p != null) p.Abandon();   // close the iterator a part-way pass holds (a closed Filter window leaves one)
        }

        /// <summary>
        /// Is every document the part-way pass was started over still a live one? A link that was unloaded (or unloaded and reloaded:
        /// the reload is a NEW Document object) makes the old one invalid, and a pass that carries on over it skips that link without
        /// saying so - an under-count that would then be filed under a signature (paths and titles only) that is the same again after
        /// the reload (U1 review H1). Such a pass is dropped and counted again over the current set.
        /// </summary>
        private static bool PassDocsValid(List<LinkContext.ScanTarget> docs)
        {
            if (docs == null) return true;
            foreach (var d in docs)
            {
                try { if (d == null || d.Doc == null || !d.Doc.IsValidObject) return false; }
                catch { return false; }
            }
            return true;
        }

        /// <summary>The pass is over. File its answer under the signature it walked, or - if it failed part-way, skipped a document
        /// that would not open, or a document it walked has gone since - drop it so the lazy path rebuilds (never a partial answer).</summary>
        private static bool FilePass()
        {
            var pass = _pass; var tallies = _passTallies; var docs = _passDocs;
            DropPass();
            if (pass == null) return false;
            if (pass.Failed)
            {
                Log.Warn("Category count warm-up abandoned; it will build on first use");
                _fellBack = true;
                return false;
            }
            if (!pass.Intact || !PassDocsValid(docs))
            {
                Log.Warn("Category count not kept: " + pass.Skipped + " document(s) could not be read, or a linked model was unloaded or reloaded during the count; it will be counted again on first use");
                _fellBack = true;
                return false;
            }
            _cache.Put(pass.Signature, tallies);
            _fellBack = false;
            Log.Info("Category counts ready: " + tallies.Counts.Count
                     + " categories from " + pass.Seen + " element(s) across "
                     + (docs == null ? 0 : docs.Count) + " document(s).");
            return true;
        }

        /// <summary>How far the part-way count is, 0..1, by documents finished (a single-document model reads 0 until it is done: see
        /// <see cref="CountDetail"/> for the number that keeps moving). 0 when no pass is running.</summary>
        internal static double CountFraction
        {
            get
            {
                var p = _pass;
                if (p == null || p.DocCount == 0) return 0.0;
                return Math.Min(0.99, (double)p.DocIndex / p.DocCount);
            }
        }

        /// <summary>"123,456 elements, model 2 of 5" while a count pass is running, else null. The element count moves every slice.</summary>
        internal static string CountDetail
        {
            get
            {
                var p = _pass;
                if (p == null) return null;
                return p.Seen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " elements"
                       + (p.DocCount > 1 ? ", model " + Math.Min(p.DocCount, p.DocIndex + 1) + " of " + p.DocCount : "");
            }
        }

        /// <summary>Elements the part-way pass has visited, or -1 when no pass is running (the in-Revit checks use it to prove a pass was part-way).</summary>
        internal static int PassSeen { get { var p = _pass; return p == null ? -1 : p.Seen; } }

        /// <summary>The sliced pass failed or lost a document, so the next answer is a one-shot count on the UI thread (the old behaviour, now labelled and logged).</summary>
        internal static bool CountFellBack { get { return _fellBack; } }
        private static bool _fellBack;

        /// <summary>The last answer TalliesFor gave was a one-shot count that is missing a document (one that could not be read, or a link lost meanwhile): some counts may be low.</summary>
        internal static bool CountWasPartial { get { return _lastPartial; } }
        private static bool _lastPartial;

        /// <summary>
        /// Advance the per-category element count by at most <paramref name="budgetMs"/>, and say whether it finished.
        ///
        /// The whole pass is ~7 seconds on a 300,000-element model and 56 s cold on a busy machine, which is fine to wait for
        /// once but NOT fine to do in one go on Revit's main thread. Slicing it means the work happens in the gaps - the idle
        /// warm-up between keystrokes, or the Filter window's own pump - and the filter dialog opens instantly.
        ///
        /// <paramref name="scanDocs"/> is the document list the caller already has for this moment (one enumeration of the
        /// links per Filter press, not one per call); null = take it now.
        /// </summary>
        public static bool WarmStep(Document doc, int budgetMs, List<LinkContext.ScanTarget> scanDocs = null)
        {
            if (doc == null || !doc.IsValidObject) return true;

            // A fixed list the caller holds (the Filter press takes it once) that has a lost document in it would restart the pass on every slice and never
            // finish: take the current set instead, once (U1 review N7).
            if (scanDocs != null && !PassDocsValid(scanDocs)) scanDocs = LinkContext.ScanDocuments(doc);

            try
            {
                // A pass that is part-way keeps walking the document set it STARTED with (its signature is fixed), which is what the
                // idle warm-up wants. A caller that has just taken the current set (the Filter window's pump) and finds it is a
                // different one - a link was loaded or unloaded since - gets a new pass for ITS set, not the stale one's answer: the
                // stale pass would be filed under its own signature, miss on this caller's lookup, and the caller would then pay a
                // one-shot count on the UI thread, which is the freeze this exists to remove.
                if (_pass != null && ReferenceEquals(_passHost, doc) && scanDocs != null
                    && !_pass.IsFor(LinkContext.ScanSignature(scanDocs)))
                    DropPass();

                // ...and whichever caller it is, a pass whose documents have gone invalid since it started (a link unloaded, or unloaded
                // and reloaded - the same signature again, but a new Document) is dropped, never carried on past the lost link.
                if (_pass != null && !PassDocsValid(_passDocs)) DropPass();

                if (_pass == null || !ReferenceEquals(_passHost, doc))
                {
                    var docs = scanDocs ?? LinkContext.ScanDocuments(doc);       // ONCE per pass
                    string signature = LinkContext.ScanSignature(docs);          // from the SAME list the pass will walk
                    Tallies have;
                    if (_cache.TryGet(signature, out have)) { _fellBack = false; return true; }   // already have it

                    DropPass();
                    _passTallies = new Tallies();
                    _passHost = doc;
                    _passDocs = docs;
                    _pass = NewPass(signature, docs, _passTallies, 1024);
                }

                if (!_pass.Step(budgetMs)) return false;
                FilePass();
                return true;
            }
            catch (Exception ex)
            {
                // Abandon rather than carry a half-built answer; the lazy path still works.
                DropPass();
                Log.Warn("Category count warm-up abandoned; it will build on first use", ex);
                _fellBack = true;
                return true;
            }
        }

        /// <summary>
        /// Instances per category, from ONE pass over the model.
        ///
        /// Per-category collectors would be the obvious approach and are unusable:
        /// constructing a FilteredElementCollector costs O(document) - ~185 ms on a
        /// 310,000-element model - so 137 of them is half a minute. One streamed pass
        /// is about six seconds, and it is cached, so the filter dialog pays it once
        /// per model rather than once per opening.
        /// </summary>
        public static Dictionary<int, int> Counts(Document doc, bool force)
        {
            return TalliesFor(doc, force, null).Counts;
        }

        private static Tallies TalliesFor(Document doc, bool force, List<LinkContext.ScanTarget> scanDocs)
        {
            if (doc == null || !doc.IsValidObject) return new Tallies();

            if (scanDocs != null && !PassDocsValid(scanDocs)) scanDocs = null;   // a fixed list with a lost document: take the current set
            var docs = scanDocs ?? LinkContext.ScanDocuments(doc);               // ONE enumeration of the links per call
            string signature = LinkContext.ScanSignature(docs);

            if (!force)
            {
                Tallies hit;
                if (_cache.TryGet(signature, out hit)) { _fellBack = false; _lastPartial = false; return hit; }

                // A warm pass may be part-way through, over exactly this document set. Finishing it is far cheaper than
                // starting again, and it is the common case: the user clicked Filter before the idle slices got there.
                if (_pass != null && !PassDocsValid(_passDocs)) DropPass();   // a lost or reloaded link: never finish this pass (U1 review H1)
                if (_pass != null && ReferenceEquals(_passHost, doc) && _pass.IsFor(signature))
                {
                    _pass.Step(int.MaxValue);
                    if (FilePass() && _cache.TryGet(signature, out hit)) { _lastPartial = false; return hit; }
                }
            }

            // One shot. Each document is walked on its own, so one that will not enumerate costs only its own counts.
            // This is also where a failed sliced pass lands: the old behaviour (a full count on the UI thread, 10-60 s cold), so it is logged.
            Log.Info("Counting without slices" + (_fellBack ? " after an error" : "") + " (" + docs.Count + " document(s)).");
            var tallies = new Tallies();
            bool whole = true;
            for (int i = 0; i < docs.Count; i++)
            {
                var one = new List<LinkContext.ScanTarget> { docs[i] };
                var model = docs[i].IsHost ? null : docs[i].ModelName;
                var pass = new SlicedPass<LinkContext.ScanTarget, Element>(
                    signature, one, st => new ElementWalk(st.Doc),
                    (ix, e) => Tally(e, tallies.Counts, tallies.Provenance, model), ClockMs, 1024);
                pass.Step(int.MaxValue);
                if (!pass.Intact) { whole = false; Log.Warn("Counting elements per category failed for a document"); }
            }

            // An answer that is missing a document is returned for this call but NOT kept: filing it would serve the under-count
            // again for as long as this document set stays the current one.
            bool kept = whole && PassDocsValid(docs);
            if (kept) _cache.Put(signature, tallies);
            _lastPartial = !kept;
            _fellBack = false;
            return tallies;
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
                    // Systems and Electrical Circuits are groupings - they have
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

        /// <summary>
        /// Drop every cached count and provenance map, and any pass still part-way.
        ///
        /// Keyed per scan now rather than a single slot, so entries no longer evict each
        /// other - which means something has to drop them. Called from
        /// ClashEngine.InvalidateAll alongside the other caches, so the whole set has one
        /// lifetime and none of them can outlive the documents they describe.
        /// </summary>
        public static void InvalidateCounts()
        {
            _cache.Clear();
            DropPass();
            _fellBack = false; _lastPartial = false;
        }

        /// <summary>Fills <see cref="CategoryChoice.Count"/> and <see cref="CategoryChoice.Models"/>.</summary>
        public static void ApplyCounts(Document doc, List<CategoryChoice> list, bool force)
        {
            ApplyCounts(doc, list, force, null);
        }

        /// <summary>As above, given the scan documents the caller already holds (one link enumeration per press, not several).</summary>
        public static void ApplyCounts(Document doc, List<CategoryChoice> list, bool force, List<LinkContext.ScanTarget> scanDocs)
        {
            if (list == null) return;
            var t = TalliesFor(doc, force, scanDocs);     // counts and provenance are two halves of one answer
            foreach (var c in list)
            {
                int n;
                c.Count = t.Counts.TryGetValue((int)c.Category, out n) ? n : 0;

                HashSet<string> models;
                if (t.Provenance.TryGetValue((int)c.Category, out models))
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
