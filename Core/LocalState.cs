using System;

namespace LiveClash.Core
{
    /// <summary>
    /// The folder under which LiveClash keeps its per-user files (the settings, status and audit-label folders): the user's
    /// local application data. One place, so a dev job can point a test Revit at a scratch folder (review 2026-10-31,
    /// round 2 of the delta review, DM2 / rule E6: the in-Revit tests used to read and write the user's real ledgers).
    ///
    /// Debug builds only (<c>DEV</c>): <c>RootForTests</c> replaces the root. A Release build has no such member at all,
    /// which the release smoke test checks by scanning the Release DLL for the name. The log is deliberately NOT moved: the release
    /// smoke test and the log checks read the real one, and the real-folder guard leaves it out.
    /// </summary>
    public static class LocalState
    {

        /// <summary>The root that the "LiveClash" folder lives under.</summary>
        public static string Root
        {
            get
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }
    }
}
