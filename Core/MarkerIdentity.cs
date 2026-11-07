using System;

namespace LiveClash.Core
{
    /// <summary>
    /// What makes an element one of LiveClash's markers (spec 5.1, RF12): its DirectShape identity AND its record. The
    /// category, a parameter value (a hand-typed "LC-123"), a name, or a record alone never make one. The engine
    /// excludes markers by this identity, never by category alone. Pure.
    /// </summary>
    public static class MarkerIdentity
    {
        /// <summary>
        /// True for any DirectShape made by LiveClash (its ApplicationId), record or not. What the engine uses to leave markers
        /// out of every clash check: wider than <see cref="IsMarker"/> on purpose, since an element LiveClash made is never a
        /// model element to check.
        /// </summary>
        public static bool IsOurs(string applicationId)
        {
            return string.Equals(applicationId, MarkerFormats.ApplicationId, StringComparison.Ordinal);
        }

        public static bool IsMarker(string applicationId, string applicationDataId, bool hasRecord)
        {
            return hasRecord
                && string.Equals(applicationId, MarkerFormats.ApplicationId, StringComparison.Ordinal)
                && string.Equals(applicationDataId, MarkerFormats.ApplicationDataId, StringComparison.Ordinal);
        }
    }
}
