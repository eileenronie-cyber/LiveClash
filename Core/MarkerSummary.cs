using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>The Refresh summary text built from counts re-read from the model (spec 10.3, rule C5). Pure.</summary>
    public static class MarkerSummary
    {
        public sealed class Reason
        {
            public string Text { get; set; }
            public int Count { get; set; }
        }

        public sealed class Counts
        {
            public int Clashes { get; set; }
            public int Unchanged { get; set; }
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Resolved { get; set; }
            /// <summary>Markers deleted (a merged duplicate, or retention): part of the sum.</summary>
            public int Removed { get; set; }
            // Notes about markers already counted above (not part of the sum).
            public int LostDecisions { get; set; }
            public int Merged { get; set; }
            public int Moved { get; set; }
            public int PendingApplied { get; set; }
            public List<Reason> PassedOver { get; set; } = new List<Reason>();
            public List<Reason> NotRetested { get; set; } = new List<Reason>();
            public List<Reason> NotMarked { get; set; } = new List<Reason>();

            /// <summary>A copy of every field, the lists too, so a field added later cannot be forgotten by a hand-written copy.</summary>
            public Counts Clone()
            {
                Func<List<Reason>, List<Reason>> copy = list => (list ?? new List<Reason>()).Select(r => new Reason { Text = r.Text, Count = r.Count }).ToList();
                return new Counts
                {
                    Clashes = Clashes, Unchanged = Unchanged, Created = Created, Updated = Updated, Resolved = Resolved, Removed = Removed,
                    LostDecisions = LostDecisions, Merged = Merged, Moved = Moved, PendingApplied = PendingApplied,
                    PassedOver = copy(PassedOver), NotRetested = copy(NotRetested), NotMarked = copy(NotMarked)
                };
            }
        }

        public sealed class SetCounts
        {
            public string Name { get; set; }
            public int InPanel { get; set; }
            public int UpToDate { get; set; }
            public int NotLive { get; set; }
            public bool SetOff { get; set; }
            public int NotRetested { get; set; }
        }

        private static string N(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        private static string Noun(int n, string one, string many) { return N(n) + " " + (n == 1 ? one : many); }

        private static List<Reason> Listed(List<Reason> reasons)
        {
            return (reasons ?? new List<Reason>()).Where(r => r != null && r.Count > 0)
                .OrderByDescending(r => r.Count).ThenBy(r => r.Text ?? "", StringComparer.Ordinal).ToList();
        }

        private static int Sum(List<Reason> reasons) { return Listed(reasons).Sum(r => r.Count); }

        public static int UpToDate(Counts c)
        {
            return c == null ? 0 : c.Unchanged + c.Created + c.Updated + c.Resolved;
        }

        /// <summary>Clashes = unchanged + created + updated + resolved + removed + passed over + not re-tested + not marked.</summary>
        public static bool Balanced(Counts c)
        {
            return c != null && c.Clashes == UpToDate(c) + c.Removed + Sum(c.PassedOver) + Sum(c.NotRetested) + Sum(c.NotMarked);
        }

        private static string WithReasons(int total, string label, List<Reason> reasons)
        {
            // One reason is the whole total ("3 not re-tested (link not loaded)"); several get their own counts.
            var listed = Listed(reasons);
            var text = listed.Count == 1 ? (listed[0].Text ?? "other") : string.Join(", ", listed.Select(r => N(r.Count) + " " + (r.Text ?? "other")));
            return N(total) + " " + label + " (" + text + ")";
        }

        public static string Describe(Counts c)
        {
            if (c == null) return "";
            int passed = Sum(c.PassedOver), notRetested = Sum(c.NotRetested), notMarked = Sum(c.NotMarked);
            if (c.Clashes <= 0 && UpToDate(c) == 0 && passed == 0 && notRetested == 0 && notMarked == 0 && c.Removed == 0 && c.Merged == 0 && c.Moved == 0 && c.PendingApplied == 0 && c.LostDecisions == 0)
                return "No clashes to put in a schedule.";

            var parts = new List<string>();
            if (UpToDate(c) > 0)
            {
                var inner = new List<string>();
                if (c.Unchanged > 0) inner.Add(N(c.Unchanged) + " unchanged");
                if (c.Created > 0) inner.Add(N(c.Created) + " created");
                if (c.Updated > 0) inner.Add(N(c.Updated) + " updated");
                if (c.Resolved > 0) inner.Add(N(c.Resolved) + " resolved");
                parts.Add(N(UpToDate(c)) + " up to date (" + string.Join(", ", inner) + ")");
            }
            if (c.Removed > 0) parts.Add(N(c.Removed) + " removed");
            if (c.Merged > 0) parts.Add(N(c.Merged) + " merged");
            if (c.Moved > 0) parts.Add(N(c.Moved) + " moved");
            if (c.PendingApplied > 0) parts.Add(N(c.PendingApplied) + " pending applied");
            if (c.LostDecisions > 0) parts.Add(Noun(c.LostDecisions, "merge decision", "merge decisions") + " not kept (see the Record)");
            if (passed > 0) parts.Add(WithReasons(passed, "passed over", c.PassedOver));
            if (notRetested > 0) parts.Add(WithReasons(notRetested, "not re-tested", c.NotRetested));
            if (notMarked > 0) parts.Add(WithReasons(notMarked, "not marked", c.NotMarked));

            return "Clash markers: " + Noun(c.Clashes, "clash", "clashes") + "." + (parts.Count > 0 ? " " + string.Join(", ", parts) + "." : "");
        }

        /// <summary>One line per set (spec 4b.3): what this seat's panel holds and what the model holds.</summary>
        public static string SetLine(SetCounts s)
        {
            if (s == null || s.Name == null) return "";
            if (s.SetOff) return s.Name + " (set off on this seat): " + Noun(s.NotRetested, "marker", "markers") + " not re-tested.";
            return s.Name + ": " + N(s.InPanel) + " in your panel, " + Noun(s.UpToDate, "marker", "markers") + " up to date"
                + (s.NotLive > 0 ? "; " + N(s.NotLive) + " more not live on this seat" : "") + ".";
        }

        public static string NothingToDo(int markers)
        {
            return "Markers up to date (" + N(markers) + ").";
        }
    }
}
