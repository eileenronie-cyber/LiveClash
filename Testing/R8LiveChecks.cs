using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8 (builder "live"): the Revit-free checks. Predictions: tests\evidence\liveclash\r8-live\PREDICTIONS.md (4bbddb8), committed
    /// before any of this. Shared, like every block in SharedChecks: the headless gate and the in-Revit run both run it.
    /// The in-Revit half is in ReviewChecks.LivePath.R8.cs and ReviewChecks.R8Live.cs.
    /// </summary>
    public static partial class R8LiveChecks
    {
        private const string P = "r8 live: ";

        public static void RunAll(Action<string, bool?> check)
        {
            FollowPathChange(check);
            StaleInstanceIsNotBack(check);
            IdleOrder(check);
            ResetSites(check);
            TimeBoxedSlice(check);
            TrackerNamesADeletedLinkAsRemoved(check);   // item 9 probe P9-2
            BacklogModel(check);   // delta review L-3
            WarnThrottled(check);   // delta review I-2
            NoMojibake(check);   // delta review I-1
            SameObjectByEquals(check);   // second review R2-3
            VanishedElementRule(check);   // M-2 / P9-1
            CommittedDeletionMemory(check);   // delta check D-3, D-6
            CloseTrackerRules(check);   // P9-3
            ReversePolicyAndEncResult(check);   // review L3, L4
            ModelScopeRules(check);   // where a model-bound check fails and where it skips by name
            NoMojibakeNeedsTheTree(check);   // ENC-2, ENC-3
        }

        // ================================================================== item 2: D3-3, a stale instance is not "back"

        private static void StaleInstanceIsNotBack(Action<string, bool?> check)
        {
            check(P + "D33-1 [red] a link instance that is no longer a valid object (a stale deleted one) is not back", !DroppedLinkRows.CountsAsBack(true, false));
            check(P + "D33-2 [green] a valid link instance is back", DroppedLinkRows.CountsAsBack(true, true));
            check(P + "D33-3 [green] an element that is not a link instance is not back, valid or not",
                !DroppedLinkRows.CountsAsBack(false, true) && !DroppedLinkRows.CountsAsBack(false, false));

            // The holder with a lookup built on the rule: a stale instance leaves the pairs held (nothing re-listed), a valid one returns them once.
            const string doc = "zz-r8-d33";
            DroppedLinkRows.Forget(doc);
            try
            {
                var row = new ClashResult { Key = "r8-d33-a", ModelName = "r8", Severity = ClashSeverity.Hard };
                DroppedLinkRows.Hold(doc, 77, new[] { row });
                var staleBack = DroppedLinkRows.TakeBack(doc, id => DroppedLinkRows.CountsAsBack(true, false));
                int heldAfterStale = DroppedLinkRows.Count(doc);
                check(P + "D33-4 [red] TakeBack on a stale instance lists nothing and keeps the pair held (listed " + staleBack.Count + ", held " + heldAfterStale + ")",
                    staleBack.Count == 0 && heldAfterStale == 1);

                DroppedLinkRows.Forget(doc);   // independent of D33-4: at the red stub its TakeBack already consumed the pair
                DroppedLinkRows.Hold(doc, 77, new[] { row });
                var validBack = DroppedLinkRows.TakeBack(doc, id => DroppedLinkRows.CountsAsBack(true, true));
                check(P + "D33-5 [green] ...and once the instance is valid again the pair comes back once, Unverified (listed " + validBack.Count + ", held " + DroppedLinkRows.Count(doc) + ")",
                    validBack.Count == 1 && validBack[0].Severity == ClashSeverity.Unverified && DroppedLinkRows.Count(doc) == 0);
            }
            finally { DroppedLinkRows.Forget(doc); }
        }

        // ================================================================== item 1: fix B, the session follows the document

        private const string Old = @"C:\central\model.rvt";
        private const string New = @"C:\local\model_detached.rvt";

        private static void FollowPathChange(Action<string, bool?> check)
        {
            check(P + "FB-1 [red] the active adopted document whose path moved is followed",
                SessionGuard.ShouldFollowPathChange(false, false, true, true, false, New, Old));

            // The same document cannot be proved (no object or GUID match) but nothing holds the adopted key any more: it IS gone.
            check(P + "FB-2 [red] ...or the adopted key is no longer held by any open document (identity unknown)",
                SessionGuard.ShouldFollowPathChange(false, false, true, false, false, New, Old));

            // SP3 and the ordinary view switch: another project, the adopted key still held by the document that has it.
            check(P + "FB-3 [green] SP3: another document that is not the adopted one, adopted key still held, is not followed",
                !SessionGuard.ShouldFollowPathChange(false, false, true, false, true, New, Old));

            check(P + "FB-4 [green] SP3: a document that is not active (a background open) is not followed, whatever else is true",
                !SessionGuard.ShouldFollowPathChange(false, false, false, true, false, New, Old)
                && !SessionGuard.ShouldFollowPathChange(false, false, false, false, false, New, Old)
                && !SessionGuard.ShouldFollowPathChange(false, false, false, true, true, New, Old));

            check(P + "FB-5 [green] a family, a link, equal keys, nothing adopted, an empty key: not followed",
                !SessionGuard.ShouldFollowPathChange(true, false, true, true, false, New, Old)
                && !SessionGuard.ShouldFollowPathChange(false, true, true, true, false, New, Old)
                && !SessionGuard.ShouldFollowPathChange(false, false, true, true, false, Old, Old)
                && !SessionGuard.ShouldFollowPathChange(false, false, true, true, false, New, null)
                && !SessionGuard.ShouldFollowPathChange(false, false, true, true, false, New, "")
                && !SessionGuard.ShouldFollowPathChange(false, false, true, true, false, "", Old)
                && !SessionGuard.ShouldFollowPathChange(false, false, true, true, false, null, Old));

            var rnd = new Random(20261102);
            string[] keys = { null, "", @"C:\m\a.rvt", @"C:\m\A.rvt", @"C:\m\b.rvt", "unsaved::P1" };
            int wrong = 0;
            string first = null;
            for (int i = 0; i < 500; i++)
            {
                bool fam = rnd.Next(4) == 0, link = rnd.Next(4) == 0, active = rnd.Next(3) != 0, same = rnd.Next(2) == 0, held = rnd.Next(2) == 0;
                string dk = keys[rnd.Next(keys.Length)], ak = keys[rnd.Next(keys.Length)];
                bool expected = !fam && !link && active
                                && !string.IsNullOrEmpty(dk) && !string.IsNullOrEmpty(ak)
                                && !string.Equals(dk, ak, StringComparison.Ordinal)
                                && (same || !held);
                if (SessionGuard.ShouldFollowPathChange(fam, link, active, same, held, dk, ak) != expected)
                {
                    wrong++;
                    if (first == null) first = fam + "/" + link + "/" + active + "/" + same + "/" + held + "/" + dk + "/" + ak;
                }
            }
            check(P + "FB-6 [red] property: 500 random facts follow the rule - a project, active, two different non-empty keys, and (the adopted document or its key held by nobody)"
                  + (wrong == 0 ? " (seed 20261102)" : " (" + wrong + " wrong, first " + first + ")"), wrong == 0);
        }
    }
}
