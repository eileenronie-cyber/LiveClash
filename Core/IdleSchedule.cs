using System;

namespace LiveClash.Core
{
    /// <summary>One step of the idle tick (LiveClashApp.RunIdleTick).</summary>
    public enum IdleStep
    {
        FollowPath,
        Triggers,
        SettingsReset,
        WarmIndex,
        RefreshContexts,
        WarmCounts,
        ShowScope,
        VerifySlice,
        Drain
    }

    /// <summary>
    /// The ORDER of the idle tick, as data, so a check can pin it (round 8, DC-6). The order matters (delta review 3, D3-1): the settings
    /// retry runs when the settings are first READ, and the reset of the results must follow that swap BEFORE any step that re-tests or
    /// re-lists rows reads the settings, or the old rows are judged under the new rules in the same tick and recorded as fixes.
    /// Pure, so the rule is asserted headlessly.
    /// </summary>
    public static class IdleSchedule
    {
        /// <summary>The steps of one idle tick, in the order LiveClashApp.RunIdleTick runs them.</summary>
        public static readonly IdleStep[] Order =
        {
            IdleStep.FollowPath, IdleStep.Triggers, IdleStep.SettingsReset, IdleStep.WarmIndex, IdleStep.RefreshContexts,
            IdleStep.WarmCounts, IdleStep.ShowScope, IdleStep.VerifySlice, IdleStep.Drain
        };

        /// <summary>
        /// Does the step read <c>ClashSettings.Current</c> in a way that can swap the rules (the first read after a locked file is released)?
        /// FollowPath loads the new document's settings in Adopt and Triggers only reads them when a rebuild was requested, and the reset follows
        /// in the same tick before any row is re-tested; neither is counted.
        /// </summary>
        public static bool ReadsSettings(IdleStep step)
        {
            switch (step)
            {
                case IdleStep.WarmIndex:
                case IdleStep.RefreshContexts:
                case IdleStep.ShowScope:
                case IdleStep.VerifySlice:
                case IdleStep.Drain:
                    return true;
                default:
                    return false;
            }
        }
    }
}
