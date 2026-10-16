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
        }
    }
}
