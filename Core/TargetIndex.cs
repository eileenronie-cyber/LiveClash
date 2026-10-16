using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The broad phase: a uniform spatial hash over the target elements' bounding
    /// boxes, built once per scan and queried per source element.
    ///
    /// WHY THIS REPLACED THE REVIT COLLECTOR (measured in Revit 2026, 2026-08-21,
    /// 33,980 targets across 19 categories on a 310,835-element model):
    ///
    ///     new FilteredElementCollector(doc, targetIds)     60.8    ms/element
    ///     cached boxes, linear scan                        12.4    ms/element
    ///     cached boxes + this grid                          0.1427 ms/element   (426x)
    ///
    /// Index build is 87 ms total — 84 ms to read the boxes, 3 ms to bin them — and
    /// it is cached in ScanContext, so a live edit pays none of it. All three
    /// approaches returned identical candidate sets; this is a speed change, not a
    /// behaviour change.
    ///
    /// The grid answers "which targets might overlap this box". It is deliberately
    /// conservative: it can return a candidate that does not really overlap, and the
    /// caller re-tests with an exact box comparison. It must never MISS an overlap.
    /// </summary>
    public class TargetIndex
    {
        /// <summary>
        /// Cell size is chosen from the model, then clamped to this range in feet.
        /// Too small and a long duct is shredded across thousands of cells; too large
        /// and everything lands in one bucket and the grid stops filtering anything.
        /// </summary>
        private const double MinCellSize = 5.0;
        private const double MaxCellSize = 50.0;

        /// <summary>Used when nothing in the set has a usable dimension.</summary>
        private const double FallbackCellSize = 10.0;

        /// <summary>
        /// An element spanning more than this many cells is not binned at all — it
        /// goes in <see cref="_oversize"/> and is tested on every query instead.
        /// Without this, one model-wide floor slab would be inserted into tens of
        /// thousands of cells and make the index slower to build than to skip.
        ///
        /// The cap is on the BOX, not the cell: clamping the cell size does not bound
        /// how many cells one enormous element covers. It was 64, which suited a fixed
        /// 20 ft cell; with an adaptive cell that can be a tenth of that, the same
        /// element covers a hundred times as many, and 64 would have pushed ordinary
        /// walls and slabs into the always-tested list — turning an optimisation into
        /// a slowdown.
        /// </summary>
        private const int MaxCellsPerElement = 4096;

        /// <summary>
        /// Chosen once at Build from the elements actually present, and fixed for the
        /// life of the index — incremental Insert has to bin into the same grid.
        /// </summary>
        private double _cellSize = FallbackCellSize;
        private double _invCellSize = 1.0 / FallbackCellSize;

        private readonly List<ElementId> _ids = new List<ElementId>();
        private readonly List<double> _minX = new List<double>(), _minY = new List<double>(), _minZ = new List<double>();
        private readonly List<double> _maxX = new List<double>(), _maxY = new List<double>(), _maxZ = new List<double>();

        private readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        private readonly List<int> _oversize = new List<int>();

        /// <summary>Slot lookup by raw id value, so an element can be re-indexed in place.</summary>
        private readonly Dictionary<long, int> _slotOf = new Dictionary<long, int>();

        /// <summary>
        /// Retired slots. Removing an element from a spatial hash properly means
        /// visiting every cell it was binned into; tombstoning is O(1) and the slot
        /// is simply skipped by queries. Churn from one modelling session is trivial,
        /// and a full rebuild reclaims everything.
        ///
        /// A parallel bool list rather than a HashSet: this is read once per candidate
        /// in the hot loop, and an indexed read beats a hash lookup there.
        /// </summary>
        private readonly List<bool> _dead = new List<bool>();
        private int _deadCount;

        /// <summary>
        /// Per-slot "seen in this query" stamps, and the current query's number.
        ///
        /// This replaces a HashSet allocated per query. A cell-bucket sweep visits the
        /// same slot repeatedly when an element spans several cells, so duplicates
        /// have to be suppressed — but hashing to do it, and allocating the set each
        /// time, cost more than the test being suppressed. Comparing an int and
        /// bumping a generation counter clears the whole structure in O(1).
        /// </summary>
        private int[] _stamp = new int[0];
        private int _generation;

        public int Count { get { return _ids.Count - _deadCount; } }

        /// <summary>Number of elements too large to bin; tested on every query.</summary>
        public int OversizeCount { get { return _oversize.Count; } }

        private TargetIndex() { }

        // ------------------------------------------------------------ incremental

        /// <summary>
        /// Drop an element from the index. Safe to call for something that was never
        /// in it.
        /// </summary>
        public void Remove(ElementId id)
        {
            if (id == null) return;
            int slot;
            if (!_slotOf.TryGetValue(IdUtil.Value(id), out slot)) return;
            _slotOf.Remove(IdUtil.Value(id));
            if (slot >= 0 && slot < _dead.Count && !_dead[slot])
            {
                _dead[slot] = true;
                _deadCount++;
            }
        }

        /// <summary>
        /// Add or re-index one element, reading its current bounding box.
        ///
        /// This is what keeps the cached index usable across a whole modelling
        /// session. Rebuilding wholesale costs ~330 ms on a 40,000-target model, and
        /// a time-based cache meant any edit made more than the TTL after the last
        /// one paid that again — which, at real modelling pace, was most edits, and
        /// showed up as "Budget hit" in the log. Patching the handful of elements the
        /// updater just handed us is microseconds and keeps the index correct.
        /// </summary>
        public void Insert(Document doc, ElementId id)
        {
            if (doc == null || id == null) return;

            Remove(id);

            Element e;
            try { e = doc.GetElement(id); } catch { return; }
            if (e == null || !e.IsValidObject) return;

            BoundingBoxXYZ bb;
            try { bb = e.get_BoundingBox(null); } catch { return; }
            if (bb == null) return;

            double nx, ny, nz, xx, xy, xz;
            Extents(bb, out nx, out ny, out nz, out xx, out xy, out xz);

            // Same storage the build uses, so the two can never disagree about
            // what a slot consists of.
            Append(id, nx, ny, nz, xx, xy, xz);
            Bin(_ids.Count - 1);
        }

        /// <summary>
        /// Read every target's bounding box once and bin it. Elements that refuse a
        /// bounding box are skipped rather than failing the whole index.
        /// </summary>
        public static TargetIndex Build(Document doc, ICollection<ElementId> ids)
        {
            var ix = new TargetIndex();
            if (doc == null || !doc.IsValidObject || ids == null || ids.Count == 0) return ix;

            // Pass 1: record every box. Deliberately NOT via Insert, which would bin
            // as it goes — the cell size is not known until the whole population has
            // been seen, and re-reading 40,000 bounding boxes to find out would double
            // the cost of building the index.
            var longest = new List<double>(ids.Count);
            foreach (var id in ids)
            {
                if (id == null) continue;

                Element e;
                try { e = doc.GetElement(id); } catch { continue; }
                if (e == null || !e.IsValidObject) continue;

                BoundingBoxXYZ bb;
                try { bb = e.get_BoundingBox(null); } catch { continue; }
                if (bb == null) continue;

                double nx, ny, nz, xx, xy, xz;
                Extents(bb, out nx, out ny, out nz, out xx, out xy, out xz);

                ix.Append(id, nx, ny, nz, xx, xy, xz);

                double dx = xx - nx, dy = xy - ny, dz = xz - nz;
                double d = dx > dy ? dx : dy;
                if (dz > d) d = dz;
                if (d > 0) longest.Add(d);
            }

            // Pass 2: now the grid knows what shape the model is.
            ix._cellSize = ChooseCellSize(longest);
            ix._invCellSize = 1.0 / ix._cellSize;
            for (int i = 0; i < ix._ids.Count; i++) ix.Bin(i);

            return ix;
        }

        /// <summary>Store one element's extents without binning it.</summary>
        private void Append(ElementId id, double nx, double ny, double nz,
                                          double xx, double xy, double xz)
        {
            int slot = _ids.Count;
            _ids.Add(id);
            _minX.Add(nx); _minY.Add(ny); _minZ.Add(nz);
            _maxX.Add(xx); _maxY.Add(xy); _maxZ.Add(xz);
            _dead.Add(false);
            _slotOf[IdUtil.Value(id)] = slot;

            if (_stamp.Length < _ids.Count)
            {
                var bigger = new int[Math.Max(64, _ids.Count * 2)];
                Array.Copy(_stamp, bigger, _stamp.Length);
                _stamp = bigger;
            }
        }

        /// <summary>
        /// Widest axis-aligned extents of a box, folding in its own Transform via all
        /// eight corners. get_BoundingBox(null) is identity-transformed in practice,
        /// but transforming only Min and Max is the classic bug the moment it is not.
        /// </summary>
        private static void Extents(BoundingBoxXYZ bb,
            out double nx, out double ny, out double nz,
            out double xx, out double xy, out double xz)
        {
            nx = ny = nz = double.MaxValue;
            xx = xy = xz = double.MinValue;
            foreach (var p in GeometryUtil.Corners(bb))
            {
                if (p.X < nx) nx = p.X; if (p.X > xx) xx = p.X;
                if (p.Y < ny) ny = p.Y; if (p.Y > xy) xy = p.Y;
                if (p.Z < nz) nz = p.Z; if (p.Z > xz) xz = p.Z;
            }
        }

        private void Bin(int i)
        {
            int x0 = Cell(_minX[i]), x1 = Cell(_maxX[i]);
            int y0 = Cell(_minY[i]), y1 = Cell(_maxY[i]);
            int z0 = Cell(_minZ[i]), z1 = Cell(_maxZ[i]);

            long span = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (span > MaxCellsPerElement) { _oversize.Add(i); return; }

            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = Key(x, y, z);
                        List<int> bucket;
                        if (!_grid.TryGetValue(k, out bucket)) { bucket = new List<int>(); _grid[k] = bucket; }
                        bucket.Add(i);
                    }
        }

        /// <summary>
        /// Targets whose bounding box overlaps <paramref name="outline"/>, which must
        /// already be expressed in this index's coordinate system — for a link index
        /// that means link space. Use GeometryUtil.TransformToOutline to get there,
        /// since it folds in the clearance and handles rotation via all eight corners.
        /// </summary>
        public List<ElementId> Query(Outline outline, ElementId exclude)
        {
            var hits = new List<ElementId>();
            if (_ids.Count == 0 || outline == null) return hits;

            var lo = outline.MinimumPoint;
            var hi = outline.MaximumPoint;

            int x0 = Cell(lo.X), x1 = Cell(hi.X);
            int y0 = Cell(lo.Y), y1 = Cell(hi.Y);
            int z0 = Cell(lo.Z), z1 = Cell(hi.Z);

            long excludeValue = exclude != null ? IdUtil.Value(exclude) : long.MinValue;

            // One pass: a slot is stamped and tested the first time it is reached,
            // instead of being gathered into a set and walked again afterwards.
            _generation++;
            int gen = _generation;

            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        List<int> bucket;
                        if (!_grid.TryGetValue(Key(x, y, z), out bucket)) continue;
                        for (int b = 0; b < bucket.Count; b++) Consider(bucket[b], gen, lo, hi, excludeValue, hits);
                    }

            for (int o = 0; o < _oversize.Count; o++)
                Consider(_oversize[o], gen, lo, hi, excludeValue, hits);

            return hits;
        }

        private void Consider(int i, int gen, XYZ lo, XYZ hi, long excludeValue, List<ElementId> hits)
        {
            if (_stamp[i] == gen) return;      // already dealt with in this query
            _stamp[i] = gen;

            if (_dead[i]) return;

            // Exact box test — the grid is only a filter.
            if (_maxX[i] < lo.X || _minX[i] > hi.X) return;
            if (_maxY[i] < lo.Y || _minY[i] > hi.Y) return;
            if (_maxZ[i] < lo.Z || _minZ[i] > hi.Z) return;

            var id = _ids[i];
            if (IdUtil.Value(id) == excludeValue) return;
            hits.Add(id);
        }

        private int Cell(double v)
        {
            return (int)Math.Floor(v * _invCellSize);
        }

        /// <summary>
        /// Cell edge from the 90th PERCENTILE of longest box edge, clamped.
        ///
        /// A percentile rather than a mean because one outlier ruins a mean — the
        /// longest box on this model is 5,945 ft against a median of 2.1.
        ///
        /// The NINETIETH rather than the median, which is where the straight port from
        /// the Federated grid went wrong. Measured on PRJ01, binning + querying the
        /// whole target set:
        ///
        ///      1.0 ft   703 ms      10.0 ft    18 ms
        ///      2.1 ft   147 ms      20.0 ft    17 ms
        ///      5.0 ft    27 ms      40.0 ft    29 ms
        ///
        /// A broad flat optimum from 5 to 20 ft, and the median lands at 2.1 — eight
        /// times worse than the hard-coded 20 ft it replaced. The median describes the
        /// swarm of small fittings, but the cost is driven by long runs being shredded
        /// across cells, and p90 is what keeps all but the longest tenth in one cell.
        /// Candidate counts were IDENTICAL at every size, so this only ever trades
        /// bookkeeping — never the quality of the filtering.
        /// </summary>
        private static double ChooseCellSize(List<double> longestEdges)
        {
            if (longestEdges == null || longestEdges.Count == 0) return FallbackCellSize;

            longestEdges.Sort();
            int i = (int)(longestEdges.Count * 0.90);
            if (i >= longestEdges.Count) i = longestEdges.Count - 1;

            double p90 = longestEdges[i];
            if (p90 <= 0) return FallbackCellSize;

            return Math.Max(MinCellSize, Math.Min(p90, MaxCellSize));
        }

        /// <summary>
        /// Standard spatial-hash mix. Collisions are harmless — two different cells
        /// sharing a bucket only ever costs extra candidates, which the exact box
        /// test above then rejects.
        /// </summary>
        private static long Key(int x, int y, int z)
        {
            return ((long)x * 73856093) ^ ((long)y * 19349663) ^ ((long)z * 83492791);
        }
    }
}
