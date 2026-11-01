using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8, builder "live", seventh part (the round 9-11 review; predictions: tests\evidence\liveclash\r8-live\PREDICTIONS-7.md, 1427adc):
    /// the rule for skipping the reverse check (L3) and what an ENC check does without a source tree (L4). Revit-free.
    /// </summary>
    public static partial class R8LiveChecks
    {
#if LOGICTESTS_HEADLESS
        private const bool Headless = true;
#else
        private const bool Headless = false;
#endif

        /// <summary>
        /// The result of a source-tree check whose verdict may be "no tree" (null). Headless the tree is always there, so no tree is a FAILURE (a runner
        /// started from a copied folder must not lose the scan silently); in Revit the base directory is the Revit folder, so it is a named SKIP.
        /// </summary>
        private static bool? EncResult(bool? verdict, bool headless)
        {
            if (verdict == null) return headless ? (bool?)false : null;
            return verdict;
        }

        private static void ReversePolicyAndEncResult(Action<string, bool?> check)
        {
            int[] a = { 1, 2 }, b = { 2, 1 }, c = { 1, 2, 3 };
            check(P + "RP-1 [red] equal source and target category sets with empty filters: the reverse check can find nothing", ReverseCheckPolicy.CanFindNothing(a, b, true, true));
            check(P + "RP-2 [red] a source category that is not a target: it can find something", !ReverseCheckPolicy.CanFindNothing(c, a, true, true));
            check(P + "RP-3 [red] a target category that is not a source: it can find something", !ReverseCheckPolicy.CanFindNothing(a, c, true, true));
            check(P + "RP-4 [red] equal sets but a source filter: it can find something", !ReverseCheckPolicy.CanFindNothing(a, b, false, true));
            check(P + "RP-5 [red] equal sets but a target filter: it can find something", !ReverseCheckPolicy.CanFindNothing(a, b, true, false));
            check(P + "RP-6 [green] empty sets: nothing to decide, so not skipped", !ReverseCheckPolicy.CanFindNothing(System.Array.Empty<int>(), System.Array.Empty<int>(), true, true)
                && !ReverseCheckPolicy.CanFindNothing(null, a, true, true));

            check(P + "ENC-5 [red] headless, a missing source tree is a FAILURE, not a skip", EncResult(null, true) == false);
            check(P + "ENC-6 [green] in Revit a missing tree is a named skip, and a real verdict passes through either way",
                EncResult(null, false) == null && EncResult(true, true) == true && EncResult(false, true) == false && EncResult(true, false) == true && EncResult(false, false) == false);
        }
    }
}
