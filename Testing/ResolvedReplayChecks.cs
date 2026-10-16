using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The resolved ledger's replay, and specifically RETRACTION.
    ///
    /// WHY THIS EXISTS. The ledger was written to by a path that could be wrong —
    /// PairVerifier.VerifyLink read solids at Medium detail and returned Resolved from a
    /// test that never ran — so entries on disk claim fixes that never happened, and a
    /// correction pass has to be able to withdraw them. The file is append-only, so a
    /// withdrawal is another line rather than an edit, which puts this code in exactly the
    /// position the triage ledger was in: the interesting failures are ORDERING failures,
    /// not parsing ones. A retraction can legitimately reach the file BEFORE the
    /// occurrence it withdraws — a second session appends while this one is mid-write, or
    /// the file sits on a shared folder.
    ///
    /// So these assertions feed one history in different orders and require the same
    /// answer, the same contract StatusReplayChecks asserts for the other ledger.
    ///
    /// Revit-free, so both tiers run it.
    /// </summary>
    public static class ResolvedReplayChecks
    {
        private const string K = "100|0|200";
        private const string J = "300|0|400";

        private const string T10 = "2026-09-08T10:00:00.0000000Z";
        private const string T11 = "2026-09-08T11:00:00.0000000Z";
        private const string T12 = "2026-09-08T12:00:00.0000000Z";

        /// <summary>A resolution line: the pair stopped clashing at <paramref name="isoUtc"/>.</summary>
        private static string Res(string key, string isoUtc)
        {
            return string.Join(ResolvedLogReplay.Sep.ToString(), new[]
            {
                key, isoUtc, "tester", "100", "Pipe", "Pipes", "200", "Beam", "Structural Framing",
                "Current model", ResolvedLogReplay.Resolution, ""
            });
        }

        /// <summary>A line in the ORIGINAL ten-field format, before retraction existed.</summary>
        private static string Legacy(string key, string isoUtc)
        {
            return string.Join(ResolvedLogReplay.Sep.ToString(), new[]
            {
                key, isoUtc, "tester", "100", "Pipe", "Pipes", "200", "Beam", "Structural Framing",
                "Current model"
            });
        }

        /// <summary>A retraction of the occurrence (key, whenUtc), recorded at retractedUtc.</summary>
        private static string Ret(string key, string whenUtc, string retractedUtc)
        {
            return string.Join(ResolvedLogReplay.Sep.ToString(), new[]
            {
                key, whenUtc, "tester", "100", "Pipe", "Pipes", "200", "Beam", "Structural Framing",
                "Current model", ResolvedLogReplay.Retraction, retractedUtc
            });
        }

        private static List<ResolvedRecord> Replay(params string[] lines)
        {
            var into = new List<ResolvedRecord>();
            int events, retracted;
            ResolvedLogReplay.Replay(lines, into, out events, out retracted);
            return into;
        }

        private static int Standing(List<ResolvedRecord> recs)
        {
            int n = 0;
            foreach (var r in recs) if (!r.Retracted) n++;
            return n;
        }

        public static void RunAll(Action<string, bool?> check)
        {
            // ---- the existing format must keep loading, or every ledger on disk is lost
            var legacy = Replay("# comment", Legacy(K, T10), "", Legacy(J, T11));
            check("resolved: ten-field lines from before retraction still load",
                legacy.Count == 2 && Standing(legacy) == 2);
            check("resolved: absent event field reads as a resolution, not as nothing",
                legacy.Count == 2 && !legacy[0].Retracted);

            // ---- a retraction withdraws its occurrence
            var after = Replay(Res(K, T10), Ret(K, T10, T12));
            check("retract: withdraws the occurrence it names",
                after.Count == 1 && after[0].Retracted);
            check("retract: a withdrawn occurrence stops counting as a fix",
                Standing(after) == 0);
            check("retract: the withdrawal time is kept for the audit trail",
                after.Count == 1 && after[0].RetractedUtc == DateTime.Parse(T12).ToUniversalTime());

            // ---- ORDER MUST NOT MATTER. This is the whole reason the class exists.
            var before = Replay(Ret(K, T10, T12), Res(K, T10));
            check("retract: applied when it PRECEDES its occurrence in the file",
                before.Count == 1 && before[0].Retracted);

            // ---- it withdraws ONE OCCURRENCE, not the pair
            var twice = Replay(Res(K, T10), Res(K, T11), Ret(K, T10, T12));
            check("retract: the same pair fixed twice keeps both occurrences",
                twice.Count == 2);
            check("retract: only the named occurrence is withdrawn",
                Standing(twice) == 1);
            bool rightOne = true;
            foreach (var r in twice)
                if (r.WhenUtc == DateTime.Parse(T11).ToUniversalTime() && r.Retracted) rightOne = false;
            check("retract: ...and it is the right one", rightOne);

            // ---- a retraction naming nothing does nothing
            var orphan = Replay(Res(K, T10), Ret(J, T11, T12));
            check("retract: naming an occurrence that is not there withdraws nothing",
                orphan.Count == 1 && Standing(orphan) == 1);

            // ---- a retraction on one pair leaves another alone
            var two = Replay(Res(K, T10), Res(J, T10), Ret(K, T10, T12));
            check("retract: a withdrawal on one pair leaves the other standing",
                two.Count == 2 && Standing(two) == 1);

            // ---- withdrawing twice is one withdrawal, and the counter says so
            var into = new List<ResolvedRecord>();
            int ev, rc;
            ResolvedLogReplay.Replay(new[] { Res(K, T10), Ret(K, T10, T11), Ret(K, T10, T12) },
                                     into, out ev, out rc);
            check("retract: a second withdrawal of the same occurrence is not a second one",
                rc == 1 && Standing(into) == 0);
            check("retract: the latest withdrawal time wins",
                into.Count == 1 && into[0].RetractedUtc == DateTime.Parse(T12).ToUniversalTime());

            // ---- a retraction can also withdraw a LEGACY occurrence, which is the
            //      actual job: the false entries on disk are all in the old format.
            var mixed = Replay(Legacy(K, T10), Ret(K, T10, T12));
            check("retract: withdraws a ten-field legacy occurrence too",
                mixed.Count == 1 && mixed[0].Retracted);

            // ---- the round trip through the real writer, not a hand-built line
            var rec = new ResolvedRecord
            {
                Key = K,
                WhenUtc = DateTime.Parse(T10).ToUniversalTime(),
                By = "tester",
                HostId = 100, HostName = "Pipe", HostCategory = "Pipes",
                OtherId = 200, OtherName = "Beam", OtherCategory = "Structural Framing",
                ModelName = "Current model"
            };
            var written = Replay(
                ResolvedLogReplay.Line(rec, ResolvedLogReplay.Resolution, default(DateTime)),
                ResolvedLogReplay.Line(rec, ResolvedLogReplay.Retraction,
                                       DateTime.Parse(T12).ToUniversalTime()));
            check("retract: the writer's own output replays as a withdrawal",
                written.Count == 1 && written[0].Retracted);
            check("retract: the writer preserves the pair's detail",
                written.Count == 1 && written[0].HostName == "Pipe"
                && written[0].OtherCategory == "Structural Framing" && written[0].HostId == 100);

            // ---- a tab in a name would split the line; the writer has to scrub it
            var dirty = new ResolvedRecord
            {
                Key = K, WhenUtc = DateTime.Parse(T10).ToUniversalTime(), By = "t",
                HostName = "Pipe\twith\ttabs", HostCategory = "Pipes",
                OtherName = "Beam", OtherCategory = "SF", ModelName = "m"
            };
            var scrubbed = Replay(ResolvedLogReplay.Line(dirty, ResolvedLogReplay.Resolution,
                                                         default(DateTime)));
            check("resolved: a tab in a name cannot split the line",
                scrubbed.Count == 1 && scrubbed[0].HostCategory == "Pipes"
                && scrubbed[0].OtherName == "Beam");
        }
    }
}
