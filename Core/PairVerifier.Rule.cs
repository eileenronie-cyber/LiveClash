using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    public static partial class PairVerifier
    {
        /// <summary>A rule given to the verifier instead of the seat's settings.</summary>
        private sealed class Rule
        {
            public double ClearanceFeet;
            public bool Insulated;
        }

        /// <summary>
        /// Re-tests one pair under an EXPLICIT rule instead of the seat's settings: the clearance and the insulation
        /// choice a clash marker was found under (spec 6.4, E3). A seat with a smaller clearance, or without insulation
        /// sets, cannot read a pair Resolved because its own rule is looser. The settings are not read.
        /// </summary>
        [RevitBound]
        public static Verdict Verify(
            Document doc, ClashResult r, Dictionary<long, LinkContext> links,
            double clearanceMm, bool insulated, out ClashSeverity severity)
        {
            severity = r == null ? ClashSeverity.Hard : r.Severity;
            if (doc == null || !doc.IsValidObject || r == null) return Verdict.Unknown;

            Element host;
            try { host = doc.GetElement(r.HostElementId); } catch { return Verdict.Unknown; }
            if (host == null || !host.IsValidObject) return Verdict.Resolved;   // deleted

            var rule = new Rule
            {
                ClearanceFeet = UnitUtils.ConvertToInternalUnits(Math.Max(0.0, clearanceMm), UnitTypeId.Millimeters),
                Insulated = insulated
            };
            return r.LinkInstanceId == null
                ? VerifyHost(doc, host, r, null, out severity, rule)
                : VerifyLink(doc, host, r, null, links, out severity, rule);
        }

        /// <summary>
        /// The rule the seat's settings imply for this pair: the largest clearance, in millimetres, of the enabled sets whose
        /// scope covers it, and whether any such set is insulation-aware. What a new marker records. False when the pair's
        /// elements cannot be read.
        /// </summary>
        [RevitBound]
        internal static bool RuleFor(
            Document doc, ClashResult r, ClashSettings s, Dictionary<long, LinkContext> links,
            out double clearanceMm, out bool insulated)
        {
            clearanceMm = 0;
            insulated = false;
            if (doc == null || !doc.IsValidObject || r == null || s == null) return false;

            Element host, other;
            string linkName = null;
            try
            {
                host = doc.GetElement(r.HostElementId);
                if (r.LinkInstanceId == null) other = doc.GetElement(r.OtherElementId);
                else
                {
                    LinkContext link = null;
                    if (links != null) links.TryGetValue(IdUtil.Value(r.LinkInstanceId), out link);
                    if (link == null || link.Document == null || !link.Document.IsValidObject) return false;
                    other = link.Document.GetElement(r.OtherElementId);
                    linkName = link.Name;
                }
            }
            catch { return false; }
            if (host == null || !host.IsValidObject || other == null || !other.IsValidObject) return false;

            clearanceMm = UnitUtils.ConvertFromInternalUnits(MaxClearanceForPair(s, host, other, linkName), UnitTypeId.Millimeters);
            insulated = AnySetWantsInsulation(s, host, other, linkName);
            return true;
        }
    }
}
