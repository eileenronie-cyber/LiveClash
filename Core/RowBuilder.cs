using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// From one clash, the ledger and this seat's re-test to the plan's row (spec 4, 6.1, 6.4, 6.10). Pure: the
    /// Revit-bound adapter turns a ClashResult into a <see cref="Source"/> (UniqueIds, level at the clash point) and
    /// the re-test through the explicit-rule PairVerifier into a <see cref="Retest"/>; everything else is here.
    /// </summary>
    public static class RowBuilder
    {
        /// <summary>A clash as plain values.</summary>
        public sealed class Source
        {
            public string Key { get; set; }
            public string HostUid { get; set; }
            public string LinkUid { get; set; }
            public string OtherUid { get; set; }
            public long HostId { get; set; }
            public long LinkId { get; set; }
            public long OtherId { get; set; }
            /// <summary>"Hard", "Clearance" or "Unverified".</summary>
            public string Severity { get; set; }
            public bool HasPoint { get; set; } = true;
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
            public string SetName { get; set; }
            public List<string> AlsoSets { get; set; }
            /// <summary>Rows with one non-empty key are one proximity group; empty is a pair alone.</summary>
            public string GroupKey { get; set; }
            public string HostName { get; set; }
            public string HostCategory { get; set; }
            public string OtherName { get; set; }
            public string OtherCategory { get; set; }
            /// <summary>"Current model" or the link's name.</summary>
            public string ModelName { get; set; }
            /// <summary>The host level at or below the clash point.</summary>
            public string Level { get; set; }
            /// <summary>The rule this seat's settings imply for the pair (what a marker created now records).</summary>
            public double RuleClearanceMm { get; set; }
            public bool RuleInsulated { get; set; }
        }

        /// <summary>The seat's re-test of a pair that already has a marker, under that marker's own rule.</summary>
        public sealed class Retest
        {
            public PlanVerdict Verdict { get; set; }
            public string Reason { get; set; }
            public double ClearanceMm { get; set; }
            public bool Insulated { get; set; }
        }

        /// <summary>What the ledger holds for the pair.</summary>
        public sealed class Decision
        {
            public bool HasStatus { get; set; }
            public MarkerStatus Status { get; set; }
            public string Note { get; set; }
            public string By { get; set; }
            public DateTime WhenUtc { get; set; }
            public bool Gone { get; set; }
            public string AssignedTo { get; set; }
            public string IssueNo { get; set; }
        }

        public static MarkerPlan.Row Build(Source source, Retest retest, Decision decision, bool live)
        {
            if (source == null)
                return new MarkerPlan.Row { Live = false, Verdict = PlanVerdict.NotRetested, NotRetestedReason = "no clash" };

            var row = new MarkerPlan.Row
            {
                Key = source.Key, HostUid = source.HostUid, LinkUid = source.LinkUid, OtherUid = source.OtherUid,
                HostId = source.HostId, LinkId = source.LinkId, OtherId = source.OtherId, Live = live,
                Severity = source.Severity, HasPoint = source.HasPoint, X = source.X, Y = source.Y, Z = source.Z,
                SetName = source.SetName,
                AlsoSets = live ? (source.AlsoSets ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList() : null,
                GroupKey = live ? source.GroupKey ?? "" : null
            };

            if (retest != null)
            {
                row.Verdict = retest.Verdict; row.NotRetestedReason = retest.Reason;
                row.RuleClearanceMm = retest.ClearanceMm; row.RuleInsulated = retest.Insulated;
            }
            else
            {
                row.RuleClearanceMm = source.RuleClearanceMm; row.RuleInsulated = source.RuleInsulated;
                row.Verdict = live ? PlanVerdict.Clashing : PlanVerdict.NotRetested;
                if (!live) row.NotRetestedReason = "not live on this seat";
            }

            row.Shown = ShownOf(source);
            row.ShownHash = HashOf(row.Shown);

            if (decision != null)
            {
                row.RememberedIssueNo = decision.IssueNo;
                bool hasAssignee = !string.IsNullOrEmpty(decision.AssignedTo);
                if (!decision.Gone && (decision.HasStatus || hasAssignee))
                    row.Seed = new MarkerPlan.Seed
                    {
                        Status = decision.HasStatus ? decision.Status : MarkerStatus.New, Note = decision.Note, AssignedTo = decision.AssignedTo,
                        By = decision.By, At = decision.HasStatus ? new DateTimeOffset(DateTime.SpecifyKind(decision.WhenUtc, DateTimeKind.Utc)) : (DateTimeOffset?)null
                    };
            }
            return row;
        }

        /// <summary>The display values a marker shows, keyed by the schedule's parameter names.</summary>
        public static Dictionary<string, string> ShownOf(Source source)
        {
            var s = source ?? new Source();
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "LC A Id", s.HostId.ToString(CultureInfo.InvariantCulture) }, { "LC A Name", s.HostName ?? "" }, { "LC A Category", s.HostCategory ?? "" },
                { "LC B Id", s.OtherId.ToString(CultureInfo.InvariantCulture) }, { "LC B Name", s.OtherName ?? "" }, { "LC B Category", s.OtherCategory ?? "" },
                { "LC B Model", s.ModelName ?? "" }, { "LC Level", s.Level ?? "" }
            };
        }

        /// <summary>An order-independent hash of a display map; "" for none. The marker's side is computed from its parameters with the same function.</summary>
        public static string HashOf(IDictionary<string, string> shown)
        {
            if (shown == null || shown.Count == 0) return "";
            return ShortHash.Of(string.Join("\n", shown.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (kv.Value ?? ""))));
        }

        public static Decision DecisionOf(ClashStatusEntry entry, LedgerExtras.AssignedEntry assigned, LedgerExtras.IssueEntry issue)
        {
            var d = new Decision { Note = "", By = "", AssignedTo = assigned == null ? "" : assigned.Assignee ?? "", IssueNo = issue == null ? "" : issue.IssueNo ?? "" };
            if (entry != null)
            {
                d.HasStatus = true; d.Status = StatusOf(entry.Kind); d.Note = entry.Note ?? ""; d.By = entry.By ?? ""; d.WhenUtc = entry.WhenUtc; d.Gone = entry.Gone;
            }
            return d;
        }

        public static MarkerStatus StatusOf(ClashStatusKind kind)
        {
            switch (kind)
            {
                case ClashStatusKind.Pending: return MarkerStatus.Pending;
                case ClashStatusKind.Coordinated: return MarkerStatus.Coordinated;
                case ClashStatusKind.Ignored: return MarkerStatus.Ignored;
                default: return MarkerStatus.New;
            }
        }
    }
}
