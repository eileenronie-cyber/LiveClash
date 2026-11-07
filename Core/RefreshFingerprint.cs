using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// What a Refresh plan would do, as three numbers, so Apply can tell that it would now do MORE than the plan the user was shown (independent
    /// review 2026-11-01, L2): a full check or a live edit between the dialog and the click can change the plan, and the dialog is the user's consent
    /// for what it said. Doing less is harmless and allowed. Pure.
    /// </summary>
    public sealed class RefreshFingerprint
    {
        public int Creations { get; set; }
        public int Removals { get; set; }
        public int RetentionOffers { get; set; }
        public int Changes { get; set; }
        public int LostDecisions { get; set; }
        /// <summary>Who is in the plan, so a swap at the same count is seen (null when not known: the counts alone then decide).</summary>
        public HashSet<string> CreationIds { get; set; }
        public HashSet<string> ChangeIds { get; set; }
        public HashSet<string> RemovalIds { get; set; }
        public HashSet<string> OfferIds { get; set; }

        public static RefreshFingerprint Of(MarkerPlan.Result plan)
        {
            if (plan == null) return new RefreshFingerprint();
            return new RefreshFingerprint
            {
                Creations = plan.Creations.Count, Removals = plan.Removals.Count, RetentionOffers = plan.RetentionOffers.Count,
                Changes = plan.Changes.Count, LostDecisions = plan.LostDecisions.Count,
                CreationIds = Ids(plan.Creations.Select(c => c.Triple)), ChangeIds = Ids(plan.Changes.Select(c => c.MarkerUid)),
                RemovalIds = Ids(plan.Removals.Select(r => r.MarkerUid)), OfferIds = Ids(plan.RetentionOffers.Select(r => r.MarkerUid))
            };
        }

        private static HashSet<string> Ids(IEnumerable<string> ids)
        {
            return new HashSet<string>(ids.Where(i => i != null), StringComparer.Ordinal);
        }

        /// <summary>True when <paramref name="now"/> holds something that <paramref name="allowed"/> does not; false when either is not known.</summary>
        private static bool Other(HashSet<string> now, HashSet<string> allowed)
        {
            if (now == null || allowed == null) return false;
            return now.Any(i => !allowed.Contains(i));
        }

        /// <summary>
        /// The words for refusing, or null when applying is fine. <paramref name="shown"/> is null when no plan was shown first. The shown plan is made
        /// without the retention tick, so with it ticked the removals may grow by exactly the retention offers that were shown.
        /// </summary>
        public static string Changed(RefreshFingerprint shown, RefreshFingerprint now, bool retentionTicked)
        {
            if (shown == null || now == null) return null;
            var reasons = new List<string>();
            if (now.Creations > shown.Creations)
                reasons.Add("it would now create " + now.Creations + " markers, not " + shown.Creations);
            int allowedRemovals = shown.Removals + (retentionTicked ? shown.RetentionOffers : 0);
            if (now.Removals > allowedRemovals)
                reasons.Add("it would now remove " + now.Removals + " markers, not " + allowedRemovals);
            if (now.RetentionOffers > shown.RetentionOffers)
                reasons.Add(now.RetentionOffers + " markers are now old enough to remove, not " + shown.RetentionOffers);
            if (now.Changes > shown.Changes)
                reasons.Add("it would now change " + now.Changes + " markers, not " + shown.Changes);
            if (now.LostDecisions > shown.LostDecisions)
                reasons.Add("it would now lose " + now.LostDecisions + " decisions, not " + shown.LostDecisions);

            // The same number but not the same markers (a swap) is more than was shown too: the user agreed to those, not to these.
            if (now.Creations <= shown.Creations && Other(now.CreationIds, shown.CreationIds))
                reasons.Add("it would now create other markers than the ones shown");
            if (now.Changes <= shown.Changes && Other(now.ChangeIds, shown.ChangeIds))
                reasons.Add("it would now change other markers than the ones shown");
            if (now.Removals <= allowedRemovals && shown.RemovalIds != null && shown.OfferIds != null)
            {
                var allowedIds = new HashSet<string>(shown.RemovalIds, StringComparer.Ordinal);
                if (retentionTicked) allowedIds.UnionWith(shown.OfferIds);
                if (Other(now.RemovalIds, allowedIds)) reasons.Add("it would now remove other markers than the ones shown");
            }
            if (now.RetentionOffers <= shown.RetentionOffers && Other(now.OfferIds, shown.OfferIds))
                reasons.Add("other markers are now old enough to remove than the ones shown");
            if (reasons.Count == 0) return null;
            return "The clashes changed since the plan was shown: " + string.Join(" and ", reasons) + ". Review the plan again. Nothing was changed.";
        }
    }
}
