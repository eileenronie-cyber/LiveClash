using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// The levels of a model as a table read ONCE: which level a point is on is then a binary search, not a new Revit collector per clash.
    /// (Clash schedule at scale, 2026-11-02: the old per-clash lookup built a <c>FilteredElementCollector</c> for each of 12,497 clashes.)
    /// The definition is the old one, word for word: the name of the highest level whose elevation is at or below z + 1e-6 feet, "" when there is
    /// none; two levels at one elevation give the one listed first. Pure (no Revit), so it is checked headless against the old linear scan.
    /// </summary>
    public sealed class LevelTable
    {
        /// <summary>A point this close under a level (feet) still counts as on it.</summary>
        public const double Tolerance = 1e-6;

        private readonly double[] _elevation;   // ascending, one entry per distinct elevation
        private readonly string[] _name;

        public LevelTable(IEnumerable<KeyValuePair<double, string>> levels)
        {
            var ordered = (levels ?? new List<KeyValuePair<double, string>>())
                .Select((kv, i) => new { Elevation = kv.Key, Name = kv.Value ?? "", Index = i })
                .OrderBy(x => x.Elevation).ThenBy(x => x.Index).ToList();
            var elevation = new List<double>();
            var name = new List<string>();
            foreach (var x in ordered)
            {
                if (elevation.Count > 0 && elevation[elevation.Count - 1] == x.Elevation) continue;   // the first listed at an elevation wins
                elevation.Add(x.Elevation);
                name.Add(x.Name);
            }
            _elevation = elevation.ToArray();
            _name = name.ToArray();
        }

        public int Count { get { return _elevation.Length; } }

        /// <summary>The name of the highest level at or below <paramref name="zFeet"/> (plus the tolerance); "" when there is none.</summary>
        public string At(double zFeet)
        {
            double limit = zFeet + Tolerance;
            int lo = 0, hi = _elevation.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_elevation[mid] <= limit) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found < 0 ? "" : _name[found];
        }
    }
}
