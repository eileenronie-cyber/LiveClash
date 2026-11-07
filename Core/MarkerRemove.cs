using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    public sealed class MarkerRemoveResult
    {
        public bool Ok { get; set; }
        /// <summary>For the panel: what happened, or why it did not (a failure ends with "Nothing was changed.").</summary>
        public string Message { get; set; } = "";
        /// <summary>Nothing to remove: no group, no transaction, nothing borrowed.</summary>
        public bool NoOp { get; set; }
        public int Removed { get; set; }
        /// <summary>Markers not removed because another user holds them, they changed in central, or they are in a group.</summary>
        public List<MarkerPlan.Reasoned> PassedOver { get; set; } = new List<MarkerPlan.Reasoned>();
        public TransactionStatus GroupStatus { get; set; }
    }

    /// <summary>
    /// Remove resolved, Remove all markers and Remove LiveClash parameters (spec 6.9). Each one refuses before writing anything, does nothing
    /// when there is nothing to do, borrows only what it deletes and passes over what it cannot have, and deletes inside ONE transaction
    /// group assimilated into one Undo, rolled back whole on any error. The local ledgers are not touched (the record keeps everything).
    /// </summary>
    [RevitBound]
    public static class MarkerRemove
    {

        private static MarkerRemoveResult Fail(string message)
        {
            return new MarkerRemoveResult { Ok = false, Message = message };
        }

        private static MarkerRemoveResult Done(string message, bool noOp)
        {
            return new MarkerRemoveResult { Ok = true, NoOp = noOp, Message = message };
        }

        private static string Refusal(Document doc, bool needsSettings)
        {
            if (doc == null || !doc.IsValidObject) return "There is no project to remove from.";
            var env = MarkerRefresh.ReadEnv(doc);
            env.ForeignParameterName = "";   // a foreign parameter is not ours to remove or to refuse over
            env.SettingsHeldBy = needsSettings ? MarkerSettings.HeldByOther(doc) : "";
            return MarkerRefusals.Check(env);
        }

        private static string Said(Exception ex, MarkerRefresh.Failures failures)
        {
            var first = failures.Errors.Count > 0 ? failures.Errors[0] : ex.Message;
            var said = first.TrimEnd();
            return said + (said.Length > 0 && said[said.Length - 1] == '.' ? " " : ". ") + "Nothing was changed.";
        }

        /// <summary>One transaction group around one transaction. Never throws; the result says what happened.</summary>
        private static void RunGroup(Document doc, string groupName, string transactionName, MarkerRemoveResult result, Action body)
        {
            var failures = new MarkerRefresh.Failures();
            TransactionGroup group = null;
            try
            {
                group = new TransactionGroup(doc, groupName);
                group.Start();
                MarkerRefresh.InTransaction(doc, transactionName, failures, false, () =>
                {
                    body();
                });
                result.GroupStatus = group.Assimilate();
                if (result.GroupStatus != TransactionStatus.Committed) throw new MarkerRefresh.RolledBack("Revit did not keep the change.");
                result.Ok = true;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Removed = 0;
                result.Message = Said(ex, failures);
                Log.Warn("Marker removal rolled back: " + result.Message, ex);
            }
            finally
            {
                try { if (group != null && group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch (Exception ex) { Log.Warn("Marker removal group did not roll back", ex); }
                try { if (group != null) group.Dispose(); } catch { }
            }
        }

        private static void Delete(Document doc, IEnumerable<ElementId> ids)
        {
            var list = ids.Distinct().ToList();
            foreach (var id in list)
            {
                var e = doc.GetElement(id);
                if (e != null && e.Pinned) e.Pinned = false;
            }
            if (list.Count > 0) doc.Delete(list);
        }

        /// <summary>Splits markers into those this user may delete now and those that are passed over, borrowing exactly the first kind.</summary>
        private static List<MarkerFound> Deletable(Document doc, List<MarkerFound> candidates, MarkerRemoveResult result)
        {
            var free = new List<MarkerFound>();
            foreach (var m in candidates)
            {
                var v = MarkerOwnership.Verdict(doc, m);
                if (v.CanWrite) free.Add(m);
                else result.PassedOver.Add(new MarkerPlan.Reasoned { MarkerUid = m.Uid, Key = m.Read != null && m.Read.Record != null ? m.Read.Record.Key : null, Reason = v.Reason });
            }
            if (free.Count > 0)
            {
                var got = MarkerOwnership.Checkout(doc, free.Select(m => m.Uid));
                foreach (var kv in got.PassedOver)
                    result.PassedOver.Add(new MarkerPlan.Reasoned { MarkerUid = kv.Key, Reason = kv.Value });
                free = free.Where(m => !got.PassedOver.ContainsKey(m.Uid)).ToList();
            }
            return free;
        }

        private static string PassedText(MarkerRemoveResult r)
        {
            if (r.PassedOver.Count == 0) return "";
            return " " + r.PassedOver.Count + " could not be removed (" + string.Join(", ", r.PassedOver.Select(p => p.Reason).Distinct(StringComparer.Ordinal)) + ").";
        }

        // ------------------------------------------------------------------ resolved

        /// <summary>Deletes the markers whose status is Resolved (spec 6.9).</summary>
        public static MarkerRemoveResult Resolved(Document doc)
        {
            try
            {
                var refusal = Refusal(doc, false);
                if (refusal != null) return Fail(refusal);
                var resolved = MarkerElements.Collect(doc)
                    .Where(m => m.Read != null && m.Read.Status == MarkerReadStatus.Ok && m.Read.Record.Status == MarkerStatus.Resolved).ToList();
                if (resolved.Count == 0) return Done("No resolved markers to remove.", true);

                var result = new MarkerRemoveResult();
                var free = Deletable(doc, resolved, result);
                if (free.Count == 0)
                {
                    result.Ok = true;
                    result.NoOp = true;
                    result.Message = "No resolved marker could be removed." + PassedText(result);
                    return result;
                }
                RunGroup(doc, MarkerTx.RemoveResolved, MarkerTx.RemoveResolved, result, () => Delete(doc, free.Select(m => m.Id)));
                if (result.Ok)
                {
                    result.Removed = free.Count;
                    result.Message = "Removed " + free.Count + " resolved marker" + (free.Count == 1 ? "" : "s") + "." + PassedText(result);
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn("Remove resolved failed", ex);
                return Fail(ex.Message + " Nothing was changed.");
            }
        }

        // ------------------------------------------------------------------ all

        /// <summary>
        /// Deletes every marker this user can have. When none had to be passed over it also deletes the schedule, the marker type, the settings
        /// record and the counter records, and leaves the parameter bindings (removing them is a separate, confirmed command).
        /// </summary>
        public static MarkerRemoveResult All(Document doc)
        {
            try
            {
                var refusal = Refusal(doc, true);
                if (refusal != null) return Fail(refusal);
                var markers = MarkerElements.Collect(doc);
                var result = new MarkerRemoveResult();
                var free = Deletable(doc, markers, result);

                var schedule = MarkerSchedule.Find(doc);
                var typeId = MarkerElements.FindType(doc);
                // The settings and counter records are borrowed like the markers (review L5): one another user holds is passed over and named, and then
                // the schedule, the type and the other records stay too, as they do for a held marker.
                var recordUids = result.PassedOver.Count == 0 ? MarkerSettings.RecordUids(doc) : new List<string>();
                if (recordUids.Count > 0)
                {
                    var got = MarkerOwnership.Checkout(doc, recordUids);
                    foreach (var kv in got.PassedOver)
                        result.PassedOver.Add(new MarkerPlan.Reasoned { MarkerUid = kv.Key, Reason = kv.Value });
                    recordUids = recordUids.Where(u => !got.PassedOver.ContainsKey(u)).ToList();
                }
                bool nothingStays = result.PassedOver.Count == 0;
                var records = new List<ElementId>();
                if (nothingStays)
                {
                    if (schedule != null) records.Add(schedule.Id);
                    foreach (var uid in recordUids)
                    {
                        var e = doc.GetElement(uid);
                        if (e != null) records.Add(e.Id);
                    }
                    if (typeId != null) records.Add(typeId);
                }

                if (free.Count == 0 && records.Count == 0)
                {
                    result.Ok = true;
                    result.NoOp = true;
                    result.Message = markers.Count == 0 ? "There are no clash markers to remove." : "No clash marker could be removed." + PassedText(result);
                    return result;
                }

                var remaining = records;
                RunGroup(doc, MarkerTx.RemoveAll, MarkerTx.RemoveAll, result, () =>
                {
                    Delete(doc, free.Select(m => m.Id));
                    Delete(doc, remaining);
                });
                if (result.Ok)
                {
                    result.Removed = free.Count;
                    result.Message = "Removed " + free.Count + " marker" + (free.Count == 1 ? "" : "s")
                        + (nothingStays ? ", the clash schedule and LiveClash's records." : "; the schedule and records stay for the others.") + PassedText(result);
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn("Remove all failed", ex);
                return Fail(ex.Message + " Nothing was changed.");
            }
        }

        // ------------------------------------------------------------------ parameters

        /// <summary>Removes LiveClash's parameter bindings. Refused while any marker exists, and while the schedule still uses the parameters.</summary>
        public static MarkerRemoveResult Parameters(Document doc)
        {
            try
            {
                var refusal = Refusal(doc, false);
                if (refusal != null) return Fail(refusal);
                int markers = MarkerElements.Collect(doc).Count;
                if (markers > 0)
                    return Fail("Remove the " + markers + " clash marker" + (markers == 1 ? "" : "s") + " first: removing the parameters would delete their data. Nothing was changed.");
                if (MarkerSchedule.Find(doc) != null)
                    return Fail("The clash schedule still uses the parameters. Use Remove all markers first (it removes the schedule too). Nothing was changed.");
                if (!MarkerParameters.AnyBound(doc)) return Done("LiveClash's parameters are not in this model.", true);

                var result = new MarkerRemoveResult();
                int removed = 0;
                RunGroup(doc, MarkerTx.RemoveParameters, MarkerTx.RemoveParameters, result, () => { removed = MarkerParameters.RemoveBindings(doc); });
                if (result.Ok)
                {
                    result.Removed = removed;
                    result.Message = "Removed " + removed + " LiveClash parameter" + (removed == 1 ? "" : "s") + " from this model.";
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn("Remove parameters failed", ex);
                return Fail(ex.Message + " Nothing was changed.");
            }
        }
    }
}
