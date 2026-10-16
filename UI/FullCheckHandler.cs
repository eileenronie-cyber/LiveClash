using System.Collections.Generic;
using Autodesk.Revit.DB;
using System.Diagnostics;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// One-off sweep of the whole model, so the panel starts from a real baseline
    /// instead of only knowing about elements touched this session.
    ///
    /// This runs on the main thread and will block Revit for its duration — on a
    /// large model with a lot of links that can be minutes. It is deliberately a
    /// button the user presses, never something that fires automatically.
    ///
    /// Progress goes to the LOG, not to the panel. SetStatus marshals through the
    /// WPF dispatcher, and the dispatcher runs on the very thread this sweep is
    /// blocking — so nothing posted here can repaint until the sweep is already
    /// over. The log file is the only channel that stays observable while Revit is
    /// frozen, which also makes it the only way to tell a slow sweep from a hang:
    ///     Get-Content "$env:LOCALAPPDATA\LiveClash\liveclash.log" -Wait -Tail 5
    /// </summary>
    public class FullCheckHandler : IExternalEventHandler
    {
        /// <summary>Set by the panel so it can report the outcome.</summary>
        public ClashPanel Panel { get; set; }

        public void Execute(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;

            var doc = uidoc.Document;

            // THREE CLOCKS, not one, and the split is the whole point.
            //
            // A single stopwatch started here ran across the pre-flight sample AND
            // the confirmation dialog, so it measured however long the person took
            // to read it. Measured live on PRJ01 on 2026-09-03: 28 seconds spent
            // answering the dialog, reported as "282 clash(es) in 41049 ms" for a
            // sweep that took 11.3 s, and — worse — the first progress line divided
            // that dwell time by 100 elements and announced "297.2 ms/element,
            // ~4427 s remaining". A 74-minute estimate, printed 0.4 s into a
            // twelve-second job, in the one channel the class comment tells people
            // to watch to tell a slow sweep from a hang.
            var preflight = Stopwatch.StartNew();
            var waiting = new Stopwatch();
            var sweep = new Stopwatch();

            // Whether the fresh results have replaced the listed ones. Until they have, a failure leaves the
            // previous list exactly as it was.
            bool replaced = false;

            // The window that shows progress while the sweep blocks Revit. On a thread of its own (see
            // FullCheckProgress) - and closed in the finally below, however the check ends.
            FullCheckProgress progressUi = null;

            try
            {
                // Scope guard: an accidental all-categories filter (easy to reach with
                // "Select All", especially now the category list includes linked
                // disciplines) can put the sweep into the hundreds of millions of
                // comparisons — minutes of frozen Revit with no way to interrupt. Measure
                // it cheaply first, and warn by band: green just runs, amber gets a light
                // heads-up, red a strong warning that defaults to cancel.
                var scope = ClashScopeEstimate.Measure(doc, ClashSettings.Current);
                if (scope.IsHeavy)
                {
                    // Only when it looks heavy: replace the formula with a TIMED
                    // sample before asking. Costs ~a second, and it is the number
                    // the decision actually turns on.
                    scope.MeasureSampled(doc, ClashSettings.Current);
                    Log.Info("Full check pre-flight: " + scope.Summary() + " -> " + scope.EstimatedText
                             + (scope.WasSampled ? " (measured from a " + ClashScopeEstimate.SampleSize + "-source sample)" : " (estimated)"));
                }
                if (Panel != null) Panel.UpdateScopeIndicator(scope);
                preflight.Stop();

                waiting.Start();
                bool declined = scope.Band != ScopeBand.Green && !ConfirmRun(scope);
                waiting.Stop();

                if (declined)
                {
                    Log.Info("Full check: cancelled at the scope warning (" + scope.Band + ") — "
                             + scope.Summary() + ", " + scope.EstimatedText + ".");
                    if (Panel != null)
                        Panel.SetStatus("Full check cancelled (" + scope.EstimatedText + ", "
                            + scope.Summary() + "). Narrow the category filter — source = your "
                            + "discipline, target = what you coordinate against — and try again.");
                    return;
                }
                if (scope.Band != ScopeBand.Green)
                    Log.Info("Full check: proceeding past the " + scope.Band + " scope warning — "
                             + scope.Summary() + ", " + scope.EstimatedText + ".");

                Log.Info("Full check: starting.");
                sweep.Start();

                // A window for a sweep worth watching: one expected to take a couple of seconds or more. A
                // shorter one would only flash. (If the estimate proves too low, the window is started from
                // the progress report below once the sweep has run past that long.)
                bool worthShowing = FullCheckProgress.ShouldShow(scope.EstimatedSeconds);
                if (worthShowing) progressUi = FullCheckProgress.Start(FullCheckProgress.RevitWindow());

                // What was listed BEFORE the sweep. A pair that was listed and is not
                // listed afterwards may have been fixed - and if so it belongs in the
                // resolved record, which until now only the background verifier and
                // Recheck ever wrote to, so anything fixed between two full checks was
                // never recorded at all.
                var before = ClashResultStore.Instance.Snapshot();

                // NOT cleared here. The sweep runs first and the list is swapped only when it has produced
                // something to swap in (below), so a sweep that fails part-way leaves the previous results
                // listed instead of a blank panel and nothing to go back to.
                long lastLogMs = 0;
                var results = ClashEngine.RunFullCheckAllSets(doc, ClashSettings.Current, (done, total) =>
                {
                    // One line per batch would flood a big model; one per second is
                    // enough to prove liveness and to measure the rate.
                    //
                    // THE SWEEP CLOCK, not the total. Dividing wall-clock-since-entry
                    // by elements checked charges the dialog's dwell time to the first
                    // batch, which is what produced a 74-minute ETA on a 12-second job.
                    long now = sweep.ElapsedMilliseconds;

                    // The window BEFORE the log's once-a-second throttle (it throttles itself), and started
                    // late if the estimate said this would be quick and it was not.
                    if (progressUi == null && now > 2000 && done < total)
                        progressUi = FullCheckProgress.Start(FullCheckProgress.RevitWindow());
                    if (progressUi != null)
                        progressUi.Report(done, total, now, done > 0 ? (long)((double)now / done * (total - done)) : -1);

                    if (now - lastLogMs < 1000 && done < total) return;
                    lastLogMs = now;

                    double perElement = done > 0 ? (double)now / done : 0.0;
                    long remainingMs = (long)(perElement * (total - done));

                    Log.Info("Full check: " + done + "/" + total
                             + " (" + (total > 0 ? done * 100 / total : 0) + "%)"
                             + "  " + now + " ms elapsed"
                             + ", " + perElement.ToString("0.0") + " ms/element"
                             + ", ~" + (remainingMs / 1000) + " s remaining.");
                });

                ClashResultStore.Instance.Replace(results);
                replaced = true;
                sweep.Stop();

                // Timed on its own: it re-verifies every pair that dropped out, which
                // is real work but is not the sweep, and folding it in would make two
                // runs over the same model look different for a reason nobody could see.
                if (progressUi != null) progressUi.SetStage("Checking which of the clashes you had listed have been fixed…");
                var resolving = Stopwatch.StartNew();
                RecordResolved(doc, before);
                resolving.Stop();

                // Report what LANDED, not what was produced. The engine returns a
                // result per direction, so a pair found both ways (A hits B and B
                // hits A) is two results and one row — 196 vs 193 on this model.
                // Logging the raw count made the panel look like it had lost rows.
                int stored = ClashResultStore.Instance.Count;

                // A full check re-sweeps everything a link change could have affected, so
                // whatever the notice was reporting is now reflected in the fresh results
                // (or genuinely absent). Unconditional, not "only if it started after the
                // change": Revit is single-threaded here, so nothing could have recorded a
                // NEWER change while Execute was on the stack — there is no ordering left
                // to get wrong.
                // What did the change DO? Compare the list from before the link changed with the one just built - before
                // Clear() below ends the wait for a check.
                ClashChangeTracker.Complete(ClashResultStore.Instance.Snapshot(), true);
                LinkWatch.Clear();
                if (Panel != null) Panel.RefreshLinkNotice();

                if (Panel != null) Panel.SetEmptyReason(ClashPanel.EmptyReason.CheckedAndClean);
                var msg = "Full check: " + stored + " clash(es) in " + sweep.ElapsedMilliseconds + " ms"
                        + (results.Count != stored
                            ? "  (" + (results.Count - stored) + " duplicate pair(s) collapsed)"
                            : "")
                        + ".";

                // The panel gets the sweep figure alone — that is the number someone
                // compares against last time. The breakdown goes to the log, where the
                // point is being able to account for every millisecond between pressing
                // the button and seeing rows.
                Log.Info(msg + "  [pre-flight " + preflight.ElapsedMilliseconds
                         + " ms, waiting on the dialog " + waiting.ElapsedMilliseconds
                         + " ms, resolved-check " + resolving.ElapsedMilliseconds + " ms]");
                if (Panel != null) Panel.SetStatus(msg);
            }
            catch (System.Exception ex)
            {
                Log.Error("Full check failed", ex);

                // What is ACTUALLY still listed, not what was listed when it started: if the failure came
                // after the swap the list is the new one, and saying "previous" would be untrue.
                int still = replaced ? 0 : ClashResultStore.Instance.Count;
                if (Panel != null)
                    Panel.SetStatus(still > 0
                        ? "Full check failed - your previous " + still + " result(s) are still listed - see " + Log.Path
                        : "Full check failed - see " + Log.Path);
            }
            finally
            {
                // However the check ended - finished, failed, or cancelled at the warning - the window goes.
                if (progressUi != null) progressUi.Close();
            }
        }

        /// <summary>
        /// Record pairs the sweep no longer reports — but ONLY the ones that are genuinely
        /// gone.
        ///
        /// Absence from the results is NOT the same as fixed. Narrow the filter, untick a
        /// category, switch a link off, and yesterday's clashes vanish from the sweep while
        /// still being perfectly real in the model. Logging those as "resolved" would put
        /// lies in the permanent record, which is worse than an incomplete one.
        ///
        /// So every missing pair is RE-TESTED with <see cref="PairVerifier"/>, which asks
        /// the narrow question "do these two still overlap?" independently of any filter:
        ///   Resolved      -> really fixed, record it
        ///   StillClashing -> only out of scope now, say nothing
        ///   Unknown       -> usually an unloaded link; never claim a fix we cannot see
        /// </summary>
        private static void RecordResolved(Document doc, IList<ClashResult> before)
        {
            if (doc == null || before == null || before.Count == 0) return;
            try
            {
                var still = new HashSet<string>();
                foreach (var r in ClashResultStore.Instance.Snapshot())
                    if (!string.IsNullOrEmpty(r.Key)) still.Add(r.Key);

                var missing = new List<ClashResult>();
                foreach (var r in before)
                    if (!string.IsNullOrEmpty(r.Key) && !still.Contains(r.Key)) missing.Add(r);
                if (missing.Count == 0) return;

                var links = PairVerifier.LinkLookup(doc);
                var fixedOnes = new List<ClashResult>();
                var goneKeys = new List<string>();
                int outOfScope = 0;

                foreach (var r in missing)
                {
                    ClashSeverity sev;
                    var verdict = PairVerifier.Verify(doc, r, ClashSettings.Current, links, out sev);
                    if (verdict == PairVerifier.Verdict.Resolved)
                    {
                        fixedOnes.Add(r);
                        goneKeys.Add(r.Key);
                    }
                    else if (verdict == PairVerifier.Verdict.StillClashing) outOfScope++;
                }

                if (goneKeys.Count > 0)
                {
                    // Against the decision first, so a pair that was accepted and has now
                    // been fixed shows as Reopened if it ever comes back.
                    ClashStatusStore.Instance.NoteGone(goneKeys);
                    ResolvedLog.Add(fixedOnes);
                }

                Log.Info("Full check: " + missing.Count + " listed pair(s) no longer reported — "
                         + goneKeys.Count + " verified fixed and recorded"
                         + (outOfScope > 0 ? ", " + outOfScope + " still clashing but out of the current scope" : "")
                         + ".");
            }
            catch (System.Exception ex)
            {
                Log.Warn("Full check: could not record resolved pairs", ex);
            }
        }

        /// <summary>
        /// Ask before an amber/red sweep. Runs in API context on the main thread (this is
        /// an ExternalEvent), so a native TaskDialog is right. Returns true to proceed.
        /// Amber defaults to Run (it is only tens of seconds); red defaults to Cancel
        /// (it is minutes, and usually a mis-click).
        /// </summary>
        internal static bool ConfirmRun(ClashScopeEstimate scope)
        {
            bool red = scope.Band == ScopeBand.Red;

            var td = new TaskDialog(red ? "Live Clash — large full check" : "Live Clash — full check")
            {
                MainInstruction = red
                    ? "This is a large full check — " + scope.EstimatedText
                    : "This full check will take " + scope.EstimatedText,
                MainContent =
                    "It compares " + scope.Summary() + ", roughly "
                    + scope.Comparisons.ToString("N0") + " comparisons before parameter rules "
                    + "narrow it.\n\n"
                    + "The sweep runs on Revit's main thread and cannot be interrupted — "
                    + "Revit will be unresponsive until it finishes."
                    + (red
                        ? "\n\nNarrowing the category filter (source = your discipline, target = "
                          + "what you coordinate against) usually brings this down to seconds."
                        : ""),
                AllowCancellation = true,
                TitleAutoPrefix = false,
            };

            if (red)
            {
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Cancel",
                    "Go back and narrow the category filter.");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Run it anyway",
                    "Revit will be frozen until the sweep finishes.");
                td.DefaultButton = TaskDialogResult.CommandLink1;
                return td.Show() == TaskDialogResult.CommandLink2;
            }

            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Run the full check",
                scope.EstimatedText + ", Revit frozen until it is done.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel");
            td.DefaultButton = TaskDialogResult.CommandLink1;
            return td.Show() == TaskDialogResult.CommandLink1;
        }

        public string GetName() { return "LiveClash.FullCheck"; }
    }
}
