using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// The ledger events the clash schedule adds (spec 6.3, 6.6, 6.7, 6.10), and the one reader of them.
    /// They are extra lines in the same append-only TAB-separated ledger. <see cref="ClashStatusReplay"/> skips
    /// an event name that does not parse as a <see cref="ClashStatusKind"/> (R-K2), so an older build, and
    /// today's replay, read the ledger exactly as before; this reader looks only at the new events. Pure, no Revit.
    ///
    /// Line layouts (every text field has tabs and line breaks turned into spaces):
    ///   Assigned          key, Assigned, time, by, assignee            (empty assignee = unassigned)
    ///   PendingToModel    key, PendingToModel, time, by, note, seq, flags, status, assignee
    ///                     (the open entries of a key are read as one: each of status, note and assignee from the latest entry that set it)
    ///                     (flags: S, N, A for which of status, note, assignee the triage set)
    ///   PendingDone       key, PendingDone, time, by, ""               (closes pending entries no later than itself)
    ///   Issue             key, Issue, time, by, issueNo
    ///   DuplicateNotKept  key, DuplicateNotKept, time, by, note, keptIssueNo, removedIssueNo, status, assignee
    /// Nine fields at most, never ten (a ten-field line is what the replay treats as a damaged one).
    /// </summary>
    public static class LedgerExtras
    {
        public const string AssignedEvent = "Assigned";
        public const string PendingEvent = "PendingToModel";
        public const string DoneEvent = "PendingDone";
        public const string IssueEvent = "Issue";
        public const string DuplicateEvent = "DuplicateNotKept";
        public const string ReconciledEvent = "Reconciled";

        /// <summary>The source tag on lines written by reconcile from the model's markers.</summary>
        public const string ModelTag = "model";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class AssignedEntry
        {
            public string Assignee { get; set; }
            public string By { get; set; }
            public DateTime WhenUtc { get; set; }
        }

        public sealed class PendingEntry
        {
            public long SeqAtPending { get; set; }
            /// <summary>The marker Seq each field was set at (the Seq of the entry that supplied it); null when the field is unset.</summary>
            public long? StatusSeq { get; set; }
            public long? NoteSeq { get; set; }
            public long? AssignedSeq { get; set; }
            public MarkerStatus? Status { get; set; }
            public string Note { get; set; }
            public string AssignedTo { get; set; }
            public string By { get; set; }
            public DateTime WhenUtc { get; set; }
        }

        public sealed class IssueEntry
        {
            public string IssueNo { get; set; }
            public DateTime WhenUtc { get; set; }
        }

        public sealed class ReconciledEntry
        {
            public string Source { get; set; }
            public string Event { get; set; }
            public DateTime WhenUtc { get; set; }
        }

        public sealed class DuplicateEntry
        {
            public string Key { get; set; }
            public DateTime WhenUtc { get; set; }
            public string By { get; set; }
            public string KeptIssueNo { get; set; }
            public string RemovedIssueNo { get; set; }
            public MarkerStatus Status { get; set; }
            public string Note { get; set; }
            public string AssignedTo { get; set; }
        }

        /// <summary>What the new events say, per ledger key.</summary>
        public sealed class View
        {
            public Dictionary<string, AssignedEntry> Assigned { get; } = new Dictionary<string, AssignedEntry>(StringComparer.Ordinal);
            /// <summary>Open pending-to-model entries only.</summary>
            public Dictionary<string, PendingEntry> Pending { get; } = new Dictionary<string, PendingEntry>(StringComparer.Ordinal);
            public Dictionary<string, IssueEntry> Issues { get; } = new Dictionary<string, IssueEntry>(StringComparer.Ordinal);
            public List<DuplicateEntry> Duplicates { get; } = new List<DuplicateEntry>();
            /// <summary>The latest "this decision came from the model" note per key.</summary>
            public Dictionary<string, ReconciledEntry> Reconciled { get; } = new Dictionary<string, ReconciledEntry>(StringComparer.Ordinal);
            /// <summary>The latest Gone event per key, even one with no decision to resolve (the replay drops it). A line that must outrank it is timed after it.</summary>
            public Dictionary<string, DateTime> GoneAt { get; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string Line(string key, string ev, DateTime whenUtc, string by, params string[] rest)
        {
            var head = new[] { Clean(key), ev, whenUtc.ToString("o", Inv), Clean(by) };
            return string.Join("\t", head.Concat(rest));
        }

        public static string AssignedLine(string key, DateTime whenUtc, string by, string assignee)
        {
            return Line(key, AssignedEvent, whenUtc, by, Clean(assignee));
        }

        public static string PendingLine(string key, DateTime whenUtc, string by, long seqAtPending, MarkerStatus? status, string note, string assignedTo)
        {
            string flags = (status.HasValue ? "S" : "") + (note != null ? "N" : "") + (assignedTo != null ? "A" : "");
            return Line(key, PendingEvent, whenUtc, by, Clean(note), seqAtPending.ToString(Inv), flags, status.HasValue ? MarkerStatusText.Format(status.Value) : "", Clean(assignedTo));
        }

        public static string DoneLine(string key, DateTime whenUtc, string by)
        {
            return Line(key, DoneEvent, whenUtc, by, "");
        }

        public static string IssueLine(string key, DateTime whenUtc, string by, string issueNo)
        {
            return Line(key, IssueEvent, whenUtc, by, Clean(issueNo));
        }

        public static string DuplicateLine(string key, DateTime whenUtc, string by, string keptIssueNo, string removedIssueNo, MarkerStatus status, string note, string assignedTo)
        {
            return Line(key, DuplicateEvent, whenUtc, by, Clean(note), Clean(keptIssueNo), Clean(removedIssueNo), MarkerStatusText.Format(status), Clean(assignedTo));
        }

        /// <summary>"This decision came from <paramref name="source"/>": written right after the decision line it explains, at the same time.</summary>
        public static string ReconciledLine(string key, DateTime whenUtc, string by, string source, string explainedEvent)
        {
            return Line(key, ReconciledEvent, whenUtc, by, Clean(source) + " " + Clean(explainedEvent));
        }

        private static bool TryWhen(string text, out DateTime when)
        {
            return DateTime.TryParse(text, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when);
        }

        /// <summary>Reads the new events from ledger lines in any order. Never throws; other lines are ignored.</summary>
        public static View Read(IEnumerable<string> lines)
        {
            var view = new View();
            if (lines == null) return view;

            var assigned = new Dictionary<string, AssignedEntry>(StringComparer.Ordinal);
            var pendings = new Dictionary<string, List<PendingEntry>>(StringComparer.Ordinal);
            var doneAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var issues = new Dictionary<string, IssueEntry>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                var p = line.Split('\t');
                DateTime when = DateTime.MinValue;
                if (p.Length < 3 || !TryWhen(p[2], out when)) continue;
                // Stryker disable once String : by is read only by lines with five or more fields
                string key = p[0], by = p.Length > 3 ? p[3] : "";

                switch (p[1])
                {
                    case AssignedEvent:
                        if (p.Length < 5) break;
                        var a = new AssignedEntry { Assignee = p[4], By = by, WhenUtc = when };
                        AssignedEntry cur = null;
                        if (!assigned.TryGetValue(key, out cur) || string.CompareOrdinal(AssignedOrder(a), AssignedOrder(cur)) > 0) assigned[key] = a;
                        break;

                    case PendingEvent:
                        PendingEntry pe = null;
                        if (p.Length >= 9 && TryPending(p, by, when, out pe))
                        {
                            List<PendingEntry> list = null;
                            if (!pendings.TryGetValue(key, out list)) pendings[key] = list = new List<PendingEntry>();
                            list.Add(pe);
                        }
                        break;

                    case ClashStatusReplay.GoneEvent:
                        DateTime gone = DateTime.MinValue;
                        if (!view.GoneAt.TryGetValue(key, out gone) || when > gone) view.GoneAt[key] = when;
                        break;

                    case ReconciledEvent:
                        if (p.Length < 5) break;
                        int space = p[4].IndexOf((char)32);
                        var re = new ReconciledEntry
                        {
                            Source = space < 0 ? p[4] : p[4].Substring(0, space), Event = space < 0 ? "" : p[4].Substring(space + 1), WhenUtc = when
                        };
                        ReconciledEntry have2 = null;
                        if (!view.Reconciled.TryGetValue(key, out have2) || string.CompareOrdinal(ReconciledOrder(re), ReconciledOrder(have2)) > 0) view.Reconciled[key] = re;
                        break;

                    case DoneEvent:
                        DateTime seen = DateTime.MinValue;
                        if (!doneAt.TryGetValue(key, out seen) || when > seen) doneAt[key] = when;
                        break;

                    case IssueEvent:
                        if (p.Length < 5) break;
                        var ie = new IssueEntry { IssueNo = p[4], WhenUtc = when };
                        IssueEntry have = null;
                        if (!issues.TryGetValue(key, out have) || string.CompareOrdinal(IssueOrder(ie), IssueOrder(have)) > 0) issues[key] = ie;
                        break;

                    case DuplicateEvent:
                        MarkerStatus st = MarkerStatus.New;
                        if (p.Length >= 9 && MarkerStatusText.TryParse(p[7], out st))
                            view.Duplicates.Add(new DuplicateEntry
                            {
                                Key = key, WhenUtc = when, By = by, Note = p[4], KeptIssueNo = p[5], RemovedIssueNo = p[6], Status = st, AssignedTo = p[8]
                            });
                        break;
                }
            }

            foreach (var kv in assigned) view.Assigned[kv.Key] = kv.Value;
            foreach (var kv in issues) view.Issues[kv.Key] = kv.Value;
            foreach (var kv in pendings)
            {
                DateTime closed = DateTime.MinValue;
                bool hasDone = doneAt.TryGetValue(kv.Key, out closed);
                var open = kv.Value.Where(x => !hasDone || x.WhenUtc > closed).OrderBy(x => PendingOrder(x), StringComparer.Ordinal).ToList();
                if (open.Count > 0) view.Pending[kv.Key] = MergeOpen(open);
            }
            var ordered = view.Duplicates.OrderBy(d => d.WhenUtc).ThenBy(d => d.Key, StringComparer.Ordinal).ThenBy(d => d.RemovedIssueNo, StringComparer.Ordinal)
                .ThenBy(d => d.KeptIssueNo, StringComparer.Ordinal).ThenBy(d => d.Status).ThenBy(d => d.Note, StringComparer.Ordinal).ThenBy(d => d.AssignedTo, StringComparer.Ordinal).ThenBy(d => d.By, StringComparer.Ordinal).ToList();
            view.Duplicates.Clear();
            view.Duplicates.AddRange(ordered);
            return view;
        }

        /// <summary>
        /// The open entries of one key, oldest first, as ONE: the status, the note and the assignee each come from the latest entry that
        /// set that field (so a status followed by a reason keeps both, and a later unassign beats an earlier assignee); the Seq, the author and
        /// the time are the latest entry's, so a PendingDone timed after it closes every part.
        /// </summary>
        private static PendingEntry MergeOpen(List<PendingEntry> ascending)
        {
            var last = ascending[ascending.Count - 1];
            var merged = new PendingEntry { SeqAtPending = last.SeqAtPending, By = last.By, WhenUtc = last.WhenUtc };
            foreach (var e in ascending)
            {
                if (e.Status.HasValue) { merged.Status = e.Status; merged.StatusSeq = e.StatusSeq; }
                if (e.Note != null) { merged.Note = e.Note; merged.NoteSeq = e.NoteSeq; }
                if (e.AssignedTo != null) { merged.AssignedTo = e.AssignedTo; merged.AssignedSeq = e.AssignedSeq; }
            }
            return merged;
        }

        private static bool TryPending(string[] p, string by, DateTime when, out PendingEntry entry)
        {
            entry = null;
            long seq;
            if (!long.TryParse(p[5], NumberStyles.Integer, Inv, out seq)) return false;
            string flags = p[6];
            MarkerStatus? status = null;
            if (Has(flags, (char)83))
            {
                MarkerStatus s;
                if (!MarkerStatusText.TryParse(p[7], out s)) return false;
                status = s;
            }
            entry = new PendingEntry
            {
                SeqAtPending = seq, Status = status, Note = Has(flags, (char)78) ? p[4] : null, AssignedTo = Has(flags, (char)65) ? p[8] : null, By = by, WhenUtc = when
            };
            // Each field remembers the marker Seq it was set at, so a later entry cannot carry an older field forward as if it were current (D-L4).
            if (entry.Status.HasValue) entry.StatusSeq = seq;
            if (entry.Note != null) entry.NoteSeq = seq;
            if (entry.AssignedTo != null) entry.AssignedSeq = seq;
            return true;
        }

        private static bool Has(string flags, char flag)
        {
            foreach (var c in flags) if (c == flag) return true;
            return false;
        }

        // A total order, so two lines with the same clock still give one answer whatever the file order. The clock and the Seq are fixed-width
        // (20 digits), a status is one of a fixed set of words none of which starts another, and every free text is written as its length and then itself,
        // so two different field lists never give one text (a note, an assignee or a Revit user name can hold any character, a pipe included).
        private static string AssignedOrder(AssignedEntry e)
        {
            return e.WhenUtc.Ticks.ToString("D20", Inv) + Len(e.Assignee) + Len(e.By);
        }

        private static string ReconciledOrder(ReconciledEntry e)
        {
            return e.WhenUtc.Ticks.ToString("D20", Inv) + Len(e.Source) + Len(e.Event);
        }

        private static string IssueOrder(IssueEntry e)
        {
            return e.WhenUtc.Ticks.ToString("D20", Inv) + e.IssueNo;
        }

        private static string Len(string text)
        {
            text = text ?? "";
            return text.Length.ToString("D10", Inv) + text;
        }

        private static string PendingOrder(PendingEntry e)
        {
            return e.WhenUtc.Ticks.ToString("D20", Inv) + e.SeqAtPending.ToString("D20", Inv) + (e.Status.HasValue ? MarkerStatusText.Format(e.Status.Value) : "~")
                   + (e.Note == null ? "~" : Len(e.Note)) + (e.AssignedTo == null ? "~" : Len(e.AssignedTo)) + Len(e.By);
        }
    }
}
