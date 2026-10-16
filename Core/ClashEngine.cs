using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The actual intersection work.
    ///
    /// Strategy, per changed element:
    ///   1. Broad phase — a bounding-box query, run over a PRE-COLLECTED set of
    ///      target ids rather than over the whole document.
    ///   2. Narrow phase — the exact solid/element intersection filter, run *only*
    ///      over the handful of ids the broad phase returned. Expensive, so it never
    ///      sees the whole model.
    ///   3. Anything that survived (1) but failed (2) is a clearance hit, not a hard hit.
    ///
    /// Host geometry is checked against the host document directly. For links the
    /// host element's solids are pushed into link space with the inverse of the
    /// link transform, so the query can run inside the link document where Revit's
    /// spatial index for that model lives.
    ///
    /// WHY THE TARGET IDS ARE HOISTED (measured in Revit 2026, 2026-08-20, against
    /// a 310,835-element model):
    ///     per-element document-wide collector    184.75 ms/element
    ///     collector scoped to a hoisted id set    15.70 ms/element   (11.8x)
    /// "new FilteredElementCollector(doc)" costs O(document) to construct and run
    /// however few elements match — the bounding-box quick filter is genuinely
    /// fast, but it never gets the chance to be the dominant cost. Building one per
    /// source element made a full sweep of 16,782 pipes take ~3,100 s; hoisting the
    /// target set to one query per scan takes it to ~263 s. Do not move the
    /// collection back inside the per-element loop.
    /// </summary>
    public static class ClashEngine
    {

        /// <summary>
        /// Is this element's TYPE one the audit calls provisional? Cheap: a cached
        /// set of type ids, so this is a hash lookup per reported clash.
        /// </summary>
        private static bool IsProvisional(Document doc, Element e)
        {
            try
            {
                if (doc == null || e == null) return false;
                var t = e.GetTypeId();
                if (t == null || t == ElementId.InvalidElementId) return false;
                return ProvisionalAudit.ProvisionalTypeIds(doc, false).Contains(IdUtil.Value(t));
            }
            catch { return false; }
        }

        /// <summary>
        /// True only while a FULL sweep is running.
        ///
        /// During a sweep every source is processed, so a pair where both sides are
        /// sources gets evaluated twice — once from each end. 31 of this project's 34
        /// categories are both source and target, and 100% of their hits were
        /// reachable from either side, so the sweep was doing roughly double the work.
        ///
        /// It must NOT apply to a live update. There, only the changed element is a
        /// source, so the "other side will handle it" assumption is false and skipping
        /// would silently lose the clash.
        /// </summary>
        private static bool _sweepMode;

        /// <summary>
        /// Link names already warned about in <see cref="CheckAgainstLink"/>'s stale-cache
        /// guard, so a condition that holds for a whole modelling session logs once
        /// rather than once per source element per check pass. Cleared with everything
        /// else in <see cref="InvalidateAll"/> — the same event that would actually have
        /// fixed the underlying staleness — so a genuinely recurring problem warns again
        /// rather than going quiet forever after the first sighting.
        /// </summary>
        private static readonly HashSet<string> _warnedStaleLinks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        /// <summary>
        /// Run <paramref name="body"/> with sweep mode ON, exactly as a full check does.
        /// The ONE place the flag is set, so it can never be cleared out from under a
        /// sweep that is still running.
        ///
        /// DEPTH-COUNTED rather than a bare bool. A plain set/clear pair is not
        /// re-entrant: nest one inside another and the inner finally turns the flag off
        /// while the outer sweep is still going, and ReverseWillCoverIt silently stops
        /// de-duplicating half way through - every remaining pair reported twice. Nothing
        /// nests today; the counter is what stops that being a latent trap for whoever
        /// adds the first nesting caller.
        ///
        /// Internal so the SelfTest harness can exercise <see cref="ReverseWillCoverIt"/>
        /// under its real condition: it returns false outright when sweep mode is off, so
        /// a test that did not set it would pass while asserting nothing.
        /// </summary>
        internal static void InSweepMode(Action body)
        {
            if (body == null) return;
            lock (SweepGate) _sweepDepth++;
            _sweepMode = true;
            try { body(); }
            finally
            {
                lock (SweepGate) { if (--_sweepDepth <= 0) { _sweepDepth = 0; _sweepMode = false; } }
            }
        }

        private static readonly object SweepGate = new object();
        private static int _sweepDepth;

        /// <summary>
        /// Will the pair (el, other) also be evaluated with the roles reversed? If so,
        /// exactly one of the two directions does the work — the one with the lower
        /// element id, which is a stable choice needing no memory of what was seen.
        ///
        /// Both halves have to be checked. It is not enough that `other` is a source:
        /// the reverse direction applies the TARGET rules to `el`, so `el` has to pass
        /// those too, or the pair would be dropped by both directions and vanish.
        /// </summary>
        /// <remarks>
        /// Internal rather than private so SelfTest can call the real method, the same
        /// reason ClashVerify.Intersects is internal. This decides whether a pair is
        /// evaluated once or twice, and everything downstream de-duplicates on the
        /// assumption that it is right.
        /// </remarks>
        internal static bool ReverseWillCoverIt(Document doc, Element el, Element other, ClashSettings s, ClashSet set = null)
        {
            if (!_sweepMode) return false;
            set = set ?? s.Active;

            try
            {
                // Deterministic half: only skip the higher id, so the lower one runs.
                if (IdUtil.Value(el.Id) <= IdUtil.Value(other.Id)) return false;

                return OtherSideCovers(el, other, s, set);
            }
            catch { return false; }   // never skip on doubt
        }

        /// <summary>
        /// Will <paramref name="other"/>, processed as a SOURCE, examine <paramref name="el"/> as one of its
        /// targets? True when `other` gets a turn as a source under this set and `el` survives the set's
        /// target rules. The id ordering is deliberately NOT here: <see cref="ReverseWillCoverIt"/> adds it
        /// (one of two directions does the bare work), while the host insulation pass needs only this - the
        /// insulated element's own source-side pass tests everything its insulation touches, whatever the ids.
        /// </summary>
        private static bool OtherSideCovers(Element el, Element other, ClashSettings s, ClashSet set)
        {
            try
            {
                // `other` must actually get a turn as a source.
                if (!set.SourceCategories.Contains(PairSuppressor.GetBuiltInCategory(other))) return false;
                if (!set.SourceFilter.Matches(other)) return false;
                if (ParameterFilter.TypeExcluded(other, s.AuditDrops)) return false;   // globally dropped: never a source

                // ...and `el` must survive the target rules when it does.
                if (!set.TargetCategories.Contains(PairSuppressor.GetBuiltInCategory(el))) return false;
                if (!set.TargetFilter.Matches(el)) return false;
                if (ParameterFilter.TypeExcluded(el, s.AuditDrops)) return false;      // globally dropped: never a target

                return true;
            }
            catch { return false; }   // never skip on doubt
        }


        public static void InvalidateAll()
        {
            // The target-index cache lives in ClashContext now; this stays the one
            // public "drop everything" entry point callers use.
            ClashContext.InvalidateCache();

            // Solid extents are keyed by element id and go stale with the rest.
            ClashGeometry.ClearCache();
            ClashVerify.ClearSolidCache();
            ProvisionalAudit.InvalidateTypeIndex();

            // Category counts are keyed per scan and no longer evict each other, so they
            // need dropping explicitly. Here, with the rest, so every cache in the engine
            // has one lifetime.
            CategoryCatalog.InvalidateCounts();

            // A stale link that gets fixed (or a fresh one that goes stale later) should
            // be able to warn again — see the field comment.
            _warnedStaleLinks.Clear();
        }

        /// <summary>PathName is empty for an unsaved model, so fall back to the title.</summary>
        internal static string DocKey(Document doc)
        {
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName;
                return "unsaved::" + doc.Title;
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Check a batch of host elements. Stops early if <paramref name="budget"/>
        /// runs out and reports what it did not reach in <paramref name="notChecked"/>.
        /// </summary>
        public static List<ClashResult> CheckElements(
            Document doc,
            ICollection<ElementId> ids,
            ClashSettings settings,
            Stopwatch budget,
            out List<ElementId> notChecked)
        {
            return CheckElements(doc, ids, settings, budget, null, out notChecked, null);
        }

        /// <summary>
        /// As above, but reusing a context built by <see cref="ClashContext.BuildContext"/>.
        /// A long sweep builds it once; the updater builds one per transaction.
        /// </summary>
        public static List<ClashResult> CheckElements(
            Document doc,
            ICollection<ElementId> ids,
            ClashSettings settings,
            Stopwatch budget,
            ScanContext ctx,
            out List<ElementId> notChecked,
            ClashSet set = null)
        {
            // Which set this pass is for. The multi-set loops pass it explicitly; the
            // live single-set path and the UI leave it null and mean the active one.
            set = set ?? settings.Active;

            var results = new List<ClashResult>();
            notChecked = new List<ElementId>();

            if (doc == null || !doc.IsValidObject || ids == null || ids.Count == 0) return results;

            // Solids do not survive the document changing, so they may be cached for
            // exactly one check and no longer. This scope IS that check.
            //
            // It nests: a full sweep opens one around the whole run, so each batch of
            // 100 opening its own is a no-op and a target spanning a batch boundary is
            // still extracted once. Outside a sweep the scope is this call, which is a
            // fresh session after a committed transaction — the case that must not
            // inherit anything.
            //
            // This replaced `if (!_sweepMode) ClearSolidCache()`. Same effect, except
            // that a caller which opens no scope now gets uncached geometry instead of
            // whatever the last check happened to leave lying about.
            using (ClashVerify.SolidScope())
            {

            // Cached — see GetContext. Rebuilding this per call is what stopped the
            // inline pass ever checking anything.
            if (ctx == null) ctx = ClashContext.GetContext(doc, settings);

            bool budgetBlown = false;
            int readSkips = 0;

            foreach (var id in ids)
            {
                if (budgetBlown)
                {
                    notChecked.Add(id);
                    continue;
                }

                if (budget != null && settings.TimeBudgetMs > 0 && budget.ElapsedMilliseconds > settings.TimeBudgetMs)
                {
                    budgetBlown = true;
                    notChecked.Add(id);
                    continue;
                }

                Element el;
                try { el = doc.GetElement(id); } catch { readSkips++; continue; }
                if (el == null || !el.IsValidObject) continue;
                if (PairSuppressor.IsIgnorableElement(el)) continue;

                // Source CATEGORY. RunFullCheck filters by category up front with
                // BuildSourceFilter; this path took whatever ids it was handed and only
                // ever applied the parameter rules, so anything a caller passed became a
                // clash source regardless of what it was. Centre-line elements reached
                // the register that way.
                if (!set.SourceCategories.Contains(PairSuppressor.GetBuiltInCategory(el))) continue;

                // Source parameter rules. Evaluated only on the elements that just
                // changed, so the cost is bounded by the size of your edit.
                if (!set.SourceFilter.Matches(el)) continue;

                // Global audit drops: model-wide non-clash geometry (access zones,
                // modelled openings) excluded from EVERY set as a source. Consulted
                // directly rather than copied into each set's filter, so a drop ticked
                // once in the Audit window holds across the whole matrix.
                if (ParameterFilter.TypeExcluded(el, settings.AuditDrops)) continue;

                BoundingBoxXYZ hostBox;
                try { hostBox = el.get_BoundingBox(null); } catch { readSkips++; continue; }
                if (hostBox == null) continue;

                // Where THIS element's results begin, so the insulation pass can fold into
                // exactly them and not scan everything gathered so far in the batch.
                int elementStart = results.Count;

                try
                {
                    if (settings.CheckHostVsHost)
                        results.AddRange(CheckAgainstHost(doc, el, hostBox, ctx.HostTargets, settings, set));

                    // Solid extraction is the expensive part of a link check, and it
                    // produces the same solids for every link. Extracted at most once
                    // per element, and still only when a link actually has candidates.
                    var solidCache = new List<Solid>[1];
                    foreach (var pair in ctx.LinkTargets)
                        results.AddRange(CheckAgainstLink(doc, el, hostBox, pair.Key, pair.Value, settings, set, solidCache));

                    if (set.InsulationAware)
                    {
                        // Two directions: what a link holds, reached by ITS insulation ...
                        var linkHits = new List<ClashResult>();
                        foreach (var pair in ctx.LinkTargets)
                            linkHits.AddRange(CheckAgainstLinkInsulation(
                                doc, el, hostBox, pair.Key, ctx, settings, set, solidCache));
                        MergeInsulationHits(results, elementStart, linkHits);

                        // ... and what the HOST holds, reached by ITS insulation: an insulated pipe or duct that
                        // is only a target, which the two passes above and below cannot see.
                        if (settings.CheckHostVsHost)
                            MergeInsulationHits(results, elementStart,
                                CheckAgainstHostInsulation(doc, el, hostBox, ctx, settings, set));

                        // ... and this element's own insulation, against everything.
                        CheckInsulation(doc, el, hostBox, ctx, settings, set, results, elementStart);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Clash check failed for element " + IdUtil.Value(id), ex);
                }
            }

            if (readSkips > 0)
                Log.Warn(readSkips + " source element(s) skipped: element or bounding box unreadable. "
                         + "A large count means real clashes may be going unchecked.");

            return results;
            }   // solid scope — see the note where it opens
        }

        /// <summary>
        /// Add <paramref name="r"/> to <paramref name="all"/> unless its pair is already
        /// there — and when it is, keep the HARDER verdict, not merely the first one.
        ///
        /// This used to be first-seen-wins, and that silently demoted real clashes.
        /// MEASURED on testproject (2026-09-19): of 99 pairs whose insulation physically
        /// intersects something, 97 were reported as mere Clearance once a 50 mm clearance
        /// was configured — and not because insulation checking was off: the bare pipe,
        /// being within 50 mm of the same object, reported a soft row for the same key
        /// FIRST, and first-seen-wins discarded the Hard row the insulation pass had
        /// correctly found. Insulation is ~50 mm thick, so any clearance at or above that
        /// makes the two coincide for essentially every insulation clash: the feature
        /// worked only at clearance 0. A Hard verdict is confirmed volume overlap; a
        /// Clearance one is only "inside the tolerance zone", so it must not be able to
        /// bury the former. The same holds across SETS — one set's soft row for a pair must
        /// not hide another set's hard one — which is why both cross-set merges call this.
        ///
        /// Everything else keeps first-seen, exactly as before, so the lowest-indexed set
        /// still stamps the row whenever severities agree.
        /// </summary>
        internal static void AddDeduped(
            List<ClashResult> all, Dictionary<string, int> index, ClashResult r, string setName)
        {
            if (string.IsNullOrEmpty(r.Key)) { r.SetName = setName; all.Add(r); return; }

            int at;
            if (!index.TryGetValue(r.Key, out at))
            {
                r.SetName = setName;
                index[r.Key] = all.Count;
                all.Add(r);
                return;
            }

            if (r.Severity == ClashSeverity.Hard && all[at].Severity != ClashSeverity.Hard)
            {
                r.SetName = setName;
                all[at] = r;
            }
        }

        /// <summary>
        /// Fold insulation hits into the results already gathered for THIS element
        /// (from index <paramref name="start"/>): a new pair is added; a pair the bare
        /// geometry already reported is replaced only when the insulation's verdict is
        /// Hard and the bare one is not. Same rule as <see cref="AddDeduped"/>, applied
        /// here because both rows would otherwise sit in one list sharing a key until a
        /// caller happened to de-duplicate — and the callers that do, do so first-seen.
        /// </summary>
        internal static void MergeInsulationHits(List<ClashResult> results, int start, List<ClashResult> hits)
        {
            foreach (var h in hits)
            {
                int at = -1;
                for (int i = start; i < results.Count; i++)
                    if (results[i].Key == h.Key) { at = i; break; }

                if (at < 0) results.Add(h);
                else if (h.Severity == ClashSeverity.Hard && results[at].Severity != ClashSeverity.Hard)
                    results[at] = h;
            }
        }

        /// <summary>
        /// Insulation-aware checking (Phase 3): tests <paramref name="el"/>'s own
        /// pipe/duct insulation against the same host and link targets its bare geometry
        /// was just checked against, filing any hit on <paramref name="el"/> — see the
        /// reportAs note on CheckAgainstHost/CheckAgainstLink for why that collapses
        /// cleanly onto a bare-pipe hit for the same other element rather than
        /// duplicating it.
        ///
        /// Its own insulation only: the far side of a pair has its own pass, added later - <see cref="CheckAgainstHostInsulation"/> for an insulated element in the host that is only a target, and <see cref="CheckAgainstLinkInsulation"/> for one in a link. Duct LINING is never touched — GetInsulationIds does not return it, and
        /// it sits inside the duct rather than enlarging the outer size, so there would
        /// be nothing for it to add even if it did.
        /// </summary>
        private static void CheckInsulation(
            Document doc, Element el, BoundingBoxXYZ hostBox, ScanContext ctx,
            ClashSettings settings, ClashSet set, List<ClashResult> results, int elementStart)
        {
            // The lookup the verifier uses, so the two agree. An element that cannot carry insulation
            // (a wall, a cable tray, a fixture) is a definite "none": Revit throws for it, which costs
            // 22-35 us measured against ~1.5 us without - about a second over a 30,000-element sweep, and
            // not worth a per-category cache that one odd element could turn into a silently missing
            // insulation check. Anything ELSE Revit throws is "could not say" (null): warned once per
            // category, and this element is checked bare only.
            var insulationIds = PairVerifier.InsulationIdsOf(doc, el, "");
            if (insulationIds == null || insulationIds.Count == 0) return;

            // Gathered first, then merged — see MergeInsulationHits for why appending
            // straight into `results` demoted real clashes.
            var hits = new List<ClashResult>();

            foreach (var insId in insulationIds)
            {
                Element ins;
                try { ins = doc.GetElement(insId); } catch { continue; }
                if (ins == null || !ins.IsValidObject) continue;

                BoundingBoxXYZ insBox;
                try { insBox = ins.get_BoundingBox(null); } catch { continue; }
                if (insBox == null) continue;

                if (settings.CheckHostVsHost)
                    hits.AddRange(CheckAgainstHost(doc, ins, insBox, ctx.HostTargets, settings, set, el));

                var insSolidCache = new List<Solid>[1];
                foreach (var pair in ctx.LinkTargets)
                    hits.AddRange(CheckAgainstLink(
                        doc, ins, insBox, pair.Key, pair.Value, settings, set, insSolidCache, el));
            }

            MergeInsulationHits(results, elementStart, hits);
        }

        /// <summary>
        /// The live counterpart to <see cref="RunFullCheckAllSets"/>: check a batch of
        /// changed elements against EVERY enabled set, not just the active one, so a live
        /// edit is caught by exactly the sets a full check would catch it in.
        ///
        /// Each set uses its own warm context (ClashContext caches one per target scope,
        /// so the loop does not rebuild the index per set). The budget is SHARED across
        /// the sets — the whole edit gets one time slice — and anything a blown budget
        /// leaves unreached comes back in <paramref name="notChecked"/> for idle to
        /// finish, re-run the same way. De-duplication is free: the store is keyed by
        /// ClashResult.Key, so a pair two sets both report lists once.
        /// </summary>
        public static List<ClashResult> CheckElementsAllSets(
            Document doc, ICollection<ElementId> ids, ClashSettings settings,
            Stopwatch budget, out List<ElementId> notChecked)
        {
            var all = new List<ClashResult>();
            var nc = new List<ElementId>();
            notChecked = nc;
            if (doc == null || !doc.IsValidObject || ids == null || ids.Count == 0) return all;

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);   // key -> index in `all`
            var ncSeen = new HashSet<long>();

            // The set is PASSED, never assigned to settings.ActiveSet. Mutating shared
            // state round a loop is how F3 happened: the scan context read Active for
            // its cache key while this loop moved it underneath.
            foreach (var set in settings.Sets)
            {
                if (!set.Enabled) continue;

                // Per-set warm context, keyed by that set's own scope.
                var ctx = ClashContext.GetContext(doc, settings, set);
                List<ElementId> setNotChecked;
                foreach (var r in CheckElements(doc, ids, settings, budget, ctx, out setNotChecked, set))
                    AddDeduped(all, seen, r, set.Name);

                // Union what each set could not reach; idle re-runs them against all sets.
                foreach (var id in setNotChecked)
                    if (ncSeen.Add(IdUtil.Value(id))) nc.Add(id);
            }

            return all;
        }

        // ---------------------------------------------------------------- host vs host

        private static List<ClashResult> CheckAgainstHost(
            Document doc, Element el, BoundingBoxXYZ hostBox,
            TargetIndex index, ClashSettings s, ClashSet set, Element reportAs = null)
        {
            // reportAs is set ONLY for an insulation-on-behalf-of-its-pipe pass (see
            // CheckElements): `el` and `hostBox` are the INSULATION's own geometry —
            // everything that decides WHETHER a clash exists reads them — but the
            // RESULT is filed against the pipe, so a hit collapses onto the same key a
            // bare-pipe hit for the same `other` would use, and never duplicates one.
            var host = reportAs ?? el;
            bool insulated = reportAs != null;

            var results = new List<ClashResult>();

            if (index == null || index.Count == 0) return results;

            var outline = GeometryUtil.TransformToOutline(hostBox, null, s.ClearanceFor(set));

            // Broad phase, in memory — ~0.14 ms. See TargetIndex. Excludes the
            // REPORTING host's own id, not `el`'s — during an insulation pass `el` is
            // the insulation element, which was never a candidate anyway (insulation
            // categories are never in TargetCategories), while `host` (the pipe) can be,
            // when its own category is both source and target. Currently also caught by
            // PairSuppressor.ShouldSuppress's a.Id==b.Id check further down, but this is
            // the correct place to state the exclusion rather than depend on that.
            var candidates = index.Query(outline, host.Id);
            if (candidates.Count == 0) return results;

            // Elements fetched by the de-duplication pass, kept ALIGNED WITH
            // `candidates` so the results loop can reuse them by index instead of
            // asking Revit again. Null outside a sweep, where there is no such pass and
            // nothing has been fetched yet.
            List<Element> known = null;

            // Drop the candidates the other direction will handle, BEFORE the filter runs.
            //
            // What costs is RUNNING the filter, and roughly per candidate. Measured on testproject
            // (2026-09-20) with complex sources: about 0.4 ms for 5 candidates, 0.9 ms for 20 and
            // 2.6 ms for 60, with next to no fixed cost (7 us for one). So every candidate removed
            // here is a candidate never tested, and if the list empties the run is skipped outright.
            //
            // Building the filter costs nothing (the constructor is lazy, ~0 us), and a fresh filter
            // per run costs the same as a reused one (302 us against 295 us over 79 complex sources),
            // so there is nothing to gain from building it late, sharing it between passes or
            // caching it.
            //
            // (An earlier version of this comment said constructing the filter "extracts the source's
            // solid and is ~94% of the cost", so that thinning the list saved almost nothing. Both
            // halves were wrong when measured again.)
            if (_sweepMode)
            {
                long elValue = IdUtil.Value(el.Id);
                var keep = new List<ElementId>(candidates.Count);
                known = new List<Element>(candidates.Count);

                foreach (var cid in candidates)
                {
                    // THE ID COMPARISON FIRST, before any fetch. ReverseWillCoverIt
                    // opens with "only skip the higher id" — a test needing nothing but
                    // the two id values, and both are already in hand. Fetching the
                    // element to discover that was a Revit round trip taken and thrown
                    // away for roughly half of every candidate list in the sweep.
                    //
                    // Same answer, provably: when el.Id <= cid that method returns false,
                    // and !false keeps the candidate — which is what this does directly.
                    if (elValue <= IdUtil.Value(cid)) { keep.Add(cid); known.Add(null); continue; }

                    Element cand;
                    try { cand = doc.GetElement(cid); } catch { cand = null; }
                    if (cand == null || !ReverseWillCoverIt(doc, el, cand, s, set))
                    {
                        // Kept — and REMEMBERED. This element was about to be fetched a
                        // second time a few lines below, for every candidate that
                        // survives to be reported.
                        keep.Add(cid);
                        known.Add(cand);
                    }
                }

                candidates = keep;
                if (candidates.Count == 0) return results;
            }

            // Narrow phase over the candidate set only.
            var hardIds = new HashSet<long>();
            var unverified = new HashSet<long>();

            // Build the filter ONCE, and separately from running it.
            //
            // Revit throws "the category of the element is not supported for element
            // intersection filters" from the CONSTRUCTOR, and the constructor takes
            // the SOURCE element — so when it fails, it fails for the element itself
            // and no candidate can ever be tested against it. Retrying per candidate
            // would rebuild the identical doomed filter once per candidate: the same
            // answer, N times more slowly. Observed live on 2026-08-21.
            ElementIntersectsElementFilter narrow = null;
            try
            {
                narrow = new ElementIntersectsElementFilter(el);
            }
            catch (Exception ex)
            {
                foreach (var cid in candidates) unverified.Add(IdUtil.Value(cid));
                Log.Warn("Intersection filter unsupported for "
                         + PairSuppressor.CategoryName(el) + " " + IdUtil.Value(el.Id)
                         + "; " + candidates.Count + " candidate(s) marked unverified", ex);
            }

            if (narrow != null)
            {
                try
                {
                    var hard = new FilteredElementCollector(doc, candidates)
                        .WherePasses(narrow)
                        .ToElementIds();

                    foreach (var hid in hard) hardIds.Add(IdUtil.Value(hid));
                }
                catch (Exception ex)
                {
                    // The filter itself is fine, so the batch failed because of one
                    // bad CANDIDATE. Retrying individually salvages the rest — this is
                    // the case where per-candidate retry actually buys something.
                    //
                    // Before any of this, a failure here just logged and left hardIds
                    // empty, which with the default zero clearance dropped every
                    // candidate and reported the element as clean.
                    Log.Warn("Batch intersection failed for element " + IdUtil.Value(el.Id)
                             + "; retrying " + candidates.Count + " candidate(s) individually", ex);

                    foreach (var cid in candidates)
                    {
                        try
                        {
                            var one = new FilteredElementCollector(doc, new List<ElementId> { cid })
                                .WherePasses(narrow)
                                .ToElementIds();
                            if (one.Count > 0) hardIds.Add(IdUtil.Value(cid));
                        }
                        catch
                        {
                            // Cannot be answered either way — surface it.
                            unverified.Add(IdUtil.Value(cid));
                        }
                    }
                }
            }

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                var otherId = candidates[ci];

                // Decide FIRST whether this candidate can produce a result at all.
                // With no clearance set — the default — a candidate that failed the
                // narrow phase is not a clash, and everything below is wasted on it:
                // fetching the element, reading its parameters, and asking Revit
                // whether the two are joined or MEP-connected, which is the single
                // most expensive test in the loop. Same results, far less work.
                bool isHard = hardIds.Contains(IdUtil.Value(otherId));
                bool isUnverified = !isHard && unverified.Contains(IdUtil.Value(otherId));
                if (!isHard && !isUnverified && s.ClearanceFor(set) <= 0) continue;

                // Reused when the de-duplication pass already fetched it. Kept aligned
                // with `candidates`, so the loop index IS the lookup — no dictionary,
                // no hashing, and nothing to keep in step by hand.
                Element other = known != null ? known[ci] : null;
                if (other == null) other = doc.GetElement(otherId);
                if (other == null || !other.IsValidObject) continue;
                if (PairSuppressor.IsIgnorableElement(other)) continue;

                // Target parameter rules. Only candidates that already survived the
                // broad phase reach here — a handful — so this never touches the
                // whole target set and cannot undo the spatial index.
                if (!set.TargetFilter.Matches(other)) continue;
                if (ParameterFilter.TypeExcluded(other, s.AuditDrops)) continue;   // global audit drop (target side)

                // Suppression runs against the PIPE, not its insulation — a pipe joined
                // to a fitting or connected to the next run is exactly as legitimately
                // touching by way of its insulation as it is bare. Testing it against
                // `el` (the insulation) here would ask Revit a question about an
                // element with no joins or connectors of its own and get no suppression
                // at all, spamming a hit for every insulated run meeting its own fitting.
                if (PairSuppressor.ShouldSuppress(doc, host, other)) continue;

                // Confirm a hard hit really shares volume. Placed last on purpose: by
                // here the pair has survived every cheaper test, so the boolean runs
                // only for something about to be reported.
                if (isHard)
                {
                    var real = ClashVerify.OverlapIsReal(el, other);
                    if (real.HasValue && !real.Value)
                    {
                        // Confirmed zero overlap: the two only touch, at distance 0 — always
                        // "within tolerance" once a clearance is configured (0 <= any positive
                        // clearance). Report it as Clearance instead of dropping it outright;
                        // with no clearance configured there is nothing to report.
                        if (s.ClearanceFor(set) <= 0) continue;
                        isHard = false;
                    }
                    else if (!real.HasValue)
                    {
                        // No solids to measure. Say so rather than asserting a clash.
                        isHard = false;
                        isUnverified = true;
                    }
                }

                BoundingBoxXYZ otherBox = null;
                try { otherBox = other.get_BoundingBox(null); } catch { }

                results.Add(new ClashResult
                {
                    HostElementId = host.Id,
                    HostElementName = PairSuppressor.ElementLabel(host),
                    HostCategory = PairSuppressor.CategoryName(host),
                    OtherElementId = otherId,
                    OtherElementName = PairSuppressor.ElementLabel(other),
                    OtherCategory = PairSuppressor.CategoryName(other),
                    LinkInstanceId = null,
                    ModelName = "Current model",
                    Severity = isHard ? ClashSeverity.Hard
                           : isUnverified ? ClashSeverity.Unverified : ClashSeverity.Clearance,
                    Location = otherBox != null
                        ? GeometryUtil.OverlapCentre(hostBox, null, otherBox, null)
                        : Centre(hostBox),
                    GeometryKind = ClashGeometry.ClassifyPair(host, other, null),
                    Provisional = IsProvisional(doc, host) || IsProvisional(doc, other),
                    Insulated = insulated,
                    Key = ClashResult.MakeKey(host.Id, null, otherId)
                });
            }

            return results;
        }

        // ---------------------------------------------------------------- host vs link

        private static List<ClashResult> CheckAgainstLink(
            Document doc, Element el, BoundingBoxXYZ hostBox, LinkContext link,
            TargetIndex index, ClashSettings s, ClashSet set, List<Solid>[] solidCache,
            Element reportAs = null)
        {
            // See the note on CheckAgainstHost's reportAs — same meaning here: `el` is
            // whatever geometry is being tested (the pipe itself, or its insulation),
            // `host` is who the result is filed against.
            var host = reportAs ?? el;
            bool insulated = reportAs != null;

            var results = new List<ClashResult>();
            if (link.Document == null || !link.Document.IsValidObject)
            {
                // Should not be reachable now — BuildContext already skips a link in this
                // state when it collects ctx.LinkTargets (see the `continue` there), and
                // ClashContext.GetContext now checks every cached link's Document before
                // ever handing a cache hit out, which is where the actual fix lives. This
                // guard is what a still-stale cache looked like before that existed: a
                // reload or unload closes the link's background document, the cache kept
                // handing out the dead reference for up to its 10-minute TTL, and every
                // check against it landed here — silently, no log, empty results, read
                // upstream as "nothing to report" rather than "could not check". An
                // element re-checked in that window lost every one of its link clashes
                // from the panel.
                //
                // Kept as a warning rather than removed: GetContext's validation is a
                // second, independent path to the same guarantee, and if it ever has a
                // gap, this is what would catch a check silently under-counting again
                // instead of it going unnoticed a second time.
                //
                // Deduplicated per link name rather than per element: this runs once per
                // source element checked against the link, and while the condition holds
                // every one of them would hit it.
                if (_warnedStaleLinks.Add(link.Name ?? "(unnamed link)"))
                    Log.Warn("Clash check: link '" + (link.Name ?? "(unnamed link)")
                             + "' has no valid Document in a cached scan context — its "
                             + "clashes are being skipped, not resolved. This should not "
                             + "happen; ClashContext.GetContext validates every cached "
                             + "link before handing a context out, so seeing this means "
                             + "that check has a gap.");
                return results;
            }
            if (index == null || index.Count == 0) return results;

            // Host box → link space, inflated by the clearance. The link's index is
            // built in link coordinates, so the query has to meet it there.
            var linkOutline = GeometryUtil.TransformToOutline(hostBox, link.ToLink, s.ClearanceFor(set));

            var candidates = index.Query(linkOutline, null);
            if (candidates.Count == 0) return results;

            // Narrow phase: push the host element's solids into link coordinates.
            var hardIds = new HashSet<long>();
            var unverified = new HashSet<long>();

            var inLink = LinkSpaceSolids(el, link, solidCache);
            IntersectInLink(link, candidates, inLink, hardIds, unverified);

            foreach (var otherId in candidates)
            {
                // Same early bail as the host path — see the comment there.
                bool isHard = hardIds.Contains(IdUtil.Value(otherId));
                bool isUnverified = !isHard && unverified.Contains(IdUtil.Value(otherId));
                if (!isHard && !isUnverified && s.ClearanceFor(set) <= 0) continue;

                Element other = link.Document.GetElement(otherId);
                if (other == null || !other.IsValidObject) continue;
                if (PairSuppressor.IsIgnorableElement(other)) continue;
                // No suppression pass here: nothing in a link can be joined or
                // connected to host geometry, so every hit is a genuine coordination issue.

                // Target parameter rules apply inside links too — the linked model has
                // its own System Types and type comments, and filtering on them is
                // often the whole point of checking against someone else's model.
                if (!set.TargetFilter.Matches(other)) continue;
                // Global audit drops apply inside links too: a family+type the model
                // treats as non-clash geometry is not a real obstruction in a link either.
                if (ParameterFilter.TypeExcluded(other, s.AuditDrops)) continue;

                // Confirm a hard hit really shares volume, exactly as the host path does.
                // ElementIntersectsSolidFilter matches non-buildable geometry too, so
                // without this the link path reports every phantom class the host path
                // already measured and rejected.
                if (isHard)
                {
                    var real = ClashVerify.LinkOverlapIsReal(inLink, other);
                    if (real.HasValue && !real.Value)
                    {
                        // Confirmed zero overlap: distance 0, always within a configured
                        // clearance. Report as Clearance rather than dropping outright.
                        if (s.ClearanceFor(set) <= 0) continue;
                        isHard = false;
                    }
                    else if (!real.HasValue) { isHard = false; isUnverified = true; }
                }

                BoundingBoxXYZ otherBox = null;
                try { otherBox = other.get_BoundingBox(null); } catch { }

                results.Add(new ClashResult
                {
                    HostElementId = host.Id,
                    HostElementName = PairSuppressor.ElementLabel(host),
                    HostCategory = PairSuppressor.CategoryName(host),
                    OtherElementId = otherId,
                    OtherElementName = PairSuppressor.ElementLabel(other),
                    OtherCategory = PairSuppressor.CategoryName(other),
                    LinkInstanceId = link.InstanceId,
                    ModelName = link.Name,
                    Severity = isHard ? ClashSeverity.Hard
                           : isUnverified ? ClashSeverity.Unverified : ClashSeverity.Clearance,
                    Location = otherBox != null
                        ? GeometryUtil.OverlapCentre(hostBox, null, otherBox, link.ToHost)
                        : Centre(hostBox),
                    GeometryKind = ClashGeometry.ClassifyPair(host, other, link.ToHost),
                    Provisional = IsProvisional(doc, host) || IsProvisional(link.Document, other),
                    Insulated = insulated,
                    Key = ClashResult.MakeKey(host.Id, link.InstanceId, otherId)
                });
            }

            return results;
        }

        /// <summary>
        /// A host element's solids, pushed into one link's coordinates and kept - the volume check
        /// afterwards needs the same link-space solids the filter was run with. Extracted at most
        /// once per element through <paramref name="solidCache"/>, because it is the same set of
        /// solids for every link.
        /// </summary>
        private static List<Solid> LinkSpaceSolids(Element el, LinkContext link, List<Solid>[] solidCache)
        {
            // SolidsOf, not GetSolids: Fine detail with a Medium fallback. This used
            // to call GetSolids(el) with the default detail, which is MEDIUM, and 452
            // of 1,050 sampled source elements on PRJ01 -- 43%, including every
            // Lighting Device sampled, 148 Pipe Fittings and 135 Electrical Fixtures --
            // have no solids at Medium at all. Every one of those marked all its link
            // candidates unverified, so nearly half the source set was never actually
            // checked against any link.
            var solids = solidCache != null
                ? (solidCache[0] ?? (solidCache[0] = ClashVerify.SolidsOf(el) ?? new List<Solid>()))
                : (ClashVerify.SolidsOf(el) ?? new List<Solid>());

            var inLink = new List<Solid>();
            foreach (var solid in solids)
            {
                Solid t;
                try { t = SolidUtils.CreateTransformed(solid, link.ToLink); }
                catch { continue; }
                if (t == null || t.Volume <= ClashVerify.MinOverlapVolume) continue;
                inLink.Add(t);
            }
            return inLink;
        }

        /// <summary>
        /// The link narrow phase: which of <paramref name="candidates"/> (ids in the link) the
        /// host element's link-space solids really intersect (<paramref name="hardIds"/>), and
        /// which could not be answered either way (<paramref name="unverified"/>).
        /// </summary>
        private static void IntersectInLink(LinkContext link, List<ElementId> candidates, List<Solid> inLink,
            HashSet<long> hardIds, HashSet<long> unverified)
        {
            // Nothing to test with. Every candidate is a bounding-box overlap we
            // cannot confirm or deny, so say so rather than reporting the element
            // clean — the same silent-drop trap as a failed intersection filter.
            if (inLink.Count == 0)
                foreach (var cid in candidates) unverified.Add(IdUtil.Value(cid));

            foreach (var inLinkSpace in inLink)
            {
                try
                {
                    var hits = new FilteredElementCollector(link.Document, candidates)
                        .WherePasses(new ElementIntersectsSolidFilter(inLinkSpace))
                        .ToElementIds();

                    foreach (var hid in hits) hardIds.Add(IdUtil.Value(hid));
                }
                catch (Exception ex)
                {
                    // As in the host path: the batch fails because of one bad
                    // candidate, so retry individually rather than losing them all.
                    Log.Warn("Batch solid intersection failed against link " + link.Name
                             + "; retrying " + candidates.Count + " candidate(s) individually", ex);

                    foreach (var cid in candidates)
                    {
                        if (hardIds.Contains(IdUtil.Value(cid))) continue;
                        try
                        {
                            var one = new FilteredElementCollector(link.Document, new List<ElementId> { cid })
                                .WherePasses(new ElementIntersectsSolidFilter(inLinkSpace))
                                .ToElementIds();
                            if (one.Count > 0) hardIds.Add(IdUtil.Value(cid));
                        }
                        catch { unverified.Add(IdUtil.Value(cid)); }
                    }
                }
            }

            // A later solid may have answered what an earlier one could not.
            unverified.ExceptWith(hardIds);
        }

        /// <summary>
        /// Insulation-aware checking, the OTHER side: the host element against the insulation of
        /// what a LINK holds.
        ///
        /// <see cref="CheckInsulation"/> tests the checked element's own insulation, so a bare host
        /// pipe never reached a linked pipe's insulation - the case of a host model that holds the
        /// structure or architecture and links the MEP. The linked pipe was only ever a bare
        /// surface, so a wall 30 mm from it read clear when its 40 mm insulation was in the wall.
        ///
        /// How: the link's insulation elements are indexed on their own (a pipe's box does not
        /// include its insulation, and insulation categories are never target categories), the
        /// host element is tested against them, and a hit is FILED AGAINST THE INSULATED PIPE -
        /// the same idea as the reportAs of the host-side pass. It therefore collapses onto the
        /// key a bare hit for that pair would use, and never lists one pipe twice.
        ///
        /// The pipe, not its insulation, is what the set's rules speak about, so the target
        /// category (for THIS link), the parameter rules, the audit drops and the "ignorable" test
        /// are all asked of the pipe. No suppression pass, as in <see cref="CheckAgainstLink"/>:
        /// nothing in a link is joined or connected to host geometry.
        ///
        /// The host has its own version, <see cref="CheckAgainstHostInsulation"/>. Duct LINING is
        /// never touched, for the same reason as in <see cref="CheckInsulation"/>.
        /// </summary>
        private static List<ClashResult> CheckAgainstLinkInsulation(
            Document doc, Element host, BoundingBoxXYZ hostBox, LinkContext link, ScanContext ctx,
            ClashSettings s, ClashSet set, List<Solid>[] solidCache)
        {
            var results = new List<ClashResult>();
            // The bare pass over this link has already warned about a dead Document.
            if (link.Document == null || !link.Document.IsValidObject) return results;

            var index = ctx.LinkInsulationIndex(link);
            if (index == null || index.Count == 0) return results;

            var linkOutline = GeometryUtil.TransformToOutline(hostBox, link.ToLink, s.ClearanceFor(set));
            var candidates = index.Query(linkOutline, null);
            if (candidates.Count == 0) return results;

            var hardIds = new HashSet<long>();
            var unverified = new HashSet<long>();
            var inLink = LinkSpaceSolids(host, link, solidCache);
            IntersectInLink(link, candidates, inLink, hardIds, unverified);

            ElementFilter linkFilter;
            ctx.LinkTargetFilters.TryGetValue(link, out linkFilter);

            foreach (var insId in candidates)
            {
                bool isHard = hardIds.Contains(IdUtil.Value(insId));
                bool isUnverified = !isHard && unverified.Contains(IdUtil.Value(insId));
                if (!isHard && !isUnverified && s.ClearanceFor(set) <= 0) continue;

                var ins = link.Document.GetElement(insId) as InsulationLiningBase;
                if (ins == null || !ins.IsValidObject) continue;

                var otherId = ins.HostElementId;
                Element other = link.Document.GetElement(otherId);
                if (other == null || !other.IsValidObject) continue;
                if (PairSuppressor.IsIgnorableElement(other)) continue;

                // Is this pipe a target for this link at all? The insulation is never in the
                // target categories, so the category rule has to be asked of the pipe.
                if (linkFilter != null && !linkFilter.PassesFilter(link.Document, otherId)) continue;
                if (!set.TargetFilter.Matches(other)) continue;
                if (ParameterFilter.TypeExcluded(other, s.AuditDrops)) continue;

                // Confirm a hard hit really shares volume, as everywhere else.
                if (isHard)
                {
                    var real = ClashVerify.LinkOverlapIsReal(inLink, ins);
                    if (real.HasValue && !real.Value)
                    {
                        // Confirmed zero overlap: distance 0, always within a configured
                        // clearance. Report as Clearance rather than dropping outright.
                        if (s.ClearanceFor(set) <= 0) continue;
                        isHard = false;
                    }
                    else if (!real.HasValue) { isHard = false; isUnverified = true; }
                }

                BoundingBoxXYZ insBox = null;
                try { insBox = ins.get_BoundingBox(null); } catch { }

                results.Add(new ClashResult
                {
                    HostElementId = host.Id,
                    HostElementName = PairSuppressor.ElementLabel(host),
                    HostCategory = PairSuppressor.CategoryName(host),
                    OtherElementId = otherId,
                    OtherElementName = PairSuppressor.ElementLabel(other),
                    OtherCategory = PairSuppressor.CategoryName(other),
                    LinkInstanceId = link.InstanceId,
                    ModelName = link.Name,
                    Severity = isHard ? ClashSeverity.Hard
                           : isUnverified ? ClashSeverity.Unverified : ClashSeverity.Clearance,
                    Location = insBox != null
                        ? GeometryUtil.OverlapCentre(hostBox, null, insBox, link.ToHost)
                        : Centre(hostBox),
                    GeometryKind = ClashGeometry.ClassifyPair(host, other, link.ToHost),
                    Provisional = IsProvisional(doc, host) || IsProvisional(link.Document, other),
                    Insulated = true,
                    Key = ClashResult.MakeKey(host.Id, link.InstanceId, otherId)
                });
            }

            return results;
        }

        /// <summary>
        /// Insulation-aware checking, the far side in the HOST: a source element against the insulation of an
        /// insulated pipe or duct that is only a TARGET. <see cref="CheckInsulation"/> tests a source's own
        /// insulation, so a source that is not itself insulated (a wall, a duct, a cable tray) never reached the
        /// insulation of a pipe it ran beside - only the bare pipe, so it read clear 30 mm off a pipe with 40 mm
        /// of insulation. The same gap the linked version closed, for elements in the same model.
        ///
        /// A hit is FILED AGAINST THE INSULATED ELEMENT and collapses onto the key a bare hit for that pair would
        /// use. That element's own target rules (its category, parameter rules and audit drops), its
        /// joined/connected suppression and the "ignorable" test are all asked of IT, not of its insulation.
        ///
        /// Its own insulation is skipped (that is CheckInsulation's), and so, in a SWEEP, is a pair whose
        /// insulated element is itself a source that will examine this one: its own pass covers everything its
        /// insulation touches, and the two directions would only report it twice. A live update has no such
        /// cover - only the changed element is a source - so it always runs.
        ///
        /// Duct LINING is never touched, for the same reason as in CheckInsulation.
        /// </summary>
        private static List<ClashResult> CheckAgainstHostInsulation(
            Document doc, Element el, BoundingBoxXYZ hostBox, ScanContext ctx, ClashSettings s, ClashSet set)
        {
            var results = new List<ClashResult>();

            var index = ctx.HostInsulationIndex(doc);
            if (index == null || index.Count == 0) return results;

            var outline = GeometryUtil.TransformToOutline(hostBox, null, s.ClearanceFor(set));
            var candidates = index.Query(outline, null);
            if (candidates.Count == 0) return results;

            var hardIds = new HashSet<long>();
            var unverified = new HashSet<long>();

            // Built once, and separately from running it - the same reasoning as the bare pass: a filter that
            // cannot be built for this source cannot be built for any candidate either.
            ElementIntersectsElementFilter narrow = null;
            try { narrow = new ElementIntersectsElementFilter(el); }
            catch (Exception ex)
            {
                foreach (var cid in candidates) unverified.Add(IdUtil.Value(cid));
                Log.Warn("Intersection filter unsupported for " + PairSuppressor.CategoryName(el) + " " + IdUtil.Value(el.Id)
                         + "; " + candidates.Count + " insulation candidate(s) marked unverified", ex);
            }

            if (narrow != null)
            {
                try
                {
                    foreach (var hid in new FilteredElementCollector(doc, candidates).WherePasses(narrow).ToElementIds())
                        hardIds.Add(IdUtil.Value(hid));
                }
                catch (Exception ex)
                {
                    Log.Warn("Batch intersection failed against host insulation for element " + IdUtil.Value(el.Id)
                             + "; retrying " + candidates.Count + " candidate(s) individually", ex);
                    foreach (var cid in candidates)
                    {
                        try
                        {
                            var one = new FilteredElementCollector(doc, new List<ElementId> { cid }).WherePasses(narrow).ToElementIds();
                            if (one.Count > 0) hardIds.Add(IdUtil.Value(cid));
                        }
                        catch { unverified.Add(IdUtil.Value(cid)); }
                    }
                }
            }

            foreach (var insId in candidates)
            {
                bool isHard = hardIds.Contains(IdUtil.Value(insId));
                bool isUnverified = !isHard && unverified.Contains(IdUtil.Value(insId));
                if (!isHard && !isUnverified && s.ClearanceFor(set) <= 0) continue;

                var ins = doc.GetElement(insId) as InsulationLiningBase;
                if (ins == null || !ins.IsValidObject) continue;

                var otherId = ins.HostElementId;
                if (otherId == null || IdUtil.Value(otherId) == IdUtil.Value(el.Id)) continue;   // its own: CheckInsulation's

                Element other = doc.GetElement(otherId);
                if (other == null || !other.IsValidObject) continue;
                if (PairSuppressor.IsIgnorableElement(other)) continue;

                // The insulation is never in the target categories; the category rule is asked of the element.
                if (!set.TargetCategories.Contains(PairSuppressor.GetBuiltInCategory(other))) continue;
                if (!set.TargetFilter.Matches(other)) continue;
                if (ParameterFilter.TypeExcluded(other, s.AuditDrops)) continue;

                if (_sweepMode && OtherSideCovers(el, other, s, set)) continue;

                // Suppression runs against the ELEMENT, not its insulation (see CheckAgainstHost): joined to a
                // fitting or connected to the next run is as legitimately touching by way of its insulation.
                if (PairSuppressor.ShouldSuppress(doc, el, other)) continue;

                if (isHard)
                {
                    var real = ClashVerify.OverlapIsReal(el, ins);
                    if (real.HasValue && !real.Value)
                    {
                        // Confirmed zero overlap: distance 0, always within a configured
                        // clearance. Report as Clearance rather than dropping outright.
                        if (s.ClearanceFor(set) <= 0) continue;
                        isHard = false;
                    }
                    else if (!real.HasValue) { isHard = false; isUnverified = true; }
                }

                BoundingBoxXYZ insBox = null;
                try { insBox = ins.get_BoundingBox(null); } catch { }

                results.Add(new ClashResult
                {
                    HostElementId = el.Id,
                    HostElementName = PairSuppressor.ElementLabel(el),
                    HostCategory = PairSuppressor.CategoryName(el),
                    OtherElementId = otherId,
                    OtherElementName = PairSuppressor.ElementLabel(other),
                    OtherCategory = PairSuppressor.CategoryName(other),
                    LinkInstanceId = null,
                    ModelName = "Current model",
                    Severity = isHard ? ClashSeverity.Hard
                           : isUnverified ? ClashSeverity.Unverified : ClashSeverity.Clearance,
                    Location = insBox != null
                        ? GeometryUtil.OverlapCentre(hostBox, null, insBox, null)
                        : Centre(hostBox),
                    GeometryKind = ClashGeometry.ClassifyPair(el, other, null),
                    Provisional = IsProvisional(doc, el) || IsProvisional(doc, other),
                    Insulated = true,
                    Key = ClashResult.MakeKey(el.Id, null, otherId)
                });
            }

            return results;
        }

        private static XYZ Centre(BoundingBoxXYZ b)
        {
            return (b.Min + b.Max) * 0.5;
        }

        // ---------------------------------------------------------------- full sweep

        /// <summary>
        /// On-demand baseline run over every source element. Use this once after
        /// opening a model so the live checker starts from a known state instead of
        /// only knowing about elements you happen to touch this session.
        /// </summary>
        public static List<ClashResult> RunFullCheck(Document doc, ClashSettings settings, Action<int, int> progress = null, ClashSet set = null)
        {
            // ONE scope for the whole sweep, so a target hit from several batches is
            // extracted once. The per-batch scopes inside CheckElements nest into this
            // one and cost nothing. A sweep is read-only start to finish, which is what
            // makes holding it this long safe.
            using (ClashVerify.SolidScope())
            {
                List<ClashResult> results = null;
                InSweepMode(() => { results = RunFullCheckCore(doc, settings, progress, set ?? settings.Active); });
                return results;
            }
        }

        /// <summary>
        /// Time a representative SAMPLE of the sweep, so the pre-flight estimate can be
        /// MEASURED rather than predicted.
        ///
        /// Counting elements can never predict this well: most of a sweep's cost is the
        /// narrow phase, and in a large model most sources have nothing near them — the
        /// broad phase returns empty and they cost almost nothing. Two filters with
        /// identical element counts can differ tenfold. Timing real elements sidesteps all
        /// of that.
        ///
        /// Runs with <see cref="_sweepMode"/> ON, exactly as the real sweep does, so the
        /// ReverseWillCoverIt de-duplication is included in what is measured — sampling
        /// without it would over-report by about half.
        ///
        /// <paramref name="contextMs"/> comes back separately because it is a FIXED cost
        /// the real run pays once, not something to multiply by the sample ratio.
        /// </summary>
        public static long TimeSample(Document doc, ClashSettings settings,
                                      ICollection<ElementId> ids, out long contextMs, out int timed,
                                      ClashSet set = null)
        {
            contextMs = 0;
            timed = 0;
            if (doc == null || !doc.IsValidObject || ids == null || ids.Count == 0) return 0;

            // Scoped like the sweep it is timing — the sample must pay the same costs the
            // real run pays, or the estimate measures something else.
            using (ClashVerify.SolidScope())
            {

            set = set ?? settings.Active;

            // Locals, then copied to the out params: a lambda cannot assign to `out`.
            long ctxMs = 0, elapsed = 0;
            int  timedCount = 0;

            InSweepMode(() =>
            {
                var csw = Stopwatch.StartNew();
                var ctx = ClashContext.BuildContext(doc, settings, set);
                csw.Stop();
                ctxMs = csw.ElapsedMilliseconds;

                // NO WARM-UP PASS. Discarding a first batch was tried (2026-08-30) on the
                // theory that cold JIT and a cold solid cache inflated the sample; it made
                // the estimate WORSE (3.2x -> 3.7x), so the theory was wrong and the extra
                // machinery bought nothing. The real difference is structural: a full sweep
                // shares ClashVerify's solid cache across all 32,000 elements, so a source
                // is often already cached from having been a TARGET earlier, while a few
                // hundred elements strided across the model share almost nothing. That
                // cannot be recovered without doing most of the work, which is the thing
                // being avoided. The sample therefore reads high - the safe direction.
                var all = new List<ElementId>(ids);
                var sw = Stopwatch.StartNew();
                List<ElementId> skipped;
                CheckElements(doc, all, settings, null, ctx, out skipped, set);
                sw.Stop();
                timedCount = all.Count;
                elapsed = sw.ElapsedMilliseconds;
            });

            contextMs = ctxMs;
            timed = timedCount;
            return elapsed;
            }   // solid scope
        }

        /// <summary>The source ids a full check would sweep, for sampling.</summary>
        public static IList<ElementId> SourceIdsFor(Document doc, ClashSettings settings, ClashSet set = null)
        {
            if (doc == null || !doc.IsValidObject) return new List<ElementId>();
            set = set ?? settings.Active;
            try
            {
                return new List<ElementId>(new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(settings.BuildSourceFilter(set))
                    .ToElementIds());
            }
            catch { return new List<ElementId>(); }
        }

        /// <summary>
        /// Full check across the whole clash matrix: every enabled set in turn, merged
        /// and de-duplicated. Each set runs the ordinary <see cref="RunFullCheck"/> with
        /// its own scope and rules — the compatibility accessors point at whichever set
        /// is active, so the engine below needs no knowledge of sets at all. A pair a
        /// second set finds again (same Key) is dropped, so the register never lists it
        /// twice however many sets it satisfies.
        /// </summary>
        public static List<ClashResult> RunFullCheckAllSets(Document doc, ClashSettings settings, Action<int, int> progress = null)
        {
            // ShapeOf's cache (ClashGeometry) is keyed by element id alone, not by geometry, so an
            // element edited between two full checks without changing id — resized, rerouted, its
            // family swapped — kept classifying "through / ends in / inside" from the OLD solids
            // forever, since nothing on this, the canonical full-sweep entry point, ever cleared it.
            // Everything else InvalidateAll() also clears is deliberately left alone here: this is
            // the one cache with no OTHER invalidation path that a full check can rely on (a live
            // edit's own updater trigger, a filter change, a document switch — none of those fire
            // for "the same element's geometry changed since the last full check"). Found reviewing
            // the whole codebase, 2026-09-27.
            ClashGeometry.ClearCache();

            var all = new List<ClashResult>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);   // key -> index in `all`

            // Which sets are running, and how big each one is.
            //
            // Handing the caller's callback straight to each set made the progress
            // report restart: RunFullCheckCore counts done/total for ITS OWN sources,
            // so three enabled sets logged 0-100% three times and the ETA reset twice.
            // That matters more than it sounds — the progress line is the documented way
            // to tell a slow sweep from a hung one, and a bar that goes backwards is no
            // use for either.
            //
            // Counting first costs one extra collector per set (GetElementCount, not
            // ToElementIds) against a sweep measured in seconds, and buys a real total,
            // so the ETA is computed from elements rather than from an invented unit.
            var live = new List<ClashSet>();
            foreach (var set in settings.Sets)
                if (set.Enabled) live.Add(set);

            var counts = new int[live.Count];
            int grand = 0;
            if (progress != null)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    try
                    {
                        counts[i] = new FilteredElementCollector(doc)
                            .WhereElementIsNotElementType()
                            .WherePasses(settings.BuildSourceFilter(live[i]))
                            .GetElementCount();
                    }
                    catch { counts[i] = 0; }
                    grand += counts[i];
                }
            }

            // Passed, not assigned — see CheckElementsAllSets.
            int before = 0;
            for (int i = 0; i < live.Count; i++)
            {
                ClashSet set = live[i];

                // Captured per iteration on purpose: one shared variable would leave
                // every set's callback reading the LAST offset.
                int offset = before;
                Action<int, int> forSet = progress == null
                    ? null
                    : new Action<int, int>((d, t) =>
                        progress(offset + d > grand ? grand : offset + d, grand));

                foreach (var r in RunFullCheck(doc, settings, forSet, set))
                    AddDeduped(all, seen, r, set.Name);

                before += counts[i];
            }
            return all;
        }

        private static List<ClashResult> RunFullCheckCore(Document doc, ClashSettings settings, Action<int, int> progress, ClashSet set)
        {
            var results = new List<ClashResult>();
            if (doc == null || !doc.IsValidObject) return results;

            set = set ?? settings.Active;

            var sourceIds = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(settings.BuildSourceFilter(set))
                .ToElementIds();

            // Built once for the whole sweep, not once per batch.
            var ctx = ClashContext.BuildContext(doc, settings, set);

            int total = sourceIds.Count;
            int done = 0;
            const int batchSize = 100;

            var batch = new List<ElementId>(batchSize);
            foreach (var id in sourceIds)
            {
                batch.Add(id);
                if (batch.Count < batchSize) continue;

                List<ElementId> skipped;
                // No time budget on the manual run — it is allowed to take as long as it takes.
                results.AddRange(CheckElements(doc, batch, settings, null, ctx, out skipped, set));

                done += batch.Count;
                batch.Clear();
                if (progress != null) progress(done, total);
            }

            if (batch.Count > 0)
            {
                List<ElementId> skipped;
                results.AddRange(CheckElements(doc, batch, settings, null, ctx, out skipped, set));
                done += batch.Count;
                if (progress != null) progress(done, total);
            }

            return results;
        }
    }
}
