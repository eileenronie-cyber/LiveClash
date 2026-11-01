using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The live trigger.
    ///
    /// Revit calls Execute() from inside the transaction that changed the model,
    /// on the main thread, before the change is committed. That gives genuinely
    /// live feedback, but it comes with hard rules:
    ///
    ///   - Never throw. An exception here gets the updater disabled, and Revit
    ///     shows the user an alarming dialog about a failing add-in.
    ///   - Never open a transaction. One is already open.
    ///   - Never show UI. Modal dialogs inside a transaction are how you deadlock Revit.
    ///   - Be fast. Every millisecond spent here is a millisecond of frozen cursor,
    ///     which is why there is a time budget and a deferred queue.
    ///
    /// This updater only reads. It does not modify the model at all, which also
    /// means it never dirties the document or triggers a second update cycle.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
        Justification = "One instance per Revit process, registered with Revit for its whole life; Revit owns the UpdaterId's lifetime.")]
    public class ClashUpdater : IUpdater
    {
        // Must stay constant across sessions — Revit persists updater ids in the model.
        public static readonly Guid UpdaterGuid = new Guid("8d41b2c6-7e59-4a03-b6f1-2c9d5e70a184");

        private readonly UpdaterId _id;

        public ClashUpdater(AddInId addInId)
        {
            _id = new UpdaterId(addInId, UpdaterGuid);
        }

        public UpdaterId GetUpdaterId() { return _id; }

        // Late in the order, so we see geometry after Revit has finished
        // regenerating connectors, joins and fittings.
        public ChangePriority GetChangePriority() { return ChangePriority.FreeStandingComponents; }

        public string GetUpdaterName() { return "Live Clash"; }

        public string GetAdditionalInformation()
        {
            return "Checks changed elements against the current model and all loaded links, live while you model.";
        }

        public void Execute(UpdaterData data)
        {
            // Rows taken out of the store for the re-check below. A throw before they are put back must
            // not lose them (review 2026-10-31, LOW-1): the catch restores any that are not listed.
            List<ClashResult> takenOut = null;

            try
            {
                // INSIDE the try: the getter reads and parses the settings file, and the updater must never
                // throw (delta review 2026-10-31, DL-j). An unreadable file means this edit is not checked, logged
                // below; it must never be an exception into Revit's transaction.
                var settings = ClashSettings.Current;
                if (!settings.Enabled) return;

                var doc = data.GetDocument();
                if (doc == null || !doc.IsValidObject) return;

                // Only the session's own document. The store holds ITS rows, and RemoveForElement matches a
                // bare id, so an edit in a family being edited or in another open project could take rows
                // whose ids merely collide (review 2026-10-31, H1).
                if (!LiveClashApp.IsSessionDocument(doc)) return;

                // Deleted elements: RECORD everything they were involved in, then
                // forget it. Forgetting alone is what this used to do, and it lost
                // the most common way a clash actually gets fixed.
                //
                // Deleting the offending element resolves the clash. The rows left
                // the panel and nothing reached the resolved record, so the fix was
                // invisible five minutes later — no entry, no history, nothing to
                // show a coordinator. Verified on PRJ01 on 2026-09-03: a duplicated
                // pipe was deleted, two rows vanished, the resolved ledger stayed on
                // its previous entry from two days earlier.
                //
                // PairVerifier has always understood this case —
                //     if (host == null || !host.IsValidObject) return Verdict.Resolved;
                // — but the deletion path never asked it. Nothing needs to ask now
                // either: the element is gone, which IS the verdict. There is no
                // geometry left to measure and no doubt to resolve.
                //
                // QUEUED, not written. We are inside the transaction here and it has
                // not committed yet — see FlushDeletedResolutions.
                var deleted = data.GetDeletedElementIds();
                QueueDeleted(doc, deleted);

                var changed = new List<ElementId>();
                changed.AddRange(data.GetAddedElementIds());
                changed.AddRange(data.GetModifiedElementIds());

                // Keep the cached spatial index in step with what just changed —
                // BEFORE checking, so this pass sees the new geometry rather than
                // whatever the element looked like a moment ago. Patching costs
                // microseconds; rebuilding costs ~330 ms and blows the time budget.
                ClashContext.NoteChanged(doc, changed, deleted, settings);

                if (changed.Count == 0) return;

                // Clear stale hits for the changed elements before re-checking them,
                // otherwise a resolved clash stays on the list forever.
                //
                // RemoveForElement finds a row by EITHER side (ClashResultStore indexes both),
                // so a row where a changed element is only the TARGET is dropped here too — but
                // the re-check below (CheckElementsAllSets) only ever tests a changed element AS
                // A SOURCE: an element that fails the source category/filter, or is simply never
                // a source at all, is dropped and never re-added, even though nothing about the
                // clash itself resolved — the source element on the other side never moved and
                // nothing re-tests it. Verified on 2026-09-26: nudge a target-only element and a
                // real, still-existing clash silently vanishes from the panel for good.
                //
                // Fix: re-verify those specific rows directly with PairVerifier — the same tool
                // Recheck/auto-verify/full-check already use to answer "does this pair still
                // clash?" — rather than trusting the coarse source-side re-check to cover them.
                // A row whose HOST also changed is NOT left alone: hostChanged below.
                List<ClashResult> hostChanged;
                var targetOnly = SplitRemovedRows(changed, ClashResultStore.Instance.RemoveForElement, out hostChanged);
                takenOut = new List<ClashResult>(targetOnly);
                takenOut.AddRange(hostChanged);
                if (targetOnly.Count > 0) ReverifyTargetOnly(doc, settings, targetOnly);

                // Big edit: don't do it inline. Hand it to idle time.
                if (changed.Count > settings.MaxElementsPerUpdate)
                {
                    // hostChanged cannot wait for the deferred re-check the way the rest of this
                    // edit can: DeferredQueue only ever remembers element ids, not the specific
                    // ClashResult rows their removal orphaned, and it batches ids from possibly
                    // several edits together across several idle ticks — by the time any of
                    // these ids is actually re-checked, there is no way back to which rows were
                    // removed for THIS edit. Reconciled directly here instead, before deferring,
                    // the same way ReverifyTargetOnly already runs unconditionally regardless of
                    // edit size. Found reviewing the whole codebase, 2026-09-27, immediately
                    // after fixing the same-shaped gap for the ordinary (non-deferred) edit size.
                    if (hostChanged.Count > 0) ReverifyHostChanged(doc, settings, hostChanged, "bulk edit");
                    DeferredQueue.Enqueue(doc, changed);
                    return;
                }

                var sw = Stopwatch.StartNew();
                List<ElementId> notChecked;
                var results = ClashEngine.CheckElementsAllSets(doc, changed, settings, sw, out notChecked);

                ClashResultStore.Instance.AddRange(results);

                // Of the rows whose HOST changed, the ones this exact re-check just reproduced
                // need nothing further — they are already back in the store above. Only the ones
                // that did NOT reappear need a real answer: genuinely resolved, or merely
                // unreachable right now (its other half is in a link that happens to be
                // unloaded). See ReverifyHostChanged for why this matters and what it does.
                if (hostChanged.Count > 0)
                {
                    var resultKeys = new HashSet<string>();
                    foreach (var r in results) if (r.Key != null) resultKeys.Add(r.Key);
                    var stillMissing = new List<ClashResult>();
                    foreach (var r in hostChanged)
                        if (r.Key == null || !resultKeys.Contains(r.Key)) stillMissing.Add(r);
                    if (stillMissing.Count > 0) ReverifyHostChanged(doc, settings, stillMissing);
                }

                if (notChecked.Count > 0)
                {
                    DeferredQueue.Enqueue(doc, notChecked);
                    Log.Info("Budget hit (" + sw.ElapsedMilliseconds + " ms); deferred "
                             + notChecked.Count + " element(s).");
                }

                // The reverse: a changed or added element that is a TARGET of a set is checked against the SOURCE
                // elements near it, which the pass above never does (it only tests a changed element AS a source).
                // Without this a cable tray drawn through a pipe, with only pipes as sources, was never listed until a
                // full check (review N1). It runs AFTER everything the forward pass owes (the re-verify and the deferral
                // above) and in its own try: a throw here must not leave those undone (review L2). Shares the same
                // stopwatch; what it cannot finish inside the budget is deferred like a big source edit. A pair it lists
                // is not a fix, so a moved fix queued for the same key a moment ago leaves the queue (review L6).
                try
                {
                    List<ElementId> notCheckedTargets;
                    var targetRows = ClashEngine.CheckTargetsAllSets(doc, changed, settings, sw, false, out notCheckedTargets);
                    ClashResultStore.Instance.AddRange(targetRows);
                    DropPendingMovedForKeys(DocKeyOf(doc), targetRows.Where(r => r != null && r.Key != null).Select(r => r.Key).ToList());
                    if (notCheckedTargets.Count > 0) DeferredQueue.Enqueue(doc, notCheckedTargets);
                }
                catch (Exception ex) { Log.Warn("The reverse check failed; the forward pass is unaffected", ex); }
            }
            catch (Exception ex)
            {
                // Swallow deliberately — see the class comment.
                Log.Error("ClashUpdater.Execute failed", ex);
                RestoreUnlisted(takenOut);
            }
        }

        /// <summary>
        /// Put back any of <paramref name="rows"/> the store no longer lists. A row the re-check had already
        /// shown fixed may come back too; that is the safe direction (the next auto-verify takes it off
        /// again), where losing a real clash is not. Never throws.
        /// </summary>
        internal static void RestoreUnlisted(List<ClashResult> rows)
        {
            if (rows == null || rows.Count == 0) return;
            try
            {
                var listed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in ClashResultStore.Instance.Snapshot()) if (r != null && r.Key != null) listed.Add(r.Key);
                var back = new List<ClashResult>();
                foreach (var r in rows) if (r != null && !string.IsNullOrEmpty(r.Key) && !listed.Contains(r.Key)) back.Add(r);
                if (back.Count > 0)
                {
                    ClashResultStore.Instance.AddRange(back);
                    Log.Warn("Live check failed part-way; " + back.Count + " clash(es) it had taken off the list were put back.", null);
                }
            }
            catch (Exception ex) { Log.Warn("Could not put clashes back after a failed live check", ex); }
        }

        /// <summary>
        /// Of the rows <paramref name="removeForElement"/> drops for each changed id, the ones
        /// whose HOST did NOT also change — a changed element was their TARGET only. Pure: takes
        /// the removal as a delegate so the selection itself (which rows need re-verifying) is
        /// provable with no Document and no real store, in Testing\ClashTargetOnlyChecks.cs.
        /// Thin wrapper over <see cref="SplitRemovedRows"/> so this exact signature, and its own
        /// test, are unaffected by hostChanged also needing the same removal pass.
        /// </summary>
        internal static List<ClashResult> TargetOnlyRows(
            IEnumerable<ElementId> changed, Func<ElementId, List<ClashResult>> removeForElement)
        {
            List<ClashResult> hostChanged;
            return SplitRemovedRows(changed, removeForElement, out hostChanged);
        }

        /// <summary>
        /// Every row a changed element's own removal orphaned, split by whether the changed
        /// element was that row's HOST (its own source side) or only its TARGET — ONE removal
        /// pass for both, since <paramref name="removeForElement"/> drops a row for good the
        /// first time either of its sides is asked about.
        /// </summary>
        internal static List<ClashResult> SplitRemovedRows(
            IEnumerable<ElementId> changed, Func<ElementId, List<ClashResult>> removeForElement,
            out List<ClashResult> hostChanged)
        {
            var targetOnly = new List<ClashResult>();
            hostChanged = new List<ClashResult>();
            if (changed == null || removeForElement == null) return targetOnly;

            var changedIds = new HashSet<long>();
            foreach (var id in changed) changedIds.Add(IdUtil.Value(id));

            foreach (var id in changed)
                foreach (var row in removeForElement(id) ?? new List<ClashResult>())
                {
                    if (row == null) continue;
                    if (changedIds.Contains(IdUtil.Value(row.HostElementId))) hostChanged.Add(row);
                    else targetOnly.Add(row);
                }

            return targetOnly;
        }

        /// <summary>
        /// Rows <see cref="TargetOnlyRows"/> found — RemoveForElement dropped them because a
        /// changed element was their TARGET, not their source — CheckElementsAllSets never
        /// re-tests these, so this is the only thing that stops a still-real clash from silently
        /// vanishing. See the comment where this is called.
        ///
        /// StillClashing and Unknown both put the row back — Unknown most often means a link is
        /// not currently loaded, and dropping a clash because its other half cannot be seen right
        /// now is exactly the failure PairVerifier's own class comment warns against. Only a
        /// confirmed Resolved verdict leaves it off the panel, but QUEUED, like a host-side fix
        /// (<see cref="ReverifyHostChanged"/>): recorded at Save or Sync after PairVerifier has asked
        /// again, and asked again on a rollback or an idle tick, so a move of the row's other element
        /// that is rolled back brings the clash back (second pass 2026-10-31, SP2). Before, a Resolved
        /// row was simply dropped: a rollback had nothing to re-ask, and the fix was never recorded,
        /// since the row is gone from the store and nothing else (auto-verify, Recheck, a full check)
        /// ever sees it. Writes nothing to the ledger here, inside the transaction.
        /// </summary>
        internal static void ReverifyTargetOnly(Document doc, ClashSettings settings, List<ClashResult> rows)
        {
            Dictionary<long, LinkContext> links = null;
            foreach (var r in rows)
            {
                if (r.LinkInstanceId == null) continue;
                links = PairVerifier.LinkLookup(doc);
                break;
            }

            var keep = new List<ClashResult>();
            foreach (var r in rows)
            {
                ClashSeverity sev;
                var verdict = PairVerifier.Verify(doc, r, settings, links, out sev);
                if (verdict == PairVerifier.Verdict.Resolved) { QueueMovedRow(doc, r); continue; }
                if (verdict == PairVerifier.Verdict.StillClashing) r.Severity = sev;
                keep.Add(r);
            }
            if (keep.Count > 0) ClashResultStore.Instance.AddRange(keep);
        }

        /// <summary>
        /// Rows a changed element's removal orphaned where the changed element WAS that row's
        /// HOST, and that this exact pass's own re-check (CheckElementsAllSets, re-testing the
        /// changed element directly) did NOT reproduce.
        ///
        /// Absence here is USUALLY genuine — this is a full, real re-check of the changed
        /// element, not a coarse guess — but a pair whose other half is in a link that happens
        /// to be unloaded right now cannot be reproduced by any re-check that only sees
        /// currently-loaded links, resolved or not: the exact problem TargetOnlyRows/
        /// ReverifyTargetOnly already solved for the other side. PairVerifier tells the two
        /// apart: StillClashing/Unknown restores the row (never lost, just not yet proven
        /// fixed); only a confirmed Resolved gets recorded.
        ///
        /// UNLIKE ReverifyTargetOnly, this DOES feed the resolved ledger — because unlike
        /// that case, nothing else ever gets a turn at this one. The live path is the single
        /// most common way a clash actually gets resolved (editing something to fix it, not
        /// deleting it), and this removal has already taken the row out of the store by the
        /// time Recheck, a Full Check, or the next Auto-Verify idle tick could compare against
        /// it — there is nothing left for any of them to find missing. Found reviewing the
        /// whole codebase, 2026-09-27: the deletion path just below (QueueDeleted /
        /// FlushDeletedResolutions) already got exactly this fix in an earlier round; a live
        /// edit that moves something apart instead of deleting it never did, and a full check
        /// run right afterward showed nothing newly resolved because there was nothing left in
        /// its own "before" snapshot to have noticed the gap.
        ///
        /// CORRECTION 2026-10-31 (review H2): this used to say no deferral was needed, because an
        /// Undo re-checks the element. The re-check put the clash back on the panel, but the
        /// "fixed" entry written here stayed in the ledger, and a ROLLBACK (a cancelled Revit
        /// error, another add-in's TransactionGroup) re-checked nothing at all. So a confirmed fix
        /// is now queued exactly like a deletion and recorded at Save or Sync, re-verified then.
        /// </summary>
        /// <param name="doc">The document the rows belong to.</param>
        /// <param name="settings">The live settings the rows were found under.</param>
        /// <param name="rows">Rows whose HOST element changed and which the re-check did not find again.</param>
        /// <param name="source">What removed them, for the log line ("live edit", "bulk edit", "undo/redo").</param>
        internal static void ReverifyHostChanged(Document doc, ClashSettings settings, List<ClashResult> rows, string source = "live edit")
        {
            Dictionary<long, LinkContext> links = null;
            foreach (var r in rows)
            {
                if (r.LinkInstanceId == null) continue;
                links = PairVerifier.LinkLookup(doc);
                break;
            }

            var restore = new List<ClashResult>();
            var fixedOnes = new List<ClashResult>();
            foreach (var r in rows)
            {
                ClashSeverity sev;
                var verdict = PairVerifier.Verify(doc, r, settings, links, out sev);
                if (verdict == PairVerifier.Verdict.Resolved)
                {
                    fixedOnes.Add(r);
                }
                else
                {
                    if (verdict == PairVerifier.Verdict.StillClashing) r.Severity = sev;
                    restore.Add(r);
                }
            }

            if (restore.Count > 0) ClashResultStore.Instance.AddRange(restore);

            // QUEUED, not written (review 2026-10-31, H2). This runs inside the transaction, before it has
            // held: a rollback (a cancelled error, another add-in's TransactionGroup) or an undo puts the
            // geometry back and would have left a "fixed" entry for a clash that is still there. A move is
            // now a fix the way a deletion is: once it is in the saved model, re-verified at Save or Sync
            // (FlushDeletedResolutions). That also takes the file I/O out of the transaction.
            foreach (var r in fixedOnes) QueueMovedRow(doc, r);

            // Every other path that records a fix logs it; this one did not, so only the ledger file
            // could prove it had run (Roadmap R03). One line per call, only when there was something to say.
            if (rows.Count > 0)
                Log.Info("Re-verified " + rows.Count + " clash(es) after a " + (source ?? "change") + ": "
                         + fixedOnes.Count + " confirmed resolved and queued for the next save, " + restore.Count
                         + " kept (still clashing or not verifiable now).");
        }

        // -------------------------------------------------- deletions, recorded late

        /// <summary>
        /// A fix that has happened but has not yet been shown to survive: a deletion, or (since the
        /// 2026-10-31 review, H2) a move that took the pair apart. For a move, DeletedId is the host.
        /// </summary>
        private sealed class PendingResolution
        {
            public string DocKey;
            public ElementId DeletedId;
            public ClashResult Row;
            public bool Moved;

            /// <summary>A deletion the settings reset kept (DropPendingMoved). Its element is gone, which is a fix under any rules; but if the element comes
            /// back (an undo) it must NOT be re-tested under the new rules and recorded: the list it was found in is gone (delta check, DC-2).</summary>
            public bool RulesReplaced;

            /// <summary>When this fix was last asked again (MinValue = never), so a slice takes the oldest first.</summary>
            public DateTime CheckedUtc;
        }

        /// <summary>
        /// Queue a row a MOVE resolved (both elements still exist), to be re-verified and recorded at
        /// Save or Sync like a deletion. See ReverifyHostChanged for why it is not recorded at once.
        /// </summary>
        internal static void QueueMovedRow(Document doc, ClashResult row)
        {
            if (doc == null || row == null || string.IsNullOrEmpty(row.Key) || row.HostElementId == null) return;

            // A settings re-read has swapped the rules and the idle handler has not reset the results yet: this row was judged Resolved under
            // rules the list was not found under, and the save would record it as fixed with nothing in the model changed (delta review 3, D3-1).
            // The updater's own read of the settings can be the one that swapped them, inside a transaction. The reset clears the list, and the
            // next check lists what the new rules find.
            if (ClashSettings.RulesReloadPending)
            {
                // Said, not silent (delta review 3, DC-6): a genuine move made in the transaction or tick where the swap lands is lost here. No key or
                // name in the line.
                Log.Info("A moved fix was not queued: the settings were just re-read, so the rules changed under it; the next check lists what the new rules find.");
                return;
            }

            lock (PendingGate)
                Pending.Add(new PendingResolution { DocKey = DocKeyOf(doc), DeletedId = row.HostElementId, Row = row, Moved = true });
        }

        /// <summary>
        /// An undo, a redo or a rollback just touched <paramref name="ids"/>. Every queued fix of this
        /// document that names one of them is asked again, now: one that is no longer Resolved goes
        /// back on the panel and leaves the queue (it never was a fix), and one that still is stays
        /// queued for the save.
        ///
        /// This is what brings back the clash of an undone TARGET deletion (review 2026-10-31, M1):
        /// the undo re-queues the target only as a source, which it is not, and the host never moved,
        /// so nothing else would ever re-test the pair. And it is what brings back a clash a rolled-back
        /// move had taken off (H2). Never throws.
        /// </summary>
        /// <returns>How many rows went back on the panel.</returns>
        internal static int ReconcilePending(Document doc, ICollection<ElementId> ids)
        {
            if (doc == null || ids == null || ids.Count == 0) return 0;
            try
            {
                string key = DocKeyOf(doc);
                var touched = new HashSet<long>();
                foreach (var id in ids) if (id != null) touched.Add(IdUtil.Value(id));

                var asked = new List<PendingResolution>();
                lock (PendingGate)
                    foreach (var p in Pending)
                        if (p.DocKey == key && p.Row != null && Names(p, touched)) asked.Add(p);
                return Reconcile(doc, asked);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not reconcile queued fixes after an undo or rollback", ex);
                return 0;
            }
        }

        /// <summary>
        /// Ask up to <paramref name="max"/> of this document's queued fixes again, the ones asked longest ago
        /// first, whatever any event listed. Two callers (review 2026-10-31, H2): the idle verify pass, every
        /// tick, because a TransactionGroup rollback is not shown to raise any event that could put a row back,
        /// and nothing else would; and the document-changed handler on a rollback, where the event may list no ids.
        /// A fix that is no longer Resolved goes back on the panel and leaves the queue. It only ever puts rows
        /// back, never takes one off. Never throws.
        /// </summary>
        /// <returns>How many rows went back on the panel.</returns>
        internal static int ReconcilePendingSlice(Document doc, int max, int budgetMs = 150, string kind = "tick")
        {
            if (doc == null || max <= 0) return 0;
            if (kind == "rollback") _backlogSinceUtc = DateTime.UtcNow;   // what this pass does not reach is the backlog the idle ticks drain
            try
            {
                string key = DocKeyOf(doc);
                List<PendingResolution> all;
                lock (PendingGate)
                    all = Pending.Where(p => p.DocKey == key && p.Row != null)
                                 .OrderBy(p => p.CheckedUtc)
                                 .ToList();
                return Reconcile(doc, all.Take(max).ToList(), all.Count, budgetMs, kind);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not reconcile queued fixes on the idle pass", ex);
                return 0;
            }
        }

        /// <summary>
        /// Remember the ids a COMMITTED transaction deleted, so a later rollback can tell an element a user deleted (a fix) from one whose creation
        /// the rollback undid. Element ids are never reused, so the memory only grows; past its cap the document is marked overflowed and
        /// DropRowsOfVanishedElements drops nothing for it until it closes, because a permanent record must never silently lose a real fix
        /// (recording a rolled-back draw is the older, known behaviour). It is forgotten when the document closes.
        /// </summary>
        internal static void NoteCommittedDeletions(Document doc, ICollection<ElementId> deletedIds)
        {
            if (doc == null || deletedIds == null || deletedIds.Count == 0) return;
            try
            {
                var ids = new List<long>(deletedIds.Count);
                foreach (var id in deletedIds) if (id != null) ids.Add(IdUtil.Value(id));
                if (CommittedDeletions.Shared.Note(DocKeyOf(doc), ids))
                    Log.Info("Too many deleted elements to remember in this session: rolled-back draws are no longer dropped from the list for this model until it is closed.");
            }
            catch (Exception ex) { Log.Warn("Could not remember a committed deletion", ex); }
        }

        /// <summary>Forget what NoteCommittedDeletions remembered for a document that is closing.</summary>
        internal static void ForgetCommittedDeletions(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return;
            CommittedDeletions.Shared.Forget(docKey);
        }

        /// <summary>
        /// A ROLLBACK just ended (it lists no ids, measured): drop, unrecorded and not shown as fixed, every listed row whose host element, or non-link
        /// other element, no longer exists and was NOT deleted by an earlier committed transaction. After a rollback an element can only be gone from the
        /// model because the rollback undid its creation; an element a user deleted before is a fix, and its row is left to the normal route (the deletion
        /// queue, or auto-verify when live checking was paused). For a document whose memory overflowed nothing is dropped. Never throws.
        /// </summary>
        /// <returns>How many rows were dropped.</returns>
        internal static int DropRowsOfVanishedElements(Document doc)
        {
            if (doc == null) return 0;
            try
            {
                string key = DocKeyOf(doc);
                if (CommittedDeletions.Shared.Overflowed(key)) return 0;

                var drop = new List<string>();
                foreach (var r in ClashResultStore.Instance.Snapshot())
                {
                    if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                    if (VanishedByRollback(doc, key, r.HostElementId)
                        || (r.LinkInstanceId == null && VanishedByRollback(doc, key, r.OtherElementId)))
                        drop.Add(r.Key);
                }
                if (drop.Count > 0) ClashResultStore.Instance.RemoveKeys(drop, asResolved: false);   // dropped, not fixed: it must not read as a fix on the panel
                return drop.Count;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not drop the rows of elements a rollback removed", ex);
                return 0;
            }
        }

        private static bool VanishedByRollback(Document doc, string docKey, ElementId id)
        {
            if (id == null) return false;
            if (StillThere(doc, id)) return false;
            return CommittedDeletions.Shared.Knowledge(docKey, IdUtil.Value(id)) == DeletionKnowledge.NotDeleted;
        }
        // When the last rollback pass began. Queued fixes whose CheckedUtc is older than this were not reached by it (the slices take the oldest
        // first), so they are the backlog; it is forgotten once there are none.
        private static DateTime _backlogSinceUtc = DateTime.MinValue;

        /// <summary>
        /// Are there queued fixes of <paramref name="doc"/> that have not been asked again since the last rollback pass (a rollback pass can
        /// stop by time before it reaches all of them)? The idle tick drains them slice by slice (LiveClashApp.DrainReconcileBacklog). Never throws.
        /// </summary>
        internal static bool ReconcileBacklogRemains(Document doc)
        {
            try
            {
                var since = _backlogSinceUtc;
                if (doc == null || since == DateTime.MinValue) return false;
                string key = DocKeyOf(doc);
                bool any;
                lock (PendingGate) any = Pending.Any(p => p.DocKey == key && p.Row != null && p.CheckedUtc < since);
                if (!any) _backlogSinceUtc = DateTime.MinValue;
                return any;
            }
            catch { return false; }
        }

        /// <summary>The shared body: re-verify <paramref name="asked"/>, put back what is not a fix.</summary>
        private static int Reconcile(Document doc, List<PendingResolution> asked, int queued = 0, int budgetMs = int.MaxValue, string kind = null)
        {
            if (asked == null || asked.Count == 0) return 0;
            try
            {
                var now = DateTime.UtcNow;
                var back = new List<ClashResult>();
                var settled = new List<PendingResolution>();
                Dictionary<long, LinkContext> links = null;

                // Each queued fix is asked in turn, oldest first, and the slice stops when its time is spent (SP8): what it did not reach keeps its
                // old CheckedUtc and is first in line next time. The first is always asked, so a slice always makes progress.
                var clock = Stopwatch.StartNew();
                var slice = SliceBudget.Run(asked, budgetMs, () => clock.ElapsedMilliseconds, p =>
                {
                    p.CheckedUtc = now;
                    if (!p.Moved && !StillThere(doc, p.DeletedId)) return;   // still deleted: still a fix to record
                    if (!p.Moved && p.RulesReplaced)
                    {
                        // Kept by a settings reset and the element is back: the pair was found under rules that are gone, and under the new ones it
                        // would be judged Resolved and recorded as fixed with nothing in the model changed. The list was reset; the next check lists
                        // what the new rules find (delta check, DC-2).
                        settled.Add(p);
                        return;
                    }
                    if (links == null && p.Row.LinkInstanceId != null) links = PairVerifier.LinkLookup(doc);
                    ClashSeverity sev;
                    var verdict = PairVerifier.Verify(doc, p.Row, ClashSettings.Current, links, out sev);
                    if (verdict == PairVerifier.Verdict.Resolved) return;      // still fixed: recorded at the save
                    if (verdict == PairVerifier.Verdict.StillClashing) p.Row.Severity = sev;
                    back.Add(p.Row);
                    settled.Add(p);
                });

                if (kind != null) LogSliceTiming(kind, asked.Count, queued, slice);

                if (settled.Count > 0)
                    lock (PendingGate) foreach (var p in settled) Pending.Remove(p);
                if (back.Count > 0)
                {
                    ClashResultStore.Instance.AddRange(back);
                    Log.Info("Undo/rollback brought back " + back.Count + " clash(es) that were waiting to be recorded as fixed; not recorded.");
                }
                return back.Count;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not reconcile queued fixes after an undo or rollback", ex);
                return 0;
            }
        }

        private static DateTime _lastTickLogUtc = DateTime.MinValue;

        /// <summary>
        /// The timing line of a reconcile slice (SP8 / E7): every rollback pass, and a tick when it was slow, was stopped by time, or has not been
        /// logged for a minute. The numbers are what the time budget is judged on.
        /// </summary>
        private static void LogSliceTiming(string kind, int asked, int queued, SliceResult slice)
        {
            try
            {
                bool log = true;
                if (kind == "tick")
                {
                    log = SliceBudget.ShouldLogTick(asked, slice.Ms, slice.StoppedByTime, (DateTime.UtcNow - _lastTickLogUtc).TotalSeconds);
                    if (log) _lastTickLogUtc = DateTime.UtcNow;
                }
                if (log) Log.Info(SliceBudget.TimingLine(kind, asked, queued, slice.Done, slice.Ms, slice.StoppedByTime));
            }
            catch { }
        }

        private static bool Names(PendingResolution p, HashSet<long> ids)
        {
            if (p.DeletedId != null && ids.Contains(IdUtil.Value(p.DeletedId))) return true;
            if (p.Row.HostElementId != null && ids.Contains(IdUtil.Value(p.Row.HostElementId))) return true;
            return p.Row.LinkInstanceId == null && p.Row.OtherElementId != null && ids.Contains(IdUtil.Value(p.Row.OtherElementId));
        }

        private static bool StillThere(Document doc, ElementId id)
        {
            try
            {
                var e = doc.GetElement(id);
                return e != null && e.IsValidObject;
            }
            catch { return false; }
        }

        private static readonly object PendingGate = new object();
        private static readonly List<PendingResolution> Pending = new List<PendingResolution>();

        /// <summary>
        /// Drop the rows for elements that have just been deleted, and REMEMBER them
        /// for <see cref="FlushDeletedResolutions"/> to record once the deletion has
        /// settled.
        ///
        /// NO FILE I/O HERE, and that is the point of splitting this in two.
        ///
        /// The first version wrote both ledgers straight from Execute. That broke two
        /// things. The class rules at the top of this file say be fast — every
        /// millisecond is a frozen cursor — and two file appends inside every
        /// transaction that deletes anything is not fast, least of all a mass delete.
        /// Worse, Execute runs BEFORE the transaction commits, so it recorded fixes
        /// that had not happened yet: undo the delete, or close the model without
        /// saving, and the ledger kept an entry for a clash that is still there.
        /// Observed on 2026-09-06 — a duplicate pipe was deleted, recorded, and the
        /// deletion then discarded on close; the entry stayed.
        ///
        /// The removal itself still happens here, immediately. Only the RECORD waits.
        /// </summary>
        private static void QueueDeleted(Document doc, ICollection<ElementId> deleted)
        {
            if (doc == null || deleted == null || deleted.Count == 0) return;

            string key = DocKeyOf(doc);

            foreach (var id in deleted)
            {
                foreach (var r in ClashResultStore.Instance.RemoveForElement(id))
                {
                    if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                    QueueRemovedRow(doc, id, r);
                }
            }
        }

        /// <summary>
        /// Queue an ALREADY-REMOVED row as a pending deletion-based resolution — the row's own
        /// removal has already happened by the time this is called, so unlike QueueDeleted this
        /// does not touch ClashResultStore itself. Used by QueueDeleted above for the live
        /// updater's own deletions, and by LiveClashApp's auto-verify pass when PairVerifier's
        /// Resolved verdict is because an element in THIS document is gone, not because it moved
        /// away — see FlushDeletedResolutions' own comment for why recording that immediately is
        /// wrong: an element deleted, found gone by the very next check, and then undone a moment
        /// later must not leave a permanent "fixed" entry for a clash that is still there.
        /// </summary>
        internal static void QueueRemovedRow(Document doc, ElementId deletedId, ClashResult row)
        {
            if (doc == null || deletedId == null || row == null || string.IsNullOrEmpty(row.Key)) return;
            lock (PendingGate)
                Pending.Add(new PendingResolution { DocKey = DocKeyOf(doc), DeletedId = deletedId, Row = row });
        }

        /// <summary>
        /// Write the queued deletions to the permanent record, keeping only the ones
        /// whose element is STILL gone.
        ///
        /// CALLED ON SAVE, and the timing is the entire mechanism. A deletion is only
        /// a fix once it is in the saved model; until then it can be undone, rolled
        /// back, or thrown away by closing without saving. At save time the question
        /// is simply answerable — the element is gone from what was just written, or
        /// it is back and this was never a fix.
        ///
        /// Earlier versions called this from the updater (file I/O inside Revit's
        /// transaction, against the class rules above) and then from Idling (out of
        /// the transaction, but still early enough that an undo a few seconds later
        /// had already been beaten to it). Both recorded fixes that later evaporated.
        ///
        /// ORDER MATTERS on the write, and it is the same order RecordResolved uses:
        /// the decision is noted gone FIRST, so a pair somebody had accepted and has
        /// now deleted shows as Reopened if it ever comes back rather than quietly
        /// reverting to accepted.
        /// </summary>
        public static void FlushDeletedResolutions(Document doc)
        {
            if (doc == null || !doc.IsValidObject) return;

            string key = DocKeyOf(doc);

            // TAKE ONLY THIS DOCUMENT'S ENTRIES. Draining the whole queue and then
            // filtering would throw away deletions belonging to another open model,
            // which has not been saved yet and is entitled to record its own when it
            // is. Saving A must not silently discard B's pending fixes.
            var batch = new List<PendingResolution>();
            lock (PendingGate)
            {
                if (Pending.Count == 0) return;
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    if (Pending[i].DocKey != key) continue;
                    batch.Add(Pending[i]);
                    Pending.RemoveAt(i);
                }
            }
            if (batch.Count == 0) return;

            try
            {
                var rows = new List<ClashResult>();
                var keys = new List<string>();
                var seen = new HashSet<string>();
                var back = new List<ClashResult>();
                Dictionary<long, LinkContext> links = null;

                foreach (var p in batch)
                {
                    try
                    {
                        // A deletion whose element is gone from what was just saved: that IS the verdict.
                        // A deletion whose element is BACK (undone before the save landed), and every move,
                        // is asked again: the save is what makes it a fix, and only if it is still one.
                        if (!p.Moved && p.RulesReplaced && StillThere(doc, p.DeletedId)) continue;   // kept by a settings reset, then undone: neither recorded nor listed (DC-2)
                        if (p.Moved || StillThere(doc, p.DeletedId))
                        {
                            if (links == null && p.Row.LinkInstanceId != null) links = PairVerifier.LinkLookup(doc);
                            ClashSeverity sev;
                            PairVerifier.Verdict verdict;
                            verdict = PairVerifier.Verify(doc, p.Row, ClashSettings.Current, links, out sev);
                            if (verdict != PairVerifier.Verdict.Resolved)
                            {
                                // Not a fix after all, or not one that can be shown now (Unknown: a link not
                                // loaded). Back on the panel - the store had already dropped it (review
                                // 2026-10-31, M1: this used to be counted "restored" and left off the list).
                                if (verdict == PairVerifier.Verdict.StillClashing) p.Row.Severity = sev;
                                back.Add(p.Row);
                                continue;
                            }
                        }

                        // Both ends deleted at once would otherwise list the pair twice.
                        if (!seen.Add(p.Row.Key)) continue;

                        rows.Add(p.Row);
                        keys.Add(p.Row.Key);
                    }
                    catch (Exception ex)
                    {
                        // One entry whose re-test throws (an invalid object, a link mid-reload) used to take the whole batch with
                        // it: the catch below ended the flush, and every other queued fix was neither recorded nor restored (review
                        // 2026-10-31 round 2, DL3). It is unmeasured, so it is listed again and left unrecorded, like an Unknown.
                        Log.Warn("A queued fix could not be re-tested at the save; it is listed again and left unrecorded", ex);
                        back.Add(p.Row);
                    }
                }

                if (back.Count > 0)
                {
                    ClashResultStore.Instance.AddRange(back);
                    Log.Info("A queued fix did not survive to the save (undone, or not shown fixed now); "
                             + back.Count + " clash(es) listed again and left unrecorded.");
                }

                if (rows.Count == 0) return;

                ClashStatusStore.Instance.NoteGone(keys);
                ResolvedLog.Add(rows);
                Log.Info("Deleted or moved element(s) resolved " + rows.Count + " clash(es); recorded.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not record clash(es) resolved by deletion", ex);
            }
        }

        /// <summary>
        /// Move queued deletions from one document key to another, for the one event
        /// that changes a document's key underneath them: Save As.
        ///
        /// PROVEN NECESSARY, not defensive (2026-09-18, on a throwaway copy of Snowdon):
        /// delete a clashing element, Save As to a new name, and the queued fix is
        /// stranded. Measured twice over — the entry stayed under the OLD path while
        /// DocKeyOf(doc) already read the NEW one, so <see cref="FlushDeletedResolutions"/>
        /// matched nothing; and a later ordinary save matched nothing either, leaving the
        /// deletion permanently absent from the record (ResolvedLog stayed at 0). That is
        /// the quiet direction of failure this whole queue exists to avoid: a real fix
        /// that never reaches the permanent record.
        ///
        /// Re-keying rather than flushing with an override on purpose: the entries end up
        /// in a CORRECT state whether or not the flush that follows succeeds, so the next
        /// ordinary save picks up anything this moment missed. The already-proven flush
        /// path stays untouched.
        ///
        /// A document that had NEVER been saved is covered too, without guessing: its entries were
        /// queued under "unsaved::&lt;title&gt;", the event's OriginalPath is empty and the title has
        /// already changed, so the old key is captured BEFORE the save (LiveClashApp.OnDocumentSavingAs)
        /// and passed in here. Re-keying every "unsaved::" entry instead would risk attributing one
        /// unsaved model's fix to another's ledger, and a wrong entry in the permanent record is worse
        /// than a missing one (the rule FullCheckHandler.RecordResolved states for the same reason) -
        /// which is why this still moves NOTHING for an empty or unknown old key.
        /// </summary>
        /// <returns>How many queued deletions were moved.</returns>
        public static int RekeyPending(string fromKey, string toKey)
        {
            if (string.IsNullOrEmpty(fromKey) || string.IsNullOrEmpty(toKey) || fromKey == toKey)
                return 0;

            int moved = 0;
            lock (PendingGate)
                foreach (var p in Pending)
                    if (p.DocKey == fromKey) { p.DocKey = toKey; moved++; }
            CommittedDeletions.Shared.Rekey(fromKey, toKey);   // the memory of committed deletions follows the document, merged into any already under the new key
            return moved;
        }

        /// <summary>
        /// Forget every queued deletion of a document that is closing. An unsaved deletion is not a fix, and
        /// a closed document can never flush them - so left alone they sit in the queue for the rest of the
        /// session, keyed by a name that can come round again. That is not harmless: a never-saved model is
        /// keyed "unsaved::Project1", Revit hands "Project1" out again after it closes, and the first Save As
        /// of the NEXT "Project1" re-keys everything under that name - including the closed one's entries,
        /// which would then be recorded as fixes of clashes that belong to a different model. A missing entry
        /// is the failure this queue accepts; a wrong one is not.
        /// </summary>
        /// <returns>How many queued deletions were dropped.</returns>
        public static int DropPending(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return 0;

            int dropped = 0;
            lock (PendingGate)
                for (int i = Pending.Count - 1; i >= 0; i--)
                    if (Pending[i].DocKey == docKey) { Pending.RemoveAt(i); dropped++; }
            return dropped;
        }

        /// <summary>
        /// Remove the queued MOVED fixes of the document whose pair key is among <paramref name="keys"/>: the pair was listed again in the same pass
        /// (the reverse check), so it is not a fix. Queued deletions are not touched.
        /// </summary>
        /// <returns>How many fixes were removed.</returns>
        public static int DropPendingMovedForKeys(string docKey, ICollection<string> keys)
        {
            if (string.IsNullOrEmpty(docKey) || keys == null || keys.Count == 0) return 0;
            var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
            int dropped = 0;
            lock (PendingGate)
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    var p = Pending[i];
                    if (p.DocKey != docKey || !p.Moved || p.Row == null || p.Row.Key == null || !wanted.Contains(p.Row.Key)) continue;
                    Pending.RemoveAt(i);
                    dropped++;
                }
            return dropped;
        }

        /// <summary>
        /// Forget the document's queued MOVED fixes (QueueMovedRow), the ones judged under rules that have just been replaced (delta review 3,
        /// D3-1). A queued DELETION is kept, and marked: an element that is gone is a fix whatever the rules say, and the save should still record it.
        /// If that element comes back (an undo) the entry leaves the queue unrecorded and unlisted instead of being re-tested under the new rules (DC-2).
        /// </summary>
        /// <returns>How many queued moved fixes were dropped.</returns>
        public static int DropPendingMoved(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return 0;

            int dropped = 0;
            lock (PendingGate)
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    if (Pending[i].DocKey != docKey) continue;
                    if (Pending[i].Moved) { Pending.RemoveAt(i); dropped++; }
                    else Pending[i].RulesReplaced = true;
                }
            return dropped;
        }

        /// <summary>
        /// Which document a queued deletion belongs to, so a flush cannot judge it
        /// against a model the user has since switched to.
        /// </summary>
        private static string DocKeyOf(Document doc)
        {
            try { return ClashEngine.DocKey(doc); }
            catch { return ""; }
        }

        // ------------------------------------------------------------- registration

        /// <summary>
        /// Registers application-wide, so it applies to every document opened this
        /// session without needing per-document hookup.
        /// </summary>
        public static ClashUpdater Register(AddInId addInId)
        {
            var updater = new ClashUpdater(addInId);
            var id = updater.GetUpdaterId();

            if (UpdaterRegistry.IsUpdaterRegistered(id))
                UpdaterRegistry.UnregisterUpdater(id);

            // isOptional: true means an older Revit opening this model without the
            // add-in installed will not complain about a missing updater.
            UpdaterRegistry.RegisterUpdater(updater, true);

            AddTriggers();
            return updater;
        }

        /// <summary>
        /// Rebuild the triggers — call this after the user edits the source category
        /// list, otherwise newly enabled categories will not fire.
        /// </summary>
        private static volatile bool _triggersDirty;

        /// <summary>
        /// Ask for the triggers to be rebuilt at the next safe moment.
        ///
        /// AddTriggers touches UpdaterRegistry, which needs a real Revit API context.
        /// The filter window runs on the WPF dispatcher, which is NOT one — doing
        /// Revit API work there is the classic way to take Revit down with a soft
        /// crash rather than a catchable exception. So the dialog only sets a flag
        /// and LiveClashApp.OnIdling does the work.
        /// </summary>
        public static void RequestTriggerRebuild()
        {
            _triggersDirty = true;
        }

        /// <summary>Called from Idling, where we are safely in API context.</summary>
        public static void RebuildTriggersIfRequested()
        {
            if (!_triggersDirty) return;
            _triggersDirty = false;
            try
            {
                AddTriggers();
                Log.Info("Triggers rebuilt for "
                    + ClashSettings.Current.SourceCategories.Count + " source categories.");
            }
            catch (Exception ex)
            {
                Log.Error("Trigger rebuild failed", ex);
            }
        }

        public static void AddTriggers()
        {
            var id = new UpdaterId(LiveClashApp.AddInId, UpdaterGuid);
            if (!UpdaterRegistry.IsUpdaterRegistered(id)) return;

            UpdaterRegistry.RemoveAllTriggers(id);

            // Fire on the UNION of every enabled set's source categories — the live
            // path checks a changed element against each set it belongs to, so the
            // trigger has to wake for any of them.
            // And, when host-vs-host is on, of their TARGET categories (review N1). A target is edited too: a cable tray
            // drawn through a pipe, or moved into one, must be checked against the sources near it, and a target moved
            // clear must leave the panel. With only the source categories watched, Execute never ran for it and the
            // pair was missed until a full check.
            var union = new System.Collections.Generic.HashSet<BuiltInCategory>();
            foreach (var set in ClashSettings.Current.Sets)
                if (set.Enabled)
                {
                    foreach (var c in set.SourceCategories) union.Add(c);
                    if (ClashSettings.Current.CheckHostVsHost)
                        foreach (var c in set.TargetCategories) union.Add(c);
                }
            if (union.Count == 0) return;   // nothing to watch; leave triggers cleared

            var filter = new LogicalAndFilter(
                new ElementIsElementTypeFilter(true),      // inverted: instances only
                new ElementMulticategoryFilter(new System.Collections.Generic.List<BuiltInCategory>(union)));

            // THREE separate change types are needed, and this is easy to get wrong.
            // GetChangeTypeGeometry fires only when an EXISTING element's geometry
            // changes — move, resize, reroute. It does NOT fire for a newly drawn
            // element: that is GetChangeTypeElementAddition, a distinct change type.
            // With only the geometry trigger registered, drawing two pipes straight
            // through each other produced nothing at all — Execute was never called,
            // even though it already handles GetAddedElementIds. Confirmed live in
            // Revit 2026 on 2026-08-21, twice, before the cause was found.
            // Deletion is the same story: Execute clears results for deleted ids, but
            // never ran for them without its own trigger, so removing a clashing
            // element left its clash on the list forever.
            UpdaterRegistry.AddTrigger(id, filter, Element.GetChangeTypeGeometry());
            UpdaterRegistry.AddTrigger(id, filter, Element.GetChangeTypeElementAddition());
            UpdaterRegistry.AddTrigger(id, filter, Element.GetChangeTypeElementDeletion());
        }

        public static void Unregister()
        {
            try
            {
                var id = new UpdaterId(LiveClashApp.AddInId, UpdaterGuid);
                if (UpdaterRegistry.IsUpdaterRegistered(id))
                    UpdaterRegistry.UnregisterUpdater(id);
            }
            catch (Exception ex)
            {
                Log.Warn("Unregister failed", ex);
            }
        }
    }
}
