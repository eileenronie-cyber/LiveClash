using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// Marker to ledger (spec 6.3): in a model that has markers, the marker record is the truth for the shared
    /// fields, and the local ledger follows it, append-only. Pure: it takes what the markers say and what the
    /// ledger holds and returns the lines to append. A key with an open pending-to-model entry is left alone.
    ///
    /// Mapping (D27, D28): Pending, Coordinated and Ignored as they are; Reopened is Pending; New with no note is
    /// "no decision"; a Resolved marker over a ledger decision that is not yet Gone appends a Gone line. Every
    /// decision line is exactly today's layout (the shipped reader's field-count rules make any other count unsafe,
    /// D29 revised); that it came from the model is its own <see cref="LedgerExtras.ReconciledEvent"/> line, written
    /// right after it. Each line is timed at this PC's now, but never earlier than one tick after the latest event
    /// it has to outrank for that key (D30), so a second reconcile finds nothing to do.
    /// </summary>
    public static class MarkerReconcile
    {
        public sealed class ModelMarker
        {
            public string Key { get; set; }
            public MarkerStatus Status { get; set; }
            public string Note { get; set; }
            public string AssignedTo { get; set; }
            public string ChangedBy { get; set; }
            public string ReportedBy { get; set; }
            public string IssueNo { get; set; }
        }

        public sealed class Result
        {
            /// <summary>The lines to append to the ledger, in order: each decision line followed by its Reconciled line.</summary>
            public List<string> Lines { get; } = new List<string>();
            /// <summary>Only the decision lines (a status, Gone, Assigned, Issue): what the counts and the summary are about.</summary>
            public List<string> Decisions { get; } = new List<string>();
            /// <summary>Keys where the ledger held a decision and the marker's differed: the model's was kept ("3 of your decisions differ from the model").</summary>
            public int Overridden { get; set; }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="markers">One entry per marker, by its current ledger key.</param>
        /// <param name="decisions">The decisions in force, from <see cref="ClashStatusReplay"/>.</param>
        /// <param name="extras">The new events, from <see cref="LedgerExtras.Read"/>.</param>
        /// <param name="nowUtc">This PC's clock; the only clock used.</param>
        public static Result Plan(IEnumerable<ModelMarker> markers, IDictionary<string, ClashStatusEntry> decisions, LedgerExtras.View extras, DateTime nowUtc)
        {
            var result = new Result();
            if (markers == null) return result;
            decisions = decisions ?? new Dictionary<string, ClashStatusEntry>();
            extras = extras ?? new LedgerExtras.View();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in markers.Where(x => x != null && !string.IsNullOrEmpty(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!seen.Add(m.Key) || extras.Pending.ContainsKey(m.Key)) continue;
                string by = string.IsNullOrEmpty(m.ChangedBy) ? m.ReportedBy ?? "" : m.ChangedBy;
                ClashStatusEntry e;
                decisions.TryGetValue(m.Key, out e);

                Status(result, m, e, extras, by, nowUtc);
                Assignee(result, m, extras, by, nowUtc);
                Issue(result, m, extras, by, nowUtc);
            }
            return result;
        }

        /// <summary>The ledger kind for a marker status: Reopened is Pending (D28). Resolved has no kind of its own (it is a Gone line).</summary>
        internal static ClashStatusKind KindOf(MarkerStatus status)
        {
            switch (status)
            {
                case MarkerStatus.Pending:
                case MarkerStatus.Reopened: return ClashStatusKind.Pending;
                case MarkerStatus.Coordinated: return ClashStatusKind.Coordinated;
                case MarkerStatus.Ignored: return ClashStatusKind.Ignored;
                default: return ClashStatusKind.New;
            }
        }

        /// <summary>This PC's now, but one tick after <paramref name="floor"/> when that is later (D30).</summary>
        internal static DateTime After(DateTime now, DateTime floor)
        {
            return floor >= now ? floor.AddTicks(1) : now;
        }

        /// <summary>Appends a decision line and, right after it, the Reconciled line that says the model is where it came from.</summary>
        private static void Emit(Result result, string key, string ev, DateTime when, string by, string decisionLine)
        {
            result.Decisions.Add(decisionLine);
            result.Lines.Add(decisionLine);
            result.Lines.Add(LedgerExtras.ReconciledLine(key, when, by, LedgerExtras.ModelTag, ev));
        }

        private static void Status(Result result, ModelMarker m, ClashStatusEntry e, LedgerExtras.View extras, string by, DateTime now)
        {
            DateTime floor = e == null ? DateTime.MinValue : (e.Gone && e.GoneUtc > e.WhenUtc ? e.GoneUtc : e.WhenUtc);
            DateTime orphan;
            if (extras.GoneAt.TryGetValue(m.Key, out orphan) && orphan > floor) floor = orphan;
            if (m.Status == MarkerStatus.Resolved)
            {
                if (e != null && !e.Gone)
                {
                    var when = After(now, floor);
                    Emit(result, m.Key, ClashStatusReplay.GoneEvent, when, by,
                        string.Join("\t", new[] { m.Key, ClashStatusReplay.GoneEvent, when.ToString("o", Inv), Clean(by), "" }));
                }
                return;
            }

            var kind = KindOf(m.Status);
            string note = m.Note ?? "";
            bool wantsNone = kind == ClashStatusKind.New && note.Length == 0;
            bool differs = wantsNone ? e != null : e == null || e.Kind != kind || (e.Note ?? "") != note;
            bool reopen = m.Status == MarkerStatus.Reopened && e != null && e.Gone;
            if (!differs && !reopen) return;

            if (differs && e != null) result.Overridden++;
            var at = After(now, floor);
            Emit(result, m.Key, kind.ToString(), at, by, string.Join("\t", new[] { m.Key, kind.ToString(), at.ToString("o", Inv), Clean(by), Clean(note) }));
        }

        private static void Assignee(Result result, ModelMarker m, LedgerExtras.View extras, string by, DateTime now)
        {
            LedgerExtras.AssignedEntry a;
            extras.Assigned.TryGetValue(m.Key, out a);
            string have = a == null ? "" : a.Assignee ?? "";
            string want = m.AssignedTo ?? "";
            if (have == want) return;
            var when = After(now, a == null ? DateTime.MinValue : a.WhenUtc);
            Emit(result, m.Key, LedgerExtras.AssignedEvent, when, by, LedgerExtras.AssignedLine(m.Key, when, by, want));
        }

        private static void Issue(Result result, ModelMarker m, LedgerExtras.View extras, string by, DateTime now)
        {
            string want = m.IssueNo ?? "";
            if (want.Length == 0) return;
            LedgerExtras.IssueEntry i;
            extras.Issues.TryGetValue(m.Key, out i);
            if (i != null && i.IssueNo == want) return;
            var when = After(now, i == null ? DateTime.MinValue : i.WhenUtc);
            Emit(result, m.Key, LedgerExtras.IssueEvent, when, by, LedgerExtras.IssueLine(m.Key, when, by, want));
        }

        private static string Clean(string s)
        {
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
