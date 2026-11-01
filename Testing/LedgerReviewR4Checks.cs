using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 4 of the 2026-10-31 review (F3), the Revit-free half: the folder snapshot behind the real-state guard (DM2), the sentence for
    /// a refused settings save (DL-e) and the resume advice in a notice (DM4). The predictions were committed before this file existed:
    /// tests\evidence\liveclash\REVIT-RUNS-F3-r4.md (e0a6cff). The in-Revit half is Testing\ReviewChecks.LedgersR4.cs.
    /// </summary>
    public static class LedgerReviewR4Checks
    {
        private const string P = "review F3 r4: ";
        private const int Seed = 20261103;

        private static readonly string[] Plain = { "enabled=True", "set.begin=0", "setname=Default", "set.end=0" };
        private static readonly string[] LogPrefix = { "liveclash.log" };

        public static void RunAll(Action<string, bool?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "liveclash-f3r4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var savedClock = RetryBackoff.Clock;
            try
            {
                using (Log.RedirectForTests(Path.Combine(dir, "test.log")))
                {
                    Snapshots(check, dir);
                    SaveNotices(check);
                    ResumeAdvice(check, dir);
                }
            }
            finally
            {
                RetryBackoff.Clock = savedClock;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ================================================================== DM2: the folder snapshot

        private static void Snapshots(Action<string, bool?> check, string dir)
        {
            var d = Path.Combine(dir, "state");
            Directory.CreateDirectory(Path.Combine(d, "status"));
            File.WriteAllText(Path.Combine(d, "status", "a.resolved.txt"), "one\n");
            File.WriteAllText(Path.Combine(d, "settings.txt"), "enabled=True\n");
            File.WriteAllText(Path.Combine(d, "liveclash.log"), "log\n");
            var s0 = FolderSnapshot.Take(d, LogPrefix);

            check(P + "FS-1 [green] a snapshot compared with itself reports nothing", !FolderSnapshot.Compare(s0, FolderSnapshot.Take(d, LogPrefix)).Any);

            File.WriteAllText(Path.Combine(d, "status", "b.resolved.txt"), "two\n");
            var added = FolderSnapshot.Compare(s0, FolderSnapshot.Take(d, LogPrefix));
            check(P + "FS-2 [green] a file added after the snapshot is reported as added (" + added + ")",
                added.Added.Count == 1 && added.Added[0] == "status/b.resolved.txt" && added.Changed.Count == 0 && added.Removed.Count == 0);

            var s1 = FolderSnapshot.Take(d, LogPrefix);
            File.WriteAllText(Path.Combine(d, "settings.txt"), "enabled=Fals\n");   // same length as "enabled=True", different content
            var changed = FolderSnapshot.Compare(s1, FolderSnapshot.Take(d, LogPrefix));
            check(P + "FS-3 [green] a file changed to other content of the same length is reported as changed (" + changed + ")",
                changed.Changed.Count == 1 && changed.Changed[0] == "settings.txt" && changed.Added.Count == 0 && changed.Removed.Count == 0);

            var s2 = FolderSnapshot.Take(d, LogPrefix);
            File.Delete(Path.Combine(d, "status", "a.resolved.txt"));
            var removed = FolderSnapshot.Compare(s2, FolderSnapshot.Take(d, LogPrefix));
            check(P + "FS-4 [green] a removed file is reported (" + removed + ")",
                removed.Removed.Count == 1 && removed.Removed[0] == "status/a.resolved.txt" && removed.Added.Count == 0 && removed.Changed.Count == 0);

            var s3 = FolderSnapshot.Take(d, LogPrefix);
            File.AppendAllText(Path.Combine(d, "liveclash.log"), "more log\n");
            File.WriteAllText(Path.Combine(d, "liveclash.log.1"), "rolled\n");
            check(P + "FS-5 [green] the log files are left out, however much they change", !FolderSnapshot.Compare(s3, FolderSnapshot.Take(d, LogPrefix)).Any);

            // FS-6: property. Random edits to a folder; the report is exactly that set.
            var r = new Random(Seed);
            string first = null;
            for (int h = 0; h < 200 && first == null; h++)
            {
                var root = Path.Combine(dir, "p" + h);
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                var names = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    var rel = (i % 2 == 0 ? "" : "sub/") + "f" + i + ".txt";
                    File.WriteAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), "content " + i + "\n");
                    names.Add(rel);
                }
                var before = FolderSnapshot.Take(root, LogPrefix);
                var wantAdded = new List<string>(); var wantChanged = new List<string>(); var wantRemoved = new List<string>();
                foreach (var rel in names)
                {
                    var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    switch (r.Next(4))
                    {
                        case 0: File.Delete(p); wantRemoved.Add(rel); break;
                        case 1: File.WriteAllText(p, "CONTENT " + rel.Length + "\n"); wantChanged.Add(rel); break;   // upper case: same length, other bytes
                    }
                }
                for (int i = r.Next(3); i > 0; i--)
                {
                    var rel = "new" + i + ".txt";
                    File.WriteAllText(Path.Combine(root, rel), "n\n");
                    wantAdded.Add(rel);
                }
                var diff = FolderSnapshot.Compare(before, FolderSnapshot.Take(root, LogPrefix));
                Func<List<string>, string> j = l => string.Join(",", l.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                if (j(diff.Added) != j(wantAdded) || j(diff.Changed) != j(wantChanged) || j(diff.Removed) != j(wantRemoved))
                    first = "folder " + h + ": wanted +" + j(wantAdded) + " ~" + j(wantChanged) + " -" + j(wantRemoved) + ", got " + diff;
            }
            check(P + "FS-6 [green] property: 200 random sets of adds, changes and removals are reported exactly (seed " + Seed + ")"
                  + (first == null ? "" : " - FIRST " + first), first == null);

            bool threw = false;
            Dictionary<string, string> none = null;
            try { none = FolderSnapshot.Take(Path.Combine(dir, "no-such-folder"), LogPrefix); FolderSnapshot.Take(null); FolderSnapshot.Compare(null, null); }
            catch { threw = true; }
            check(P + "FS-7 [green] a missing folder gives an empty snapshot and never throws", !threw && none != null && none.Count == 0);
        }

        // ================================================================== DL-e: the sentence

        private static bool Has(string text, string part)
        {
            return System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text ?? "", part, System.Globalization.CompareOptions.IgnoreCase) >= 0;
        }

        private static void SaveNotices(Action<string, bool?> check)
        {
            check(P + "SN-1 [green] a save that worked has nothing to say", SaveNotice.After(true, false) == null && SaveNotice.After(true, true) == null);

            var unreadable = SaveNotice.After(false, false) ?? "";
            // Round 8, D3-4: the old assert required "this session", which is the wrong promise (the edit lasts until the file is read again,
            // then the file's own settings replace it). Stricter now: it must say how long, and that the edit is then lost.
            check(P + "SN-2 [red] refused because the file could not be read: not saved, why, and that it lasts only until the file is read again, then is lost (" + unreadable + ")",
                Has(unreadable, "not saved") && Has(unreadable, "could not be read") && Has(unreadable, "until the file")
                && Has(unreadable, "read again") && Has(unreadable, "lost"));

            var discarded = SaveNotice.After(false, true) ?? "";
            check(P + "SN-3 [red] refused because the settings were read again meanwhile: not saved, read again, discarded (" + discarded + ")",
                Has(discarded, "not saved") && Has(discarded, "read again") && Has(discarded, "discarded"));

            check(P + "SN-4 [red] the two sentences differ", unreadable.Length > 0 && discarded.Length > 0 && unreadable != discarded);
        }

        // ================================================================== DM4: the advice in the resume notice

        private static void ResumeAdvice(Action<string, bool?> check, string dir)
        {
            var t0 = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            var now = t0;
            RetryBackoff.Clock = () => now;
            var path = Path.Combine(dir, "advice.txt");
            File.WriteAllLines(path, Plain);

            Func<RewrittenFile, List<string>> recover = rf =>
            {
                var seen = new List<string>();
                rf.Notice += seen.Add;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) rf.Open(path, null);
                now = now.AddSeconds(6);
                rf.TryReopen();
                return seen;
            };

            var withAdvice = recover(new RewrittenFile("Settings", " Run a full check."));
            check(P + "NT-7 [red] a store built with resume advice puts it in the resume notice (" + string.Join(" | ", withAdvice) + ")",
                withAdvice.Count == 2 && withAdvice[1].Contains("read again") && withAdvice[1].Contains("Run a full check."));

            var without = recover(new RewrittenFile("Audit labels"));
            check(P + "NT-8 [green] one built without advice (the audit labels) has none (" + string.Join(" | ", without) + ")",
                without.Count == 2 && without[1].Contains("read again") && !without[1].Contains("full check"));
        }
    }
}
