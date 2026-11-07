using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The Revit side of the clash schedule: implements builder B's <see cref="IClashScheduleActions"/> for the panel. Every call runs on the
    /// Revit thread through one <see cref="MarkerJobRunner"/> behind the usual guards, and every Task completes through it: a refusal or a
    /// failure is a RESULT (Ok = false and the words), never an exception. The snapshot is in memory; it is rebuilt after each action and
    /// <see cref="Changed"/> is raised on the panel's dispatcher.
    /// Real here: opening the schedule, showing the markers in Focus, the three removals, status / note / assign on marked rows (the marker
    /// first, then the ledger in step; a marker someone else holds becomes a pending-to-model line with the Seq it was read at), Discard pending,
    /// and the Assign picker's names, and Refresh (PlanRefresh / ApplyRefresh through the plan assembler).
    /// </summary>
    [RevitBound]
    public sealed class ClashScheduleActions : IClashScheduleActions
    {
        private const int MaxElementsForNames = 500;

        private readonly ClashPanel _panel;
        private readonly MarkerJobRunner _runner;
        private ScheduleSnapshot _snapshot = new ScheduleSnapshot();
        private RefreshFingerprint _shown;   // the plan PlanRefresh last showed (review L2); touched on the Revit thread only

        public ClashScheduleActions(ClashPanel panel)
        {
            _panel = panel;
            _runner = MarkerJobRunner.Create(panel);
        }

        public ScheduleSnapshot Snapshot { get { return _snapshot; } }

        public event Action Changed;

        /// <summary>The job runner, so a check can run the queued jobs by hand (as R18 does).</summary>
        internal MarkerJobRunner Runner { get { return _runner; } }

        /// <summary>The session's document closed (or there is none yet): the snapshot is empty and not ready, so the panel cannot take a stale model's rows for this one's.</summary>
        public void Reset()
        {
            _snapshot = new ScheduleSnapshot();
            RaiseChanged();
        }

        /// <summary>Reads the model again (at start, and after a document switch). Fire and forget: the answer arrives through <see cref="Changed"/>.</summary>
        public void Reload()
        {
            _runner.Run("read the clash schedule", doc => { SetSnapshot(doc); return true; }, text =>
            {
                // A snapshot that is ready stays as it is; one that is not says why, so the panel can tell "still loading" from "could not be read".
                if (!_snapshot.Ready) { _snapshot.LoadFailure = text; RaiseChanged(); }
                return false;
            });
        }

        private void SetSnapshot(Document doc)
        {
            var snap = MarkerSnapshot.Build(doc);
            try
            {
                var lines = ClashStatusStore.Instance.ReadLines();
                if (lines != null)
                    foreach (var kv in LedgerExtras.Read(lines).Pending)
                    {
                        RowScheduleInfo row;
                        if (!snap.Rows.TryGetValue(kv.Key, out row)) continue;
                        var p = kv.Value;
                        row.PendingStatus = p.Status.HasValue ? MarkerStatusText.Format(p.Status.Value)
                            : p.AssignedTo != null ? "Assign " + (p.AssignedTo.Length == 0 ? "nobody" : p.AssignedTo) : "Note";
                        row.PendingReason = string.IsNullOrEmpty(row.HeldBy) ? "waiting for the model" : "held by " + row.HeldBy + " until they synchronize";
                    }
            }
            catch (Exception ex) { Log.Warn("The pending decisions could not be read for the snapshot", ex); }
            _snapshot = snap;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try
            {
                if (_panel == null || _panel.Dispatcher.CheckAccess()) handler();
                else _panel.Dispatcher.BeginInvoke(handler);
            }
            catch (Exception ex) { Log.Warn("The panel could not follow a clash schedule change", ex); }
        }

        private static ActionResult To(bool ok, string message)
        {
            return ok ? ActionResult.Success(message) : ActionResult.Fail(message);
        }

        private static ActionResult Failed(string message)
        {
            return ActionResult.Fail(message);
        }

        public Task<ActionResult> OpenSchedule()
        {
            return _runner.RunUi("open the clash schedule", uidoc =>
            {
                if (uidoc == null) return Failed("Open the project first. Nothing was changed.");
                var r = MarkerViews.OpenSchedule(uidoc);
                return To(r.Ok, r.Message);
            }, Failed);
        }

        public Task<ActionResult> ShowMarkersInFocus()
        {
            return _runner.Run("show the clash markers in your Focus view", doc =>
            {
                var r = MarkerViews.ShowInFocus(doc);
                SetSnapshot(doc);
                return To(r.Ok, r.Message);
            }, Failed);
        }

        public Task<ActionResult> RemoveResolved()
        {
            return _runner.Run("remove the resolved clash markers", doc =>
            {
                var r = MarkerRemove.Resolved(doc);
                SetSnapshot(doc);
                return To(r.Ok, r.Message);
            }, Failed);
        }

        public Task<ActionResult> RemoveAllMarkers()
        {
            return _runner.Run("remove all the clash markers", doc =>
            {
                var r = MarkerRemove.All(doc);
                SetSnapshot(doc);
                return To(r.Ok, r.Message);
            }, Failed);
        }

        public Task<ActionResult> RemoveParameters()
        {
            return _runner.Run("remove the LiveClash parameters", doc =>
            {
                var r = MarkerRemove.Parameters(doc);
                SetSnapshot(doc);
                return To(r.Ok, r.Message);
            }, Failed);
        }

        // ------------------------------------------------------------------ status, note, assign: the marker first, then the ledger

        private static List<MarkerFound> MarkersFor(Document doc, IReadOnlyList<string> rowKeys)
        {
            var keys = new HashSet<string>(rowKeys ?? new List<string>(), StringComparer.Ordinal);
            return MarkerElements.Collect(doc)
                .Where(m => m.Read != null && m.Read.Status == MarkerReadStatus.Ok && keys.Contains(m.Read.Record.Key)).ToList();
        }

        private static string KeyOf(List<MarkerFound> found, MarkerTriagePending p)
        {
            if (!string.IsNullOrEmpty(p.Key)) return p.Key;
            var m = found.FirstOrDefault(x => x.Uid == p.MarkerUid);
            return m == null || m.Read == null || m.Read.Record == null ? null : m.Read.Record.Key;
        }

        /// <summary>One PendingToModel line (the Seq it was read at) for each marker the model did not take. Appended after the marker transaction.</summary>
        private static void WritePendingLines(List<MarkerFound> found, MarkerTriageResult result, string me)
        {
            var now = DateTime.UtcNow;
            var lines = new List<string>();
            foreach (var p in result.Pending)
            {
                var key = KeyOf(found, p);
                if (string.IsNullOrEmpty(key)) continue;
                lines.Add(LedgerExtras.PendingLine(key, now, me, p.SeqAtPending, p.Status, p.Note, p.AssignedTo));
            }
            if (lines.Count > 0) ClashStatusStore.Instance.AppendLines(lines);
        }

        private static List<string> LooseKeys(IReadOnlyList<string> rowKeys, List<MarkerFound> found)
        {
            var marked = new HashSet<string>(found.Select(m => m.Read.Record.Key), StringComparer.Ordinal);
            return (rowKeys ?? new List<string>()).Where(k => !string.IsNullOrEmpty(k) && !marked.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
        }

        private static List<string> WrittenKeys(List<MarkerFound> found, MarkerTriageResult result)
        {
            return found.Where(m => result.Written.Contains(m.Uid)).Select(m => m.Read.Record.Key).ToList();
        }

        private Task<ActionResult> Triage(string action, IReadOnlyList<string> rowKeys, MarkerStatus? status, string note, Action<List<string>> ledgerForWritten)
        {
            return _runner.Run(action, doc =>
            {
                var found = MarkersFor(doc, rowKeys);
                // A key with no marker is a ledger-only row, exactly as before the schedule: it is saved whatever the snapshot said, so a panel
                // that routed by a stale or empty snapshot cannot lose a decision (review 2026-11-01, H1).
                var loose = LooseKeys(rowKeys, found);
                if (found.Count == 0)
                {
                    if (loose.Count > 0) ledgerForWritten(loose);
                    SetSnapshot(doc);
                    return loose.Count > 0 ? ActionResult.Success("Saved to your record: those rows have no clash marker.") : Failed("No rows were given. Nothing was changed.");
                }
                var me = MarkerSettings.UserName(doc);
                var result = MarkerTriage.Apply(doc, new MarkerTriageRequest
                {
                    MarkerUids = found.Select(m => m.Uid).ToList(), Status = status, Note = note, User = me, Now = DateTimeOffset.Now
                });
                if (result.Ok)
                {
                    if (loose.Count > 0) ledgerForWritten(loose);   // a failed triage writes nothing, loose rows included
                    ledgerForWritten(WrittenKeys(found, result));
                    WritePendingLines(found, result, me);
                }
                SetSnapshot(doc);
                return To(result.Ok, result.Message);
            }, Failed);
        }

        public Task<ActionResult> SetStatus(IReadOnlyList<string> rowKeys, ClashStatusKind kind, string note)
        {
            return Triage("set the clash status", rowKeys, RowBuilder.StatusOf(kind), note,
                keys => { if (keys.Count > 0) ClashStatusStore.Instance.Set(keys, kind, note); });
        }

        public Task<ActionResult> SetNote(IReadOnlyList<string> rowKeys, string note)
        {
            var text = note ?? "";
            return Triage("set the clash note", rowKeys, null, text,
                keys => { if (keys.Count > 0) ClashStatusStore.Instance.SetNote(keys, text); });
        }

        public Task<AssignOutcome> Assign(IReadOnlyList<string> rowKeys, string user)
        {
            var assignee = user ?? "";
            return _runner.Run("assign the clashes", doc =>
            {
                var found = MarkersFor(doc, rowKeys);
                var loose = LooseKeys(rowKeys, found);
                Action writeLoose = () =>
                {
                    if (loose.Count == 0) return;
                    var when = DateTime.UtcNow;
                    var who = MarkerSettings.UserName(doc);
                    ClashStatusStore.Instance.AppendLines(loose.Select(k => LedgerExtras.AssignedLine(k, when, who, assignee)).ToList());
                };
                if (found.Count == 0)
                {
                    writeLoose();
                    SetSnapshot(doc);
                    return new AssignOutcome { Ok = loose.Count > 0, Message = loose.Count > 0 ? "Saved to your record: those rows have no clash marker." : "No rows were given. Nothing was changed.", Assigned = loose.Count };
                }
                var me = MarkerSettings.UserName(doc);
                var result = MarkerTriage.Apply(doc, new MarkerTriageRequest
                {
                    MarkerUids = found.Select(m => m.Uid).ToList(), AssignedTo = assignee, User = me, Now = DateTimeOffset.Now
                });
                if (result.Ok)
                {
                    writeLoose();   // the rows with no marker are saved only once the marker triage took: a failed one writes nothing (delta review D-L2)
                    var now = DateTime.UtcNow;
                    var lines = WrittenKeys(found, result).Select(k => LedgerExtras.AssignedLine(k, now, me, assignee)).ToList();
                    if (lines.Count > 0) ClashStatusStore.Instance.AppendLines(lines);
                    WritePendingLines(found, result, me);
                }
                SetSnapshot(doc);
                return new AssignOutcome { Ok = result.Ok, Message = result.Message, Assigned = result.Written.Count, Pending = result.Pending.Count };
            }, text => new AssignOutcome { Ok = false, Message = text });
        }

        public Task<ActionResult> DiscardPending(string rowKey)
        {
            return _runner.Run("discard the pending decision", doc =>
            {
                var store = ClashStatusStore.Instance;
                var lines = store.ReadLines();
                var extras = LedgerExtras.Read(lines ?? new List<string>());
                LedgerExtras.PendingEntry open;
                if (string.IsNullOrEmpty(rowKey) || !extras.Pending.TryGetValue(rowKey, out open))
                    return Failed("There is no pending decision on that row. Nothing was changed.");
                var me = MarkerSettings.UserName(doc);
                store.AppendLines(new List<string> { LedgerExtras.DoneLine(rowKey, MarkerReconcile.After(DateTime.UtcNow, open.WhenUtc), me) });
                SetSnapshot(doc);
                return ActionResult.Success("The pending decision was discarded. The marker was not changed.");
            }, Failed);
        }

        // ------------------------------------------------------------------ the Assign picker's names

        public Task<List<AssignCandidate>> AssignCandidates(IReadOnlyList<string> rowKeys)
        {
            return _runner.Run("list the people to assign", doc =>
            {
                var names = new Dictionary<string, Tuple<int, string>>(StringComparer.OrdinalIgnoreCase);
                Action<string, string> note = (name, reason) =>
                {
                    if (string.IsNullOrWhiteSpace(name)) return;
                    Tuple<int, string> have;
                    names[name] = names.TryGetValue(name, out have) ? Tuple.Create(have.Item1 + 1, have.Item2) : Tuple.Create(1, reason);
                };
                foreach (var m in MarkerElements.Collect(doc))
                {
                    if (m.Read == null || m.Read.Record == null) continue;
                    note(m.Read.Record.AssignedTo, "already has clashes assigned");
                    note(m.Read.Record.ReportedBy, "made clash markers");
                }
                if (doc.IsWorkshared)
                {
                    int looked = 0;
                    foreach (var m in MarkersFor(doc, rowKeys))
                    {
                        var rec = m.Read.Record;
                        var ids = new List<long> { rec.HostId };
                        if (rec.LinkId == 0) ids.Add(rec.OtherId);
                        foreach (var id in ids.Where(i => i != 0))
                        {
                            if (++looked > MaxElementsForNames) break;
                            try
                            {
                                var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, IdUtil.From(id));
                                note(info.Creator, "created an element in these clashes");
                                note(info.LastChangedBy, "last changed an element in these clashes");
                            }
                            catch { }
                        }
                    }
                }
                return names.OrderByDescending(kv => kv.Value.Item1).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new AssignCandidate { Name = kv.Key, Reason = kv.Value.Item2 }).ToList();
            }, text => new List<AssignCandidate>());
        }

        // ------------------------------------------------------------------ Refresh: slice 4f-3c

        private static List<string> FirstCreateAdds(Document doc)
        {
            var adds = new List<string>();
            if (!MarkerParameters.IsBound(doc)) adds.Add("22 read-only shared parameters and a hidden status order, bound to Specialty Equipment");
            if (MarkerElements.FindType(doc) == null) adds.Add("a marker type (a 300 mm octahedron)");
            if (doc.IsWorkshared && MarkerWorkset.Find(doc) < 0) adds.Add("a workset named LiveClash, hidden by default");
            if (MarkerSchedule.Find(doc) == null) adds.Add("the schedule \"" + MarkerSchedule.Name + "\"");
            if (MarkerSettings.ReadSettings(doc) == null) adds.Add("a settings record, and your own issue-number counter");
            return adds;
        }

        public Task<RefreshPlanView> PlanRefresh()
        {
            return _runner.Run("plan the clash markers", doc =>
            {
                var refusal = MarkerRefusals.Check(MarkerRefresh.ReadEnv(doc));
                if (refusal != null) return new RefreshPlanView { Refusal = refusal };
                // A big model: say what is happening (the plan runs on Revit's thread and used to show nothing for minutes).
                var progress = PlanProgress.StartFor(ClashResultStore.Instance.Snapshot().Count);
                MarkerCollector.Collected c;
                try { c = MarkerCollector.Collect(doc, false, progress == null ? (Action<int, int>)null : progress.Report); }
                finally { if (progress != null) progress.Close(); }
                var byStatus = c.Assembly.Request.Markers.Where(m => m.Record != null)
                    .GroupBy(m => MarkerStatusText.Format(m.Record.Status), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new MarkerSummary.Reason { Text = g.Key, Count = g.Count() }).ToList();
                var context = new RefreshPlanContext
                {
                    NonWorkshared = !doc.IsWorkshared, ByStatus = byStatus,
                    FirstCreateAdds = c.Plan.ExistingMarkers == 0 ? FirstCreateAdds(doc) : new List<string>()
                };
                var view = RefreshPlanFactory.From(c.Plan, c.Options, context);
                _shown = RefreshFingerprint.Of(c.Plan);   // what the user is about to agree to; Apply refuses to do more than this
                SetSnapshot(doc);   // a plan is a fresh look at the model: the panel's columns follow it
                return view;
            }, text => new RefreshPlanView { Refusal = text });
        }

        public Task<RefreshOutcome> ApplyRefresh(RefreshChoices choices)
        {
            choices = choices ?? new RefreshChoices();
            return _runner.Run("refresh the clash markers", doc =>
            {
                var refusal = MarkerRefusals.Check(MarkerRefresh.ReadEnv(doc));
                if (refusal != null) return new RefreshOutcome { Ok = false, Message = refusal };
                var c = MarkerCollector.Collect(doc, choices.RemoveRetention);
                var changed = RefreshFingerprint.Changed(_shown, RefreshFingerprint.Of(c.Plan), choices.RemoveRetention);
                if (changed != null)
                {
                    _shown = null;   // the user must look again: the next Refresh shows a fresh plan
                    return new RefreshOutcome { Ok = false, Message = changed };
                }
                if (!doc.IsWorkshared && c.Plan.ExistingMarkers == 0 && c.Plan.Creations.Count > 0 && !choices.ConfirmedNonWorkshared)
                    return new RefreshOutcome { Ok = false, Message = "This model is not workshared, so markers show in every view that shows Specialty Equipment, and would print. Tick the confirmation to go on. Nothing was changed." };

                var options = new MarkerApplyOptions { CreateCapOnly = choices.CreateCapOnly };
                var result = MarkerRefresh.Apply(doc, c.Plan, options);
                var me = MarkerSettings.UserName(doc);
                var outcome = new MarkerPlanAssembler.Outcome
                {
                    Failed = !result.Ok,
                    NotWritten = new HashSet<string>(result.PassedOverNow.Select(p => p.MarkerUid), StringComparer.Ordinal),
                    CreationsMade = choices.CreateCapOnly ? Math.Min(c.Plan.Creations.Count, options.Cap) : -1
                };
                var written = MarkerPlanAssembler.Lines(c.Assembly, c.Plan, outcome, me, DateTime.UtcNow);
                if (result.Ok && written.Lines.Count > 0) ClashStatusStore.Instance.AppendLines(written.Lines);
                SetSnapshot(doc);

                var sets = c.Plan.SetCounts.Select(s => new MarkerSummary.SetCounts
                {
                    Name = s.Name, InPanel = s.InPanel, UpToDate = s.UpToDate, NotLive = s.NotLive, SetOff = s.SetOff, NotRetested = s.NotRetested
                }).ToList();
                if (!result.Ok) return new RefreshOutcome { Ok = false, Message = result.Refusal ?? result.FirstError, Sets = sets };
                _shown = null;   // the plan the user saw has been acted on
                var message = result.NoOp && result.PassedOverNow.Count == 0 ? MarkerSummary.NothingToDo(c.Plan.ExistingMarkers) : MarkerSummary.Describe(result.Counts);
                return new RefreshOutcome { Ok = true, Message = message, Counts = result.Counts, Sets = sets, Notes = written.Notes };
            }, text => new RefreshOutcome { Ok = false, Message = text });
        }
    }
}
