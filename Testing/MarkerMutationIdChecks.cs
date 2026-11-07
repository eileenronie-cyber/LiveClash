using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 8: the issue-number prefix and renumbering rules and the text of a clash id's triple, which the second
    /// Stryker run showed no check pinned down. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationIdChecks
    {
        private const string P = "markers mutation ids: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Prefixes(check);
            Moves(check);
            Renumbering(check);
            Triples(check);
        }

        private static string[] Texts(params string[] items) { return items; }
        private static IssueNumbers.Counter C(string uid, string user, string prefix) { return new IssueNumbers.Counter { Uid = uid, UserName = user, Prefix = prefix, Next = 1 }; }
        private static IssueNumbers.IssueMarker M(string uid, string issue, string by) { return new IssueNumbers.IssueMarker { MarkerUid = uid, IssueNo = issue, ReportedBy = by }; }

        // ------------------------------------------------------------------ prefixes and the text of a number

        private static void Prefixes(Action<string, bool?> check)
        {
            check(P + "a name that starts with '@' has nothing before it, so it gets 'XX'; a two-letter name is its own prefix; a one-letter name gets an X",
                IssueNumbers.PrefixOf("@jdoe") == "XX" && IssueNumbers.PrefixOf("jd") == "JD" && IssueNumbers.PrefixOf("j") == "JX" && IssueNumbers.PrefixOf("jdoe@x.org") == "JD" && IssueNumbers.PrefixOf(null) == "XX");
            check(P + "a number with no prefix is written with the dash and four digits", IssueNumbers.Format(null, 5) == "-0005" && IssueNumbers.Format("JD", -3) == "JD-0000" && IssueNumbers.Format("JD", 12345) == "JD-12345");
        }

        // ------------------------------------------------------------------ who keeps a prefix

        private static void Moves(Action<string, bool?> check)
        {
            var two = new[] { C("u3", "x", "CD"), C("u4", "y", "CD"), C("u1", "p", "AB"), C("u2", "q", "AB") };
            var forward = IssueNumbers.PrefixMoves(two).Select(m => m.Uid + ">" + m.NewPrefix).ToList();
            var backward = IssueNumbers.PrefixMoves(Enumerable.Reverse(two)).Select(m => m.Uid + ">" + m.NewPrefix).ToList();
            check(P + "the prefix moves come out by prefix, the lowest UniqueId of each prefix keeping it, whatever the list order", forward.SequenceEqual(Texts("u2>AB2", "u4>CD2")) && backward.SequenceEqual(forward));

            var noUid = IssueNumbers.PrefixMoves(new[] { C(null, "z", "JD"), C("A", "b", "JD") });
            check(P + "a record with no UniqueId sorts before any name and keeps the prefix", noUid.Count == 1 && noUid[0].Uid == "A" && noUid[0].UserName == "b");
            var sameUid = IssueNumbers.PrefixMoves(new[] { C("u1", "b", "JD"), C("u1", "a", "JD") });
            check(P + "...two records with one UniqueId are told apart by user name, the lower keeping the prefix", sameUid.Count == 1 && sameUid[0].UserName == "b");
            var noName = IssueNumbers.PrefixMoves(new[] { C("u1", null, "JD"), C("u1", "A", "JD") });
            check(P + "...and a missing user name sorts first", noName.Count == 1 && noName[0].UserName == "A");
        }

        // ------------------------------------------------------------------ renumbering after a move

        private static void Renumbering(Action<string, bool?> check)
        {
            var collide = new[] { C("u1", "jdoe", "JD"), C("u2", "kdoe", "JD") };
            check(P + "with no markers to look at the plan is empty, not a failure", IssueNumbers.RenumberPlan(collide, null).Count == 0 && IssueNumbers.RenumberPlan(new[] { C("u1", "jdoe", "JD") }, new[] { M("m1", "JD-0001", "jdoe") }).Count == 0);

            var three = new[] { C("u3", "jdoe", "JD"), C("u1", "jdoe", "JD"), C("u2", "jdoe", "JD") };
            var threeMarkers = new[] { M("m3", "JD-0005", "jdoe"), M("m1", "JD-0005", "jdoe"), M("m2", "JD-0005", "jdoe") };
            var plan = IssueNumbers.RenumberPlan(three, threeMarkers);
            check(P + "one user with three counters on one prefix: of three markers numbered alike the lowest stays and the next two take the other prefixes in UniqueId order, each once",
                plan.Select(r => r.MarkerUid + ">" + r.NewIssueNo).SequenceEqual(Texts("m2>JD2-0005", "m3>JD3-0005")));

            var friends = new[] { C("u1", "ab", "C"), C("u2", "ab", "C"), C("u3", "a", "BC"), C("u4", "a", "BC") };
            var friendMarkers = new[] { M("m1", "C-0001", "ab"), M("m2", "C-0001", "ab"), M("m3", "BC-0001", "a"), M("m4", "BC-0001", "a") };
            var both = IssueNumbers.RenumberPlan(friends, friendMarkers);
            check(P + "two users whose name and prefix would read alike if run together ('ab' and 'C', 'a' and 'BC') are each renumbered", both.Select(r => r.MarkerUid + ">" + r.NewIssueNo).SequenceEqual(Texts("m2>C2-0001", "m4>BC2-0001")));
        }

        // ------------------------------------------------------------------ the text of a pair's identity

        private static void Triples(Action<string, bool?> check)
        {
            check(P + "a pair with no link is written host and other in ordinal order around '||', and one with a link as host, link and other around single bars",
                ClashIdOf.Triple("b", "", "a") == "a||b" && ClashIdOf.Triple("a", "", "b") == "a||b" && ClashIdOf.Triple("a", "l", "b") == "a|l|b" && ClashIdOf.Triple("b", "L", "a") == "b|L|a");

            var entries = new[]
            {
                new ClashIdOf.Entry { MarkerUid = "m5", ClashId = "ABC123", Triple = "x" }, new ClashIdOf.Entry { MarkerUid = "m2", ClashId = "ABC123", Triple = "x" },
                new ClashIdOf.Entry { MarkerUid = "m3", ClashId = "ABC123", Triple = "y" }
            };
            var fix = ClashIdOf.ResolveCollisions(entries);
            check(P + "of two pairs that share an id, the one whose lowest marker UniqueId is lower keeps it and the other takes '-2'", fix.Count == 1 && fix["m3"] == "ABC123-2");
        }
    }
}
