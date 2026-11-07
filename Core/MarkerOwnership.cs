using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Who may write which marker (spec 6.2, 8): the read-only status that feeds the plan, and the borrowing of exactly the markers
    /// a plan changes. A marker someone else holds, or that changed in central, is PASSED OVER and named, never forced. Reading
    /// needs no borrowing. The rules themselves are the pure <see cref="MarkerOwnershipRules"/>.
    /// </summary>
    [RevitBound]
    public static class MarkerOwnership
    {

        public sealed class CheckoutResult
        {
            public List<string> Granted { get; } = new List<string>();
            /// <summary>Marker UniqueId to the words for the summary ("held by kdoe").</summary>
            public Dictionary<string, string> PassedOver { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>Borrowing only exists in a workshared model that has a central: a workshared document never saved (no path) has nothing to borrow from.</summary>
        public static bool HasCentral(Document doc)
        {
            return doc != null && doc.IsWorkshared && !string.IsNullOrEmpty(doc.PathName);
        }

        private static MarkerAccessVerdict Overridden(string uid)
        {
            return null;
        }

        private static MarkerAccessVerdict Passed(MarkerAccess kind, string reason)
        {
            return new MarkerAccessVerdict { Kind = kind, Reason = reason };
        }

        /// <summary>The access to a marker right now, without borrowing anything.</summary>
        public static MarkerAccessVerdict Verdict(Document doc, MarkerFound m)
        {
            var forced = Overridden(m.Uid);
            if (forced != null) return forced;
            if (m.Read == null || m.Read.Status == MarkerReadStatus.NewerFormat) return Passed(MarkerAccess.NewerFormat, "written by a newer LiveClash");
            if (m.Read.Status != MarkerReadStatus.Ok) return Passed(MarkerAccess.Unreadable, "its record could not be read");

            var owner = MarkerOwner.Unowned;
            string ownerName = "";
            var central = MarkerCentral.Current;
            if (HasCentral(doc))
            {
                try
                {
                    string name;
                    var status = WorksharingUtils.GetCheckoutStatus(doc, m.Id, out name);
                    owner = status == CheckoutStatus.OwnedByOtherUser ? MarkerOwner.OwnedByOther
                          : status == CheckoutStatus.OwnedByCurrentUser ? MarkerOwner.OwnedByMe : MarkerOwner.Unowned;
                    ownerName = name ?? "";
                    var updates = WorksharingUtils.GetModelUpdatesStatus(doc, m.Id);
                    central = updates == ModelUpdatesStatus.UpdatedInCentral ? MarkerCentral.UpdatedInCentral
                            : updates == ModelUpdatesStatus.DeletedInCentral ? MarkerCentral.DeletedInCentral : MarkerCentral.Current;
                }
                catch (Exception ex)
                {
                    Log.Warn("Marker ownership could not be read; the marker is passed over", ex);
                    return Passed(MarkerAccess.HeldByOther, "its ownership could not be read");
                }
            }
            return MarkerOwnershipRules.Classify(owner, ownerName, central, m.Grouped);
        }

        /// <summary>The planner's view of a marker: access, a hash of the display parameters, whether it is off its point, and whether it is on the wrong workset.</summary>
        public static MarkerPlan.Existing ToExisting(Document doc, MarkerFound m, int markerWorkset)
        {
            var verdict = Verdict(doc, m);
            var record = m.Read != null && m.Read.Status == MarkerReadStatus.Ok ? m.Read.Record : null;
            bool off = record != null && (m.Center == null
                || Math.Abs(m.Center.X - record.X) > MarkerPlan.PointTolerance
                || Math.Abs(m.Center.Y - record.Y) > MarkerPlan.PointTolerance
                || Math.Abs(m.Center.Z - record.Z) > MarkerPlan.PointTolerance);
            return new MarkerPlan.Existing
            {
                MarkerUid = m.Uid, Record = record, Access = verdict.Kind, HeldBy = verdict.HeldBy,
                ShownHash = MarkerParameters.CurrentShownHash(m.Element, record),
                OffPlace = off,
                WrongWorkset = doc.IsWorkshared && markerWorkset >= 0 && m.Workset != markerWorkset
            };
        }

        /// <summary>
        /// Borrows exactly these markers (never creations, never unchanged ones). The ones that cannot be had are returned with their
        /// reason. A model that is not workshared grants everything. Needs no transaction.
        /// </summary>
        public static CheckoutResult Checkout(Document doc, IEnumerable<string> uids)
        {
            var result = new CheckoutResult();
            var ask = new List<string>();
            foreach (var uid in uids.Distinct(StringComparer.Ordinal))
            {
                var forced = Overridden(uid);
                if (forced != null && !forced.CanWrite) { result.PassedOver[uid] = forced.Reason; continue; }
                ask.Add(uid);
            }
            if (!HasCentral(doc) || ask.Count == 0)
            {
                result.Granted.AddRange(ask);
                return result;
            }

            var byId = new Dictionary<long, string>();
            var ids = new List<ElementId>();
            foreach (var uid in ask)
            {
                var e = doc.GetElement(uid);
                if (e == null) { result.PassedOver[uid] = "it is gone"; continue; }
                byId[IdUtil.Value(e.Id)] = uid;
                ids.Add(e.Id);
            }
            if (ids.Count == 0) return result;

            ICollection<ElementId> got = null;
            try
            {
                got = WorksharingUtils.CheckoutElements(doc, ids);
            }
            catch (Exception ex) { Log.Warn("CheckoutElements failed", ex); }
            var gotValues = new HashSet<long>((got ?? new List<ElementId>()).Select(IdUtil.Value));
            foreach (var id in ids)
            {
                var uid = byId[IdUtil.Value(id)];
                string owner;
                bool mine = false;
                try { mine = WorksharingUtils.GetCheckoutStatus(doc, id, out owner) == CheckoutStatus.OwnedByCurrentUser; } catch { }
                if (gotValues.Contains(IdUtil.Value(id)) || mine) { result.Granted.Add(uid); continue; }
                var why = "it could not be borrowed";
                try
                {
                    var e = doc.GetElement(id);
                    var found = new MarkerFound { Element = e as DirectShape, Id = id, Uid = uid, Read = new MarkerReadResult { Status = MarkerReadStatus.Ok } };
                    var v = Verdict(doc, found);
                    if (!v.CanWrite && !string.IsNullOrEmpty(v.Reason)) why = v.Reason;
                }
                catch { }
                result.PassedOver[uid] = why;
            }
            return result;
        }
    }
}
