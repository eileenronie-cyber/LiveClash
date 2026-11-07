using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    public sealed class MarkerTriageRequest
    {
        public List<string> MarkerUids { get; set; } = new List<string>();
        /// <summary>Null keeps the status.</summary>
        public MarkerStatus? Status { get; set; }
        /// <summary>Null keeps the note; empty clears it.</summary>
        public string Note { get; set; }
        /// <summary>Null keeps the assignee; empty unassigns.</summary>
        public string AssignedTo { get; set; }
        public string User { get; set; } = "";
        /// <summary>The time of the decision, from the caller: this code reads no clock.</summary>
        public DateTimeOffset Now { get; set; }
    }

    /// <summary>A triage the model did not take (spec 6.7): the actions object records it in the ledger as "pending to model" with this Seq.</summary>
    public sealed class MarkerTriagePending
    {
        public string MarkerUid { get; set; }
        public string Key { get; set; }
        public long SeqAtPending { get; set; }
        public string Reason { get; set; } = "";
        /// <summary>What was asked, so the ledger's pending line can hold it: null when that part was not asked.</summary>
        public MarkerStatus? Status { get; set; }
        public string Note { get; set; }
        public string AssignedTo { get; set; }
    }

    public sealed class MarkerTriageResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public List<string> Written { get; set; } = new List<string>();
        public List<MarkerTriagePending> Pending { get; set; } = new List<MarkerTriagePending>();
        public int Unchanged { get; set; }
    }

    /// <summary>
    /// A status or a note set from the panel on rows that have a marker (spec 6.7). The marker is written first, in ONE plain transaction, and
    /// only the markers that change are borrowed. A marker that is held, changed in central, in a group or unreadable is NOT written: it comes
    /// back as pending with the Seq it was read at, so a later Refresh can apply it if nobody else touched the marker meanwhile.
    /// </summary>
    [RevitBound]
    public static class MarkerTriage
    {
        private static MarkerRecord Clone(MarkerRecord r)
        {
            return MarkerReader.Default.Read(MarkerFormats.Format1, MarkerFormats.Write(r)).Record;
        }

        private static Dictionary<string, string> TextFor(MarkerRecord record)
        {
            var text = MarkerParameterMap.Build(record, null);
            foreach (var n in MarkerParameterMap.ShownNames) text.Remove(n);   // the display columns stay as they are
            return text;
        }

        private static string Said(Exception ex, MarkerRefresh.Failures failures)
        {
            var first = failures.Errors.Count > 0 ? failures.Errors[0] : ex.Message;
            var said = first.TrimEnd();
            return said + (said.Length > 0 && said[said.Length - 1] == '.' ? " " : ". ") + "Nothing was changed.";
        }

        public static MarkerTriageResult Apply(Document doc, MarkerTriageRequest request)
        {
            var result = new MarkerTriageResult();
            try
            {
                if (doc == null || !doc.IsValidObject || request == null) { result.Message = "There is no project to write to."; return result; }
                var env = MarkerRefresh.ReadEnv(doc);
                env.ForeignParameterName = "";
                env.SettingsHeldBy = "";
                var refusal = MarkerRefusals.Check(env);
                if (refusal != null) { result.Message = refusal; return result; }

                var wanted = new HashSet<string>(request.MarkerUids ?? new List<string>(), StringComparer.Ordinal);
                var found = MarkerElements.Collect(doc).Where(m => wanted.Contains(m.Uid)).OrderBy(m => m.Uid, StringComparer.Ordinal).ToList();
                foreach (var missing in wanted.Where(u => found.All(m => m.Uid != u)).OrderBy(u => u, StringComparer.Ordinal))
                    result.Pending.Add(new MarkerTriagePending { MarkerUid = missing, Reason = "the marker is not in the model", Status = request.Status, Note = request.Note, AssignedTo = request.AssignedTo });

                var todo = new List<Tuple<MarkerFound, MarkerRecord>>();
                foreach (var m in found)
                {
                    var verdict = MarkerOwnership.Verdict(doc, m);
                    var rec = m.Read != null && m.Read.Status == MarkerReadStatus.Ok ? m.Read.Record : null;
                    if (!verdict.CanWrite || rec == null)
                    {
                        result.Pending.Add(new MarkerTriagePending { MarkerUid = m.Uid, Key = rec == null ? null : rec.Key, SeqAtPending = rec == null ? 0 : rec.Seq, Reason = verdict.Reason, Status = request.Status, Note = request.Note, AssignedTo = request.AssignedTo });
                        continue;
                    }
                    var after = Clone(rec);
                    bool changed = false;
                    if (request.Status.HasValue && request.Status.Value != rec.Status)
                    {
                        after.Status = request.Status.Value;
                        if (after.Status != MarkerStatus.Resolved) { after.ResolvedBy = ""; after.ResolvedAt = null; }
                        changed = true;
                    }
                    if (request.Note != null && request.Note != (rec.Note ?? "")) { after.Note = request.Note; changed = true; }
                    if (request.AssignedTo != null && request.AssignedTo != (rec.AssignedTo ?? "")) { after.AssignedTo = request.AssignedTo; changed = true; }
                    if (!changed) { result.Unchanged++; continue; }
                    after.ChangedBy = request.User ?? "";
                    after.ChangedAt = request.Now;
                    after.Seq = rec.Seq + 1;
                    todo.Add(Tuple.Create(m, after));
                }

                if (todo.Count > 0)
                {
                    var got = MarkerOwnership.Checkout(doc, todo.Select(t => t.Item1.Uid));
                    foreach (var kv in got.PassedOver)
                    {
                        var t = todo.First(x => x.Item1.Uid == kv.Key);
                        result.Pending.Add(new MarkerTriagePending { MarkerUid = kv.Key, Key = t.Item2.Key, SeqAtPending = t.Item1.Read.Record.Seq, Reason = kv.Value, Status = request.Status, Note = request.Note, AssignedTo = request.AssignedTo });
                    }
                    todo = todo.Where(t => !got.PassedOver.ContainsKey(t.Item1.Uid)).ToList();
                }

                if (todo.Count == 0)
                {
                    result.Ok = true;
                    result.Message = result.Pending.Count == 0 ? "Nothing to change." : result.Pending.Count + " waiting for the model (" + string.Join(", ", result.Pending.Select(p => p.Reason).Distinct(StringComparer.Ordinal)) + ").";
                    return result;
                }

                var failures = new MarkerRefresh.Failures();
                try
                {
                    MarkerRefresh.InTransaction(doc, MarkerTx.SetStatus, failures, false, () =>
                    {
                        foreach (var t in todo)
                        {
                            MarkerStorage.Write(t.Item1.Element, t.Item2);
                            MarkerParameters.Write(t.Item1.Element, TextFor(t.Item2), MarkerStatusText.Order(t.Item2.Status));
                        }
                    });
                }
                catch (Exception ex)
                {
                    result.Pending.Clear();
                    result.Message = Said(ex, failures);
                    Log.Warn("Set clash status rolled back: " + result.Message, ex);
                    return result;
                }
                result.Written.AddRange(todo.Select(t => t.Item1.Uid));
                result.Ok = true;
                result.Message = "Set " + result.Written.Count + " marker" + (result.Written.Count == 1 ? "" : "s") + "."
                    + (result.Pending.Count == 0 ? "" : " " + result.Pending.Count + " waiting for the model (" + string.Join(", ", result.Pending.Select(p => p.Reason).Distinct(StringComparer.Ordinal)) + ").");
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn("Set clash status failed", ex);
                return new MarkerTriageResult { Ok = false, Message = ex.Message + " Nothing was changed." };
            }
        }
    }
}
