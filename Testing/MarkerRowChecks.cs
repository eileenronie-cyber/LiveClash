using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 3d: the builder from a clash, the ledger and a re-test to the plan's row (spec 4, 6.1,
    /// 6.4). Revit-free, so both tiers run it. The group labels the plan computes from it are in MarkerPlanChecks.
    /// Red-first: tests/evidence/clash-schedule/BUILD-LOG.md.
    /// </summary>
    public static class MarkerRowChecks
    {
        private const string P = "markers row: ";
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            Shown(check);
            Fields(check);
            Verdicts(check);
            Decisions(check);
            Edges(check);
        }

        private static RowBuilder.Source Src()
        {
            return new RowBuilder.Source
            {
                Key = "11|0|22", HostUid = "hu", LinkUid = "lu", OtherUid = "ou", HostId = 11, LinkId = 33, OtherId = 22, Severity = "Clearance",
                HasPoint = true, X = 1.5, Y = 2.5, Z = 3.5, SetName = "MEP vs STR", AlsoSets = new List<string> { "B", "A" }, GroupKey = "g1",
                HostName = "Pipe: Steel 100 [11]", HostCategory = "Pipes", OtherName = "Beam: W200 [22]", OtherCategory = "Structural Framing", ModelName = "PRJ01-STR", Level = "Level 2",
                RuleClearanceMm = 50, RuleInsulated = true
            };
        }

        private static string[] Names(params string[] items)
        {
            return items;
        }

        private static void Shown(Action<string, bool?> check)
        {
            var shown = RowBuilder.ShownOf(Src());
            check(P + "the display values have the schedule's eight parameter names with the host as A and the other element as B (spec 4)",
                shown.Count == 8 && shown["LC A Id"] == "11" && shown["LC A Name"] == "Pipe: Steel 100 [11]" && shown["LC A Category"] == "Pipes" && shown["LC B Id"] == "22"
                && shown["LC B Name"] == "Beam: W200 [22]" && shown["LC B Category"] == "Structural Framing" && shown["LC B Model"] == "PRJ01-STR" && shown["LC Level"] == "Level 2");
            var bare = RowBuilder.ShownOf(new RowBuilder.Source { HostId = 9007199254740993L });
            check(P + "missing texts are empty and a 64-bit id is written in full", bare.Count == 8 && bare["LC A Name"] == "" && bare["LC Level"] == "" && bare["LC A Id"] == "9007199254740993");

            var m1 = new Dictionary<string, string> { { "b", "2" }, { "a", "1" } };
            var m2 = new Dictionary<string, string> { { "a", "1" }, { "b", "2" } };
            check(P + "the hash is the short hash of the sorted key=value lines, so the order they were added in does not matter",
                RowBuilder.HashOf(m1) == RowBuilder.HashOf(m2) && RowBuilder.HashOf(m1) == ShortHash.Of("a=1\nb=2"));
            m2["b"] = "3";
            var m3 = new Dictionary<string, string> { { "a", "1" }, { "b", "2" }, { "c", "" } };
            check(P + "any different value, or an extra key, gives a different hash", RowBuilder.HashOf(m1) != RowBuilder.HashOf(m2) && RowBuilder.HashOf(m1) != RowBuilder.HashOf(m3));
            check(P + "no map, or an empty one, hashes to the empty text", RowBuilder.HashOf(null) == "" && RowBuilder.HashOf(new Dictionary<string, string>()) == "");
            var a = RowBuilder.ShownOf(Src());
            var b = RowBuilder.ShownOf(Src());
            check(P + "the same clash always gives the same hash, and a changed name or level changes it",
                RowBuilder.HashOf(a) == RowBuilder.HashOf(b) && RowBuilder.HashOf(a) != "" && RowBuilder.HashOf(a) != RowBuilder.HashOf(RowBuilder.ShownOf(new RowBuilder.Source
                {
                    Key = "11|0|22", HostId = 11, OtherId = 22, HostName = "Pipe: Steel 100 [11]", HostCategory = "Pipes", OtherName = "Beam: W200 [22]", OtherCategory = "Structural Framing", ModelName = "PRJ01-STR", Level = "Level 3"
                })));
            check(P + "null values in a map hash as empty values", NoThrow(() => RowBuilder.HashOf(new Dictionary<string, string> { { "a", null } })) && RowBuilder.HashOf(new Dictionary<string, string> { { "a", null } }) == ShortHash.Of("a="));
        }

        private static void Fields(Action<string, bool?> check)
        {
            var src = Src();
            var row = RowBuilder.Build(src, null, null, true);
            check(P + "a row carries the clash's key, UniqueIds, ids, severity and point",
                row.Key == "11|0|22" && row.HostUid == "hu" && row.LinkUid == "lu" && row.OtherUid == "ou" && row.HostId == 11 && row.LinkId == 33 && row.OtherId == 22
                && row.Severity == "Clearance" && row.HasPoint && row.X == 1.5 && row.Y == 2.5 && row.Z == 3.5 && row.Live);
            check(P + "...its set, a sorted COPY of the also-caught-by sets, and the group key",
                row.SetName == "MEP vs STR" && row.AlsoSets.SequenceEqual(Names("A", "B")) && !ReferenceEquals(row.AlsoSets, src.AlsoSets) && src.AlsoSets[0] == "B" && row.GroupKey == "g1");
            var shown = RowBuilder.ShownOf(src);
            check(P + "...and its display values with the hash of them", row.Shown != null && row.Shown.Count == 8 && row.Shown["LC Level"] == "Level 2" && row.ShownHash == RowBuilder.HashOf(shown));
        }

        private static void Verdicts(Action<string, bool?> check)
        {
            var src = Src();
            var live = RowBuilder.Build(src, null, null, true);
            check(P + "a live pair with no re-test is Clashing under the rule this seat's settings imply (a new marker records that rule)",
                live.Verdict == PlanVerdict.Clashing && live.RuleClearanceMm == 50 && live.RuleInsulated);
            var off = RowBuilder.Build(src, null, null, false);
            check(P + "a pair not live on this seat is NotRetested \"not live on this seat\" and not Live", off.Verdict == PlanVerdict.NotRetested && off.NotRetestedReason == "not live on this seat" && !off.Live);

            var gone = RowBuilder.Build(src, new RowBuilder.Retest { Verdict = PlanVerdict.NoLongerClashes, ClearanceMm = 100, Insulated = false }, null, true);
            check(P + "a re-test's verdict and the rule it was run under win over the settings' rule",
                gone.Verdict == PlanVerdict.NoLongerClashes && gone.RuleClearanceMm == 100 && !gone.RuleInsulated);
            var link = RowBuilder.Build(src, new RowBuilder.Retest { Verdict = PlanVerdict.NotRetested, Reason = "link not loaded", ClearanceMm = 100 }, null, true);
            check(P + "...and a re-test that could not be made names why", link.Verdict == PlanVerdict.NotRetested && link.NotRetestedReason == "link not loaded");

            var unverified = Src(); unverified.Severity = "Unverified";
            var u = RowBuilder.Build(unverified, null, null, true);
            check(P + "an Unverified pair with no marker stays Clashing, so an unmeasurable pair is still listed (D35)", u.Verdict == PlanVerdict.Clashing && u.Severity == "Unverified");
        }

        private static void Decisions(Action<string, bool?> check)
        {
            Func<ClashStatusKind, string, ClashStatusEntry> entry = (k, note) => new ClashStatusEntry { Kind = k, Note = note, By = "kdoe", WhenUtc = T0 };
            var map = new[] { ClashStatusKind.New, ClashStatusKind.Pending, ClashStatusKind.Coordinated, ClashStatusKind.Ignored }.Select(RowBuilder.StatusOf).ToArray();
            check(P + "ledger statuses map to marker statuses: New, Pending, Coordinated, Ignored", map.SequenceEqual(new[] { MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored }));

            var d = RowBuilder.DecisionOf(entry(ClashStatusKind.Ignored, "n"), new LedgerExtras.AssignedEntry { Assignee = "asmith", By = "x", WhenUtc = T0 }, new LedgerExtras.IssueEntry { IssueNo = "JD-0007", WhenUtc = T0 });
            check(P + "a decision is read from the ledger entry, the assignee and the issue number",
                d.HasStatus && d.Status == MarkerStatus.Ignored && d.Note == "n" && d.By == "kdoe" && d.WhenUtc == T0 && !d.Gone && d.AssignedTo == "asmith" && d.IssueNo == "JD-0007");
            var none = RowBuilder.DecisionOf(null, null, null);
            check(P + "no entries is no decision", !none.HasStatus && !none.Gone && none.AssignedTo == "" && none.IssueNo == "" && none.Note == "");
            var gone = RowBuilder.DecisionOf(new ClashStatusEntry { Kind = ClashStatusKind.Pending, Gone = true, WhenUtc = T0 }, null, null);
            check(P + "a Gone entry is marked Gone", gone.Gone && gone.HasStatus);

            var hollow = RowBuilder.DecisionOf(new ClashStatusEntry { Kind = ClashStatusKind.Coordinated, Note = null, By = null, WhenUtc = T0 }, new LedgerExtras.AssignedEntry { Assignee = null }, new LedgerExtras.IssueEntry { IssueNo = null });
            check(P + "a ledger entry with no note or author, an assignee entry with no name and an issue entry with no number read as empty",
                hollow.Status == MarkerStatus.Coordinated && hollow.Note == "" && hollow.By == "" && hollow.AssignedTo == "" && hollow.IssueNo == "");

            var seeded = RowBuilder.Build(Src(), null, d, true);
            check(P + "a standing decision becomes the row's seed: status, note, assignee, author and time; the issue number is the remembered one",
                seeded.Seed != null && seeded.Seed.Status == MarkerStatus.Ignored && seeded.Seed.Note == "n" && seeded.Seed.AssignedTo == "asmith" && seeded.Seed.By == "kdoe"
                && seeded.Seed.At == new DateTimeOffset(T0) && seeded.RememberedIssueNo == "JD-0007");
            var goneRow = RowBuilder.Build(Src(), null, new RowBuilder.Decision { HasStatus = true, Status = MarkerStatus.Pending, Gone = true, AssignedTo = "asmith", IssueNo = "JD-0003" }, true);
            check(P + "a Gone decision gives no seed (the spec makes markers for standing decisions), but the issue number is still remembered (D36)", goneRow.Seed == null && goneRow.RememberedIssueNo == "JD-0003");
            var onlyAssignee = RowBuilder.Build(Src(), null, new RowBuilder.Decision { AssignedTo = "asmith" }, true);
            check(P + "an assignee alone is a seed (status New)", onlyAssignee.Seed != null && onlyAssignee.Seed.Status == MarkerStatus.New && onlyAssignee.Seed.AssignedTo == "asmith");
            var empty = RowBuilder.Build(Src(), null, new RowBuilder.Decision(), true);
            check(P + "an empty decision gives no seed and no remembered number", empty.Seed == null && string.IsNullOrEmpty(empty.RememberedIssueNo));
        }

        private static void Edges(Action<string, bool?> check)
        {
            var live = RowBuilder.Build(new RowBuilder.Source { Key = "k" }, null, null, true);
            var off = RowBuilder.Build(new RowBuilder.Source { Key = "k", AlsoSets = new List<string> { "A" }, GroupKey = "g" }, null, null, false);
            check(P + "a live row's also-sets and group are computed (empty), a row not live on this seat leaves them not computed (null) so it never rewrites them (D37)",
                live.AlsoSets != null && live.AlsoSets.Count == 0 && live.GroupKey == "" && off.AlsoSets == null && off.GroupKey == null);
            check(P + "no source, or a source of nulls, never throws", NoThrow(() =>
            {
                RowBuilder.Build(null, null, null, true);
                RowBuilder.Build(new RowBuilder.Source(), null, new RowBuilder.Decision { Note = null, By = null }, true);
                RowBuilder.ShownOf(null);
            }));
            var nullSource = RowBuilder.Build(null, null, null, true);
            check(P + "no source gives a row that cannot create or change a marker", nullSource.Verdict == PlanVerdict.NotRetested && !nullSource.Live && string.IsNullOrEmpty(nullSource.HostUid));
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }
    }
}
