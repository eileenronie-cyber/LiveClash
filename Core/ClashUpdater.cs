using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            var settings = ClashSettings.Current;
            if (!settings.Enabled) return;

            try
            {
                var doc = data.GetDocument();
                if (doc == null || !doc.IsValidObject) return;

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
            }
            catch (Exception ex)
            {
                // Swallow deliberately — see the class comment.
                Log.Error("ClashUpdater.Execute failed", ex);
            }
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
        /// confirmed Resolved verdict leaves it gone. Never writes to the resolved ledger: that is
        /// the background auto-verify pass's job, and it will pick up a genuine fix on its own
        /// next tick, the same as it always has for anything not caught by the live path.
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
                if (verdict == PairVerifier.Verdict.Resolved) continue;
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
        /// UNLIKE ReverifyTargetOnly, this DOES write to the resolved ledger — because unlike
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
        /// No deferral needed here the way a deletion needs one: the changed element still
        /// EXISTS (this is the modify path, not the delete one — see QueueDeleted), so an Undo
        /// immediately after fires its own DocumentChanged with this same element in it, and
        /// the ordinary live re-check restores the clash correctly if it comes back.
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
            var confirmedGone = new List<string>();
            var fixedOnes = new List<ClashResult>();
            foreach (var r in rows)
            {
                ClashSeverity sev;
                var verdict = PairVerifier.Verify(doc, r, settings, links, out sev);
                if (verdict == PairVerifier.Verdict.Resolved)
                {
                    confirmedGone.Add(r.Key);
                    fixedOnes.Add(r);
                }
                else
                {
                    if (verdict == PairVerifier.Verdict.StillClashing) r.Severity = sev;
                    restore.Add(r);
                }
            }

            if (restore.Count > 0) ClashResultStore.Instance.AddRange(restore);
            if (confirmedGone.Count > 0)
            {
                // Against the decision first, so a pair that was accepted and has now been
                // fixed shows as Reopened if it ever comes back rather than being silently
                // re-hidden — the same order FlushDeletedResolutions and RecordResolved use.
                ClashStatusStore.Instance.NoteGone(confirmedGone);
                ResolvedLog.Add(fixedOnes);
            }

            // Every other path that records a fix logs it; this one did not, so only the ledger file
            // could prove it had run (Roadmap R03). One line per call, only when there was something to say.
            if (rows.Count > 0)
                Log.Info("Re-verified " + rows.Count + " clash(es) after a " + (source ?? "change") + ": "
                         + fixedOnes.Count + " confirmed resolved and recorded, " + restore.Count
                         + " kept (still clashing or not verifiable now).");
        }

        // -------------------------------------------------- deletions, recorded late

        /// <summary>
        /// A deletion that has happened but has not yet been shown to survive.
        /// </summary>
        private sealed class PendingResolution
        {
            public string DocKey;
            public ElementId DeletedId;
            public ClashResult Row;
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
                int restored = 0;

                foreach (var p in batch)
                {
                    // Back again: undone before the save landed.
                    Element still;
                    try { still = doc.GetElement(p.DeletedId); } catch { still = null; }
                    if (still != null && still.IsValidObject) { restored++; continue; }

                    // Both ends deleted at once would otherwise list the pair twice.
                    if (!seen.Add(p.Row.Key)) continue;

                    rows.Add(p.Row);
                    keys.Add(p.Row.Key);
                }

                if (restored > 0)
                    Log.Info("Deletion did not survive to the save (undone); "
                             + restored + " clash(es) left unrecorded.");

                if (rows.Count == 0) return;

                ClashStatusStore.Instance.NoteGone(keys);
                ResolvedLog.Add(rows);
                Log.Info("Deleted element(s) resolved " + rows.Count + " clash(es); recorded.");
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
            var union = new System.Collections.Generic.HashSet<BuiltInCategory>();
            foreach (var set in ClashSettings.Current.Sets)
                if (set.Enabled)
                    foreach (var c in set.SourceCategories) union.Add(c);
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
