using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B2: the Refresh plan dialog's text, lines and buttons (spec 9, 6.2, 6.9, 7).
    /// Revit-free. Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class SchedulePlanChecks
    {
        private const string P = "schedule plan: ";

        public static void RunAll(Action<string, bool?> check)
        {
            CreateLine(check);
            Board(check);
            Reasons(check);
            Frame(check);
            Retention(check);
            Caps(check);
            FirstCreate(check);
            Apply(check);
            Defaults(check);
            UpToDate(check);
            Robust(check);
        }

        // B4c: the dialog says when nothing needs writing.
        private static void UpToDate(Action<string, bool?> check)
        {
            check(P + "nothing to write: \"Markers up to date (140).\"", new RefreshPlanModel(new RefreshPlanView { ExistingMarkers = 140, Unchanged = 140 }).NothingNote == "Markers up to date (140).");
            check(P + "...not when there is a write", new RefreshPlanModel(new RefreshPlanView { ExistingMarkers = 140, Update = 1 }).NothingNote == "");
            check(P + "...not when the refresh is refused", new RefreshPlanModel(new RefreshPlanView { ExistingMarkers = 140, Refusal = "No." }).NothingNote == "");
            check(P + "...not when over the cap", new RefreshPlanModel(new RefreshPlanView { ExistingMarkers = 140, OverCap = true, WouldCreate = 2100 }).NothingNote == "");
            var passed = new RefreshPlanModel(new RefreshPlanView { ExistingMarkers = 140, Unchanged = 128, NeedsDialog = true, PassedOver = { R("held by jdoe", 12) } });
            check(P + "passed-over markers only: the note and the passed-over line both show", passed.NothingNote == "Markers up to date (140)." && Line(passed, "Passed over") == "12|held by jdoe");
            check(P + "a null view has no note", new RefreshPlanModel(null).NothingNote == "");
            var cult = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                check(P + "...and 1,240 markers read \"(1,240)\" under any culture", new RefreshPlanModel(new RefreshPlanView { ExistingMarkers = 1240 }).NothingNote == "Markers up to date (1,240).");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = cult; }
        }

        // Branch coverage, added at green: the fall-backs for numbers and lists a caller left unset or null.
        private static void Defaults(Action<string, bool?> check)
        {
            check(P + "retention days of zero or less fall back to 30", new RefreshPlanModel(new RefreshPlanView { RetentionCount = 2, RetentionDays = 0 }).RetentionText == "Remove 2 markers resolved more than 30 days ago");
            check(P + "a cap of zero or less falls back to 2,000", new RefreshPlanModel(new RefreshPlanView { OverCap = true, WouldCreate = 3000, Cap = 0 }).PrimaryText == "Create 2,000 now");
            var nulls = new RefreshPlanView { PassedOver = null, NotRetested = null, ByStatus = null, FirstCreateAdds = null, ExistingSchedulesShowing = null, IsCreate = true, OverWarn = true, TotalMarkers = 5001 };
            var m = new RefreshPlanModel(nulls);
            check(P + "null reason and block lists are read as empty", m.Lines.Count == 1 && m.AddsLines.Count == 0 && m.ShowingLines.Count == 0 && m.Warning.Contains("5,001"));
            var blanks = new RefreshPlanModel(new RefreshPlanView { IsCreate = true, FirstCreateAdds = { "", null, "  the workset " }, ExistingSchedulesShowing = { " " } });
            check(P + "blank lines in the blocks are dropped and the rest trimmed", blanks.AddsLines.Count == 1 && blanks.AddsLines[0] == "the workset" && blanks.ShowingHeading == "");
            var noStatusText = new RefreshPlanModel(new RefreshPlanView { OverWarn = true, TotalMarkers = 5100, ByStatus = { R(null, 3) } });
            check(P + "a status with no name reads \"other\" in the warning", noStatusText.Warning.Contains("3 other"));
        }

        private static MarkerSummary.Reason R(string text, int n) { return new MarkerSummary.Reason { Text = text, Count = n }; }

        private static string Line(RefreshPlanModel m, string label)
        {
            var l = m.Lines.FirstOrDefault(x => x.Label == label);
            return l == null ? null : l.Count.ToString(CultureInfo.InvariantCulture) + "|" + l.Detail;
        }

        private static RefreshPlanView BoardExample()
        {
            return new RefreshPlanView
            {
                IsCreate = false, NeedsDialog = true, Clashes = 813,
                Create = 6, FirstIssueNo = "JD-0141", LastIssueNo = "JD-0146", Update = 4, Move = 2, Resolve = 2, Merge = 1, PendingApplied = 1, Unchanged = 786,
                PassedOver = { R("held by kdoe", 9), R("changed in central", 2), R("inside a group", 1) },
                NotRetested = { R("link \"PRJ01-STR\" not loaded", 3) },
                RetentionCount = 5
            };
        }

        private static void CreateLine(Action<string, bool?> check)
        {
            var m = new RefreshPlanModel(new RefreshPlanView { Create = 6, FirstIssueNo = "JD-0141", LastIssueNo = "JD-0146" });
            check(P + "the create line: 6 \"new clashes (JD-0141 to JD-0146)\"", Line(m, "Create") == "6|new clashes (JD-0141 to JD-0146)");
            var one = new RefreshPlanModel(new RefreshPlanView { Create = 1, FirstIssueNo = "JD-0141", LastIssueNo = "JD-0141" });
            check(P + "one new clash, or first = last: \"new clash (JD-0141)\"", Line(one, "Create") == "1|new clash (JD-0141)");
            var same = new RefreshPlanModel(new RefreshPlanView { Create = 3, FirstIssueNo = "JD-0141", LastIssueNo = "JD-0141" });
            check(P + "...also for several with one number given", Line(same, "Create") == "3|new clashes (JD-0141)");
            var none = new RefreshPlanModel(new RefreshPlanView { Create = 4 });
            check(P + "no issue numbers given: \"new clashes\"", Line(none, "Create") == "4|new clashes");
            var onlyLast = new RefreshPlanModel(new RefreshPlanView { Create = 4, LastIssueNo = "JD-0004" });
            check(P + "only the last number given: it is the one shown", Line(onlyLast, "Create") == "4|new clashes (JD-0004)");
        }

        private static void Board(Action<string, bool?> check)
        {
            var m = new RefreshPlanModel(BoardExample());
            var labels = string.Join(",", m.Lines.Select(l => l.Label));
            check(P + "the board's example gives its lines in the board's order", labels == "Create,Update,Move,Resolved,Merge,Pending,Unchanged,Passed over,Not re-tested");
            check(P + "...with the board's counts and wording",
                Line(m, "Update") == "4|status, note, severity, set or group" && Line(m, "Move") == "2|moved to where the clash is now"
                && Line(m, "Resolved") == "2|re-tested here: no longer clash" && Line(m, "Merge") == "1|two markers for one clash"
                && Line(m, "Pending") == "1|your triage, held until now: applied" && Line(m, "Unchanged") == "786|re-tested, nothing to write");
            check(P + "...passed over 12 with its three reasons, not re-tested 3 with its one",
                Line(m, "Passed over") == "12|9 held by kdoe, 2 changed in central, 1 inside a group" && Line(m, "Not re-tested") == "3|link \"PRJ01-STR\" not loaded");

            var quiet = new RefreshPlanModel(new RefreshPlanView { Unchanged = 140 });
            check(P + "lines with a zero count are left out and Unchanged is always shown",
                quiet.Lines.Count == 1 && Line(quiet, "Unchanged") == "140|re-tested, nothing to write");
            var zero = new RefreshPlanModel(new RefreshPlanView());
            check(P + "...even at zero", zero.Lines.Count == 1 && Line(zero, "Unchanged") == "0|re-tested, nothing to write");
            var neg = new RefreshPlanModel(new RefreshPlanView { Create = -3, Update = -1, Unchanged = -9 });
            check(P + "a negative count is treated as zero (no line)", neg.Lines.Count == 1 && Line(neg, "Unchanged") == "0|re-tested, nothing to write");
        }

        private static void Reasons(Action<string, bool?> check)
        {
            var many = new RefreshPlanModel(new RefreshPlanView { PassedOver = { R("b reason", 2), R("a reason", 2), R("big", 5) } });
            check(P + "reasons go by count, then name, each with its count", Line(many, "Passed over") == "9|5 big, 2 a reason, 2 b reason");
            var one = new RefreshPlanModel(new RefreshPlanView { NotRetested = { R("link not loaded", 3) } });
            check(P + "one reason is its text alone", Line(one, "Not re-tested") == "3|link not loaded");
            var junk = new RefreshPlanModel(new RefreshPlanView { PassedOver = { R("held by kdoe", 0), R("gone", -2), R("inside a group", 1), null } });
            check(P + "reasons with a count of 0 or less, and null ones, are ignored", Line(junk, "Passed over") == "1|inside a group");
            var allZero = new RefreshPlanModel(new RefreshPlanView { PassedOver = { R("held by kdoe", 0) } });
            check(P + "...and when none is left the line is gone", Line(allZero, "Passed over") == null);
            var noText = new RefreshPlanModel(new RefreshPlanView { PassedOver = { R(null, 2) } });
            check(P + "a reason without text reads \"other\"", Line(noText, "Passed over") == "2|other");
        }

        private static void Frame(Action<string, bool?> check)
        {
            check(P + "a create is titled \"Create clash markers\"", new RefreshPlanModel(new RefreshPlanView { IsCreate = true }).Title == "Create clash markers");
            check(P + "a refresh is titled \"Refresh clash markers\"", new RefreshPlanModel(new RefreshPlanView()).Title == "Refresh clash markers");
            check(P + "the footer for a workshared model says others see it after Synchronize",
                new RefreshPlanModel(new RefreshPlanView()).Footer == "One Undo. Others see the changes after you Synchronize.");
            check(P + "...and for one that is not workshared it only says One Undo.", new RefreshPlanModel(new RefreshPlanView { NonWorkshared = true }).Footer == "One Undo.");
            check(P + "the buttons are Apply and Cancel", new RefreshPlanModel(new RefreshPlanView()).PrimaryText == "Apply" && new RefreshPlanModel(new RefreshPlanView()).CancelText == "Cancel");
        }

        private static void Retention(Action<string, bool?> check)
        {
            var none = new RefreshPlanModel(new RefreshPlanView());
            check(P + "no retention offer at zero", !none.ShowRetention && none.RetentionText == "");
            var m = new RefreshPlanModel(new RefreshPlanView { RetentionCount = 5 });
            check(P + "retention is offered, unticked, \"Remove 5 markers resolved more than 30 days ago\"",
                m.ShowRetention && !m.RemoveRetention && m.RetentionText == "Remove 5 markers resolved more than 30 days ago");
            check(P + "...singular for one, and the days come from the view",
                new RefreshPlanModel(new RefreshPlanView { RetentionCount = 1, RetentionDays = 45 }).RetentionText == "Remove 1 marker resolved more than 45 days ago");
            check(P + "unticked by default, the choices do not remove (E9)", !m.Choices().RemoveRetention);
            m.RemoveRetention = true;
            check(P + "ticking it reaches the choices", m.Choices().RemoveRetention);
            var hidden = new RefreshPlanModel(new RefreshPlanView());
            hidden.RemoveRetention = true;
            check(P + "a hidden retention offer cannot be ticked into the choices", !hidden.Choices().RemoveRetention);
        }

        private static void Caps(Action<string, bool?> check)
        {
            var m = new RefreshPlanModel(new RefreshPlanView { OverCap = true, WouldCreate = 2350, Cap = 2000, Create = 2000 });
            check(P + "over the cap: the message names the count and the way out",
                m.CapMessage == "2,350 markers would be created. Narrow the sets, or create them in two runs (Refresh again after this one).");
            check(P + "...the primary button is \"Create 2,000 now\" and the choices ask for the cap only", m.PrimaryText == "Create 2,000 now" && m.Choices().CreateCapOnly);
            check(P + "...a cap of 1,000 reads \"Create 1,000 now\"", new RefreshPlanModel(new RefreshPlanView { OverCap = true, WouldCreate = 1500, Cap = 1000 }).PrimaryText == "Create 1,000 now");
            var ok = new RefreshPlanModel(new RefreshPlanView { Create = 5 });
            check(P + "under the cap: Apply, no message, the cap is not asked for", ok.PrimaryText == "Apply" && ok.CapMessage == "" && !ok.Choices().CreateCapOnly);

            var cult = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var de = new RefreshPlanModel(new RefreshPlanView { OverCap = true, WouldCreate = 2350, Cap = 2000 });
                check(P + "the separators do not follow the machine's culture", de.CapMessage.StartsWith("2,350 markers", StringComparison.Ordinal) && de.PrimaryText == "Create 2,000 now");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = cult; }

            var w = new RefreshPlanModel(new RefreshPlanView { OverWarn = true, TotalMarkers = 5001, ByStatus = { R("Resolved", 1001), R("New", 4000) } });
            check(P + "over 5,000 the warning gives the total, the counts by status and what to do",
                w.Warning == "This model holds 5,001 markers, more than 5,000. By status: 4,000 New, 1,001 Resolved. Consider Remove resolved, or narrower sets.");
            check(P + "...and there is no warning when the plan says there is none", new RefreshPlanModel(new RefreshPlanView { TotalMarkers = 9000 }).Warning == "");
            var noStatus = new RefreshPlanModel(new RefreshPlanView { OverWarn = true, TotalMarkers = 6000 });
            check(P + "...without status counts it still warns", noStatus.Warning == "This model holds 6,000 markers, more than 5,000. Consider Remove resolved, or narrower sets.");
        }

        private static void FirstCreate(Action<string, bool?> check)
        {
            var v = new RefreshPlanView
            {
                IsCreate = true,
                FirstCreateAdds = { "22 shared parameters", "the LiveClash workset", "the schedule \"LiveClash - Clashes\"" },
                ExistingSchedulesShowing = { "Specialty Equipment Schedule", "View filter \"Equipment\"" }
            };
            var m = new RefreshPlanModel(v);
            check(P + "the first create names what it adds", m.AddsHeading == "This adds to the model:" && m.AddsLines.Count == 3 && m.AddsLines[1] == "the LiveClash workset");
            check(P + "...and the existing schedules and filters that will show markers (M7)",
                m.ShowingHeading == "These existing schedules and filters do not look for LiveClash markers and will show them:" && m.ShowingLines.Count == 2);
            v.IsCreate = false;
            var r = new RefreshPlanModel(v);
            check(P + "a refresh shows neither block", r.AddsHeading == "" && r.AddsLines.Count == 0 && r.ShowingHeading == "" && r.ShowingLines.Count == 0);
            var emptyCreate = new RefreshPlanModel(new RefreshPlanView { IsCreate = true });
            check(P + "a create with nothing to name shows no heading", emptyCreate.AddsHeading == "" && emptyCreate.ShowingHeading == "");
        }

        private static void Apply(Action<string, bool?> check)
        {
            check(P + "with something to write, Apply is on", new RefreshPlanModel(new RefreshPlanView { Update = 1 }).CanApply);
            bool each = true;
            foreach (var one in new Action<RefreshPlanView>[] { v => v.Create = 1, v => v.Move = 1, v => v.Resolve = 1, v => v.Merge = 1, v => v.PendingApplied = 1 })
            {
                var v2 = new RefreshPlanView();
                one(v2);
                each &= new RefreshPlanModel(v2).CanApply;
            }
            check(P + "...each kind of write turns Apply on", each);
            check(P + "nothing to write: Apply is off", !new RefreshPlanModel(new RefreshPlanView { Unchanged = 786, PassedOver = { R("held by kdoe", 9) } }).CanApply);
            var ret = new RefreshPlanModel(new RefreshPlanView { RetentionCount = 3 });
            check(P + "...unless the retention removal is ticked", !ret.CanApply && Tick(ret).CanApply);
            check(P + "over the cap Apply is on", new RefreshPlanModel(new RefreshPlanView { OverCap = true, WouldCreate = 2100, Cap = 2000 }).CanApply);
            var refused = new RefreshPlanModel(new RefreshPlanView { Refusal = "This model is read-only.", Update = 3 });
            check(P + "a refusal is carried and Apply is off", refused.Refusal == "This model is read-only." && !refused.CanApply);
            check(P + "a null view gives an empty model that cannot apply", new RefreshPlanModel(null).Lines.Count == 0 && !new RefreshPlanModel(null).CanApply && new RefreshPlanModel(null).Title != null);
        }

        private static RefreshPlanModel Tick(RefreshPlanModel m) { m.RemoveRetention = true; return m; }

        private static void Robust(Action<string, bool?> check)
        {
            var rnd = new Random(20261031);
            bool ok = true, counts = true, text = true;
            for (int i = 0; i < 400; i++)
            {
                var v = new RefreshPlanView
                {
                    IsCreate = rnd.Next(2) == 0, NonWorkshared = rnd.Next(2) == 0, Create = rnd.Next(-2, 3000), Update = rnd.Next(-2, 50), Move = rnd.Next(0, 3),
                    Resolve = rnd.Next(0, 5), Merge = rnd.Next(0, 2), PendingApplied = rnd.Next(0, 2), Unchanged = rnd.Next(0, 9000),
                    FirstIssueNo = rnd.Next(3) == 0 ? null : "JD-0001", LastIssueNo = rnd.Next(3) == 0 ? null : "JD-0009",
                    RetentionCount = rnd.Next(-1, 9), OverCap = rnd.Next(4) == 0, WouldCreate = rnd.Next(2001, 9000), OverWarn = rnd.Next(4) == 0, TotalMarkers = rnd.Next(5001, 9999),
                    Refusal = rnd.Next(5) == 0 ? "No." : null
                };
                for (int k = rnd.Next(4); k > 0; k--) v.PassedOver.Add(R(rnd.Next(2) == 0 ? "held by kdoe" : null, rnd.Next(-1, 9)));
                for (int k = rnd.Next(3); k > 0; k--) v.NotRetested.Add(R("link not loaded", rnd.Next(0, 4)));
                try
                {
                    var m = new RefreshPlanModel(v);
                    int p = v.PassedOver.Where(x => x != null && x.Count > 0).Sum(x => x.Count);
                    counts &= (Line(m, "Create") == null) == (v.Create <= 0) && (v.Create <= 0 || m.Lines.Any(l => l.Label == "Create" && l.Count == v.Create))
                        && (Line(m, "Passed over") == null) == (p == 0);
                    var all = m.Lines.Select(l => l.Label + l.Detail).Concat(new[] { m.Title, m.Footer, m.CapMessage, m.Warning, m.PrimaryText, m.RetentionText });
                    text &= all.All(t => t != null && !t.Contains('{') && !t.Contains('}') && !t.Contains("null"));
                    m.Choices();
                }
                catch (Exception) { ok = false; }
            }
            check(P + "fuzz (400 views): never throws", ok);
            check(P + "fuzz: a line is present exactly when its count is positive, with the same count", counts);
            check(P + "fuzz: no text is null or holds a stray brace or \"null\"", text);
        }
    }
}
