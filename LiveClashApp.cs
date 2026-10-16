using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using LiveClash.Core;
using LiveClash.UI;

namespace LiveClash
{
    public class LiveClashApp : IExternalApplication
    {
        public static AddInId AddInId { get; private set; }
        private const string TabName = "Live Clash";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                AddInId = application.ActiveAddInId;

                BuildRibbon(application);

                // Light or dark, following Revit (the panel is docked beside its own dark panels).
                LiveClash.UI.ThemeManager.HookRevit(application);

                // "What changed" after a link change compares the clash list from before with the one after; it reaches the
                // list through this. Nothing is tracked until it is set.
                ClashChangeTracker.SnapshotSource = () => ClashResultStore.Instance.Snapshot();

                // Dockable panes must be registered during startup, before any
                // document is open. Revit rejects late registration.
                application.RegisterDockablePane(
                    ClashPaneProvider.PaneId,
                    ClashPaneProvider.PaneTitle,
                    new ClashPaneProvider());

                // We are in API context here, so ExternalEvents can be created.
                ClashPaneProvider.Panel.InitialiseEvents();

                ClashUpdater.Register(AddInId);

                application.Idling += OnIdling;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                application.ViewActivated += OnViewActivated;

                // A link being LOADED — including a link that was unloaded when the scan
                // context was cached, which is the one case D1/D2's fixes do not reach —
                // fires neither DocumentClosing nor DocumentOpened (measured directly:
                // Reload/Unload/Load raise none of them). It DOES reliably raise this one,
                // for both Load() and Reload(); confirmed the same way. See the handler.
                application.ControlledApplication.LinkedResourceOpened += OnLinkedResourceOpened;

                // A deletion is only a FIX once it is in the saved model. Both events,
                // because a workshared local saves through one and syncs through the
                // other — recording on only one would miss half the ways work lands.
                application.ControlledApplication.DocumentSaved += OnDocumentSaved;
                application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynced;

                // Save As raises NEITHER of the two above — measured directly, not
                // assumed: a real File > Save As fired DocumentSavedAs alone, so the
                // flush was never even called, and the document's key had already moved
                // to the new path by the time it did fire. See OnDocumentSavedAs.
                application.ControlledApplication.DocumentSavedAs += OnDocumentSavedAs;

                // ... and the one thing DocumentSavedAs cannot tell us for a model that has never been saved:
                // what its queued deletions were keyed under. That is read BEFORE the save, while the title
                // is still the old one. See OnDocumentSavingAs.
                application.ControlledApplication.DocumentSavingAs += OnDocumentSavingAs;

                Log.Info("Live Clash started.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("OnStartup failed", ex);
                TaskDialog.Show("Live Clash", "Failed to start:\n\n" + ex.Message + "\n\nLog: " + Log.Path);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.Idling -= OnIdling;
                application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
                application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.ControlledApplication.LinkedResourceOpened -= OnLinkedResourceOpened;
                application.ViewActivated -= OnViewActivated;
                application.ControlledApplication.DocumentSaved -= OnDocumentSaved;
                application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynced;
                application.ControlledApplication.DocumentSavedAs -= OnDocumentSavedAs;
                application.ControlledApplication.DocumentSavingAs -= OnDocumentSavingAs;
                LiveClash.UI.ThemeManager.UnhookRevit();
                ClashUpdater.Unregister();
            }
            catch (Exception ex)
            {
                Log.Warn("OnShutdown failed", ex);
            }
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ ribbon

        private void BuildRibbon(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }

            var panel = app.CreateRibbonPanel(TabName, "Clash");
            string asm = Assembly.GetExecutingAssembly().Location;

            var showBtn = new PushButtonData(
                "LiveClashShow", "Clash\nPanel", asm, "LiveClash.Commands.ShowPanelCommand")
            {
                ToolTip = "Open the live clash results panel.",
                LongDescription = "Shows clashes found while you model, against the current model and every loaded link."
            };

            var toggleBtn = new PushButtonData(
                "LiveClashToggle", "Pause /\nResume", asm, "LiveClash.Commands.ToggleLiveCommand")
            {
                ToolTip = "Turn live checking on or off without unloading the add-in."
            };

            var settingsBtn = new PushButtonData(
                "LiveClashSettings", "Clearance", asm, "LiveClash.Commands.SettingsCommand")
            {
                ToolTip = "Set the soft-clash clearance distance."
            };

            var glanceBtn = new PushButtonData(
                "LiveClashGlance", "At a\nglance", asm, "LiveClash.Commands.GlanceCommand")
            {
                ToolTip = "A quick read-only summary of the current clashes by severity and category."
            };

            showBtn.LargeImage     = LoadPng("clash32.png");
            showBtn.Image          = LoadPng("clash16.png");
            toggleBtn.LargeImage   = LoadPng("pause32.png");
            toggleBtn.Image        = LoadPng("pause16.png");
            settingsBtn.LargeImage = LoadPng("clearance32.png");
            settingsBtn.Image      = LoadPng("clearance16.png");
            glanceBtn.LargeImage   = LoadPng("clash32.png");
            glanceBtn.Image        = LoadPng("clash16.png");

            panel.AddItem(showBtn);
            panel.AddItem(toggleBtn);
            panel.AddItem(settingsBtn);
            panel.AddItem(glanceBtn);
        }

        /// <summary>
        /// Load a ribbon icon from an embedded PNG. Returns null on any failure, and a
        /// null image simply leaves the button text-only -- an icon must never be the
        /// thing that stops the ribbon building.
        /// </summary>
        private static System.Windows.Media.ImageSource LoadPng(string file)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var name = asm.GetName().Name + ".Resources." + file;
                if (System.Array.IndexOf(asm.GetManifestResourceNames(), name) < 0)
                {
                    // Fall back to a suffix match if the logical name is not what the
                    // RootNamespace convention predicts.
                    foreach (var n in asm.GetManifestResourceNames())
                        if (n.EndsWith(file, System.StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                }
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    img.BeginInit();
                    img.StreamSource = s;
                    img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ idling

        /// <summary>
        /// Drains the deferred queue in small slices while Revit has nothing else to do.
        /// This is what keeps a 5000-element paste from freezing the session.
        /// </summary>
        private void OnIdling(object sender, IdlingEventArgs e)
        {
            // Safe API context — do any pending trigger rebuild here, not in the dialog.
            ClashUpdater.RebuildTriggersIfRequested();

            // Build the index before the user's first edit has to.
            TryWarmIndex(sender as UIApplication);

            // Refresh a per-set context BEFORE its 10-minute TTL lapses, so the next live edit
            // never has to pay for the rebuild inside its own transaction. Same reasoning as
            // TryWarmIndex just above; see ClashContext.RefreshStaleSoon.
            TryRefreshContexts(sender as UIApplication);

            // Left to Revit's own cadence, Idling fires about every 2.5 seconds, so
            // 25 ms slices counted 19,000 elements a minute and would have taken
            // eleven minutes on this model. Asking to be raised again immediately —
            // the same thing the deferred queue does below — turns that into seconds.
            // Revit still pumps its message loop between raises, so the UI stays live.
            if (!TryWarmCounts(sender as UIApplication)) e.SetRaiseWithoutDelay();

            // Once the counts are warm, show the full-check cost hint on the panel.
            TryShowScope(sender as UIApplication);

            // Re-verify a slice of the existing results even while Live is paused:
            // a clash resolved during a pause should still stop being listed.
            TryVerifySlice(sender as UIApplication);

            if (!ClashSettings.Current.Enabled) return;
            if (DeferredQueue.PendingCount == 0) return;

            try
            {
                var uiapp = sender as UIApplication;
                if (uiapp == null) return;

                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;

                var doc = uidoc.Document;
                if (doc == null || !doc.IsValidObject) return;

                const int sliceSize = 25;
                var slice = DeferredQueue.Dequeue(doc, sliceSize);
                if (slice.Count == 0) return;

                var sw = Stopwatch.StartNew();
                List<ElementId> notChecked;

                // A slightly larger budget than the in-transaction path: nobody is
                // waiting on us here, but we still want to stay responsive.
                var settings = ClashSettings.Current;
                int saved = settings.TimeBudgetMs;
                settings.TimeBudgetMs = 250;

                try
                {
                    var results = ClashEngine.CheckElementsAllSets(doc, slice, settings, sw, out notChecked);
                    ClashResultStore.Instance.AddRange(results);
                    if (notChecked.Count > 0) DeferredQueue.Enqueue(doc, notChecked);
                }
                finally
                {
                    settings.TimeBudgetMs = saved;
                }

                // Come straight back if there is more waiting.
                if (DeferredQueue.PendingCount > 0) e.SetRaiseWithoutDelay();
            }
            catch (Exception ex)
            {
                Log.Warn("Idle processing failed", ex);
            }
        }

        // ------------------------------------------------------------ warm start

        /// <summary>Set when a document opens; cleared once its index has been built.</summary>
        private static bool _warmPending;

        /// <summary>
        /// Builds the spatial index during idle time, shortly after a document opens,
        /// rather than leaving it to whoever makes the first edit.
        ///
        /// Building it costs 330-420 ms on a large model. Lazily, that lands on the
        /// FIRST edit of the session — which then blows the 120 ms budget and defers
        /// everything, so the very first thing a user does is the one time the tool
        /// feels slow and reports nothing inline ("Budget hit (323 ms); deferred 1
        /// element(s)" in the log). Every edit after it costs ~44 ms.
        ///
        /// Paying it here spends idle time nobody is waiting on, right after an open
        /// while the user is still orienting, and makes the first edit as fast as the
        /// hundredth.
        /// </summary>
        private void TryWarmIndex(UIApplication uiapp)
        {
            if (!_warmPending) return;

            try
            {
                if (uiapp == null) return;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                if (doc == null || !doc.IsValidObject) return;

                _warmPending = false;

                // Warm ONE context per enabled set, so the first edit is fast for every
                // set the live path will check it against — not just the active one.
                var sw = Stopwatch.StartNew();
                var settings = ClashSettings.Current;
                int warmed = 0, totalTargets = 0;

                // The set is passed, not assigned to settings.ActiveSet — see
                // ClashEngine.CheckElementsAllSets for why that mattered.
                foreach (var set in settings.Sets)
                {
                    if (!set.Enabled) continue;
                    var c = ClashContext.GetContext(doc, settings, set);
                    warmed++;
                    if (c != null && c.HostTargets != null) totalTargets += c.HostTargets.Count;
                }
                sw.Stop();

                Log.Info("Index warmed on open: " + warmed + " enabled set(s), "
                         + totalTargets + " target(s) total in " + sw.ElapsedMilliseconds
                         + " ms — the first edit no longer pays for this.");
            }
            catch (Exception ex)
            {
                // Never let warming break anything; the lazy path still works.
                _warmPending = false;
                Log.Warn("Index warm-up failed; it will build on first use", ex);
            }
        }

        /// <summary>
        /// Keeps every enabled set's ClashContext cache from ever going cold on the live edit
        /// path — see ClashContext.RefreshStaleSoon for why the TTL needs this. Cheap when
        /// nothing is close to expiring: one age check per enabled set, no document read.
        /// </summary>
        private void TryRefreshContexts(UIApplication uiapp)
        {
            if (!ClashSettings.Current.Enabled) return;
            try
            {
                if (uiapp == null) return;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                if (doc == null || !doc.IsValidObject) return;

                ClashContext.RefreshStaleSoon(doc, ClashSettings.Current);
            }
            catch (Exception ex)
            {
                Log.Warn("Idle context refresh failed; the live path will rebuild on its own", ex);
            }
        }

        /// <summary>
        /// Advance the per-category element count a slice at a time.
        ///
        /// 25 ms is small enough to be invisible between keystrokes and large enough
        /// that a 310,000-element model is counted within a few seconds of idling.
        /// Unlike the index warm this cannot be done in one go: the whole pass is
        /// nearly seven seconds, and Idling runs on the UI thread.
        /// </summary>
        private static bool _countsWarm;

        /// <returns>True when there is nothing left to count.</returns>
        private bool TryWarmCounts(UIApplication uiapp)
        {
            if (_countsWarm) return true;
            if (_warmPending) return false;           // index first, it matters more

            try
            {
                if (uiapp == null) return false;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return false;
                var doc = uidoc.Document;
                if (doc == null || !doc.IsValidObject) return false;

                // Smaller slices now that they arrive back to back, so Revit keeps
                // a decent share of each cycle for itself.
                _countsWarm = CategoryCatalog.WarmStep(doc, 12);
                return _countsWarm;
            }
            catch (Exception ex)
            {
                _countsWarm = true;
                Log.Warn("Category count warm-up failed; it will build on first use", ex);
                return true;
            }
        }

        /// <summary>
        /// Populate the panel's green/amber/red full-check cost hint once per model, after
        /// the category counts are warm (Measure reads the same collectors, so paying for
        /// it before the warm finishes would just double the cost during opening).
        /// </summary>
        private static bool _scopeShown;

        private void TryShowScope(UIApplication uiapp)
        {
            if (_scopeShown || !_countsWarm) return;
            try
            {
                var uidoc = uiapp != null ? uiapp.ActiveUIDocument : null;
                var doc = uidoc != null ? uidoc.Document : null;
                if (doc == null || !doc.IsValidObject) return;

                var panel = ClashPaneProvider.Panel;
                if (panel == null) return;

                panel.UpdateScopeIndicator(ClashScopeEstimate.Measure(doc, ClashSettings.Current));
                _scopeShown = true;
            }
            catch (Exception ex)
            {
                _scopeShown = true;
                Log.Warn("Full-check scope hint failed", ex);
            }
        }

        // ------------------------------------------------------------ verification

        /// <summary>
        /// Keys already re-verified in the current pass, so the next slice takes rows
        /// that have NOT had their turn.
        ///
        /// This was an integer cursor indexing into ClashResultStore.Snapshot() — which
        /// re-sorts by severity and then detection time on every call. Rows added,
        /// removed or re-severitied between slices shifted position underneath the
        /// cursor, so some pairs were skipped pass after pass while others were
        /// re-verified early. Nothing broke (verification can only remove rows), but a
        /// clash the user had already fixed could sit on the list far longer than the
        /// round-robin implies, which is the one thing this loop exists to prevent.
        ///
        /// Identity beats position: a key survives re-sorting.
        /// </summary>
        private static readonly HashSet<string> _verifiedThisPass = new HashSet<string>(StringComparer.Ordinal);
        private static DateTime _lastVerifyUtc = DateTime.MinValue;

        /// <summary>
        /// Re-checks a slice of the listed clashes in idle time and drops the ones
        /// that no longer exist.
        ///
        /// This is the answer to clashes resolved somewhere the updater cannot see:
        /// inside a link, by an undo, or while Live was paused. It is affordable only
        /// because verifying a KNOWN pair needs no broad phase and no index — see
        /// PairVerifier — so a slice is a handful of direct intersection tests.
        ///
        /// It never adds anything. A pair it cannot answer for (an unloaded link) is
        /// kept, because silently dropping real coordination issues is far worse than
        /// briefly listing a stale one.
        /// </summary>
        private void TryVerifySlice(UIApplication uiapp)
        {
            var s = ClashSettings.Current;

            // The interval gate comes BEFORE the AutoVerify switch, because this pass
            // also refreshes the status store's record of the document — which has to
            // happen whether or not background verification is switched on.
            if ((DateTime.UtcNow - _lastVerifyUtc).TotalSeconds < s.VerifyIntervalSeconds) return;

            try
            {
                if (uiapp == null) return;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                if (doc == null || !doc.IsValidObject) return;

                // Cheap and idempotent — reloads only if the model actually changed,
                // but always re-reads doc.Title so mid-session drift is recorded.
                ClashStatusStore.Instance.LoadFor(doc);
                ResolvedLog.LoadFor(doc);

                if (!s.AutoVerify) { _lastVerifyUtc = DateTime.UtcNow; return; }

                // Stamped BEFORE the empty check, not after it. Returning early without
                // stamping left the interval gate at line 1 permanently open, so with an
                // empty register — the normal state just after opening a model — every
                // single Idling tick paid two LoadFor calls and a locked Snapshot. The
                // one state with no work to do was the one that ran flat out.
                _lastVerifyUtc = DateTime.UtcNow;

                var all = ClashResultStore.Instance.Snapshot();
                if (all.Count == 0) { _verifiedThisPass.Clear(); return; }

                // Rows that have not had their turn this pass. When none are left every
                // listed clash has been checked once, so a new pass begins.
                var slice = new List<ClashResult>();
                foreach (var r in all)
                {
                    if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                    if (_verifiedThisPass.Contains(r.Key)) continue;
                    slice.Add(r);
                    if (slice.Count >= s.VerifySliceSize) break;
                }
                if (slice.Count == 0)
                {
                    // Pass complete. Start the next one from the top; keys belonging to
                    // rows that have since gone drop out with it.
                    _verifiedThisPass.Clear();
                    return;
                }

                // Collecting links is a document-wide query, so only pay for it when
                // the slice actually contains a link pair.
                Dictionary<long, LinkContext> links = null;
                foreach (var r in slice)
                {
                    if (r.LinkInstanceId == null) continue;
                    links = PairVerifier.LinkLookup(doc);
                    break;
                }

                var gone = new List<string>();
                var goneResults = new List<ClashResult>();
                var goneKeys = new List<string>();
                int deferred = 0;
                foreach (var r in slice)
                {
                    _verifiedThisPass.Add(r.Key);
                    ClashSeverity sev;
                    var verdict = PairVerifier.Verify(doc, r, s, links, out sev);

                    if (verdict == PairVerifier.Verdict.Resolved)
                    {
                        gone.Add(r.Key);   // leaves the panel now either way — the clash is not listed

                        // A HOST or (host-vs-host) OTHER element actually deleted in THIS document
                        // has not yet been shown to survive: an undo a moment later would put it
                        // right back. Deferred through the exact same queue the live updater's own
                        // deletion path already uses (QueueDeleted/FlushDeletedResolutions), which
                        // re-checks at save time and drops anything undone before then. Resolved
                        // because the geometry genuinely moved apart (both elements still exist), or
                        // because a LINK's own element vanished — not this document's undo stack to
                        // worry about — is recorded right away, as before. Found reviewing the whole
                        // codebase, 2026-09-26: a deleted target, seen gone by the very next 5-second
                        // tick, then undone, left a permanent "fixed" entry for a clash still there.
                        var deletedId = DeletedElementIn(doc, r);
                        if (deletedId != null)
                        {
                            ClashUpdater.QueueRemovedRow(doc, deletedId, r);
                            deferred++;
                        }
                        else
                        {
                            // Captured whole, not just the key: once it leaves the store its element
                            // names and categories are gone with it, and the resolved log would have
                            // nothing readable to show.
                            goneResults.Add(r);
                            goneKeys.Add(r.Key);
                        }
                    }
                    else if (verdict == PairVerifier.Verdict.StillClashing && sev != r.Severity)
                        ClashResultStore.Instance.UpdateSeverity(r.Key, sev);
                }
                if (gone.Count > 0)
                {
                    ClashResultStore.Instance.RemoveKeys(gone);

                    if (goneResults.Count > 0)
                    {
                        // Against the decision first, so a pair that was accepted and has now
                        // been fixed is remembered as gone — and shows as Reopened if it ever
                        // comes back.
                        ClashStatusStore.Instance.NoteGone(goneKeys);
                        ResolvedLog.Add(goneResults);
                    }
                    Log.Info("Auto-verify: " + gone.Count + " clash(es) no longer present"
                             + (deferred > 0 ? " (" + deferred + " deferred until save/sync)" : "") + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Auto-verify failed", ex);
            }
        }

        /// <summary>
        /// Which element a Resolved verdict is actually about, if it is a genuine deletion IN
        /// THIS DOCUMENT rather than geometry moving apart — null for everything else, including a
        /// host-vs-link pair whose LINK's own element vanished: that is not this document's undo
        /// stack to worry about, and recording it right away is correct.
        /// </summary>
        private static ElementId DeletedElementIn(Document doc, ClashResult r)
        {
            if (r.HostElementId != null && !StillValid(doc, r.HostElementId)) return r.HostElementId;
            if (r.LinkInstanceId == null && r.OtherElementId != null && !StillValid(doc, r.OtherElementId))
                return r.OtherElementId;
            return null;
        }

        /// <summary>
        /// GetElement for a just-deleted id does not reliably return null on its own — the Element
        /// it hands back can throw InvalidObjectException the moment IsValidObject (or anything
        /// else) is asked of it, not only on the GetElement call itself. Found reviewing the whole
        /// codebase, 2026-09-26 (via this method's own headless-adjacent test failing in Revit,
        /// where PairVerifier's equivalent check happens to have avoided hitting this exact
        /// timing). Both calls need the SAME try, not one each.
        /// </summary>
        private static bool StillValid(Document doc, ElementId id)
        {
            try
            {
                var e = doc.GetElement(id);
                return e != null && e.IsValidObject;
            }
            catch { return false; }
        }

        /// <summary>
        /// Undo and redo do NOT run updaters, so without this an undone edit leaves
        /// the results it produced sitting on the list — and an undo that recreates a
        /// clash never gets noticed at all. DocumentChanged does fire, and reports the
        /// affected ids, so both directions are handled: forget what we knew about
        /// those elements, then queue them for a fresh check at idle.
        /// </summary>
        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.IsValidObject || doc.IsLinked) return;

                var addedIds = e.GetAddedElementIds();
                var modifiedIds = e.GetModifiedElementIds();
                var touched = new List<ElementId>();
                touched.AddRange(modifiedIds);
                touched.AddRange(addedIds);
                var deletedIds = e.GetDeletedElementIds();

                // Ahead of NoteChanged, on the same ids, for the same reason it runs for
                // every transaction: a link change here is a signal the panel should show,
                // not a rebuild NoteChanged needs to do anything different for.
                LinkWatch.Observe(doc, addedIds, modifiedIds, deletedIds);
                NotifyLinkNotice();

                // NoteChanged runs for EVERY transaction, not just undo/redo — that is the
                // fix. The updater's own Execute already patches the cached target index
                // inline, but only for elements in a category the trigger's filter
                // watches, which is the union of SOURCE categories across enabled sets
                // (ClashUpdater.AddTriggers). A category that is only ever a TARGET —
                // structural framing checked against pipes, say, with nothing configured
                // to check FROM Structural Framing — has no trigger at all, so the
                // updater's Execute never runs for it and never patches anything.
                //
                // DocumentChanged is different: Revit raises it after every committed
                // transaction, for every touched id, regardless of category or trigger.
                // So it is the one hook that sees a target-only element the updater
                // cannot. Before this, a beam drawn or moved through a pipe was invisible
                // to the broad-phase index until the 10-minute cache TTL happened to
                // expire and rebuild from scratch — the comment on that TTL names this
                // exact case as the reason the limit exists, but a limit only bounds how
                // long the cache stays stale, not how long a clash stays undetected: a
                // pipe edited inside that window found nothing, and nothing retried the
                // pair until the pipe was edited again or a full check ran.
                //
                // Cheap enough to always pay: NoteChanged is a dictionary patch keyed by
                // id, not a rebuild — microseconds for the handful of elements one
                // transaction normally touches — and calling it twice for a source-
                // category edit (once here, once already in Execute) is redundant but
                // harmless, since patching the same ids to the same state twice leaves
                // the index exactly where one patch would have.
                ClashContext.NoteChanged(doc, touched, deletedIds, ClashSettings.Current);

                // Everything below stays gated to undo/redo, exactly as before. Updaters
                // do not run for an undo or a redo at all — that is a Revit API
                // limitation, not a choice made here — so THIS is the only place a
                // source-category element's result rows and deferred re-check happen for
                // those two operations. Running it for every ordinary transaction too
                // would fight the updater: Execute already removes, re-checks and
                // re-adds a source-category element's rows SYNCHRONOUSLY, inside the
                // transaction, which is the whole point of using an IUpdater instead of
                // this event. Doing it again here, after commit, would strip the rows
                // Execute had just restored and replace instant detection with a flicker
                // to empty followed by a deferred idle re-check — for every edit, not
                // just the target-only ones this exists to cover.
                if (e.Operation != Autodesk.Revit.DB.Events.UndoOperation.TransactionUndone
                    && e.Operation != Autodesk.Revit.DB.Events.UndoOperation.TransactionRedone)
                    return;

                // Updaters do not run for undo/redo, so this is the ONLY place a row an undo or a
                // redo orphaned ever gets a chance at being reconciled — nothing downstream (a
                // deferred re-check only ever remembers element ids, never which specific rows
                // their removal orphaned) can do it later. The same gap ClashUpdater.Execute had
                // for a host-changed row, and the same fix: a deletion goes through the SAME
                // deferred-until-save queue a live deletion already uses, so an undo of THIS undo
                // a moment later still cannot leave a false "fixed" entry; a target-only or
                // host-changed row is reconciled directly with PairVerifier. Found reviewing the
                // whole codebase, 2026-09-27, right after the same-shaped ClashUpdater gap.
                foreach (var id in deletedIds)
                    foreach (var r in ClashResultStore.Instance.RemoveForElement(id))
                        if (r != null && !string.IsNullOrEmpty(r.Key))
                            ClashUpdater.QueueRemovedRow(doc, id, r);

                List<ClashResult> hostChanged;
                var targetOnly = ClashUpdater.SplitRemovedRows(touched, ClashResultStore.Instance.RemoveForElement, out hostChanged);
                if (targetOnly.Count > 0) ClashUpdater.ReverifyTargetOnly(doc, ClashSettings.Current, targetOnly);
                if (hostChanged.Count > 0) ClashUpdater.ReverifyHostChanged(doc, ClashSettings.Current, hostChanged);

                // Still deferred for a FRESH check: reconciling the OLD rows above only answers
                // whether what was already listed is still true, never whether the undo/redo
                // introduced a clash nobody has seen yet. Gated on Enabled, unlike the
                // reconciliation above - recording a genuine fix belongs to the ledger regardless
                // of whether live checking happens to be paused, the same as Recheck/Full Check
                // are never gated on it either, but silently ADDING newly found rows while the
                // user has paused live checking would defeat the pause.
                if (touched.Count > 0 && ClashSettings.Current.Enabled)
                    DeferredQueue.Enqueue(doc, touched);
            }
            catch (Exception ex)
            {
                Log.Warn("Document-changed handling failed", ex);
            }
        }

        /// <summary>
        /// A deletion is only a FIX once it is in the saved model, so this is where
        /// deletions reach the permanent record.
        ///
        /// Recording them at the moment of deletion was wrong three ways, and the
        /// first two survived a rewrite before this landed:
        ///
        ///   - written from inside the updater it was file I/O inside Revit's
        ///     transaction, against the updater's own "be fast" rule;
        ///   - written at Idling it was out of the transaction but still too early —
        ///     an undo a few seconds later had already been beaten to it;
        ///   - and closing the model without saving discarded the deletion while the
        ///     ledger kept the entry, claiming a fix that had never happened.
        ///
        /// Saving is the moment all three stop being a problem: the element is gone
        /// from a file on disk, or it is not. Both save paths are hooked because a
        /// workshared local saves through DocumentSaved and syncs through
        /// DocumentSynchronizedWithCentral.
        ///
        /// A session that deletes and never saves records nothing. That is correct,
        /// not a gap — an unsaved deletion is not a fix.
        /// </summary>
        private void OnDocumentSaved(object sender, Autodesk.Revit.DB.Events.DocumentSavedEventArgs e)
        {
            FlushIfActive(sender, e == null ? null : e.Document);
        }

        private void OnDocumentSynced(
            object sender, Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            FlushIfActive(sender, e == null ? null : e.Document);
        }

        /// <summary>
        /// Record a saved document's queued deletions - but only if it is the ACTIVE one.
        ///
        /// The ledgers are a session singleton aimed at the adopted (= active) model, so flushing a
        /// BACKGROUND document's deletions writes them into a ledger that belongs to a different model:
        /// a wrong entry in the permanent record, which is worse than a missing one. Save As already
        /// refused to do this (OnDocumentSavedAs); a plain Save and a Synchronize did not. The entries are
        /// simply left queued, correctly keyed, and recorded when that document is the active one and is
        /// saved again.
        /// </summary>
        private static void FlushIfActive(object sender, Document doc)
        {
            if (doc == null) return;
            if (!IsActiveDocument(sender, doc))
            {
                Log.Info("Saved a document that is not the active one ('" + SafeTitle(doc)
                         + "'): its queued deletions stay queued, so nothing is recorded against the model the ledgers belong to.");
                return;
            }
            ClashUpdater.FlushDeletedResolutions(doc);
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc.Title; } catch { return "?"; }
        }

        /// <summary>
        /// Save As is the one save that MOVES a document's key, and until this existed it
        /// silently threw away every deletion waiting to be recorded.
        ///
        /// Measured on a throwaway copy, 2026-09-18, rather than reasoned about: a real
        /// File > Save As fired DocumentSavedAs and NOT DocumentSaved, so neither handler
        /// above ran and FlushDeletedResolutions was never called at all. By the time the
        /// event arrives, e.Document.PathName — which IS the key, see ClashEngine.DocKey —
        /// already reads the NEW path, while everything queued still carries the OLD one,
        /// so simply subscribing and flushing would still have matched nothing. Both
        /// halves had to be fixed, and the event itself supplies what the second needs:
        /// OriginalPath is exactly the key the entries were queued under.
        ///
        /// The ledgers are re-pointed BEFORE the flush because they are keyed by model
        /// title and are, at this instant, still aimed at the file this document used to
        /// be. The deletion belongs to the file that now CONTAINS it: the original on disk
        /// was never saved with it, so recording the fix against the original would claim
        /// something of that file which is not true of it.
        /// </summary>
        private void OnDocumentSavedAs(object sender, Autodesk.Revit.DB.Events.DocumentSavedAsEventArgs e)
        {
            try
            {
                if (e == null || e.Document == null || !e.Document.IsValidObject) return;

                // OriginalPath is the key for a model that had been saved. For one that had NEVER been saved it
                // is empty, and the key was captured in OnDocumentSavingAs.
                string from = e.OriginalPath;
                if (string.IsNullOrEmpty(from)) from = TakeSavingAsKey(e.Document);
                int moved = ClashUpdater.RekeyPending(from, ClashEngine.DocKey(e.Document));

                // The ledgers are a SESSION singleton aimed at the adopted (= active) model. A Save
                // As on some OTHER open document would drag them onto itself, and every later fix in
                // the model actually being worked in would be recorded against the wrong one. Its
                // queued entries are still moved to the new key (above) so they stay correct; they
                // are simply not flushed into a ledger that belongs to a different model.
                if (!IsActiveDocument(sender, e.Document))
                {
                    if (moved > 0)
                        Log.Info("Save As on a document that is not the active one: " + moved
                                 + " queued deletion(s) re-keyed, ledgers left on the session model.");
                    return;
                }

                ClashSettings.LoadFor(e.Document);
                ClashStatusStore.Instance.LoadFor(e.Document);
                ResolvedLog.LoadFor(e.Document);

                // _adoptedDocKey is compared against ClashEngine.DocKey, which is the file PATH —
                // and Save As just changed it. Left stale, the very next ViewActivated (which fires
                // on almost anything, not just a real model switch) would see a "different"
                // document and run the full Adopt(): clear every result and reload settings from
                // the new title, which has no saved file yet, so it silently falls back to
                // defaults — a Save As that quietly wipes the panel and the filter a moment later.
                // Found reviewing the whole codebase, 2026-09-26.
                _adoptedDocKey = ClashEngine.DocKey(e.Document);

                if (moved > 0)
                    Log.Info("Save As: " + moved + " queued deletion(s) re-keyed from '"
                             + e.OriginalPath + "' so they record against the saved-as model.");

                ClashUpdater.FlushDeletedResolutions(e.Document);
            }
            catch (Exception ex)
            {
                Log.Warn("Save-as handling failed", ex);
            }
        }

        // The queue key of a never-saved model, captured just before its first Save As, by the document's
        // creation GUID - which belongs to exactly one document and, unlike its title or path, does not
        // change underneath the entry. Never a guess and never shared between models.
        private static readonly Dictionary<Guid, string> SavingAsKeys = new Dictionary<Guid, string>();

        /// <summary>
        /// The FIRST Save As of a model that has never been saved used to strand its queued deletions:
        /// they were keyed "unsaved::&lt;title&gt;", the event's OriginalPath is empty, and by the time
        /// DocumentSavedAs fires the title has already changed, so the old key could not be rebuilt. The
        /// rule against guessing stands - sweeping up every "unsaved::" entry could attribute one model's
        /// fix to another's ledger, and a wrong entry in the permanent record is worse than a missing one.
        ///
        /// So the exact key is read here, BEFORE the save, while the title is still the old one, and looked
        /// up afterwards by the document's creation GUID. A save that is cancelled or fails simply leaves an
        /// entry that the next attempt overwrites.
        /// </summary>
        private void OnDocumentSavingAs(object sender, Autodesk.Revit.DB.Events.DocumentSavingAsEventArgs e)
        {
            try
            {
                var doc = e == null ? null : e.Document;
                if (doc == null || !doc.IsValidObject) return;
                if (!string.IsNullOrEmpty(doc.PathName)) return;   // a saved model: OriginalPath will be the key
                lock (SavingAsKeys) SavingAsKeys[doc.CreationGUID] = ClashEngine.DocKey(doc);
            }
            catch (Exception ex)
            {
                Log.Warn("Save-as preparation failed", ex);
            }
        }

        /// <summary>The key captured for this document by <see cref="OnDocumentSavingAs"/>, or empty.</summary>
        private static string TakeSavingAsKey(Document doc)
        {
            try
            {
                string key;
                lock (SavingAsKeys)
                {
                    var id = doc.CreationGUID;
                    if (!SavingAsKeys.TryGetValue(id, out key)) return "";
                    SavingAsKeys.Remove(id);
                }
                return key ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Is <paramref name="doc"/> the document the user is working in? Compared by path, because
        /// at DocumentSavedAs time the renamed document already reports its NEW path, as does the
        /// active document if it is the one that was saved. If it cannot be told (no application
        /// on the event, or the lookup throws) the answer is "yes" - the behaviour before this
        /// check existed - rather than silently dropping a real fix.
        /// </summary>
        private static bool IsActiveDocument(object sender, Document doc)
        {
            try
            {
                var app = sender as Autodesk.Revit.ApplicationServices.Application;
                if (app == null) return true;
                var ui = new UIApplication(app).ActiveUIDocument;
                if (ui == null || ui.Document == null) return false;
                // The GUID is only ever read for a document whose path is EMPTY. Reading it for every document
                // would let a throw on some unrelated saved one land in the catch below, which answers "yes,
                // active" - and a guard that says yes when it cannot tell is no guard.
                string activePath = ui.Document.PathName, docPath = doc.PathName;
                return SameDocument(
                    activePath, string.IsNullOrEmpty(activePath) ? ui.Document.CreationGUID : Guid.Empty,
                    docPath, string.IsNullOrEmpty(docPath) ? doc.CreationGUID : Guid.Empty);
            }
            catch { return true; }
        }

        /// <summary>
        /// Are these the same document? By path - and, when the path is EMPTY, by creation GUID. Two documents
        /// that have never been saved BOTH have an empty path, so the path cannot tell them apart; the GUID
        /// can. For saved documents the GUID cannot (a copy of a file has the same GUID as the original),
        /// which is why the path comes first. Pure, so it is tested with plain values.
        /// </summary>
        internal static bool SameDocument(string pathA, Guid guidA, string pathB, Guid guidB)
        {
            if (!string.Equals(pathA ?? "", pathB ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(pathA)) return guidA == guidB;
            return true;
        }

        // ------------------------------------------------------------------ documents

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            // A LINKED document opening in the background — at minimum when the host
            // document is opened and Revit loads its links; NOT confirmed for a live
            // reload of an already-open link, which was tested directly on Revit 2026
            // and raises neither this event nor DocumentClosing (see the note on
            // ClashContext.GetContext) — is NOT the user switching models. Acting on it
            // cleared the host's results and — the real bug — called LoadFor(linkDoc),
            // which swapped the settings to the link file's key: the link has no saved
            // settings, so it seeded the default filter and the host model's rules
            // vanished for the rest of the session. Only the host/project document
            // drives session state.
            if (e.Document == null || e.Document.IsLinked || e.Document.IsFamilyDocument) return;
            Adopt(e.Document, "opened");
        }

        /// <summary>
        /// The document whose settings and stores are currently loaded. Null until the
        /// first one is adopted.
        /// </summary>
        private static string _adoptedDocKey;

        /// <summary>
        /// Make <paramref name="doc"/> the session's document: its filter, its triage,
        /// its results, nothing left over from the last one.
        ///
        /// Called from BOTH the open event and the view-activated event. Opening was not
        /// enough on its own: DocumentOpened does not fire when you switch back to a model
        /// that is already open, so with two models open, A's edits were being checked
        /// against B's filter while ClashStatusStore and ResolvedLog followed the ACTIVE
        /// document — A's rows judged by B's rules and written against A's history. The
        /// per-model settings split was reasoned about carefully (see the comment on
        /// SettingsPath: inheriting another project's exclusions is the quiet, dangerous
        /// failure); this was the same hazard reached by a different route.
        ///
        /// Order matters. Settings BEFORE the stores: the filter decides what the warm-up
        /// indexes, so loading it second would index the previous model's rules.
        /// </summary>
        private static void Adopt(Document doc, string why)
        {
            // A family document (Edit Family / Edit In-Place) is not a session document
            // either — it has no clash settings of its own, and switching to it, then back
            // to the project via Load into Project, used to run this TWICE in a row: once
            // "adopting" the family (wiping the project's results and settings) and once
            // "returning" to the project (wiping them again), even though the project's own
            // session never actually changed. Found reviewing the whole codebase, 2026-09-27.
            if (doc == null || !doc.IsValidObject || doc.IsLinked || doc.IsFamilyDocument) return;

            // ALREADY OURS — do nothing. Opening a model raises both events: ViewActivated
            // fires as its first view comes up, then DocumentOpened ~150 ms later. Without
            // this guard every open reloaded settings, both stores and the caches twice.
            // OnDocumentClosing forgets the key, so closing and reopening the same model
            // still re-adopts properly rather than being skipped as "already ours".
            string key = ClashEngine.DocKey(doc);
            if (key == _adoptedDocKey) return;

            _adoptedDocKey = key;

            // Results are per-session and per-document; a fresh document starts clean.
            ClashResultStore.Instance.Clear();
            DeferredQueue.Clear(null);
            ClashEngine.InvalidateAll();   // target ids belong to the old document

            // A notice naming the last model's link has no business surviving into this
            // one, and this is also what stops a false notice from Revit's own burst of
            // LinkedResourceOpened events while a model's links come up during open — by
            // the time Adopt runs (DocumentOpened, ~150 ms after the first view), any of
            // those have already fired and this clears them along with everything else.
            LinkWatch.Reset(doc);
            NotifyLinkNotice();

            ClashSettings.LoadFor(doc);
            ClashStatusStore.Instance.LoadFor(doc);
            ResolvedLog.LoadFor(doc);

            // THE TRIGGERS BELONG TO THE OLD MODEL'S FILTER. They are built from the
            // source category list at registration, and until now that only ever
            // happened at startup — before any document existed, so from the machine
            // default — plus when the Filter or Audit dialog rebuilt them by hand.
            // Switch from a model watching Pipes to one watching Ducts and live
            // checking went on firing for Pipes only, silently, until somebody happened
            // to open the Filter window. The first model of a session had the same
            // problem whenever its own filter differed from the default.
            //
            // Requested rather than done here: AddTriggers touches UpdaterRegistry,
            // which needs a real API context, and Adopt runs from ViewActivated /
            // DocumentOpened. OnIdling does the work at the next safe moment.
            ClashUpdater.RequestTriggerRebuild();

            // Arm the warm-up rather than doing it here: on the open path this fires
            // during the open itself, and adding 400 ms to opening a model is the wrong
            // trade.
            _warmPending = true;
            _countsWarm = false;   // a new document needs a new count
            _scopeShown = false;   // ...and a fresh full-check cost estimate

            Log.Info("Session document is now '" + doc.Title + "' (" + why
                     + "): filter, triage and results reloaded for it.");

            // The panel's list just emptied, and 'my clashes vanished' is exactly the
            // moment people conclude live checking is broken.
            try
            {
                var panel = UI.ClashPaneProvider.Panel;
                if (panel != null)
                {
                    // The Live and Group boxes were seeded once, in the constructor,
                    // and the panel outlives every document — so without this they go
                    // on showing the previous model's settings while the behaviour
                    // follows the new ones.
                    panel.SyncToSettings();

                    panel.SetEmptyReason(UI.ClashPanel.EmptyReason.DocumentSwitched);
                    panel.SetStatus("Switched to \"" + doc.Title
                        + "\" — its own filter and triage are loaded. Run a check to list its clashes.");
                }
            }
            catch (Exception ex) { Log.Warn("Panel notify on document switch failed", ex); }
        }

        /// <summary>
        /// Switching between two models that are ALREADY open raises no document event at
        /// all, so the active view is what tells us. It fires on every view change, so the
        /// document key is compared first and the same-document case costs a string
        /// compare.
        /// </summary>
        private void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try
            {
                var doc = e == null ? null : e.Document;
                if (doc == null || !doc.IsValidObject || doc.IsLinked || doc.IsFamilyDocument) return;
                if (ClashEngine.DocKey(doc) == _adoptedDocKey) return;   // same model, different view
                Adopt(doc, "switched to");
            }
            catch (Exception ex)
            {
                Log.Warn("View-activated handling failed", ex);
            }
        }

        /// <summary>
        /// A link being LOADED — including one that was UNLOADED when the scan-context
        /// cache was built, which is the one case that survived both D1 and D2's fixes.
        ///
        /// GetContext's own validation (see the note there) catches a CACHED link going
        /// invalid — a reload, or an unload of a link the cache already knew about, both
        /// measured to flip the cached Document.IsValidObject false. What it cannot catch
        /// is a link that was never cached at all because it was unloaded at build time:
        /// there is no stale reference for it to notice, since there was never a
        /// reference. Confirmed live on Snowdon: a cache built with Structural unloaded
        /// stayed unaware of it after Structural was loaded mid-session — the cache
        /// entry count did not move, and a fresh GetContext call still returned the same
        /// stale entry, Structural absent, with nothing to check ever finding it.
        ///
        /// DocumentOpened does not cover this — see OnDocumentClosing's note; neither
        /// Load() nor Reload() raise it. This event does, reliably, for both: confirmed
        /// directly by subscribing and watching it fire on Load() and on Reload(), never
        /// on Unload() (which needs no separate handling, since GetContext's Document
        /// check already covers an unload of anything the cache knew about).
        /// </summary>
        private void OnLinkedResourceOpened(
            object sender, Autodesk.Revit.DB.Events.LinkedResourceOpenedEventArgs e)
        {
            try
            {
                if (e != null && e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded)
                {
                    ClashContext.InvalidateCache();

                    // The event's own resource path names the link even for a link that
                    // is not currently in any cache — the one piece of information
                    // OnDocumentChanged's companion event (see LinkWatch.Observe) cannot
                    // offer here, since it would only see the RevitLinkTYPE, not which
                    // file it points at.
                    string name = null;
                    try { name = System.IO.Path.GetFileNameWithoutExtension(e.LinkedResourcePathName); }
                    catch { }
                    // The clash rows carry the link TYPE's name, which is not always the file's (a renamed type):
                    // record the type name(s) that point at this file, and the file's own name only if none does.
                    var typeNames = LinkWatch.TypeNamesForPath(e.Document, e.LinkedResourcePathName);
                    if (typeNames.Count == 0) LinkWatch.RecordLoadedOrReloaded(name);
                    else foreach (var typeName in typeNames) LinkWatch.RecordLoadedOrReloaded(typeName);
                    NotifyLinkNotice();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Linked-resource-opened handling failed", ex);
            }
        }

        /// <summary>Push the current link-change notice (if any) to the panel. Static
        /// because Adopt, which also calls this, is static.</summary>
        private static void NotifyLinkNotice()
        {
            try
            {
                var panel = UI.ClashPaneProvider.Panel;
                if (panel != null) panel.RefreshLinkNotice();
            }
            catch (Exception ex)
            {
                Log.Warn("Link-notice panel refresh failed", ex);
            }
        }

        private void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            try
            {
                // Unloading a link closes its background document; that must not wipe the
                // host session's results — settings, triage and the panel all stay put.
                //
                // A stale link Document in the warm scan-context cache is real and was
                // measured directly (see the note on ClashContext.GetContext, which is
                // where it is actually fixed): reloading OR unloading a link invalidates
                // the cached Document reference (both measured to flip IsValidObject
                // false). What did NOT hold up is this event as the way to hear about it
                // — subscribed directly and watched neither DocumentClosing nor
                // DocumentOpened fire for RevitLinkType.Reload(), Unload() or Load(), on
                // Revit 2026. So despite what an earlier version of this comment claimed,
                // a link reload cannot be relied on to reach this handler at all — LOADING
                // one is covered by OnLinkedResourceOpened instead, which does fire.
                //
                // InvalidateCache stays here anyway, cheap and harmless, for whatever
                // path — if any — DOES raise this for a link (a different Revit version,
                // a workflow not tested, the host document itself closing with links
                // attached). It is not load-bearing; GetContext no longer trusts a cached
                // link without checking it regardless of whether anything told it to.
                if (e.Document != null && e.Document.IsLinked)
                {
                    ClashContext.InvalidateCache();
                    return;
                }
                if (e.Document == null) return;

                // Its own deferred work goes either way — that queue is already per
                // document, and a closing model's pending ids are dead whoever is active.
                DeferredQueue.Clear(e.Document);

                // Likewise its queued DELETIONS awaiting a save: closing ends their chance of ever being
                // recorded, and left behind they could be re-keyed onto a different model that reuses the
                // name (see ClashUpdater.DropPending).
                int dropped = ClashUpdater.DropPending(ClashEngine.DocKey(e.Document));
                if (dropped > 0)
                    Log.Info("Closed '" + SafeTitle(e.Document) + "' with " + dropped
                             + " queued deletion(s) never recorded: an unsaved deletion is not a fix.");

                // Same reasoning for its focus view: the entry holds an ElementId in the
                // document that is closing, so it is dead whichever model stays active.
                // Above the adoption guard for exactly that reason.
                UI.FocusHandler.Forget(e.Document);

                // BUT ONLY THE SESSION'S OWN DOCUMENT CLEARS THE SESSION. With two projects
                // open and A active, closing B used to clear A's results and drop A's warm
                // target index — so A's next edit paid the rebuild and blew the update
                // budget, the panel emptied with whatever banner happened to be showing,
                // and nothing re-adopted because A was still the adopted document.
                //
                // That was survivable while two open models were an accident. Adopt() and
                // ViewActivated made it a supported way to work, which is what turned this
                // into a real fault: it is the failure DocumentSwitched exists to explain,
                // arrived at from the other side and with no explanation at all.
                if (ClashEngine.DocKey(e.Document) != _adoptedDocKey) return;

                ClashResultStore.Instance.Clear();
                ClashEngine.InvalidateAll();

                // Forget it, so reopening the same model is a real adoption rather than
                // being skipped by Adopt's "already ours" guard.
                _adoptedDocKey = null;
            }
            catch { }
        }
    }
}
