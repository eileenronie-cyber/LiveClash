using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 1: the Group column label (spec 6.10, Q21) and the Refresh summary text
    /// (spec 10.3, rule C5). Revit-free. Red-first: BUILD-LOG.md.
    /// </summary>
    public static class MarkerTextChecks
    {
        private const string G = "markers group: ";
        private const string S = "markers summary: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Groups(check);
            Summary(check);
            SummaryMore(check);
        }

        // ------------------------------------------------------------------ group label

        private static void Groups(Action<string, bool?> check)
        {
            check(G + "a pair alone has an empty label", GroupLabel.Of(A("JD-0042")) == "" && GroupLabel.Of(Array.Empty<string>()) == "" && GroupLabel.Of(null) == "");
            check(G + "two members: the lowest number and +1", GroupLabel.Of(A("JD-0043", "JD-0042")) == "JD-0042 (+1)");
            var fourteen = Enumerable.Range(42, 14).Select(i => IssueNumbers.Format("JD", i)).ToList();
            check(G + "fourteen members: JD-0042 (+13)", GroupLabel.Of(fourteen) == "JD-0042 (+13)");
            check(G + "numbers compare as numbers: 9 before 10, and 9999 before 10000",
                GroupLabel.Of(A("JD-0010", "JD-0009")) == "JD-0009 (+1)" && GroupLabel.Of(A("JD-10000", "JD-9999")) == "JD-9999 (+1)");
            check(G + "across prefixes: by prefix (ordinal) then number; JD sorts before JD2 and KD",
                GroupLabel.Of(A("KD-0001", "JD-0099")) == "JD-0099 (+1)" && GroupLabel.Of(A("JD2-0001", "JD-0099")) == "JD-0099 (+1)");
            bool same = true;
            foreach (var perm in MarkerIdentityChecks.Permutations(A("JD-0007", "KD-0002", "JD2-0001", "JD-0003")))
                same &= GroupLabel.Of(perm) == "JD-0003 (+3)";
            check(G + "the label does not depend on the order of the members (all 24 orders)", same);
            check(G + "members with no readable number are counted but cannot be the lowest",
                GroupLabel.Of(A("", "JD-0005", null)) == "JD-0005 (+2)" && GroupLabel.Of(A("junk", "JD-0005")) == "JD-0005 (+1)");
            check(G + "a group with no readable number at all has no label", GroupLabel.Of(A("", null, "x")) == "");
            check(G + "a lower-case number is read as upper case", GroupLabel.Of(A("jd-0002", "JD-0001")) == "JD-0001 (+1)");
            check(G + "the same number twice counts twice (a merged pair keeps its row)", GroupLabel.Of(A("JD-0001", "JD-0001")) == "JD-0001 (+1)");
        }

        // ------------------------------------------------------------------ summary

        private static MarkerSummary.Reason R(string text, int n)
        {
            return new MarkerSummary.Reason { Text = text, Count = n };
        }

        private static MarkerSummary.Counts Example()
        {
            return new MarkerSummary.Counts
            {
                Clashes = 813, Unchanged = 786, Created = 6, Updated = 4, Resolved = 2, Merged = 1, Moved = 2, PendingApplied = 1,
                PassedOver = { R("held by kdoe", 9), R("changed in central", 2), R("inside a group", 1) },
                NotRetested = { R("link not loaded", 3) }
            };
        }

        private static void Summary(Action<string, bool?> check)
        {
            var c = Example();
            check(S + "up to date is unchanged + created + updated + resolved (798)", MarkerSummary.UpToDate(c) == 798);
            check(S + "the counts balance when clashes = up to date + passed over + not re-tested (813)", MarkerSummary.Balanced(c));
            c.Clashes = 812;
            check(S + "...and do not when they differ (812)", !MarkerSummary.Balanced(c));
            c.Clashes = 813;
            c.PassedOver[0].Count = 8;
            check(S + "...nor when a reason is short", !MarkerSummary.Balanced(c));

            check(S + "the example line, as the spec words it (10.3)",
                MarkerSummary.Describe(Example()) ==
                "Clash markers: 813 clashes. 798 up to date (786 unchanged, 6 created, 4 updated, 2 resolved), 1 merged, 2 moved, 1 pending applied, "
                + "12 passed over (9 held by kdoe, 2 changed in central, 1 inside a group), 3 not re-tested (link not loaded).");

            check(S + "parts with a zero count are left out",
                MarkerSummary.Describe(new MarkerSummary.Counts { Clashes = 5, Unchanged = 5 }) == "Clash markers: 5 clashes. 5 up to date (5 unchanged).");
            check(S + "one clash reads \"1 clash\"",
                MarkerSummary.Describe(new MarkerSummary.Counts { Clashes = 1, Created = 1 }) == "Clash markers: 1 clash. 1 up to date (1 created).");

            var sorted = Example();
            sorted.PassedOver = new List<MarkerSummary.Reason> { R("inside a group", 1), R("zeta", 2), R("alpha", 2), R("none", 0), R("held by kdoe", 9) };
            sorted.Clashes = 798 + 14 + 3;
            check(S + "reasons are listed by count (most first) then by text, and a zero reason is dropped",
                MarkerSummary.Describe(sorted).Contains("14 passed over (9 held by kdoe, 2 alpha, 2 zeta, 1 inside a group)")
                && MarkerSummary.Describe(sorted).IndexOf("none", StringComparison.Ordinal) < 0);

            check(S + "no clashes at all says so (10.2)", MarkerSummary.Describe(new MarkerSummary.Counts()) == "No clashes to put in a schedule.");
            check(S + "a null count never throws", NoThrow(() => { MarkerSummary.Describe(null); MarkerSummary.Balanced(null); MarkerSummary.UpToDate(null); MarkerSummary.SetLine(null); })
                && MarkerSummary.Describe(null) == "" && !MarkerSummary.Balanced(null) && MarkerSummary.UpToDate(null) == 0 && MarkerSummary.SetLine(null) == "");
            check(S + "null reason lists never throw", NoThrow(() => MarkerSummary.Describe(new MarkerSummary.Counts { Clashes = 2, Unchanged = 2, PassedOver = null, NotRetested = null })));

            check(S + "a set line: in your panel, up to date, and how many more are not live on this seat",
                MarkerSummary.SetLine(new MarkerSummary.SetCounts { Name = "MEP vs STR", InPanel = 120, UpToDate = 120, NotLive = 14 })
                == "MEP vs STR: 120 in your panel, 120 markers up to date; 14 more not live on this seat.");
            check(S + "...without the 'not live' part when none, and singular for one",
                MarkerSummary.SetLine(new MarkerSummary.SetCounts { Name = "MEP vs STR", InPanel = 1, UpToDate = 1 }) == "MEP vs STR: 1 in your panel, 1 marker up to date.");
            check(S + "a set that is off on this seat says its markers were not re-tested",
                MarkerSummary.SetLine(new MarkerSummary.SetCounts { Name = "MEP vs MEP", SetOff = true, NotRetested = 30 })
                == "MEP vs MEP (set off on this seat): 30 markers not re-tested.");
            check(S + "nothing to do names the marker count (no transaction was opened)", MarkerSummary.NothingToDo(140) == "Markers up to date (140).");
        }

        // ------------------------------------------------------------------ slice 2 additions

        private static void SummaryMore(Action<string, bool?> check)
        {
            var notMarked = new MarkerSummary.Counts { Clashes = 5, Unchanged = 2, NotMarked = { R("over the cap of 2,000", 3) } };
            check(S + "not marked: creations left out are in the balance and named with their reason (A4)",
                MarkerSummary.Balanced(notMarked)
                && MarkerSummary.Describe(notMarked) == "Clash markers: 5 clashes. 2 up to date (2 unchanged), 3 not marked (over the cap of 2,000).");
            notMarked.NotMarked[0].Count = 2;
            check(S + "not marked: a short count does not balance", !MarkerSummary.Balanced(notMarked));

            var removed = new MarkerSummary.Counts { Clashes = 4, Unchanged = 2, Removed = 2 };
            check(S + "removed: deleted markers are in the balance, said after the up-to-date part, and not counted as up to date",
                MarkerSummary.Balanced(removed) && MarkerSummary.UpToDate(removed) == 2
                && MarkerSummary.Describe(removed) == "Clash markers: 4 clashes. 2 up to date (2 unchanged), 2 removed.");

            var lost = new MarkerSummary.Counts { Clashes = 2, Unchanged = 2, Merged = 1, LostDecisions = 1 };
            var lost2 = new MarkerSummary.Counts { Clashes = 2, Unchanged = 2, LostDecisions = 2 };
            check(S + "lost decisions: a merge that did not keep a decision says so, singular and plural, and they are notes outside the balance (A6)",
                MarkerSummary.Balanced(lost) && MarkerSummary.Describe(lost) == "Clash markers: 2 clashes. 2 up to date (2 unchanged), 1 merged, 1 merge decision not kept (see the Record)."
                && MarkerSummary.Describe(lost2).EndsWith("2 merge decisions not kept (see the Record).", StringComparison.Ordinal));

            var all = new MarkerSummary.Counts
            {
                Clashes = 6, Unchanged = 1, Created = 1, Removed = 1, Merged = 1, Moved = 1, PendingApplied = 1, LostDecisions = 1,
                PassedOver = { R("held by kdoe", 1) }, NotRetested = { R("rule differs", 1) }, NotMarked = { R("no position", 1) }
            };
            check(S + "the full line, in the spec order, with every part present",
                MarkerSummary.Balanced(all) && MarkerSummary.Describe(all) ==
                "Clash markers: 6 clashes. 2 up to date (1 unchanged, 1 created), 1 removed, 1 merged, 1 moved, 1 pending applied, 1 merge decision not kept (see the Record), "
                + "1 passed over (held by kdoe), 1 not re-tested (rule differs), 1 not marked (no position).");
            check(S + "nothing at all, including nothing removed or left out, still says there are no clashes",
                MarkerSummary.Describe(new MarkerSummary.Counts()) == "No clashes to put in a schedule."
                && MarkerSummary.Describe(new MarkerSummary.Counts { Removed = 1, Clashes = 1 }) == "Clash markers: 1 clash. 1 removed.");
        }

        private static string[] A(params string[] items)
        {
            return items;
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }
    }
}
