using System;

namespace LiveClash.Testing
{
    /// <summary>
    /// Every Revit-free assertion, in one list.
    ///
    /// THE ONE LIST, and that is the whole job. Both tiers run these — the headless CI
    /// gate on a machine with no Revit, and the in-Revit checks — and each used to name
    /// the blocks itself. They drifted: the in-Revit run never had AuditDropFormat, so a
    /// check could pass in CI and be flatly absent in Revit with nothing in either total
    /// to say so. Same failure as the filter keys having two readers, one level up.
    ///
    /// So: add a block here, and both tiers get it. Neither caller may name blocks again.
    /// </summary>
    public static class SharedChecks
    {
        public static void RunAll(Action<string, bool?> check)
        {
            FilterLogicChecks.RunAll(check);
            FilterMatchChecks.RunAll(check);
            StatusReplayChecks.RunAll(check);
            ResolvedReplayChecks.RunAll(check);
            LinkWatchChecks.RunAll(check);
            ClashSearchChecks.RunAll(check);
            ClashChangeChecks.RunAll(check);
            ModelIdentityChecks.RunAll(check);
            CsvStampChecks.RunAll(check);
            ResultFormatChecks.RunAll(check);
            LogChecks.RunAll(check);
            FuzzChecks.RunAll(check);
            EdgeCaseChecks.RunAll(check);
            ReviewChecksLivePathShared.RunAll(check);
            FullCheckReviewLogicChecks.RunAll(check);   // review 2026-10-31, F2 (M2, M10, LOW-8)
            LedgerReviewChecks.RunAll(check);
            LedgerReviewR2Checks.RunAll(check);
            LedgerReviewR3Checks.RunAll(check);
            LedgerReviewR4Checks.RunAll(check);
            LedgerReviewR8FilesChecks.RunAll(check);   // round 8, files builder: D3-4, D3-2 b/c, SP7, DL-f
            UiOutcomeChecks.RunAll(check);
            SessionHandlerLogicChecks.RunAll(check);   // second pass 2026-10-31, SP1
            R8LiveChecks.RunAll(check);   // round 8, builder "live" (tests\evidence\liveclash\r8-live)
            GroupLedgerChecks.RunAll(check);   // the overnight ModificationForbiddenException: groups a check leaves open
        }
    }
}
