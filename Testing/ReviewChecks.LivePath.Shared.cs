using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The Revit-free half of the live path's review checks (2026-10-31, work package F1): the rule that decides
    /// which document the live and idle paths may act on (H1). Written with the fix, since the rule did not exist
    /// before it; the in-Revit H1 checks in ReviewChecks.LivePath.cs are the ones seen red first.
    ///
    /// Shared, like every block in SharedChecks: the headless gate and the in-Revit run both run it.
    /// </summary>
    public static class ReviewChecksLivePathShared
    {
        private const string P = "review fix: ";

        public static void RunAll(Action<string, bool?> check)
        {
            check(P + "H1: the adopted project document is acted on",
                SessionGuard.ShouldActOn(false, false, @"C:\m\a.rvt", @"C:\m\a.rvt"));
            check(P + "H1: ...a family document never is, even one whose key matches",
                !SessionGuard.ShouldActOn(true, false, @"C:\m\a.rvt", @"C:\m\a.rvt")
                && !SessionGuard.ShouldActOn(true, false, "unsaved::Family1", null));
            check(P + "H1: ...nor a link's document",
                !SessionGuard.ShouldActOn(false, true, @"C:\m\a.rvt", @"C:\m\a.rvt"));
            check(P + "H1: ...nor another open project",
                !SessionGuard.ShouldActOn(false, false, @"C:\m\b.rvt", @"C:\m\a.rvt"));
            check(P + "H1: ...keys compare exactly, as the adopted key is stored",
                !SessionGuard.ShouldActOn(false, false, @"C:\m\A.rvt", @"C:\m\a.rvt"));
            check(P + "H1: before anything is adopted, a project document is acted on (live checking never goes quiet)",
                SessionGuard.ShouldActOn(false, false, @"C:\m\a.rvt", null) && SessionGuard.ShouldActOn(false, false, @"C:\m\a.rvt", ""));
            check(P + "H1: ...and a document with no key is not",
                !SessionGuard.ShouldActOn(false, false, "", null) && !SessionGuard.ShouldActOn(false, false, null, @"C:\m\a.rvt"));
            check(P + "F3: only a project can own the session, whatever its key (a family or a link never can)",
                SessionGuard.CanOwnSession(false, false) && !SessionGuard.CanOwnSession(true, false)
                && !SessionGuard.CanOwnSession(false, true) && !SessionGuard.CanOwnSession(true, true));
            check(P + "SP3: a project the user has active is adopted when it opens",
                SessionGuard.ShouldAdoptOnOpen(false, false, true));
            check(P + "SP3: ...a project opened in the background (no view, not active) is not",
                !SessionGuard.ShouldAdoptOnOpen(false, false, false));
            check(P + "SP3: ...and a family or a link never is, active or not",
                !SessionGuard.ShouldAdoptOnOpen(true, false, true) && !SessionGuard.ShouldAdoptOnOpen(false, true, true)
                && !SessionGuard.ShouldAdoptOnOpen(true, true, false));
        }
    }
}
