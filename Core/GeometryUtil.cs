using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    public static class GeometryUtil
    {
        private const double MinVolume = 1e-9;

        /// <summary>Minimum half-thickness given to any outline, in feet (~0.03 mm).</summary>
        private const double MinPad = 1e-4;

        /// <summary>
        /// All solids of an element, in that element's *document* coordinates.
        /// GeometryInstance.GetInstanceGeometry() already bakes the instance
        /// transform in, so nothing extra is applied for family instances.
        /// </summary>
        public static List<Solid> GetSolids(Element e, ViewDetailLevel lod = ViewDetailLevel.Medium)
        {
            var solids = new List<Solid>();
            if (e == null) return solids;

            var opt = new Options
            {
                DetailLevel = lod,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            GeometryElement ge = null;
            try { ge = e.get_Geometry(opt); } catch { return solids; }
            if (ge == null) return solids;

            Collect(ge, solids, e.Document);
            return solids;
        }

        /// <summary>
        /// The light a fitting emits, rather than the fitting itself.
        ///
        /// Revit hands back a lighting fixture's light source as an ordinary Solid, and
        /// it dwarfs the hardware: the external pole light in PRJ01 yields a
        /// 6.09 x 5.28 x 4.16 m cone of 31.78 m3 next to a lamp head of 0.004 m3 --
        /// nearly 8,000 times the real geometry. Nothing can collide with a beam of
        /// light, but ElementIntersectsElementFilter and a boolean intersect will both
        /// happily report a hit against it. That is how fire alarm devices several
        /// metres from a lamp post came to be reported as clashing with it, and why the
        /// volume verification did not catch them: the overlap it measured was real,
        /// the solid was not.
        ///
        /// Keyed on the built-in Light Source category rather than on the style name or
        /// the solid's size, so a genuinely large light fitting still clashes normally
        /// and a small light cone is still excluded.
        /// </summary>
        private static bool IsLightSource(Solid solid, Document doc)
        {
            if (doc == null) return false;
            try
            {
                var id = solid.GraphicsStyleId;
                if (id == null || id == ElementId.InvalidElementId) return false;

                var gs = doc.GetElement(id) as GraphicsStyle;
                if (gs == null || gs.GraphicsStyleCategory == null) return false;

                return IdUtil.Value(gs.GraphicsStyleCategory.Id)
                       == (long)BuiltInCategory.OST_LightingFixtureSource;
            }
            catch { return false; }
        }

        private static void Collect(GeometryElement ge, List<Solid> sink, Document doc)
        {
            foreach (GeometryObject go in ge)
            {
                var solid = go as Solid;
                if (solid != null)
                {
                    if (solid.Faces.Size > 0 && solid.Volume > MinVolume && !IsLightSource(solid, doc))
                        sink.Add(solid);
                    continue;
                }

                var inst = go as GeometryInstance;
                if (inst != null)
                {
                    GeometryElement instGeom = null;
                    try { instGeom = inst.GetInstanceGeometry(); } catch { }
                    if (instGeom != null) Collect(instGeom, sink, doc);
                    continue;
                }

                var nested = go as GeometryElement;
                if (nested != null) Collect(nested, sink, doc);
            }
        }

        /// <summary>
        /// Move an axis-aligned box through a transform and re-fit an axis-aligned
        /// Outline around the result. Transforming only Min and Max is wrong once
        /// the link is rotated, so all eight corners are used.
        /// </summary>
        public static Outline TransformToOutline(BoundingBoxXYZ box, Transform t, double inflate = 0.0)
        {
            // Fast path for the overwhelmingly common case: no link transform and an
            // identity box transform, which is what Element.get_BoundingBox(null)
            // returns. Min and Max are then already the extents, so the eight-corner
            // expansion — and the list it allocates — is pure overhead. Called once
            // per element per check, so the allocation is worth avoiding.
            if (t == null && (box.Transform == null || box.Transform.IsIdentity))
            {
                double pad0 = inflate + MinPad;
                return new Outline(
                    new XYZ(box.Min.X - pad0, box.Min.Y - pad0, box.Min.Z - pad0),
                    new XYZ(box.Max.X + pad0, box.Max.Y + pad0, box.Max.Z + pad0));
            }

            var pts = Corners(box);

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var p in pts)
            {
                var q = t == null ? p : t.OfPoint(p);
                if (q.X < minX) minX = q.X;
                if (q.Y < minY) minY = q.Y;
                if (q.Z < minZ) minZ = q.Z;
                if (q.X > maxX) maxX = q.X;
                if (q.Y > maxY) maxY = q.Y;
                if (q.Z > maxZ) maxZ = q.Z;
            }

            // Revit rejects a degenerate Outline, and a planar element (a floor edge
            // seen on, a zero-thickness sweep) can produce one. Always give it a
            // sliver of thickness.
            double pad = inflate + MinPad;

            var min = new XYZ(minX - pad, minY - pad, minZ - pad);
            var max = new XYZ(maxX + pad, maxY + pad, maxZ + pad);
            return new Outline(min, max);
        }

        /// <summary>
        /// Eight corners of a BoundingBoxXYZ, expressed in the coordinate system the
        /// box lives in. A BoundingBoxXYZ carries its own Transform, which is
        /// identity for Element.get_BoundingBox(null) but not always elsewhere.
        /// </summary>
        public static List<XYZ> Corners(BoundingBoxXYZ box)
        {
            var t = box.Transform;
            var mn = box.Min;
            var mx = box.Max;

            var local = new List<XYZ>
            {
                new XYZ(mn.X, mn.Y, mn.Z), new XYZ(mx.X, mn.Y, mn.Z),
                new XYZ(mx.X, mx.Y, mn.Z), new XYZ(mn.X, mx.Y, mn.Z),
                new XYZ(mn.X, mn.Y, mx.Z), new XYZ(mx.X, mn.Y, mx.Z),
                new XYZ(mx.X, mx.Y, mx.Z), new XYZ(mn.X, mx.Y, mx.Z)
            };

            if (t == null || t.IsIdentity) return local;

            var world = new List<XYZ>(8);
            foreach (var p in local) world.Add(t.OfPoint(p));
            return world;
        }

        /// <summary>
        /// Rough clash location: the centre of the overlap of the two bounding boxes,
        /// both expressed in host coordinates. Good enough to zoom to; this is not a
        /// true intersection-solid centroid.
        /// </summary>
        public static XYZ OverlapCentre(BoundingBoxXYZ a, Transform aToHost, BoundingBoxXYZ b, Transform bToHost)
        {
            var oa = TransformToOutline(a, aToHost);
            var ob = TransformToOutline(b, bToHost);

            double x = (System.Math.Max(oa.MinimumPoint.X, ob.MinimumPoint.X) + System.Math.Min(oa.MaximumPoint.X, ob.MaximumPoint.X)) * 0.5;
            double y = (System.Math.Max(oa.MinimumPoint.Y, ob.MinimumPoint.Y) + System.Math.Min(oa.MaximumPoint.Y, ob.MaximumPoint.Y)) * 0.5;
            double z = (System.Math.Max(oa.MinimumPoint.Z, ob.MinimumPoint.Z) + System.Math.Min(oa.MaximumPoint.Z, ob.MaximumPoint.Z)) * 0.5;

            return new XYZ(x, y, z);
        }
    }
}
