using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Re-verifies the clashes already on the list, and nothing else.
    ///
    /// The live updater keeps things honest for anything you touch, but three cases
    /// leave stale rows behind: a clash resolved by editing something INSIDE a link
    /// (no trigger fires in another document), anything changed while Live was
    /// paused, and results nobody has touched since they were found.
    ///
    /// A full sweep would catch all of it and costs minutes. This costs a fraction of
    /// a second, because it only re-checks the elements that produced the current
    /// results — a few hundred at ~2 ms each — and it is the honest way to answer
    /// "is this list still true?".
    ///
    /// The cached target index is dropped first: a link edit is invisible precisely
    /// because nothing told us about it, so the index has to be rebuilt rather than
    /// trusted.
    /// </summary>
    public class RecheckHandler : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }

        public void Execute(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;

            var doc = HandlerGuard.Pick(uidoc);
            if (doc == null || !doc.IsValidObject) return;
            if (HandlerGuard.Refuses(doc, Panel, "recheck")) return;
            if (HandlerGuard.SettingsReloaded()) return;   // the rules changed under the listed rows: reset first, record nothing (DC-1)

            var sw = Stopwatch.StartNew();
            try
            {
                var snapshot = ClashResultStore.Instance.Snapshot();
                if (snapshot.Count == 0)
                {
                    if (Panel != null) Panel.SetStatus("Nothing to recheck.");
                    return;
                }

                // Distinct host-document elements that produced the current rows.
                var seen = new HashSet<long>();
                var ids = new List<ElementId>();
                foreach (var r in snapshot)
                {
                    if (r.HostElementId != null && seen.Add(IdUtil.Value(r.HostElementId)))
                        ids.Add(r.HostElementId);

                    // The other side too, when it is in this document — otherwise a
                    // clash resolved by moving the TARGET would survive the recheck.
                    if (r.LinkInstanceId == null && r.OtherElementId != null
                        && seen.Add(IdUtil.Value(r.OtherElementId)))
                        ids.Add(r.OtherElementId);
                }

                int verifiedFixed = 0, keptUnchecked = 0, removedLinks = 0;
                var droppedLinkRows = new List<ClashResult>();

                // Keys we knew about, so the ones that do not come back can be
                // recorded as gone against any decision they carry.
                var known = new HashSet<string>();
                foreach (var r in snapshot) if (!string.IsNullOrEmpty(r.Key)) known.Add(r.Key);
                var listedBefore = new HashSet<string>(known);

                // Forget what we knew about these elements, then ask again.
                foreach (var id in ids) ClashResultStore.Instance.RemoveForElement(id);

                ClashEngine.InvalidateAll();

                // Re-check against EVERY enabled set, the same way the live path and the
                // full check do — a row caught by any set must be re-evaluated by that set.
                List<ElementId> notChecked;
                List<string> notCollected;
                CollectionFailures.Begin();   // a host or link whose targets cannot be collected is said, not read as clean (LOW-12)
                List<ClashResult> results;
                try
                {
                    results = ClashEngine.CheckElementsAllSets(
                        doc, ids, ClashSettings.Current, null, out notChecked);
                }
                finally { notCollected = CollectionFailures.End(); }
                // The contexts the recheck just built are cached; what they could not read is part of what this recheck did not check (L-2).
                notCollected = CollectionFailures.Union(new IList<string>[] { notCollected, ClashContext.UnreadNow() });
                ClashResultStore.Instance.AddRange(results);

                // Anything that was listed before and is not listed now MIGHT be resolved - but
                // the coarse re-check above only re-tests the HOST side (CheckElementsAllSets
                // never checks a changed element as a target), so a pair whose other half is in a
                // link this batch could not reach — most commonly, the link is no longer loaded —
                // never comes back here even though it may still be a real clash. Found reviewing
                // the whole codebase, 2026-09-26: unloading a link and clicking Recheck logged
                // every one of that link's clashes as Resolved. FullCheckHandler.RecordResolved
                // already re-verifies before believing a "missing" pair is genuinely gone; Recheck
                // needs the exact same check, not a coarser version of it, to actually answer "is
                // this list still true?" the way its own class comment claims to.
                foreach (var r in ClashResultStore.Instance.Snapshot())
                    if (r.Key != null) known.Remove(r.Key);

                if (known.Count > 0)
                {
                    // The originals still hold the names and categories; the store no
                    // longer does, so verification and the log both read from the snapshot.
                    var missing = new List<ClashResult>();
                    foreach (var r in snapshot)
                        if (r.Key != null && known.Contains(r.Key)) missing.Add(r);

                    Dictionary<long, LinkContext> links = null;
                    var confirmedGone = new List<string>();
                    var fixedOnes = new List<ClashResult>();
                    var restore = new List<ClashResult>();
                    foreach (var r in missing)
                    {
                        if (links == null && r.LinkInstanceId != null) links = PairVerifier.LinkLookup(doc);
                        ClashSeverity sev;
                        var verdict = PairVerifier.Verify(doc, r, ClashSettings.Current, links, out sev);
                        if (verdict == PairVerifier.Verdict.Resolved)
                        {
                            confirmedGone.Add(r.Key);
                            fixedOnes.Add(r);
                        }
                        else if (PairVerifier.DropsRow(verdict, () => PairVerifier.LinkInstanceRemoved(doc, r)))
                        {
                            // Unknown for good: the link instance was removed from the model, so this pair can never
                            // be re-tested and would stay listed for ever (delta review 2026-10-31, DN1). Not
                            // recorded as fixed: nothing was measured. Said on the panel.
                            removedLinks++;
                            droppedLinkRows.Add(r);
                        }
                        else
                        {
                            // StillClashing, or Unknown (most often its link is not loaded right
                            // now): the coarse re-check above dropped it wrongly. Put it back
                            // rather than losing a real, unresolved coordination issue.
                            //
                            // An unknown pair goes back at Unverified, as the full check keeps it (delta review
                            // 2026-10-31, DL1): it no longer claims a severity nobody measured, and "What changed"
                            // can tell it was not checked instead of reading "no change".
                            var back = verdict == PairVerifier.Verdict.Unknown ? r.Clone() : r;
                            back.Severity = CheckOutcome.RestoredSeverity(verdict, r.Severity, sev);
                            if (verdict == PairVerifier.Verdict.Unknown) keptUnchecked++;
                            restore.Add(back);
                        }
                    }

                    if (restore.Count > 0) ClashResultStore.Instance.AddRange(restore);
                    ClashResultStore.Instance.ForgetResolved(droppedLinkRows.Select(r => r.Key));   // dropped, not fixed: never shown as just fixed (SP5)
                    DroppedLinkRows.HoldFor(doc, droppedLinkRows);   // an undo of the deletion lists them again (DM3)

                    // The settings were read again during this recheck (a retry landed on a read after the guard): the rows were listed under one set
                    // of rules and re-tested under another, so none of them is a measured fix and nothing is recorded (delta review 4, DR4-1). The
                    // idle tick resets the list.
                    // A pair whose element is GONE is still a fix whatever the rules say (decision T-g; delta check of DR4, DC4-1): only the
                    // re-measured ones are dropped.
                    if (ClashSettings.RulesReloadPending && fixedOnes.Count > 0)
                    {
                        var deleted = fixedOnes.Where(r => PairVerifier.ElementGone(doc, r)).ToList();
                        int notRecorded = fixedOnes.Count - deleted.Count;
                        if (notRecorded > 0)
                            Log.Info("Recheck: the settings were read again during the recheck; " + notRecorded
                                     + " pair(s) no longer reported are NOT recorded as fixed (re-measured under rules that changed).");
                        fixedOnes = deleted;
                        confirmedGone = deleted.Select(r => r.Key).ToList();
                    }
                    verifiedFixed = confirmedGone.Count;

                    if (confirmedGone.Count > 0)
                    {
                        // Against the decision first, so a pair that was accepted and then fixed
                        // shows as Reopened if it ever comes back rather than being silently
                        // re-hidden.
                        ClashStatusStore.Instance.NoteGone(confirmedGone);
                        ResolvedLog.Add(fixedOnes);
                    }
                }

                sw.Stop();
                var listedAfter = ClashResultStore.Instance.Snapshot();
                int after = listedAfter.Count;
                int newPairs = 0;
                foreach (var r in listedAfter)
                    if (!string.IsNullOrEmpty(r.Key) && !listedBefore.Contains(r.Key)) newPairs++;

                var msg = CheckOutcome.RecheckMessage(ids.Count, sw.ElapsedMilliseconds, after, verifiedFixed, newPairs, keptUnchecked, removedLinks, notCollected);
                Log.Info(msg);

                // If a link changed and a check is awaited, say what this recheck found (provisionally: it re-tests only
                // what already clashes). The baseline stays until a full check.
                ClashChangeTracker.Complete(ClashResultStore.Instance.Snapshot(), false);

                if (Panel != null) Panel.SetStatus(msg);
            }
            catch (System.Exception ex)
            {
                Log.Error("Recheck failed", ex);
                if (Panel != null) Panel.SetStatus("Recheck failed — see " + Log.Path);
            }
        }

        public string GetName() { return "LiveClash.Recheck"; }
    }
}
