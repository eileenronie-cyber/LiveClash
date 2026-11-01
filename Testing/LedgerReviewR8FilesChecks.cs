using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8 of the 2026-10-31 review, the "files" builder, Revit-free half: the refused-save wording (D3-4), the held link rows across
    /// Save As and close (D3-2 b, c), a Save As while a file is unreadable (SP7: a locked file, and a file with corrupt content) and the key
    /// a Save As carries from (DL-f). The predictions were committed before this file existed:
    /// tests\evidence\liveclash\r8-files\PREDICTIONS.md (441a5ca). The in-Revit half is Testing\ReviewChecks.R8Files.cs.
    /// </summary>
    public static class LedgerReviewR8FilesChecks
    {
        private const string P = "r8 files: ";

        private static readonly string[] Plain = { "enabled=True", "set.begin=0", "setname=Default", "set.end=0" };

        public static void RunAll(Action<string, bool?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "liveclash-r8files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var savedClock = RetryBackoff.Clock;
            try
            {
                using (Log.RedirectForTests(Path.Combine(dir, "test.log")))
                {
                    Wording(check);
                    HeldRows(check);
                    Unreadable(check, dir);
                    CopyFailures(check, dir);
                    UndeliveredNotices(check);
                    ScratchFolders(check);
#if LOGICTESTS_HEADLESS
                    ScratchFolderRealShortName(check);
                    ScratchFolderWildcards(check);
                    ManifestGuard(check);
#endif
                    FromKeys(check);
                }
            }
            finally
            {
                RetryBackoff.Clock = savedClock;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static bool Has(string text, string part)
        {
            return System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text ?? "", part, System.Globalization.CompareOptions.IgnoreCase) >= 0;
        }

        // ================================================================== D3-4: the sentence

        private static void Wording(Action<string, bool?> check)
        {
            var s = SaveNotice.After(false, false) ?? "";
            check(P + "SN-2b [red] the refused-save sentence does not promise 'this session' (" + s + ")", s.Length > 0 && !Has(s, "this session"));
        }

        // ================================================================== D3-2 (b), (c): the held link rows

        private static ClashResult Row(string key)
        {
            return new ClashResult { Key = key, ModelName = "Structure", Severity = ClashSeverity.Hard, HostElementName = "h " + key, OtherElementName = "o " + key };
        }

        private static void HeldRows(Action<string, bool?> check)
        {
            string u = Guid.NewGuid().ToString("N").Remove(8);
            Func<string, string> k = n => "r8hr-" + u + "-" + n;

            // HR-1
            string a = k("a1"), b = k("b1");
            DroppedLinkRows.Hold(a, 7, new[] { Row("1|7|2"), Row("1|7|3") });
            DroppedLinkRows.Hold(a, 8, new[] { Row("1|8|2") });
            int moved = SaveAsCarry.RekeyHeldRows(a, b);
            check(P + "HR-1 [red] Save As: the rows held under the old key are held under the new one (" + DroppedLinkRows.Count(b) + " of 3, " + DroppedLinkRows.Count(a) + " left behind, " + moved + " moved)",
                moved == 3 && DroppedLinkRows.Count(b) == 3 && DroppedLinkRows.Count(a) == 0);

            // HR-2
            a = k("a2"); b = k("b2");
            DroppedLinkRows.Hold(a, 7, new[] { Row("2|7|2"), Row("2|7|3") });
            DroppedLinkRows.Hold(a, 8, new[] { Row("2|8|2") });
            SaveAsCarry.RekeyHeldRows(a, b);
            var back = DroppedLinkRows.TakeBack(b, id => id == 7);
            check(P + "HR-2 [red] ...and they stay grouped by link instance: an undo of ONE instance brings back only its pairs (" + back.Count + " back, " + DroppedLinkRows.Count(b) + " still held)",
                back.Count == 2 && back.All(r => r.Key.StartsWith("2|7|", StringComparison.Ordinal)) && DroppedLinkRows.Count(b) == 1);

            // HR-3
            a = k("a3"); b = k("b3");
            DroppedLinkRows.Hold(a, 7, new[] { Row("3|7|2"), Row("3|7|3") });
            DroppedLinkRows.Hold(b, 7, new[] { Row("3|7|3"), Row("3|7|4") });
            SaveAsCarry.RekeyHeldRows(a, b);
            check(P + "HR-3 [red] ...rows already held under the new key are merged, a pair is held once (" + DroppedLinkRows.Count(b) + " of 3)",
                DroppedLinkRows.Count(b) == 3 && DroppedLinkRows.Count(a) == 0);

            // HR-4
            a = k("a4"); b = k("b4"); string c = k("c4");
            DroppedLinkRows.Hold(a, 7, new[] { Row("4|7|2") });
            DroppedLinkRows.Hold(c, 9, new[] { Row("4|9|2") });
            SaveAsCarry.RekeyHeldRows(a, b);
            check(P + "HR-4 [green] another document's held rows are not touched by the re-key (" + DroppedLinkRows.Count(c) + " of 1)", DroppedLinkRows.Count(c) == 1);

            // HR-5
            a = k("a5");
            DroppedLinkRows.Hold(a, 7, new[] { Row("5|7|2") });
            bool threw = false; int n;
            try
            {
                n = SaveAsCarry.RekeyHeldRows(a, a) + SaveAsCarry.RekeyHeldRows(null, k("b5")) + SaveAsCarry.RekeyHeldRows(a, null)
                    + SaveAsCarry.RekeyHeldRows("", k("b5")) + SaveAsCarry.RekeyHeldRows(a, "");
                SaveAsCarry.ForgetHeldRows(null); SaveAsCarry.ForgetHeldRows("");
            }
            catch { threw = true; n = -1; }
            check(P + "HR-5 [green] the same key, a null or an empty key moves nothing and never throws (" + n + " moved, " + DroppedLinkRows.Count(a) + " held)",
                !threw && n == 0 && DroppedLinkRows.Count(a) == 1 && DroppedLinkRows.Count(k("b5")) == 0);

            // HR-6
            a = k("a6");
            DroppedLinkRows.Hold(a, 7, new[] { Row("6|7|2") });
            SaveAsCarry.ForgetHeldRows(a);
            var after = DroppedLinkRows.TakeBack(a, id => true);
            check(P + "HR-6 [red] closing a model lets go of its held rows, so a reopen cannot say 'their link came back' (" + DroppedLinkRows.Count(a) + " held, " + after.Count + " would come back)",
                DroppedLinkRows.Count(a) == 0 && after.Count == 0);
        }

        // ================================================================== SP7: Save As while a file is unreadable

        private static string[] ReadOrNull(string path)
        {
            try { return File.ReadAllLines(path); } catch { return null; }
        }

        private static bool StartsWith(byte[] whole, byte[] prefix)
        {
            return whole != null && whole.Length >= prefix.Length && whole.Take(prefix.Length).SequenceEqual(prefix);
        }

        // Bytes that are not text in any encoding the code reads: a file that will not make sense once read.
        private static readonly byte[] Corrupt = { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0x80, 0xC3, 0x28, 0x00, 0x00, 0x7F, 0xFF };

        private static void Unreadable(Action<string, bool?> check, string dir)
        {
            var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            RetryBackoff.Clock = () => now;
            // The file is locked at open, so the store is flagged unreadable; the caller then releases the lock or keeps it.
            Func<string, RewrittenFile> unreadableAt = path =>
            {
                var rf = new RewrittenFile("Settings");
                rf.Open(path, null);
                return rf;
            };

            // ---- a locked file whose lock is gone by the time of the Save As (the retry has not run yet)
            var old1 = Path.Combine(dir, "sp7-old1.txt");
            var new1 = Path.Combine(dir, "sp7-new1.txt");
            File.WriteAllLines(old1, Plain);
            RewrittenFile rf1;
            var lk = new FileStream(old1, FileMode.Open, FileAccess.Read, FileShare.None);
            try { rf1 = unreadableAt(old1); } finally { lk.Dispose(); }
            bool flagged = rf1.Unreadable;
            bool carried = rf1.CarryUnreadableTo(new1, "id-r8");
            var expect = Plain.Concat(new[] { ModelIdentity.Line("id-r8") }).ToArray();
            var got = ReadOrNull(new1);
            check(P + "SP7-1 [red] a store flagged unreadable (its lock is gone by the Save As) carries the file's content to the new name, plus the new identity"
                  + " (flagged " + flagged + ", carried " + carried + ", " + (got == null ? "no file" : got.Length + " line(s)") + ")",
                flagged && carried && got != null && got.SequenceEqual(expect) && rf1.Path == new1);

            now = now.AddSeconds(6);
            var lines = rf1.TryReopen();
            check(P + "SP7-2 [red] ...and the existing retry then reads the carried file and clears the flag"
                  + " (" + (lines == null ? "nothing read" : lines.Length + " line(s)") + ", unreadable " + rf1.Unreadable + ")",
                lines != null && lines.SequenceEqual(expect) && !rf1.Unreadable && rf1.Path == new1);

            // ---- a file that is still locked: nothing can be copied, nothing is created, nothing changes
            var old3 = Path.Combine(dir, "sp7-old3.txt");
            var new3 = Path.Combine(dir, "sp7-new3.txt");
            File.WriteAllLines(old3, Plain);
            bool c3; RewrittenFile rf3;
            lk = new FileStream(old3, FileMode.Open, FileAccess.Read, FileShare.None);
            try { rf3 = unreadableAt(old3); c3 = rf3.CarryUnreadableTo(new3, "id-r8"); } finally { lk.Dispose(); }
            check(P + "SP7-3 [green] a file that is still locked is not carried: no new file, the old one unchanged, the store still points at it"
                  + " (carried " + c3 + ", new file " + File.Exists(new3) + ")",
                !c3 && !File.Exists(new3) && rf3.Path == old3 && rf3.Unreadable && File.ReadAllLines(old3).SequenceEqual(Plain)
                && Directory.GetFiles(dir, "sp7-new3*").Length == 0);

            // ---- the sentence, and the existing refusal of the parsed carry
            var s = SaveNotice.CarryRefused("settings") ?? "";
            check(P + "SP7-4 [red] the sentence for a Save As that could not carry a file says what was not copied, and where the original is (" + s + ")",
                Has(s, "Save As") && Has(s, "settings") && Has(s, "not copied") && Has(s, "old name"));

            var old5 = Path.Combine(dir, "sp7-old5.txt");
            var new5 = Path.Combine(dir, "sp7-new5.txt");
            File.WriteAllLines(old5, Plain);
            RewrittenFile rf5;
            lk = new FileStream(old5, FileMode.Open, FileAccess.Read, FileShare.None);
            try { rf5 = unreadableAt(old5); } finally { lk.Dispose(); }
            check(P + "SP7-5 [green] CarryTo (the parsed content) still refuses while unreadable: defaults are not planted under the new name",
                !rf5.CarryTo(new5, Plain) && !File.Exists(new5));

            // ---- a file with corrupt content: the bytes are carried as they are, and the retry reads them
            var old6 = Path.Combine(dir, "sp7-old6.txt");
            var new6 = Path.Combine(dir, "sp7-new6.txt");
            File.WriteAllBytes(old6, Corrupt);
            RewrittenFile rf6;
            lk = new FileStream(old6, FileMode.Open, FileAccess.Read, FileShare.None);
            try { rf6 = unreadableAt(old6); } finally { lk.Dispose(); }
            bool c6 = rf6.CarryUnreadableTo(new6, "id-r8");
            byte[] b6 = File.Exists(new6) ? File.ReadAllBytes(new6) : null;
            now = now.AddSeconds(6);
            var l6 = c6 ? rf6.TryReopen() : null;
            check(P + "SP7-6 [red] a file with corrupt content is carried byte for byte, and the retry reads it (carried " + c6 + ", "
                  + (b6 == null ? "no file" : b6.Length + " byte(s)") + ", read " + (l6 != null) + ")",
                c6 && StartsWith(b6, Corrupt) && l6 != null && !rf6.Unreadable && rf6.Path == new6);

            // ---- the whole chain with the notice: a lock blocks the copy, so the user is told; a carry says nothing
            var old7 = Path.Combine(dir, "sp7-old7.txt");
            var new7 = Path.Combine(dir, "sp7-new7.txt");
            File.WriteAllLines(old7, Plain);
            var said7 = new List<string>();
            bool c7; RewrittenFile rf7;
            lk = new FileStream(old7, FileMode.Open, FileAccess.Read, FileShare.None);
            try { rf7 = unreadableAt(old7); c7 = SaveAsCarry.CarryUnreadable(rf7, new7, "id-r8", "settings", said7.Add); } finally { lk.Dispose(); }
            check(P + "SP7-7 [red] a locked file that cannot be copied is SAID, once, with the file named (carried " + c7 + ", said " + said7.Count + ": " + string.Join(" | ", said7) + ")",
                !c7 && said7.Count == 1 && Has(said7[0], "settings") && Has(said7[0], "Save As") && !File.Exists(new7));

            var old8 = Path.Combine(dir, "sp7-old8.txt");
            var new8 = Path.Combine(dir, "sp7-new8.txt");
            File.WriteAllLines(old8, Plain);
            var said8 = new List<string>();
            RewrittenFile rf8;
            lk = new FileStream(old8, FileMode.Open, FileAccess.Read, FileShare.None);
            try { rf8 = unreadableAt(old8); } finally { lk.Dispose(); }
            bool c8 = SaveAsCarry.CarryUnreadable(rf8, new8, "id-r8", "settings", said8.Add);
            check(P + "SP7-8 [red] a carry that worked says nothing (carried " + c8 + ", said " + said8.Count + ")", c8 && said8.Count == 0 && File.Exists(new8));

            // ---- the ledgers: no RewrittenFile, a raw byte copy of the file
            var old9 = Path.Combine(dir, "sp7-old9.txt");
            var new9 = Path.Combine(dir, "sp7-new9.txt");
            File.WriteAllLines(old9, Plain);
            var said9 = new List<string>();
            bool c9;
            lk = new FileStream(old9, FileMode.Open, FileAccess.Read, FileShare.None);
            try { c9 = SaveAsCarry.CarryUnreadable(old9, new9, "id-r8", "triage", said9.Add); } finally { lk.Dispose(); }
            check(P + "SP7-9 [red] a ledger that is locked cannot be copied, and is SAID, naming the ledger (carried " + c9 + ", said " + said9.Count + ": " + string.Join(" | ", said9) + ")",
                !c9 && said9.Count == 1 && Has(said9[0], "triage") && !File.Exists(new9));

            var old10 = Path.Combine(dir, "sp7-old10.txt");
            var new10 = Path.Combine(dir, "sp7-new10.txt");
            File.WriteAllBytes(old10, Corrupt);
            var said10 = new List<string>();
            bool c10 = SaveAsCarry.CarryUnreadable(old10, new10, "id-r8", "triage", said10.Add);
            byte[] b10 = File.Exists(new10) ? File.ReadAllBytes(new10) : null;
            check(P + "SP7-10 [red] a ledger with corrupt content is carried byte for byte and nothing is said (carried " + c10 + ", said " + said10.Count + ")",
                c10 && said10.Count == 0 && StartsWith(b10, Corrupt));
        }

        // ================================================================== Addendum 2: a READABLE file whose copy fails at Save As

        private static void CopyFailures(Action<string, bool?> check, string dir)
        {
            // CF-1: the old file is readable, but another handle holds it at the instant of the copy.
            var old1 = Path.Combine(dir, "cf-old1.txt");
            var new1 = Path.Combine(dir, "cf-new1.txt");
            File.WriteAllLines(old1, Plain);
            var said1 = new List<string>();
            bool c1;
            using (new FileStream(old1, FileMode.Open, FileAccess.Read, FileShare.None))
                c1 = SaveAsCarry.CarryReadable(old1, new1, "id-r8", "triage", said1.Add);
            check(P + "CF-1 [red] a readable ledger whose copy fails (held at that instant) is SAID, once, naming the ledger and that it could not be copied"
                  + " (carried " + c1 + ", said " + said1.Count + ": " + string.Join(" | ", said1) + ")",
                !c1 && said1.Count == 1 && Has(said1[0], "triage") && Has(said1[0], "could not be copied") && !File.Exists(new1));

            // CF-2: the same, not held: carried, nothing said.
            var old2 = Path.Combine(dir, "cf-old2.txt");
            var new2 = Path.Combine(dir, "cf-new2.txt");
            File.WriteAllLines(old2, Plain);
            var said2 = new List<string>();
            bool c2 = SaveAsCarry.CarryReadable(old2, new2, "id-r8", "triage", said2.Add);
            var got2 = ReadOrNull(new2);
            check(P + "CF-2 [red] a readable ledger that is copied says nothing, and the new file holds the lines plus the identity (carried " + c2 + ", said " + said2.Count + ")",
                c2 && said2.Count == 0 && got2 != null && got2.SequenceEqual(Plain.Concat(new[] { ModelIdentity.Line("id-r8") })));

            // CF-3: a parsed carry of a readable store to a path that cannot be written.
            var rf3 = new RewrittenFile("Settings");
            var old3 = Path.Combine(dir, "cf-old3.txt");
            File.WriteAllLines(old3, Plain);
            rf3.Open(old3, null);
            var said3 = new List<string>();
            var bad = Path.Combine(dir, "cf-no-such-folder", "cf-new3.txt");
            bool c3 = SaveAsCarry.CarryParsed(rf3, bad, Plain, "settings", said3.Add);
            check(P + "CF-3 [red] a parsed carry that cannot be written is SAID, once, naming the file (carried " + c3 + ", said " + said3.Count + ": " + string.Join(" | ", said3) + ")",
                !c3 && said3.Count == 1 && Has(said3[0], "settings") && Has(said3[0], "could not be copied") && rf3.Path == old3);

            // CF-4: and one that works says nothing.
            var new4 = Path.Combine(dir, "cf-new4.txt");
            var said4 = new List<string>();
            bool c4 = SaveAsCarry.CarryParsed(rf3, new4, Plain, "settings", said4.Add);
            check(P + "CF-4 [red] a parsed carry that works says nothing and writes the file (carried " + c4 + ", said " + said4.Count + ")",
                c4 && said4.Count == 0 && File.Exists(new4) && rf3.Path == new4);

            // CF-5: the sentence is its own (the file COULD be read), not the unreadable-file one.
            var s = SaveNotice.CopyFailed("triage") ?? "";
            check(P + "CF-5 [red] the sentence for a copy that failed says Save As, what, that it could not be copied, and where the original is; it is not the unreadable-file sentence (" + s + ")",
                Has(s, "Save As") && Has(s, "triage") && Has(s, "could not be copied") && Has(s, "old name") && s != (SaveNotice.CarryRefused("triage") ?? ""));
        }

        // ================================================================== L-4: a sentence raised while nobody listens is kept

        private static void UndeliveredNotices(Action<string, bool?> check)
        {
            // NQ-1: no subscriber: kept, taken once, then gone.
            var q1 = new NoticeHolder();
            q1.Raise("a sentence", null);
            var t1 = q1.Take();
            var t1b = q1.Take();
            check(P + "NQ-1 [red] a sentence raised with nobody listening is kept, taken once, then gone (" + t1.Count + " then " + t1b.Count + ")",
                t1.Count == 1 && t1[0] == "a sentence" && t1b.Count == 0);

            // NQ-2: a subscriber gets it and nothing is kept.
            var q2 = new NoticeHolder();
            var got2 = new List<string>();
            q2.Raise("to the panel", got2.Add);
            check(P + "NQ-2 [red] a sentence raised with a listener goes to it and is not kept (listener " + got2.Count + ", kept " + q2.Take().Count + ")",
                got2.Count == 1 && got2[0] == "to the panel" && q2.Take().Count == 0);

            // NQ-3: bounded, newest kept, in order.
            var q3 = new NoticeHolder();
            for (int i = 1; i <= 12; i++) q3.Raise("s" + i, null);
            var t3 = q3.Take();
            check(P + "NQ-3 [red] at most " + NoticeHolder.Capacity + " are kept: the newest, oldest first (" + string.Join(",", t3) + ")",
                t3.Count == NoticeHolder.Capacity && t3[0] == "s5" && t3[t3.Count - 1] == "s12");

            // NQ-4: consecutive duplicates collapse.
            var q4 = new NoticeHolder();
            q4.Raise("same", null); q4.Raise("same", null); q4.Raise("same", null); q4.Raise("other", null); q4.Raise("same", null);
            var t4 = q4.Take();
            check(P + "NQ-4 [red] a sentence repeated straight after itself is kept once (" + string.Join(",", t4) + ")",
                t4.Count == 3 && t4[0] == "same" && t4[1] == "other" && t4[2] == "same");

            // NQ-5: a throwing listener never throws out, and nothing is kept for it.
            var q5 = new NoticeHolder();
            bool threw = false;
            try { q5.Raise("boom", s => { throw new InvalidOperationException("listener failed"); }); }
            catch { threw = true; }
            check(P + "NQ-5 [green] a listener that throws never throws out of Raise, and the sentence is not kept (threw " + threw + ", kept " + q5.Take().Count + ")",
                !threw && q5.Take().Count == 0);

            // NQ-6: order kept, Take clears.
            var q6 = new NoticeHolder();
            q6.Raise("first", null); q6.Raise("second", null);
            var t6 = q6.Take();
            check(P + "NQ-6 [red] the sentences come back in the order raised, and a second Take is empty (" + string.Join(",", t6) + ")",
                t6.Count == 2 && t6[0] == "first" && t6[1] == "second" && q6.Take().Count == 0);

            // NQ-7: nothing, empty or null text is not kept.
            var q7 = new NoticeHolder();
            q7.Raise(null, null); q7.Raise("", null);
            check(P + "NQ-7 [green] a null or empty sentence is not kept", q7.Take().Count == 0);
        }

        // ================================================================== the probe's scratch-folder check (Addendum 6)

        private static readonly string[] TempOnly = { @"C:\Temp" };
        private static readonly string[] DriveOnly = { @"D:\" };

        // A stand-in for GetLongPathName that knows one short name, so the rule is tested on every machine.
        private static string FakeLong(string p)
        {
            return (p ?? "").Replace("LONGNA~1", "LongName");
        }

        private static void ScratchFolders(Action<string, bool?> check)
        {
            const string prefix = "liveclash-undoprobe-";
            string detail;
            string root = @"D:\Profiles\LongName\AppData\Local\Temp";

            bool ok1 = ScratchFolder.Check(@"D:\Profiles\LONGNA~1\AppData\Local\Temp\liveclash-undoprobe-ab12cd34", prefix, new[] { root }, FakeLong, out detail);
            check(P + "SF-1 [red] the same folder spelled short (8.3) on one side and long on the other is accepted (" + detail + ")", ok1);

            bool ok2 = ScratchFolder.Check(@"d:\profiles\longname\appdata\local\temp\sub\..\liveclash-undoprobe-ab12cd34\", prefix, new[] { root + "\\" }, FakeLong, out detail);
            check(P + "SF-2 [red] a trailing separator, a different case and a '..' step do not matter (" + detail + ")", ok2);

            bool ok3 = ScratchFolder.Check(@"D:\elsewhere\liveclash-undoprobe-x", prefix, new[] { root, @"C:\Other\Tmp" }, FakeLong, out detail);
            check(P + "SF-3 [red] a folder under no root is refused, and the refusal names the folder AND every root (" + detail + ")",
                !ok3 && Has(detail, @"D:\elsewhere\liveclash-undoprobe-x") && Has(detail, root) && Has(detail, @"C:\Other\Tmp"));

            bool ok4 = ScratchFolder.Check(root + @"\scratch-1", prefix, new[] { root }, FakeLong, out detail);
            check(P + "SF-4 [green] a folder under a root without the prefix is refused (" + detail + ")", !ok4);

            bool ok5 = ScratchFolder.Check(@"C:\Temp2\liveclash-undoprobe-x", prefix, TempOnly, FakeLong, out detail);
            check(P + "SF-5 [green] a sibling folder whose name merely starts with the root's name is refused (" + detail + ")", !ok5);

            bool ok7 = ScratchFolder.Check(root + @"\liveclash-undoprobe-ab12cd34", prefix, new[] { @"C:\A", root }, FakeLong, out detail);
            check(P + "SF-7 [red] one matching root among several is enough (" + detail + ")", ok7);

            // SF-8: edges of the normalisation. Nothing throws; an unusable resolver leaves the spelling as it was.
            Func<string, string> throwing = p => { throw new InvalidOperationException("resolver failed"); };
            Func<string, string> nothing = p => null;
            string n1 = ScratchFolder.Normalize(null, FakeLong), n2 = ScratchFolder.Normalize("   ", FakeLong), n3 = ScratchFolder.Normalize(@"C:\", FakeLong);
            string n4 = ScratchFolder.Normalize(@"C:\a\b", throwing), n5 = ScratchFolder.Normalize(@"C:\a\b\", nothing), n6 = ScratchFolder.Normalize(@"C:/a//b", FakeLong);
            check(P + "SF-8 [green] empty and root paths, a resolver that throws or returns nothing, and forward slashes are all handled ('" + n1 + "', '" + n3 + "', '" + n4 + "', '" + n5 + "', '" + n6 + "')",
                n1 == "" && n2 == "" && n3 == @"C:\" && n4 == @"C:\a\b" && n5 == @"C:\a\b" && n6 == @"C:\a\b");

            // SF-9: LongPath on real and missing paths, and a Check with no roots, no prefix, or the root itself.
            string temp = Path.GetTempPath().TrimEnd('\\');
            string missing = Path.Combine(temp, "r8-no-such-folder-" + Guid.NewGuid().ToString("N").Remove(8), "child");
            bool longOk = ScratchFolder.LongPath(null) == null && ScratchFolder.LongPath("") == "" && ScratchFolder.LongPath(@"C:\") == @"C:\"
                          && ScratchFolder.LongPath(missing) == missing
                          && string.Equals(ScratchFolder.LongPath(temp), ScratchFolder.LongPath(ScratchFolder.LongPath(temp)), StringComparison.OrdinalIgnoreCase);
            // A path that cannot be a path (an embedded NUL) is handed back as it came, never thrown at the caller.
            string bad = temp + "\\bad\0name";
            longOk = longOk && ScratchFolder.LongPath(temp + "\\x\0y") == temp + "\\x\0y" && ScratchFolder.Normalize(bad, FakeLong) == bad.Trim();
            string dNone, dRoot;
            bool noRoots = ScratchFolder.Check(root + @"\liveclash-undoprobe-x", prefix, null, FakeLong, out dNone);
            bool noPrefix = ScratchFolder.Check(root + @"\anything", null, new[] { root }, FakeLong, out detail);
            bool isRoot = ScratchFolder.Check(root, "", new[] { root }, FakeLong, out dRoot);
            // SF-12: a share root, a drive root as the temp root, and an empty folder.
            string dEmpty, dDrive;
            string unc = ScratchFolder.Normalize(@"\\srv\share", FakeLong);
            bool underDrive = ScratchFolder.Check(@"D:\liveclash-undoprobe-x", prefix, DriveOnly, FakeLong, out dDrive);
            bool emptyDir = ScratchFolder.Check("", prefix, new[] { root }, FakeLong, out dEmpty);
            check(P + "SF-12 [green] a share root is kept as it is, a folder directly under a drive root is under it, an empty folder is refused (" + unc + ")",
                unc == @"\\srv\share" && underDrive && !emptyDir);

            check(P + "SF-9 [green] LongPath leaves missing and root paths alone; no roots refuses and says 'none'; no prefix accepts any folder under a root; the root itself is not under itself",
                longOk && !noRoots && Has(dNone, "none") && noPrefix && !isRoot);
        }

#if LOGICTESTS_HEADLESS
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int bufferSize);

        private static void ScratchFolderRealShortName(Action<string, bool?> check)
        {
            var parent = Path.Combine(Path.GetTempPath(), "r8 scratch folder with a long name " + Guid.NewGuid().ToString("N").Remove(8));
            var child = Path.Combine(parent, "liveclash-undoprobe-abcd1234");
            try
            {
                Directory.CreateDirectory(child);
                var sb = new System.Text.StringBuilder(520);
                int n = GetShortPathName(child, sb, sb.Capacity);
                string shortName = n > 0 ? sb.ToString() : child;
                if (string.Equals(shortName, child, StringComparison.OrdinalIgnoreCase))
                {
                    check(P + "SF-6 (this volume does not give the folder an 8.3 name, so the real short-name case did not run here)", (bool?)null);
                    return;
                }
                string detail;
                bool ok = ScratchFolder.Check(shortName, "liveclash-undoprobe-", new[] { Path.GetTempPath() }, ScratchFolder.LongPath, out detail);
                check(P + "SF-6 [red] a real folder reached through its real 8.3 name is accepted (" + detail + ")", ok);
            }
            finally { try { Directory.Delete(parent, true); } catch { } }
        }

        /// <summary>D-9: a folder name is a NAME, never a search pattern. A '*' or '?' in a job's scratch folder must not resolve to some other
        /// folder that matches it; a '[' (legal in a file name, not special to Windows) still resolves to itself.</summary>
        private static void ScratchFolderWildcards(Action<string, bool?> check)
        {
            var parent = Path.Combine(Path.GetTempPath(), "r8 wild test " + Guid.NewGuid().ToString("N").Remove(8));
            try
            {
                Directory.CreateDirectory(Path.Combine(parent, "r8-wild-A"));
                Directory.CreateDirectory(Path.Combine(parent, "r8 [x] folder"));
                string star = Path.Combine(parent, "r8-wild-*"), ques = Path.Combine(parent, "r8-wild-?");
                string gotStar = ScratchFolder.LongPath(star), gotQues = ScratchFolder.LongPath(ques);
                check(P + "SF-10 [red] a '*' or '?' in a folder name is not a search pattern: the path is handed back, not resolved to another folder"
                      + " ('" + Path.GetFileName(gotStar) + "', '" + Path.GetFileName(gotQues) + "')",
                    gotStar == star && gotQues == ques);

                string bracket = Path.Combine(parent, "r8 [x] folder");
                string gotBracket = ScratchFolder.LongPath(bracket);
                string detail;
                bool inside = ScratchFolder.Check(Path.Combine(bracket, "liveclash-undoprobe-x"), "liveclash-undoprobe-", new[] { parent }, null, out detail);
                check(P + "SF-11 [green] a real folder with a '[' in its name resolves to itself, and a probe folder inside it is accepted (" + detail + ")",
                    string.Equals(gotBracket, bracket, StringComparison.OrdinalIgnoreCase) && inside);
            }
            finally { try { Directory.Delete(parent, true); } catch { } }
        }

        // ================================================================== the dev launchers' manifest guard (headless only)

        private static string FindUp(string folder, string file)
        {
            try
            {
                var d = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    var p = Path.Combine(d.FullName, folder, file);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The manifest guard script (-Prove): a launcher that builds into its own worktree while Revit's LiveClash manifest loads another
        /// checkout's DLL runs the wrong code and reports a misleading "never started". The guard compares the path the manifest loads with
        /// the build the launcher means to run; the comparison is the pure function the script's own self-test drives, one "CASE n PASS" line
        /// per case. Runs only in the headless gate (it starts a PowerShell), never inside Revit.
        /// </summary>
        private static void ManifestGuard(Action<string, bool?> check)
        {
            var descriptions = new[]
            {
                "a manifest that loads ANOTHER checkout's DLL is refused, and the message names both paths",
                "the same DLL spelled differently (case, forward slashes, a '..' step) is accepted",
                "a manifest with a RELATIVE assembly path is refused (it cannot be shown to be this build)",
                "a manifest with no <Assembly> element is refused",
                "no manifest file is refused"
            };
            string script = FindUp("dev", "manifest-guard.ps1");
            if (script == null) { check(P + "GD-1..5 (dev\\manifest-guard.ps1 was not found above the test folder: not run here)", (bool?)null); return; }

            string output = "";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Prove")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } }
                    else output = stdout.Result;
                }
            }
            catch (Exception ex)
            {
                check(P + "GD-1..5 (the guard's self-test could not be started: " + ex.GetType().Name + ")", (bool?)null);
                return;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int n = 1; n <= descriptions.Length; n++)
            {
                string prefix = "CASE " + n + " ";
                string line = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) ?? "(no line)";
                check(P + "GD-" + n + " [red] " + descriptions[n - 1] + " (" + (line.Length > 100 ? line.Substring(0, 100) : line) + ")",
                    line.StartsWith(prefix + "PASS", StringComparison.Ordinal));
            }
        }
#endif

        // ================================================================== DL-f: the key a Save As carries from

        private static void FromKeys(Action<string, bool?> check)
        {
            const string unc = @"\\srv\share\models\m.rvt";
            const string mapped = @"Z:\models\m.rvt";

            var from = SaveAsCarry.FromKey(unc, mapped);
            check(P + "FK-1 [red] the key captured before the save wins over OriginalPath, however OriginalPath is spelled", from == unc);

            check(P + "FK-2 [red] ...so a store loaded under the captured key is the saved document's and is carried (verdict " + LedgerProvenance.Decide(unc, from) + ")",
                LedgerProvenance.Decide(unc, from) == LedgerProvenance.Verdict.Carry);

            check(P + "FK-3 [green] control: judged by OriginalPath alone, the same Save As is refused (that is DL-f as it stands)",
                LedgerProvenance.Decide(unc, mapped) == LedgerProvenance.Verdict.Refuse);

            check(P + "FK-4 [red] a never-saved model has no original path: the captured key is used", SaveAsCarry.FromKey("unsaved::Project1", "") == "unsaved::Project1");

            check(P + "FK-5 [green] nothing captured: OriginalPath is used; neither: empty, which the provenance rule refuses",
                SaveAsCarry.FromKey("", mapped) == mapped && SaveAsCarry.FromKey("   ", mapped) == mapped
                && SaveAsCarry.FromKey(null, null) == "" && SaveAsCarry.FromKey("", "") == ""
                && LedgerProvenance.Decide("x", SaveAsCarry.FromKey("", "")) == LedgerProvenance.Verdict.Refuse);
        }
    }
}
