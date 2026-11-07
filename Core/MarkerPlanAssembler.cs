using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// From what the Revit side collected to the plan's input, and from the plan's outcome to the ledger lines that follow it
    /// (spec 6.2, 6.3, 6.7, 6.10). Pure: the Revit collector hands over plain data and gets plain data back, so everything that
    /// decides anything is checked headless. <see cref="Assemble"/> turns clashes, the ledger, the markers and the counters into
    /// <see cref="MarkerPlan.Input"/> through <see cref="RowBuilder"/> and <see cref="PendingMap"/>. <see cref="Lines"/> turns the
    /// plan, and what Apply actually did, into every ledger line to append afterwards: the triage that was closed, the decisions a
    /// merge did not keep, and the lines that make the ledger follow the markers as they now stand (<see cref="MarkerReconcile"/>).
    /// Neither throws: null and empty inputs give an empty result.
    /// </summary>
    public static class MarkerPlanAssembler
    {
        private static readonly StringComparer Ord = StringComparer.Ordinal;

        /// <summary>One clash as the Revit side saw it. <see cref="Live"/> is false for a decided pair that is not live on this seat (its Source is built by the caller).</summary>
        public sealed class Clash
        {
            public RowBuilder.Source Source { get; set; }
            /// <summary>The re-test under the marker's own rule; used only when a readable marker has the same triple.</summary>
            public RowBuilder.Retest Retest { get; set; }
            public bool Live { get; set; } = true;
        }

        public sealed class Request
        {
            public List<Clash> Clashes { get; set; } = new List<Clash>();
            /// <summary>The decisions in force, by ledger key (from <see cref="ClashStatusReplay"/>).</summary>
            public IDictionary<string, ClashStatusEntry> Decisions { get; set; }
            /// <summary>The new events (from <see cref="LedgerExtras.Read"/>).</summary>
            public LedgerExtras.View Extras { get; set; }
            public List<MarkerPlan.Existing> Markers { get; set; } = new List<MarkerPlan.Existing>();
            public IssueNumbers.Counter OwnCounter { get; set; }
            public List<IssueNumbers.Counter> AllCounters { get; set; } = new List<IssueNumbers.Counter>();
            public MarkerPlan.Options Options { get; set; }
        }

        public sealed class Assembly
        {
            public MarkerPlan.Input Input { get; set; } = new MarkerPlan.Input();
            /// <summary>Open pending entries whose key matched no marker and no row: to be closed.</summary>
            public List<string> UnmatchedPendingKeys { get; set; } = new List<string>();
            /// <summary>The open pending entries that matched, by ledger key, to their triple.</summary>
            public Dictionary<string, string> PendingKeyToTriple { get; set; } = new Dictionary<string, string>(Ord);
            public Request Request { get; set; }
        }

        /// <summary>What Apply did, as far as the ledger is concerned.</summary>
        public sealed class Outcome
        {
            /// <summary>Refused or rolled back: nothing happened, so no line is written.</summary>
            public bool Failed { get; set; }
            /// <summary>Marker UniqueIds Apply passed over at the last moment (their change or removal was not written).</summary>
            public HashSet<string> NotWritten { get; set; } = new HashSet<string>(Ord);
            /// <summary>Apply made the first this-many creations; -1 means all of them.</summary>
            public int CreationsMade { get; set; } = -1;
        }

        public sealed class Written
        {
            /// <summary>Every line to append, in order.</summary>
            public List<string> Lines { get; set; } = new List<string>();
            public int PendingClosed { get; set; }
            public int DuplicatesNoted { get; set; }
            public int ReconcileDecisions { get; set; }
            /// <summary>Keys where the ledger held a decision and the marker's differed: the model's was kept.</summary>
            public int Overridden { get; set; }
            /// <summary>Sentences for the Refresh summary, in order: the decisions that differ from the model, then the triage that was not applied. Empty when there is nothing to say.</summary>
            public List<string> Notes { get; set; } = new List<string>();
            /// <summary>The same triage reasons as counted rows, grouped by their text.</summary>
            public List<MarkerSummary.Reason> PendingNotApplied { get; set; } = new List<MarkerSummary.Reason>();
        }

        private const string KeptText = "Your pending triage waits for the marker to be free.";
        private const string DroppedText = "Your pending triage was dropped.";
        private const string NotWrittenText = "Your pending triage was not written: the marker was passed over at the last moment, so it stays open and is tried again.";

        private static string TripleOf(MarkerRecord record)
        {
            return ClashIdOf.Triple(record.HostUid, record.LinkUid, record.OtherUid);
        }

        // ------------------------------------------------------------------ assemble

        public static Assembly Assemble(Request request)
        {
            var assembly = new Assembly { Request = request };
            if (request == null) return assembly;

            var markers = (request.Markers ?? new List<MarkerPlan.Existing>()).Where(m => m != null && m.MarkerUid != null).ToList();
            var extras = request.Extras ?? new LedgerExtras.View();
            var readable = new HashSet<string>(markers.Where(m => m.Record != null).Select(m => TripleOf(m.Record)).Where(t => t.Length > 0), Ord);

            // The ledger keys a pending triage was written under: the marker records' keys, then the rows' (the current key wins).
            var keyToTriple = new Dictionary<string, string>(Ord);
            foreach (var m in markers.Where(m => m.Record != null).OrderBy(m => m.MarkerUid, Ord))
            {
                string t = TripleOf(m.Record);
                if (t.Length > 0 && !string.IsNullOrEmpty(m.Record.Key)) keyToTriple[m.Record.Key] = t;
            }

            var rows = new List<MarkerPlan.Row>();
            var clashes = (request.Clashes ?? new List<Clash>()).Where(c => c != null && c.Source != null)
                .Select(c => new { Clash = c, Triple = ClashIdOf.Triple(c.Source.HostUid, c.Source.LinkUid, c.Source.OtherUid) })
                .OrderBy(x => x.Clash.Source.Key ?? "", Ord).ThenBy(x => x.Triple, Ord).ToList();
            foreach (var x in clashes)
            {
                var source = x.Clash.Source;
                bool live = x.Clash.Live;
                var retest = live && x.Triple.Length > 0 && readable.Contains(x.Triple) ? x.Clash.Retest : null;
                rows.Add(RowBuilder.Build(source, retest, DecisionFor(source.Key, request.Decisions, extras), live));
                if (x.Triple.Length > 0 && !string.IsNullOrEmpty(source.Key)) keyToTriple[source.Key] = x.Triple;
            }

            var mapped = PendingMap.Build(extras, keyToTriple);
            foreach (var key in extras.Pending.Keys)
            {
                string triple;
                if (keyToTriple.TryGetValue(key, out triple)) assembly.PendingKeyToTriple[key] = triple;
            }
            assembly.UnmatchedPendingKeys = mapped.UnmatchedKeys;
            assembly.Input = new MarkerPlan.Input
            {
                Rows = rows, Markers = markers, Pending = mapped.Pending, Counter = request.OwnCounter,
                AllCounters = (request.AllCounters ?? new List<IssueNumbers.Counter>()).Where(c => c != null).ToList(),
                Options = request.Options ?? new MarkerPlan.Options()
            };
            return assembly;
        }

        private static RowBuilder.Decision DecisionFor(string key, IDictionary<string, ClashStatusEntry> decisions, LedgerExtras.View extras)
        {
            if (string.IsNullOrEmpty(key)) return null;
            ClashStatusEntry entry = null;
            if (decisions != null) decisions.TryGetValue(key, out entry);
            LedgerExtras.AssignedEntry assigned;
            extras.Assigned.TryGetValue(key, out assigned);
            LedgerExtras.IssueEntry issue;
            extras.Issues.TryGetValue(key, out issue);
            return entry == null && assigned == null && issue == null ? null : RowBuilder.DecisionOf(entry, assigned, issue);
        }

        // ------------------------------------------------------------------ lines

        public static Written Lines(Assembly assembly, MarkerPlan.Result plan, Outcome outcome, string by, DateTime nowUtc)
        {
            var written = new Written();
            if (assembly == null || plan == null) return written;
            outcome = outcome ?? new Outcome();
            if (outcome.Failed) return written;
            var notWritten = outcome.NotWritten ?? new HashSet<string>(Ord);
            var request = assembly.Request;
            var extras = request != null && request.Extras != null ? request.Extras : new LedgerExtras.View();
            var decisions = request != null && request.Decisions != null ? request.Decisions : new Dictionary<string, ClashStatusEntry>(Ord);

            // 1. Triage the plan applied (and wrote) or dropped, closed under the key each open entry has.
            var closed = new HashSet<string>(Ord);
            foreach (var o in plan.Pending.Where(p => p != null && p.Triple != null).OrderBy(p => p.Triple, Ord))
            {
                bool done = o.Kind == PendingKind.Dropped || (o.Kind == PendingKind.Applied && !(o.MarkerUid != null && notWritten.Contains(o.MarkerUid)));
                if (!done) continue;
                foreach (var kv in assembly.PendingKeyToTriple.Where(k => k.Value == o.Triple).OrderBy(k => k.Key, Ord))
                {
                    if (!closed.Add(kv.Key)) continue;
                    written.Lines.AddRange(PendingMap.DoneLines(new[] { o }, new Dictionary<string, string>(Ord) { { o.Triple, kv.Key } }, extras, by, nowUtc));
                }
            }

            // 2. Open entries whose key matched no marker and no row. A marker whose record has a Key but an empty triple (the reader accepts empty UniqueIds) is not in
            // the key map either, so its key can be among them: closing it here also lets the reconcile below write that marker, instead of one Refresh later (DR3-L1).
            foreach (var key in assembly.UnmatchedPendingKeys) closed.Add(key);
            written.Lines.AddRange(PendingMap.UnmatchedDoneLines(assembly.UnmatchedPendingKeys, extras, by, nowUtc));
            written.PendingClosed = written.Lines.Count;

            // 3. The markers as they stand after the write: the model the ledger follows.
            var post = PostState(assembly.Input, plan, notWritten, outcome.CreationsMade);

            // 4. Decisions a merge did not keep, under the kept marker's key, when both halves of the merge were written.
            var keyOf = new Dictionary<string, string>(Ord);
            foreach (var m in post.Where(m => m.Record != null && !string.IsNullOrEmpty(m.Record.Key))) keyOf[m.MarkerUid] = m.Record.Key;
            var lost = plan.LostDecisions.Where(l => l != null && !notWritten.Contains(l.KeptUid) && !notWritten.Contains(l.RemovedUid));
            var duplicates = PendingMap.DuplicateLines(lost, keyOf, by, nowUtc);
            written.Lines.AddRange(duplicates);
            written.DuplicatesNoted = duplicates.Count;

            // 5. The ledger follows the model. A pending entry that was just closed no longer holds its key back.
            var models = post.Where(m => m.Record != null && !string.IsNullOrEmpty(m.Record.Key)).OrderBy(m => m.MarkerUid, Ord).Select(m => new MarkerReconcile.ModelMarker
            {
                Key = m.Record.Key, Status = m.Record.Status, Note = m.Record.Note, AssignedTo = m.Record.AssignedTo,
                ChangedBy = m.Record.ChangedBy, ReportedBy = m.Record.ReportedBy, IssueNo = m.Record.IssueNo
            }).ToList();
            var reconciled = MarkerReconcile.Plan(models, decisions, WithoutPending(extras, closed), nowUtc);
            written.Lines.AddRange(reconciled.Lines);
            written.ReconcileDecisions = reconciled.Decisions.Count;
            written.Overridden = reconciled.Overridden;

            // 6. What the summary must say: never silent (C4).
            written.PendingNotApplied = NotApplied(plan, notWritten);
            if (written.Overridden > 0)
                written.Notes.Add(written.Overridden.ToString(CultureInfo.InvariantCulture) + (written.Overridden == 1 ? " of your decisions differs" : " of your decisions differ") + " from the model; the model's value was kept.");
            foreach (var r in written.PendingNotApplied) written.Notes.Add(r.Count == 1 ? r.Text : r.Text + " (" + r.Count.ToString(CultureInfo.InvariantCulture) + ")");
            return written;
        }

        /// <summary>The triage that was not applied, one row per reason in ordinal order: dropped (the plan's message), kept for the holder (the plan's message), or applied but not written at the last moment.</summary>
        private static List<MarkerSummary.Reason> NotApplied(MarkerPlan.Result plan, HashSet<string> notWritten)
        {
            var counts = new Dictionary<string, int>(Ord);
            foreach (var o in plan.Pending.Where(p => p != null))
            {
                string text;
                if (o.Kind == PendingKind.Applied) text = o.MarkerUid != null && notWritten.Contains(o.MarkerUid) ? NotWrittenText : o.NotApplied != null && o.NotApplied.Count > 0 ? o.Message : null;
                else text = string.IsNullOrEmpty(o.Message) ? (o.Kind == PendingKind.Kept ? KeptText : DroppedText) : o.Message;
                if (text == null) continue;
                int n;
                counts[text] = counts.TryGetValue(text, out n) ? n + 1 : 1;
            }
            return counts.OrderBy(kv => kv.Key, Ord).Select(kv => new MarkerSummary.Reason { Text = kv.Key, Count = kv.Value }).ToList();
        }

        /// <summary>The markers after Apply did what the outcome says: removals and changes that were written, then the creations that were made.</summary>
        private static List<MarkerPlan.Existing> PostState(MarkerPlan.Input input, MarkerPlan.Result plan, HashSet<string> notWritten, int creationsMade)
        {
            var removed = new HashSet<string>(plan.Removals.Where(r => r != null && r.MarkerUid != null && !notWritten.Contains(r.MarkerUid)).Select(r => r.MarkerUid), Ord);
            var changes = new Dictionary<string, MarkerPlan.Change>(Ord);
            foreach (var c in plan.Changes.Where(c => c != null && c.MarkerUid != null && c.After != null && !notWritten.Contains(c.MarkerUid))) changes[c.MarkerUid] = c;

            var post = new List<MarkerPlan.Existing>();
            foreach (var m in (input == null || input.Markers == null ? new List<MarkerPlan.Existing>() : input.Markers).Where(m => m != null && m.MarkerUid != null && !removed.Contains(m.MarkerUid)))
            {
                MarkerPlan.Change change;
                post.Add(changes.TryGetValue(m.MarkerUid, out change) ? new MarkerPlan.Existing { MarkerUid = m.MarkerUid, Record = change.After } : m);
            }
            int made = creationsMade < 0 ? plan.Creations.Count : Math.Min(creationsMade, plan.Creations.Count);
            foreach (var c in plan.Creations.Where(c => c != null && c.Record != null).Take(made))
                // Stryker disable once String : the prefix only keeps a created marker's id apart from a real UniqueId; the id is used for nothing else
                post.Add(new MarkerPlan.Existing { MarkerUid = "new-" + c.Triple, Record = c.Record });
            return post;
        }

        private static LedgerExtras.View WithoutPending(LedgerExtras.View extras, HashSet<string> closed)
        {
            var view = new LedgerExtras.View();
            foreach (var kv in extras.Assigned) view.Assigned[kv.Key] = kv.Value;
            foreach (var kv in extras.Issues) view.Issues[kv.Key] = kv.Value;
            foreach (var kv in extras.Reconciled) view.Reconciled[kv.Key] = kv.Value;
            foreach (var kv in extras.GoneAt) view.GoneAt[kv.Key] = kv.Value;
            foreach (var kv in extras.Pending.Where(p => !closed.Contains(p.Key))) view.Pending[kv.Key] = kv.Value;
            return view;   // the duplicates are history the reconcile never reads
        }
    }
}
