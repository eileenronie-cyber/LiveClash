using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B1: the panel row's schedule columns, the status suffixes and the search text
    /// (spec 9, 6.7). Revit-free. Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class ScheduleRowChecks
    {
        private const string P = "schedule row: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Cells(check);
            Status(check);
            Pending(check);
            Columns(check);
            Search(check);
            Robust(check);
            Data(check);
            RowGroup(check);
            MarkerRouting(check);
            NoWaiting(check);
        }

        // ------------------------------------------------------------------ which keys go through the marker (B6)

        private static readonly string[] Cab = { "c", "a", "b", "a" };

        private static void MarkerRouting(Action<string, bool?> check)
        {
            var s = Many(true, "jdoe", Row("a", "JD-1"), Row("b", "JD-2"));
            s.Rows["c"] = new RowScheduleInfo { HasMarker = false, IssueNo = "JD-3" };
            var k = RowScheduleText.MarkerKeys(s, Cab);
            check("schedule row route: keys with a marker go through it, in order, once each; the rest (no row, no marker) stay on the ledger path", string.Join(",", k) == "a,b");
            var refused = Many(true, "jdoe", Row("a", "JD-1"));
            refused.RefusalReason = "This model is read-only.";
            check("schedule row route: nothing goes through the marker when the model refuses writes, when there is no snapshot, or no keys",
                RowScheduleText.MarkerKeys(refused, Ks("a")).Count == 0 && RowScheduleText.MarkerKeys(null, Ks("a")).Count == 0
                && RowScheduleText.MarkerKeys(s, null).Count == 0 && RowScheduleText.MarkerKeys(s, Ks("zzz", null)).Count == 0);
            var blank = Many(true, "jdoe", Row("a", "JD-1"));
            blank.RefusalReason = "   ";
            check("schedule row route: a blank refusal text is no refusal", RowScheduleText.MarkerKeys(blank, Ks("a")).Count == 1);
        }

        // ------------------------------------------------------------------ a row that holds several pairs (B4a)

        private const string G = "schedule row group: ";

        private static readonly string[] Ab = { "a", "b" };
        private static readonly string[] Ba = { "b", "a" };
        private static readonly string[] Abc = { "a", "b", "c" };
        private static readonly string[] Abcd = { "a", "b", "c", "d" };

        private static ScheduleSnapshot Many(bool workshared, string me, params KeyValuePair<string, RowScheduleInfo>[] rows)
        {
            var s = new ScheduleSnapshot { Me = me, Workshared = workshared, MarkerCount = rows.Length };
            foreach (var r in rows) s.Rows[r.Key] = r.Value;
            return s;
        }

        private static KeyValuePair<string, RowScheduleInfo> Row(string key, string issue, string assigned = null, string held = null, string decided = null, string group = null, string pendingStatus = null)
        {
            return new KeyValuePair<string, RowScheduleInfo>(key, new RowScheduleInfo
            {
                HasMarker = true, IssueNo = issue, AssignedTo = assigned, HeldBy = held, DecidedBy = decided, GroupLabel = group,
                PendingStatus = pendingStatus, PendingReason = pendingStatus == null ? null : "held by kdoe"
            });
        }

        private static void RowGroup(Action<string, bool?> check)
        {
            var one = Many(true, "jdoe", Row("a", "JD-0042", "asmith", "kdoe", "kdoe", "JD-0042 (+1)", "Ignored"));
            var f = RowScheduleText.For(one, "a"); var g = RowScheduleText.ForKeys(one, Ks("a"));
            check(G + "one key gives the same cells as the single-pair rule",
                g.IssueNo == f.IssueNo && g.Assigned == f.Assigned && g.HeldBy == f.HeldBy && g.DecidedBy == f.DecidedBy && g.Group == f.Group
                && g.PendingMark == f.PendingMark && g.PendingTooltip == f.PendingTooltip && g.IssueNo == "JD-0042" && g.Assigned == "asmith");

            var none = RowScheduleText.ForKeys(null, Ks("a"));
            var noKeys = RowScheduleText.ForKeys(one, null);
            var unknown = RowScheduleText.ForKeys(one, Ks("zzz"));
            check(G + "a null snapshot, null keys or keys with no marker give empty cells",
                none != null && noKeys != null && unknown != null && none.IssueNo == "" && noKeys.IssueNo == "" && unknown.IssueNo == "" && unknown.Assigned == "");

            var s3 = Many(true, "jdoe", Row("a", "JD-0010"), Row("b", "JD-0009"), Row("c", "KD-0001"));
            bool lowest = true;
            foreach (var perm in MarkerIdentityChecks.Permutations(Abc)) lowest &= RowScheduleText.ForKeys(s3, perm).IssueNo == "JD-0009";
            check(G + "the issue number is the lowest, by prefix then number, in every order of three keys", lowest);
            var wide = Many(true, "jdoe", Row("a", "JD-10000"), Row("b", "JD-9999"));
            check(G + "...numbers compare as numbers", RowScheduleText.ForKeys(wide, Ks("a", "b")).IssueNo == "JD-9999");
            var junk = Many(true, "jdoe", Row("a", "zeta"), Row("b", "alpha"), Row("c", "JD-0100"), Row("d", ""));
            check(G + "...a readable number beats an unreadable one, and unreadable ones go by ordinal text",
                RowScheduleText.ForKeys(junk, Ks("a", "b", "c", "d")).IssueNo == "JD-0100" && RowScheduleText.ForKeys(junk, Ks("a", "b", "d")).IssueNo == "alpha"
                && RowScheduleText.ForKeys(junk, Ks("d")).IssueNo == "");

            bool junkOrder = true;
            foreach (var perm in MarkerIdentityChecks.Permutations(Abcd)) junkOrder &= RowScheduleText.ForKeys(junk, perm).IssueNo == "JD-0100";
            check(G + "...in every order of four keys", junkOrder);

            // Branch coverage, added at green: members with the same (or no) issue number are told apart by their other
            // fields, so the "first" pending one and the shown spelling do not depend on the order of the keys.
            string Sig(RowScheduleCells c) { return string.Join("|", c.IssueNo, c.Assigned, c.HeldBy, c.Group, c.DecidedBy, c.PendingMark, c.PendingTooltip); }
            Func<ScheduleSnapshot, string[], bool> orderFree = (snap, ks) =>
            {
                var first = Sig(RowScheduleText.ForKeys(snap, ks));
                return MarkerIdentityChecks.Permutations(ks).All(p => Sig(RowScheduleText.ForKeys(snap, p)) == first);
            };
            var tieA = Many(true, "jdoe", Row("a", "", "b-user"), Row("b", "", "a-user"));
            var tieH = Many(true, "jdoe", Row("a", "JD-1", held: "zed"), Row("b", "JD-1", held: "amy"));
            var tieD = Many(true, "jdoe", Row("a", "JD-1", decided: "zed"), Row("b", "JD-1", decided: "Amy"));
            var tieS = Many(true, "jdoe", Row("a", "JD-1", pendingStatus: "Pending"), Row("b", "JD-1", pendingStatus: "Ignored"));
            var tieR = Many(true, "jdoe", Row("a", "JD-1", pendingStatus: "Ignored"), Row("b", "JD-1", pendingStatus: "Ignored"));
            tieR.Rows["a"].PendingReason = "zzz"; tieR.Rows["b"].PendingReason = "aaa";
            var tieEq = Many(true, "jdoe", Row("a", "JD-1", "x", "y", "z"), Row("b", "JD-1", "x", "y", "z"));
            check(G + "members with the same or no issue number are told apart by assigned, held by, decided by, pending status and reason, whatever the key order",
                orderFree(tieA, Ab) && orderFree(tieH, Ab) && orderFree(tieD, Ab)
                && orderFree(tieS, Ab) && orderFree(tieR, Ab) && orderFree(tieEq, Ab));
            check(G + "...the first pending one is the lowest by status, then by reason",
                RowScheduleText.ForKeys(tieS, Ab).PendingTooltip == "JD-1: Ignored (pending: held by kdoe) (and 1 more pending)"
                && RowScheduleText.ForKeys(tieR, Ab).PendingTooltip == "JD-1: Ignored (pending: aaa) (and 1 more pending)");
            check(G + "...and the shown spelling of a shared name is the first in that order", RowScheduleText.ForKeys(tieD, Ab).DecidedBy == "various"
                && RowScheduleText.ForKeys(Many(true, "jdoe", Row("a", "JD-1", decided: "Amy"), Row("b", "JD-1", decided: "amy")), Ba).DecidedBy == "Amy");

            var same = Many(true, "jdoe", Row("a", "JD-1", "asmith"), Row("b", "JD-2", "ASMITH"));
            var empties = Many(true, "jdoe", Row("a", "JD-1"), Row("b", "JD-2"));
            var mixed = Many(true, "jdoe", Row("a", "JD-1", "asmith"), Row("b", "JD-2", "kdoe"));
            var half = Many(true, "jdoe", Row("a", "JD-1", "asmith"), Row("b", "JD-2"));
            check(G + "assigned: a shared value (any capitals) is shown; all empty is empty",
                RowScheduleText.ForKeys(same, Ks("a", "b")).Assigned == "asmith" && RowScheduleText.ForKeys(empties, Ks("a", "b")).Assigned == "");
            check(G + "...different values, or one assigned and one not, read \"various\"",
                RowScheduleText.ForKeys(mixed, Ks("a", "b")).Assigned == "various" && RowScheduleText.ForKeys(half, Ks("a", "b")).Assigned == "various");

            var held1 = Many(true, "jdoe", Row("a", "JD-1", held: "kdoe"), Row("b", "JD-2", held: "KDOE"), Row("c", "JD-3"));
            var held2 = Many(true, "jdoe", Row("a", "JD-1", held: "kdoe"), Row("b", "JD-2", held: "asmith"));
            check(G + "held by: one holder is named (one member free is fine), two holders read \"various\", none is empty",
                RowScheduleText.ForKeys(held1, Ks("a", "b", "c")).HeldBy == "kdoe" && RowScheduleText.ForKeys(held2, Ks("a", "b")).HeldBy == "various"
                && RowScheduleText.ForKeys(held1, Ks("c")).HeldBy == "");
            var local = Many(false, "jdoe", Row("a", "JD-1", held: "kdoe"), Row("b", "JD-2", held: "kdoe"));
            check(G + "...nothing is held in a model that is not workshared", RowScheduleText.ForKeys(local, Ks("a", "b")).HeldBy == "");

            var dec = Many(true, "jdoe", Row("a", "JD-1", decided: "jdoe"), Row("b", "JD-2", decided: "kdoe"), Row("c", "JD-3", decided: "KDOE"));
            var dec2 = Many(true, "jdoe", Row("a", "JD-1", decided: "kdoe"), Row("b", "JD-2", decided: "asmith"));
            var own = Many(true, "jdoe", Row("a", "JD-1", decided: "jdoe"), Row("b", "JD-2", decided: "JDoe"));
            check(G + "decided by: my own is hidden, one other is named, two others read \"various\"",
                RowScheduleText.ForKeys(dec, Ks("a", "b", "c")).DecidedBy == "kdoe" && RowScheduleText.ForKeys(dec2, Ks("a", "b")).DecidedBy == "various"
                && RowScheduleText.ForKeys(own, Ks("a", "b")).DecidedBy == "");

            var grp = Many(true, "jdoe", Row("a", "JD-1"), Row("b", "JD-2", group: "JD-0001 (+1)"), Row("c", "JD-3", group: "JD-0001 (+1)"));
            check(G + "the group label is the first one given", RowScheduleText.ForKeys(grp, Ks("a", "b", "c")).Group == "JD-0001 (+1)");

            var pend = Many(true, "jdoe", Row("a", "JD-1"), Row("b", "JD-2", pendingStatus: "Ignored"), Row("c", "JD-3", pendingStatus: "Pending"));
            var p = RowScheduleText.ForKeys(pend, Ks("a", "b", "c"));
            check(G + "pending: the mark shows if any member is pending; the tooltip is the first one plus a count of the others",
                p.PendingMark == "pending" && p.PendingTooltip == "JD-2: Ignored (pending: held by kdoe) (and 1 more pending)");
            var p1 = RowScheduleText.ForKeys(pend, Ks("a", "b"));
            check(G + "...with one pending member there is no count", p1.PendingTooltip == "JD-2: Ignored (pending: held by kdoe)");
            check(G + "...and none pending gives no mark", RowScheduleText.ForKeys(pend, Ks("a")).PendingMark == "");

            var gap = Many(true, "jdoe", Row("a", "JD-1", "asmith"));
            check(G + "keys without a marker do not dilute the answer", RowScheduleText.ForKeys(gap, Ks("a", "nope", "nada")).Assigned == "asmith");

            var rich = Many(true, "jdoe", Row("a", "JD-0005", "asmith", "kdoe", "kdoe", "JD-0005 (+2)", "Ignored"), Row("b", "JD-0006", "kdoe", null, "asmith", "JD-0005 (+2)"), Row("c", "JD-0007", "asmith", "asmith", null));
            bool order = true;
            string first = Sig(RowScheduleText.ForKeys(rich, Ks("a", "b", "c")));
            foreach (var perm in MarkerIdentityChecks.Permutations(Abc)) order &= Sig(RowScheduleText.ForKeys(rich, perm)) == first;
            check(G + "the cells do not depend on the order of the keys (all 6 orders)", order && first.StartsWith("JD-0005|various|various|JD-0005 (+2)|various|pending|", StringComparison.Ordinal));

            var srch = Many(true, "jdoe", Row("a", "JD-0005", "asmith"), Row("b", "JD-0006", "kdoe"));
            srch.Rows["a"].MergedIssueNos.Add("KD-0003");
            var text = RowScheduleText.SearchTextOf(srch, Ks("a", "b", "nope"));
            check(G + "search text joins every member's numbers, merged numbers and assignees; unknown keys add nothing",
                text.Contains("JD-0005") && text.Contains("KD-0003") && text.Contains("asmith") && text.Contains("JD-0006") && text.Contains("kdoe")
                && RowScheduleText.SearchTextOf(srch, Ks("nope")) == "" && RowScheduleText.SearchTextOf(null, Ks("a")) == "" && RowScheduleText.SearchTextOf(srch, null) == "");

            check(G + "decided by others: true if any member's decision is someone else's",
                RowScheduleText.DecidedByOthersAny(dec, Ks("a", "b")) && RowScheduleText.DecidedByOthersAny(dec, Ks("c")));
            check(G + "...false for my own, for members without a marker, and for null",
                !RowScheduleText.DecidedByOthersAny(own, Ks("a", "b")) && !RowScheduleText.DecidedByOthersAny(dec, Ks("zzz"))
                && !RowScheduleText.DecidedByOthersAny(null, Ks("a")) && !RowScheduleText.DecidedByOthersAny(dec, null));
        }

        // The plain data the interface carries: defaults are the spec's numbers (30 days, 2,000 cap), lists are never null.
        private static void Data(Action<string, bool?> check)
        {
            var plan = new RefreshPlanView();
            check(P + "a new plan view has the spec's defaults: 30 days retention, a cap of 2,000, empty lists and no refusal",
                plan.RetentionDays == 30 && plan.Cap == 2000 && plan.PassedOver.Count == 0 && plan.NotRetested.Count == 0 && plan.ByStatus.Count == 0
                && plan.FirstCreateAdds.Count == 0 && plan.ExistingSchedulesShowing.Count == 0 && plan.Refusal == null && !plan.OverCap && !plan.OverWarn && !plan.NeedsDialog && !plan.IsCreate);

            plan.Refusal = "No."; plan.IsCreate = true; plan.NeedsDialog = true; plan.NonWorkshared = true; plan.Clashes = 9; plan.ExistingMarkers = 8;
            plan.Create = 1; plan.FirstIssueNo = "JD-0001"; plan.LastIssueNo = "JD-0001"; plan.Update = 2; plan.Move = 3; plan.Resolve = 4; plan.Merge = 5;
            plan.PendingApplied = 6; plan.Unchanged = 7; plan.RetentionCount = 10; plan.RetentionDays = 45; plan.OverCap = true; plan.WouldCreate = 2350;
            plan.Cap = 1000; plan.OverWarn = true; plan.TotalMarkers = 5001;
            check(P + "a plan view keeps every value it is given",
                plan.Refusal == "No." && plan.IsCreate && plan.NeedsDialog && plan.NonWorkshared && plan.Clashes == 9 && plan.ExistingMarkers == 8
                && plan.Create == 1 && plan.FirstIssueNo == "JD-0001" && plan.LastIssueNo == "JD-0001" && plan.Update == 2 && plan.Move == 3 && plan.Resolve == 4
                && plan.Merge == 5 && plan.PendingApplied == 6 && plan.Unchanged == 7 && plan.RetentionCount == 10 && plan.RetentionDays == 45 && plan.OverCap
                && plan.WouldCreate == 2350 && plan.Cap == 1000 && plan.OverWarn && plan.TotalMarkers == 5001);

            var ch = new RefreshChoices();
            check(P + "the choices start unticked (E9)", !ch.RemoveRetention && !ch.CreateCapOnly && !ch.ConfirmedNonWorkshared);
            ch.RemoveRetention = true; ch.CreateCapOnly = true; ch.ConfirmedNonWorkshared = true;
            check(P + "...and keep what is ticked", ch.RemoveRetention && ch.CreateCapOnly && ch.ConfirmedNonWorkshared);

            var outcome = new RefreshOutcome { Ok = true, Message = "m", Counts = new MarkerSummary.Counts { Clashes = 1 } };
            outcome.Sets.Add(new MarkerSummary.SetCounts { Name = "MEP vs STR" });
            check(P + "an outcome carries its counts and per-set lines", outcome.Ok && outcome.Message == "m" && outcome.Counts.Clashes == 1 && outcome.Sets.Count == 1);

            var good = ActionResult.Success("done");
            var bad = ActionResult.Fail("no");
            check(P + "action results say whether they worked", good.Ok && good.Message == "done" && !bad.Ok && bad.Message == "no");

            var cand = new AssignCandidate { Name = "asmith", Reason = "you" };
            var assigned = new AssignOutcome { Ok = true, Message = "ok", Assigned = 2, Pending = 1 };
            check(P + "assign candidates and outcomes keep their values", cand.Name == "asmith" && cand.Reason == "you" && assigned.Ok && assigned.Message == "ok" && assigned.Assigned == 2 && assigned.Pending == 1);

            var info = new RowScheduleInfo();
            var snap = new ScheduleSnapshot { Me = "jdoe", Workshared = true, MarkerCount = 1, OutOfDate = 2, ResolvedCount = 3, HasSchedule = true, ParametersPresent = true, RefusalReason = " " };
            snap.Rows["k"] = info;
            check(P + "a snapshot finds a row by key, not a missing or null key, and blank refusal text is no refusal",
                ReferenceEquals(snap.Row("k"), info) && snap.Row("x") == null && snap.Row(null) == null && !snap.Refused && snap.HasMarkers
                && !new ScheduleSnapshot().HasMarkers && snap.OutOfDate == 2 && snap.ResolvedCount == 3 && snap.HasSchedule && snap.ParametersPresent && snap.Me == "jdoe" && snap.Workshared);
            check(P + "a snapshot with no rows dictionary finds nothing", new ScheduleSnapshot { Rows = null }.Row("k") == null);
        }

        private static List<string> Ks(params string[] keys) { return keys.ToList(); }

        private static ScheduleSnapshot Snap(bool workshared, string me, string key, RowScheduleInfo info)
        {
            var s = new ScheduleSnapshot { Me = me, Workshared = workshared, MarkerCount = 5 };
            s.Rows[key] = info;
            return s;
        }

        private static RowScheduleInfo Full()
        {
            return new RowScheduleInfo
            {
                HasMarker = true, IssueNo = "JD-0042", AssignedTo = "asmith", HeldBy = "kdoe", GroupLabel = "JD-0042 (+13)", DecidedBy = "kdoe",
                MergedIssueNos = { "KD-0003" }
            };
        }

        private static void Cells(Action<string, bool?> check)
        {
            var empty = RowScheduleText.For(null, "k");
            check(P + "a null snapshot gives empty cells", empty != null && empty.IssueNo == "" && empty.Assigned == "" && empty.HeldBy == "" && empty.Group == "" && empty.DecidedBy == "" && empty.PendingMark == "");
            var s = Snap(true, "jdoe", "k1", Full());
            var unknown = RowScheduleText.For(s, "nope");
            check(P + "an unknown row gives empty cells", unknown.IssueNo == "" && unknown.Assigned == "" && unknown.HeldBy == "");
            var nomark = Snap(true, "jdoe", "k1", new RowScheduleInfo { HasMarker = false, IssueNo = "JD-0001", AssignedTo = "asmith" });
            check(P + "a row whose HasMarker is false gives empty cells whatever else is set", RowScheduleText.For(nomark, "k1").IssueNo == "" && RowScheduleText.For(nomark, "k1").Assigned == "");

            var c = RowScheduleText.For(Snap(true, "jdoe", "k1", new RowScheduleInfo { HasMarker = true, IssueNo = " JD-0042 ", AssignedTo = " asmith ", GroupLabel = " JD-0042 (+13) " }), "k1");
            check(P + "issue number, assigned and group label pass through, trimmed", c.IssueNo == "JD-0042" && c.Assigned == "asmith" && c.Group == "JD-0042 (+13)");

            check(P + "held by is shown in a workshared model", RowScheduleText.For(s, "k1").HeldBy == "kdoe");
            var local = Snap(false, "jdoe", "k1", Full());
            check(P + "...and is empty when the model is not workshared", RowScheduleText.For(local, "k1").HeldBy == "");

            check(P + "decided by someone else is shown (\"kdoe\")", RowScheduleText.For(s, "k1").DecidedBy == "kdoe");
            var own = Snap(true, "JDOE", "k1", new RowScheduleInfo { HasMarker = true, DecidedBy = "jdoe" });
            check(P + "...but not my own decision, whatever the capitals", RowScheduleText.For(own, "k1").DecidedBy == "");
            var nobody = Snap(true, "jdoe", "k1", new RowScheduleInfo { HasMarker = true, DecidedBy = "  " });
            check(P + "...nor an empty one", RowScheduleText.For(nobody, "k1").DecidedBy == "");
            var noMe = Snap(true, null, "k1", new RowScheduleInfo { HasMarker = true, DecidedBy = "kdoe" });
            check(P + "...and with no known user every decider is someone else", RowScheduleText.For(noMe, "k1").DecidedBy == "kdoe");
        }

        private static void Status(Action<string, bool?> check)
        {
            var plain = new RowScheduleCells();
            var by = new RowScheduleCells { DecidedBy = "jdoe" };
            var pend = new RowScheduleCells { PendingMark = "pending" };
            var both = new RowScheduleCells { DecidedBy = "jdoe", PendingMark = "pending" };
            check(P + "status text with nothing to add is the status", RowScheduleText.StatusText("Ignored", plain) == "Ignored");
            check(P + "...\"Ignored by jdoe\"", RowScheduleText.StatusText("Ignored", by) == "Ignored by jdoe");
            check(P + "...\"Ignored (pending)\"", RowScheduleText.StatusText("Ignored", pend) == "Ignored (pending)");
            check(P + "...\"Ignored by jdoe (pending)\"", RowScheduleText.StatusText("Ignored", both) == "Ignored by jdoe (pending)");
            check(P + "an empty status keeps the suffix without a leading space", RowScheduleText.StatusText("", by) == "by jdoe" && RowScheduleText.StatusText(null, pend) == "(pending)");
            check(P + "null cells give the status back, null status gives empty", RowScheduleText.StatusText("Ignored", null) == "Ignored" && RowScheduleText.StatusText(null, null) == "");
        }

        private static void Pending(Action<string, bool?> check)
        {
            var info = new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0042", PendingStatus = "Ignored", PendingReason = "held by kdoe until they synchronize" };
            var c = RowScheduleText.For(Snap(true, "jdoe", "k", info), "k");
            check(P + "a pending triage shows the mark \"pending\"", c.PendingMark == "pending");
            check(P + "...and the tooltip \"JD-0042: Ignored (pending: held by kdoe until they synchronize)\"",
                c.PendingTooltip == "JD-0042: Ignored (pending: held by kdoe until they synchronize)");
            var noNo = RowScheduleText.For(Snap(true, "jdoe", "k", new RowScheduleInfo { HasMarker = true, PendingStatus = "Pending", PendingReason = "changed in central" }), "k");
            check(P + "the tooltip without an issue number drops the prefix", noNo.PendingTooltip == "Pending (pending: changed in central)");
            var noWhy = RowScheduleText.For(Snap(true, "jdoe", "k", new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0042", PendingStatus = "Ignored" }), "k");
            check(P + "the tooltip without a reason reads \"(pending)\"", noWhy.PendingTooltip == "JD-0042: Ignored (pending)");
            var none = RowScheduleText.For(Snap(true, "jdoe", "k", new RowScheduleInfo { HasMarker = true, IssueNo = "JD-0042", PendingReason = "held" }), "k");
            check(P + "no pending status means no mark and no tooltip", none.PendingMark == "" && none.PendingTooltip == "");
        }

        private static void Columns(Action<string, bool?> check)
        {
            var none = RowScheduleText.Columns(new ScheduleSnapshot { Workshared = true });
            check(P + "no markers: no schedule column has a width", none != null && !none.IssueNo && !none.Assigned && !none.HeldBy && !none.Group);
            var nul = RowScheduleText.Columns(null);
            check(P + "a null snapshot: none either", nul != null && !nul.IssueNo && !nul.Assigned && !nul.HeldBy && !nul.Group);
            var some = RowScheduleText.Columns(new ScheduleSnapshot { Workshared = true, MarkerCount = 3 });
            check(P + "markers in a workshared model: issue no, assigned, held by and group", some.IssueNo && some.Assigned && some.HeldBy && some.Group);
            var local = RowScheduleText.Columns(new ScheduleSnapshot { Workshared = false, MarkerCount = 3 });
            check(P + "markers in a model that is not workshared: no held by", local.IssueNo && local.Assigned && local.Group && !local.HeldBy);
        }

        private static void Search(Action<string, bool?> check)
        {
            var s = Snap(true, "jdoe", "k", Full());
            var text = RowScheduleText.SearchText(s, "k");
            check(P + "search text holds the issue number, the merged numbers and the assigned user", text.Contains("JD-0042") && text.Contains("KD-0003") && text.Contains("asmith"));
            check(P + "search text is empty for a row without a marker", RowScheduleText.SearchText(s, "other") == "" && RowScheduleText.SearchText(null, "k") == "");
            check(P + "search finds the row by its number, a merged number or the assignee, in any capitals",
                ClashSearch.Matches("JD-0042", new[] { text }) && ClashSearch.Matches("kd-0003", new[] { text }) && ClashSearch.Matches("ASMITH", new[] { text }));
            check(P + "...and not by a number it does not have", !ClashSearch.Matches("JD-0099", new[] { text }));

            check(P + "decided by others: true when the decider is not me", RowScheduleText.DecidedByOthers(s, "k"));
            var own = Snap(true, "kdoe", "k", Full());
            check(P + "...false when the decider is me, empty, or the row has no marker",
                !RowScheduleText.DecidedByOthers(own, "k")
                && !RowScheduleText.DecidedByOthers(Snap(true, "jdoe", "k", new RowScheduleInfo { HasMarker = true }), "k")
                && !RowScheduleText.DecidedByOthers(s, "other") && !RowScheduleText.DecidedByOthers(null, "k"));
        }

        private static void Robust(Action<string, bool?> check)
        {
            bool ok = true;
            try
            {
                var info = new RowScheduleInfo { HasMarker = true, IssueNo = null, AssignedTo = null, HeldBy = null, GroupLabel = null, DecidedBy = null, PendingStatus = "Ignored", PendingReason = null, MergedIssueNos = null };
                var s = Snap(true, null, "k", info);
                var c = RowScheduleText.For(s, "k");
                ok &= c != null && c.IssueNo != null && c.Assigned != null && c.HeldBy != null && c.Group != null && c.DecidedBy != null && c.PendingMark != null && c.PendingTooltip != null;
                ok &= RowScheduleText.SearchText(s, "k") != null && RowScheduleText.StatusText("x", c) != null;
                ok &= RowScheduleText.For(new ScheduleSnapshot { Rows = null }, "k") != null && RowScheduleText.For(s, null) != null;
                ok &= RowScheduleText.Columns(new ScheduleSnapshot { Rows = null }) != null;
            }
            catch (Exception) { ok = false; }
            check(P + "null fields everywhere: no throw, and no cell is ever null", ok);
        }

        // The UI never waits on a Task (TESTINGS condition 1). Skipped where the sources are not on disk (in Revit).
        private static void NoWaiting(Action<string, bool?> check)
        {
            string root = FindRoot();
            if (root == null) { check(P + "no .Wait, .Result or GetResult in the schedule UI sources (runs headless only: needs the source tree)", null); return; }

            var files = new List<string>();
            foreach (var dir in Ks("Core", "UI"))
            {
                var d = Path.Combine(root, dir);
                if (!Directory.Exists(d)) continue;
                files.AddRange(Directory.GetFiles(d, "*Schedule*.cs"));
                files.AddRange(Directory.GetFiles(d, "RowScheduleText.cs"));
                files.AddRange(Directory.GetFiles(d, "RefreshPlan*.cs"));
                files.AddRange(Directory.GetFiles(d, "Assign*.cs"));
                files.AddRange(Directory.GetFiles(d, "ConfirmModel.cs"));
            }
            var bad = new List<string>();
            foreach (var f in files.Distinct())
            {
                var t = File.ReadAllText(f);
                if (t.Contains(".Wait(") || System.Text.RegularExpressions.Regex.IsMatch(t, @"\.Result\b(?!\s*[(\w])") || t.Contains("GetAwaiter().GetResult()")) bad.Add(Path.GetFileName(f));
            }
            check(P + "no .Wait, .Result or GetResult in the schedule UI sources (" + files.Distinct().Count() + " files)" + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""), bad.Count == 0);
        }

        private static string FindRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "LiveClash.csproj")) && Directory.Exists(Path.Combine(dir.FullName, "Core"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
