using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 2: what the plan says about ONE marker, field by field (spec 6.2): which fields it names, the flags it sets,
    /// why it passes a marker over, when it offers retention, what it tells the user about a pending triage, and the numbers it counts.
    /// Revit-free, so both tiers run it. Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationPlan2Checks
    {
        private const string P = "markers mutation plan 2: ";
        private static readonly DateTimeOffset Today = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        public static void RunAll(Action<string, bool?> check)
        {
            Fields(check);
            Boundaries(check);
            Verdicts(check);
            PassedOver(check);
            Retention(check);
            PendingMessages(check);
            Counting(check);
            Merge(check);
        }

        private static string[] Texts(params string[] items) { return items; }

        private static MarkerRecord Rec(int i)
        {
            return new MarkerRecord
            {
                Key = "k" + i, HostUid = "h" + i, LinkUid = "", OtherUid = "o" + i, HostId = 100 + i, OtherId = 200 + i, ClashId = ClashIdOf.Of("h" + i, "", "o" + i), IssueNo = "JD-000" + i,
                Status = MarkerStatus.New, ReportedBy = "jdoe", ReportedAt = Today.AddDays(-3), Severity = "Hard", ClearanceMm = 25, SetName = "Set A", X = i, Seq = 4,
                LastCheckedAt = Today.AddDays(-1), LastCheckedBy = "kdoe"
            };
        }

        private static MarkerPlan.Existing Ex(int i, MarkerRecord rec, MarkerAccess access)
        {
            return new MarkerPlan.Existing { MarkerUid = "m" + i, Record = rec, Access = access, HeldBy = "kdoe", ShownHash = "H" };
        }

        private static MarkerPlan.Existing Ex(int i) { return Ex(i, Rec(i), MarkerAccess.Free); }

        private static MarkerPlan.Row Row(int i)
        {
            return new MarkerPlan.Row
            {
                Key = "k" + i, HostUid = "h" + i, LinkUid = "", OtherUid = "o" + i, HostId = 100 + i, OtherId = 200 + i, Live = true, Verdict = PlanVerdict.Clashing, RuleClearanceMm = 25,
                Severity = "Hard", HasPoint = true, X = i, SetName = "Set A", ShownHash = "H", Shown = new Dictionary<string, string> { { "LC Level", "L1" } }
            };
        }

        private static MarkerPlan.Result Plan(MarkerPlan.Existing marker, MarkerPlan.Row row)
        {
            return Plan(marker, row, null);
        }

        private static MarkerPlan.Result Plan(MarkerPlan.Existing marker, MarkerPlan.Row row, Action<MarkerPlan.Input> more)
        {
            var input = new MarkerPlan.Input { Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" } };
            if (marker != null) input.Markers.Add(marker);
            if (row != null) input.Rows.Add(row);
            if (more != null) more(input);
            return MarkerPlan.Build(input);
        }

        private static MarkerPlan.Row Edited(Action<MarkerPlan.Row> edit)
        {
            var r = Row(1);
            edit(r);
            return r;
        }

        private static bool Only(MarkerPlan.Result r, params string[] fields)
        {
            return r.Changes.Count == 1 && r.Changes[0].Fields.SequenceEqual(fields);
        }

        private static bool None(MarkerPlan.Result r)
        {
            return r.Changes.Count == 0 && r.Unchanged == 1;
        }

        // ------------------------------------------------------------------ which fields a change names

        private static void Fields(Action<string, bool?> check)
        {
            var sev = Plan(Ex(1), Edited(r => r.Severity = "Clearance"));
            check(P + "a different severity names Severity and writes it; an empty severity changes nothing", Only(sev, "Severity") && sev.Changes[0].After.Severity == "Clearance" && None(Plan(Ex(1), Edited(r => r.Severity = ""))));

            var moved = Plan(Ex(1), Edited(r => { r.X = 1.01; r.Y = 2; r.Z = 3; }));
            check(P + "a moved point names Point, sets Moved and writes the three coordinates; no position changes nothing",
                Only(moved, "Point") && moved.Changes[0].Moved && moved.Changes[0].After.X == 1.01 && moved.Changes[0].After.Y == 2 && moved.Changes[0].After.Z == 3 && None(Plan(Ex(1), Edited(r => { r.X = 9; r.HasPoint = false; }))));

            var set = Plan(Ex(1), Edited(r => r.SetName = "Set B"));
            check(P + "a different set names SetName; an empty set changes nothing", Only(set, "SetName") && set.Changes[0].After.SetName == "Set B" && None(Plan(Ex(1), Edited(r => r.SetName = ""))));

            var also = Plan(Ex(1), Edited(r => r.AlsoSets = new List<string> { "b", "a", "b" }));
            var rec = Rec(1);
            rec.AlsoSets = new List<string> { "a", "b" };
            check(P + "also-sets are stored sorted and distinct, named only when the set differs (not the order); an unknown list changes nothing",
                Only(also, "AlsoSets") && also.Changes[0].After.AlsoSets.SequenceEqual(Texts("a", "b")) && None(Plan(Ex(1, rec, MarkerAccess.Free), Edited(r => r.AlsoSets = new List<string> { "b", "a" })))
                && None(Plan(Ex(1), Edited(r => r.AlsoSets = null))));

            var host = Plan(Ex(1), Edited(r => r.HostId = 555));
            var link = Plan(Ex(1), Edited(r => r.LinkId = 556));
            var other = Plan(Ex(1), Edited(r => r.OtherId = 557));
            var key = Plan(Ex(1), Edited(r => r.Key = "newkey"));
            check(P + "a different element id or key names HostId, LinkId, OtherId or Key, writes it and sets Rekeyed",
                Only(host, "HostId") && host.Changes[0].Rekeyed && host.Changes[0].After.HostId == 555 && Only(link, "LinkId") && link.Changes[0].Rekeyed && link.Changes[0].After.LinkId == 556
                && Only(other, "OtherId") && other.Changes[0].Rekeyed && other.Changes[0].After.OtherId == 557 && Only(key, "Key") && key.Changes[0].Rekeyed && key.Changes[0].After.Key == "newkey");
            check(P + "an id of 0 or an empty key is 'not known' and changes nothing, and is not a rekey", None(Plan(Ex(1), Edited(r => { r.HostId = 0; r.OtherId = 0; r.LinkId = 0; r.Key = ""; }))));

            var shown = Plan(Ex(1), Edited(r => r.ShownHash = "H2"));
            var shownNull = Plan(Ex(1), Edited(r => r.ShownHash = null));
            check(P + "a different hash of the display values names Parameters and carries the new hash; a row with no hash changes nothing and carries none",
                Only(shown, "Parameters") && shown.Changes[0].ShownHash == "H2" && shown.Changes[0].Shown != null && None(shownNull) && None(Plan(Ex(1), Row(1))));

            var off = Ex(1);
            off.OffPlace = true;
            var ws = Ex(1);
            ws.WrongWorkset = true;
            var offPlan = Plan(off, Row(1));
            var wsPlan = Plan(ws, Row(1));
            check(P + "a marker off its point names Location and sets PutBack; one on the wrong workset names Workset and sets WorksetFix",
                Only(offPlan, "Location") && offPlan.Changes[0].PutBack && !offPlan.Changes[0].Moved && Only(wsPlan, "Workset") && wsPlan.Changes[0].WorksetFix && !wsPlan.Changes[0].PutBack);

            var written = Plan(Ex(1), Edited(r => r.Severity = "Clearance"));
            var after = written.Changes.Count == 1 ? written.Changes[0].After : null;
            check(P + "a written change bumps Seq by one and stamps Last checked with today and the user; the record before is kept as it was",
                after != null && after.Seq == 5 && after.LastCheckedAt == Today && after.LastCheckedBy == "jdoe" && written.Changes[0].Before.Seq == 4 && written.Changes[0].Before.LastCheckedBy == "kdoe"
                && written.Changes[0].StatusOrder == 1 && written.Changes[0].Shown != null && written.Changes[0].Shown["LC Level"] == "L1");
        }

        // ------------------------------------------------------------------ exactly at the tolerance

        private static void Boundaries(Action<string, bool?> check)
        {
            var at = Rec(1);
            at.X = 0; at.Y = 0; at.Z = 0;
            Func<Action<MarkerPlan.Row>, MarkerPlan.Result> run = e => Plan(Ex(1, Clone(at), MarkerAccess.Free), Edited(r => { r.X = 0; e(r); }));
            check(P + "a coordinate that differs by exactly the point tolerance (0.001) is not a move, on each axis; a hair more is",
                None(run(r => r.X = MarkerPlan.PointTolerance)) && None(run(r => r.Y = MarkerPlan.PointTolerance)) && None(run(r => r.Z = MarkerPlan.PointTolerance))
                && Only(run(r => r.X = 0.0011), "Point") && Only(run(r => r.Y = 0.0011), "Point") && Only(run(r => r.Z = 0.0011), "Point")
                && Only(run(r => r.X = -0.0011), "Point"));
        }

        private static MarkerRecord Clone(MarkerRecord s)
        {
            var d = Rec(1);
            d.Key = s.Key; d.X = s.X; d.Y = s.Y; d.Z = s.Z; d.ClearanceMm = s.ClearanceMm; d.Status = s.Status;
            return d;
        }

        // ------------------------------------------------------------------ the re-test's verdict

        private static void Verdicts(Action<string, bool?> check)
        {
            var resolved = Plan(Ex(1), Edited(r => r.Verdict = PlanVerdict.NoLongerClashes));
            var rc = resolved.Changes.Count == 1 ? resolved.Changes[0] : null;
            check(P + "a pair that no longer clashes resolves its marker: Status named, Resolved set, resolved by/at and changed by/at stamped",
                rc != null && rc.Fields.SequenceEqual(Texts("Status")) && rc.Resolved && !rc.Reopened && rc.After.Status == MarkerStatus.Resolved && rc.After.ResolvedBy == "jdoe" && rc.After.ResolvedAt == Today
                && rc.After.ChangedBy == "jdoe" && rc.After.ChangedAt == Today && rc.StatusOrder == 6 && resolved.ToCounts().Resolved == 1 && resolved.ToCounts().Updated == 0);

            var done = Rec(1);
            done.Status = MarkerStatus.Resolved; done.ResolvedAt = Today.AddDays(-2); done.ResolvedBy = "kdoe";
            check(P + "an already resolved marker whose pair still does not clash is left alone", None(Plan(Ex(1, done, MarkerAccess.Free), Edited(r => r.Verdict = PlanVerdict.NoLongerClashes))));

            var back = Plan(Ex(1, CloneResolved(), MarkerAccess.Free), Row(1));
            var bc = back.Changes.Count == 1 ? back.Changes[0] : null;
            check(P + "a resolved marker whose pair clashes again is reopened: Status named, Reopened set, the resolution cleared, changed by/at stamped",
                bc != null && bc.Fields.SequenceEqual(Texts("Status")) && bc.Reopened && !bc.Resolved && bc.After.Status == MarkerStatus.Reopened && bc.After.ResolvedBy == "" && bc.After.ResolvedAt == null
                && bc.After.ChangedBy == "jdoe" && bc.After.ChangedAt == Today && bc.StatusOrder == 3 && back.ToCounts().Updated == 1);

            var named = Plan(Ex(1), Edited(r => { r.Verdict = PlanVerdict.NotRetested; r.NotRetestedReason = "link not loaded"; r.Severity = "Clearance"; }));
            var unnamed = Plan(Ex(1), Edited(r => { r.Verdict = PlanVerdict.NotRetested; r.NotRetestedReason = ""; }));
            check(P + "a pair that was not re-tested writes nothing and names its reason, or 'not re-tested' when there is none",
                named.Changes.Count == 0 && named.NotRetested.Count == 1 && named.NotRetested[0].Reason == "link not loaded" && named.NotRetested[0].MarkerUid == "m1" && named.NotRetested[0].Key == "k1"
                && unnamed.NotRetested.Count == 1 && unnamed.NotRetested[0].Reason == "not re-tested" && unnamed.Unchanged == 0);

            var zero = Rec(1);
            zero.ClearanceMm = 0;
            var tiny = Plan(Ex(1, zero, MarkerAccess.Free), Edited(r => r.RuleClearanceMm = 1e-6));
            var same = Plan(Ex(1, zero, MarkerAccess.Free), Edited(r => r.RuleClearanceMm = 5e-7));
            var insulated = Plan(Ex(1), Edited(r => r.RuleInsulated = true));
            check(P + "a re-test under another rule is not trusted: 'rule differs' at exactly 1e-6 of clearance or when the insulation differs, trusted below it",
                tiny.NotRetested.Count == 1 && tiny.NotRetested[0].Reason == "rule differs" && same.NotRetested.Count == 0 && same.Unchanged == 1 && insulated.NotRetested.Count == 1 && insulated.NotRetested[0].Reason == "rule differs");

            var notLive = Plan(Ex(1), Edited(r => { r.Live = false; r.Severity = "Clearance"; r.Verdict = PlanVerdict.NoLongerClashes; }));
            check(P + "a row that is not live on this seat neither judges nor updates the marker", notLive.Changes.Count == 0 && notLive.NotRetested.Count == 0 && notLive.Unchanged == 1);
        }

        private static MarkerRecord CloneResolved()
        {
            var r = Rec(1);
            r.Status = MarkerStatus.Resolved; r.ResolvedAt = Today.AddDays(-2); r.ResolvedBy = "kdoe";
            return r;
        }

        // ------------------------------------------------------------------ markers that cannot be written

        private static void PassedOver(Action<string, bool?> check)
        {
            Func<MarkerAccess, MarkerRecord, string, MarkerPlan.Result> run = (access, rec, held) =>
            {
                var m = Ex(1, rec, access);
                m.HeldBy = held;
                return Plan(m, Edited(r => r.Severity = "Clearance"));
            };
            Func<MarkerPlan.Result, string> why = r => r.PassedOver.Count == 1 && r.Changes.Count == 0 ? r.PassedOver[0].Reason : "?";
            check(P + "a marker that cannot be written is passed over with its reason: held by the holder's name, or by 'another user' when it is not known",
                why(run(MarkerAccess.HeldByOther, Rec(1), "kdoe")) == "held by kdoe" && why(run(MarkerAccess.HeldByOther, Rec(1), "")) == "held by another user" && why(run(MarkerAccess.HeldByOther, Rec(1), null)) == "held by another user");
            check(P + "...changed in central, inside a group, written by a newer LiveClash, and unreadable each say so",
                why(run(MarkerAccess.ChangedInCentral, Rec(1), null)) == "changed in central" && why(run(MarkerAccess.InsideGroup, Rec(1), null)) == "inside a group"
                && why(run(MarkerAccess.NewerFormat, null, null)) == "written by a newer LiveClash" && why(run(MarkerAccess.Unreadable, null, null)) == "unreadable record" && why(run(MarkerAccess.Free, null, null)) == "unreadable record");
            var p = run(MarkerAccess.HeldByOther, Rec(1), "kdoe");
            var u = run(MarkerAccess.Unreadable, null, null);
            check(P + "...naming the marker and the clash key (no key for a marker with no record), and counting it as passed over, not as updated or unchanged",
                p.PassedOver[0].MarkerUid == "m1" && p.PassedOver[0].Key == "k1" && u.PassedOver[0].Key == null && p.ToCounts().PassedOver.Count == 1 && p.ToCounts().PassedOver[0].Text == "held by kdoe" && p.ToCounts().Updated == 0 && p.Unchanged == 0
                && p.NeedsDialog);
            var heldQuiet = Plan(Ex(1, Rec(1), MarkerAccess.HeldByOther), Row(1));
            check(P + "a held marker that needs no change is simply up to date, not passed over", heldQuiet.PassedOver.Count == 0 && heldQuiet.Unchanged == 1 && !heldQuiet.NeedsDialog);
        }

        // ------------------------------------------------------------------ retention

        private static MarkerRecord ResolvedAgo(TimeSpan age)
        {
            var r = Rec(1);
            r.Status = MarkerStatus.Resolved;
            r.ResolvedAt = Today - age;
            r.ResolvedBy = "kdoe";
            return r;
        }

        private static void Retention(Action<string, bool?> check)
        {
            Func<TimeSpan, bool, Action<MarkerPlan.Input>, MarkerPlan.Result> run = (age, remove, more) =>
                Plan(Ex(1, ResolvedAgo(age), MarkerAccess.Free), Edited(r => r.Verdict = PlanVerdict.NoLongerClashes), i => { i.Options.RemoveRetention = remove; if (more != null) more(i); });
            var day30 = TimeSpan.FromDays(30);
            check(P + "retention: a marker resolved exactly 30 days ago stays; one tick older is offered (not removed unless ticked)",
                run(day30, true, null).RetentionOffers.Count == 0 && run(day30, true, null).Removals.Count == 0 && run(day30 + TimeSpan.FromTicks(1), false, null).RetentionOffers.Count == 1
                && run(day30 + TimeSpan.FromTicks(1), false, null).Removals.Count == 0 && run(day30 + TimeSpan.FromTicks(1), false, null).RetentionOffers[0].Reason == "retention"
                && run(day30 + TimeSpan.FromTicks(1), false, null).RetentionOffers[0].MarkerUid == "m1");
            var ticked = run(day30 + TimeSpan.FromDays(1), true, null);
            check(P + "...ticked: it is also removed, as 'retention', and counted as removed, not up to date",
                ticked.RetentionOffers.Count == 1 && ticked.Removals.Count == 1 && ticked.Removals[0].Reason == "retention" && ticked.Removals[0].MarkerUid == "m1" && ticked.ToCounts().Removed == 1 && ticked.Unchanged == 0);
            var tenDays = run(TimeSpan.FromDays(11), false, i => i.Options.RetentionDays = 10);
            check(P + "...the number of days is the option's", tenDays.RetentionOffers.Count == 1 && run(TimeSpan.FromDays(9), false, i => i.Options.RetentionDays = 10).RetentionOffers.Count == 0);
            var old = TimeSpan.FromDays(90);
            var clashingAgain = Plan(Ex(1, ResolvedAgo(old), MarkerAccess.Free), Row(1), i => i.Options.RemoveRetention = true);
            var notLiveRow = Plan(Ex(1, ResolvedAgo(old), MarkerAccess.Free), Edited(r => r.Live = false), i => i.Options.RemoveRetention = true);
            var noRow = Plan(Ex(1, ResolvedAgo(old), MarkerAccess.Free), null, i => i.Options.RemoveRetention = true);
            var open = Plan(Ex(1, ResolvedAgo(old), MarkerAccess.Free), Edited(r => r.Verdict = PlanVerdict.NoLongerClashes), i =>
            {
                i.Options.RemoveRetention = true;
                i.Pending.Add(new MarkerPlan.Pending { Triple = ClashIdOf.Triple("h1", "", "o1"), SeqAtPending = 4, Status = MarkerStatus.Ignored });
            });
            var notResolved = Rec(1);
            notResolved.ResolvedAt = Today.AddDays(-90);
            var wrongStatus = Plan(Ex(1, notResolved, MarkerAccess.Free), Edited(r => r.Verdict = PlanVerdict.NoLongerClashes), i => i.Options.RemoveRetention = true);
            var noDate = ResolvedAgo(old);
            noDate.ResolvedAt = null;
            var undated = Plan(Ex(1, noDate, MarkerAccess.Free), Edited(r => r.Verdict = PlanVerdict.NoLongerClashes), i => i.Options.RemoveRetention = true);
            check(P + "...not while the pair clashes again, not with an open triage, and only for a Resolved marker with a date; with no row, or a row not live, the old resolved marker still goes",
                clashingAgain.RetentionOffers.Count == 0 && open.RetentionOffers.Count == 0 && wrongStatus.RetentionOffers.Count == 0 && undated.RetentionOffers.Count == 0
                && notLiveRow.Removals.Count == 1 && noRow.Removals.Count == 1);
            var held = Plan(Ex(1, ResolvedAgo(old), MarkerAccess.HeldByOther), null, i => i.Options.RemoveRetention = true);
            check(P + "...and never for a marker that cannot be written", held.RetentionOffers.Count == 0 && held.Removals.Count == 0);
        }

        // ------------------------------------------------------------------ what the user is told about a pending triage

        private static void PendingMessages(Action<string, bool?> check)
        {
            Func<MarkerPlan.Existing, long, MarkerPlan.Result> run = (m, seq) => Plan(m, Row(1), i => i.Pending.Add(new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = seq, Status = MarkerStatus.Ignored, Note = "n", By = "kdoe" }));
            var kept = run(Ex(1, Rec(1), MarkerAccess.HeldByOther), 4);
            var applied = run(Ex(1), 4);
            var dropped = run(Ex(1), 9);
            check(P + "pending triage: kept for a held marker says who holds it, written says so, dropped says the model won; each names the marker and the kind",
                kept.Pending.Count == 1 && kept.Pending[0].Kind == PendingKind.Kept && kept.Pending[0].Message == "Your pending triage waits: held by kdoe." && kept.Pending[0].MarkerUid == "m1"
                && applied.Pending[0].Kind == PendingKind.Applied && applied.Pending[0].Message == "Your pending triage was written to the marker." && applied.Pending[0].MarkerUid == "m1"
                && dropped.Pending[0].Kind == PendingKind.Dropped && dropped.Pending[0].Message == "Your pending triage was not applied: the marker was changed by someone else first (the model wins); yours is in the Record.");
            var gone = Plan(null, null, i => i.Pending.Add(new MarkerPlan.Pending { Triple = Tri(1), SeqAtPending = 1, Status = MarkerStatus.Ignored }));
            check(P + "...a triage whose marker is gone is dropped with its own message", gone.Pending.Count == 1 && gone.Pending[0].Kind == PendingKind.Dropped && gone.Pending[0].Message == "Your pending triage was dropped: the marker is no longer there." && gone.Pending[0].MarkerUid == null);
            check(P + "...a kept triage leaves the held marker out of both 'up to date' and 'updated' (it is passed over, with the holder named)",
                kept.PassedOver.Count == 1 && kept.PassedOver[0].Reason == "held by kdoe" && kept.Unchanged == 0 && applied.Changes.Count == 1 && applied.Changes[0].After.Status == MarkerStatus.Ignored && applied.Changes[0].After.Note == "n"
                && applied.Changes[0].After.ChangedBy == "kdoe" && applied.Changes[0].After.ChangedAt == Today && applied.Changes[0].Fields.SequenceEqual(Texts("Note", "Status")));
        }

        private static string Tri(int n) { return ClashIdOf.Triple("h" + n, "", "o" + n); }

        // ------------------------------------------------------------------ the numbers

        private static void Counting(Action<string, bool?> check)
        {
            var input = new MarkerPlan.Input { Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe", RemoveRetention = true, DisabledSets = new List<string> { "Set Z" } } };
            // 1: unchanged; 2: updated; 3: resolved; 4: held and needing a change; 5: not re-tested; 6: old resolved, removed; 7: not live
            input.Markers.Add(Ex(1)); input.Rows.Add(Row(1));
            input.Markers.Add(Ex(2)); input.Rows.Add(Edited(r => { r.Key = "k2"; r.HostUid = "h2"; r.OtherUid = "o2"; r.HostId = 102; r.OtherId = 202; r.X = 2; r.Severity = "Clearance"; }));
            input.Markers.Add(Ex(3)); input.Rows.Add(Edited(r => { r.Key = "k3"; r.HostUid = "h3"; r.OtherUid = "o3"; r.HostId = 103; r.OtherId = 203; r.X = 3; r.Verdict = PlanVerdict.NoLongerClashes; }));
            input.Markers.Add(Ex(4, Rec(4), MarkerAccess.HeldByOther)); input.Rows.Add(Edited(r => { r.Key = "k4"; r.HostUid = "h4"; r.OtherUid = "o4"; r.HostId = 104; r.OtherId = 204; r.X = 4; r.Severity = "Clearance"; }));
            input.Markers.Add(Ex(5)); input.Rows.Add(Edited(r => { r.Key = "k5"; r.HostUid = "h5"; r.OtherUid = "o5"; r.HostId = 105; r.OtherId = 205; r.X = 5; r.Verdict = PlanVerdict.NotRetested; r.NotRetestedReason = "link not loaded"; }));
            var six = Rec(6); six.Status = MarkerStatus.Resolved; six.ResolvedAt = Today.AddDays(-60);
            input.Markers.Add(Ex(6, six, MarkerAccess.Free));
            var seven = Rec(7); seven.SetName = "Set Z";
            input.Markers.Add(Ex(7, seven, MarkerAccess.Free));
            input.Rows.Add(Edited(r => { r.Key = "k7"; r.HostUid = "h7"; r.OtherUid = "o7"; r.HostId = 107; r.OtherId = 207; r.X = 7; r.SetName = "Set Z"; r.Live = false; }));
            input.Rows.Add(Edited(r => { r.Key = "k8"; r.HostUid = "h8"; r.OtherUid = "o8"; r.HostId = 108; r.OtherId = 208; r.X = 8; r.SetName = "Set B"; }));
            var plan = MarkerPlan.Build(input);
            var c = plan.ToCounts();
            check(P + "the counts of a mixed plan: 8 clashes = 7 existing markers + 1 created; unchanged 1 (the not-live one counts as up to date), updated 1, resolved 1, removed 1, passed over 1 held, not re-tested 1",
                plan.ExistingMarkers == 7 && c.Clashes == 8 && c.Created == 1 && c.Updated == 1 && c.Resolved == 1 && c.Removed == 1 && c.Unchanged == 2 && c.PassedOver.Count == 1 && c.PassedOver[0].Count == 1
                && c.NotRetested.Count == 1 && c.NotRetested[0].Text == "link not loaded" && c.Moved == 0 && c.Merged == 0 && c.PendingApplied == 0 && plan.Unchanged == 2 && plan.NeedsDialog);
            var sets = plan.SetCounts.ToDictionary(s => s.Name, StringComparer.Ordinal);
            check(P + "the counts for each set: in the panel, up to date, not live, and (for a set that is switched off) held back",
                sets.Count == 3 && sets["Set A"].InPanel == 3 && sets["Set A"].UpToDate == 3 && sets["Set A"].NotLive == 1 && !sets["Set A"].SetOff && sets["Set B"].InPanel == 1 && sets["Set B"].UpToDate == 1
                && sets["Set Z"].InPanel == 0 && sets["Set Z"].SetOff && sets["Set Z"].UpToDate == 0 && sets["Set Z"].NotLive == 0 && sets["Set Z"].NotRetested == 1);

            Func<Action<MarkerPlan.Input>, MarkerPlan.Result> dialog = edit =>
            {
                var i = new MarkerPlan.Input { Options = new MarkerPlan.Options { Today = Today, UserName = "jdoe" } };
                i.Markers.Add(Ex(1)); i.Rows.Add(Row(1));
                edit(i);
                return MarkerPlan.Build(i);
            };
            check(P + "the dialog is needed only for: more creations than the threshold, a first create, a passed-over marker, a retention offer, a marker count over the warning, over the cap, or set-up; a quiet plan needs none",
                !dialog(i => { }).NeedsDialog
                && !dialog(i => { i.Options.DialogCreateThreshold = 1; i.Rows.Add(NewRow(2)); }).NeedsDialog && dialog(i => { i.Options.DialogCreateThreshold = 1; i.Rows.Add(NewRow(2)); i.Rows.Add(NewRow(3)); }).NeedsDialog
                && dialog(i => { i.Markers.Clear(); i.Rows.Clear(); i.Rows.Add(NewRow(2)); }).NeedsDialog && !dialog(i => { i.Options.WarnAt = 1; }).OverWarn && dialog(i => { i.Options.WarnAt = 0; }).NeedsDialog
                && dialog(i => { i.Options.WarnAt = 0; }).OverWarn && dialog(i => { i.Options.SetupNeeded = true; }).NeedsDialog
                && dialog(i => { i.Markers[0] = Ex(1, Rec(1), MarkerAccess.HeldByOther); i.Rows[0].Severity = "Clearance"; }).NeedsDialog
                && dialog(i => { i.Markers[0] = Ex(1, ResolvedAgo(TimeSpan.FromDays(60)), MarkerAccess.Free); i.Rows[0].Verdict = PlanVerdict.NoLongerClashes; }).NeedsDialog
                && dialog(i => { i.Options.Cap = 1; i.Rows.Add(NewRow(2)); i.Rows.Add(NewRow(3)); }).NeedsDialog);
        }

        // ------------------------------------------------------------------ two markers for one pair

        private static MarkerPlan.Existing Twin(string uid, Action<MarkerRecord> edit, MarkerAccess access)
        {
            var r = Rec(1);
            edit(r);
            return new MarkerPlan.Existing { MarkerUid = uid, Record = r, Access = access, HeldBy = "kdoe", ShownHash = "H" };
        }

        private static MarkerPlan.Result Merged(Action<MarkerRecord> kept, Action<MarkerRecord> dup)
        {
            return Plan(null, null, i => { i.Markers.Add(Twin("m1a", kept, MarkerAccess.Free)); i.Markers.Add(Twin("m1b", dup, MarkerAccess.Free)); });
        }

        private static void Merge(Action<string, bool?> check)
        {
            var plain = Merged(k => k.IssueNo = "JD-0001", d => d.IssueNo = "JD-0002");
            var kept = plain.Changes.Count == 1 ? plain.Changes[0] : null;
            check(P + "duplicates: the lowest UniqueId is kept, the other is removed as 'merged duplicate' naming the kept one, counted as a merge and a removal, and its issue number is kept on the survivor",
                kept != null && kept.MarkerUid == "m1a" && kept.Fields.SequenceEqual(Texts("MergedIssueNos")) && kept.After.MergedIssueNos.SequenceEqual(Texts("JD-0002")) && plain.Removals.Count == 1
                && plain.Removals[0].MarkerUid == "m1b" && plain.Removals[0].Reason == "merged duplicate" && plain.Removals[0].KeptUid == "m1a" && plain.Merges == 1 && plain.ToCounts().Merged == 1 && plain.ToCounts().Removed == 1
                && plain.LostDecisions.Count == 0 && plain.ToCounts().Updated == 1);

            var numbers = Merged(k => { k.IssueNo = "JD-0001"; k.MergedIssueNos = new List<string> { "JD-0007" }; }, d => { d.IssueNo = "jd-0001"; d.MergedIssueNos = new List<string> { "JD-0005", "jd-0007", "", null, "JD-0005" }; });
            check(P + "...a duplicate's own number (any case), empty ones and ones the survivor already has are not added twice; the rest are, once",
                numbers.Changes.Count == 1 && numbers.Changes[0].After.MergedIssueNos.SequenceEqual(Texts("JD-0007", "JD-0005")));
            var same = Merged(k => { k.IssueNo = "JD-0001"; k.MergedIssueNos = new List<string> { "JD-0007" }; }, d => { d.IssueNo = "JD-0001"; d.MergedIssueNos = new List<string> { "JD-0007" }; });
            check(P + "...a duplicate with nothing new to give writes nothing to the survivor but is still removed", same.Changes.Count == 0 && same.Removals.Count == 1 && same.Merges == 1);

            var give = Merged(k => { }, d =>
            {
                d.IssueNo = "JD-0002"; d.Status = MarkerStatus.Pending; d.Note = "n"; d.AssignedTo = "asmith"; d.ChangedBy = "kdoe"; d.ChangedAt = Today.AddDays(-1);
            });
            var g = give.Changes.Count == 1 ? give.Changes[0] : null;
            check(P + "a decision on the duplicate only is given to the survivor: status, note, assignee and who/when, all named in the change",
                g != null && g.After.Status == MarkerStatus.Pending && g.After.Note == "n" && g.After.AssignedTo == "asmith" && g.After.ChangedBy == "kdoe" && g.After.ChangedAt == Today.AddDays(-1)
                && g.Fields.SequenceEqual(Texts("AssignedTo", "MergedIssueNos", "Note", "Status")) && give.LostDecisions.Count == 0);

            var reopen = Merged(k => { k.Status = MarkerStatus.Resolved; k.ResolvedAt = Today.AddDays(-3); k.ResolvedBy = "kdoe"; }, d => { d.Status = MarkerStatus.Ignored; d.Note = null; d.AssignedTo = null; d.ChangedBy = null; d.ChangedAt = null; });
            var rc = reopen.Changes.Count == 1 ? reopen.Changes[0] : null;
            check(P + "...a human decision given to a resolved survivor clears its resolution; missing text becomes empty",
                rc != null && rc.After.Status == MarkerStatus.Ignored && rc.After.ResolvedBy == "" && rc.After.ResolvedAt == null && rc.After.Note == "" && rc.After.AssignedTo == "" && rc.After.ChangedBy == "" && rc.After.ChangedAt == null);

            var soft = Merged(k => { }, d => { d.Status = MarkerStatus.Reopened; d.Note = "x"; });
            var sc = soft.Changes.Count == 1 ? soft.Changes[0] : null;
            check(P + "...a re-test result (Reopened) on the duplicate is not a decision, but its note is", sc != null && sc.After.Status == MarkerStatus.New && sc.After.Note == "x" && sc.Fields.SequenceEqual(Texts("Note")));

            var equal = Merged(k => { k.Status = MarkerStatus.Ignored; k.Note = null; k.AssignedTo = "asmith"; }, d => { d.Status = MarkerStatus.Ignored; d.Note = ""; d.AssignedTo = "asmith"; });
            var effective = Merged(k => { k.Status = MarkerStatus.Resolved; k.Note = "n"; k.ResolvedAt = Today.AddDays(-1); }, d => { d.Status = MarkerStatus.Reopened; d.Note = "n"; });
            check(P + "two equal decisions (a missing note is an empty one), or two re-test results with one note, lose nothing", equal.LostDecisions.Count == 0 && effective.LostDecisions.Count == 0);

            var byStatus = Merged(k => k.Status = MarkerStatus.Ignored, d => { d.IssueNo = "JD-0002"; d.Status = MarkerStatus.Coordinated; d.Note = "why"; d.AssignedTo = "bsmith"; d.ChangedBy = "kdoe"; });
            var l = byStatus.LostDecisions.Count == 1 ? byStatus.LostDecisions[0] : null;
            check(P + "two different decisions: the survivor's stays and the other is recorded as lost, with its status, note, assignee, number and author, and counted",
                l != null && l.RemovedUid == "m1b" && l.RemovedIssueNo == "JD-0002" && l.KeptUid == "m1a" && l.KeptIssueNo == "JD-0001" && l.Status == MarkerStatus.Coordinated && l.Note == "why" && l.AssignedTo == "bsmith"
                && l.By == "kdoe" && byStatus.ToCounts().LostDecisions == 1);
            var byNote = Merged(k => { k.Status = MarkerStatus.Ignored; k.Note = "a"; }, d => { d.Status = MarkerStatus.Ignored; d.Note = "b"; d.ChangedBy = ""; d.ReportedBy = "ksmith"; });
            var byAssignee = Merged(k => { k.Status = MarkerStatus.Ignored; k.AssignedTo = "a"; }, d => { d.Status = MarkerStatus.Ignored; d.AssignedTo = "b"; d.ChangedBy = null; d.ReportedBy = null; });
            check(P + "...a different note alone, or a different assignee alone, is also lost; the author is the changer, else the reporter, else empty",
                byNote.LostDecisions.Count == 1 && byNote.LostDecisions[0].By == "ksmith" && byAssignee.LostDecisions.Count == 1 && byAssignee.LostDecisions[0].By == "");

            var heldDup = Plan(null, null, i => { i.Markers.Add(Twin("m1a", k => { }, MarkerAccess.Free)); i.Markers.Add(Twin("m1b", d => { }, MarkerAccess.HeldByOther)); });
            var heldKept = Plan(null, null, i => { i.Markers.Add(Twin("m1a", k => { }, MarkerAccess.HeldByOther)); i.Markers.Add(Twin("m1b", d => { }, MarkerAccess.Free)); });
            check(P + "a duplicate is merged only when both are free; otherwise it is passed over, saying which one holds",
                heldDup.Removals.Count == 0 && heldDup.Merges == 0 && heldDup.PassedOver.Any(p => p.MarkerUid == "m1b" && p.Reason == "duplicate: held by kdoe")
                && heldKept.Removals.Count == 0 && heldKept.PassedOver.Any(p => p.MarkerUid == "m1b" && p.Reason == "duplicate: kept marker held by kdoe"));

            var three = Plan(null, null, i =>
            {
                i.Markers.Add(Twin("m1c", k => k.IssueNo = "JD-0003", MarkerAccess.Free)); i.Markers.Add(Twin("m1a", k => k.IssueNo = "JD-0001", MarkerAccess.Free)); i.Markers.Add(Twin("m1b", k => k.IssueNo = "JD-0002", MarkerAccess.Free));
            });
            check(P + "three markers for one pair: the lowest UniqueId survives with both numbers, the other two are removed", three.Removals.Select(r => r.MarkerUid).SequenceEqual(Texts("m1b", "m1c")) && three.Merges == 2
                && three.Changes.Count == 1 && three.Changes[0].MarkerUid == "m1a" && three.Changes[0].After.MergedIssueNos.SequenceEqual(Texts("JD-0002", "JD-0003")));

            var apart = Plan(null, null, i => { i.Markers.Add(Ex(1)); i.Markers.Add(Ex(2)); i.Markers.Add(new MarkerPlan.Existing { MarkerUid = "m3", Record = null, Access = MarkerAccess.Unreadable }); i.Markers.Add(new MarkerPlan.Existing { MarkerUid = "m4", Record = null, Access = MarkerAccess.Unreadable }); });
            check(P + "markers for different pairs, and markers with no record, are never merged", apart.Merges == 0 && apart.Removals.Count == 0 && apart.Unchanged == 2 && apart.PassedOver.Count == 2);
        }

        private static MarkerPlan.Row NewRow(int i)
        {
            var r = Row(i);
            r.ShownHash = "H";
            return r;
        }
    }
}
