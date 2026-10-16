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
        }
    }
}
