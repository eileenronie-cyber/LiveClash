using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The 2026-10-31 review's ledger, settings and "What changed" findings (F3): M3, M4, M5, M9 and
    /// LOW-3, LOW-4, LOW-10, LOW-11. Revit-free, so it runs headless and in Revit.
    ///
    /// RED FIRST (rule D1). Every check was written, with the prediction beside it, before the fix,
    /// and run against the code as reviewed (abe7dfd plus seams that keep its behaviour). The predicted
    /// result at that commit is in each name's tag: [red] or [green] (a guard against over-fixing).
    /// The run is recorded in tests\evidence\liveclash\REVIT-RUNS-F3.md.
    ///
    /// The in-Revit half (the settings store, the triage store, a real Save As) is
    /// Testing\ReviewChecks.Ledgers.cs.
    /// </summary>
    public static class LedgerReviewChecks
    {
        private const string P = "review F3: ";
        private const int Seed = 20261031;

        // Constant inputs, held once (CA1861).
        private static readonly string[] OneX = { "x" };
        private static readonly string[] OneNew = { "new" };
        private static readonly string[] OneA = { "a" };
        private static readonly string[] EnabledTrue = { "enabled=True" };
        private static readonly string[] SomeoneElse = { "someone else's" };
        private static readonly string[] OtherModel = { "a different model's decisions" };
        private static readonly string[] A1 = { "a=1" };
        private static readonly string[] A1Null = { "a=1", null };
        private static readonly string[] EscEnd = { "a\u001b" };
        private static readonly string[] EscX = { "\u001bx" };

        public static void RunAll(Action<string, bool?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "liveclash-f3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (Log.RedirectForTests(Path.Combine(dir, "test.log")))
                {
                    WhatChanged(check);
                    UndecidedNote(check);
                    HalfLine(check, dir);
                    WholeFile(check, dir);
                    SaveAsCarry(check, dir);
                    PickedValues(check);
                    Fingerprint(check);
                    Edges(check, dir);
                }
                LogScrub(check, dir);
            }
            finally
            {
                LedgerFile.DieAfterLinesForTests = -1;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ================================================================== M3

        private static ClashResult Row(string model, int i)
        {
            return new ClashResult { Key = model + "|" + i, ModelName = model, Severity = ClashSeverity.Hard };
        }

        /// <summary>Run one "what changed" wait: the list before, the link events, the list after.</summary>
        private static ClashChangeTracker.Summary Track(List<ClashResult> before, IEnumerable<KeyValuePair<LinkWatch.Kind, string>> events,
            List<ClashResult> after, bool full)
        {
            var saved = ClashChangeTracker.Snapshot();
            try
            {
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.SnapshotSource = () => before;
                foreach (var e in events) ClashChangeTracker.NoteLinkChange(e.Key, e.Value);
                ClashChangeTracker.Complete(after, full);
                return ClashChangeTracker.Current;
            }
            finally { ClashChangeTracker.Restore(saved); }
        }

        private static KeyValuePair<LinkWatch.Kind, string> Ev(LinkWatch.Kind k, string link) { return new KeyValuePair<LinkWatch.Kind, string>(k, link); }

        private static void WhatChanged(Action<string, bool?> check)
        {
            var a1 = Row("A", 1); var a2 = Row("A", 2); var b1 = Row("B", 1); var b2 = Row("B", 2);
            var before = new List<ClashResult> { a1, a2, b1, b2 };
            var onlyB = new List<ClashResult> { b1, b2 };

            // M3-1 [red]: reload, then unload, then a full check. The full check finds nothing for A
            // because A is not there. At abe7dfd the headline reads "Since A changed: 2 cleared."
            var s = Track(before, new[] { Ev(LinkWatch.Kind.LoadedOrReloaded, "A"), Ev(LinkWatch.Kind.Unloaded, "A") }, onlyB, true);
            check(P + "M3-1 [red] reload then unload: A's rows are NOT counted as cleared (cleared " + s.Set.Cleared.Count + ")",
                s.Set.Cleared.Count == 0);
            check(P + "M3-1b [red] ...they are listed as not checked, and A is named as not loaded (" + s.NotChecked.Count + ", " + string.Join(",", s.NotLoadedLinks) + ")",
                s.NotChecked.Count == 2 && s.NotLoadedLinks.Contains("A"));
            check(P + "M3-1c [red] ...and the headline says 'not checked', never 'cleared': " + s.Headline,
                s.Headline.Contains("not checked") && !s.Headline.Contains("cleared"));

            // M3-2 [red]: the UNCONFIRMED Revit order - an unload that also reports the instance as
            // modified. A move does not load a link, so A is still not loaded.
            s = Track(before, new[] { Ev(LinkWatch.Kind.LoadedOrReloaded, "A"), Ev(LinkWatch.Kind.Unloaded, "A"), Ev(LinkWatch.Kind.InstanceMoved, "A") }, onlyB, true);
            check(P + "M3-2 [red] reload, unload, then a 'moved' notice: still not cleared (cleared " + s.Set.Cleared.Count + ", not checked " + s.NotChecked.Count + ")",
                s.Set.Cleared.Count == 0 && s.NotChecked.Count == 2);

            // M3-3 [green]: unloaded and then loaded again - the rows that are gone now ARE cleared.
            s = Track(before, new[] { Ev(LinkWatch.Kind.Unloaded, "A"), Ev(LinkWatch.Kind.LoadedOrReloaded, "A") }, onlyB, true);
            check(P + "M3-3 [green] unload then reload: A is loaded at the check, so its gone rows are cleared (cleared " + s.Set.Cleared.Count + ", not checked " + s.NotChecked.Count + ")",
                s.Set.Cleared.Count == 2 && s.NotChecked.Count == 0);

            // M3-4 [red]: two links. A reloaded then unloaded; B reloaded and one of its clashes really went.
            s = Track(before, new[] { Ev(LinkWatch.Kind.LoadedOrReloaded, "A"), Ev(LinkWatch.Kind.LoadedOrReloaded, "B"), Ev(LinkWatch.Kind.Unloaded, "A") },
                new List<ClashResult> { b1 }, true);
            check(P + "M3-4 [red] two links: only B's real fix is cleared, A's rows are not checked (cleared " + s.Set.Cleared.Count + ", not checked " + s.NotChecked.Count + "): " + s.Headline,
                s.Set.Cleared.Count == 1 && s.Set.Cleared[0].Key == b2.Key && s.NotChecked.Count == 2 && s.Headline.Contains("1 cleared"));

            // M3-5 [green]: an unload alone keeps its own wording - the link is gone, and says so.
            s = Track(before, new[] { Ev(LinkWatch.Kind.Unloaded, "A") }, onlyB, true);
            check(P + "M3-5 [green] an unload alone still reads 'A is no longer there': " + s.Headline,
                s.AllGone && s.Headline.StartsWith("A is no longer there", StringComparison.Ordinal));

            // M3-6 [red]: a recheck reports the same way.
            s = Track(before, new[] { Ev(LinkWatch.Kind.LoadedOrReloaded, "A"), Ev(LinkWatch.Kind.Unloaded, "A") }, onlyB, false);
            check(P + "M3-6 [red] after a recheck too, the unloaded link's rows are not cleared (cleared " + s.Set.Cleared.Count + ")",
                s.Set.Cleared.Count == 0 && s.NotChecked.Count == 2);

            // M3-7 [red]: property. Any sequence of link events: a row counts as cleared only if its link
            // was loaded at the end; cleared + not checked together are exactly the rows that left the list.
            var r = new Random(Seed);
            var kinds = new[] { LinkWatch.Kind.LoadedOrReloaded, LinkWatch.Kind.Unloaded, LinkWatch.Kind.InstanceAdded, LinkWatch.Kind.InstanceMoved, LinkWatch.Kind.InstanceDeleted };
            string first = null;
            for (int h = 0; h < 400 && first == null; h++)
            {
                var evs = new List<KeyValuePair<LinkWatch.Kind, string>>();
                var lastLoadState = new Dictionary<string, bool>();
                for (int i = 1 + r.Next(5); i > 0; i--)
                {
                    var k = kinds[r.Next(kinds.Length)];
                    var link = r.Next(2) == 0 ? "A" : "B";
                    evs.Add(Ev(k, link));
                    if (k == LinkWatch.Kind.Unloaded || k == LinkWatch.Kind.InstanceDeleted) lastLoadState[link] = false;
                    else if (k == LinkWatch.Kind.LoadedOrReloaded || k == LinkWatch.Kind.InstanceAdded) lastLoadState[link] = true;
                }
                var after = before.Where(x => r.Next(2) == 0).ToList();
                var sum = Track(before, evs, after, r.Next(2) == 0);
                if (sum == null) { first = "history " + h + ": no summary"; break; }
                if (sum.AllGone) continue;
                var left = ClashChange.Compare(before, after, evs.Select(e => e.Value).Distinct().ToList()).Cleared.Select(x => x.Key).OrderBy(x => x).ToList();
                var both = sum.Set.Cleared.Concat(sum.NotChecked).Select(x => x.Key).OrderBy(x => x).ToList();
                bool loaded;
                bool bad = sum.Set.Cleared.Any(x => lastLoadState.TryGetValue(x.ModelName, out loaded) && !loaded);
                if (bad || !left.SequenceEqual(both))
                    first = "history " + h + ": " + string.Join(" ", evs.Select(e => e.Key + ":" + e.Value)) + " -> cleared " + string.Join(",", sum.Set.Cleared.Select(x => x.Key))
                          + " not checked " + string.Join(",", sum.NotChecked.Select(x => x.Key));
            }
            check(P + "M3-7 [red] property: no row of a link that ended unloaded is ever 'cleared', and nothing that left the list is lost (400 histories, seed " + Seed + ")"
                  + (first == null ? "" : " - FIRST " + first), first == null);
        }

        // ================================================================== M5

        private static string L(string key, string ev, DateTime when, string note)
        {
            return string.Join(ClashStatusReplay.Sep.ToString(), new[] { key, ev, when.ToString("o", CultureInfo.InvariantCulture), "tester", note ?? "" });
        }

        private static Dictionary<string, ClashStatusEntry> Replay(IEnumerable<string> lines)
        {
            var m = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int e, l, rr;
            ClashStatusReplay.Replay(lines, m, out e, out l, out rr);
            return m;
        }

        private static void UndecidedNote(Action<string, bool?> check)
        {
            var t = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
            const string K = "11|0|22";
            ClashStatusEntry e;

            // M5-1 [red]: a note on a New row is what the panel saves; a reload must keep it.
            var m = Replay(new[] { L(K, "New", t, "check with the fire engineer") });
            check(P + "M5-1 [red] a note on an undecided (New) clash survives a reload",
                m.TryGetValue(K, out e) && e.Kind == ClashStatusKind.New && e.Note == "check with the fire engineer");

            // M5-2 [green]: New with no note is still nothing to hold.
            check(P + "M5-2 [green] New with no note is still dropped on reload", !Replay(new[] { L(K, "New", t, "") }).ContainsKey(K));

            // M5-3 [red]: decided, then set back to New with a note: the note is what stands.
            m = Replay(new[] { L(K, "Coordinated", t, ""), L(K, "New", t.AddMinutes(1), "reopened by the client") });
            check(P + "M5-3 [red] decided, then back to New with a note: New with that note after a reload",
                m.TryGetValue(K, out e) && e.Kind == ClashStatusKind.New && e.Note == "reopened by the client");

            // M5-4 [green]: the note cleared later - nothing left.
            m = Replay(new[] { L(K, "New", t, "temp"), L(K, "New", t.AddMinutes(1), "") });
            check(P + "M5-4 [green] a note later cleared leaves nothing", !m.ContainsKey(K));

            // M5-5 [red]: property against the store's own in-memory rule (ClashStatusStore.Set): the latest
            // decision by clock stands, and is held unless it is New with no note.
            var r = new Random(Seed + 1);
            var keys = new[] { "1|0|2", "3|0|4", "5|0|6" };
            var kinds = new[] { "Pending", "Coordinated", "Ignored", "New" };
            string first = null;
            for (int h = 0; h < 500 && first == null; h++)
            {
                var mins = Enumerable.Range(0, 50).OrderBy(x => r.Next()).ToList();
                var lines = new List<string>();
                var latest = new Dictionary<string, Tuple<DateTime, string, string>>();
                for (int i = 0; i < 1 + r.Next(10); i++)
                {
                    var k = keys[r.Next(keys.Length)];
                    var ev = kinds[r.Next(kinds.Length)];
                    var note = r.Next(2) == 0 ? "" : "n" + i;
                    var when = t.AddMinutes(mins[i]);
                    lines.Add(L(k, ev, when, note));
                    Tuple<DateTime, string, string> cur;
                    if (!latest.TryGetValue(k, out cur) || when > cur.Item1) latest[k] = Tuple.Create(when, ev, note);
                }
                var expect = latest.Where(kv => !(kv.Value.Item2 == "New" && kv.Value.Item3 == ""))
                                   .Select(kv => kv.Key + "=" + kv.Value.Item2 + ":" + kv.Value.Item3).OrderBy(x => x, StringComparer.Ordinal);
                var got = Replay(lines.OrderBy(x => r.Next())).Select(kv => kv.Key + "=" + kv.Value.Kind + ":" + (kv.Value.Note ?? "")).OrderBy(x => x, StringComparer.Ordinal);
                if (!expect.SequenceEqual(got)) first = "history " + h + ": expected " + string.Join(" ", expect) + " got " + string.Join(" ", got);
            }
            check(P + "M5-5 [red] property: in any order, a reload holds exactly what the store held in memory (500 histories, seed " + (Seed + 1) + ")"
                  + (first == null ? "" : " - FIRST " + first), first == null);

            // M5-6 [green]: junk never throws, whatever is mixed in.
            bool threw = false;
            try
            {
                var junk = new List<string>();
                for (int i = 0; i < 300; i++)
                {
                    var sb = new StringBuilder();
                    const string pool = "\t|New#=x\u00e9 0Z-:";
                    for (int j = r.Next(40); j > 0; j--) sb.Append(pool[r.Next(pool.Length)]);
                    junk.Add(sb.ToString());
                    if (i % 7 == 0) junk.Add(L("9|0|9", "New", t.AddSeconds(i), "n"));
                }
                Replay(junk);
            }
            catch { threw = true; }
            check(P + "M5-6 [green] junk lines mixed with New notes never throw", !threw);
        }

        // ================================================================== LOW-3

        private static void HalfLine(Action<string, bool?> check, string dir)
        {
            var t = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
            var good = L("7|0|8", "Coordinated", t, "");
            var next = L("70|0|80", "Ignored", t.AddMinutes(1), "");

            // L3-1 [red]: a crash left half a line with no newline. The next record must start on its own line.
            var path = Path.Combine(dir, "half.txt");
            File.WriteAllText(path, "# header" + Environment.NewLine + good + Environment.NewLine + "70|0|80\tCoord");
            LedgerFile.Append(path, "# header" + Environment.NewLine, next + Environment.NewLine);
            var lines = File.ReadAllLines(path);
            check(P + "L3-1 [red] an append after a half-written line starts on a new line", lines.Length > 0 && lines[lines.Length - 1] == next);
            var m = Replay(lines);
            check(P + "L3-2 [red] ...so the replay reads the new record (and the earlier good one)", m.ContainsKey("70|0|80") && m.ContainsKey("7|0|8"));

            // L3-3 [green]: a file that ends cleanly gets no blank line.
            var clean = Path.Combine(dir, "clean.txt");
            File.WriteAllText(clean, good + Environment.NewLine);
            LedgerFile.Append(clean, null, next + Environment.NewLine);
            check(P + "L3-3 [green] a file that ends in a newline gets no blank line", File.ReadAllLines(clean).SequenceEqual(new[] { good, next }));

            // L3-4 [green]: a fresh file gets its header first.
            var fresh = Path.Combine(dir, "fresh.txt");
            LedgerFile.Append(fresh, "# h" + Environment.NewLine, next + Environment.NewLine);
            check(P + "L3-4 [green] a fresh file gets its header, then the record", File.ReadAllLines(fresh).SequenceEqual(new[] { "# h", next }));

            // L3-5 [red]: property. Cut a valid ledger at ANY byte (a crash mid-append) and append one more
            // record: that record is always read back.
            var whole = "# h\r\n" + good + "\r\n" + L("5|0|6", "Pending", t.AddMinutes(2), "x") + "\r\n";
            int lost = 0, cuts = 0;
            for (int cut = 1; cut < whole.Length; cut++)
            {
                var p = Path.Combine(dir, "cut.txt");
                File.WriteAllText(p, whole.Remove(cut));
                LedgerFile.Append(p, "# h\r\n", next + "\r\n");
                if (!Replay(File.ReadAllLines(p)).ContainsKey("70|0|80")) lost++;
                cuts++;
            }
            check(P + "L3-5 [red] property: after a cut at any of " + cuts + " bytes, the next record is never swallowed (lost " + lost + ")", lost == 0);
        }

        // ================================================================== M9

        private static void WholeFile(Action<string, bool?> check, string dir)
        {
            var path = Path.Combine(dir, "settings.txt");
            var real = new[] { "enabled=True", "set.begin=0", "setname=keep-me", "set.end=0" };
            var defaults = new[] { "enabled=True", "set.begin=0", "setname=Default", "set.end=0" };

            // M9-1 [red]: a write that dies part-way must leave the old file whole (abe7dfd rewrote in place).
            File.WriteAllLines(path, real);
            LedgerFile.DieAfterLinesForTests = 2;
            bool wrote = LedgerFile.WriteWhole(path, defaults);
            LedgerFile.DieAfterLinesForTests = -1;
            check(P + "M9-1 [red] a write that dies part-way leaves the old file exactly as it was",
                !wrote && File.ReadAllLines(path).SequenceEqual(real));
            check(P + "M9-2 [green] ...and leaves no temp file behind",
                Directory.GetFiles(dir, "settings.txt*").Length == 1);

            // M9-3..6: a read that fails must not become defaults written over the real file.
            var store = new RewrittenFile();
            string[] got;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                got = store.Open(path, null);
            check(P + "M9-3 [red] a file that exists but cannot be read is marked unreadable (not 'missing')", got == null && store.Unreadable);
            bool saved = store.Save(defaults);
            check(P + "M9-4 [red] ...and saving defaults over it is refused, the real file untouched",
                !saved && File.ReadAllLines(path).SequenceEqual(real));
            check(P + "M9-5 [red] ...and the next load of the SAME file reads it again (the failure does not stick)", store.NeedsOpen(path));
            got = store.Open(path, null);
            check(P + "M9-6 [green] the retry reads the real file and saving works again",
                got != null && got.SequenceEqual(real) && !store.Unreadable && store.Save(real));

            // M9-7 [green]: no file yet is not a failure - a fresh model may save.
            var fresh = new RewrittenFile();
            var none = fresh.Open(Path.Combine(dir, "none.txt"), null);
            check(P + "M9-7 [green] a missing file is 'nothing yet', not unreadable, and may be saved",
                none == null && !fresh.Unreadable && fresh.Save(defaults) && File.Exists(Path.Combine(dir, "none.txt")));

            // M9-8 [red]: a replace keeps the old content when asked to.
            var bak = path + ".bak";
            check(P + "M9-8 [red] a whole write can keep the replaced file as a backup",
                LedgerFile.WriteWhole(path, defaults, bak) && File.Exists(bak) && File.ReadAllLines(bak).SequenceEqual(real)
                && File.ReadAllLines(path).SequenceEqual(defaults));
        }

        // ================================================================== M4 (the file half)

        private static void SaveAsCarry(Action<string, bool?> check, string dir)
        {
            var oldPath = Path.Combine(dir, "Tower.txt");
            var newPath = Path.Combine(dir, "Tower-v2.txt");
            var lines = new[] { "enabled=True", "set.begin=0", "setname=MEP vs STR", "set.end=0" };
            File.WriteAllLines(oldPath, lines);

            var store = new RewrittenFile();
            store.Open(oldPath, null);
            bool carried = store.CarryTo(newPath, lines);
            check(P + "M4-1 [red] Save As carries the model's settings to the new name",
                carried && store.Path == newPath && File.Exists(newPath) && File.ReadAllLines(newPath).SequenceEqual(lines));
            check(P + "M4-2 [green] ...and leaves the old name's file as it was", File.ReadAllLines(oldPath).SequenceEqual(lines));

            // M4-3 [red]: Save As OVER a name that already has a file: the model's own content wins, the
            // file it replaces is kept beside it.
            var other = new[] { "enabled=False" };
            var target = Path.Combine(dir, "Existing.txt");
            File.WriteAllLines(target, other);
            carried = store.CarryTo(target, lines);
            check(P + "M4-3 [red] Save As over an existing name writes this model's settings and keeps the replaced file as .bak",
                carried && File.ReadAllLines(target).SequenceEqual(lines) && File.Exists(target + ".bak") && File.ReadAllLines(target + ".bak").SequenceEqual(other));

            // M4-4 [green]: a store whose own file could not be read has only defaults: it must not carry those.
            var locked = Path.Combine(dir, "Locked.txt");
            File.WriteAllLines(locked, lines);
            var bad = new RewrittenFile();
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) bad.Open(locked, null);
            var dest = Path.Combine(dir, "Locked-v2.txt");
            check(P + "M4-4 [green] an unreadable store does not carry defaults to the new name", !bad.CarryTo(dest, EnabledTrue) && !File.Exists(dest));

            // M4-5..7 (added with the fix): the append-only ledgers are carried by LedgerFile.CopyOver.
            var ledger = Path.Combine(dir, "Tower.status.txt");
            var ledgerLines = new[] { "# h", "1|0|2\tCoordinated\t2026-10-31T09:00:00.0000000Z\tu\t" };
            File.WriteAllLines(ledger, ledgerLines);
            var moved = Path.Combine(dir, "Tower-v2.status.txt");
            check(P + "M4-5 the triage file is carried to the new name byte for byte, the old one left alone",
                LedgerFile.CopyOver(ledger, moved) && File.ReadAllLines(moved).SequenceEqual(ledgerLines) && File.ReadAllLines(ledger).SequenceEqual(ledgerLines));
            var occupied = Path.Combine(dir, "Other.status.txt");
            File.WriteAllLines(occupied, SomeoneElse);
            check(P + "M4-6 a ledger already at the new name is kept as .bak, never merged",
                LedgerFile.CopyOver(ledger, occupied) && File.ReadAllLines(occupied).SequenceEqual(ledgerLines)
                && File.ReadAllLines(occupied + ".bak").SequenceEqual(SomeoneElse));
            var stale = Path.Combine(dir, "Stale.status.txt");
            File.WriteAllLines(stale, OtherModel);
            check(P + "M4-7 a model with nothing recorded does not inherit the new name's old ledger (set aside as .bak)",
                LedgerFile.CopyOver(Path.Combine(dir, "never-written.txt"), stale) && !File.Exists(stale) && File.Exists(stale + ".bak"));
        }

        // ================================================================== LOW-4

        private static ParameterFilter RoundTrip(ParameterFilter f)
        {
            var back = new ParameterFilter();
            var other = new ParameterFilter();
            bool cs = false, ct = false;
            foreach (var line in f.Serialize("s"))
            {
                int eq = line.IndexOf('=');
                ParameterFilter.ApplyFilterLine(back, other, line.Substring(0, eq), line.Substring(eq + 1), ref cs, ref ct);
            }
            return back;
        }

        private static void PickedValues(Action<string, bool?> check)
        {
            var f = new ParameterFilter();
            f.Picked.Add(new ValuePick { ParameterName = "System Type", Scope = ParamScope.Type, Value = "CHW|Supply" });
            f.Excluded.Add(new ValuePick { ParameterName = "Mark", Scope = ParamScope.Instance, Value = "A|B|C" });
            f.OnlyValues.Add(new ValuePick { ParameterName = "Comments", Value = "line one\r\nline two" });
            f.Rules.Add(new Rule { ParameterName = "Description", Op = RuleOp.Contains, Values = { "a|b", "x\ny" } });
            f.Groups.Add(new RuleGroup { Join = RuleJoin.Or, Rules = { new Rule { ParameterName = "Size", Op = RuleOp.Is, Values = { "50|65" } } } });
            var lines = f.Serialize("s");
            var b = RoundTrip(f);

            check(P + "L4-1 [red] a picked value containing '|' reads back exactly",
                b.Picked.Count == 1 && b.Picked[0].Value == "CHW|Supply" && b.Excluded.Count == 1 && b.Excluded[0].Value == "A|B|C");
            check(P + "L4-2 [red] a rule value containing '|' reads back exactly (flat and grouped)",
                b.Rules.Count == 1 && b.Rules[0].Values.Contains("a|b") && b.Groups.Count == 1 && b.Groups[0].Rules[0].Values.Contains("50|65"));
            check(P + "L4-3 [red] a multi-line value is written on one line and reads back whole",
                lines.All(l => l.IndexOf('\r') < 0 && l.IndexOf('\n') < 0)
                && b.OnlyValues.Count == 1 && b.OnlyValues[0].Value == "line one\r\nline two" && b.Rules[0].Values.Contains("x\ny"));

            // L4-4 [green]: what is already on disk reads as before ('/' stays '/').
            var legacy = new ParameterFilter();
            legacy.ApplyLine("pick", "Either|P|v/1");
            legacy.ApplyLine("rule", "0|Either|Contains|R|a/b");
            check(P + "L4-4 [green] files written before the fix read exactly as before",
                legacy.Picked.Count == 1 && legacy.Picked[0].Value == "v/1" && legacy.Rules[0].Values[0] == "a/b");

            // L4-5 [red]: property. Any value, any of the five kinds, round trips.
            var r = new Random(Seed + 2);
            const string alphabet = "ab |/\\\r\n\t=#%\u00e9\u001b\u001d\u001e0renn";
            string first = null;
            for (int i = 0; i < 500 && first == null; i++)
            {
                var sb = new StringBuilder();
                for (int j = 1 + r.Next(12); j > 0; j--) sb.Append(alphabet[r.Next(alphabet.Length)]);
                var v = sb.ToString();
                if (v.Trim().Length == 0) continue;
                var g = new ParameterFilter();
                switch (r.Next(5))
                {
                    case 0: g.Picked.Add(new ValuePick { ParameterName = "P", Value = v }); break;
                    case 1: g.Excluded.Add(new ValuePick { ParameterName = "P", Value = v }); break;
                    case 2: g.OnlyValues.Add(new ValuePick { ParameterName = "P", Value = v }); break;
                    case 3: g.Rules.Add(new Rule { ParameterName = "P", Op = RuleOp.Is, Values = { v } }); break;
                    default: g.Groups.Add(new RuleGroup { Rules = { new Rule { ParameterName = "P", Op = RuleOp.Is, Values = { v } } } }); break;
                }
                var gb = RoundTrip(g);
                string back = gb.Picked.Concat(gb.Excluded).Concat(gb.OnlyValues).Select(x => x.Value)
                    .Concat(gb.Rules.SelectMany(x => x.Values)).Concat(gb.Groups.SelectMany(x => x.Rules).SelectMany(x => x.Values)).FirstOrDefault();
                if (back != v) first = "case " + i + ": " + Show(v) + " came back " + Show(back);
            }
            check(P + "L4-5 [red] property: 500 random values with '|', CR, LF, tabs and control characters round trip exactly (seed " + (Seed + 2) + ")"
                  + (first == null ? "" : " - FIRST " + first), first == null);
        }

        private static string Show(string s)
        {
            if (s == null) return "(null)";
            var sb = new StringBuilder("\"");
            foreach (var c in s) sb.Append(c < ' ' ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString());
            return sb.Append('"').ToString();
        }

        // ================================================================== LOW-10

        private static void Fingerprint(Action<string, bool?> check)
        {
            // 50 mm in feet. .NET Framework's ToString() keeps 15 significant digits, .NET Core keeps the
            // shortest string that round-trips: the same rule, two different lines.
            const string Net48 = "0.164041994750656", Core = "0.1640419947506562";
            check(P + "L10-1 [red] the same clearance written by Revit 2024 (.NET 4.8) and 2025/2026 (.NET 8/10) fingerprints the same",
                RulesFingerprint.OfLines(new[] { "clearance=" + Net48, "setclearance=" + Net48 })
                == RulesFingerprint.OfLines(new[] { "clearance=" + Core, "setclearance=" + Core }));
            check(P + "L10-2 [green] different clearances still fingerprint differently (50 mm vs 51 mm)",
                RulesFingerprint.OfLines(new[] { "clearance=" + (50 / 304.8).ToString("R", CultureInfo.InvariantCulture) })
                != RulesFingerprint.OfLines(new[] { "clearance=" + (51 / 304.8).ToString("R", CultureInfo.InvariantCulture) }));

            // L10-3 [red]: property over every clearance from 0 to 500 mm in 0.5 mm steps: the 15-digit string
            // (.NET 4.8) and the round-trip string (.NET 8/10) fingerprint the same.
            int differ = 0, n = 0;
            for (double mm = 0; mm <= 500; mm += 0.5)
            {
                double ft = mm / 304.8;
                var a = ft.ToString("G15", CultureInfo.InvariantCulture);
                var b = ft.ToString("R", CultureInfo.InvariantCulture);
                if (RulesFingerprint.OfLines(new[] { "clearance=" + a }) != RulesFingerprint.OfLines(new[] { "clearance=" + b })) differ++;
                n++;
            }
            check(P + "L10-3 [red] property: " + n + " clearances fingerprint the same on both runtimes (" + differ + " differ)", differ == 0);
        }

        // ================================================================== edges (added with the fix)

        /// <summary>The new helpers' edges: bad arguments, a folder where a file should be, a target held
        /// by another process during the swap, escapes at the end of a value. Each must fail safe.</summary>
        private static void Edges(Action<string, bool?> check, string dir)
        {
            LedgerFile.ReadState st;
            var sub = Path.Combine(dir, "edges");
            Directory.CreateDirectory(sub);
            var f = Path.Combine(sub, "f.txt");

            check(P + "edge: reading no path is a failure, not 'missing'", LedgerFile.Read(null, out st) == null && st == LedgerFile.ReadState.Failed);
            check(P + "edge: appending nothing, or to no path, writes nothing",
                !LedgerFile.Append(null, null, "x\n") && !LedgerFile.Append(f, null, "") && !File.Exists(f));
            File.WriteAllText(f, "");
            check(P + "edge: an empty existing file with no header gets just the record",
                LedgerFile.Append(f, null, "a\n") && File.ReadAllText(f) == "a\n");
            int w0 = Log.WarningCount;
            check(P + "edge: appending to a folder fails with a warning, not a throw", !LedgerFile.Append(sub, null, "a\n") && Log.WarningCount == w0 + 1);
            using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
                check(P + "edge: appending to a file held past the retry fails with a warning", !LedgerFile.Append(f, null, "b\n") && Log.WarningCount == w0 + 2);

            check(P + "edge: a whole write with no path or no lines writes nothing", !LedgerFile.WriteWhole(null, OneX) && !LedgerFile.WriteWhole(f, null));
            using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
                check(P + "edge: a whole write over a file held past the retry fails, leaving it and no temp file",
                    !LedgerFile.WriteWhole(f, OneNew));
            check(P + "edge: ...the held file is unchanged and nothing is left beside it",
                File.ReadAllText(f) == "a\n" && Directory.GetFiles(sub, "f.txt.tmp*").Length == 0);
            check(P + "edge: a whole write into a folder that does not exist fails safe",
                !LedgerFile.WriteWhole(Path.Combine(sub, "no-such-dir", "x.txt"), OneX));

            check(P + "edge: carrying with no path does nothing; onto itself is already done",
                !LedgerFile.CopyOver(null, f) && !LedgerFile.CopyOver(f, "") && LedgerFile.CopyOver(f, f.ToUpperInvariant()));
            var g = Path.Combine(sub, "g.txt");
            File.WriteAllText(g, "old");
            File.WriteAllText(g + ".bak", "older");
            check(P + "edge: setting a stale ledger aside replaces an older .bak",
                LedgerFile.CopyOver(Path.Combine(sub, "none.txt"), g) && !File.Exists(g) && File.ReadAllText(g + ".bak") == "old");
            File.WriteAllText(g, "held");
            using (new FileStream(g, FileMode.Open, FileAccess.Read, FileShare.None))
                check(P + "edge: carrying onto a file held by another process fails, leaving no temp file",
                    !LedgerFile.CopyOver(f, g) && Directory.GetFiles(sub, "g.txt.tmp*").Length == 0);

            // RewrittenFile
            var rf = new RewrittenFile();
            check(P + "edge: a store with no file yet refuses to save", !rf.Save(OneX));
            var pre = new[] { "pre" };
            check(P + "edge: lines the caller already read are used as they are", ReferenceEquals(rf.Open(f, pre), pre) && !rf.Unreadable && !rf.NeedsOpen(f) && rf.NeedsOpen(g));
            var copy = rf.Copy();
            check(P + "edge: a copy has the same state", copy.Path == rf.Path && copy.Unreadable == rf.Unreadable);
            check(P + "edge: carrying to no path or with no lines does nothing", !rf.CarryTo(null, pre) && !rf.CarryTo(g, null) && rf.Path == f);
            check(P + "edge: a carry that cannot be written keeps the old path",
                !rf.CarryTo(Path.Combine(sub, "no-such-dir", "x.txt"), pre) && rf.Path == f);
            var held = new RewrittenFile();
            int w1 = Log.WarningCount;
            using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                held.Open(f, null);
                held.Open(f, null);
            }
            check(P + "edge: a file that stays unreadable is warned about once, not on every retry",
                held.Unreadable && Log.WarningCount == w1 + 1);

            // RulesFingerprint
            check(P + "edge: fingerprint of nothing, and null lines skipped",
                RulesFingerprint.OfLines(null) == RulesFingerprint.OfLines(Array.Empty<string>())
                && RulesFingerprint.OfLines(A1Null) == RulesFingerprint.OfLines(A1));
            check(P + "edge: a length that does not parse, or is absurd, is hashed as written",
                RulesFingerprint.Canonical("clearance=abc") == "clearance=abc"
                && RulesFingerprint.Canonical("clearance=1e300") == "clearance=1e300"
                && RulesFingerprint.Canonical("clearance=NaN") == "clearance=NaN"
                && RulesFingerprint.Canonical("source=OST_Walls") == "source=OST_Walls"
                && RulesFingerprint.Canonical("groupdist=6.5") == "groupdist=um:1981200");

            // ParameterFilter values
            check(P + "edge: a value from no fields, or past the end, is empty",
                ParameterFilter.ValueFrom(null, 0) == "" && ParameterFilter.ValueFrom(OneA, 1) == "" && ParameterFilter.Value(null) == "");
            check(P + "edge: an escape character at the end, or before an unknown letter, stays as it is",
                ParameterFilter.ValueFrom(EscEnd, 0) == "a\u001b" && ParameterFilter.ValueFrom(EscX, 0) == "\u001bx");

            // Log
            Log.NoteSensitive(null);
            Log.NoteSensitive("  ");
            Log.NoteSensitive("ab");
            Log.NoteSensitive("Edge-Model-X");
            Log.NoteSensitive("edge-model-x");
            check(P + "edge: short and blank names are not noted; a name noted twice is one tag",
                Log.Scrub("ab Edge-Model-X") == "ab " + Log.Tag("edge-model-x") && Log.Scrub(null) == null && Log.Scrub("") == "" && Log.Tag(null) == Log.Tag(""));
            check(P + "edge: a missing or too-short folder or user name scrubs nothing",
                Log.ReplaceFolder("C:\\x", null, "T") == "C:\\x" && Log.ReplaceFolder("C:\\x", "C:", "T") == "C:\\x"
                && Log.ReplaceUser("bob was here", null) == "bob was here" && Log.ReplaceUser("jo was here", "jo") == "jo was here"
                && Log.ReplaceUser("jdoe was here, not jdoes", "JDOE") == "<user> was here, not jdoes"
                && Log.ReplaceUser("xjdoe", "jdoe") == "xjdoe");
        }

        // ================================================================== LOW-11

        private static void LogScrub(Action<string, bool?> check, string dir)
        {
            var file = Path.Combine(dir, "scrub.log");
            string user = Environment.UserName ?? "";
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "";
            string text;
            using (Log.RedirectForTests(file))
            {
                Log.NoteSensitive("PRJ01-Tower-MEP");
                Log.Info("Settings: loaded this model's own file for 'PRJ01-Tower-MEP'");
                Log.Info("Link PRJ01-Tower-MEP reloaded from " + Path.Combine(profile, "Projects", "model.rvt"));
                Log.Warn("Could not read", new IOException("The process cannot access the file '" + Path.Combine(profile, "AppData", "x.txt") + "'"));
                Log.Info("Opened D:\\Jobs\\ClientCo\\PRJ01\\Tower-ST.rvt");
                text = File.ReadAllText(file);
            }
            check(P + "L11-1 [red] a model title noted as sensitive never reaches the log as written", !text.Contains("PRJ01-Tower-MEP"));
            check(P + "L11-2 [red] ...it is replaced by the same short tag every time, so the log is still readable",
                System.Text.RegularExpressions.Regex.Matches(text, "model#[0-9a-f]+").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value).Distinct().Count() == 1
                && System.Text.RegularExpressions.Regex.Matches(text, "model#[0-9a-f]+").Cast<System.Text.RegularExpressions.Match>().Count() == 2);
            check(P + "L11-3 [red] the Windows profile path (and so the user name) is not in the log, in messages or in exceptions",
                profile.Length > 0 && !text.Contains(profile) && (user.Length < 3 || text.IndexOf(user, StringComparison.OrdinalIgnoreCase) < 0));
            check(P + "L11-4 [red] a path outside the profile keeps its file name and loses its folders",
                text.Contains("Tower-ST.rvt") && !text.Contains("ClientCo") && !text.Contains("\\Jobs\\"));
            check(P + "L11-5 [green] the message itself is still there", text.Contains("Settings: loaded this model's own file for"));
        }
    }
}
