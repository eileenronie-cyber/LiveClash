using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Classifies a clash by how the two elements meet.
    ///
    /// On PRJ01 this split 525 pipe-vs-equipment pairs into 353 services threading
    /// their own slab openings, 136 terminating in plant that carries no connector,
    /// and 36 genuine pass-throughs — a 93% reduction in what a person has to look
    /// at, from geometry alone, with nothing to configure.
    ///
    /// THREE STAGES, each one paying for the next, because every cheaper proxy was
    /// caught lying:
    ///
    ///   1. The bounding box. Rejects almost everything for almost nothing. But
    ///      Revit includes a lighting fixture's light-source geometry in it, and an
    ///      exit-sign blade 0.30 m long reports a box 2.65 m long.
    ///   2. The extent of the actual SOLIDS. Fixed the exit sign, and moved 359
    ///      results out of "enclosed" where a loose box had swallowed both ends of a
    ///      pipe stub. Still a box, though — and an external pole light's solids span
    ///      six metres while filling 16% of that space, so a tray passing through
    ///      empty air between pole and lamp head still read as ending inside it.
    ///   3. The solids THEMSELVES, by boolean. Exact. Runs only where stage 2 is a
    ///      poor proxy for its own solids, which is the only place it can be wrong.
    ///
    /// The whole thing runs once per REPORTED CLASH, never per candidate, so even
    /// stage 3 is a rounding error beside the narrow phase that produced the clash.
    /// </summary>
    public static class ClashGeometry
    {
        /// <summary>
        /// Below this, the solids' bounding box is a bad description of the solids and
        /// stage 3 is worth its cost. A chiller fills its box and never gets here; a
        /// pole light at 16% and an exit sign at 0.29% always do.
        /// </summary>
        private const double TrustExtentAbove = 0.5;

        /// <summary>Half-size of the probe cube, in feet. ~6 mm.</summary>
        private const double ProbeHalf = 0.02;

        private class Shape
        {
            public XYZ Min, Max;
            public bool NeedsSolidTest;
            public List<Solid> Solids;      // only kept when NeedsSolidTest
        }

        private static readonly object Gate = new object();
        private static Dictionary<string, Shape> _shapes = new Dictionary<string, Shape>();

        public static void ClearCache()
        {
            lock (Gate) _shapes = new Dictionary<string, Shape>();
        }

        public static ClashGeometryKind Classify(Element service, Element other, Transform otherToHost)
        {
            try
            {
                var lc = service == null ? null : service.Location as LocationCurve;
                if (lc == null) return ClashGeometryKind.Unknown;

                Curve c;
                try { c = lc.Curve; } catch { return ClashGeometryKind.Unknown; }
                if (c == null) return ClashGeometryKind.Unknown;

                BoundingBoxXYZ bb;
                try { bb = other == null ? null : other.get_BoundingBox(null); } catch { return ClashGeometryKind.Unknown; }
                if (bb == null) return ClashGeometryKind.Unknown;

                XYZ a, b;
                try { a = c.GetEndPoint(0); b = c.GetEndPoint(1); } catch { return ClashGeometryKind.Unknown; }

                // --- stage 1: the loose box.
                XYZ min = bb.Min, max = bb.Max;
                if (bb.Transform != null && !bb.Transform.IsIdentity)
                    Corners(bb.Transform, bb.Min, bb.Max, out min, out max);
                if (otherToHost != null && !otherToHost.IsIdentity)
                    Corners(otherToHost, min, max, out min, out max);

                bool ea = Inside(a, min, max);
                bool eb = Inside(b, min, max);
                if (!ea && !eb) return ClashGeometryKind.Through;

                var shape = ShapeOf(other);
                if (shape != null)
                {
                    // --- stage 2: the solids' extent.
                    XYZ tmin = shape.Min, tmax = shape.Max;
                    if (otherToHost != null && !otherToHost.IsIdentity)
                        Corners(otherToHost, tmin, tmax, out tmin, out tmax);

                    ea = Inside(a, tmin, tmax);
                    eb = Inside(b, tmin, tmax);

                    // --- stage 3: the solids themselves, where the extent lies.
                    if (shape.NeedsSolidTest && (ea || eb) && shape.Solids != null)
                    {
                        // The solids live in the other element's own coordinates, so
                        // the point travels to them rather than the other way about.
                        var toOther = (otherToHost != null && !otherToHost.IsIdentity)
                            ? otherToHost.Inverse : null;

                        if (ea) ea = PointInSolids(toOther == null ? a : toOther.OfPoint(a), shape.Solids);
                        if (eb) eb = PointInSolids(toOther == null ? b : toOther.OfPoint(b), shape.Solids);
                    }
                }

                if (ea && eb) return ClashGeometryKind.Enclosed;
                if (ea || eb)
                    return CouldConnect(service, other)
                        ? ClashGeometryKind.Termination
                        : ClashGeometryKind.EndsInside;

                // Something said maybe and the exact test said no: it passes through.
                return ClashGeometryKind.Through;
            }
            catch { return ClashGeometryKind.Unknown; }
        }

        /// <summary>
        /// Which of the pair is the routed service. Both can be — pipe against pipe —
        /// in which case the classification is not meaningful and Unknown is honest.
        /// </summary>
        public static ClashGeometryKind ClassifyPair(Element host, Element other, Transform otherToHost)
        {
            bool hostIsCurve = host is MEPCurve;
            bool otherIsCurve = other is MEPCurve;

            if (hostIsCurve && !otherIsCurve) return Classify(host, other, otherToHost);
            if (otherIsCurve && !hostIsCurve)
            {
                var inverse = otherToHost == null ? null : otherToHost.Inverse;
                return Classify(other, host, inverse);
            }
            return ClashGeometryKind.Unknown;
        }

        /// <summary>
        /// Is the point genuinely inside the metal? A small cube at the point,
        /// boolean-intersected with each solid; any non-zero result means yes.
        ///
        /// Booleans throw on geometry Revit dislikes. A failure returns TRUE — the
        /// extent already said the point was inside, and falling back to the cheaper
        /// answer is better than inventing a pass-through from an exception.
        /// </summary>
        private static bool PointInSolids(XYZ p, List<Solid> solids)
        {
            Solid probe;
            try { probe = Probe(p); } catch { return true; }
            if (probe == null) return true;

            foreach (var s in solids)
            {
                try
                {
                    var hit = BooleanOperationsUtils.ExecuteBooleanOperation(
                        probe, s, BooleanOperationsType.Intersect);
                    if (hit != null && hit.Volume > 1e-12) return true;
                }
                catch { return true; }
            }
            return false;
        }

        private static Solid Probe(XYZ p)
        {
            double h = ProbeHalf;
            var p0 = new XYZ(p.X - h, p.Y - h, p.Z - h);
            var p1 = new XYZ(p.X + h, p.Y - h, p.Z - h);
            var p2 = new XYZ(p.X + h, p.Y + h, p.Z - h);
            var p3 = new XYZ(p.X - h, p.Y + h, p.Z - h);

            var loop = CurveLoop.Create(new List<Curve>
            {
                Line.CreateBound(p0, p1),
                Line.CreateBound(p1, p2),
                Line.CreateBound(p2, p3),
                Line.CreateBound(p3, p0)
            });

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop }, XYZ.BasisZ, 2 * h);
        }

        /// <summary>
        /// The element's solids: their extent, and whether that extent is trustworthy.
        /// Cached, because the same equipment is hit by many services.
        /// </summary>
        private static Shape ShapeOf(Element e)
        {
            if (e == null) return null;

            // Keyed by DOCUMENT AND id, not id alone. ClassifyPair is called with link
            // elements as well as host ones (ClashEngine.CheckAgainstLink), and linked
            // documents number their elements independently, so a host element and a
            // link element routinely share an id value. With a bare id key the first
            // one cached was handed back for the other, and "how they meet" — through,
            // ends in, inside — was decided from the wrong element's geometry.
            //
            // ClashVerify._verifySolids has carried this exact guard, with this exact
            // reasoning, since it was found there; this cache simply never got it.
            string key;
            try { key = ClashEngine.DocKey(e.Document) + "#" + IdUtil.Value(e.Id); }
            catch { return null; }

            lock (Gate)
            {
                Shape hit;
                if (_shapes.TryGetValue(key, out hit)) return hit;
            }

            Shape result = null;
            try
            {
                double nx = double.MaxValue, ny = double.MaxValue, nz = double.MaxValue;
                double xx = double.MinValue, xy = double.MinValue, xz = double.MinValue;
                double volume = 0;
                var solids = new List<Solid>();

                // FINE, with Medium as the fallback — the same rule, for the same reason,
                // as ClashVerify.SolidsOf. Reading at Medium alone meant ShapeOf returned
                // null for anything whose geometry only exists at Fine, and a null shape
                // skips stages 2 and 3 entirely: the classification falls back to the
                // loose bounding box that stage 1 already showed to be unreliable.
                //
                // Measured on Snowdon Towers, 2026-08-30: of the 42 elements ShapeOf is
                // actually asked about across the 83 reported clashes, 26 — 62% — have NO
                // solids at Medium and do at Fine. Twenty-five of them are Pipe Fittings.
                // So the majority of classifications were being decided by the box the
                // three-stage design exists to get away from.
                var geo = ReadGeometry(e, ViewDetailLevel.Fine);
                if (geo != null)
                    Walk(geo, solids, ref nx, ref ny, ref nz, ref xx, ref xy, ref xz, ref volume, 0);

                if (solids.Count == 0)
                {
                    geo = ReadGeometry(e, ViewDetailLevel.Medium);
                    if (geo != null)
                        Walk(geo, solids, ref nx, ref ny, ref nz, ref xx, ref xy, ref xz, ref volume, 0);
                }

                if (solids.Count > 0)
                {
                    double boxVol = (xx - nx) * (xy - ny) * (xz - nz);
                    double fill = boxVol > 1e-9 ? volume / boxVol : 1.0;

                    result = new Shape
                    {
                        Min = new XYZ(nx, ny, nz),
                        Max = new XYZ(xx, xy, xz),
                        NeedsSolidTest = fill < TrustExtentAbove
                    };

                    // Hold the solids only where stage 3 will actually use them —
                    // keeping every solid in the model would be a memory problem.
                    if (result.NeedsSolidTest) result.Solids = solids;
                }
            }
            catch { result = null; }

            lock (Gate) _shapes[key] = result;
            return result;
        }

        private static GeometryElement ReadGeometry(Element e, ViewDetailLevel level)
        {
            try
            {
                return e.get_Geometry(new Options { ComputeReferences = false, DetailLevel = level });
            }
            catch { return null; }
        }

        private static void Walk(GeometryElement geo, List<Solid> solids,
            ref double nx, ref double ny, ref double nz,
            ref double xx, ref double xy, ref double xz, ref double volume, int depth)
        {
            if (geo == null || depth > 4) return;      // nested families are not infinite

            foreach (var o in geo)
            {
                var s = o as Solid;
                if (s != null && s.Volume > 1e-9)
                {
                    solids.Add(s);
                    volume += s.Volume;
                    foreach (Edge ed in s.Edges)
                    {
                        foreach (var p in ed.Tessellate())
                        {
                            if (p.X < nx) nx = p.X;
                            if (p.Y < ny) ny = p.Y;
                            if (p.Z < nz) nz = p.Z;
                            if (p.X > xx) xx = p.X;
                            if (p.Y > xy) xy = p.Y;
                            if (p.Z > xz) xz = p.Z;
                        }
                    }
                    continue;
                }

                var gi = o as GeometryInstance;
                if (gi != null)
                {
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { }
                    Walk(inner, solids, ref nx, ref ny, ref nz, ref xx, ref xy, ref xz, ref volume, depth + 1);
                }
            }
        }

        /// <summary>
        /// Could these two ever have been joined?
        ///
        /// "Ends inside" means a connection modelled without a connector ONLY when
        /// the pair is plausible. On PRJ01, open duct ends landed inside busway
        /// tap-offs and electrical enclosures. Those are clashes, not connections, and
        /// reporting them as "a family-content gap" would have excused real hits.
        ///
        /// The test is by CATEGORY, deliberately, rather than asking whether the other
        /// side carries a matching connector. Every plant family in this model has
        /// zero connectors — that absence is the very thing being reported, so it
        /// cannot also be the test for whether to report it.
        /// </summary>
        private static bool CouldConnect(Element service, Element other)
        {
            var sc = PairSuppressor.GetBuiltInCategory(service);
            var oc = PairSuppressor.GetBuiltInCategory(other);
            if (sc == BuiltInCategory.INVALID || oc == BuiltInCategory.INVALID) return false;

            switch (sc)
            {
                case BuiltInCategory.OST_DuctCurves:
                case BuiltInCategory.OST_FlexDuctCurves:
                    return In(oc,
                        BuiltInCategory.OST_MechanicalEquipment,
                        BuiltInCategory.OST_DuctTerminal,
                        BuiltInCategory.OST_DuctAccessory,
                        BuiltInCategory.OST_DuctFitting,
                        BuiltInCategory.OST_DuctCurves,
                        BuiltInCategory.OST_FlexDuctCurves,
                        BuiltInCategory.OST_SpecialityEquipment);

                case BuiltInCategory.OST_PipeCurves:
                case BuiltInCategory.OST_FlexPipeCurves:
                    return In(oc,
                        BuiltInCategory.OST_MechanicalEquipment,
                        BuiltInCategory.OST_PlumbingFixtures,
                        BuiltInCategory.OST_Sprinklers,
                        BuiltInCategory.OST_PipeAccessory,
                        BuiltInCategory.OST_PipeFitting,
                        BuiltInCategory.OST_PipeCurves,
                        BuiltInCategory.OST_FlexPipeCurves,
                        BuiltInCategory.OST_SpecialityEquipment);

                case BuiltInCategory.OST_CableTray:
                    return In(oc,
                        BuiltInCategory.OST_ElectricalEquipment,
                        BuiltInCategory.OST_CableTrayFitting,
                        BuiltInCategory.OST_CableTray);

                case BuiltInCategory.OST_Conduit:
                    return In(oc,
                        BuiltInCategory.OST_ElectricalEquipment,
                        BuiltInCategory.OST_ElectricalFixtures,
                        BuiltInCategory.OST_LightingFixtures,
                        BuiltInCategory.OST_ConduitFitting,
                        BuiltInCategory.OST_Conduit);
            }
            return false;
        }

        private static bool In(BuiltInCategory c, params BuiltInCategory[] set)
        {
            foreach (var x in set) if (c == x) return true;
            return false;
        }

        private static bool Inside(XYZ p, XYZ min, XYZ max)
        {
            const double tol = 0.01;   // ~3 mm
            return p.X >= min.X - tol && p.X <= max.X + tol
                && p.Y >= min.Y - tol && p.Y <= max.Y + tol
                && p.Z >= min.Z - tol && p.Z <= max.Z + tol;
        }

        /// <summary>
        /// Transform all EIGHT corners and re-bound. Transforming only min and max is
        /// the classic rotated-link bug: under rotation those two points no longer
        /// bound the box.
        /// </summary>
        private static void Corners(Transform t, XYZ lo, XYZ hi, out XYZ min, out XYZ max)
        {
            double nx = double.MaxValue, ny = double.MaxValue, nz = double.MaxValue;
            double xx = double.MinValue, xy = double.MinValue, xz = double.MinValue;

            for (int i = 0; i < 8; i++)
            {
                var p = t.OfPoint(new XYZ(
                    (i & 1) == 0 ? lo.X : hi.X,
                    (i & 2) == 0 ? lo.Y : hi.Y,
                    (i & 4) == 0 ? lo.Z : hi.Z));

                if (p.X < nx) nx = p.X;
                if (p.Y < ny) ny = p.Y;
                if (p.Z < nz) nz = p.Z;
                if (p.X > xx) xx = p.X;
                if (p.Y > xy) xy = p.Y;
                if (p.Z > xz) xz = p.Z;
            }

            min = new XYZ(nx, ny, nz);
            max = new XYZ(xx, xy, xz);
        }
    }
}
