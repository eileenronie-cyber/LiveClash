using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 2 of the 2026-10-31 review, F3's items that can be checked without Revit: DN2's retry and
    /// notice (through <see cref="RewrittenFile"/> and <see cref="RetryBackoff"/>), DN3's identity on a carried
    /// ledger, DL7's format line and preset header, DL8's link names in the log, DL9's cheap retry.
    ///
    /// RED FIRST, with the predictions committed on their own BEFORE this file existed:
    /// tests\evidence\liveclash\REVIT-RUNS-F3-r2.md (commit 10e0ca0). The in-Revit half is
    /// Testing\ReviewChecks.LedgersR2.cs.
    /// </summary>
    public static class LedgerReviewR2Checks
    {
        private const string P = "review F3 r2: ";
        private const int Seed = 20261101;

        private static readonly string[] Plain = { "enabled=True", "set.begin=0", "setname=Default", "set.end=0" };

        // Constant inputs for the format checks, held once (CA1861).
        private static readonly string[] AmongOthers = { "a=1", FilterFormat.Line, "b=2" };
        private static readonly string[] NoVersion = { "a=1" };
        private static readonly string[] TwoThenThree = { "lcformat=2", "lcformat=3" };
        private static readonly string[] ThreeThenTwo = { "lcformat=3", "lcformat=2" };
        private static readonly string[] Three = { "lcformat=3" };
        private static readonly string[] Current = { FilterFormat.Line };
        private static readonly string[] JunkVersion = { "lcformat=abc" };
        private static readonly string[] Disabled = { "enabled=False" };

        public static void RunAll(Action<string, bool?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "liveclash-f3r2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var savedClock = RetryBackoff.Clock;
            try
            {
                using (Log.RedirectForTests(Path.Combine(dir, "test.log")))
                {
                    Backoff(check);
                    Reopen(check, dir);
                    Notices(check, dir);
                    Attempts(check, dir);
                    Identity(check, dir);
                    Format(check);
                    LinkNames(check);
                }
            }
            finally
            {
                RetryBackoff.Clock = savedClock;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ================================================================== DL9 / DN2: the back-off

        private static DateTime Clock;

        private static void UseClock(DateTime start) { Clock = start; RetryBackoff.Clock = () => Clock; }

        private static readonly int[] Steps = { 5, 10, 20, 40, 60, 60 };

        private static void Backoff(Action<string, bool?> check)
        {
            var t0 = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
            UseClock(t0);
            var b = new RetryBackoff();
            check(P + "BO-1 [green] a new gate is due", b.Due());

            b.Failed();
            Clock = t0.AddSeconds(4.9);
            bool closed = !b.Due();
            Clock = t0.AddSeconds(5);
            check(P + "BO-2 [red] after a failure the gate is closed until the first step (5 s) has passed", closed && b.Due());

            // BO-3: each further failure doubles the wait, up to a minute.
            UseClock(t0);
            b = new RetryBackoff();
            string bad = null;
            for (int i = 0; i < Steps.Length && bad == null; i++)
            {
                b.Failed();
                var failedAt = Clock;
                Clock = failedAt.AddSeconds(Steps[i] - 0.1);
                if (b.Due()) bad = "failure " + (i + 1) + ": due " + (Steps[i] - 0.1) + " s after, wanted " + Steps[i];
                Clock = failedAt.AddSeconds(Steps[i]);
                if (bad == null && !b.Due()) bad = "failure " + (i + 1) + ": not due " + Steps[i] + " s after";
            }
            check(P + "BO-3 [red] each further failure doubles the wait: 5, 10, 20, 40, 60, 60 s" + (bad == null ? "" : " - " + bad), bad == null);

            // BO-4 and BO-5: a success opens the gate at once and resets the steps.
            UseClock(t0);
            b = new RetryBackoff();
            b.Failed(); b.Failed(); b.Failed();
            b.Succeeded();
            check(P + "BO-4 [green] a success opens the gate at once", b.Due());
            b.Failed();
            Clock = t0.AddSeconds(4.9);
            bool closedAgain = !b.Due();
            Clock = t0.AddSeconds(5);
            check(P + "BO-5 [red] after a success the next failure waits the first step again", closedAgain && b.Due());

            // BO-6: property against a reference model.
            var r = new Random(Seed);
            string first = null;
            for (int h = 0; h < 300 && first == null; h++)
            {
                UseClock(t0);
                b = new RetryBackoff();
                int failures = 0;
                DateTime next = DateTime.MinValue;
                var log = new StringBuilder();
                for (int op = 0; op < 20 && first == null; op++)
                {
                    switch (r.Next(3))
                    {
                        case 0:
                            b.Failed(); failures++;
                            next = Clock.AddSeconds(Math.Min(5 * Math.Pow(2, failures - 1), 60));
                            log.Append("F ");
                            break;
                        case 1:
                            b.Succeeded(); failures = 0; next = DateTime.MinValue;
                            log.Append("S ");
                            break;
                        default:
                            Clock = Clock.AddSeconds(r.Next(0, 40));
                            log.Append("+t ");
                            break;
                    }
                    if (b.Due() != (Clock >= next)) first = "history " + h + ": " + log + " -> Due " + b.Due() + ", model says " + (Clock >= next);
                }
            }
            check(P + "BO-6 [red] property: 300 random histories of failures, successes and clock moves match the reference model (seed " + Seed + ")"
                  + (first == null ? "" : " - FIRST " + first), first == null);
        }

        // ================================================================== DN2: RewrittenFile.TryReopen

        private static string[] Lock(string path, Func<string[]> whileLocked, out string[] result)
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                result = whileLocked();
            return result;
        }

        private static void Reopen(Action<string, bool?> check, string dir)
        {
            var t0 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
            var path = Path.Combine(dir, "reopen.txt");
            File.WriteAllLines(path, Plain);

            // RF-1: locked at load, then readable, a retry past the back-off reads it.
            UseClock(t0);
            var rf = new RewrittenFile("Settings");
            string[] unused;
            Lock(path, () => rf.Open(path, null), out unused);
            bool wasUnreadable = rf.Unreadable;
            Clock = t0.AddSeconds(6);
            var back = rf.TryReopen();
            check(P + "RF-1 [red] a retry past the back-off reads the file once it is readable, and clears Unreadable"
                  + (wasUnreadable ? "" : " (PRECONDITION FAILED: the locked open did not mark it unreadable)"),
                wasUnreadable && back != null && back.SequenceEqual(Plain) && !rf.Unreadable && !rf.NeedsOpen(path));

            // RF-4: and it may save again.
            var changed = Disabled;
            check(P + "RF-4 [red] after a successful retry, Save writes again", rf.Save(changed) && File.ReadAllLines(path).SequenceEqual(changed));

            // RF-2: before the back-off is due nothing is read, even though the file is readable.
            File.WriteAllLines(path, Plain);
            UseClock(t0);
            rf = new RewrittenFile("Settings");
            Lock(path, () => rf.Open(path, null), out unused);
            Clock = t0.AddSeconds(1);
            check(P + "RF-2 [red] before the back-off is due a retry reads nothing and stays unreadable", rf.TryReopen() == null && rf.Unreadable);

            // RF-3: a retry that finds the file locked costs one attempt, not five.
            Clock = t0.AddSeconds(6);
            var sw = new Stopwatch();
            string[] got = null;
            Lock(path, () => { sw.Start(); got = rf.TryReopen(); sw.Stop(); return got; }, out unused);
            check(P + "RF-3 [red] a retry that finds the file locked makes one attempt (" + sw.ElapsedMilliseconds + " ms; five would be about 300)",
                got == null && rf.Unreadable && sw.ElapsedMilliseconds < 150);

            // RF-6 (added with the fix, no red run): a retry that failed pushes the next one out to the second step (10 s).
            Clock = t0.AddSeconds(12);   // 6 s after the failed retry: the first step (5 s) would be due, the second (10 s) is not
            check(P + "RF-6 [green] a failed retry doubles the wait: 6 s later the file is not read yet", rf.TryReopen() == null && rf.Unreadable);
            Clock = t0.AddSeconds(17);
            check(P + "RF-6b [green] ...and 11 s after it is", rf.TryReopen() != null && !rf.Unreadable);

            // RF-5: a store that is fine does nothing.
            UseClock(t0);
            var ok = new RewrittenFile("Settings");
            ok.Open(path, null);
            Clock = t0.AddSeconds(60);
            check(P + "RF-5 [green] a retry on a store that is not unreadable does nothing", ok.TryReopen() == null && !ok.Unreadable);
        }

        // ================================================================== DN2: the notice

        private static void Notices(Action<string, bool?> check, string dir)
        {
            var t0 = new DateTime(2026, 11, 1, 11, 0, 0, DateTimeKind.Utc);
            var path = Path.Combine(dir, "notice.txt");
            File.WriteAllLines(path, Plain);
            UseClock(t0);
            var rf = new RewrittenFile("Settings");
            var seen = new List<string>();
            rf.Notice += seen.Add;
            string[] unused;

            Lock(path, () => rf.Open(path, null), out unused);
            check(P + "NT-1 [red] a read that first fails raises one notice: changes paused, nothing overwritten ("
                  + seen.Count + ": " + string.Join(" | ", seen) + ")",
                seen.Count == 1 && seen[0].Contains("Settings") && seen[0].Contains("paused") && seen[0].Contains("not overwritten"));

            // NT-2: three more failed retries add nothing.
            for (int i = 1; i <= 3; i++)
            {
                Clock = t0.AddSeconds(70 * i);
                Lock(path, () => rf.TryReopen(), out unused);
            }
            Lock(path, () => rf.Open(path, null), out unused);   // and a second open of the same locked file
            check(P + "NT-2 [green] repeated failed retries, and a second open of the locked file, raise no further notice (" + seen.Count + ")", seen.Count <= 1);

            // NT-3: the recovery.
            Clock = t0.AddSeconds(1000);
            rf.TryReopen();
            check(P + "NT-3 [red] the recovery raises exactly one notice: read again, changes resume (" + seen.Count + ")",
                seen.Count == 2 && seen[1].Contains("read again") && seen[1].Contains("resume"));

            // NT-5: a second episode notices again.
            int before = seen.Count;
            rf.Open(path, null);   // a fresh open of the same file
            Lock(path, () => rf.Open(path, null), out unused);
            check(P + "NT-5 [red] a second unreadable episode, after a recovery, notices again (" + (seen.Count - before) + " new)", seen.Count == before + 1);

            // NT-6 (added with the fix, no red run): the recovery through Open (the load path, not the tick) says so once.
            var viaOpen = new RewrittenFile("Settings");
            var seenOpen = new List<string>();
            viaOpen.Notice += seenOpen.Add;
            Lock(path, () => viaOpen.Open(path, null), out unused);
            viaOpen.Open(path, null);
            check(P + "NT-6 [green] a failed open and then a good open of the same file raise paused, then resume (" + seenOpen.Count + ")",
                seenOpen.Count == 2 && seenOpen[0].Contains("paused") && seenOpen[1].Contains("resume"));

            // NT-4: a missing file is not an episode.
            var none = new RewrittenFile("Settings");
            var seenNone = new List<string>();
            none.Notice += seenNone.Add;
            none.Open(Path.Combine(dir, "never-written.txt"), null);
            check(P + "NT-4 [green] a missing file raises no notice", seenNone.Count == 0 && !none.Unreadable);
        }

        // ================================================================== DL9: attempts

        private static void Attempts(Action<string, bool?> check, string dir)
        {
            var path = Path.Combine(dir, "attempts.txt");
            File.WriteAllLines(path, Plain);
            LedgerFile.ReadState st = LedgerFile.ReadState.Read;
            long oneMs, defaultMs;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var sw = Stopwatch.StartNew();
                var one = LedgerFile.Read(path, out st, 1);
                oneMs = sw.ElapsedMilliseconds;
                bool oneFailed = one == null && st == LedgerFile.ReadState.Failed;
                sw.Restart();
                LedgerFile.Read(path, out st);
                defaultMs = sw.ElapsedMilliseconds;
                check(P + "DL9-1 [red] a read with one attempt on a locked file fails at once (" + oneMs + " ms)", oneFailed && oneMs < 150);
            }
            check(P + "DL9-2 [green] a read with the default attempts still retries (" + defaultMs + " ms)", defaultMs >= 200);
        }

        // ================================================================== DN3: identity on a carried ledger

        private static string Ledger(string identity, string key, DateTime when)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# LiveClash status - append-only");
            if (identity != null) sb.AppendLine(ModelIdentity.Line(identity));
            sb.AppendLine(key + "\tCoordinated\t" + when.ToString("o", CultureInfo.InvariantCulture) + "\tu\tnote");
            return sb.ToString();
        }

        private static Dictionary<string, ClashStatusEntry> Replay(IEnumerable<string> lines)
        {
            var m = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int e, l, rr;
            ClashStatusReplay.Replay(lines, m, out e, out l, out rr);
            return m;
        }

        private static string Snapshot(Dictionary<string, ClashStatusEntry> m)
        {
            return string.Join(";", m.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value.Kind + "/" + k.Value.Note));
        }

        private static void Identity(Action<string, bool?> check, string dir)
        {
            var when = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
            var from = Path.Combine(dir, "Tower.status.txt");
            var to = Path.Combine(dir, "Tower-v2.status.txt");
            File.WriteAllText(from, Ledger("central-OLD", "1|0|2", when));

            bool carried = LedgerFile.CopyOver(from, to, "central-NEW");
            var carriedLines = File.ReadAllLines(to);
            check(P + "DN3-1 [red] after the carry the file records the NEW identity (" + ModelIdentity.RecordedIn(carriedLines) + ")",
                carried && ModelIdentity.RecordedIn(carriedLines) == "central-NEW");
            check(P + "DN3-2 [green] the source file is left as it was", ModelIdentity.RecordedIn(File.ReadAllLines(from)) == "central-OLD");

            string claim;
            var key = ModelIdentity.Resolve("Tower-v2", true, ModelIdentity.RecordedIn(carriedLines), "central-NEW", out claim);
            check(P + "DN3-3 [red] the next open resolves to the title key, not a forked title~hash file (" + key + ")", key == "Tower-v2");

            // DN3-4: no identity (a standalone model): a byte-for-byte copy.
            var plainFrom = Path.Combine(dir, "Solo.status.txt");
            var plainTo = Path.Combine(dir, "Solo-v2.status.txt");
            File.WriteAllText(plainFrom, Ledger(null, "3|0|4", when));
            LedgerFile.CopyOver(plainFrom, plainTo, "");
            check(P + "DN3-4 [green] with no identity the carry is a byte-for-byte copy", File.ReadAllBytes(plainFrom).SequenceEqual(File.ReadAllBytes(plainTo)));

            // DN3-5: a source with no identity header gets the claim appended.
            var noHeaderTo = Path.Combine(dir, "Unclaimed-v2.status.txt");
            LedgerFile.CopyOver(plainFrom, noHeaderTo, "central-NEW");
            check(P + "DN3-5 [red] a source with no identity header gets the claim appended", ModelIdentity.RecordedIn(File.ReadAllLines(noHeaderTo)) == "central-NEW");

            // DN3-6: the decisions are the same.
            check(P + "DN3-6 [green] the carried file replays to the same decisions as the source",
                Snapshot(Replay(File.ReadAllLines(from))) == Snapshot(Replay(carriedLines)));

            // DN3-8: a missing source still sets the target aside.
            var stale = Path.Combine(dir, "Stale.status.txt");
            File.WriteAllText(stale, Ledger("someone-else", "9|0|9", when));
            check(P + "DN3-8 [green] a missing source still sets the target aside, with an identity given",
                LedgerFile.CopyOver(Path.Combine(dir, "never.txt"), stale, "central-NEW") && !File.Exists(stale) && File.Exists(stale + ".bak"));

            // DN3-7: property.
            var r = new Random(Seed + 1);
            string firstBad = null;
            for (int h = 0; h < 300 && firstBad == null; h++)
            {
                var src = Path.Combine(dir, "p" + h + ".txt");
                var dst = Path.Combine(dir, "p" + h + "-v2.txt");
                string oldId = r.Next(3) == 0 ? null : "old-" + r.Next(5);
                string newId = "new-" + r.Next(5);
                var sb = new StringBuilder(Ledger(oldId, "k" + r.Next(4) + "|0|1", when.AddMinutes(r.Next(50))));
                for (int i = r.Next(6); i > 0; i--)
                    sb.AppendLine("k" + r.Next(4) + "|0|1\t" + (r.Next(2) == 0 ? "Pending" : "Ignored") + "\t" + when.AddMinutes(r.Next(50)).ToString("o", CultureInfo.InvariantCulture) + "\tu\tn" + i);
                File.WriteAllText(src, sb.ToString());
                LedgerFile.CopyOver(src, dst, newId);
                var dl = File.ReadAllLines(dst);
                string c2;
                var k2 = ModelIdentity.Resolve("T", true, ModelIdentity.RecordedIn(dl), newId, out c2);
                if (ModelIdentity.RecordedIn(dl) != newId || k2 != "T" || Snapshot(Replay(File.ReadAllLines(src))) != Snapshot(Replay(dl)))
                    firstBad = "history " + h + ": old " + oldId + " new " + newId + " recorded " + ModelIdentity.RecordedIn(dl) + " key " + k2;
            }
            check(P + "DN3-7 [red] property: 300 random ledgers carried to a new identity record it, replay the same and do not fork (seed " + (Seed + 1) + ")"
                  + (firstBad == null ? "" : " - FIRST " + firstBad), firstBad == null);
        }

        // ================================================================== DL7: the format line and the preset header

        private static readonly string[] ValueAlphabet = { "a", "b", " ", "|", "/", "\r", "\n", "\u001b", "x", "\u00e9" };

        private static ParameterFilter RandomFilter(Random r, out bool special)
        {
            bool sawSpecial = false;
            var f = new ParameterFilter();
            Func<string> value = () =>
            {
                var sb = new StringBuilder();
                for (int j = 1 + r.Next(5); j > 0; j--) sb.Append(ValueAlphabet[r.Next(ValueAlphabet.Length)]);
                var v = sb.ToString();
                if (v.Trim().Length == 0) v = "v";
                foreach (var c in v) if (c == '|' || c == '\r' || c == '\n' || c == '\u001b') sawSpecial = true;
                return v;
            };
            for (int i = r.Next(3); i > 0; i--) f.Picked.Add(new ValuePick { ParameterName = "P", Value = value() });
            for (int i = r.Next(2); i > 0; i--) f.Excluded.Add(new ValuePick { ParameterName = "P", Value = value() });
            for (int i = r.Next(2); i > 0; i--) f.OnlyValues.Add(new ValuePick { ParameterName = "P", Value = value() });
            if (r.Next(2) == 0) f.Rules.Add(new Rule { ParameterName = "R", Op = RuleOp.Is, Values = { value() } });
            if (r.Next(3) == 0) f.Groups.Add(new RuleGroup { Rules = { new Rule { ParameterName = "G", Op = RuleOp.Is, Values = { value() } } } });
            special = sawSpecial;
            return f;
        }

        private static void Format(Action<string, bool?> check)
        {
            check(P + "FF-1 [red] the declared version is read from the format line",
                FilterFormat.Declared(AmongOthers) == 2 && FilterFormat.Declared(NoVersion) == 0
                && FilterFormat.Declared(TwoThenThree) == 3 && FilterFormat.Declared(ThreeThenTwo) == 3);
            check(P + "FF-2 [red] a newer version gives a warning that says so",
                (FilterFormat.WarnIfNewer(Three) ?? "").Contains("newer"));
            check(P + "FF-3 [green] version 2, no line, or a junk line gives no warning",
                FilterFormat.WarnIfNewer(Current) == null && FilterFormat.WarnIfNewer(NoVersion) == null
                && FilterFormat.WarnIfNewer(JunkVersion) == null && FilterFormat.WarnIfNewer(null) == null);

            var r = new Random(Seed + 2);
            bool threw = false;
            try
            {
                const string pool = "lcformat=0123456789abc\t|# ";
                for (int i = 0; i < 200; i++)
                {
                    var sb = new StringBuilder();
                    for (int j = r.Next(30); j > 0; j--) sb.Append(pool[r.Next(pool.Length)]);
                    FilterFormat.Declared(new[] { sb.ToString(), null });
                    FilterFormat.WarnIfNewer(new[] { sb.ToString() });
                    FilterFormat.NeedsV2(new[] { sb.ToString(), "spick=" + sb });
                }
            }
            catch { threw = true; }
            check(P + "FF-4 [green] 200 random junk lines never throw", !threw);

            var piped = new ParameterFilter();
            piped.Picked.Add(new ValuePick { ParameterName = "System Type", Scope = ParamScope.Type, Value = "CHW|Supply" });
            var plain = new ParameterFilter();
            plain.Picked.Add(new ValuePick { ParameterName = "System Type", Scope = ParamScope.Type, Value = "CHW Supply" });
            var pipedHeader = FilterFormat.PresetHeader(piped.Serialize("s"));
            var plainHeader = FilterFormat.PresetHeader(plain.Serialize("s"));
            check(P + "FF-5 [red] the header for a preset with a '|' value is refused by the old build's check (" + pipedHeader + ")", !FilterFormat.OldReaderAccepts(pipedHeader));
            check(P + "FF-6 [green] the header for a plain preset is accepted by the old build's check (" + plainHeader + ")", FilterFormat.OldReaderAccepts(plainHeader));

            int v1, v2, vBom;
            bool a1 = FilterFormat.Accepts(FilterFormat.V1Header, out v1);
            bool a2 = FilterFormat.Accepts(FilterFormat.V2Header, out v2);
            bool aBom = FilterFormat.Accepts("\uFEFF" + FilterFormat.V2Header, out vBom);
            int vJunk;
            check(P + "FF-7 [red] the new reader accepts both headers (with or without a BOM) and reports the version, and refuses others",
                a1 && v1 == 1 && a2 && v2 == 2 && aBom && vBom == 2 && !FilterFormat.Accepts("# something else", out vJunk) && !FilterFormat.Accepts(null, out vJunk));

            // FF-8 and FF-9: property over random filters.
            string first8 = null, first9 = null;
            for (int h = 0; h < 300; h++)
            {
                bool special;
                var f = RandomFilter(r, out special);
                var lines = f.Serialize("s");
                if (first8 == null && FilterFormat.NeedsV2(lines) != special)
                    first8 = "filter " + h + ": expected " + special + " got " + !special + ": " + string.Join(" / ", lines.Select(l => l.Replace("\u001b", "<ESC>")));
                var header = FilterFormat.PresetHeader(lines);
                int version;
                if (first9 == null && (!FilterFormat.Accepts(header, out version) || version != (special ? 2 : 1) || FilterFormat.OldReaderAccepts(header) == special))
                    first9 = "filter " + h + ": special " + special + " header " + header;
            }
            check(P + "FF-8 [red] property: 300 random filters need v2 exactly when some value holds '|', CR, LF or ESC (seed " + (Seed + 2) + ")"
                  + (first8 == null ? "" : " - FIRST " + first8), first8 == null);
            check(P + "FF-9 [red] property: the header read back keeps its version, and the old build refuses exactly the v2 ones"
                  + (first9 == null ? "" : " - FIRST " + first9), first9 == null);
        }

        // ================================================================== DL8: link names in the log

        private static void LinkNames(Action<string, bool?> check)
        {
            var saved = LinkWatch.Snapshot();
            try
            {
                LinkWatch.NoteInstance(901, "R2Link-Alpha.rvt", false);
                var s = Log.Scrub("loaded R2Link-Alpha.rvt, and R2Link-Alpha, from D:\\Jobs\\R2Link-Alpha.rvt");
                check(P + "DL8-1 [red] a link name noted by a link event never reaches the log as written (" + s + ")", s.IndexOf("R2Link-Alpha", StringComparison.OrdinalIgnoreCase) < 0);

                LinkWatch.NoteInstance(902, "R2Link-Beta.rvt", false);
                var bare = Log.Scrub("R2Link-Beta");
                var withExt = Log.Scrub("R2Link-Beta.rvt");
                check(P + "DL8-2 [red] the name and its .rvt form get the same tag (" + bare + " / " + withExt + ")",
                    bare.StartsWith("model#", StringComparison.Ordinal) && withExt == bare + ".rvt");

                LinkWatch.RecordUnloaded("R2Link-Gamma.rvt");
                check(P + "DL8-3 [red] the same through the unload route", Log.Scrub("R2Link-Gamma").StartsWith("model#", StringComparison.Ordinal));

                LinkWatch.NoteInstance(903, "ab", false);
                check(P + "DL8-4 [green] a link name under 3 characters is left alone", Log.Scrub("ab here") == "ab here");
            }
            finally { LinkWatch.Restore(saved); }
        }
    }
}
