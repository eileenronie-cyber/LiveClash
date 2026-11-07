using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>The text of one panel row's schedule columns. Never null in any field.</summary>
    public sealed class RowScheduleCells
    {
        public string IssueNo { get; set; } = "";
        public string Assigned { get; set; } = "";
        public string HeldBy { get; set; } = "";
        public string Group { get; set; } = "";
        /// <summary>Who decided the row's status when that is someone other than this user, else "".</summary>
        public string DecidedBy { get; set; } = "";
        public string PendingMark { get; set; } = "";
        public string PendingTooltip { get; set; } = "";
    }

    /// <summary>Which of the panel's new columns have any width at all.</summary>
    public sealed class ScheduleColumns
    {
        public bool IssueNo { get; set; }
        public bool Assigned { get; set; }
        public bool HeldBy { get; set; }
        public bool Group { get; set; }
    }

    /// <summary>The panel's schedule columns, status suffixes and search text (spec 9, 6.7). Pure.</summary>
    public static class RowScheduleText
    {
        private const string PendingWord = "pending";
        private const string Various = "various";

        private static string T(string s) { return s == null ? "" : s.Trim(); }

        private static RowScheduleInfo MarkerOf(ScheduleSnapshot s, string rowKey)
        {
            var info = s.Row(rowKey);
            return info != null && info.HasMarker ? info : null;
        }

        private static bool IsOther(ScheduleSnapshot s, string decidedBy)
        {
            return decidedBy.Length > 0 && !string.Equals(decidedBy, T(s.Me), StringComparison.OrdinalIgnoreCase);
        }

        // The members of a row, in an order that does not depend on the order of the keys: lowest issue number first,
        // then by the other shown fields, so "the first one" is the same on every call.
        private static List<RowScheduleInfo> Members(ScheduleSnapshot s, IEnumerable<string> keys)
        {
            var list = new List<RowScheduleInfo>();
            if (s == null || keys == null) return list;
            foreach (var key in keys.Distinct())
            {
                var info = MarkerOf(s, key);
                if (info != null) list.Add(info);
            }
            return list.OrderBy(i => i, Comparer<RowScheduleInfo>.Create(Compare)).ToList();
        }

        private static int Compare(RowScheduleInfo a, RowScheduleInfo b)
        {
            int c = IssueOrder(T(a.IssueNo), T(b.IssueNo));
            if (c != 0) return c;
            foreach (var pair in new[] { Tuple.Create(a.AssignedTo, b.AssignedTo), Tuple.Create(a.HeldBy, b.HeldBy), Tuple.Create(a.DecidedBy, b.DecidedBy),
                                         Tuple.Create(a.PendingStatus, b.PendingStatus), Tuple.Create(a.PendingReason, b.PendingReason) })
            {
                c = string.CompareOrdinal(T(pair.Item1), T(pair.Item2));
                if (c != 0) return c;
            }
            return 0;
        }

        // A readable number first (prefix, then number), then any other text by ordinal order, then none.
        private static int IssueOrder(string a, string b)
        {
            string pa, pb; int na, nb;
            bool ra = IssueNumbers.TryParse(a, out pa, out na), rb = IssueNumbers.TryParse(b, out pb, out nb);
            if (ra && rb) { int c = string.CompareOrdinal(pa, pb); return c != 0 ? c : na.CompareTo(nb); }
            if (ra) return -1;
            if (rb) return 1;
            if (a.Length == 0 && b.Length == 0) return 0;
            if (a.Length == 0) return 1;
            if (b.Length == 0) return -1;
            return string.CompareOrdinal(a, b);
        }

        // One name when they all agree (ignoring capitals), otherwise "various"; none at all is "".
        private static string Shared(IEnumerable<string> values, bool emptyCounts)
        {
            var distinct = new List<string>();
            foreach (var v in values.Select(T))
            {
                if (v.Length == 0 && !emptyCounts) continue;
                if (!distinct.Any(d => string.Equals(d, v, StringComparison.OrdinalIgnoreCase))) distinct.Add(v);
            }
            return distinct.Count == 0 ? "" : distinct.Count == 1 ? distinct[0] : Various;
        }

        private static string PendingTip(RowScheduleInfo info)
        {
            var issue = T(info.IssueNo);
            var why = T(info.PendingReason);
            return (issue.Length > 0 ? issue + ": " : "") + T(info.PendingStatus) + " (" + PendingWord + (why.Length > 0 ? ": " + why : "") + ")";
        }

        public static RowScheduleCells For(ScheduleSnapshot s, string rowKey)
        {
            return ForKeys(s, new[] { rowKey });
        }

        /// <summary>The cells of a row that holds several pairs (a proximity group), from the markers its keys have.</summary>
        public static RowScheduleCells ForKeys(ScheduleSnapshot s, IEnumerable<string> keys)
        {
            var cells = new RowScheduleCells();
            var members = Members(s, keys);
            if (members.Count == 0) return cells;

            cells.IssueNo = members.Select(m => T(m.IssueNo)).FirstOrDefault(i => i.Length > 0) ?? "";
            cells.Assigned = Shared(members.Select(m => m.AssignedTo), true);
            cells.Group = members.Select(m => T(m.GroupLabel)).FirstOrDefault(g => g.Length > 0) ?? "";
            if (s.Workshared) cells.HeldBy = Shared(members.Select(m => m.HeldBy), false);
            // Stryker disable once Boolean : IsOther has removed the empty names
            cells.DecidedBy = Shared(members.Select(m => T(m.DecidedBy)).Where(d => IsOther(s, d)), false);

            var pending = members.Where(m => T(m.PendingStatus).Length > 0).ToList();
            if (pending.Count > 0)
            {
                cells.PendingMark = PendingWord;
                cells.PendingTooltip = PendingTip(pending[0]) + (pending.Count > 1 ? " (and " + (pending.Count - 1) + " more pending)" : "");
            }
            return cells;
        }

        /// <summary>The keys whose triage goes through the marker: those whose row has one, none when the snapshot refuses writes.</summary>
        public static List<string> MarkerKeys(ScheduleSnapshot s, IEnumerable<string> keys)
        {
            if (s == null || s.Refused || keys == null) return new List<string>();
            return keys.Where(k => MarkerOf(s, k) != null).Distinct().ToList();
        }

        public static ScheduleColumns Columns(ScheduleSnapshot s)
        {
            bool has = s != null && s.HasMarkers;
            return new ScheduleColumns { IssueNo = has, Assigned = has, Group = has, HeldBy = has && s.Workshared };
        }

        /// <summary>"Ignored", "Ignored by jdoe", "Ignored (pending)" or both: the row's status with who decided it and whether it waits for the model.</summary>
        public static string StatusText(string status, RowScheduleCells cells)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(status)) parts.Add(status.Trim());
            if (cells != null && !string.IsNullOrEmpty(cells.DecidedBy)) parts.Add("by " + cells.DecidedBy);
            if (cells != null && !string.IsNullOrEmpty(cells.PendingMark)) parts.Add("(" + cells.PendingMark + ")");
            return string.Join(" ", parts);
        }

        /// <summary>What the panel's search box also looks in for a row: its issue number, merged numbers and assignee.</summary>
        public static string SearchText(ScheduleSnapshot s, string rowKey)
        {
            return SearchTextOf(s, new[] { rowKey });
        }

        /// <summary>What the search also looks in for a row that holds several pairs.</summary>
        public static string SearchTextOf(ScheduleSnapshot s, IEnumerable<string> keys)
        {
            var words = new List<string>();
            foreach (var info in Members(s, keys))
            {
                words.Add(T(info.IssueNo));
                if (info.MergedIssueNos != null) words.AddRange(info.MergedIssueNos.Select(T));
                words.Add(T(info.AssignedTo));
            }
            return string.Join(" ", words.Where(w => w.Length > 0));
        }

        /// <summary>The "decided by others" filter: the row's decision was made by someone other than this user.</summary>
        public static bool DecidedByOthers(ScheduleSnapshot s, string rowKey)
        {
            return DecidedByOthersAny(s, new[] { rowKey });
        }

        /// <summary>The same for a row that holds several pairs: any of them.</summary>
        public static bool DecidedByOthersAny(ScheduleSnapshot s, IEnumerable<string> keys)
        {
            return Members(s, keys).Any(m => IsOther(s, T(m.DecidedBy)));
        }
    }
}
