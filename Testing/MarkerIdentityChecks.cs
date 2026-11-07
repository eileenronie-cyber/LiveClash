using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 1: the clash id (spec 5.2, rule M3) and the issue numbers (spec 6.10).
    /// Revit-free, so both tiers run it. Red-first: see tests/evidence/clash-schedule/BUILD-LOG.md.
    /// </summary>
    public static class MarkerIdentityChecks
    {
        private const string P = "markers id: ";
        private const string Q = "markers issue: ";

        public static void RunAll(Action<string, bool?> check)
        {
            ClashIds(check);
            Collisions(check);
            Prefixes(check);
            Formatting(check);
            Moves(check);
            Renumbering(check);
            Fuzz(check);
        }

        // ------------------------------------------------------------------ the clash id

        private static void ClashIds(Action<string, bool?> check)
        {
            var id = ClashIdOf.Of("host-1", "", "other-1");
            check(P + "an id is LC- and 8 lower-case hex characters (\"" + id + "\")", Regex.IsMatch(id ?? "", "^LC-[0-9a-f]{8}$"));
            check(P + "a host pair is order-normalised: A,B and B,A give one id",
                id != "" && ClashIdOf.Of("host-2", "", "host-1") == ClashIdOf.Of("host-1", "", "host-2")
                && ClashIdOf.Of("host-2", "", "host-1") != "");
            var fwd = ClashIdOf.Of("h", "l", "o");
            var rev = ClashIdOf.Of("o", "l", "h");
            check(P + "a link pair is NOT order-normalised (host, link, other are different roles)", fwd != "" && rev != "" && fwd != rev);
            check(P + "a different triple gives a different id", id != "" && ClashIdOf.Of("host-1", "", "other-2") != id);
            check(P + "the link instance is part of the triple",
                ClashIdOf.Of("h", "l1", "o") != "" && ClashIdOf.Of("h", "l1", "o") != ClashIdOf.Of("h", "l2", "o")
                && ClashIdOf.Of("h", "l1", "o") != ClashIdOf.Of("h", "", "o"));
            check(P + "the id is the same on every call and is the short hash of the canonical triple",
                id != "" && id == ClashIdOf.Of("host-1", "", "other-1")
                && id == ClashIdOf.Prefix + ShortHash.Of(ClashIdOf.Triple("host-1", "", "other-1")));
            check(P + "the canonical triple of a host pair is order-normalised",
                ClashIdOf.Triple("b", "", "a") != "" && ClashIdOf.Triple("b", "", "a") == ClashIdOf.Triple("a", "", "b"));
            check(P + "an incomplete triple (no host or no other UniqueId) gives an empty id, never a made-up one",
                ClashIdOf.Of("", "", "b") == "" && ClashIdOf.Of("a", "", "") == "" && ClashIdOf.Of(null, null, null) == "" && ClashIdOf.Of("a", "l", null) == "");
            check(P + "the supplied hash is used (the seam the collision checks need)",
                ClashIdOf.Of("a", "", "b", s => "cafef00d") == "LC-cafef00d");
            check(P + "BaseOf strips a collision suffix and leaves a plain id alone",
                ClashIdOf.BaseOf("LC-1a2b3c4d-2") == "LC-1a2b3c4d" && ClashIdOf.BaseOf("LC-1a2b3c4d-12") == "LC-1a2b3c4d"
                && ClashIdOf.BaseOf("LC-1a2b3c4d") == "LC-1a2b3c4d" && ClashIdOf.BaseOf(null) == "");
        }

        private static ClashIdOf.Entry E(string uid, string clashId, string triple)
        {
            return new ClashIdOf.Entry { MarkerUid = uid, ClashId = clashId, Triple = triple };
        }

        private static void Collisions(Action<string, bool?> check)
        {
            const string b = "LC-deadbeef";
            var two = new[] { E("m2", b, "T1"), E("m1", b, "T2") };
            var r = ClashIdOf.ResolveCollisions(two);
            check(P + "a collision of two triples: the lowest marker UniqueId keeps the plain id, the other gets -2",
                r.Count == 1 && r.ContainsKey("m2") && r["m2"] == b + "-2");
            check(P + "...in either input order", Same(r, ClashIdOf.ResolveCollisions(Enumerable.Reverse(two).ToArray())));

            var three = new[] { E("m3", b, "T3"), E("m1", b, "T1"), E("m2", b, "T2") };
            var allSame = true; var expected = true;
            foreach (var perm in Permutations(three))
            {
                var rr = ClashIdOf.ResolveCollisions(perm);
                allSame &= Same(rr, ClashIdOf.ResolveCollisions(three));
                expected &= rr.Count == 2 && rr.ContainsKey("m2") && rr["m2"] == b + "-2" && rr.ContainsKey("m3") && rr["m3"] == b + "-3";
            }
            check(P + "three triples on one hash: plain, -2, -3 by marker UniqueId, in all 6 input orders", allSame && expected);

            var dup = ClashIdOf.ResolveCollisions(new[] { E("m1", b, "T1"), E("m2", b, "T1"), E("m3", b, "T2") });
            check(P + "two markers of ONE triple (duplicates) are not a collision; the other triple gets -2",
                dup.Count == 1 && dup.ContainsKey("m3") && dup["m3"] == b + "-2");
            check(P + "different base ids never interact",
                ClashIdOf.ResolveCollisions(new[] { E("m1", "LC-00000001", "T1"), E("m2", "LC-00000002", "T2") }).Count == 0);
            check(P + "a lone triple with a stale suffix is left alone (nothing is rewritten without a reason)",
                ClashIdOf.ResolveCollisions(new[] { E("m1", b + "-2", "T1") }).Count == 0);

            // Idempotent: apply the answer, ask again, nothing more to do.
            var applied = three.Select(e => E(e.MarkerUid, Or(r, e.MarkerUid, e.ClashId), e.Triple)).ToArray();
            var rr3 = ClashIdOf.ResolveCollisions(three);
            var applied3 = three.Select(e => E(e.MarkerUid, Or(rr3, e.MarkerUid, e.ClashId), e.Triple)).ToArray();
            check(P + "applying the answer and asking again changes nothing", applied.Length == 3 && rr3.Count == 2 && ClashIdOf.ResolveCollisions(applied3).Count == 0);

            check(P + "null and empty input never throw", NoThrow(() => { ClashIdOf.ResolveCollisions(null); ClashIdOf.ResolveCollisions(new ClashIdOf.Entry[] { null, E(null, null, null) }); })
                && ClashIdOf.ResolveCollisions(null).Count == 0);
        }

        // ------------------------------------------------------------------ prefixes and numbers

        private static void Prefixes(Action<string, bool?> check)
        {
            check(Q + "a single-word name gives its first two letters (\"jdoe\" is JD)", IssueNumbers.PrefixOf("jdoe") == "JD");
            check(Q + "a name in words gives initials, at most three",
                IssueNumbers.PrefixOf("jane.quinn") == "JQ" && IssueNumbers.PrefixOf("jane quinn doe") == "JQD" && IssueNumbers.PrefixOf("a.b.c.d") == "ABC");
            check(Q + "an e-mail style name uses the part before the @",
                IssueNumbers.PrefixOf("jdoe@example.com") == "JD" && IssueNumbers.PrefixOf("jane.quinn@x.com") == "JQ");
            check(Q + "a name with no letters, or one letter, still gets a valid prefix",
                IssueNumbers.PrefixOf("") == "XX" && IssueNumbers.PrefixOf(null) == "XX" && IssueNumbers.PrefixOf("1234") == "XX"
                && IssueNumbers.PrefixOf("_._") == "XX" && IssueNumbers.PrefixOf("x") == "XX");
            check(Q + "digits split words and are not part of a prefix (\"j2doe\" is JD)", IssueNumbers.PrefixOf("j2doe") == "JD");
            check(Q + "letters beyond ASCII are kept and upper-cased", IssueNumbers.PrefixOf("émile") == "ÉM");

            check(Q + "a free prefix is taken as it is", IssueNumbers.FreePrefix("JD", Array.Empty<string>()) == "JD" && IssueNumbers.FreePrefix("JD", null) == "JD" && IssueNumbers.FreePrefix("JD", A("KD")) == "JD");
            check(Q + "a taken prefix becomes the first free variant (JD2, JD3, ...), case-insensitively",
                IssueNumbers.FreePrefix("JD", A("JD")) == "JD2" && IssueNumbers.FreePrefix("JD", A("JD", "JD2")) == "JD3"
                && IssueNumbers.FreePrefix("JD", A("jd")) == "JD2" && IssueNumbers.FreePrefix("JD", A("JD", "JD3")) == "JD2");
        }

        private static void Formatting(Action<string, bool?> check)
        {
            check(Q + "Format pads to four digits (JD-0042)", IssueNumbers.Format("JD", 42) == "JD-0042" && IssueNumbers.Format("JD", 1) == "JD-0001");
            check(Q + "Format widens at 10000 and never wraps (counter 9999 to 10000)",
                IssueNumbers.Format("JD", 9999) == "JD-9999" && IssueNumbers.Format("JD", 10000) == "JD-10000" && IssueNumbers.Format("JD", 123456) == "JD-123456");
            check(Q + "Format of zero or a negative number is JD-0000, never a throw", IssueNumbers.Format("JD", 0) == "JD-0000" && IssueNumbers.Format("JD", -5) == "JD-0000");

            string pre; int n;
            bool ok1 = IssueNumbers.TryParse("JD-0042", out pre, out n);
            check(Q + "TryParse reads JD-0042", ok1 && pre == "JD" && n == 42);
            bool ok2 = IssueNumbers.TryParse("JD2-0007", out pre, out n);
            check(Q + "TryParse reads a variant prefix (JD2-0007)", ok2 && pre == "JD2" && n == 7);
            bool ok3 = IssueNumbers.TryParse("jd-10000", out pre, out n);
            check(Q + "TryParse upper-cases the prefix and reads a widened number", ok3 && pre == "JD" && n == 10000);
            string p2; int n2;
            var junk = A(null, "", "JD-", "-0042", "JD0042", "JD-00x2", "JD--1", "JD-0042 ", " JD-0042", "JD-99999999999", "J D-1", "1-2");
            check(Q + "TryParse rejects junk, overflow and stray spaces, and never throws",
                junk.All(j => !IssueNumbers.TryParse(j, out p2, out n2)));

            var c = new IssueNumbers.Counter { Uid = "c", UserName = "jdoe", Prefix = "JD", Next = 9999 };
            var a = IssueNumbers.Take(c); var b = IssueNumbers.Take(c);
            check(Q + "Take hands out 9999 then 10000 and advances the counter", a == "JD-9999" && b == "JD-10000" && c.Next == 10001);
            var fresh = new IssueNumbers.Counter { Uid = "c", UserName = "jdoe", Prefix = "JD" };
            check(Q + "a new counter starts at 0001", IssueNumbers.Take(fresh) == "JD-0001" && fresh.Next == 2);
            check(Q + "Take of nothing or a counter without a prefix gives null and does not throw",
                IssueNumbers.Take(null) == null && IssueNumbers.Take(new IssueNumbers.Counter()) == null);
        }

        private static IssueNumbers.Counter C(string uid, string user, string prefix)
        {
            return new IssueNumbers.Counter { Uid = uid, UserName = user, Prefix = prefix };
        }

        private static void Moves(Action<string, bool?> check)
        {
            var two = IssueNumbers.PrefixMoves(new[] { C("c1", "jdoe", "JD"), C("c2", "jane.doe", "JD") });
            check(Q + "two counters on one prefix: the higher UniqueId moves to JD2, the lower keeps JD",
                two.Count == 1 && two[0].Uid == "c2" && two[0].OldPrefix == "JD" && two[0].NewPrefix == "JD2" && two[0].UserName == "jane.doe");

            var three = new[] { C("c1", "u1", "JD"), C("c2", "u2", "JD"), C("c3", "u3", "JD") };
            bool same = true, shape = true;
            foreach (var perm in Permutations(three))
            {
                var mv = IssueNumbers.PrefixMoves(perm).OrderBy(m => m.Uid, StringComparer.Ordinal).ToList();
                shape &= mv.Count == 2 && mv[0].Uid == "c2" && mv[0].NewPrefix == "JD2" && mv[1].Uid == "c3" && mv[1].NewPrefix == "JD3";
                same &= perm.Length == 3;
            }
            check(Q + "three counters on one prefix end as JD, JD2, JD3 in every order of input", same && shape);

            var taken = IssueNumbers.PrefixMoves(new[] { C("c1", "u1", "JD"), C("c2", "u2", "JD"), C("c9", "u9", "JD2") });
            check(Q + "a variant already held by another counter is skipped (JD3, not JD2)", taken.Count == 1 && taken[0].Uid == "c2" && taken[0].NewPrefix == "JD3");

            check(Q + "no shared prefix, no move", IssueNumbers.PrefixMoves(new[] { C("c1", "u1", "JD"), C("c2", "u2", "KD") }).Count == 0);
            check(Q + "prefixes are compared without case", IssueNumbers.PrefixMoves(new[] { C("c1", "u1", "jd"), C("c2", "u2", "JD") }).Count == 1);
            check(Q + "counters with no prefix are ignored", IssueNumbers.PrefixMoves(new[] { C("c1", "u1", ""), C("c2", "u2", null), C("c3", "u3", "") }).Count == 0);
            check(Q + "null input never throws", NoThrow(() => { IssueNumbers.PrefixMoves(null); IssueNumbers.PrefixMoves(new IssueNumbers.Counter[] { null }); }));
        }

        private static IssueNumbers.IssueMarker M(string uid, string no, string by)
        {
            return new IssueNumbers.IssueMarker { MarkerUid = uid, IssueNo = no, ReportedBy = by };
        }

        private static void Renumbering(Action<string, bool?> check)
        {
            var counters = new[] { C("c1", "jdoe", "JD"), C("c2", "jane.doe", "JD") };
            var markers = new[]
            {
                M("m1", "JD-0001", "jdoe"), M("m2", "JD-0001", "jane.doe"), M("m3", "JD-0007", "jane.doe"),
                M("m4", "KJ-0003", "jane.doe"), M("m5", "JD-0002", "jdoe")
            };
            var plan = IssueNumbers.RenumberPlan(counters, markers);
            var byUid = plan.ToDictionary(p => p.MarkerUid, p => p);
            check(Q + "the window: only the moved user's markers on the old prefix take the new prefix, keeping their number",
                plan.Count == 2 && byUid.ContainsKey("m2") && byUid["m2"].NewIssueNo == "JD2-0001" && byUid.ContainsKey("m3") && byUid["m3"].NewIssueNo == "JD2-0007");
            check(Q + "...the old number is recorded for MergedIssueNos", byUid.ContainsKey("m2") && byUid["m2"].OldIssueNo == "JD-0001");
            check(Q + "...the keeper's markers and a marker on an older prefix are untouched", !byUid.ContainsKey("m1") && !byUid.ContainsKey("m5") && !byUid.ContainsKey("m4"));
            var after = markers.Select(m => { IssueNumbers.Renumber p; return byUid.TryGetValue(m.MarkerUid, out p) ? p.NewIssueNo : m.IssueNo; }).ToList();
            check(Q + "after the plan every issue number is distinct", after.Distinct(StringComparer.OrdinalIgnoreCase).Count() == after.Count);
            check(Q + "no collision, no renumbering", IssueNumbers.RenumberPlan(new[] { C("c1", "u1", "JD"), C("c2", "u2", "KD") }, markers).Count == 0);

            // One user on two PCs (W8): same name, same prefix. Only the duplicated number moves.
            var w8c = new[] { C("c1", "jdoe", "JD"), C("c2", "jdoe", "JD") };
            var w8m = new[] { M("m1", "JD-0001", "jdoe"), M("m2", "JD-0001", "jdoe"), M("m3", "JD-0002", "jdoe"), M("m4", "JD-0003", "jdoe") };
            var w8 = IssueNumbers.RenumberPlan(w8c, w8m);
            check(Q + "one user on two PCs: only the higher-UniqueId marker of a duplicated number moves (JD-0001 -> JD2-0001)",
                w8.Count == 1 && w8[0].MarkerUid == "m2" && w8[0].NewIssueNo == "JD2-0001" && w8[0].OldIssueNo == "JD-0001");
            check(Q + "null input never throws", NoThrow(() => { IssueNumbers.RenumberPlan(null, null); IssueNumbers.RenumberPlan(counters, new IssueNumbers.IssueMarker[] { null, M(null, null, null) }); }));
        }

        // ------------------------------------------------------------------ fuzz

        private static void Fuzz(Action<string, bool?> check)
        {
            var rnd = new Random(20261031);
            string[] users = { "jdoe", "jdan", "jdee", "j.dunn", "jdz@x.com" };      // all give JD
            int bad = 0, cases = 300, badOrder = 0;
            for (int i = 0; i < cases; i++)
            {
                int nUsers = 2 + rnd.Next(users.Length - 1);
                var picked = users.OrderBy(_ => rnd.Next()).Take(nUsers).ToList();
                var counters = new List<IssueNumbers.Counter>();
                var markers = new List<IssueNumbers.IssueMarker>();
                int muid = 0;
                for (int u = 0; u < picked.Count; u++)
                {
                    var c = new IssueNumbers.Counter { Uid = "c" + rnd.Next(1000, 9999) + "-" + u, UserName = picked[u], Prefix = IssueNumbers.PrefixOf(picked[u]) };
                    counters.Add(c);
                    int k = rnd.Next(0, 7);
                    for (int j = 0; j < k; j++) markers.Add(M("m" + (muid++).ToString("D4", CultureInfo.InvariantCulture) + rnd.Next(10), IssueNumbers.Take(c), picked[u]));
                }
                var plan = IssueNumbers.RenumberPlan(counters, markers);
                var moves = IssueNumbers.PrefixMoves(counters);
                var movedUsers = new HashSet<string>(moves.Select(m => m.UserName));
                var final = markers.Select(m => { var p = plan.FirstOrDefault(x => x.MarkerUid == m.MarkerUid); return p != null ? p.NewIssueNo : m.IssueNo; }).ToList();
                var newPrefixes = counters.Select(c => { var mv = moves.FirstOrDefault(x => x.Uid == c.Uid); return mv != null ? mv.NewPrefix : c.Prefix; }).ToList();
                bool distinctPrefixes = newPrefixes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == newPrefixes.Count;
                bool distinctNumbers = final.Distinct(StringComparer.OrdinalIgnoreCase).Count() == final.Count;
                bool onlyMoved = plan.All(p => movedUsers.Contains(markers.First(m => m.MarkerUid == p.MarkerUid).ReportedBy));
                bool countRight = plan.Count == markers.Count(m => movedUsers.Contains(m.ReportedBy));
                if (!(distinctPrefixes && distinctNumbers && onlyMoved && countRight)) bad++;

                var shuffled = IssueNumbers.RenumberPlan(counters.OrderBy(_ => rnd.Next()).ToList(), markers.OrderBy(_ => rnd.Next()).ToList());
                if (!SamePlan(plan, shuffled)) badOrder++;
            }
            check(Q + "fuzz: users with the same letters in any order of first use end with distinct prefixes, distinct numbers, and only the moved users' markers renumbered (" + cases + " cases, seed 20261031)", bad == 0);
            check(Q + "fuzz: the renumbering plan is the same whatever order the records are read in", badOrder == 0);

            int thrown = 0, tooShort = 0;
            string alphabet = "ab. _-@1é中\t";
            for (int i = 0; i < 400; i++)
            {
                var s = new string(Enumerable.Range(0, rnd.Next(0, 12)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
                try { var p = IssueNumbers.PrefixOf(s); if (p == null || p.Length < 2 || p.Length > 3) tooShort++; }
                catch (Exception) { thrown++; }
            }
            check(Q + "fuzz: a prefix is 2 or 3 characters for any text and never throws (400 strings)", thrown == 0 && tooShort == 0);
        }

        // ------------------------------------------------------------------ helpers

        private static string[] A(params string[] items)
        {
            return items;
        }

        private static T Or<T>(Dictionary<string, T> d, string key, T fallback)
        {
            T v;
            return d.TryGetValue(key, out v) ? v : fallback;
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }

        private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            return a.Count == b.Count && a.All(kv => b.ContainsKey(kv.Key) && b[kv.Key] == kv.Value);
        }

        private static bool SamePlan(List<IssueNumbers.Renumber> a, List<IssueNumbers.Renumber> b)
        {
            var x = a.OrderBy(p => p.MarkerUid, StringComparer.Ordinal).Select(p => p.MarkerUid + ">" + p.NewIssueNo).ToList();
            var y = b.OrderBy(p => p.MarkerUid, StringComparer.Ordinal).Select(p => p.MarkerUid + ">" + p.NewIssueNo).ToList();
            return x.SequenceEqual(y);
        }

        internal static IEnumerable<T[]> Permutations<T>(T[] items)
        {
            if (items.Length <= 1) { yield return items.ToArray(); yield break; }
            for (int i = 0; i < items.Length; i++)
            {
                var rest = items.Where((_, idx) => idx != i).ToArray();
                foreach (var p in Permutations(rest))
                    yield return new[] { items[i] }.Concat(p).ToArray();
            }
        }
    }
}
