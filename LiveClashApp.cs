using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

                // The early follow after a refused Save As carry (an API context, like the panel's events).
                try { _followEvent = ExternalEvent.Create(new FollowHandler()); }
                catch (Exception ex) { Log.Warn("Could not create the follow event", ex); }

                ClashUpdater.Register(AddInId);

                application.Idling += OnIdling;
                application.ControlledApplication.DocumentClosing += OnDocumentClosing;
                application.ControlledApplication.DocumentClosed += OnDocumentClosed;
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
                // Which build, on which Revit and runtime: the first thing a pilot's log is asked.
                // A separate line, so tools that match "Live Clash started." exactly keep working.
                Log.Info("Version " + VersionText() + " on Revit " + application.ControlledApplication.VersionNumber
                         + " (" + application.ControlledApplication.VersionBuild + "), "
                         + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ".");
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
                application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
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
            // Last, so a failed shutdown is counted in it. A pilot is judged on this line.
            Log.Info(Log.SessionSummary());
            return Result.Succeeded;
        }

        /// <summary>The add-in's own version (the csproj Version), as the log and the pilot notes name it.</summary>
        internal static string VersionText()
        {
            try
            {
                var asm = typeof(LiveClashApp).Assembly;
                var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
                var v = info != null && !string.IsNullOrEmpty(info.InformationalVersion) ? info.InformationalVersion : asm.GetName().Version.ToString();
                // The SDK appends "+<commit>" when source link is on; the plain number is what people quote.
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
            catch { return "unknown"; }
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
        /// The results of the session document are being thrown away because the RULES changed (the filter, the clearance, the audit drops, a
        /// settings re-read): the list is cleared and the document's held link rows are let go with it (delta review 3, D3-2).
        /// </summary>
        internal static void ClearResultsForNewRules()
        {
            ClashResultStore.Instance.ClearForNewRules(_adoptedDocKey);
        }

        /// <summary>
        /// The settings were read again mid-session (the file was unreadable at open, and a retry has now swapped the defaults for the model's
        /// own settings). The rules in force changed under the listed clashes, so this does what a Filter change does (FilterHandler): the warm
        /// scan contexts go, the result store is cleared and the panel says why. Without it a clearance-only clash found under the defaults is
        /// re-tested under the real rules, called Resolved and recorded as fixed with no change to the model, and a clash only the real scope finds
        /// stays unlisted while the notice reads as all clear (delta review round 2, DM4). Called from OnIdling, which is a safe API context;
        /// the getter that did the swap can run inside the updater and so only raises the flag. Returns whether it acted.
        /// </summary>
        internal static bool HandlePendingRulesReload()
        {
            if (!ClashSettings.TakeRulesReloaded()) return false;
            try
            {
                ClashEngine.InvalidateAll();
                ClearResultsForNewRules();   // the list and the held link rows
                int dropped = ClashUpdater.DropPendingMoved(_adoptedDocKey);   // moved fixes judged under the old rules would be recorded as fixes of the new (a deletion is kept)
                ClashChangeTracker.Reset();   // a baseline taken under the old rules would report their difference as a link's
                var panel = UI.ClashPaneProvider.Panel;
                if (panel != null)
                {
                    panel.SyncToSettings();
                    panel.SetEmptyReason(UI.ClashPanel.EmptyReason.FilterChanged);
                    panel.SetStatus("Your settings were read again, so the rules changed and the list was cleared. Run a full check.");
                }
                Log.Info("Settings re-read mid-session: results cleared like a filter change" + (dropped > 0 ? ", " + dropped + " queued fix(es) dropped" : "") + "; a full check is needed.");
            }
            catch (Exception ex) { Log.Warn("Resetting the results after a settings re-read failed", ex); }
            return true;
        }

        /// <summary>
        /// The top of an idle tick: read the settings (a retry that is due swaps the rules here) and reset the results if they changed. A throw from
        /// the retry (a file read, a parse) is logged and the tick carries on: it sat outside any try and would have left the Idling event, as the
        /// guard in HandlerGuard.SettingsReloaded would have let it leave a panel button (delta check of DR4).
        /// </summary>
        internal static void RetrySettingsAndReset()
        {
            try { ClashSettings.RetryUnreadable(); }
            catch (Exception ex) { Log.Warn("The settings retry failed on an idle tick; carrying on", ex); }
            HandlePendingRulesReload();
        }

        /// <summary>
        /// Drains the deferred queue in small slices while Revit has nothing else to do.
        /// This is what keeps a 5000-element paste from freezing the session.
        /// </summary>
        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (RunIdleTick(sender as UIApplication)) e.SetRaiseWithoutDelay();
        }

        [Conditional("DEV")]
        private static void Trace(string step)
        {
        }

        /// <summary>
        /// One idle tick, split out of OnIdling (an event handler cannot be called with a made-up IdlingEventArgs) so the in-Revit
        /// checks can run it. Returns whether Idling should be raised again at once. The steps and their ORDER are IdleSchedule.Order (pinned by
        /// the headless checks IS-1 and IS-2). Each step is guarded on its own: a throw (a settings read that fails, a document that cannot be
        /// read) is logged and the tick carries on with the next step, so nothing leaves the Idling event (D3-6, N2).
        /// </summary>
        internal static bool RunIdleTick(UIApplication uiapp)
        {
            bool raiseAgain = false;
            foreach (var step in IdleSchedule.Order)
            {
                Trace(step.ToString());
                try { if (RunIdleStep(step, uiapp)) raiseAgain = true; }
                catch (Exception ex)
                {
                    // A step that keeps throwing would log a stack every tick, and ticks can be raised without delay (delta review I-2).
                    int held;
                    if (IdleWarnings.ShouldLog(step.ToString(), DateTime.UtcNow, out held))
                        Log.Warn("Idle step " + step + " failed; carrying on with the tick" + (held > 0 ? " (" + held + " more failures since the last line)" : ""), ex);
                }
            }
            return raiseAgain;
        }

        private static readonly WarnThrottle IdleWarnings = new WarnThrottle(60);

        /// <summary>One step of the idle tick. Returns whether Idling should be raised again at once.</summary>
        private static bool RunIdleStep(IdleStep step, UIApplication uiapp)
        {
            switch (step)
            {
                case IdleStep.FollowPath:
                    {
                        // The active document's path may have moved under the session (a Detach): follow it before anything else acts (fix B).
                        var uidoc = uiapp == null ? null : uiapp.ActiveUIDocument;
                        if (uidoc != null) FollowPathChange(uidoc.Document);
                        _followRequested = false;   // this tick's follow served a pending request, if there was one
                        return false;
                    }

                case IdleStep.Triggers:
                    // Safe API context — do any pending trigger rebuild here, not in the dialog.
                    ClashUpdater.RebuildTriggersIfRequested();
                    return false;

                case IdleStep.SettingsReset:
                    // The settings were re-read mid-session (DM4, D3-1). The retry runs when the settings are first READ, so read them first: the
                    // swap happens here, at the top of the tick, and the reset follows before anything re-tests a row under the new rules. Read
                    // later (the context refresh, the verify slice) it swapped them after the reset and the old rows were judged under the new
                    // rules in the same tick.
                    RetrySettingsAndReset();
                    return false;

                case IdleStep.WarmIndex:
                    // Build the index before the user's first edit has to.
                    TryWarmIndex(uiapp);
                    return false;

                case IdleStep.RefreshContexts:
                    // Refresh a per-set context BEFORE its 10-minute TTL lapses, so the next live edit never has to pay for the rebuild inside
                    // its own transaction. Same reasoning as TryWarmIndex just above; see ClashContext.RefreshStaleSoon.
                    TryRefreshContexts(uiapp);
                    return false;

                case IdleStep.WarmCounts:
                    // Left to Revit's own cadence, Idling fires about every 2.5 seconds, so 25 ms slices counted 19,000 elements a minute and would
                    // have taken eleven minutes on this model. Asking to be raised again immediately turns that into seconds. Revit still pumps its
                    // message loop between raises, so the UI stays live.
                    return !TryWarmCounts(uiapp);

                case IdleStep.ShowScope:
                    // Once the counts are warm, show the full-check cost hint on the panel.
                    TryShowScope(uiapp);
                    return false;

                case IdleStep.VerifySlice:
                    // Re-verify a slice of the existing results even while Live is paused: a clash resolved during a pause should still stop being
                    // listed.
                    TryVerifySlice(uiapp);
                    return DrainReconcileBacklog(uiapp);

                case IdleStep.Drain:
                    return DrainStep(uiapp);

                default:
                    return false;
            }
        }

        /// <summary>
        /// After a rollback pass that stopped by time, the queued fixes it did not reach are asked again here, one time-boxed slice per tick
        /// (at most ReconcilePlan.TickMaxItems entries and TickBudgetMs ms), WITHOUT waiting for the verify interval, and Idling is asked to come
        /// straight back while any are left (delta review L-3). Each slice is as short as before; only the wait between slices goes, so the
        /// rows a rolled-back move took off the panel are back in seconds, not minutes. True when more is waiting.
        /// </summary>
        internal static bool DrainReconcileBacklog(UIApplication uiapp)
        {
            if (HandlePendingRulesReload()) return false;   // like VerifySliceFor: a swap that landed after the tick's reset step must be reset before queued fixes are asked under the new rules (R2-7)
            var uidoc = uiapp == null ? null : uiapp.ActiveUIDocument;
            if (uidoc == null) return false;
            var doc = uidoc.Document;
            if (!IsSessionDocument(doc)) return false;
            if (!ClashUpdater.ReconcileBacklogRemains(doc)) return false;

            ClashUpdater.ReconcilePendingSlice(doc, ReconcilePlan.TickMaxItems, ReconcilePlan.TickBudgetMs, "tick");
            return ClashUpdater.ReconcileBacklogRemains(doc);
        }

        /// <summary>The deferred queue's slice. True when more is waiting and Idling should come straight back.</summary>
        private static bool DrainStep(UIApplication uiapp)
        {
            if (!ClashSettings.Current.Enabled) return false;
            if (DeferredQueue.PendingCount == 0) return false;

            try
            {
                if (uiapp == null) return false;

                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return false;

                // Come straight back if there is more waiting.
                return DrainDeferredSliceFor(uidoc.Document);
            }
            catch (Exception ex)
            {
                Log.Warn("Idle processing failed", ex);
                return false;
            }
        }

        /// <summary>
        /// One slice of the deferred queue for <paramref name="doc"/>. Split out of OnIdling so the
        /// in-Revit checks can hand it a document directly. Returns true when more is waiting.
        /// </summary>
        internal static bool DrainDeferredSliceFor(Document doc)
        {
            // Only the session's own document: its queue is the one the store's rows came from (H1).
            if (!IsSessionDocument(doc)) return false;
            {

                const int sliceSize = 25;
                var slice = DeferredQueue.Dequeue(doc, sliceSize);
                if (slice.Count == 0) return false;

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

                    // What the forward pass could not finish is queued again FIRST (review L2): the reverse check below may throw.
                    if (notChecked.Count > 0) DeferredQueue.Enqueue(doc, notChecked);

                    // The reverse check for any TARGET in the slice (review N1), with the first target always finished so
                    // one with many sources near it cannot be queued again for ever. In its own try (L2); a pair it lists is
                    // not a fix, so a moved fix queued for the same key leaves the queue (L6).
                    try
                    {
                        List<ElementId> notCheckedTargets;
                        var targetRows = ClashEngine.CheckTargetsAllSets(doc, slice, settings, sw, true, out notCheckedTargets);
                        ClashResultStore.Instance.AddRange(targetRows);
                        ClashUpdater.DropPendingMovedForKeys(ClashEngine.DocKey(doc), targetRows.Where(r => r != null && r.Key != null).Select(r => r.Key).ToList());
                        if (notCheckedTargets.Count > 0) DeferredQueue.Enqueue(doc, notCheckedTargets);
                    }
                    catch (Exception ex) { Log.Warn("The reverse check failed on an idle slice; the forward pass is unaffected", ex); }
                }
                finally
                {
                    settings.TimeBudgetMs = saved;
                }

                return DeferredQueue.PendingCount > 0;
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
        private static void TryWarmIndex(UIApplication uiapp)
        {
            if (!_warmPending) return;

            try
            {
                if (uiapp == null) return;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                if (!IsSessionDocument(doc)) return;   // a family being edited waits; the project is warmed when it is back

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
        private static void TryRefreshContexts(UIApplication uiapp)
        {
            try
            {
                if (!ClashSettings.Current.Enabled) return;   // inside the try: a settings read that throws must not leave the idle handler (D3-6, N2)
                if (uiapp == null) return;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                if (!IsSessionDocument(doc)) return;

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
        private static bool TryWarmCounts(UIApplication uiapp)
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
                if (!IsSessionDocument(doc)) return true;   // not now: counted when the session's model is back, without spinning Idling

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

        private static void TryShowScope(UIApplication uiapp)
        {
            if (_scopeShown || !_countsWarm) return;
            try
            {
                var uidoc = uiapp != null ? uiapp.ActiveUIDocument : null;
                var doc = uidoc != null ? uidoc.Document : null;
                if (!IsSessionDocument(doc)) return;

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
        private static void TryVerifySlice(UIApplication uiapp)
        {
            ClashSettings s;
            try { s = ClashSettings.Current; }
            catch (Exception ex) { Log.Warn("Auto-verify could not read the settings", ex); return; }   // D3-6, N2

            // The interval gate comes BEFORE the AutoVerify switch, because this pass
            // also refreshes the status store's record of the document — which has to
            // happen whether or not background verification is switched on.
            if ((DateTime.UtcNow - _lastVerifyUtc).TotalSeconds < s.VerifyIntervalSeconds) return;

            try
            {
                if (uiapp == null) return;
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return;
                VerifySliceFor(uidoc.Document);
            }
            catch (Exception ex)
            {
                Log.Warn("Auto-verify failed", ex);
            }
        }

        /// <summary>
        /// One auto-verify slice against <paramref name="doc"/>, past the interval gate. Split out of
        /// TryVerifySlice so the in-Revit checks can hand it a document directly.
        /// </summary>
        internal static void VerifySliceFor(Document doc)
        {
            ClashSettings s;
            try { s = ClashSettings.Current; }
            catch (Exception ex) { Log.Warn("Auto-verify could not read the settings", ex); return; }   // D3-6
            if (HandlePendingRulesReload()) return;   // the read above may have swapped the rules: reset before any row is re-tested under them (D3-1)
            try
            {
                // The store holds the ADOPTED document's rows. Judged against a family document (Edit
                // Family) every one of them "was gone" and left the panel, and the ledgers were pointed at
                // the family (review 2026-10-31, H1). Anything that is not the session's own document waits.
                if (!IsSessionDocument(doc)) return;   // auto-verify

                // Cheap and idempotent — reloads only if the model actually changed,
                // but always re-reads doc.Title so mid-session drift is recorded.
                ClashStatusStore.Instance.LoadFor(doc);
                ResolvedLog.LoadFor(doc);

                // Fixes still waiting for a save are asked again here, every tick, whether or not auto-verify
                // is on: a TransactionGroup rollback (a cancelled Revit error, another add-in's group) took a
                // moved pipe's row off the panel and puts the geometry back, and no event is relied on to say
                // so (review 2026-10-31, H2). This only ever puts rows back; it never removes one.
                ClashUpdater.ReconcilePendingSlice(doc, s.VerifySliceSize, ReconcilePlan.TickBudgetMs, "tick");   // capped by count AND by time (SP8)
                DroppedLinkRows.RelistFor(doc);   // link pairs dropped while their instance was deleted, if it is back (DM3)

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
                var linkRemoved = new List<string>();   // rows whose link instance is gone for good
                var linkRemovedRows = new List<ClashResult>();
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
                        // because the geometry moved apart, or because a LINK's own element vanished,
                        // is queued the same way since the 2026-10-31 review (H2, D-5): the move can be
                        // undone too, and the save re-verifies it. Found reviewing the whole
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
                        }
                    }
                    else if (verdict == PairVerifier.Verdict.StillClashing && sev != r.Severity)
                        ClashResultStore.Instance.UpdateSeverity(r.Key, sev);
                    else if (PairVerifier.DropsRow(verdict, () => PairVerifier.LinkInstanceRemoved(doc, r)))
                        { linkRemoved.Add(r.Key); linkRemovedRows.Add(r); }   // Unknown for good (delta review 2026-10-31, DN1): not fixed, just gone
                }
                if (linkRemoved.Count > 0)
                {
                    // Not recorded as fixed: nothing was measured. A link that is only unloaded keeps its rows (it
                    // will be back); one whose instance was removed never will, and they would stay for ever.
                    ClashResultStore.Instance.RemoveKeys(linkRemoved, asResolved: false);   // dropped, not fixed: never shown as just fixed (SP5)
                    DroppedLinkRows.HoldFor(doc, linkRemovedRows);   // an undo of the deletion lists them again (DM3)
                    var said = CheckOutcome.RemovedSentence(linkRemoved.Count);
                    Log.Info("Auto-verify: " + said);
                    var panel = ClashPaneProvider.Panel;
                    if (panel != null) panel.SetStatus(said);
                }
                if (gone.Count > 0)
                {
                    ClashResultStore.Instance.RemoveKeys(gone);

                    // Moved apart: queued for Save or Sync like a deletion, and re-verified then (review
                    // 2026-10-31, H2 and decision D-5). Recorded at once, an undo a moment later left a
                    // "fixed" entry for a clash that was back. The row still leaves the panel now.
                    foreach (var r in goneResults) ClashUpdater.QueueMovedRow(doc, r);
                    Log.Info("Auto-verify: " + gone.Count + " clash(es) no longer present (recorded at the next save or sync, re-checked then"
                             + (deferred > 0 ? "; " + deferred + " of them deletions" : "") + ").");
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
        /// Is <paramref name="doc"/> the session's own document, the one the store and the ledgers belong
        /// to? The rule is <see cref="SessionGuard.ShouldActOn"/>; this only reads its facts. Never throws:
        /// a document it cannot read is not acted on.
        /// </summary>
        internal static bool IsSessionDocument(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsValidObject) return false;
                return SessionGuard.ShouldActOn(doc.IsFamilyDocument, doc.IsLinked, ClashEngine.DocKey(doc), _adoptedDocKey);
            }
            catch { return false; }
        }

        /// <summary>A transaction or a whole group was rolled back (not undone by the user).</summary>
        internal static bool IsRollback(Autodesk.Revit.DB.Events.UndoOperation op)
        {
            return op == Autodesk.Revit.DB.Events.UndoOperation.TransactionRolledBack
                || op == Autodesk.Revit.DB.Events.UndoOperation.TransactionGroupRolledBack;
        }

        /// <summary>Why an element left the model, from the DocumentChanged operation (see UndoPolicy).</summary>
        internal static ChangeOrigin OriginOf(Autodesk.Revit.DB.Events.UndoOperation op)
        {
            if (op == Autodesk.Revit.DB.Events.UndoOperation.TransactionUndone) return ChangeOrigin.Undone;
            if (op == Autodesk.Revit.DB.Events.UndoOperation.TransactionRedone) return ChangeOrigin.Redone;
            if (IsRollback(op)) return ChangeOrigin.RolledBack;
            return ChangeOrigin.Committed;
        }

        /// <summary>The operations after which the updater's work may no longer match the model.</summary>
        internal static bool IsUndoLike(Autodesk.Revit.DB.Events.UndoOperation op)
        {
            return op == Autodesk.Revit.DB.Events.UndoOperation.TransactionUndone
                || op == Autodesk.Revit.DB.Events.UndoOperation.TransactionRedone
                || op == Autodesk.Revit.DB.Events.UndoOperation.TransactionRolledBack
                || op == Autodesk.Revit.DB.Events.UndoOperation.TransactionGroupRolledBack;
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
                HandleDocumentChanged(e.GetDocument(), e.GetAddedElementIds(), e.GetModifiedElementIds(),
                    e.GetDeletedElementIds(), e.Operation);
            }
            catch (Exception ex)
            {
                Log.Warn("Document-changed handling failed", ex);
            }
        }

        /// <summary>
        /// The body of <see cref="OnDocumentChanged"/>, with the event's facts passed in. Split out so
        /// the in-Revit checks can drive it for a given document and operation (an undo, a rollback),
        /// which a job cannot make Revit raise on demand.
        /// </summary>
        internal static void HandleDocumentChanged(Document doc, ICollection<ElementId> addedIds,
            ICollection<ElementId> modifiedIds, ICollection<ElementId> deletedIds,
            Autodesk.Revit.DB.Events.UndoOperation operation)
        {
            try
            {
                // Not the session's document (a family being edited, another open project): its ids are not
                // the ones in the store, and RemoveForElement matches a bare id (review 2026-10-31, H1).
                if (!IsSessionDocument(doc)) return;   // undo/redo

                // The ids a COMMITTED transaction deleted are remembered FIRST, before anything below that can throw: a later rollback tells an element
                // a user deleted (a fix, still waiting in the queue or on the panel) from one whose CREATION it undid by this memory (M-2, D-5).
                try
                {
                    if (deletedIds != null && deletedIds.Count > 0 && OriginOf(operation) == ChangeOrigin.Committed)
                        ClashUpdater.NoteCommittedDeletions(doc, deletedIds);
                }
                catch (Exception ex) { Log.Warn("Could not remember a committed deletion", ex); }

                var touched = new List<ElementId>();
                touched.AddRange(modifiedIds);
                touched.AddRange(addedIds);

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
                //
                // A ROLLBACK is the same case (review 2026-10-31, H2): the updater ran for the edit, took
                // its rows off and queued its fixes, and then the transaction (a cancelled Revit error) or
                // a TransactionGroup (another add-in) was rolled back. The geometry is back; nothing else
                // re-checks it.
                if (!IsUndoLike(operation)) return;

                // Updaters do not run for undo/redo, so this is the ONLY place a row an undo or a
                // redo orphaned ever gets a chance at being reconciled — nothing downstream (a
                // deferred re-check only ever remembers element ids, never which specific rows
                // their removal orphaned) can do it later. The same gap ClashUpdater.Execute had
                // for a host-changed row, and the same fix: a deletion goes through the SAME
                // deferred-until-save queue a live deletion already uses, so an undo of THIS undo
                // a moment later still cannot leave a false "fixed" entry; a target-only or
                // host-changed row is reconciled directly with PairVerifier. Found reviewing the
                // whole codebase, 2026-09-27, right after the same-shaped ClashUpdater gap.
                // An element that vanishes in an UNDO or a ROLLBACK had its creation undone: the user never deleted it, the pair never existed in a saved
                // state, and recording it as fixed at the save made the permanent record claim a fix for a clash nobody had (M-2, second pass). A REDO
                // is a deletion done again, and a committed one is the user's own: those are queued as always.
                var origin = OriginOf(operation);
                bool vanishedIsAFix = UndoPolicy.DeletedElementIsAFix(origin);
                int dropped = 0;
                if (origin == ChangeOrigin.RolledBack)
                    dropped += ClashUpdater.DropRowsOfVanishedElements(doc);   // a rollback lists no ids (measured): find the rows whose element is gone
                foreach (var id in deletedIds)
                    foreach (var r in ClashResultStore.Instance.RemoveForElement(id, asResolved: vanishedIsAFix))
                        if (r != null && !string.IsNullOrEmpty(r.Key))
                        {
                            if (vanishedIsAFix) ClashUpdater.QueueRemovedRow(doc, id, r);
                            else dropped++;
                        }
                if (dropped > 0)
                    Log.Info(dropped + " listed clash(es) dropped, not recorded as fixed: the element they clash on vanished because its creation was "
                             + (origin == ChangeOrigin.Undone ? "undone" : "rolled back") + ".");

                List<ClashResult> hostChanged;
                var targetOnly = ClashUpdater.SplitRemovedRows(touched, ClashResultStore.Instance.RemoveForElement, out hostChanged);
                if (targetOnly.Count > 0) ClashUpdater.ReverifyTargetOnly(doc, ClashSettings.Current, targetOnly);
                if (hostChanged.Count > 0) ClashUpdater.ReverifyHostChanged(doc, ClashSettings.Current, hostChanged, "undo/redo");

                // Fixes still waiting for a save that this undo or rollback may have taken back: an undone
                // TARGET deletion (M1) and a rolled-back move (H2) come back on the panel here, and leave the
                // queue, because no other path would ever re-test them.
                var asked = new List<ElementId>(touched);
                asked.AddRange(deletedIds);
                ClashUpdater.ReconcilePending(doc, asked);
                DroppedLinkRows.RelistFor(doc);   // an undo that restored a deleted link instance (DM3)

                // A rollback can reverse many transactions at once and may list no ids at all, so for a rollback
                // every queued fix is asked, not only those that name an id the event listed (capped; the idle
                // pass takes the rest).
                if (IsRollback(operation)) ClashUpdater.ReconcilePendingSlice(doc, ReconcilePlan.RollbackMaxItems, ReconcilePlan.RollbackBudgetMs, "rollback");   // capped by count AND by time; the idle pass reaches the rest (SP8)

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
                // DL-f (round 8): the key captured in DocumentSavingAs (the same function that keyed the stores and the queue) wins over
                // OriginalPath, which can be spelled differently (cloud, UNC, a mapped drive).
                string from = SaveAsCarry.FromKey(TakeSavingAsKey(e.Document), e.OriginalPath);
                int moved = ClashUpdater.RekeyPending(from, ClashEngine.DocKey(e.Document));

                // Edits still waiting for an idle re-check were keyed by the old path too, and were never
                // checked after a Save As (review 2026-10-31, LOW-2). Any document's, active or not.
                DeferredQueue.Rekey(from, ClashEngine.DocKey(e.Document));
                SaveAsCarry.RekeyHeldRows(from, ClashEngine.DocKey(e.Document));   // D3-2(b): link pairs held for an undo follow the document

                // A family being edited (Edit Family) or a link is never the session's document, however active it
                // is: Adopt and OnViewActivated exclude them, and this handler did not, so a Save As of a family
                // carried the PROJECT's settings, triage and record to the family's name and moved the adopted key
                // onto it (F3's report, review 2026-10-31). Left to the project's own events.
                if (!SessionGuard.CanOwnSession(e.Document.IsFamilyDocument, e.Document.IsLinked))
                {
                    Log.Info("Save As on a family or linked document: the session is left on the project.");
                    return;
                }

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

                // Carry the settings, triage, resolved record and audit labels to the new name rather
                // than loading the new name afresh, which reset them to defaults (review 2026-10-31, M4).
                if (!ModelLedgers.FollowSaveAs(e.Document, from))
                {
                    // The stores are not this document's (round 3b): an active family document, or a new project that never
                    // reached Adopt. Same as a document that is not the active one: its queue was re-keyed above and waits, and
                    // _adoptedDocKey stays where it was, so a later Adopt of this document loads its own.
                    if (moved > 0)
                        Log.Info("Save As on a document the settings and ledgers do not belong to: " + moved
                                 + " queued deletion(s) re-keyed, ledgers left where they were.");
                    // The saved document's key moved and the adopted key did not: ask for the idle follow now, not at Idling's next ~2.5 s tick.
                    RequestFollow();
                    return;
                }

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

        // The queue key of a document, captured just before ANY Save As (a never-saved model's first one, and, since DL-f of round 8, a
        // saved model's too: it is ClashEngine.DocKey, the same function that keyed the stores and the queue, and OriginalPath is only the
        // fallback), by the document's creation GUID - which belongs to exactly one document and, unlike its title or path, does not
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
            try { HandleDocumentOpened(sender, e == null ? null : e.Document); }
            catch (Exception ex) { Log.Warn("Document-opened handling failed", ex); }
        }

        /// <summary>The body of <see cref="OnDocumentOpened"/>, with the document passed in (the in-Revit checks open a real one).</summary>
        internal static void HandleDocumentOpened(object sender, Document doc)
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
            if (doc == null || doc.IsLinked || doc.IsFamilyDocument) return;

            // A PROJECT opened in the background is no more the user switching models than a link is: an add-in that
            // opens a project through the API with no view raises DocumentOpened and never ViewActivated. Adopting it
            // took the session off the user's own model, and every live path needs the adopted key (SP3). Only the
            // document the user has active is adopted here; a view switch adopts any other, as it always has.
            // IsActiveDocument answers "yes" when it cannot tell, the behaviour before this check existed.
            if (!SessionGuard.ShouldAdoptOnOpen(doc.IsFamilyDocument, doc.IsLinked, IsActiveDocument(sender, doc)))
            {
                Log.Info("A project was opened that is not the active one; the session stays on the model in use.");
                return;
            }
            Adopt(doc, "opened");
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
            RememberAdoptedIdentity(doc);

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
        /// Round 8, fix B: the active project's key moved under the document the session adopted (a Detach gives the document a new path after
        /// its first view was adopted). Follows it: re-keys the queued fixes and adopts the document under its new key. Returns whether it followed.
        /// <paramref name="active"/> is the document the user has active (the idle tick passes the active one). The rule is SessionGuard.ShouldFollowPathChange.
        /// </summary>
        internal static bool FollowPathChange(Document active)
        {
            try
            {
                if (active == null || !active.IsValidObject) return false;
                string now = ClashEngine.DocKey(active), adopted = _adoptedDocKey;
                if (string.IsNullOrEmpty(adopted) || string.Equals(now, adopted, StringComparison.Ordinal)) return false;   // the idle tick asks every time: no walk over the open documents for nothing (R2-9)
                if (!SessionGuard.ShouldFollowPathChange(active.IsFamilyDocument, active.IsLinked, IsActiveDocument(active.Application, active), IsTheAdoptedDocument(active),
                        AdoptedKeyIsHeld(active, adopted), now, adopted))
                    return false;

                // Fixes queued under the old key follow the document; the held link rows and the list belong to the old key and go (Adopt clears
                // the list, exactly as the user's next view switch would).
                int moved = ClashUpdater.RekeyPending(adopted, now);
                DroppedLinkRows.Forget(adopted);
                Log.Info("The active document's path changed under the session (a Detach, for one): following it; " + moved + " queued fix(es) re-keyed.");
                Adopt(active, "its path changed");
                return string.Equals(_adoptedDocKey, now, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Log.Warn("Following a document new path failed", ex);
                return false;
            }
        }

        // The early follow after a Save As whose carry was refused (round 8, SK). The carry is refused when the stores are not the saved document's,
        // and OnDocumentSavedAs then returns before the adopted key is updated: the saved document is not the session's until the idle tick's
        // FollowPathChange (fix B) runs, and Idling fires only about every 2.5 s. SetRaiseWithoutDelay exists only INSIDE an Idling event, so a flag
        // cannot bring the first tick forward; an ExternalEvent runs as soon as Revit is idle. Nothing is adopted inside the save event itself (Adopt
        // clears the lists and reloads the stores, too much to do in Revit's save event): the request only asks the SAME follow rule to run early.
        // That rule (SessionGuard.ShouldFollowPathChange) is what stops a document that is not the session's from being adopted.
        private static volatile bool _followRequested;
        private static ExternalEvent _followEvent;

        /// <summary>A follow was asked for after a refused Save As carry and has not run yet.</summary>
        internal static bool FollowRequested { get { return _followRequested; } }

        /// <summary>Ask for the follow now. Never throws.</summary>
        private static void RequestFollow()
        {
            _followRequested = true;
            try
            {
                if (_followEvent != null)
                {
                    var raised = _followEvent.Raise();
                }
            }
            catch (Exception ex) { Log.Warn("Could not ask for the follow after a Save As", ex); }
        }

        /// <summary>The follow the request asks for: the idle tick's own <see cref="FollowPathChange"/> on the active document. The request is spent
        /// whether or not anything needed following. Returns whether it followed.</summary>
        internal static bool RunFollowRequest(Document active)
        {
            try { return FollowPathChange(active); }
            finally { _followRequested = false; }
        }

        private sealed class FollowHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                try
                {
                    var ud = app == null ? null : app.ActiveUIDocument;
                    RunFollowRequest(ud == null ? null : ud.Document);
                }
                catch (Exception ex) { Log.Warn("The follow after a Save As failed", ex); }
            }

            public string GetName() { return "LiveClash follow after Save As"; }
        }

        // The adopted document itself, so a moved key can be told from another document. By the object, never by CreationGUID: a copy of a
        // file has the same GUID as the original, so a sibling copy matched (delta review L-1). If the object does not match, the rule falls
        // back on "no open document holds the adopted key any more".
        private static WeakReference _adoptedDocRef;

        private static void RememberAdoptedIdentity(Document doc)
        {
            try { _adoptedDocRef = new WeakReference(doc); }
            catch { _adoptedDocRef = null; }
        }

        private static bool IsTheAdoptedDocument(Document doc)
        {
            try
            {
                var r = _adoptedDocRef;
                if (r != null && r.IsAlive && SessionGuard.IsSameObject(r.Target, doc)) return true;   // Equals too: Revit may hand out a fresh wrapper (delta review R2-3)
                return false;
            }
            catch { return false; }
        }

        /// <summary>Does some open document (the active one, a background one, a link) still have the adopted key as its key?</summary>
        private static bool AdoptedKeyIsHeld(Document any, string adoptedKey)
        {
            if (string.IsNullOrEmpty(adoptedKey)) return false;
            try
            {
                foreach (Document d in any.Application.Documents)
                {
                    if (d == null || !d.IsValidObject) continue;
                    if (string.Equals(ClashEngine.DocKey(d), adoptedKey, StringComparison.Ordinal)) return true;
                }
            }
            catch { }
            return false;
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

        /// <summary>
        /// A document is closing (the body of the DocumentClosing handler's new part). RED STUB (r8 live): does nothing; the green commit notes the id and key
        /// in CloseTracker.Shared and the handlers call this.
        /// </summary>
        internal static void ClosingFor(int documentId, string key)
        {
            CloseTracker.Shared.Closing(documentId, key);
        }

        /// <summary>
        /// Revit reported a close (the body of the DocumentClosed handler). Finalises the document noted under <paramref name="documentId"/> only when the
        /// close SUCCEEDED. RED STUB (r8 live): does nothing.
        /// </summary>
        internal static void ClosedFor(int documentId, bool succeeded)
        {
            string key = CloseTracker.Shared.Closed(documentId, succeeded);
            if (key != null) FinalizeClosedDocument(key);
        }

        /// <summary>
        /// A document really closed (Revit reported DocumentClosed with the status Succeeded). Everything LiveClash kept for it goes: its deferred
        /// re-checks, its held link rows, its memory of committed deletions, its queued fixes (an unsaved deletion is not a fix, and left behind they could
        /// be re-keyed onto a different model that reuses the name), and, if it was the session's own document, the list, the warm contexts and the adopted
        /// key (so reopening the same model is a real adoption). This used to run at DocumentClosing, which a close that another add-in cancels
        /// never completes: the queued fixes of a document that stayed open were gone (found by the P9-3 probe).
        /// </summary>
        private static void FinalizeClosedDocument(string key)
        {
            DeferredQueue.ClearKey(key);
            SaveAsCarry.ForgetHeldRows(key);   // D3-2(c): a reopen must not say "their link came back"
            ClashUpdater.ForgetCommittedDeletions(key);
            int dropped = ClashUpdater.DropPending(key);
            if (dropped > 0)
                Log.Info("A document closed with " + dropped + " queued deletion(s) never recorded: an unsaved deletion is not a fix.");

            // BUT ONLY THE SESSION'S OWN DOCUMENT CLEARS THE SESSION (closing B used to clear A's results while A stayed adopted).
            if (key != _adoptedDocKey) return;
            ClashResultStore.Instance.Clear();
            ClashEngine.InvalidateAll();
            _adoptedDocKey = null;
            _adoptedDocRef = null;
        }

        private void OnDocumentClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
        {
            try { ClosedFor(e.DocumentId, e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded); }
            catch (Exception ex) { Log.Warn("Close handling failed", ex); }
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

                // Note the close; DocumentClosed (status Succeeded) does the cleanup. Doing it here dropped the queued fixes of a document whose close
                // another add-in then cancelled (P9-3). Keyed by the document's id, so a file closed without saving and opened again is a different entry.
                ClosingFor(e.DocumentId, ClashEngine.DocKey(e.Document));

                // The focus view entry holds an ElementId in the document that is closing and needs the Document object, so it goes here; it is
                // harmless if the close is cancelled (the next Focus makes a view again).
                UI.FocusHandler.Forget(e.Document);
            }
            catch { }
        }
    }
}
