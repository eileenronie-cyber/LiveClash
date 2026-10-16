using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// One coordination issue: a cluster of individual element-pair clashes that a
    /// person would point at and call a single problem.
    ///
    /// This is the difference between a report someone acts on and a list they
    /// scroll past. Measured on this project: a full sweep produced 208 rows, but
    /// three consecutive pipes each hitting the same two cable trays is one issue,
    /// not six — a pipe run crossing a tray run. Same pattern in the link test: three
    /// duct fittings against the same two link elements gave 18 rows.
    /// </summary>
    public class ClashGroup
    {
        public List<ClashResult> Members = new List<ClashResult>();

        /// <summary>Centre of the members' locations, in host coordinates.</summary>
        public XYZ Location;

        /// <summary>"Current model", or the link the other side lives in.</summary>
        public string ModelName;

        public int HardCount;
        public int ClearanceCount;

        /// <summary>Bounding boxes overlap but the exact test could not be run.</summary>
        public int UnverifiedCount;

        public int Count { get { return Members.Count; } }
        public bool AnyHard { get { return HardCount > 0; } }

        /// <summary>Representative member, used for the row's element names.</summary>
        public ClashResult First { get { return Members.Count > 0 ? Members[0] : null; } }

        /// <summary>
        /// The smallest member key (ordinal): an identity for the group that does NOT depend on
        /// the order its members arrived in. <see cref="First"/> does - it is just Members[0], and
        /// the members arrive in whatever order the store enumerates - so sorting ties by
        /// First.Key still reordered multi-member groups from one session or runtime to the next.
        /// Filled in by <see cref="ClashGrouper"/> when it builds the group.
        /// </summary>
        public string MinKey = "";

        /// <summary>The stable keys of every member, for reading or setting status.</summary>
        public List<string> Keys()
        {
            var list = new List<string>(Members.Count);
            foreach (var m in Members) if (!string.IsNullOrEmpty(m.Key)) list.Add(m.Key);
            return list;
        }

        /// <summary>
        /// The group's status is its LEAST advanced member. A group is only
        /// Coordinated once every pair in it has been accepted — otherwise ticking
        /// off an issue would quietly hide a member nobody looked at.
        /// </summary>
        public ClashStatusKind Status
        {
            get
            {
                var worst = ClashStatusKind.Ignored;
                bool any = false;
                foreach (var m in Members)
                {
                    var k = ClashStatusStore.Instance.Get(m.Key);
                    if (!any || k < worst) { worst = k; any = true; }
                }
                return any ? worst : ClashStatusKind.New;
            }
        }

        /// <summary>Distinct host-document elements on the "what I modelled" side.</summary>
        public List<ElementId> HostElementIds()
        {
            var seen = new HashSet<long>();
            var list = new List<ElementId>();
            foreach (var m in Members)
            {
                if (m.HostElementId == null) continue;
                if (seen.Add(IdUtil.Value(m.HostElementId))) list.Add(m.HostElementId);
            }
            return list;
        }

        /// <summary>
        /// Distinct elements on the other side, but only those in the host document —
        /// a linked element cannot be selected from the host.
        /// </summary>
        public List<ElementId> OtherHostDocIds()
        {
            var seen = new HashSet<long>();
            var list = new List<ElementId>();
            foreach (var m in Members)
            {
                if (m.LinkInstanceId != null || m.OtherElementId == null) continue;
                if (seen.Add(IdUtil.Value(m.OtherElementId))) list.Add(m.OtherElementId);
            }
            return list;
        }

        /// <summary>e.g. "3 × Pipes" — or the element name when there is only one.</summary>
        public string HostLabel { get { return Side(true); } }
        public string OtherLabel { get { return Side(false); } }

        private string Side(bool host)
        {
            var cats = new Dictionary<string, HashSet<long>>();
            foreach (var m in Members)
            {
                var cat = host ? m.HostCategory : m.OtherCategory;
                var id = host ? m.HostElementId : m.OtherElementId;
                if (cat == null) cat = "(no category)";
                HashSet<long> set;
                if (!cats.TryGetValue(cat, out set)) { set = new HashSet<long>(); cats[cat] = set; }
                if (id != null) set.Add(IdUtil.Value(id));
            }

            if (cats.Count == 1)
            {
                foreach (var kv in cats)
                {
                    if (kv.Value.Count == 1)
                    {
                        var m = First;
                        return host ? m.HostElementName : m.OtherElementName;
                    }
                    return kv.Value.Count + " × " + kv.Key;
                }
            }

            var parts = new List<string>();
            foreach (var kv in cats) parts.Add(kv.Value.Count + " × " + kv.Key);
            parts.Sort();
            return string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>
    /// Clusters clash results into groups by proximity.
    ///
    /// Single-linkage clustering: two clashes join the same group when their
    /// locations are within the grouping distance, and groups merge transitively —
    /// so a long run of clashes down one duct becomes one issue even though its two
    /// ends are far apart. That transitivity is the point; it is what follows a run.
    ///
    /// Clashes are only ever grouped within the same model, so a host clash and a
    /// link clash never merge even if they sit on top of each other. They are
    /// different coordination conversations with different people.
    /// </summary>
    public static class ClashGrouper
    {
        public static List<ClashGroup> Group(IList<ClashResult> results, double distanceFeet)
        {
            var groups = new List<ClashGroup>();
            if (results == null || results.Count == 0) return groups;

            // Partition by model first — never merge across documents.
            var byModel = new Dictionary<string, List<ClashResult>>();
            foreach (var r in results)
            {
                var key = r.ModelName ?? "";
                List<ClashResult> bucket;
                if (!byModel.TryGetValue(key, out bucket)) { bucket = new List<ClashResult>(); byModel[key] = bucket; }
                bucket.Add(r);
            }

            foreach (var kv in byModel)
                groups.AddRange(ClusterOne(kv.Value, distanceFeet));

            // Worst and biggest first — that is the order someone wants to work in.
            groups.Sort(delegate (ClashGroup a, ClashGroup b)
            {
                // Unverified ranks with hard: it means "nobody has checked this", which
                // needs attention more than a confirmed clearance hit does.
                bool an = a.AnyHard || a.UnverifiedCount > 0;
                bool bn = b.AnyHard || b.UnverifiedCount > 0;
                if (an != bn) return an ? -1 : 1;
                if (a.Count != b.Count) return b.Count - a.Count;

                // Equal rank: order by the group's MIN member key, so the list does not depend
                // on the order the store happened to enumerate. Returning 0 here kept ties in
                // INPUT order (List.Sort is not guaranteed stable), which is hash order - the
                // same four rows listed differently on Revit 2024 and 2026, and free to
                // reshuffle between sessions. First.Key would not do: for a group with several
                // members, First is whichever arrived first.
                return string.CompareOrdinal(a.MinKey ?? "", b.MinKey ?? "");
            });

            return groups;
        }

        private static List<ClashGroup> ClusterOne(List<ClashResult> items, double d)
        {
            var parent = new int[items.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            if (d > 0)
            {
                // Bin by cell, then only compare against the 27 neighbouring cells.
                // Comparing every pair would be O(n^2), which bites on a full sweep.
                var cells = new Dictionary<long, List<int>>();
                for (int i = 0; i < items.Count; i++)
                {
                    var p = items[i].Location;
                    if (p == null) continue;
                    long k = CellKey((int)Math.Floor(p.X / d), (int)Math.Floor(p.Y / d), (int)Math.Floor(p.Z / d));
                    List<int> bucket;
                    if (!cells.TryGetValue(k, out bucket)) { bucket = new List<int>(); cells[k] = bucket; }
                    bucket.Add(i);
                }

                double dd = d * d;
                for (int i = 0; i < items.Count; i++)
                {
                    var p = items[i].Location;
                    if (p == null) continue;
                    int cx = (int)Math.Floor(p.X / d), cy = (int)Math.Floor(p.Y / d), cz = (int)Math.Floor(p.Z / d);

                    for (int ox = -1; ox <= 1; ox++)
                        for (int oy = -1; oy <= 1; oy++)
                            for (int oz = -1; oz <= 1; oz++)
                            {
                                List<int> bucket;
                                if (!cells.TryGetValue(CellKey(cx + ox, cy + oy, cz + oz), out bucket)) continue;
                                foreach (var j in bucket)
                                {
                                    if (j <= i) continue;
                                    var q = items[j].Location;
                                    if (q == null) continue;
                                    double sx = p.X - q.X, sy = p.Y - q.Y, sz = p.Z - q.Z;
                                    if (sx * sx + sy * sy + sz * sz <= dd) Union(parent, i, j);
                                }
                            }
                }
            }

            var byRoot = new Dictionary<int, ClashGroup>();
            for (int i = 0; i < items.Count; i++)
            {
                int root = Find(parent, i);
                ClashGroup g;
                if (!byRoot.TryGetValue(root, out g))
                {
                    g = new ClashGroup { ModelName = items[i].ModelName };
                    byRoot[root] = g;
                }
                g.Members.Add(items[i]);
                if (items[i].Severity == ClashSeverity.Hard) g.HardCount++;
                else if (items[i].Severity == ClashSeverity.Unverified) g.UnverifiedCount++;
                else g.ClearanceCount++;
            }

            var result = new List<ClashGroup>();
            foreach (var g in byRoot.Values)
            {
                double x = 0, y = 0, z = 0; int n = 0;
                string minKey = null;
                foreach (var m in g.Members)
                {
                    var k = m.Key ?? "";
                    if (minKey == null || string.CompareOrdinal(k, minKey) < 0) minKey = k;
                    if (m.Location == null) continue;
                    x += m.Location.X; y += m.Location.Y; z += m.Location.Z; n++;
                }
                g.Location = n > 0 ? new XYZ(x / n, y / n, z / n) : null;
                g.MinKey = minKey ?? "";
                result.Add(g);
            }
            return result;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        private static long CellKey(int x, int y, int z)
        {
            return ((long)x * 73856093) ^ ((long)y * 19349663) ^ ((long)z * 83492791);
        }
    }
}
