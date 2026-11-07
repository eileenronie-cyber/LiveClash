using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>The names of the transactions LiveClash opens for markers, in one place: the reconcile after an Undo (probe P13) matches on them.</summary>
    public static class MarkerTx
    {
        public const string Refresh = "LiveClash: refresh clash markers";
        public const string SetUp = "LiveClash: set up clash markers";
        public const string Write = "LiveClash: write clash markers";
        public const string RemoveResolved = "LiveClash: remove resolved clash markers";
        public const string RemoveAll = "LiveClash: remove all clash markers";
        public const string RemoveParameters = "LiveClash: remove clash parameters";
        public const string ShowInFocus = "LiveClash: show markers in my Focus view";
        public const string SetStatus = "LiveClash: set clash status";
    }

    public sealed class MarkerApplyOptions
    {
        public int Cap { get; set; } = 2000;
        /// <summary>Over the cap: create only the first <see cref="Cap"/> creations (the user chose it in the dialog).</summary>
        public bool CreateCapOnly { get; set; }
    }

    public sealed class MarkerApplyResult
    {
        /// <summary>A transaction group ran and committed.</summary>
        public bool Committed { get; set; }
        /// <summary>Nothing to write and nothing to add: no group, no transaction, nothing borrowed (H1).</summary>
        public bool NoOp { get; set; }
        /// <summary>Why the Refresh may not run at all (spec 7); null otherwise.</summary>
        public string Refusal { get; set; }
        /// <summary>Revit's first error when the run was rolled back (the message ends with "Nothing was changed.").</summary>
        public string FirstError { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Resolved { get; set; }
        public int Deleted { get; set; }
        /// <summary>Markers that could not be borrowed at the last moment, with the words for the summary.</summary>
        public List<MarkerPlan.Reasoned> PassedOverNow { get; set; } = new List<MarkerPlan.Reasoned>();
        /// <summary>The counts worked out from the PLAN (kept only to compare with the model).</summary>
        public MarkerSummary.Counts PlanCounts { get; set; }
        /// <summary>The counts re-read from the MODEL after the commit (C5).</summary>
        public MarkerSummary.Counts Counts { get; set; }

        /// <summary>What Assimilate said: Committed when the group became one Undo entry.</summary>
        public TransactionStatus GroupStatus { get; set; }

        public bool Ok { get { return Refusal == null && FirstError == null; } }
    }

    /// <summary>
    /// Applies a Refresh plan (spec 6.2, 7, 8). It writes only what the plan says. No transaction when there is nothing to write; otherwise
    /// ONE transaction group (one Undo entry) of an optional set-up transaction and the write transaction, every one with a failures
    /// preprocessor, the whole group rolled back on any error or throw. Only the markers the plan changes are borrowed; the ones that
    /// cannot be had are passed over. The caller (the ExternalEvent handler) has already checked that this is the session's document.
    /// </summary>
    [RevitBound]
    public static class MarkerRefresh
    {

        internal sealed class Failures : IFailuresPreprocessor
        {
            public readonly List<string> Errors = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool error = false;
                foreach (var m in accessor.GetFailureMessages())
                {
                    if (m.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(m); continue; }
                    error = true;
                    string text = null;
                    try { text = m.GetDescriptionText(); } catch { }
                    if (!string.IsNullOrEmpty(text) && !Errors.Contains(text)) Errors.Add(text);
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        internal sealed class RolledBack : Exception
        {
            public RolledBack(string message) : base(message) { }
        }

        /// <summary>What the pre-flight reads about the document. The session-document check belongs to the handler.</summary>
        public static MarkerEnv ReadEnv(Document doc)
        {
            var env = new MarkerEnv();
            if (doc == null) return env;
            env.IsFamily = doc.IsFamilyDocument;
            env.IsLinked = doc.IsLinked;
            env.IsReadOnly = doc.IsReadOnly;
            env.Workshared = doc.IsWorkshared;
            env.WorksetClosed = doc.IsWorkshared && MarkerWorkset.IsClosed(doc);
            env.SwitchedOff = !MarkerSwitch.IsOn;
            env.ForeignParameterName = MarkerParameters.ForeignName(doc) ?? "";
            if (SetupNeeded(doc)) env.SettingsHeldBy = MarkerSettings.HeldByOther(doc);
            return env;
        }

        /// <summary>Something has to be added to the model before markers can be written (parameters, type, workset, settings record, schedule).</summary>
        public static bool SetupNeeded(Document doc)
        {
            return !MarkerParameters.IsBound(doc) || MarkerElements.FindType(doc) == null || (doc.IsWorkshared && MarkerWorkset.Find(doc) < 0)
                || MarkerSettings.ReadSettings(doc) == null || MarkerSchedule.Find(doc) == null;
        }

        /// <summary>The parameters, then the type and the workset, then the schedule and the settings record: three transactions, because a schedule field needs the binding committed first.</summary>
        private static void SetUp(Document doc, Failures failures)
        {
            if (!MarkerParameters.IsBound(doc))
                InTransaction(doc, MarkerTx.SetUp, failures, false, () =>
                {
                    var err = MarkerParameters.EnsureBound(doc);
                    if (err != null) throw new InvalidOperationException(err);
                });
            InTransaction(doc, MarkerTx.SetUp, failures, false, () =>
            {
                MarkerElements.EnsureType(doc);
                MarkerWorkset.Ensure(doc);
            });
            InTransaction(doc, MarkerTx.SetUp, failures, false, () =>
            {
                var schedule = MarkerSchedule.Ensure(doc);
                var type = doc.GetElement(MarkerElements.FindType(doc));
                MarkerSettings.WriteSettings(doc, new MarkerSettingsRecord
                {
                    ScheduleUid = schedule.UniqueId, TypeUid = type == null ? "" : type.UniqueId,
                    WorksetId = doc.IsWorkshared ? MarkerWorkset.Find(doc) : -1, ParamVersion = MarkerSettings.ParamVersion
                });
            });
        }

        private static MarkerApplyResult Refused(string text, MarkerSummary.Counts planCounts)
        {
            return new MarkerApplyResult { Refusal = text, PlanCounts = planCounts, Counts = planCounts };
        }

        private static MarkerSummary.Counts Copy(MarkerSummary.Counts c)
        {
            // Every field, so a merge, a removal or an unmarked row is never dropped from the summary (review M2).
            return c.Clone();
        }

        /// <summary>Applies the plan. Never throws: a refusal or a rolled-back run is a result.</summary>
        public static MarkerApplyResult Apply(Document doc, MarkerPlan.Result plan, MarkerApplyOptions options)
        {
            options = options ?? new MarkerApplyOptions();
            var planCounts = plan == null ? new MarkerSummary.Counts() : Copy(plan.ToCounts());
            try
            {
                if (doc == null || !doc.IsValidObject) return Refused("There is no project to write to.", planCounts);
                if (plan == null) return new MarkerApplyResult { NoOp = true, PlanCounts = planCounts, Counts = planCounts };

                var refusal = MarkerRefusals.Check(ReadEnv(doc));
                if (refusal != null) return Refused(refusal, planCounts);
                if (plan.OverCap && !options.CreateCapOnly) return Refused(MarkerRefusals.CapRefusal(plan.Creations.Count, options.Cap), planCounts);

                var creations = plan.Creations.Count > options.Cap ? plan.Creations.Take(options.Cap).ToList() : plan.Creations.ToList();
                var changes = plan.Changes.ToList();
                var removals = plan.Removals.ToList();
                bool setup = SetupNeeded(doc);
                var result = new MarkerApplyResult { PlanCounts = planCounts };

                if (creations.Count == 0 && changes.Count == 0 && removals.Count == 0 && !setup)
                {
                    result.NoOp = true;
                    result.Counts = Copy(planCounts);
                    return result;
                }

                // Borrow exactly the markers the plan changes or removes; pass over what cannot be had.
                var touched = changes.Select(c => c.MarkerUid).Concat(removals.Select(r => r.MarkerUid)).ToList();
                if (touched.Count > 0)
                {
                    var checkout = MarkerOwnership.Checkout(doc, touched);
                    var held = new HashSet<string>(checkout.PassedOver.Keys, StringComparer.Ordinal);
                    if (held.Count > 0)
                    {
                        changes = MarkerWriteFilter.Apply(changes, c => c.MarkerUid, held).Kept;
                        removals = MarkerWriteFilter.Apply(removals, r => r.MarkerUid, held).Kept;
                        foreach (var kv in checkout.PassedOver)
                            result.PassedOverNow.Add(new MarkerPlan.Reasoned { MarkerUid = kv.Key, Reason = kv.Value });
                    }
                }

                if (creations.Count == 0 && changes.Count == 0 && removals.Count == 0 && !setup)
                {
                    result.NoOp = true;
                    result.Counts = Reread(doc, planCounts, creations, changes, removals, result);
                    return result;
                }

                Run(doc, creations, changes, removals, setup, creations.Count > 0 || plan.PrefixMove != null ? plan.CounterAfter : null, result);
                if (result.FirstError == null)
                {
                    // The commit has happened: a failure while reading the counts back must not turn into "Nothing was changed." (review L3).
                    try { result.Counts = Reread(doc, planCounts, creations, changes, removals, result); }
                    catch (Exception ex) { Log.Warn("The counts could not be read back after the commit; the plan's are shown", ex); result.Counts = planCounts; }
                }
                else result.Counts = planCounts;
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn("Marker refresh failed", ex);
                return new MarkerApplyResult { FirstError = ex.Message + " Nothing was changed.", PlanCounts = planCounts, Counts = planCounts };
            }
        }

        private static void Run(Document doc, List<MarkerPlan.Creation> creations, List<MarkerPlan.Change> changes, List<MarkerPlan.Removal> removals, bool setup, IssueNumbers.Counter counterAfter, MarkerApplyResult result)
        {
            var failures = new Failures();
            TransactionGroup group = null;
            try
            {
                group = new TransactionGroup(doc, MarkerTx.Refresh);
                group.Start();
                if (setup) SetUp(doc, failures);
                InTransaction(doc, MarkerTx.Write, failures, true, () => WriteAll(doc, creations, changes, removals, counterAfter));
                result.GroupStatus = group.Assimilate();
                if (result.GroupStatus != TransactionStatus.Committed) throw new RolledBack("Revit did not keep the change.");
                result.Committed = true;
            }
            catch (Exception ex)
            {
                result.Committed = false;
                var first = failures.Errors.Count > 0 ? failures.Errors[0] : ex.Message;
                var said = first.TrimEnd();
                result.FirstError = said + (said.Length > 0 && said[said.Length - 1] == '.' ? " " : ". ") + "Nothing was changed.";
                Log.Warn("Marker refresh rolled back: " + result.FirstError, ex);
            }
            finally
            {
                // The group ends on every path: assimilated above, or rolled back here.
                try { if (group != null && group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch (Exception ex) { Log.Warn("Marker refresh group did not roll back", ex); }
                try { if (group != null) group.Dispose(); } catch { }
            }
        }

        internal static void InTransaction(Document doc, string name, Failures failures, bool inject, Action body)
        {
            using (var t = new Transaction(doc, name))
            {
                var options = t.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(failures);
                options.SetClearAfterRollback(true);
                options.SetForcedModalHandling(false);
                t.SetFailureHandlingOptions(options);
                t.Start();
                body();
                var status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new RolledBack(failures.Errors.Count == 0 ? "Revit rolled the change back." : failures.Errors[0]);
            }
        }

        private static Dictionary<string, string> TextFor(MarkerRecord record, Dictionary<string, string> shown)
        {
            var text = MarkerParameterMap.Build(record, shown);
            if (shown == null) foreach (var n in MarkerParameterMap.ShownNames) text.Remove(n);   // no row: the display columns stay as they are
            return text;
        }

        private static void WriteAll(Document doc, List<MarkerPlan.Creation> creations, List<MarkerPlan.Change> changes, List<MarkerPlan.Removal> removals, IssueNumbers.Counter counterAfter)
        {
            int workset = doc.IsWorkshared ? MarkerWorkset.Find(doc) : -1;
            ElementId typeId = null;

            foreach (var r in removals)
            {
                var e = doc.GetElement(r.MarkerUid);
                if (e == null) continue;
                e.Pinned = false;
                doc.Delete(e.Id);
            }

            foreach (var c in creations)
            {
                if (typeId == null)
                {
                    typeId = MarkerElements.FindType(doc);
                    if (typeId == null) throw new InvalidOperationException("The clash marker type is missing.");
                }
                var rec = c.Record;
                var ds = MarkerElements.Create(doc, typeId, new XYZ(rec.X, rec.Y, rec.Z), workset);
                MarkerStorage.Write(ds, rec);
                MarkerParameters.Write(ds, TextFor(rec, c.Shown), c.StatusOrder);
            }

            foreach (var c in changes)
            {
                var ds = doc.GetElement(c.MarkerUid) as DirectShape;
                if (ds == null) throw new InvalidOperationException("A marker the plan changes is no longer in the model.");
                var after = c.After;
                if (c.Moved || c.PutBack) MarkerElements.Move(doc, ds, new XYZ(after.X, after.Y, after.Z));
                if (c.WorksetFix && workset >= 0)
                {
                    var p = ds.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p != null && !p.IsReadOnly) MarkerElements.WhilePinnedOff(ds, () => p.Set(workset));
                }
                MarkerStorage.Write(ds, after);
                MarkerParameters.Write(ds, TextFor(after, c.Shown), c.StatusOrder);
            }

            // The numbers just taken: the user's own counter record, written in the same transaction as the markers that carry them.
            if (counterAfter != null) MarkerSettings.WriteMyCounter(doc, counterAfter);
        }

        private static string RecordKey(MarkerRecord r)
        {
            return string.Join("\n", MarkerFormats.Write(r).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        }

        /// <summary>The counts after the commit, read from the MODEL, never from the plan (C5).</summary>
        private static MarkerSummary.Counts Reread(Document doc, MarkerSummary.Counts planCounts, List<MarkerPlan.Creation> creations,
            List<MarkerPlan.Change> changes, List<MarkerPlan.Removal> removals, MarkerApplyResult result)
        {
            var model = MarkerElements.Collect(doc);
            var byUid = model.Where(m => m.Read != null && m.Read.Status == MarkerReadStatus.Ok).GroupBy(m => m.Uid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var known = new HashSet<string>(model.Where(m => m.Read != null && m.Read.Record != null).Select(m => RecordKey(m.Read.Record)), StringComparer.Ordinal);

            result.Created = creations.Count(c => known.Contains(RecordKey(c.Record)));
            result.Updated = 0;
            result.Resolved = 0;
            foreach (var c in changes)
            {
                MarkerFound m;
                if (!byUid.TryGetValue(c.MarkerUid, out m) || RecordKey(m.Read.Record) != RecordKey(c.After)) continue;
                if (c.Resolved) result.Resolved++; else result.Updated++;
            }
            result.Deleted = removals.Count(r => doc.GetElement(r.MarkerUid) == null);

            var counts = Copy(planCounts);
            counts.Created = result.Created;
            counts.Updated = result.Updated;
            counts.Resolved = result.Resolved;
            counts.Removed = result.Deleted;   // what is really gone from the model (C5)
            foreach (var group in result.PassedOverNow.GroupBy(r => r.Reason))
                counts.PassedOver.Add(new MarkerSummary.Reason { Text = group.Key + " (at the last moment)", Count = group.Count() });
            return counts;
        }
    }
}
