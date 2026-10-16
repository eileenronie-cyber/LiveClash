using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The "is this overlap real?" concern, split out of ClashEngine.
    ///
    /// Revit's intersection filter matches geometry that is not solid and cannot be
    /// built, so every hard hit the filter reports is confirmed here by a boolean
    /// volume test before it reaches the register. This class owns that test and the
    /// per-check solid cache it needs; nothing else in it.
    /// </summary>
    internal static class ClashVerify
    {
        /// <summary>
        /// Smallest intersection volume that counts as a clash, in cubic feet
        /// (~0.028 mm3). Deliberately tiny. The smallest genuine overlap measured on
        /// PRJ01 was 164 mm3, so there is a ~5,800x margin available -- but a service
        /// grazing a beam by half a millimetre is a real coordination issue, and no
        /// sliver false positive has ever been observed here. Accuracy first.
        /// </summary>
        internal const double MinOverlapVolume = 1e-9;

        // Solids kept for the duration of a sweep, so a chiller hit by forty pipes is
        // extracted once. Cleared with every other id-keyed cache.
        //
        // Keyed by DOCUMENT AND id, not id alone: link documents number their elements
        // independently, so a link element and a host element routinely share an id
        // value. With a bare id key, the first one cached would be handed back for the
        // other, and the boolean would be measuring the wrong geometry entirely.
        private static readonly object VerifyGate = new object();

        /// <summary>
        /// NULL unless a <see cref="SolidScope"/> is open, and that is the whole design.
        ///
        /// A cached Solid is only valid for as long as the document does not change, so
        /// "when may I use the cache" and "what is a check" are the same question. This
        /// used to be a permanently-live static that every caller was trusted to clear,
        /// and the trust did not hold: PairVerifier never cleared it and reached the
        /// cache anyway, which put an Idling-time verification — running AFTER the user
        /// had been editing — onto pre-edit geometry. Two comments said not to. Comments
        /// are not a mechanism.
        ///
        /// Now the cache cannot outlive a scope, and a caller with no scope open gets
        /// fresh geometry rather than somebody else's. The invariant is held by the
        /// lifetime of an object instead of by everyone remembering.
        /// </summary>
        private static Dictionary<string, List<Solid>> _verifySolids;
        private static int _scopeDepth;

        /// <summary>
        /// Open the window in which solids may be cached: one check, one sweep.
        ///
        /// Nesting is counted, so a sweep may hold one open across all its batches while
        /// each batch opens its own — the inner scopes are no-ops and the outer one owns
        /// the lifetime. That is what lets RunFullCheck extract a target once for the
        /// whole sweep instead of once per batch of 100.
        ///
        /// Dispose it. A scope left open is a cache left live, which is the bug this
        /// replaced.
        /// </summary>
        internal static IDisposable SolidScope()
        {
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool _disposed;

            internal Scope()
            {
                lock (VerifyGate)
                {
                    if (_scopeDepth == 0)
                        _verifySolids = new Dictionary<string, List<Solid>>();
                    _scopeDepth++;
                }
            }

            public void Dispose()
            {
                if (_disposed) return;        // disposing twice must not close someone else's scope
                _disposed = true;

                lock (VerifyGate)
                {
                    if (--_scopeDepth <= 0)
                    {
                        _scopeDepth = 0;
                        _verifySolids = null;
                    }
                }
            }
        }

        /// <summary>
        /// The element's solids, cached FOR THE OPEN SCOPE ONLY.
        ///
        /// Inside a <see cref="SolidScope"/> this is the cheap accessor — a chiller hit
        /// by forty pipes is extracted once. Outside one there is no cache to read, so it
        /// is exactly <see cref="ExtractSolids"/> and costs an extraction every call.
        /// That is deliberate: a Solid does not survive the document changing, and a
        /// caller that has not said "this is one check" has not earned the right to reuse
        /// one. Choosing wrong between this and ExtractSolids can cost time; it can no
        /// longer cost a stale answer.
        ///
        /// Keyed by DOCUMENT and id, not id alone — link documents number their elements
        /// independently, so a link element and a host element routinely share an id
        /// value, and a bare id key would hand one back for the other and measure the
        /// wrong geometry entirely.
        /// </summary>
        internal static List<Solid> SolidsOf(Element e)
        {
            if (e == null) return null;

            // NO SCOPE, NO CACHE. Reading a cache nobody owns is how a verification that
            // runs after an edit ends up measuring pre-edit geometry, so outside a scope
            // this is simply the uncached rule. Callers that want the saving open one.
            lock (VerifyGate) { if (_verifySolids == null) return ExtractSolids(e); }

            string key;
            try { key = ClashEngine.DocKey(e.Document) + "#" + IdUtil.Value(e.Id); }
            catch { return null; }

            lock (VerifyGate)
            {
                List<Solid> hit;
                // Re-checked under the lock: the scope could have closed since the test
                // above, and writing into a dictionary that is no longer anybody's is
                // both pointless and a leak.
                if (_verifySolids != null && _verifySolids.TryGetValue(key, out hit)) return hit;
            }

            List<Solid> solids = ExtractSolids(e);

            lock (VerifyGate) { if (_verifySolids != null) _verifySolids[key] = solids; }
            return solids;
        }

        /// <summary>
        /// The detail-level rule on its own, with NO caching.
        ///
        /// FINE, not Medium. 220 pipe fittings, 89 electrical boxes and 44 occupancy
        /// detectors on this model have NO solids at Medium at all — their geometry
        /// exists only at Fine — so verifying at Medium reported them as unmeasurable
        /// and skipped the check, letting 54 more zero-volume pairs through. Medium
        /// remains as a fallback for anything that somehow yields nothing at Fine.
        ///
        /// Separate from <see cref="SolidsOf"/> because the cache is only safe inside one
        /// read-only check, and this is the half that has nothing to do with caching.
        ///
        /// You no longer have to choose between them to stay correct: outside a
        /// <see cref="SolidScope"/>, SolidsOf IS this. The distinction is now about
        /// intent rather than safety — call this when caching would be meaningless
        /// anyway, call SolidsOf when a scope is open and the saving is the point.
        ///
        /// Keep the two in step: this IS the rule, and SolidsOf is it plus a cache.
        /// </summary>
        internal static List<Solid> ExtractSolids(Element e)
        {
            if (e == null) return null;

            List<Solid> solids = null;
            try { solids = GeometryUtil.GetSolids(e, ViewDetailLevel.Fine); }
            catch { }
            if (solids == null || solids.Count == 0)
            {
                try { solids = GeometryUtil.GetSolids(e, ViewDetailLevel.Medium); }
                catch { solids = null; }
            }
            return solids;
        }

        /// <summary>
        /// Drop whatever the OPEN scope has cached, without closing it. A no-op when no
        /// scope is open, because then there is nothing to be stale.
        ///
        /// Why a Solid must not outlive its check: Revit geometry handed out before a
        /// regeneration goes stale, and every boolean against a stale solid throws
        /// "A managed exception was thrown by Revit" — so Intersects returned null for
        /// the whole pair and the engine reported it as Unverified instead of dropping
        /// it. Measured on PRJ01 with 1,578 stale entries: a pipe against an external
        /// pole light gave 6 boolean attempts, 6 throws, verdict "unmeasurable", while
        /// the same two elements re-extracted gave 6 successes and a measured overlap of
        /// ZERO. The pair belonged nowhere near the register.
        ///
        /// Scope lifetime now does this job for the ordinary case. This remains for the
        /// one thing lifetime cannot express: InvalidateAll, which drops every engine
        /// cache mid-session because the model or the filter changed underneath them.
        /// </summary>
        internal static void ClearSolidCache()
        {
            lock (VerifyGate) { if (_verifySolids != null) _verifySolids.Clear(); }
        }

        /// <summary>
        /// Does the pair actually share any volume?
        ///
        /// CACHED — BOTH SIDES COME FROM <see cref="SolidsOf"/>, so this is only correct
        /// inside an open <see cref="SolidScope"/>. In one it is the cheap way to ask; a
        /// caller with no scope open gets fresh extraction for each side anyway and pays
        /// twice, so PairVerifier calls <see cref="Intersects"/> with
        /// <see cref="ExtractSolids"/> directly rather than coming through here. Kept
        /// first in the summary because it is the thing worth knowing before calling, and
        /// because the sentence lives on the method the caller is hovering.
        ///
        /// ElementIntersectsElementFilter is not answering that question. It matches
        /// against geometry that is not solid and cannot be built — a smoke detector
        /// here reports a 3,881 x 3,000 x 70 mm bounding box around a 470,185 mm3
        /// puck, the rest being its detection-coverage zone, and the filter counts a
        /// light fitting inside that zone as a clash. Measured on PRJ01: 197 of 2,165
        /// reported clashes, 9% of the register, had ZERO intersection volume, and 190
        /// of those involved fire alarm devices.
        ///
        /// So every hard hit is confirmed by boolean before it is reported. The cost
        /// is 1.5 s against a 72 s sweep, because it runs once per REPORTED clash
        /// rather than per candidate, and the solids are cached.
        /// </summary>
        /// <returns>
        /// true when they genuinely overlap, false when the volume is provably zero,
        /// and null when it could not be measured — which is NOT the same as "no
        /// clash" and must never be treated as one.
        /// </returns>
        internal static bool? OverlapIsReal(Element a, Element b)
        {
            return Intersects(SolidsOf(a), SolidsOf(b));
        }

        /// <summary>
        /// Host solids already pushed into link space, against a link element's own
        /// solids. Same question as <see cref="OverlapIsReal"/>, across documents — and
        /// CACHED on the link side for the same reason, so the same restriction applies:
        /// sweep only. Passing uncached host solids in does not make the call uncached.
        /// </summary>
        internal static bool? LinkOverlapIsReal(List<Solid> hostInLinkSpace, Element other)
        {
            return Intersects(hostInLinkSpace, SolidsOf(other));
        }

        /// <summary>
        /// Shared boolean test.
        ///
        /// A failed boolean returns null, not true. Autodesk documents the failure as
        /// "geometric inaccuracies in the solids, such as slightly misaligned faces or
        /// edges" and coincident edges — which is precisely the touching-but-not-
        /// overlapping case, the one whose answer is genuinely in doubt. Claiming a
        /// hard clash there asserts the very thing the operation failed to establish.
        /// Unverified says what is actually known, and nothing is dropped either way.
        ///
        /// One failing pair of solids does not abandon the element: the loop keeps
        /// going, because a later pair may still find real volume.
        /// </summary>
        // internal, not private, so the SelfTest harness can call the real method
        internal static bool? Intersects(List<Solid> sa, List<Solid> sb)
        {
            if (sa == null || sb == null || sa.Count == 0 || sb.Count == 0) return null;

            bool failed = false;
            foreach (var x in sa)
            {
                foreach (var y in sb)
                {
                    try
                    {
                        var inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                            x, y, BooleanOperationsType.Intersect);
                        if (inter != null && inter.Volume > MinOverlapVolume) return true;
                    }
                    catch { failed = true; }
                }
            }
            return failed ? (bool?)null : false;
        }
    }
}
