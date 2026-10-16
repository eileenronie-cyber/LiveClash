using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The document-wide work that must happen once per scan rather than once
    /// per element: which links are loaded, and the target id set in the host
    /// document and in each link.
    /// </summary>
    public class ScanContext
    {
        public List<LinkContext> Links = new List<LinkContext>();

        /// <summary>Spatial index over the host document's target elements.</summary>
        public TargetIndex HostTargets;

        /// <summary>One index per loaded link, in that link's own coordinates.</summary>
        public List<KeyValuePair<LinkContext, TargetIndex>> LinkTargets =
            new List<KeyValuePair<LinkContext, TargetIndex>>();

        /// <summary>
        /// The category filter each link was collected with (its target categories minus the ones
        /// excluded for that link). Kept so a pipe reached by way of its INSULATION can be asked the
        /// same question the pipe itself would have been: is this a target for THIS link?
        /// </summary>
        public Dictionary<LinkContext, ElementFilter> LinkTargetFilters =
            new Dictionary<LinkContext, ElementFilter>();

        private readonly Dictionary<LinkContext, TargetIndex> _linkInsulation =
            new Dictionary<LinkContext, TargetIndex>();

        /// <summary>
        /// A spatial index over the pipe and duct INSULATION elements of one link, in that link's
        /// own coordinates - what an insulation-aware set tests a host element against, so that a
        /// linked pipe is reached by its insulation and not only by its bare surface.
        ///
        /// Built on first use and then held with the rest of the context, so it lives and dies with
        /// it: a link that reloads, moves or changes drops the whole context, and this with it.
        /// Lazy rather than built with <see cref="LinkTargets"/> because most sets are not
        /// insulation-aware and most links have none, and because contexts are shared between sets
        /// of the same scope - one asking for insulation must not depend on the other's flag.
        /// Null when the collection failed (logged once); callers treat that as "nothing found".
        /// </summary>
        public TargetIndex LinkInsulationIndex(LinkContext link)
        {
            if (link == null || link.Document == null || !link.Document.IsValidObject) return null;

            TargetIndex idx;
            if (_linkInsulation.TryGetValue(link, out idx)) return idx;

            try
            {
                var ids = new FilteredElementCollector(link.Document)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                    {
                        BuiltInCategory.OST_PipeInsulations,
                        BuiltInCategory.OST_DuctInsulations
                    }))
                    .ToElementIds();
                idx = TargetIndex.Build(link.Document, ids);
            }
            catch (Exception ex)
            {
                Log.Warn("Insulation collection failed in link " + link.Name
                         + " - its insulation will not be checked against.", ex);
                idx = null;
            }
            _linkInsulation[link] = idx;
            return idx;
        }

        private TargetIndex _hostInsulation;
        private bool _hostInsulationBuilt;

        /// <summary>
        /// A spatial index over the pipe and duct INSULATION elements of the HOST document - what an
        /// insulation-aware set tests a source element against so that an insulated pipe or duct that is
        /// only ever a TARGET is reached by its insulation and not only by its bare surface.
        ///
        /// Built on first use, then kept correct by <see cref="PatchHostInsulation"/> as the model changes
        /// (unlike a link's, the host is edited all the time, and an index that went stale would silently
        /// miss the insulation someone had just added). Null when the collection failed (logged once);
        /// callers treat that as "nothing found".
        /// </summary>
        public TargetIndex HostInsulationIndex(Document doc)
        {
            if (doc == null || !doc.IsValidObject) return null;
            if (_hostInsulationBuilt) return _hostInsulation;

            try
            {
                var ids = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                    {
                        BuiltInCategory.OST_PipeInsulations,
                        BuiltInCategory.OST_DuctInsulations
                    }))
                    .ToElementIds();
                _hostInsulation = TargetIndex.Build(doc, ids);
            }
            catch (Exception ex)
            {
                Log.Warn("Insulation collection failed in the host - insulated targets will be checked bare only.", ex);
                _hostInsulation = null;
            }
            _hostInsulationBuilt = true;
            return _hostInsulation;
        }

        /// <summary>Categories whose elements can CARRY insulation. Only these are asked for theirs when they change.</summary>
        private static readonly HashSet<BuiltInCategory> InsulationHostCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves
        };

        /// <summary>
        /// Keep the host insulation index in step with an edit. A no-op until the index has been built
        /// (it then reads the model as it is). An insulation element that changed is re-indexed; one that
        /// was deleted is dropped; and when an element that can CARRY insulation changed - moved, resized -
        /// its insulation is re-indexed too, in case the change was not reported for the insulation itself.
        /// </summary>
        internal void PatchHostInsulation(Document doc, ICollection<ElementId> addedOrModified, ICollection<ElementId> deleted)
        {
            if (!_hostInsulationBuilt || _hostInsulation == null || doc == null) return;

            if (deleted != null)
                foreach (var id in deleted) _hostInsulation.Remove(id);

            if (addedOrModified == null) return;
            foreach (var id in addedOrModified)
            {
                Element el;
                try { el = doc.GetElement(id); } catch { continue; }
                if (el == null || !el.IsValidObject) { _hostInsulation.Remove(id); continue; }

                var bic = PairSuppressor.GetBuiltInCategory(el);
                if (bic == BuiltInCategory.OST_PipeInsulations || bic == BuiltInCategory.OST_DuctInsulations)
                    _hostInsulation.Insert(doc, id);
                else if (InsulationHostCategories.Contains(bic))
                {
                    var owned = PairVerifier.InsulationIdsOf(doc, el, "");
                    if (owned != null) foreach (var insId in owned) _hostInsulation.Insert(doc, insId);
                }
            }
        }
    }

    /// <summary>
    /// "What are the targets, and where are they?" — split out of ClashEngine.
    ///
    /// Building the per-document target set is the one expensive thing that must NOT
    /// happen per element, so this class owns both the build (<see cref="BuildContext"/>)
    /// and the warm cache in front of it (<see cref="GetContext"/>) that keeps the
    /// index correct across a whole modelling session via incremental patching
    /// (<see cref="NoteChanged"/>). ClashEngine consumes a <see cref="ScanContext"/>;
    /// nothing about producing one lives there any more.
    /// </summary>
    public static class ClashContext
    {
        /// <summary>
        /// Collect the links and the target id sets. One document-wide query per
        /// document — see the class comment for why this must not be per element.
        /// </summary>
        public static ScanContext BuildContext(Document doc, ClashSettings settings, ClashSet set = null)
        {
            var ctx = new ScanContext();
            if (doc == null || !doc.IsValidObject) return ctx;

            // The set this context is FOR. Null means the active one - the UI path.
            // The engine always passes it explicitly; nothing here may read Active.
            set = set ?? settings.Active;

            WarnUndetectableTargets(doc, set);

            if (settings.CheckHostVsHost)
            {
                try
                {
                    var ids = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .WherePasses(settings.BuildTargetFilter(set))
                        .ToElementIds();
                    ctx.HostTargets = TargetIndex.Build(doc, ids);
                }
                catch (Exception ex) { Log.Warn("Host target collection failed", ex); }
            }

            if (settings.CheckHostVsLinks)
            {
                ctx.Links = LinkContext.CollectFrom(doc);
                foreach (var link in ctx.Links)
                {
                    if (link.Document == null || !link.Document.IsValidObject) continue;

                    // Per-link category control: from this link, collect only the target
                    // categories not excluded for it. Null means every target category was
                    // excluded, so the link contributes nothing — skip its index entirely.
                    var linkFilter = settings.BuildTargetFilterForLink(set, link.Name);
                    if (linkFilter == null)
                    {
                        Log.Info("Link '" + link.Name + "': all target categories excluded — not collected.");
                        continue;
                    }

                    ctx.LinkTargetFilters[link] = linkFilter;

                    try
                    {
                        var ids = new FilteredElementCollector(link.Document)
                            .WhereElementIsNotElementType()
                            .WherePasses(linkFilter)
                            .ToElementIds();

                        // Boxes stay in LINK coordinates; the host box is transformed
                        // into link space at query time instead.
                        ctx.LinkTargets.Add(new KeyValuePair<LinkContext, TargetIndex>(
                            link, TargetIndex.Build(link.Document, ids)));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Target collection failed in link " + link.Name, ex);
                    }
                }
            }

            return ctx;
        }

        /// <summary>
        /// Target categories Revit's own interference logic can never report, so the
        /// user is told rather than handed a clean result the engine was structurally
        /// incapable of producing.
        ///
        /// The API guide is explicit that "elements which have no solid geometry, such
        /// as Rebar, will not pass this filter", that concrete members automatically
        /// joined at their intersections will not, and that "site elements ... are also
        /// excluded from interference checks". Hard hits come entirely from
        /// ElementIntersectsElementFilter, so every one of those exclusions is
        /// inherited. None of these categories exist in PRJ01; the warning is for the
        /// next project.
        /// </summary>
        private static void WarnUndetectableTargets(Document doc, ClashSet set)
        {
            var blind = new Dictionary<BuiltInCategory, string>
            {
                { BuiltInCategory.OST_Topography,  "site elements are excluded from interference checks" },
                { BuiltInCategory.OST_Rebar,       "no solid geometry, never passes the intersection filter" },
                { BuiltInCategory.OST_AreaRein,    "no solid geometry, never passes the intersection filter" },
                { BuiltInCategory.OST_PathRein,    "no solid geometry, never passes the intersection filter" },
                { BuiltInCategory.OST_FabricAreas, "no solid geometry, never passes the intersection filter" },
            };

            var hit = new List<string>();
            foreach (var kv in blind)
            {
                if (!set.TargetCategories.Contains(kv.Key)) continue;

                int n = 0;
                try
                {
                    n = new FilteredElementCollector(doc)
                        .OfCategory(kv.Key).WhereElementIsNotElementType().GetElementCount();
                }
                catch { }
                if (n == 0) continue;      // in the filter but absent: nothing to miss

                hit.Add(ClashSettings.DisplayName(kv.Key) + " (" + n + ") — " + kv.Value);
            }

            if (hit.Count > 0)
                Log.Warn("Target categories Revit cannot report clashes for: "
                         + string.Join("; ", hit.ToArray())
                         + ". These will always come back clean.");

            // Not a category, so it cannot be counted the same way — but worth saying
            // once when structure is in scope at all.
            bool structural = set.TargetCategories.Contains(BuiltInCategory.OST_StructuralFraming)
                           || set.TargetCategories.Contains(BuiltInCategory.OST_StructuralColumns)
                           || set.TargetCategories.Contains(BuiltInCategory.OST_StructuralFoundation)
                           || set.TargetCategories.Contains(BuiltInCategory.OST_Walls)
                           || set.TargetCategories.Contains(BuiltInCategory.OST_Floors);
            if (structural)
                Log.Info("Note: concrete members automatically joined at their intersections "
                         + "are excluded from Revit's interference logic and cannot be reported.");
        }

        // ------------------------------------------------------------ context cache
        //
        // One warm ScanContext PER distinct scope, keyed by everything BuildContext reads
        // — see ContextSignature. Live multi-set checking walks every enabled set in turn;
        // a single slot would rebuild the ~130 ms index each time the loop moved to a set
        // with different targets, so each edit would blow the budget and defer. With an
        // entry per scope, two sets that share one share a warm index, and a set the loop
        // just visited is still warm when the next edit arrives. The set cap keeps this
        // dictionary to a handful of entries.

        private static readonly object CacheGate = new object();
        private static string _cacheDocKey;

        private sealed class CacheEntry
        {
            public ScanContext Ctx;
            public HashSet<BuiltInCategory> Targets;
            public DateTime AtUtc;
        }

        private static readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>();

        /// <summary>
        /// EVERYTHING BuildContext READS, so a cached context is only reused for a scope
        /// that would rebuild identically.
        ///
        /// The key used to be the target categories alone, while BuildContext also read
        /// CheckHostVsHost, CheckHostVsLinks and — through BuildTargetFilterForLink — the
        /// ACTIVE SET's DisabledLinks and LinkExclusions. Three ways that went wrong, all
        /// on the live path (a full check calls BuildContext directly and was always right):
        ///
        ///   - CheckElementsAllSets swaps ActiveSet and asks for a context per set. Two
        ///     enabled sets with the same target categories but different link scoping
        ///     shared one context: whichever built first won, and the other silently
        ///     checked the wrong links.
        ///   - Changing link scoping in the Filter window did not change the key, so live
        ///     checking kept the old scoping for up to the 10-minute TTL.
        ///   - Turning "host vs links" off did nothing until the context happened to
        ///     rebuild, because CheckElements loops over ctx.LinkTargets unconditionally
        ///     (it tests CheckHostVsHost per element, but not CheckHostVsLinks).
        ///
        /// Order is normalised on both the link names and the category ids, so two
        /// equivalent scopes cannot produce two keys and quietly double the cache.
        /// </summary>
        private static string ContextSignature(ClashSettings s, ClashSet set)
        {
            // Read from the SET, never from s.Active - the whole point of Step 1.
            var set2 = set ?? s.Active;
            var sb = new System.Text.StringBuilder();
            sb.Append(TargetSignature(set2.TargetCategories));
            sb.Append('|').Append(s.CheckHostVsHost ? '1' : '0');
            sb.Append(s.CheckHostVsLinks ? '1' : '0');

            // Disabled links, sorted.
            sb.Append("|off:");
            var off = new List<string>(set2.DisabledLinks ?? new HashSet<string>());
            off.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in off) sb.Append(n).Append(';');

            // Per-link category exclusions, links sorted, categories sorted within each.
            sb.Append("|excl:");
            var excl = set2.LinkExclusions;
            if (excl != null && excl.Count > 0)
            {
                var names = new List<string>(excl.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var n in names)
                {
                    HashSet<BuiltInCategory> cats;
                    if (!excl.TryGetValue(n, out cats) || cats == null || cats.Count == 0) continue;
                    sb.Append(n).Append('=');
                    var ids = new List<int>();
                    foreach (var c in cats) ids.Add((int)c);
                    ids.Sort();
                    foreach (var id in ids) sb.Append(id).Append(',');
                    sb.Append(';');
                }
            }
            return sb.ToString();
        }

        /// <summary>Order-independent key for a target-category set. One component of
        /// <see cref="ContextSignature"/>, not a key in its own right.</summary>
        private static string TargetSignature(HashSet<BuiltInCategory> targets)
        {
            if (targets == null || targets.Count == 0) return "";
            var ids = new List<int>(targets.Count);
            foreach (var c in targets) ids.Add((int)c);
            ids.Sort();
            var sb = new System.Text.StringBuilder();
            foreach (var id in ids) sb.Append(id).Append(',');
            return sb.ToString();
        }

        /// <summary>
        /// Backstop only. The index is kept correct incrementally by
        /// <see cref="NoteChanged"/>, so this exists purely for changes the updater
        /// cannot see — a target-category element edited when that category is NOT
        /// also a source, which no trigger fires for.
        ///
        /// It was 30 seconds, and that was wrong: a rebuild costs ~330 ms on a
        /// 40,000-target model, and at real modelling pace most edits land more than
        /// 30 s after the last one, so nearly every edit paid full price and blew the
        /// budget — visible as repeated "Budget hit" lines in the log.
        /// </summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The cached form of <see cref="BuildContext"/>.
        ///
        /// WHY THIS EXISTS (measured in Revit 2026, 2026-08-20): BuildContext costs
        /// 121-161 ms on a 310k-element model, and the updater's stopwatch is already
        /// running when it is called. Against the 120 ms TimeBudgetMs that meant the
        /// budget was blown before the FIRST element was ever examined, so every edit
        /// deferred 100% of its elements to idle — measured as deferred=1 of 1,
        /// 4 of 4, 40 of 40 — while still costing ~130 ms of frozen cursor to build a
        /// context it then threw away. The inline pass, the entire point of an
        /// IUpdater, detected nothing at all.
        ///
        /// Cached, a single-element edit costs ~35-47 ms and lands comfortably inside
        /// the budget, which is what makes detection genuinely live.
        ///
        /// Invalidated three ways: the SCOPE changing — target categories, the two
        /// CheckHostVs* switches, or the active set's per-link scoping, all compared by
        /// value via <see cref="ContextSignature"/> so no caller has to remember to
        /// announce it — the TTL above, and an explicit <see cref="InvalidateCache"/> on
        /// document open/close or when a stale id is detected.
        /// </summary>
        public static ScanContext GetContext(Document doc, ClashSettings settings, ClashSet set = null)
        {
            if (doc == null || !doc.IsValidObject) return new ScanContext();

            string docKey = ClashEngine.DocKey(doc);
            set = set ?? settings.Active;
            string sig = ContextSignature(settings, set);

            lock (CacheGate)
            {
                // A new document invalidates every entry — the ids belong to the old one.
                if (_cacheDocKey != docKey) { _cache.Clear(); _cacheDocKey = docKey; }

                CacheEntry e;
                if (_cache.TryGetValue(sig, out e) && (DateTime.UtcNow - e.AtUtc) < CacheTtl)
                {
                    // VALIDATE EVERY LINK BEFORE HANDING THE CACHE OUT, not just the scope
                    // signature and the age. A link reload or unload closes that link's
                    // background Document — measured directly in Revit 2026: a
                    // RevitLinkType.Reload() flips the OLD Document reference's
                    // IsValidObject from true to false and GetLinkDocument() comes back
                    // as a genuinely different object — but it does NOT raise
                    // DocumentClosing or DocumentOpened on Application, confirmed the same
                    // way, by subscribing directly and watching neither fire for
                    // Reload(), nor for an explicit Unload()+Load() pair. So event-driven
                    // invalidation cannot be the mechanism this relies on; it is checked
                    // here instead, at the one place that is asked whether the cache is
                    // still good, which cannot be skipped by however Revit chooses to
                    // implement a reload.
                    //
                    // Before this, a cache entry built with a link that later reloaded or
                    // unloaded stayed valid for up to the 10-minute TTL. CheckAgainstLink
                    // met the dead Document, returned no results, and logged nothing — a
                    // pipe edited in that window silently lost every one of its existing
                    // link clashes from the panel. Checking IsValidObject on a handful of
                    // links costs nothing next to the signature comparison already made
                    // above; a stale entry is simply dropped and rebuilt exactly as an
                    // expired one already is below.
                    bool linksOk = true;
                    if (e.Ctx != null && e.Ctx.LinkTargets != null)
                        foreach (var kv in e.Ctx.LinkTargets)
                        {
                            var linkDoc = kv.Key == null ? null : kv.Key.Document;
                            if (linkDoc == null || !linkDoc.IsValidObject) { linksOk = false; break; }
                        }
                    if (linksOk) return e.Ctx;
                }
            }

            // Build outside the lock — it is the slow part, and a duplicate build
            // under contention is harmless.
            var ctx = BuildContext(doc, settings, set);

            lock (CacheGate)
            {
                if (_cacheDocKey != docKey) { _cache.Clear(); _cacheDocKey = docKey; }
                _cache[sig] = new CacheEntry
                {
                    Ctx = ctx,
                    Targets = new HashSet<BuiltInCategory>(set.TargetCategories),
                    AtUtc = DateTime.UtcNow
                };
            }

            return ctx;
        }

        /// <summary>
        /// Ask every enabled set for its context during idle time, so a context that has gone
        /// stale (past <see cref="CacheTtl"/>, or a link that reloaded/unloaded) is rebuilt on an
        /// idle tick rather than by the next live edit.
        ///
        /// Found reviewing the whole codebase, 2026-09-27: nothing ever asked early, so once every
        /// ~10 minutes the NEXT live edit's own call to GetContext found the cache gone cold and
        /// paid the same 121-330 ms rebuild TryWarmIndex exists specifically to keep off the edit
        /// path — but this time inside the edit's own transaction, since CheckElementsAllSets runs
        /// from the updater. GetContext ALREADY does the right thing cheaply when a context is
        /// still fresh (one dictionary lookup and an age comparison) — this only has to call it;
        /// there is no separate "close to stale" state worth tracking here; OnIdling fires every
        /// couple of seconds while Revit is idle, so the rebuild lands within that window of the
        /// context actually going stale, comfortably before most edits would otherwise hit it cold.
        /// A set with no context yet gets one built here too (harmless — TryWarmIndex already
        /// does the same at document-open, so this is normally a no-op the rest of the session).
        /// </summary>
        public static void RefreshStaleSoon(Document doc, ClashSettings settings)
        {
            if (doc == null || !doc.IsValidObject || settings == null) return;

            foreach (var set in settings.Sets)
            {
                if (!set.Enabled) continue;
                try { GetContext(doc, settings, set); }
                catch (Exception ex) { Log.Warn("Idle context refresh failed for set '" + set.Name + "'", ex); }
            }
        }

        /// <summary>
        /// Patch the cached index with the elements the updater just handed us,
        /// instead of throwing the whole thing away. Cheap — microseconds for a
        /// handful of elements against a ~330 ms rebuild — and it is what lets the
        /// cache stay warm across an entire modelling session.
        ///
        /// Only elements in a TARGET category matter here; a source-only element is
        /// never something another element can be checked against.
        ///
        /// EXCEPT a RevitLinkInstance, which this INVALIDATES rather than patches —
        /// see the note below. A link instance's own category (OST_RvtLinks) is
        /// never anyone's declared target, so without this check it fell through
        /// this method doing nothing, silently, which is exactly what let it go
        /// stale.
        ///
        /// Does nothing when there is no cached context yet: the next GetContext will
        /// build a fresh one that already includes these changes.
        /// </summary>
        public static void NoteChanged(
            Document doc,
            ICollection<ElementId> addedOrModified,
            ICollection<ElementId> deleted,
            ClashSettings settings)
        {
            if (doc == null || !doc.IsValidObject) return;

            // Patch EVERY warm entry: a changed target element affects every set whose
            // targets include it, not just the active one. Each entry keeps its own
            // target set, so membership is decided per entry.
            List<CacheEntry> entries;
            lock (CacheGate)
            {
                if (_cacheDocKey != ClashEngine.DocKey(doc) || _cache.Count == 0) return;
                entries = new List<CacheEntry>(_cache.Values);
            }

            // A LINK INSTANCE CANNOT BE PATCHED THE WAY A NORMAL TARGET ELEMENT CAN.
            //
            // ctx.LinkTargets holds one TargetIndex per loaded link, built once from
            // that instance's placement transform (LinkContext.ToHost/ToLink) at
            // BuildContext time — see LinkContext.CollectFrom. Everything below this
            // point only ever writes into ctx.HostTargets, keyed by the changed
            // element's OWN category, so an instance being added, moved or deleted
            // never touched LinkTargets at all: a new instance was simply absent from
            // the list that was built before it existed; a moved instance kept the
            // stale transform its solids were checked through; a deleted instance's
            // TargetIndex just stayed in the cache, still queryable, at the position
            // it no longer occupies.
            //
            // MEASURED LIVE ON SNOWDON, both proven, not inferred: a second copy of
            // the HVAC link placed exactly on the first was never checked against
            // for the rest of the cache's life — nothing had added it. The HVAC link
            // moved 1000 ft was still checked at its ORIGINAL position — nothing had
            // moved it. Both lasted until the next full rebuild (the 10-minute TTL,
            // the Filter/Audit window applying, Recheck, or a model switch), and a
            // pair that read as "not found" in that window is what the resolution
            // audit (see PairVerifier's callers) can write to the ledger as a fix
            // that never happened — the exact class of bug D1 above exists to stop,
            // reached by a different door.
            //
            // A moved instance cannot be incrementally patched in any case: a link
            // moving means every one of its target elements' cached positions needs
            // re-transforming, which is most of what a rebuild already does. So this
            // does not try — a link-instance change of any kind INVALIDATES the whole
            // cache and hands it to the same rebuild path D1 already proved correct,
            // rather than inventing a second, partial repair mechanism for one
            // structure. NoteChanged (via DocumentChanged) does not cover a link's LOADED
            // STATE changing — reloading, unloading, or loading one that was unloaded
            // when the cache was built. This is NOT because the event fails to fire —
            // Load() was measured to raise DocumentChanged after all (TransactionCommitted,
            // naming the modified RevitLinkType). What it never carries is the
            // RevitLinkInstance itself, only the RevitLinkType, so the instance-scan above
            // has nothing to match regardless of whether the event fires (Unload/Reload's
            // DocumentChanged behavior was not separately measured; only that none of the
            // three raise DocumentClosing or DocumentOpened, per D1). Reload and unload of
            // an ALREADY-CACHED link are covered
            // regardless, by GetContext's own Document.IsValidObject check — both were
            // measured to invalidate the cached reference. The remaining case — a link
            // that was never cached because it was unloaded at build time, later loaded
            // — is covered by LiveClashApp.OnLinkedResourceOpened instead, subscribed to
            // Application.LinkedResourceOpened, which DOES fire reliably for Load() and
            // Reload() (confirmed directly) though not for Unload() (which needs no
            // separate handling, per the above). Three mechanisms for one problem because
            // no single one sees every route to it: NoteChanged for an ordinary
            // transaction changing a link INSTANCE, GetContext for a CACHED link's
            // Document going invalid, LinkedResourceOpened for a link being LOADED that
            // the cache never knew about. All three were found by testing what actually
            // fires, not by assuming a smaller set would do.
            bool linkInstanceChanged = false;

            if (addedOrModified != null)
                foreach (var id in addedOrModified)
                {
                    Element el;
                    try { el = doc.GetElement(id); } catch { continue; }
                    if (el is RevitLinkInstance) { linkInstanceChanged = true; break; }
                }

            if (!linkInstanceChanged && deleted != null)
            {
                // The element is already gone by the time this runs, so it cannot be
                // re-queried for its type — ask whether the CACHE remembers it as a
                // link instance instead, since that is the only record left of what
                // it was.
                var deletedIds = new HashSet<long>();
                foreach (var id in deleted) deletedIds.Add(IdUtil.Value(id));

                foreach (var e in entries)
                {
                    if (e.Ctx == null || e.Ctx.LinkTargets == null) continue;
                    foreach (var kv in e.Ctx.LinkTargets)
                        if (kv.Key != null && deletedIds.Contains(IdUtil.Value(kv.Key.InstanceId)))
                        { linkInstanceChanged = true; break; }
                    if (linkInstanceChanged) break;
                }
            }

            if (linkInstanceChanged)
            {
                InvalidateCache();
                return;
            }

            try
            {
                foreach (var e in entries)
                    if (e.Ctx != null) e.Ctx.PatchHostInsulation(doc, addedOrModified, deleted);

                if (deleted != null)
                    foreach (var id in deleted)
                        foreach (var e in entries)
                            if (e.Ctx != null && e.Ctx.HostTargets != null) e.Ctx.HostTargets.Remove(id);

                if (addedOrModified != null)
                {
                    foreach (var id in addedOrModified)
                    {
                        // Fetch the element once, then apply it to each entry's index.
                        Element el;
                        try { el = doc.GetElement(id); } catch { continue; }
                        bool valid = el != null && el.IsValidObject;
                        BuiltInCategory bic = valid ? PairSuppressor.GetBuiltInCategory(el) : BuiltInCategory.INVALID;

                        foreach (var e in entries)
                        {
                            var index = e.Ctx != null ? e.Ctx.HostTargets : null;
                            if (index == null) continue;
                            if (valid && e.Targets != null && e.Targets.Contains(bic))
                                index.Insert(doc, id);
                            else
                                index.Remove(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let index maintenance break the updater — drop the cache and
                // let it rebuild rather than carry something inconsistent.
                Log.Warn("Index patch failed; dropping cache", ex);
                ClashEngine.InvalidateAll();
            }
        }

        /// <summary>
        /// Drop the cached context. Call on document open/close, and whenever a
        /// cached id turns out to be stale. ClashEngine.InvalidateAll wraps this
        /// together with the solid/geometry caches, so callers use that.
        /// </summary>
        public static void InvalidateCache()
        {
            lock (CacheGate)
            {
                _cache.Clear();
                _cacheDocKey = null;
            }
        }
    }
}
