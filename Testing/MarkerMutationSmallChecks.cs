using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 3: the small pure files whose texts and guards no check pinned down: the split-button menu, the Undo names,
    /// the Refresh fingerprint's refusal, the pending-triage join and the ledger events' order and text. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationSmallChecks
    {
        private const string P = "markers mutation small: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            Menu(check);
            Undo(check);
            Fingerprint(check);
            Join(check);
            LedgerText(check);
            LedgerOrder(check);
        }

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string[] Texts(params string[] items) { return items; }

        // ------------------------------------------------------------------ the menu

        private static string Dump(ScheduleMenu m)
        {
            return m.ButtonText + "||" + m.ButtonTooltip + "||" + string.Join("##", m.Items.Select(i => i.Id + "|" + i.Text + "|" + (i.Enabled ? "on" : "off") + "|" + i.Tooltip + "|" + (i.SeparatorBefore ? "sep" : "")));
        }

        private const string ButtonTip = "Put every clash in the model as a marker that a Revit schedule lists, shared with the team";

        private static void Menu(Action<string, bool?> check)
        {
            const string Off = "The clash schedule is not available yet.";
            check(P + "menu: with no snapshot every item is off with the one reason, the button is plain, and 'Remove resolved' has a separator before it",
                Dump(ScheduleMenuModel.Build(null)) == "Clash schedule||" + ButtonTip + "||refresh|Create markers|off|" + Off + "|##open|Open the schedule|off|" + Off + "|##focus|Show markers in Focus view|off|" + Off
                + "|##removeResolved|Remove resolved|off|" + Off + "|sep##removeAll|Remove all markers...|off|" + Off + "|");

            var none = new ScheduleSnapshot();
            check(P + "menu: no markers yet: Create markers is on, the rest say why they are off",
                Dump(ScheduleMenuModel.Build(none)) == "Clash schedule||" + ButtonTip + "||refresh|Create markers|on||##open|Open the schedule|off|There is no schedule yet. Create markers first.|##focus|Show markers in Focus view|off|There are no markers yet."
                + "|##removeResolved|Remove resolved|off|Nothing is resolved yet.|sep##removeAll|Remove all markers...|off|There are no markers to remove.|");

            var full = new ScheduleSnapshot { MarkerCount = 1234, OutOfDate = 12, ResolvedCount = 1500, HasSchedule = true };
            check(P + "menu: markers, a schedule, work to do and resolved ones: counts in the button and items, with thousands separators; everything on",
                Dump(ScheduleMenuModel.Build(full)) == "Clash schedule (12)||" + ButtonTip + "||refresh|Refresh markers (12 to do)|on||##open|Open the schedule (1,234)|on||##focus|Show markers in Focus view|on||##removeResolved|Remove resolved (1,500)|on||sep##removeAll|Remove all markers...|on||");

            var quiet = new ScheduleSnapshot { MarkerCount = 3, OutOfDate = 0, ResolvedCount = 0, HasSchedule = false };
            check(P + "menu: markers but nothing to do, nothing resolved and the schedule missing: plain Refresh, Remove resolved off with its reason, Open off with the 'missing' reason",
                Dump(ScheduleMenuModel.Build(quiet)) == "Clash schedule||" + ButtonTip + "||refresh|Refresh markers|on||##open|Open the schedule|off|The schedule is missing. Refresh makes it again.|##focus|Show markers in Focus view|on||##removeResolved|Remove resolved|off|Nothing is resolved yet.|sep##removeAll|Remove all markers...|on||");

            var refused = new ScheduleSnapshot { MarkerCount = 3, OutOfDate = 2, ResolvedCount = 1, HasSchedule = true, RefusalReason = "This project is read-only." };
            check(P + "menu: a refused snapshot turns every action off with the refusal as the reason, but Open stays on",
                Dump(ScheduleMenuModel.Build(refused)) == "Clash schedule (2)||" + ButtonTip + "||refresh|Refresh markers (2 to do)|off|This project is read-only.|##open|Open the schedule (3)|on||##focus|Show markers in Focus view|off|This project is read-only."
                + "|##removeResolved|Remove resolved (1)|off|This project is read-only.|sep##removeAll|Remove all markers...|off|This project is read-only.|");

            var params1 = new ScheduleSnapshot { ParametersPresent = true };
            var params2 = new ScheduleSnapshot { ParametersPresent = true, RefusalReason = "Busy." };
            var params3 = new ScheduleSnapshot { ParametersPresent = true, MarkerCount = 1 };
            var negative = new ScheduleSnapshot { MarkerCount = 2, OutOfDate = -4, ResolvedCount = -1 };
            var m1 = ScheduleMenuModel.Build(params1);
            var m2 = ScheduleMenuModel.Build(params2);
            check(P + "menu: left-over parameters (no markers) add 'Remove LiveClash parameters', off when refused; with markers they do not; negative counts count as zero",
                m1.Items.Count == 6 && m1.Item("removeParameters").Text == "Remove LiveClash parameters" && m1.Item("removeParameters").Enabled && m1.Item("removeParameters").Tooltip == ""
                && m2.Item("removeParameters").Enabled == false && m2.Item("removeParameters").Tooltip == "Busy." && ScheduleMenuModel.Build(params3).Items.Count == 5
                && ScheduleMenuModel.Build(negative).ButtonText == "Clash schedule" && ScheduleMenuModel.Build(negative).Item("refresh").Text == "Refresh markers" && ScheduleMenuModel.Build(negative).Item("removeResolved").Text == "Remove resolved"
                && ScheduleMenuModel.Build(none).Item("nothing") == null && ScheduleMenuModel.Build(none).Items.Count(i => i.SeparatorBefore) == 1);
        }

        // ------------------------------------------------------------------ the Undo names

        private static void Undo(Action<string, bool?> check)
        {
            var expected = Texts("LiveClash: refresh clash markers", "LiveClash: set up clash markers", "LiveClash: write clash markers", "LiveClash: remove resolved clash markers", "LiveClash: remove all clash markers",
                "LiveClash: remove clash parameters", "LiveClash: show markers in my Focus view", "LiveClash: set clash status");
            check(P + "undo: the transaction names that concern markers are exactly these eight (a Revit-tier check keeps them in step with MarkerTx)", MarkerUndo.Names.SequenceEqual(expected));
            check(P + "undo: each name is recognised on its own, ordinal and exact; a list with one of them, or only other names, or null, is answered",
                expected.All(n => MarkerUndo.Concerns(new[] { n })) && MarkerUndo.Concerns(new[] { "Move", null, expected[3] }) && !MarkerUndo.Concerns(new[] { "Move", null, "liveclash: refresh clash markers", "LiveClash: refresh clash markers " })
                && !MarkerUndo.Concerns(null) && !MarkerUndo.Concerns(Array.Empty<string>()));
        }

        // ------------------------------------------------------------------ the fingerprint's refusal

        private static RefreshFingerprint Fp(int creations, int removals, int offers, int changes, int lost, string[] cIds, string[] chIds, string[] rIds, string[] oIds)
        {
            Func<string[], HashSet<string>> set = a => a == null ? null : new HashSet<string>(a, StringComparer.Ordinal);
            return new RefreshFingerprint
            {
                Creations = creations, Removals = removals, RetentionOffers = offers, Changes = changes, LostDecisions = lost, CreationIds = set(cIds), ChangeIds = set(chIds), RemovalIds = set(rIds), OfferIds = set(oIds)
            };
        }

        private static void Fingerprint(Action<string, bool?> check)
        {
            const string Head = "The clashes changed since the plan was shown: ";
            const string Tail = ". Review the plan again. Nothing was changed.";
            var shown = Fp(2, 1, 1, 3, 0, Texts("c1", "c2"), Texts("u1", "u2", "u3"), Texts("r1"), Texts("o1"));
            Func<RefreshFingerprint, bool, string> why = (now, tick) => RefreshFingerprint.Changed(shown, now, tick);

            check(P + "fingerprint: each kind of 'more than was shown' is refused with its own words, and several are joined with ' and '",
                why(Fp(3, 1, 1, 3, 0, null, null, null, null), false) == Head + "it would now create 3 markers, not 2" + Tail
                && why(Fp(2, 2, 1, 3, 0, null, null, null, null), false) == Head + "it would now remove 2 markers, not 1" + Tail
                && why(Fp(2, 1, 2, 3, 0, null, null, null, null), false) == Head + "2 markers are now old enough to remove, not 1" + Tail
                && why(Fp(2, 1, 1, 4, 0, null, null, null, null), false) == Head + "it would now change 4 markers, not 3" + Tail
                && why(Fp(2, 1, 1, 3, 1, null, null, null, null), false) == Head + "it would now lose 1 decisions, not 0" + Tail
                && why(Fp(3, 1, 1, 4, 0, null, null, null, null), false) == Head + "it would now create 3 markers, not 2 and it would now change 4 markers, not 3" + Tail);
            check(P + "fingerprint: the same counts with other markers in them are refused with their own words, when the ids are known",
                why(Fp(2, 1, 1, 3, 0, Texts("c1", "cX"), Texts("u1", "u2", "u3"), Texts("r1"), Texts("o1")), false) == Head + "it would now create other markers than the ones shown" + Tail
                && why(Fp(2, 1, 1, 3, 0, Texts("c1", "c2"), Texts("u1", "u2", "uX"), Texts("r1"), Texts("o1")), false) == Head + "it would now change other markers than the ones shown" + Tail
                && why(Fp(2, 1, 1, 3, 0, Texts("c1", "c2"), Texts("u1", "u2", "u3"), Texts("rX"), Texts("o1")), false) == Head + "it would now remove other markers than the ones shown" + Tail
                && why(Fp(2, 1, 1, 3, 0, Texts("c1", "c2"), Texts("u1", "u2", "u3"), Texts("r1"), Texts("oX")), false) == Head + "other markers are now old enough to remove than the ones shown" + Tail);
            check(P + "fingerprint: nothing is refused when the ids are not known (the counts alone decide), when the shown ids are missing, or when the same ids come back in fewer numbers",
                why(Fp(2, 1, 1, 3, 0, null, null, null, null), false) == null && why(Fp(2, 1, 1, 3, 0, Texts("cX", "cY"), Texts("uX", "uY", "uZ"), Texts("rX"), Texts("oX")), false) != null
                && RefreshFingerprint.Changed(Fp(2, 1, 1, 3, 0, null, null, null, null), Fp(2, 1, 1, 3, 0, Texts("zz", "yy"), Texts("zz"), Texts("zz"), Texts("zz")), false) == null
                && RefreshFingerprint.Changed(Fp(2, 1, 1, 3, 0, Texts("c1"), Texts("u1"), null, Texts("o1")), Fp(2, 1, 1, 3, 0, Texts("c1"), Texts("u1"), Texts("rX"), Texts("o1")), false) == null
                && RefreshFingerprint.Changed(Fp(2, 1, 1, 3, 0, Texts("c1"), Texts("u1"), Texts("r1"), null), Fp(2, 1, 1, 3, 0, Texts("c1"), Texts("u1"), Texts("rX"), Texts("o1")), false) == null
                && why(Fp(1, 0, 0, 2, 0, Texts("c1"), Texts("u1", "u2"), Texts(), Texts()), false) == null);
            check(P + "fingerprint: a ticked retention box allows the removals to grow by exactly the offers shown, and their ids; null on either side is 'nothing to compare'",
                why(Fp(2, 2, 1, 3, 0, Texts("c1", "c2"), Texts("u1", "u2", "u3"), Texts("r1", "o1"), Texts("o1")), true) == null
                && why(Fp(2, 3, 1, 3, 0, null, null, null, null), true) == Head + "it would now remove 3 markers, not 2" + Tail
                && why(Fp(2, 2, 1, 3, 0, Texts("c1", "c2"), Texts("u1", "u2", "u3"), Texts("r1", "oX"), Texts("o1")), true) == Head + "it would now remove other markers than the ones shown" + Tail
                && RefreshFingerprint.Changed(null, shown, false) == null && RefreshFingerprint.Changed(shown, null, false) == null);

            var empty = RefreshFingerprint.Of(null);
            var plan = new MarkerPlan.Result();
            plan.Creations.Add(new MarkerPlan.Creation { Triple = "t1" });
            plan.Creations.Add(new MarkerPlan.Creation { Triple = null });
            plan.Changes.Add(new MarkerPlan.Change { MarkerUid = "m1" });
            plan.Removals.Add(new MarkerPlan.Removal { MarkerUid = "m2" });
            plan.RetentionOffers.Add(new MarkerPlan.Removal { MarkerUid = "m3" });
            plan.LostDecisions.Add(new MarkerPlan.LostDecision());
            var fp = RefreshFingerprint.Of(plan);
            check(P + "fingerprint: of a plan it counts creations, changes, removals, offers and lost decisions and lists who is in each (a missing id left out); of no plan it is empty with no ids",
                empty.Creations == 0 && empty.CreationIds == null && fp.Creations == 2 && fp.Changes == 1 && fp.Removals == 1 && fp.RetentionOffers == 1 && fp.LostDecisions == 1
                && fp.CreationIds.SetEquals(Texts("t1")) && fp.ChangeIds.SetEquals(Texts("m1")) && fp.RemovalIds.SetEquals(Texts("m2")) && fp.OfferIds.SetEquals(Texts("m3")));
        }

        // ------------------------------------------------------------------ the pending-triage join

        private static MarkerPlan.PendingOutcome Out(string triple, PendingKind kind) { return new MarkerPlan.PendingOutcome { Triple = triple, Kind = kind }; }

        private static void Join(Action<string, bool?> check)
        {
            var view = LedgerExtras.Read(new[] { LedgerExtras.PendingLine("kb", T(1), "jdoe", 1, MarkerStatus.Ignored, null, null), LedgerExtras.PendingLine("ka", T(1), "jdoe", 1, MarkerStatus.Ignored, null, null) });
            var map = new Dictionary<string, string>(StringComparer.Ordinal) { { "tb", "kb" }, { "ta", "ka" }, { "te", "" }, { "tn", null } };
            var lines = PendingMap.DoneLines(new[] { Out("tb", PendingKind.Applied), Out("ta", PendingKind.Dropped), Out("te", PendingKind.Applied), Out("tn", PendingKind.Applied), Out("tz", PendingKind.Applied), Out(null, PendingKind.Applied), null, Out("ta", PendingKind.Kept) },
                map, view, "jdoe", T(60));
            check(P + "pending join: Done lines come out in the order of the triples; an outcome with no key, an empty key, no triple or no entry in the map writes nothing",
                lines.Count == 2 && lines[0].StartsWith("ka\t", StringComparison.Ordinal) && lines[1].StartsWith("kb\t", StringComparison.Ordinal));
            check(P + "pending join: with no outcomes, no map or null entries it writes nothing and does not throw",
                PendingMap.DoneLines(null, map, view, "jdoe", T(1)).Count == 0 && PendingMap.DoneLines(new[] { Out("ta", PendingKind.Applied) }, null, view, "jdoe", T(1)).Count == 0
                && PendingMap.DoneLines(new[] { Out("ta", PendingKind.Applied) }, map, null, "jdoe", T(1)).Count == 1);

            var un = PendingMap.UnmatchedDoneLines(Texts("kz", "ka", null, "", "ka", "kb"), view, "jdoe", T(60));
            check(P + "pending join: unmatched keys are written once each, in order, skipping empty ones", un.Select(l => l.Split((char)9)[0]).SequenceEqual(Texts("ka", "kb", "kz")));

            var lost = new[]
            {
                new MarkerPlan.LostDecision { KeptUid = "m2", RemovedUid = "mB", RemovedIssueNo = "KD-0002", KeptIssueNo = "JD-0002", Status = MarkerStatus.Ignored, Note = "n", AssignedTo = "a" },
                new MarkerPlan.LostDecision { KeptUid = "m1", RemovedUid = "mC", RemovedIssueNo = "KD-0003", KeptIssueNo = "JD-0001", Status = MarkerStatus.Pending },
                new MarkerPlan.LostDecision { KeptUid = "m1", RemovedUid = "mA", RemovedIssueNo = "KD-0001", KeptIssueNo = "JD-0001", Status = MarkerStatus.Pending },
                new MarkerPlan.LostDecision { KeptUid = "m3", RemovedUid = "mD" }, new MarkerPlan.LostDecision { KeptUid = "m4", RemovedUid = "mE" }, null
            };
            var uidToKey = new Dictionary<string, string>(StringComparer.Ordinal) { { "m1", "k1" }, { "m2", "k2" }, { "m3", "" } };
            var dups = PendingMap.DuplicateLines(lost, uidToKey, "jdoe", T(60));
            check(P + "pending join: lost decisions are recorded in the order of the kept marker, then the removed one; a kept marker with no key (empty or missing) records nothing",
                dups.Count == 3 && dups[0].StartsWith("k1\t", StringComparison.Ordinal) && dups[0].Contains("KD-0001") && dups[1].StartsWith("k1\t", StringComparison.Ordinal) && dups[1].Contains("KD-0003") && dups[2].StartsWith("k2\t", StringComparison.Ordinal)
                && PendingMap.DuplicateLines(null, uidToKey, "jdoe", T(1)).Count == 0 && PendingMap.DuplicateLines(lost, null, "jdoe", T(1)).Count == 0);
        }

        // ------------------------------------------------------------------ the ledger events' text

        private static string[] Fields(string line) { return line.Split((char)9); }

        private static void LedgerText(Action<string, bool?> check)
        {
            var assigned = LedgerExtras.AssignedLine("k", T(1), null, null);
            var pending = LedgerExtras.PendingLine("k", T(1), null, 7, null, null, null);
            var done = LedgerExtras.DoneLine("k", T(1), null);
            var issue = LedgerExtras.IssueLine("k", T(1), null, null);
            var dup = LedgerExtras.DuplicateLine("k", T(1), null, null, null, MarkerStatus.Ignored, null, null);
            var rec = LedgerExtras.ReconciledLine("k", T(1), null, null, null);
            check(P + "ledger text: a missing text is an empty field, never a placeholder, in every event, and the field counts and event names are exactly these",
                Fields(assigned).SequenceEqual(Texts("k", "Assigned", T(1).ToString("o", Inv), "", "")) && Fields(pending).SequenceEqual(Texts("k", "PendingToModel", T(1).ToString("o", Inv), "", "", "7", "", "", ""))
                && Fields(done).SequenceEqual(Texts("k", "PendingDone", T(1).ToString("o", Inv), "", "")) && Fields(issue).SequenceEqual(Texts("k", "Issue", T(1).ToString("o", Inv), "", ""))
                && Fields(dup).SequenceEqual(Texts("k", "DuplicateNotKept", T(1).ToString("o", Inv), "", "", "", "", "Ignored", "")) && Fields(rec).SequenceEqual(Texts("k", "Reconciled", T(1).ToString("o", Inv), "", " ")));
            var tabs = LedgerExtras.PendingLine("k\t1", T(1), "j\td", 7, MarkerStatus.Pending, "a\tb\r\nc", "x\ny");
            check(P + "ledger text: tabs and line breaks in any text become spaces, so a line keeps its nine fields", Fields(tabs).Length == 9 && Fields(tabs)[0] == "k 1" && Fields(tabs)[3] == "j d" && Fields(tabs)[4] == "a b  c" && Fields(tabs)[8] == "x y");
        }

        // ------------------------------------------------------------------ the ledger events' order

        private static bool Agrees(string[] lines, Func<LedgerExtras.View, string> pick)
        {
            string first = pick(LedgerExtras.Read(lines));
            string second = pick(LedgerExtras.Read(Enumerable.Reverse(lines).ToArray()));
            return first == second;
        }

        private static void LedgerOrder(Action<string, bool?> check)
        {
            var a1 = LedgerExtras.AssignedLine("k", T(1), "c", "ab");
            var a2 = LedgerExtras.AssignedLine("k", T(1), "bc", "a");
            check(P + "ledger order: two assignments on the same clock whose texts would run together without a separator still give one answer in either file order",
                Agrees(new[] { a1, a2 }, v => v.Assigned["k"].Assignee + "/" + v.Assigned["k"].By));
            var r1 = LedgerExtras.ReconciledLine("k", T(1), "u", "a", "bc");
            var r2 = LedgerExtras.ReconciledLine("k", T(1), "u", "ab", "c");
            check(P + "ledger order: two 'came from the model' notes on the same clock with run-together source and event give one answer", Agrees(new[] { r1, r2 }, v => v.Reconciled["k"].Source + "/" + v.Reconciled["k"].Event));
            var p1 = LedgerExtras.PendingLine("k", T(1), "u", 5, MarkerStatus.Ignored, "a", "bc");
            var p2 = LedgerExtras.PendingLine("k", T(1), "u", 5, MarkerStatus.Ignored, "ab", "c");
            var p3 = LedgerExtras.PendingLine("k", T(1), "bc", 5, MarkerStatus.Ignored, "a", "b");
            var p4 = LedgerExtras.PendingLine("k", T(1), "c", 5, MarkerStatus.Ignored, "a", "bb");
            var p5 = LedgerExtras.PendingLine("k", T(1), "u", 5, MarkerStatus.Ignored, null, null);
            var p6 = LedgerExtras.PendingLine("k", T(1), "u", 5, MarkerStatus.Ignored, "", "");
            Func<LedgerExtras.View, string> text = v => v.Pending["k"].Note + "/" + v.Pending["k"].AssignedTo + "/" + v.Pending["k"].By;
            check(P + "ledger order: pending entries on the same clock and Seq whose note, assignee and author would run together give one answer; an unset text and an empty one differ",
                Agrees(new[] { p1, p2 }, text) && Agrees(new[] { p3, p4 }, text) && Agrees(new[] { p5, p6 }, text) && LedgerExtras.Read(new[] { p5, p6 }).Pending["k"].Note == "");
            var i1 = LedgerExtras.IssueLine("k", T(1), "u", "JD-0002");
            var i2 = LedgerExtras.IssueLine("k", T(1), "u", "JD-0010");
            check(P + "ledger order: of two issue numbers on one clock the greater text wins whatever the file order", LedgerExtras.Read(new[] { i1, i2 }).Issues["k"].IssueNo == "JD-0010" && LedgerExtras.Read(new[] { i2, i1 }).Issues["k"].IssueNo == "JD-0010");
            var older = LedgerExtras.PendingLine("k", T(1), "u", 2, MarkerStatus.Ignored, null, null);
            var newer = LedgerExtras.PendingLine("k", T(1), "u", 3, MarkerStatus.Coordinated, null, null);
            check(P + "ledger order: on one clock the higher Seq decides which status the merged entry takes, whatever the file order",
                LedgerExtras.Read(new[] { older, newer }).Pending["k"].Status == MarkerStatus.Coordinated && LedgerExtras.Read(new[] { newer, older }).Pending["k"].Status == MarkerStatus.Coordinated);
        }
    }
}
