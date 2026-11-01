using System;

namespace LiveClash.Core
{
    /// <summary>
    /// The Revit-free half of <see cref="PairVerifier"/>: its verdicts, and the rules that turn what a
    /// test could and could not measure into one. Split out so the headless gate (coverage and
    /// mutation) can hold the rules to account; the geometry that feeds them stays in PairVerifier.cs.
    /// </summary>
    public static partial class PairVerifier
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

        /// <summary>
        /// The verdict of the link verifier's solid tests once neither a hard hit nor the clearance box
        /// confirmed the pair: <paramref name="tested"/> is whether any of the host's solids was tested
        /// against the linked element, <paramref name="anyFailed"/> whether any of them could not be
        /// (its transform into link space or its intersection test threw).
        /// </summary>
        /// <remarks>
        /// Resolved only when a test ran AND none failed. One failed solid beside one that tested clean
        /// is not "clear": the failed one may be the part that overlaps (review 2026-10-31, M10; the
        /// engine marks the same input Unverified, and ClashVerify.Intersects tracks the same flag).
        /// </remarks>
        internal static Verdict AfterSolidTests(bool tested, bool anyFailed)
        {
            return tested && !anyFailed ? Verdict.Resolved : Verdict.Unknown;
        }

        /// <summary>
        /// Should a listed pair that the verifier could not answer for leave the list, because its link
        /// instance is gone for good? Only an Unknown verdict asks (<paramref name="linkInstanceRemoved"/> is not
        /// called for any other), and only a removed instance drops: a link that is merely unloaded, or a lookup
        /// that cannot say, keeps the row.
        /// </summary>
        internal static bool DropsRow(Verdict verdict, Func<bool> linkInstanceRemoved)
        {
            return verdict == Verdict.Unknown && linkInstanceRemoved();
        }
    }
}
