using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Builds a section box, in HOST coordinates, that frames a clash — so a pair whose
    /// elements cannot be selected from the host (both in links, or a link element that
    /// Selection Box would leave out) can still be viewed in 3D. Geometry only: a section
    /// box is a view property, not a selection, so it works regardless of what can be
    /// picked, and link geometry renders in the host 3D view, so both sides appear.
    /// </summary>
    public static class ClashFocus
    {
        // A long run would otherwise blow the box up to the size of the model, and a tiny
        // fitting would give a box too small to orient in. Clamp each element's reach
        // around the clash point, pad the result, and enforce a minimum. Feet.
        private const double MaxHalfFeet = 20.0;
        private const double PadFeet = 2.0;
        private const double MinHalfFeet = 2.0;

        /// <summary>
        /// A host-coordinate box framing every given clash: centred on each clash location
        /// and expanded to include the nearby geometry of both sides (link elements pushed
        /// into host space via their link transform), clamped so one long run cannot
        /// dominate. Null when nothing measurable is found.
        /// </summary>
        public static BoundingBoxXYZ BuildFocusBox(Document doc, IEnumerable<ClashResult> targets)
        {
            if (doc == null || !doc.IsValidObject || targets == null) return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            foreach (var r in targets)
            {
                if (r == null) continue;

                // Where the two meet, in host coordinates. The engine already stores this.
                XYZ centre = r.Location;

                var hostBox = SafeBox(doc, r.HostElementId, Transform.Identity);

                // The other side may live in a link; push its box into host space.
                Transform toHost = Transform.Identity;
                Document otherDoc = doc;
                if (r.LinkInstanceId != null)
                {
                    var link = doc.GetElement(r.LinkInstanceId) as RevitLinkInstance;
                    if (link != null)
                    {
                        try { toHost = link.GetTotalTransform() ?? Transform.Identity; } catch { toHost = Transform.Identity; }
                        try { otherDoc = link.GetLinkDocument() ?? doc; } catch { otherDoc = doc; }
                    }
                }
                var otherBox = SafeBox(otherDoc, r.OtherElementId, toHost);

                if (centre == null) centre = MidOf(hostBox) ?? MidOf(otherBox);
                if (centre == null) continue;   // nothing to anchor on

                // Always include the clash point itself.
                Expand(centre, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                any = true;

                // Include each element's extents, but only the part near the clash, so a
                // 40 m pipe frames the intersection rather than the whole run.
                ExpandClamped(hostBox, centre, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                ExpandClamped(otherBox, centre, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            }

            if (!any) return null;

            // Pad, then enforce a minimum half-size on each axis so the box is never
            // degenerate (a coincident-point clash would otherwise be a zero-width box).
            minX -= PadFeet; minY -= PadFeet; minZ -= PadFeet;
            maxX += PadFeet; maxY += PadFeet; maxZ += PadFeet;
            EnsureSpan(ref minX, ref maxX);
            EnsureSpan(ref minY, ref maxY);
            EnsureSpan(ref minZ, ref maxZ);

            var bb = new BoundingBoxXYZ();
            bb.Min = new XYZ(minX, minY, minZ);
            bb.Max = new XYZ(maxX, maxY, maxZ);
            return bb;
        }

        /// <summary>
        /// An element's extents as an axis-aligned box in HOST coordinates.
        ///
        /// Goes through <see cref="GeometryUtil.Corners"/> rather than reading Min/Max,
        /// because a BoundingBoxXYZ carries its OWN Transform. It is identity for
        /// Element.get_BoundingBox(null) in the common case but not always, and reading
        /// Min/Max raw then treats box-local coordinates as host coordinates — which put
        /// the section box somewhere other than the clash. The rest of the engine has
        /// always known this (GeometryUtil.TransformToOutline tests box.Transform for the
        /// same reason); this was the one place that did not.
        ///
        /// Corners applies the box's own transform; <paramref name="toHost"/> then carries
        /// a link element into host space. Both must be applied, in that order.
        /// </summary>
        private static BoundingBoxXYZ SafeBox(Document d, ElementId id, Transform toHost)
        {
            try
            {
                if (d == null || !d.IsValidObject || id == null || id == ElementId.InvalidElementId) return null;
                var el = d.GetElement(id);
                if (el == null || !el.IsValidObject) return null;
                var b = el.get_BoundingBox(null);
                if (b == null) return null;

                double nx = double.MaxValue, ny = double.MaxValue, nz = double.MaxValue;
                double xx = double.MinValue, xy = double.MinValue, xz = double.MinValue;
                foreach (var c in GeometryUtil.Corners(b))
                {
                    var p = (toHost == null || toHost.IsIdentity) ? c : toHost.OfPoint(c);
                    if (p.X < nx) nx = p.X; if (p.X > xx) xx = p.X;
                    if (p.Y < ny) ny = p.Y; if (p.Y > xy) xy = p.Y;
                    if (p.Z < nz) nz = p.Z; if (p.Z > xz) xz = p.Z;
                }
                if (nx > xx) return null;   // no corners

                var r = new BoundingBoxXYZ();
                r.Min = new XYZ(nx, ny, nz);
                r.Max = new XYZ(xx, xy, xz);
                return r;
            }
            catch { return null; }
        }

        private static XYZ MidOf(BoundingBoxXYZ b)
        {
            return b == null ? null : (b.Min + b.Max) * 0.5;
        }

        private static void Expand(XYZ p,
            ref double minX, ref double minY, ref double minZ,
            ref double maxX, ref double maxY, ref double maxZ)
        {
            if (p == null) return;
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }

        /// <summary>Fold in a box, but only the portion within MaxHalfFeet of the centre.</summary>
        private static void ExpandClamped(BoundingBoxXYZ b, XYZ centre,
            ref double minX, ref double minY, ref double minZ,
            ref double maxX, ref double maxY, ref double maxZ)
        {
            if (b == null || centre == null) return;
            var lo = new XYZ(
                Math.Max(b.Min.X, centre.X - MaxHalfFeet),
                Math.Max(b.Min.Y, centre.Y - MaxHalfFeet),
                Math.Max(b.Min.Z, centre.Z - MaxHalfFeet));
            var hi = new XYZ(
                Math.Min(b.Max.X, centre.X + MaxHalfFeet),
                Math.Min(b.Max.Y, centre.Y + MaxHalfFeet),
                Math.Min(b.Max.Z, centre.Z + MaxHalfFeet));
            // A box entirely outside the clamp region collapses (lo > hi); skip it then.
            if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) return;
            Expand(lo, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            Expand(hi, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
        }

        private static void EnsureSpan(ref double lo, ref double hi)
        {
            var mid = (lo + hi) * 0.5;
            if (hi - lo < 2 * MinHalfFeet) { lo = mid - MinHalfFeet; hi = mid + MinHalfFeet; }
        }
    }
}
