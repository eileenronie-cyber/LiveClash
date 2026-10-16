// The values alone, in a file of their own: ClashResult carries one, and ClashResult is a plain data class that the headless
// tests link in. The classifier that produces them (ClashGeometryKind.cs) calls into Revit and the engine and cannot be.
namespace LiveClash.Core
{
    /// <summary>
    /// HOW two things intersect, which is usually a better guide to what to do about
    /// it than what they are.
    /// </summary>
    public enum ClashGeometryKind
    {
        /// <summary>Could not be worked out, or neither side is a routed service.</summary>
        Unknown = 0,

        /// <summary>
        /// A service ENDS inside the other element AND the two could plausibly have
        /// been joined — a connection drawn to the equipment body rather than to a
        /// connector. A family-content gap, not a routing fault. Where they could NOT
        /// have been joined, see EndsInside.
        /// </summary>
        Termination = 1,

        /// <summary>
        /// A service crosses clean through and comes out the other side. Either a
        /// designed penetration or a real reroute; nothing else looks like this.
        /// </summary>
        Through = 2,

        /// <summary>The service lies entirely inside the other element.</summary>
        Enclosed = 3,

        /// <summary>
        /// The service ends inside something it could never have connected to — a
        /// duct dying inside a busway tap-off, say. Geometrically the same test as
        /// Termination, but with none of the excuse: this is simply a clash.
        /// </summary>
        EndsInside = 4
    }
}
