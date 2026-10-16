using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Asks the narrow question: does THIS pair still clash?
    ///
    /// Detection and verification are not the same problem, and conflating them is
    /// what makes resolved clashes linger. Finding a clash means asking "what does
    /// this element hit?" — broad phase, spatial index, candidate set. Verifying one
    /// means asking "do these two specific elements still overlap?", and both ids are
    /// already known, so it is a single intersection test with no search at all.
    ///
    /// That asymmetry is the point: verification is cheap enough to run over the
    /// whole result list repeatedly in idle time, which is how a pair resolved inside
    /// a link, during an undo, or while Live was paused gets noticed without the user
    /// pressing anything.
    ///
    /// It can only ever REMOVE pairs. A verification pass will never find a new
    /// clash — that stays the updater's job — so the two are complementary rather
    /// than alternatives.
    /// </summary>
    public static class PairVerifier
    {
        public enum Verdict
        {
            /// <summary>Still a real clash. Severity may have changed.</summary>
            StillClashing,

            /// <summary>Gone — geometry moved, or an element was deleted.</summary>
            Resolved,

            /// <summary>
            /// Could not be answered — most often a link that is no longer loaded.
            /// Treated as "keep": dropping a clash because we cannot see its other
            /// half would quietly lose real coordination issues.
            /// </summary>
            Unknown
        }

        /// <summary>Categories already warned about failing GetInsulationIds during
        /// verification, so a category that fails once (this can run every idle pass,
        /// for every listed row of that category) logs it once, not every 5 seconds.</summary>
        private static readonly HashSet<string> _warnedInsulationLookupFailed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Link instances by their id value, so a link pair can find its document
        /// without re-collecting links for every result.
        /// </summary>
        public static Dictionary<long, LinkContext> LinkLookup(Document doc)
        {
            var map = new Dictionary<long, LinkContext>();
            try
            {
                foreach (var l in LinkContext.CollectFrom(doc))
                    map[IdUtil.Value(l.InstanceId)] = l;
            }
            catch (Exception ex) { Log.Warn("Link lookup failed", ex); }
            return map;
        }

        public static Verdict Verify(
            Document doc, ClashResult r, ClashSettings s,
            Dictionary<long, LinkContext> links, out ClashSeverity severity)
        {
            severity = r.Severity;
            if (doc == null || !doc.IsValidObject || r == null) return Verdict.Unknown;

            Element host;
            try { host = doc.GetElement(r.HostElementId); } catch { return Verdict.Unknown; }
            if (host == null || !host.IsValidObject) return Verdict.Resolved;   // deleted

            return r.LinkInstanceId == null
                ? VerifyHost(doc, host, r, s, out severity)
                : VerifyLink(doc, host, r, s, links, out severity);
        }

        // ------------------------------------------------------------------ host

        private static Verdict VerifyHost(
            Document doc, Element host, ClashResult r, ClashSettings s, out ClashSeverity severity)
        {
            severity = r.Severity;

            Element other;
            try { other = doc.GetElement(r.OtherElementId); } catch { return Verdict.Unknown; }
            if (other == null || !other.IsValidObject) return Verdict.Resolved;

            double clearance = MaxClearanceForPair(s, host, other);

            // Both helpers below take severity as their own `out`, which they must
            // assign on every path — including Unknown, where there is nothing real to
            // report. Reading straight into `severity` here would silently replace the
            // row's actual prior severity with that placeholder on anything but a
            // confirmed StillClashing, breaking the (currently unused, but documented)
            // contract that this method's severity means something on every verdict —
            // not just the one every caller happens to check today. So a local holds
            // each helper's answer, and only a StillClashing verdict copies it into the
            // real out parameter; anything else leaves `severity` at its r.Severity seed.
            ClashSeverity bareSeverity;
            var verdict = VerifyElementPair(doc, host, other, clearance, out bareSeverity);
            if (verdict == Verdict.StillClashing)
            {
                severity = bareSeverity;

                // The bare pipe reaches this pair only through its clearance envelope, but
                // its insulation may physically intersect it — the case detection now
                // reports as Hard (ClashEngine.AddDeduped). Returning here would let the
                // next idle pass demote it straight back to Clearance.
                //
                // ONLY for a row that is Hard right now: the question is "does the insulation
                // justify keeping it Hard", so it is asked exactly when the answer would change
                // the outcome. A row already at Clearance has nothing to be kept, and asking for
                // it costs GetInsulationIds plus a solid intersection per insulation piece, on
                // every idle pass, for every soft row in an insulation-aware set.
                if (bareSeverity != ClashSeverity.Hard && r.Severity == ClashSeverity.Hard
                    && AnySetWantsInsulation(s, host, other))
                {
                    ClashSeverity harder;
                    if (VerifyHostInsulation(doc, host, other, clearance, out harder, seekHard: true)
                            == Verdict.StillClashing && harder == ClashSeverity.Hard)
                        severity = ClashSeverity.Hard;
                    else if (VerifyHostOtherInsulation(doc, host, other, clearance, out harder, seekHard: true)
                            == Verdict.StillClashing && harder == ClashSeverity.Hard)
                        severity = ClashSeverity.Hard;
                }
                return verdict;
            }
            if (verdict != Verdict.Resolved) return verdict;

            // The BARE host is provably clear. If any enabled set whose scope could
            // contain this pair has insulation-aware checking on, the host's own
            // insulation may still be what actually clashes — see the note on
            // AnySetWantsInsulation for why this must run for EVERY such pair, not only
            // rows flagged ClashResult.Insulated. Uses the pipe's own clearance
            // envelope, not a fresh MaxClearanceForPair call keyed to the insulation
            // element's category — an insulation category is never in any set's scope
            // on either side, so that call would silently fall back to the model
            // default and ignore the set's real override.
            if (!AnySetWantsInsulation(s, host, other)) return Verdict.Resolved;

            // Both sources, as in VerifyLink: the host element's own insulation, and the insulation of the
            // OTHER element (which the engine tests when that element is only a target). Leaving the second out
            // is a false fix - a pair the engine lists on the strength of the other element's insulation would
            // read Resolved here on the first idle pass. Scanned with seekHard because the two can disagree,
            // and a Hard must beat a Clearance rather than be demoted by whichever answered first.
            return ScanPieces(new[] { 0, 1 },
                (int which, out ClashSeverity s2) => which == 0
                    ? VerifyHostInsulation(doc, host, other, clearance, out s2, seekHard: true)
                    : VerifyHostOtherInsulation(doc, host, other, clearance, out s2, seekHard: true),
                true, out severity);
        }

        /// <summary>
        /// The mirror of <see cref="VerifyHostInsulation"/>: the insulation of the OTHER element against the
        /// host element - the pair the engine's CheckAgainstHostInsulation finds when the other element is only
        /// a target. Same rules, through the same shared loop.
        /// </summary>
        private static Verdict VerifyHostOtherInsulation(
            Document doc, Element host, Element other, double clearance, out ClashSeverity severity,
            bool seekHard = false)
        {
            return VerifyInsulationOf(doc, other, "", out severity, seekHard,
                (Element ins, out ClashSeverity s2) => VerifyElementPair(doc, host, ins, clearance, out s2));
        }

        /// <summary>
        /// Re-tests EVERY insulation element the bare host itself did not already
        /// account for, stopping at the first that still clashes. Unknown from one
        /// element must not be overridden by Resolved from a later one — if nothing
        /// confirms StillClashing, the verdict is Unknown when any element could not be
        /// measured, Resolved only when EVERY element was measured and cleared.
        ///
        /// <paramref name="seekHard"/> is the other question this answers: not "does
        /// anything still clash" but "does the insulation clash HARDER than the bare pipe
        /// already does". The bare pipe can only reach a pair through its clearance
        /// envelope while its insulation — ~50 mm thick, so within that envelope of
        /// essentially the same objects — physically intersects them. Detection now keeps
        /// that Hard verdict (see ClashEngine.AddDeduped); a verifier that returned at the
        /// bare pipe's Clearance would demote it again on the next idle pass. With it set,
        /// a Clearance-severity confirmation does not end the scan — only Hard does.
        /// </summary>
        private static Verdict VerifyHostInsulation(
            Document doc, Element host, Element other, double clearance, out ClashSeverity severity,
            bool seekHard = false)
        {
            return VerifyInsulationOf(doc, host, "", out severity, seekHard,
                (Element ins, out ClashSeverity s) => VerifyElementPair(doc, ins, other, clearance, out s));
        }

        /// <summary>
        /// One element against one element, host-document geometry — the test VerifyHost
        /// has always done, factored out so <see cref="VerifyHostInsulation"/> can run the
        /// identical test against an insulation element without a second copy of it.
        /// </summary>
        private static Verdict VerifyElementPair(
            Document doc, Element a, Element b, double clearance, out ClashSeverity severity)
        {
            severity = ClashSeverity.Hard;

            // One element against one element — no collector over the model, no index.
            bool hard;
            try
            {
                var hits = new FilteredElementCollector(doc, new List<ElementId> { b.Id })
                    .WherePasses(new ElementIntersectsElementFilter(a))
                    .ToElementIds();
                hard = hits.Count > 0;
            }
            catch
            {
                // Some elements reject the intersection filter (in-place families,
                // certain imports). Cannot prove it is gone, so keep it.
                return Verdict.Unknown;
            }

            if (hard)
            {
                // CONFIRM BY VOLUME, the same boolean the engine confirms with before it
                // reports anything. ElementIntersectsElementFilter matches geometry that
                // is not solid: measured on PRJ01, 197 of 2,165 reported clashes — 9% —
                // had ZERO intersection volume, 190 of them fire alarm devices whose
                // detection-coverage zones the filter counts as geometry.
                //
                // Detection has always confirmed; verification did not, so the two
                // disagreed about what a clash IS. That was survivable while every caller
                // only ever KEPT a row on StillClashing. It stopped being survivable when
                // the resolution audit began WITHDRAWING on it: there, an unconfirmed hit
                // retracts a true record of a real fix, which is the exact inversion that
                // pass exists to correct — and it reads Snapshot(), so a withdrawn
                // occurrence is never re-tested.
                //
                // null is UNMEASURABLE, which is not "no clash". It must never reach a
                // withdrawal, so it returns Unknown and the row stands.
                //
                // Intersects with UNCACHED solids on both sides, not OverlapIsReal.
                // OverlapIsReal is the engine's entry point and takes both sides from
                // SolidsOf — the per-check cache, which is only safe inside one read-only
                // sweep. Nothing in this class clears it (all four ClearSolidCache callers
                // are in ClashEngine), and verification runs from Idling AFTER the user has
                // been editing, so the cache is exactly the pre-edit geometry VerifyLink's
                // note warns about. Calling it here would have made this class contradict
                // its own comment one method away.
                bool? real = ClashVerify.Intersects(
                    ClashVerify.ExtractSolids(a), ClashVerify.ExtractSolids(b));
                if (real == null) return Verdict.Unknown;

                if (real.Value) { severity = ClashSeverity.Hard; return Verdict.StillClashing; }

                // Provably zero volume: not a hard clash. Fall through — clearance may
                // still hold, and if it does not, this pair genuinely is resolved.
            }

            if (clearance > 0 && BoxesOverlap(a, null, b, null, clearance))
            {
                severity = ClashSeverity.Clearance;
                return Verdict.StillClashing;
            }

            return Verdict.Resolved;
        }

        // ------------------------------------------------------------------ link

        private static Verdict VerifyLink(
            Document doc, Element host, ClashResult r, ClashSettings s,
            Dictionary<long, LinkContext> links, out ClashSeverity severity)
        {
            severity = r.Severity;

            LinkContext link = null;
            if (links != null) links.TryGetValue(IdUtil.Value(r.LinkInstanceId), out link);

            // Link unloaded or gone: we genuinely cannot tell. Keep the row.
            if (link == null || link.Document == null || !link.Document.IsValidObject)
                return Verdict.Unknown;

            Element other;
            try { other = link.Document.GetElement(r.OtherElementId); } catch { return Verdict.Unknown; }
            if (other == null || !other.IsValidObject) return Verdict.Resolved;

            double clearance = MaxClearanceForPair(s, host, other, link.Name);

            // See the note in VerifyHost on why the callee's answer goes through a
            // local first: it must assign severity on every path, including Unknown,
            // and only a confirmed StillClashing should be allowed to replace the row's
            // actual prior severity.
            ClashSeverity bareSeverity;
            var verdict = VerifyElementAgainstLink(doc, host, other, link, clearance, out bareSeverity);
            if (verdict == Verdict.StillClashing)
            {
                severity = bareSeverity;

                // Same as VerifyHost: a bare Clearance may hide insulation that Hard-clashes.
                if (bareSeverity != ClashSeverity.Hard && r.Severity == ClashSeverity.Hard
                    && AnySetWantsInsulation(s, host, other, link.Name))
                {
                    ClashSeverity harder;
                    if (VerifyLinkInsulation(doc, host, other, link, clearance, out harder, seekHard: true)
                            == Verdict.StillClashing && harder == ClashSeverity.Hard)
                        severity = ClashSeverity.Hard;
                    else if (VerifyLinkOtherInsulation(doc, host, other, link, clearance, out harder, seekHard: true)
                            == Verdict.StillClashing && harder == ClashSeverity.Hard)
                        severity = ClashSeverity.Hard;
                }
                return verdict;
            }
            if (verdict != Verdict.Resolved) return verdict;

            // The bare host is provably clear in link space too. Same reasoning as
            // VerifyHost's own note: an insulation-aware set's scope may still cover
            // this pair, and its insulation is what a full check would actually find.
            if (!AnySetWantsInsulation(s, host, other, link.Name)) return Verdict.Resolved;

            // Both directions, because the engine finds a pair in both: the host element's own
            // insulation, and the insulation of the linked element. Leaving out the second is
            // not a missing feature but a false fix - a pair the engine lists on the strength of
            // the linked pipe's insulation would read Resolved here, on the first idle pass, and
            // be written to the ledger as fixed while nothing had moved.
            //
            // The two sources are scanned with seekHard, not stopped at the first confirmation: they can
            // DISAGREE. With both pipes insulated, the host's own insulation can sit inside the clearance
            // (a Clearance hit) while the linked pipe's thicker insulation reaches the host's bare surface
            // (a Hard hit), and the engine keeps the Hard. Stopping at whichever answered first would demote
            // the row on the next idle pass. Unknown if either could not say, so an unanswerable test can
            // never be read as "resolved" (the same rules ScanPieces holds for several pieces).
            return ScanPieces(new[] { 0, 1 },
                (int which, out ClashSeverity s) => which == 0
                    ? VerifyLinkInsulation(doc, host, other, link, clearance, out s, seekHard: true)
                    : VerifyLinkOtherInsulation(doc, host, other, link, clearance, out s, seekHard: true),
                true, out severity);
        }

        /// <summary>
        /// The mirror of <see cref="VerifyLinkInsulation"/>: the insulation of the LINKED element
        /// (looked up in the link's own document) against the host element - the pair the engine's
        /// CheckAgainstLinkInsulation finds. Same stop-on-first-StillClashing, same
        /// Unknown-beats-Resolved rule, same <paramref name="seekHard"/>.
        /// </summary>
        private static Verdict VerifyLinkOtherInsulation(
            Document doc, Element host, Element other, LinkContext link, double clearance,
            out ClashSeverity severity, bool seekHard = false)
        {
            // The HOST element is the geometry being tested; the linked insulation is what it is tested
            // against. `clearance` is the real host/other pair's, as always.
            return VerifyInsulationOf(link.Document, other, "linked ", out severity, seekHard,
                (Element ins, out ClashSeverity s) => VerifyElementAgainstLink(doc, host, ins, link, clearance, out s));
        }

        /// <summary>Link-space counterpart to <see cref="VerifyHostInsulation"/> — same
        /// stop-on-first-StillClashing, same Unknown-beats-Resolved rule when nothing
        /// confirms, and the same <paramref name="seekHard"/> (see there).</summary>
        private static Verdict VerifyLinkInsulation(
            Document doc, Element host, Element other, LinkContext link, double clearance,
            out ClashSeverity severity, bool seekHard = false)
        {
            return VerifyInsulationOf(doc, host, "", out severity, seekHard,
                (Element ins, out ClashSeverity s) => VerifyElementAgainstLink(doc, ins, other, link, clearance, out s));
        }

        /// <summary>
        /// One host-document element's solids, pushed into link space, against one link
        /// element — the test VerifyLink has always done, factored out so
        /// <see cref="VerifyLinkInsulation"/> can run the identical test against an
        /// insulation element without a second copy of it. <paramref name="a"/> is
        /// whichever geometry is being tested (the pipe, or one of its insulation
        /// elements); <paramref name="clearance"/> is always computed from the REAL
        /// host/other pair by the caller, never re-derived here from `a`'s own category.
        /// </summary>
        private static Verdict VerifyElementAgainstLink(
            Document doc, Element a, Element other, LinkContext link, double clearance,
            out ClashSeverity severity)
        {
            severity = ClashSeverity.Hard;
            bool hard = false;
            bool tested = false;

            // ExtractSolids, not GeometryUtil.GetSolids: Fine detail with a Medium
            // fallback. This used to call GetSolids(host) with the DEFAULT detail,
            // which is Medium, and 452 of 1,050 sampled source elements on PRJ01 —
            // 43%, including every Lighting Device sampled, 148 Pipe Fittings and 135
            // Electrical Fixtures — have no solids at Medium at all. The engine moved
            // off that default for exactly this reason (see ClashEngine.CheckAgainstLink);
            // this method was the one that did not, and it is the one that decides a
            // clash is FIXED. See the guard below for what that cost.
            //
            // Uncached on purpose: this runs from Idling after the user has been
            // editing, so the sweep's cache holds pre-edit geometry.
            var solids = ClashVerify.ExtractSolids(a) ?? new List<Solid>();
            var one = new List<ElementId> { other.Id };

            // Transformed once and KEPT: the filter test below and the volume
            // confirmation after it both need the host's geometry in link coordinates,
            // and re-transforming for the second would be the same work twice.
            var hostInLink = new List<Solid>();
            foreach (var solid in solids)
            {
                Solid moved;
                try { moved = SolidUtils.CreateTransformed(solid, link.ToLink); }
                catch { continue; }
                if (moved == null || moved.Volume <= 1e-9) continue;
                hostInLink.Add(moved);
            }

            foreach (var moved in hostInLink)
            {
                try
                {
                    var hits = new FilteredElementCollector(link.Document, one)
                        .WherePasses(new ElementIntersectsSolidFilter(moved))
                        .ToElementIds();
                    tested = true;
                    if (hits.Count > 0) { hard = true; break; }
                }
                catch { /* try the next solid */ }
            }

            if (hard)
            {
                // Confirmed by volume, exactly as VerifyHost now is and as the engine
                // always was — see the note there for why an unconfirmed hit became
                // dangerous the moment a caller started withdrawing on it, and for why
                // this goes through Intersects rather than LinkOverlapIsReal.
                //
                // BOTH SIDES UNCACHED. The host solids above already are; the link
                // element's are extracted here for the same reason, because
                // LinkOverlapIsReal would have taken them from the sweep's cache and
                // undone the invariant the note at the top of this method states.
                bool? real = ClashVerify.Intersects(hostInLink, ClashVerify.ExtractSolids(other));
                if (real == null) return Verdict.Unknown;

                if (real.Value) { severity = ClashSeverity.Hard; return Verdict.StillClashing; }

                // Provably zero volume: not a hard clash. Fall through to clearance.
            }

            // Clearance first: it works off bounding boxes, so it can still confirm a
            // clash even when no solid was testable.
            if (clearance > 0 && BoxesOverlap(a, null, other, link.ToHost, clearance))
            {
                severity = ClashSeverity.Clearance;
                return Verdict.StillClashing;
            }

            // NOTHING WAS ACTUALLY TESTED — say Unknown, never Resolved.
            //
            // This guard used to read `!tested && solids.Count > 0`, which let the
            // worst case straight through: no solids at all meant the loop never ran,
            // tested stayed false, `0 > 0` was false, and the method fell to Resolved
            // below. A row that CheckAgainstLink had measured a real overlap for was
            // then handed to NoteGone + ResolvedLog.Add + RemoveKeys and written to the
            // permanent record as fixed. Under-reporting, filed as a fix, on the
            // element classes least likely to have Medium geometry.
            //
            // Resolved is a claim that the clash is gone. It may only be returned by a
            // test that ran.
            if (!tested) return Verdict.Unknown;

            return Verdict.Resolved;
        }

        // ------------------------------------------------------ the insulation verify loop

        /// <summary>Tests ONE insulation piece of a pair; returns the verdict and, when it is
        /// StillClashing, the severity. The only thing the three callers differ in.</summary>
        internal delegate Verdict PieceTest<T>(T piece, out ClashSeverity severity);

        /// <summary>
        /// The one loop behind VerifyHostInsulation, VerifyLinkInsulation and
        /// VerifyLinkOtherInsulation - which were three copies, and the first defect found in them
        /// (an element that cannot carry insulation read Unknown) had to be fixed in all three.
        ///
        /// Looks up <paramref name="owner"/>'s insulation in <paramref name="ownerDoc"/> and scans it.
        /// </summary>
        private static Verdict VerifyInsulationOf(
            Document ownerDoc, Element owner, string what, out ClashSeverity severity, bool seekHard,
            PieceTest<Element> test)
        {
            severity = ClashSeverity.Hard;

            // Cannot tell whether this owner has insulation at all, let alone whether it clashes -
            // Resolved is a claim that nothing here does, and that claim needs a test that ran. Same
            // rule VerifyElementPair/VerifyElementAgainstLink already apply to their own unanswerable
            // cases. (An element that CANNOT carry insulation is a different thing: see InsulationIdsOf.)
            var insIds = InsulationIdsOf(ownerDoc, owner, what);
            if (insIds == null) return Verdict.Unknown;
            if (insIds.Count == 0) return Verdict.Resolved;

            var pieces = new List<Element>();
            foreach (var insId in insIds)
            {
                Element ins;
                try { ins = ownerDoc.GetElement(insId); } catch { continue; }
                if (ins == null || !ins.IsValidObject) continue;
                pieces.Add(ins);
            }
            return ScanPieces(pieces, test, seekHard, out severity);
        }

        /// <summary>
        /// The verdict over a set of insulation pieces. Stops at the first StillClashing, unless
        /// <paramref name="seekHard"/>; then only a Hard one ends the scan and the best severity seen is kept
        /// (the question is then not "does anything still clash" but "does the insulation clash HARDER
        /// than the bare pipe already does"). If nothing confirms, the verdict is Unknown when ANY piece
        /// could not be measured and Resolved only when every piece was measured and cleared - an
        /// unanswerable test must never be read as "fixed".
        ///
        /// Generic and internal so it can be tested with plain values. A pipe or duct carries ONE
        /// insulation element, so with real Revit data the multi-piece rules could never be observed;
        /// they are defensive, and this is what lets a test hold them to account.
        /// </summary>
        internal static Verdict ScanPieces<T>(IEnumerable<T> pieces, PieceTest<T> test, bool seekHard,
            out ClashSeverity severity)
        {
            severity = ClashSeverity.Hard;

            bool anyUnknown = false;
            bool anyClash = false;
            ClashSeverity best = ClashSeverity.Clearance;
            foreach (var piece in pieces)
            {
                ClashSeverity sev;
                var verdict = test(piece, out sev);
                if (verdict == Verdict.StillClashing)
                {
                    if (!seekHard) { severity = sev; return Verdict.StillClashing; }
                    anyClash = true;
                    if (sev == ClashSeverity.Hard) { severity = ClashSeverity.Hard; return Verdict.StillClashing; }
                    best = sev;
                }
                else if (verdict == Verdict.Unknown) anyUnknown = true;
            }

            if (anyClash) { severity = best; return Verdict.StillClashing; }
            return anyUnknown ? Verdict.Unknown : Verdict.Resolved;
        }
        // ------------------------------------------------------------ insulation lookup

        /// <summary>
        /// The insulation ids of <paramref name="el"/> in <paramref name="doc"/>: possibly EMPTY, or
        /// null when Revit could not say.
        ///
        /// The distinction is the point. GetInsulationIds throws ArgumentException ("This id does not
        /// represent a valid host for insulation") for anything that cannot carry insulation at all -
        /// a wall, a cable tray, a fixture. That is not "unknown"; it is a definite "none", and
        /// answering Unknown to it is not harmless: Unknown keeps a row FOREVER. Found by review on
        /// 2026-09-20 when the linked-insulation verifier asked a linked wall, and it turned out the
        /// two older call sites had the same flaw for any set whose sources are not all pipes and
        /// ducts (cable trays, conduit, fixtures), whose fixed pairs could never have been retired.
        ///
        /// Anything else Revit throws IS an unanswerable case: null, warned once per category.
        /// </summary>
        internal static ICollection<ElementId> InsulationIdsOf(Document doc, Element el, string what)
        {
            try
            {
                return InsulationLiningBase.GetInsulationIds(doc, el.Id) ?? new List<ElementId>();
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                return new List<ElementId>();
            }
            catch (Exception ex)
            {
                if (_warnedInsulationLookupFailed.Add(what + (PairSuppressor.CategoryName(el) ?? "?")))
                    Log.Warn("Insulation lookup: GetInsulationIds failed for " + what
                             + PairSuppressor.CategoryName(el) + " - it cannot say whether these have insulation "
                             + "(the engine checks them bare, the verifier keeps their rows). Logged once per category.", ex);
                return null;
            }
        }
        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// The clearance to verify a pair against, now that a set can override the
        /// model default: the LARGEST clearance of any enabled set whose scope
        /// contains this pair, in EITHER direction (src=host or src=other) — never
        /// just the set that originally found it, which <see cref="ClashResult.SetName"/>
        /// names but this method deliberately never reads.
        ///
        /// Why it must be the largest, not the row's own set: a pair a zero-clearance
        /// set finds HARD, that later backs off into volume-zero territory, can still
        /// sit inside ANOTHER enabled set's clearance envelope for the same two
        /// categories. Verifying against only the finding set's clearance would read
        /// that as Resolved and hand it to NoteGone + ResolvedLog.Add — a false fix
        /// filed because of which set happened to report it first, the exact class of
        /// bug the Clearance-changed fix (Commands.SettingsCommand) exists to prevent,
        /// reached here by a set boundary instead of a setting change. It is also the
        /// only correct answer for a row rebuilt from its key (RecordRecheckHandler),
        /// which never carries a set name to fall back on at all.
        ///
        /// Falls back to the model default when NO enabled set's scope claims the pair
        /// any more (every set that once matched it was since disabled or narrowed) —
        /// the same thing every pair inherited before per-set overrides existed.
        /// </summary>
        /// <remarks>
        /// Internal rather than private so the in-Revit checks can call the real method — the same
        /// reason <see cref="ClashEngine.ReverseWillCoverIt"/> and
        /// <see cref="ClashVerify.Intersects"/> are internal, not a copy the checks build
        /// its own version of.
        /// </remarks>
        internal static double MaxClearanceForPair(
            ClashSettings s, Element host, Element other, string linkName = null)
        {
            double max = 0.0;
            bool any = false;
            foreach (var set in s.Sets)
            {
                if (!set.Enabled) continue;
                if (LinkExcludesPair(s, set, linkName, other)) continue;
                if (!InScope(s, set, host, other) && !InScope(s, set, other, host)) continue;
                double c = s.ClearanceFor(set);
                if (!any || c > max) { max = c; any = true; }
            }
            return any ? max : s.ClearanceFeet;
        }

        /// <summary>
        /// Does any ENABLED set whose scope could contain this pair have insulation-
        /// aware checking on? Governs whether <see cref="VerifyHost"/>/<see cref="VerifyLink"/>
        /// bother looking at the host's insulation at all once its bare geometry is
        /// provably clear.
        ///
        /// Checked for EVERY qualifying pair, never gated on <see cref="ClashResult.Insulated"/>
        /// — a row rebuilt from its key (RecordRecheckHandler) carries no flag, and a row
        /// the bare pipe itself found (Insulated=false) can still have insulation that
        /// keeps it clashing after the bare geometry backs off, which is exactly the case
        /// this exists to catch rather than silently resolving.
        /// </summary>
        private static bool AnySetWantsInsulation(
            ClashSettings s, Element host, Element other, string linkName = null)
        {
            foreach (var set in s.Sets)
            {
                if (!set.Enabled || !set.InsulationAware) continue;
                if (LinkExcludesPair(s, set, linkName, other)) continue;
                if (InScope(s, set, host, other) || InScope(s, set, other, host)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="set"/> would never collect <paramref name="other"/>
        /// as a target from the link named <paramref name="linkName"/> at all — its own
        /// per-link on/off gate or category exclusion, via the same
        /// <see cref="ClashSettings.TargetCategoriesForLink"/> that
        /// <see cref="ClashContext.BuildContext"/> already calls when it actually builds
        /// each set's link target index. <see cref="InScope"/> has no notion of links at
        /// all — it tests category/filter/audit-drops only — so without this a set that
        /// has explicitly turned a link off (or excluded this category from it) was still
        /// counted as "claiming" a pair found through that exact link, handing it that
        /// set's clearance or insulation-aware flag regardless. A null or empty linkName
        /// (a host-side pair) never excludes anything — there is no link to gate.
        /// </summary>
        private static bool LinkExcludesPair(ClashSettings s, ClashSet set, string linkName, Element other)
        {
            if (string.IsNullOrEmpty(linkName)) return false;
            try
            {
                var cat = PairSuppressor.GetBuiltInCategory(other);
                return !s.TargetCategoriesForLink(set, linkName).Contains(cat);
            }
            catch { return false; }   // never let a scope check crash verification
        }

        /// <summary>
        /// Would <paramref name="set"/> accept <paramref name="src"/> as a source and
        /// <paramref name="tgt"/> as a target — category, parameter filter, and the
        /// global audit drops, the same three tests <see cref="ClashEngine.ReverseWillCoverIt"/>
        /// applies for the same reason (deciding whether a set's scope covers a
        /// direction of a pair), kept as its own copy here rather than shared: that
        /// method is sweep-mode-gated and returns early outside a sweep, which would
        /// silently make this always false from Idling, where verification runs.
        /// </summary>
        private static bool InScope(ClashSettings s, ClashSet set, Element src, Element tgt)
        {
            try
            {
                if (!set.SourceCategories.Contains(PairSuppressor.GetBuiltInCategory(src))) return false;
                if (!set.SourceFilter.Matches(src)) return false;
                if (ParameterFilter.TypeExcluded(src, s.AuditDrops)) return false;

                if (!set.TargetCategories.Contains(PairSuppressor.GetBuiltInCategory(tgt))) return false;
                if (!set.TargetFilter.Matches(tgt)) return false;
                if (ParameterFilter.TypeExcluded(tgt, s.AuditDrops)) return false;

                return true;
            }
            catch { return false; }   // never let a scope check crash verification
        }

        /// <summary>
        /// Bounding-box overlap in host coordinates, with the clearance applied to
        /// the host side only — matching how the engine inflates its query box, so
        /// verification agrees with detection rather than quietly disagreeing.
        /// </summary>
        private static bool BoxesOverlap(
            Element a, Transform aToHost, Element b, Transform bToHost, double inflate)
        {
            try
            {
                var ba = a.get_BoundingBox(null);
                var bb = b.get_BoundingBox(null);
                if (ba == null || bb == null) return false;

                var oa = GeometryUtil.TransformToOutline(ba, aToHost, inflate);
                var ob = GeometryUtil.TransformToOutline(bb, bToHost, 0.0);

                if (oa.MaximumPoint.X < ob.MinimumPoint.X || oa.MinimumPoint.X > ob.MaximumPoint.X) return false;
                if (oa.MaximumPoint.Y < ob.MinimumPoint.Y || oa.MinimumPoint.Y > ob.MaximumPoint.Y) return false;
                if (oa.MaximumPoint.Z < ob.MinimumPoint.Z || oa.MinimumPoint.Z > ob.MaximumPoint.Z) return false;
                return true;
            }
            catch (Exception ex)
            {
                // A geometry error here must NOT quietly mark the clash resolved --
                // under-reporting is the dangerous direction for a clash tool. Keep it
                // (treat as still overlapping); the next verify pass re-checks it.
                Log.Warn("Clearance re-check failed; keeping the clash pending re-verify", ex);
                return true;
            }
        }
    }
}
