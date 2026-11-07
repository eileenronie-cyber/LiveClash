using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Independent review 2026-11-01, M2: the Refresh summary lost Removed, LostDecisions and NotMarked because a hand-written
    /// copy of the counts forgot them. These checks make forgetting a field impossible to miss. Revit-free.
    /// Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class ScheduleReviewChecks
    {
        private const string P = "schedule review: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Clone(check);
            Describing(check);
        }

        // Every public property of Counts gets its own value, so a copy that skips one is seen.
        private static MarkerSummary.Counts Filled()
        {
            var c = new MarkerSummary.Counts();
            int n = 3;
            foreach (var p in typeof(MarkerSummary.Counts).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite))
            {
                if (p.PropertyType == typeof(int)) p.SetValue(c, n++, null);
                else if (p.PropertyType == typeof(List<MarkerSummary.Reason>))
                    p.SetValue(c, new List<MarkerSummary.Reason> { new MarkerSummary.Reason { Text = p.Name + " a", Count = n++ }, new MarkerSummary.Reason { Text = p.Name + " b", Count = n++ } }, null);
            }
            return c;
        }

        private static void Clone(Action<string, bool?> check)
        {
            var src = Filled();
            var copy = src.Clone();
            var props = typeof(MarkerSummary.Counts).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite).ToList();
            var missed = new List<string>();
            foreach (var p in props)
            {
                var a = p.GetValue(src, null); var b = p.GetValue(copy, null);
                var la = a as IList; var lb = b as IList;
                if (la != null)
                {
                    bool same = lb != null && la.Count == lb.Count && !ReferenceEquals(la, lb);
                    for (int i = 0; same && i < la.Count; i++)
                    {
                        var ra = (MarkerSummary.Reason)la[i]; var rb = (MarkerSummary.Reason)lb[i];
                        same &= !ReferenceEquals(ra, rb) && ra.Text == rb.Text && ra.Count == rb.Count;
                    }
                    if (!same) missed.Add(p.Name);
                }
                else if (!Equals(a, b)) missed.Add(p.Name);
            }
            check(P + "Clone copies every public property of Counts, the lists and their entries too" + (missed.Count > 0 ? " (missed: " + string.Join(", ", missed) + ")" : "") + " (" + props.Count + " properties)",
                props.Count >= 13 && missed.Count == 0);

            var small = new MarkerSummary.Counts { Clashes = 1 };
            var one = small.Clone();
            one.PassedOver.Add(new MarkerSummary.Reason { Text = "x", Count = 1 });
            check(P + "...a clone is its own object: changing it does not change the original", small.PassedOver.Count == 0 && !ReferenceEquals(small, one) && one.Clashes == 1);
        }

        private static MarkerSummary.Counts Consistent()
        {
            // 12 clashes: 8 up to date (6 unchanged + 1 updated + 1 created), 2 removed, 1 passed over, 1 not marked.
            var c = new MarkerSummary.Counts { Clashes = 12, Unchanged = 6, Updated = 1, Created = 1, Removed = 2, Merged = 1, LostDecisions = 1 };
            c.PassedOver.Add(new MarkerSummary.Reason { Text = "held by kdoe", Count = 1 });
            c.NotMarked.Add(new MarkerSummary.Reason { Text = "no position", Count = 1 });
            return c;
        }

        private static void Describing(Action<string, bool?> check)
        {
            var copy = Consistent().Clone();
            var text = MarkerSummary.Describe(copy);
            check(P + "the summary of a copy still says \"2 removed\", \"1 merge decision not kept (see the Record)\" and \"1 not marked (no position)\"",
                text.Contains("2 removed") && text.Contains("1 merge decision not kept (see the Record)") && text.Contains("1 not marked (no position)"));
            check(P + "...and the counts of a copy still balance (12 clashes = 8 up to date + 2 removed + 1 passed over + 1 not marked)", MarkerSummary.Balanced(copy));
        }
    }
}
