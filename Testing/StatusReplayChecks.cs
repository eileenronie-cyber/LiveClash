using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The triage ledger's replay: an append-only file in, the decisions in force out.
    ///
    /// WHY THIS EXISTS. Two writers share this file — a colleague, or a second Revit
    /// session — so events arrive INTERLEAVED, and every bug this code has had was an
    /// ordering bug rather than a parsing one. Both were invisible to every check in the
    /// suite, because nothing could reach the replay at all: it was inline in LoadFor,
    /// reachable only through a path, and the in-Revit tier deliberately refuses to write
    /// real triage files. So the interesting cases were the untestable ones.
    ///
    /// These assertions feed the SAME sequence in different orders and require the same
    /// answer. That is the actual contract — not "does it parse", but "does the file's
    /// order stop mattering" — and it is why they are written as orderings of one history
    /// rather than as separate scenarios.
    ///
    /// Revit-free, like FilterLogicChecks, so it runs on a build machine with no Revit
    /// installed as well as inside one.
    /// </summary>
    public static class StatusReplayChecks
    {
        private const string K = "100|0|200";
        private const string J = "300|0|400";

        /// <summary>One event line in the store's tab-separated format.</summary>
        private static string L(string key, string ev, string isoUtc)
        {
            return string.Join(ClashStatusReplay.Sep.ToString(),
                new[] { key, ev, isoUtc, "tester", "" });
        }

        private static Dictionary<string, ClashStatusEntry> Replay(params string[] lines)
        {
            var byKey = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int e, l, r;
            ClashStatusReplay.Replay(lines, byKey, out e, out l, out r);
            return byKey;
        }

        private static ClashStatusEntry One(Dictionary<string, ClashStatusEntry> m, string key)
        {
            ClashStatusEntry e;
            return m.TryGetValue(key, out e) ? e : null;
        }

        public static void RunAll(Action<string, bool?> check)
        {
            const string T10 = "2026-09-08T10:00:00.0000000Z";
            const string T1030 = "2026-09-08T10:30:00.0000000Z";
            const string T11 = "2026-09-08T11:00:00.0000000Z";

            // ---------------------------------------------------------- decisions

            var latest = Replay(
                L(K, "Pending", T10),
                L(K, "Coordinated", T11));
            check("replay: the newest decision wins",
                One(latest, K) != null && One(latest, K).Kind == ClashStatusKind.Coordinated);

            // The same two events, written the other way round. A second writer's older
            // event arriving later must not undo the newer one.
            var reversed = Replay(
                L(K, "Coordinated", T11),
                L(K, "Pending", T10));
            check("replay: an older decision appended LATER does not win",
                One(reversed, K) != null && One(reversed, K).Kind == ClashStatusKind.Coordinated);

            check("replay: 'New' is the absence of a decision, so it is dropped",
                !Replay(L(K, "New", T10)).ContainsKey(K));

            // ---------------------------------------------------------------- Gone
            //
            // Four orderings, one history. The first is the tidy case that always
            // worked; the next two are what interleaving actually produces and both
            // used to fail; the last is the one that must NOT be marked resolved.

            var natural = Replay(
                L(K, "Coordinated", T10),
                L(K, "Gone", T11));
            check("gone: applied when it follows its decision",
                One(natural, K) != null && One(natural, K).Gone);

            var early = Replay(
                L(K, "Gone", T11),
                L(K, "Coordinated", T10));
            check("gone: applied when it PRECEDES its decision in the file",
                One(early, K) != null && One(early, K).Gone);

            var superseded = Replay(
                L(K, "Coordinated", T10),
                L(K, "Gone", T11),
                L(K, "Coordinated", T1030));
            check("gone: survives a newer decision replayed after it",
                One(superseded, K) != null && One(superseded, K).Gone);

            var reopened = Replay(
                L(K, "Coordinated", T10),
                L(K, "Gone", T1030),
                L(K, "Coordinated", T11));
            check("gone: a decision made AFTER it reopens the pair, so it is NOT resolved",
                One(reopened, K) != null && !One(reopened, K).Gone);

            check("gone: with no decision to attach to, it resolves nothing",
                !Replay(L(K, "Gone", T11)).ContainsKey(K));

            // The latest Gone is the one that counts, whichever order they arrive in.
            var twoGones = Replay(
                L(K, "Coordinated", T1030),
                L(K, "Gone", T11),
                L(K, "Gone", T10));
            check("gone: the LATEST gone wins, not the last one in the file",
                One(twoGones, K) != null && One(twoGones, K).Gone
                && One(twoGones, K).GoneUtc.ToUniversalTime().Hour == 11);

            // ------------------------------------------------------------ isolation

            var twoKeys = Replay(
                L(K, "Coordinated", T10),
                L(J, "Pending", T10),
                L(K, "Gone", T11));
            check("replay: a gone on one key leaves another alone",
                One(twoKeys, K) != null && One(twoKeys, K).Gone
                && One(twoKeys, J) != null && !One(twoKeys, J).Gone);

            // -------------------------------------------------------------- format

            var counted = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int events, legacy, repaired;
            ClashStatusReplay.Replay(
                new[]
                {
                    "# a comment",
                    "",
                    "100|0|200|Coordinated|whenever",   // the broken pipe format
                    L(K, "Coordinated", T10)
                },
                counted, out events, out legacy, out repaired);
            check("format: a comment and a blank line are not events", events == 1);
            check("format: a pipe-separated legacy line is counted and skipped", legacy == 1);
            check("format: ...and does not load as a phantom decision", counted.Count == 1);

            Detail(check, T10, T11);
        }

        /// <summary>Fields joined with the ledger's separator.</summary>
        private static string F(params string[] fields) { return string.Join(ClashStatusReplay.Sep.ToString(), fields); }

        /// <summary>
        /// The element detail on a decision line: loaded when present, carried forward by an older-format
        /// line, and RECOVERED from the ten-field lines the char+long bug wrote (host id written 18 too
        /// high and glued to the host name, both tabs swallowed). That repair had no check of its own.
        /// </summary>
        private static void Detail(Action<string, bool?> check, string t10, string t11)
        {
            var healthy = Replay(F(K, "Pending", t10, "tester", "a note", "74691431", "LV Ladder", "Cable Trays", "5512", "Duct 9", "Ducts", "MECH"));
            var h = One(healthy, K);
            check("detail: a twelve-field line loads the host, the other element and the model",
                h != null && h.HasDetail && h.Note == "a note" && h.HostId == 74691431 && h.HostName == "LV Ladder"
                && h.HostCategory == "Cable Trays" && h.OtherId == 5512 && h.OtherName == "Duct 9"
                && h.OtherCategory == "Ducts" && h.ModelName == "MECH");

            var carried = Replay(
                F(K, "Pending", t10, "tester", "", "74691431", "LV Ladder", "Cable Trays", "5512", "Duct 9", "Ducts", "MECH"),
                L(K, "Coordinated", t11));
            var c = One(carried, K);
            check("detail: a newer line without detail keeps the detail the older decision had",
                c != null && c.Kind == ClashStatusKind.Coordinated && c.HostId == 74691431 && c.OtherName == "Duct 9" && c.ModelName == "MECH");

            // The same two lines in the OTHER file order - what a second seat appending produces. The
            // detail used to be lost here, because it was carried forward only from what had already been read.
            var carriedReversed = One(Replay(
                L(K, "Coordinated", t11),
                F(K, "Pending", t10, "tester", "", "74691431", "LV Ladder", "Cable Trays", "5512", "Duct 9", "Ducts", "MECH")), K);
            check("detail: ...and keeps it when the older detailed line comes LATER in the file",
                carriedReversed != null && carriedReversed.Kind == ClashStatusKind.Coordinated && carriedReversed.HostId == 74691431
                && carriedReversed.OtherName == "Duct 9" && carriedReversed.ModelName == "MECH");

            var newestDetail = One(Replay(
                F(K, "Pending", "2026-09-08T09:00:00.0000000Z", "t", "", "1", "Old", "c", "2", "o", "c", "OLD"),
                L(K, "Coordinated", t11),
                F(K, "Ignored", t10, "t", "", "3", "Newer", "c", "4", "o", "c", "NEWER")), K);
            check("detail: a winning line without detail takes the NEWEST detail on record, not the first one read",
                newestDetail != null && newestDetail.Kind == ClashStatusKind.Coordinated && newestDetail.HostName == "Newer" && newestDetail.ModelName == "NEWER");

            var none = One(Replay(L(K, "Pending", t10)), K);
            check("detail: a five-field line has no detail", none != null && !none.HasDetail && none.HostName == null);

            // The damaged shape: note held "<hostId+18><hostName>", everything after sits two places early.
            var damagedLines = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int events, legacy, repaired;
            ClashStatusReplay.Replay(new[] { F(K, "Coordinated", t10, "ADMIN", "74691449LV Ladder [74691431]", "Cable Trays", "5512", "Duct 9", "Ducts", "MECH") },
                damagedLines, out events, out legacy, out repaired);
            var d = One(damagedLines, K);
            check("detail: a line damaged by the char+long bug is repaired - the real host id is 18 lower",
                d != null && repaired == 1 && d.HostId == 74691431);
            check("detail: ...the glued text is the host name, the note was never a note, and the rest shifts back",
                d != null && d.HostName == "LV Ladder [74691431]" && d.Note == "" && d.HostCategory == "Cable Trays"
                && d.OtherId == 5512 && d.OtherName == "Duct 9" && d.OtherCategory == "Ducts" && d.ModelName == "MECH");

            var tooBig = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            ClashStatusReplay.Replay(new[] { F(K, "Coordinated", t10, "ADMIN", "99999999999999999999999 not an id", "a", "b", "c", "d", "e") },
                tooBig, out events, out legacy, out repaired);
            var tb = One(tooBig, K);
            check("detail: digits too long to be an id are left as the note, not 'repaired' into nonsense",
                tb != null && repaired == 0 && tb.Note == "99999999999999999999999 not an id" && !tb.HasDetail);

            var tenNotDamaged = One(Replay(F(K, "Pending", t10, "ADMIN", "plain note", "a", "b", "c", "d", "e")), K);
            check("detail: a ten-field line whose note does not start with a digit is not treated as damaged",
                tenNotDamaged != null && tenNotDamaged.Note == "plain note" && !tenNotDamaged.HasDetail);

            var unknown = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            ClashStatusReplay.Replay(new[] { L(K, "Bogus", t10), L(J, "Pending", t10) }, unknown, out events, out legacy, out repaired);
            check("format: an event name nobody writes is skipped, not loaded and not counted",
                !unknown.ContainsKey(K) && unknown.ContainsKey(J) && events == 1);

            var badDate = Replay(L(K, "Pending", t10), L(K, "Ignored", "not a date"));
            check("format: a decision with an unreadable time counts as the oldest, so it cannot override a dated one",
                One(badDate, K) != null && One(badDate, K).Kind == ClashStatusKind.Pending);

            var nulls = new Dictionary<string, ClashStatusEntry>();
            ClashStatusReplay.Replay(null, nulls, out events, out legacy, out repaired);
            ClashStatusReplay.Replay(new[] { L(K, "Pending", t10) }, null, out events, out legacy, out repaired);
            check("format: no lines or no destination is a no-op, not a crash", nulls.Count == 0 && events == 0);
        }
    }
}
