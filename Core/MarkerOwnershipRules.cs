using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>Who holds a marker, from Revit's checkout status. Plain, so the rules need no Revit.</summary>
    public enum MarkerOwner { Unowned, OwnedByMe, OwnedByOther }

    /// <summary>The marker against the central model (<c>GetModelUpdatesStatus</c>).</summary>
    public enum MarkerCentral { Current, UpdatedInCentral, DeletedInCentral }

    public sealed class MarkerAccessVerdict
    {
        public MarkerAccess Kind { get; set; }
        public string HeldBy { get; set; } = "";
        /// <summary>The words for the summary ("held by kdoe"); empty when free.</summary>
        public string Reason { get; set; } = "";
        public bool CanWrite { get; set; }
    }

    /// <summary>Which markers may be written and why the others are passed over (spec 6.2, 8). Pure.</summary>
    public static class MarkerOwnershipRules
    {
        /// <summary>
        /// Priority when several apply: inside a group (never editable at all), then held by another user (the actionable fact),
        /// then changed in central. A marker this user already holds is free.
        /// </summary>
        public static MarkerAccessVerdict Classify(MarkerOwner owner, string ownerName, MarkerCentral central, bool insideGroup)
        {
            if (insideGroup)
                return new MarkerAccessVerdict { Kind = MarkerAccess.InsideGroup, Reason = "inside a group" };
            if (owner == MarkerOwner.OwnedByOther)
            {
                var name = string.IsNullOrWhiteSpace(ownerName) ? "" : ownerName;
                return new MarkerAccessVerdict
                {
                    Kind = MarkerAccess.HeldByOther, HeldBy = name,
                    Reason = name.Length == 0 ? "held by another user" : "held by " + name
                };
            }
            if (central != MarkerCentral.Current)
                return new MarkerAccessVerdict { Kind = MarkerAccess.ChangedInCentral, Reason = "changed in central: Reload Latest first" };
            return new MarkerAccessVerdict { Kind = MarkerAccess.Free, CanWrite = true };
        }

        public static bool CanWrite(MarkerAccess kind)
        {
            return kind == MarkerAccess.Free;   // HeldByOther, ChangedInCentral, InsideGroup, NewerFormat and Unreadable are all passed over
        }
    }

    /// <summary>
    /// Drops the writes a pass-over decided (a marker another user took between the plan and the checkout), so the transaction
    /// writes all of the rest or none. A write with no existing uid is a create and is never dropped. Generic: the plan's own item type.
    /// </summary>
    public static class MarkerWriteFilter
    {
        public sealed class Split<T>
        {
            public List<T> Kept { get; set; } = new List<T>();
            public List<T> Dropped { get; set; } = new List<T>();
        }

        public static Split<T> Apply<T>(IEnumerable<T> writes, Func<T, string> existingUid, ISet<string> passedOver)
        {
            var result = new Split<T>();
            if (writes == null) return result;
            foreach (var w in writes)
            {
                var uid = existingUid == null ? null : existingUid(w);
                if (!string.IsNullOrEmpty(uid) && passedOver != null && passedOver.Contains(uid)) result.Dropped.Add(w);
                else result.Kept.Add(w);
            }
            return result;
        }

        /// <summary>True when nothing is left to write and nothing is missing: no transaction, no checkout, no Undo entry (H1).</summary>
        public static bool IsNoOp(int keptCount, bool setupNeeded)
        {
            return keptCount == 0 && !setupNeeded;
        }
    }
}
