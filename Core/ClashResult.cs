using System;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    public enum ClashSeverity
    {
        /// <summary>Solids actually overlap.</summary>
        Hard,

        /// <summary>Within the clearance envelope but not physically touching.</summary>
        Clearance,

        /// <summary>
        /// Bounding boxes overlap, but Revit refused to run the exact intersection
        /// test on this pair — in-place families and some imports do this.
        ///
        /// Reported rather than dropped. A clash tool that silently skips the
        /// elements it could not check is worse than one that admits it: the user
        /// sees nothing and concludes there is nothing there. This says "look at
        /// this one yourself".
        /// </summary>
        Unverified
    }

    public class ClashResult
    {
        /// <summary>The element you were modelling (always lives in the host document).</summary>
        [RevitBound] public ElementId HostElementId { get; set; }
        public string HostElementName { get; set; }
        public string HostCategory { get; set; }

        /// <summary>The thing it hit.</summary>
        [RevitBound] public ElementId OtherElementId { get; set; }
        public string OtherElementName { get; set; }
        public string OtherCategory { get; set; }

        /// <summary>
        /// Null when the other element is in the host document, otherwise the
        /// RevitLinkInstance id — needed to select/zoom the linked element.
        /// </summary>
        [RevitBound] public ElementId LinkInstanceId { get; set; }

        /// <summary>"Current model" or the link's display name. Shown in the panel.</summary>
        public string ModelName { get; set; }

        public ClashSeverity Severity { get; set; }

        /// <summary>Approximate centre of the overlap, in host coordinates.</summary>
        [RevitBound] public XYZ Location { get; set; }

        public DateTime DetectedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>How the two meet: through, terminating, enclosed.</summary>
        public ClashGeometryKind GeometryKind { get; set; }

        /// <summary>Either side is a type the audit flags as provisional geometry.</summary>
        public bool Provisional { get; set; }

        /// <summary>
        /// This hit came from testing INSULATION, not bare geometry: the HOST element's own
        /// insulation, or the insulation of a pipe or duct it meets that is only a TARGET - in this
        /// model or in a link. Either way the bare geometry does not itself reach
        /// <see cref="OtherElementId"/>. Severity is
        /// still Hard or Clearance, exactly as a bare hit's would be; this is a display
        /// flag (a chip, a CSV column), not a third severity. When the bare geometry ALSO
        /// clashes, that result shares this row's <see cref="Key"/> and wins — see
        /// ClashEngine, which checks the bare pass first — so this is true on a
        /// row with no other way to have been found, or one whose bare result was only a
        /// Clearance or Unverified and whose insulation is Hard (see ClashEngine.MergeInsulationHits).
        /// </summary>
        public bool Insulated { get; set; }

        /// <summary>Stable identity for de-duplication across repeated checks.</summary>
        public string Key { get; set; }

        /// <summary>
        /// Which clash set reported this pair, for the panel's Set column. Stamped by the
        /// multi-set loops (RunFullCheckAllSets / CheckElementsAllSets). When more than one
        /// enabled set catches the same pair, the LOWEST-indexed set wins — both loops
        /// de-duplicate first-seen, so the name is deterministic rather than order-of-add.
        /// Empty on an unstamped single-set result.
        /// </summary>
        public string SetName { get; set; }

        /// <summary>
        /// For a same-document pair the id order is normalised, so A-hits-B and
        /// B-hits-A collapse into one entry — otherwise moving each side of a clash
        /// in turn would list the same problem twice.
        /// Cross-document pairs keep their order: the ids live in different
        /// documents and are not interchangeable.
        /// </summary>
        // Stryker disable all : [RevitBound] - takes ElementIds; the rule below is tested headless, this wrapper in Revit.
        [RevitBound]
        public static string MakeKey(ElementId a, ElementId linkInstance, ElementId b)
        {
            return MakeKey(IdUtil.Value(a), linkInstance == null ? (long?)null : IdUtil.Value(linkInstance), IdUtil.Value(b));
        }
        // Stryker restore all

        /// <summary>The rule itself, on raw id values: null <paramref name="link"/> means same document.</summary>
        internal static string MakeKey(long va, long? link, long vb)
        {
            if (link == null)
            {
                long lo = va < vb ? va : vb;
                long hi = va < vb ? vb : va;
                return lo + "|0|" + hi;
            }

            return va + "|" + link.Value + "|" + vb;
        }

        public override string ToString()
        {
            return HostElementName + "  →  " + OtherElementName + "  [" + ModelName + "]";
        }

        /// <summary>
        /// An independent copy. ClashResultStore.UpdateSeverity mutates a stored row's Severity
        /// IN PLACE (a hard hit backing off into the clearance envelope updates the same object
        /// live rather than being resolved-and-rediscovered) — so anything that keeps a
        /// point-in-time snapshot to compare against LATER, such as ClashChangeTracker.Baseline.
        /// Before, must hold a copy of its own or that later mutation silently rewrites its
        /// "before" picture too, and a real severity change compares as no change at all. Found
        /// reviewing the whole codebase, 2026-09-27.
        ///
        /// The four ElementId/XYZ-typed fields are skipped under LOGICTESTS_HEADLESS: this method
        /// is reached from ClashChangeTracker.NoteLinkChange, which the headless runner exercises
        /// with no real RevitAPI available, and merely copying one of those typed properties is
        /// enough to JIT-fault (see LiveClash.LogicTests.csproj). The headless tier never
        /// populates them on a ClashResult to begin with, so there is nothing to preserve there;
        /// the real in-Revit build keeps them.
        /// </summary>
        public ClashResult Clone()
        {
            var c = new ClashResult
            {
                HostElementName = HostElementName,
                HostCategory = HostCategory,
                OtherElementName = OtherElementName,
                OtherCategory = OtherCategory,
                ModelName = ModelName,
                Severity = Severity,
                DetectedUtc = DetectedUtc,
                GeometryKind = GeometryKind,
                Provisional = Provisional,
                Insulated = Insulated,
                Key = Key,
                SetName = SetName,
            };
#if !LOGICTESTS_HEADLESS
            c.HostElementId = HostElementId;
            c.OtherElementId = OtherElementId;
            c.LinkInstanceId = LinkInstanceId;
            c.Location = Location;
#endif
            return c;
        }
    }
}
