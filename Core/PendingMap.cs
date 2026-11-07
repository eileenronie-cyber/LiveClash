using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// The join between the ledger and the plan for a pending-to-model triage (spec 6.7). The ledger keys its events by the
    /// clash key (ElementIds), the plan by the UniqueId triple; this maps the one to the other and writes the ledger lines
    /// that close a pending entry, or record a decision a merge did not keep, from what the plan decided. Pure.
    /// </summary>
    public static class PendingMap
    {
        public sealed class Mapped
        {
            public List<MarkerPlan.Pending> Pending { get; } = new List<MarkerPlan.Pending>();
            /// <summary>Open pending entries whose key has no marker (gone or unreadable): to be closed.</summary>
            public List<string> UnmatchedKeys { get; } = new List<string>();
        }

        /// <summary>The open pending entries of the ledger as plan pendings, by triple.</summary>
        public static Mapped Build(LedgerExtras.View extras, IDictionary<string, string> keyToTriple)
        {
            var mapped = new Mapped();
            if (extras == null) return mapped;
            foreach (var kv in extras.Pending.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string triple = null;
                if (keyToTriple == null || !keyToTriple.TryGetValue(kv.Key, out triple) || string.IsNullOrEmpty(triple)) { mapped.UnmatchedKeys.Add(kv.Key); continue; }
                var p = kv.Value;
                mapped.Pending.Add(new MarkerPlan.Pending
                {
                    Triple = triple, SeqAtPending = p.SeqAtPending, StatusSeq = p.StatusSeq, NoteSeq = p.NoteSeq, AssignedSeq = p.AssignedSeq,
                    Status = p.Status, Note = p.Note, AssignedTo = p.AssignedTo, By = p.By
                });
            }
            return mapped;
        }

        private static string DoneFor(string key, LedgerExtras.View extras, string by, DateTime nowUtc)
        {
            LedgerExtras.PendingEntry open = null;
            if (extras != null) extras.Pending.TryGetValue(key, out open);
            return LedgerExtras.DoneLine(key, MarkerReconcile.After(nowUtc, open == null ? DateTime.MinValue : open.WhenUtc), by);
        }

        /// <summary>A PendingDone line for every outcome that was Applied or Dropped; none for Kept.</summary>
        public static List<string> DoneLines(IEnumerable<MarkerPlan.PendingOutcome> outcomes, IDictionary<string, string> tripleToKey,
            LedgerExtras.View extras, string by, DateTime nowUtc)
        {
            var lines = new List<string>();
            if (outcomes == null || tripleToKey == null) return lines;
            foreach (var o in outcomes.Where(x => x != null && x.Kind != PendingKind.Kept && x.Triple != null).OrderBy(x => x.Triple, StringComparer.Ordinal))
            {
                string key;
                if (tripleToKey.TryGetValue(o.Triple, out key) && !string.IsNullOrEmpty(key)) lines.Add(DoneFor(key, extras, by, nowUtc));
            }
            return lines;
        }

        /// <summary>PendingDone lines for open entries whose key matched no marker.</summary>
        public static List<string> UnmatchedDoneLines(IEnumerable<string> keys, LedgerExtras.View extras, string by, DateTime nowUtc)
        {
            var lines = new List<string>();
            if (keys == null) return lines;
            foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
                lines.Add(DoneFor(key, extras, by, nowUtc));
            return lines;
        }

        /// <summary>A DuplicateNotKept history line for each decision a merge did not keep, under the kept marker's ledger key.</summary>
        public static List<string> DuplicateLines(IEnumerable<MarkerPlan.LostDecision> lost, IDictionary<string, string> markerUidToKey, string by, DateTime nowUtc)
        {
            var lines = new List<string>();
            if (lost == null || markerUidToKey == null) return lines;
            foreach (var l in lost.Where(x => x != null && x.KeptUid != null).OrderBy(x => x.KeptUid, StringComparer.Ordinal).ThenBy(x => x.RemovedUid, StringComparer.Ordinal))
            {
                string key;
                if (markerUidToKey.TryGetValue(l.KeptUid, out key) && !string.IsNullOrEmpty(key))
                    lines.Add(LedgerExtras.DuplicateLine(key, nowUtc, by, l.KeptIssueNo, l.RemovedIssueNo, l.Status, l.Note, l.AssignedTo));
            }
            return lines;
        }
    }
}
