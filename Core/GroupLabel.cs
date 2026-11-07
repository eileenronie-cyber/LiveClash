using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// The schedule's Group column: the lowest issue number in the panel's proximity group and how
    /// many others it holds ("JD-0042 (+13)"); empty for a pair alone (spec 6.10). Pure.
    /// "Lowest" is by prefix (ordinal, upper case) then number (numeric), so it never depends on a clock.
    /// </summary>
    public static class GroupLabel
    {
        public static string Of(IEnumerable<string> memberIssueNos)
        {
            if (memberIssueNos == null) return "";
            var members = memberIssueNos.ToList();
            if (members.Count < 2) return "";

            var readable = new List<Tuple<string, int>>();
            foreach (var m in members)
            {
                string p; int n;
                if (IssueNumbers.TryParse(m, out p, out n)) readable.Add(Tuple.Create(p, n));
            }
            if (readable.Count == 0) return "";

            var ordered = readable.OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2);
            // Stryker disable once Linq : an empty list returned at the line above
            var lowest = ordered.First();
            return IssueNumbers.Format(lowest.Item1, lowest.Item2) + " (+" + (members.Count - 1).ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
