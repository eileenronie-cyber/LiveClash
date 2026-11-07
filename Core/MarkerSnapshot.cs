using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The snapshot the panel reads (spec 9): what the model says about the markers, as plain data. Built on the Revit thread after each
    /// action and once at start; the panel never reads Revit for the schedule. A row is keyed by the record's key (the panel's row key when the
    /// marker was written or last re-keyed). The pending fields and the out-of-date count come from the ledger and the plan (4f-3c).
    /// </summary>
    [RevitBound]
    public static class MarkerSnapshot
    {

        public static ScheduleSnapshot Build(Document doc)
        {
            var snap = new ScheduleSnapshot();
            if (doc == null || !doc.IsValidObject) return snap;
            try
            {
                snap.Me = MarkerSettings.UserName(doc);
                snap.Workshared = doc.IsWorkshared;
                snap.RefusalReason = MarkerRefusals.Check(MarkerRefresh.ReadEnv(doc));
                snap.ParametersPresent = MarkerParameters.IsBound(doc);
                snap.HasSchedule = MarkerSchedule.Find(doc) != null;
                foreach (var m in MarkerElements.Collect(doc))
                {
                    snap.MarkerCount++;
                    var rec = m.Read != null && m.Read.Status == MarkerReadStatus.Ok ? m.Read.Record : null;
                    if (rec == null || string.IsNullOrEmpty(rec.Key)) continue;
                    if (rec.Status == MarkerStatus.Resolved) snap.ResolvedCount++;
                    var verdict = MarkerOwnership.Verdict(doc, m);
                    snap.Rows[rec.Key] = new RowScheduleInfo
                    {
                        HasMarker = true,
                        IssueNo = rec.IssueNo ?? "",
                        MergedIssueNos = rec.MergedIssueNos == null ? new List<string>() : new List<string>(rec.MergedIssueNos),
                        AssignedTo = rec.AssignedTo ?? "",
                        GroupLabel = rec.GroupLabel ?? "",
                        DecidedBy = rec.ChangedBy ?? "",
                        HeldBy = verdict.Kind == MarkerAccess.HeldByOther ? verdict.HeldBy ?? "" : ""
                    };
                }
                snap.Ready = true;   // only a complete read is ready: a throw above leaves it not ready (delta review D-M1)
            }
            catch (Exception ex)
            {
                snap.Ready = false;
                snap.LoadFailure = "The clash markers could not be read for this model. " + ex.Message;
                Log.Warn("The clash schedule snapshot could not be read", ex);
            }
            return snap;
        }
    }
}
