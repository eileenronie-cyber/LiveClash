using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8, builder "live", second part (after the delta review, predictions: tests\evidence\liveclash\r8-live\PREDICTIONS-2.md, 059210c):
    /// the backlog model (L-3), the warning throttle (I-2) and the encoding scan (I-1). Revit-free.
    /// </summary>
    public static partial class R8LiveChecks
    {
        // ================================================================== L-3: the time cap and the rollback backlog

        private static void BacklogModel(Action<string, bool?> check)
        {
            // 480 queued fixes still to be asked after a rollback pass, 50 ms each.
            long longestNew, longestOld;
            int slicesNew = ReconcilePlan.SlicesToDrain(480, 50, ReconcilePlan.TickMaxItems, ReconcilePlan.TickBudgetMs, out longestNew);
            int slicesOld = ReconcilePlan.SlicesToDrain(480, 50, ReconcilePlan.TickMaxItems, long.MaxValue, out longestOld);   // e494c85: 25 per tick, no time cap
            double oldSeconds = ReconcilePlan.DrainSeconds(slicesOld, longestOld, ReconcilePlan.VerifyIntervalSeconds);
            double asBuilt = ReconcilePlan.DrainSeconds(slicesNew, longestNew, ReconcilePlan.VerifyIntervalSeconds);

            check(P + "L3-1 [model] the model: 480 queued fixes at 50 ms take " + slicesNew + " slices of at most " + longestNew + " ms under the time box (" + asBuilt.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s at one slice per "
                  + ReconcilePlan.VerifyIntervalSeconds + " s tick) against " + slicesOld + " slices of " + longestOld + " ms before it (" + oldSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s)",
                slicesNew == 160 && longestNew == 150 && slicesOld == 20 && longestOld == 1250 && Math.Abs(oldSeconds - 125) < 0.01 && Math.Abs(asBuilt - 824) < 0.01);

            const double backlogGap = 0;   // the idle step runs one slice per tick and asks Idling back at once; RB-1 (Revit) is the proof that the product does
            double withBacklog = ReconcilePlan.DrainSeconds(slicesNew, longestNew, backlogGap);
            check(P + "L3-2 [model] ...a backlog drained between ticks (gap " + backlogGap + " s; the product behaviour is RB-1 in Revit) takes no longer than before the time box (" + withBacklog.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s against "
                  + oldSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s)",
                withBacklog <= oldSeconds);

            check(P + "L3-3 [model] ...and the time box stays: no slice runs past its budget by more than one entry (longest " + longestNew + " ms, budget " + ReconcilePlan.TickBudgetMs + " ms, entry 50 ms)",
                longestNew <= ReconcilePlan.TickBudgetMs + 50);
        }

        // ================================================================== I-2: the warning throttle

        private static void WarnThrottled(Action<string, bool?> check)
        {
            var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            var th = new WarnThrottle(60);
            int h1, h2, h3, h4, h5;
            bool first = th.ShouldLog("VerifySlice", t0, out h1);
            bool second = th.ShouldLog("VerifySlice", t0.AddSeconds(10), out h2);
            bool third = th.ShouldLog("VerifySlice", t0.AddSeconds(20), out h3);
            bool later = th.ShouldLog("VerifySlice", t0.AddSeconds(61), out h4);
            bool other = th.ShouldLog("Drain", t0.AddSeconds(62), out h5);

            check(P + "WT-1 [red] a repeat of the same step inside the window is held back (second " + second + ", third " + third + ")", !second && !third);
            check(P + "WT-2 [green] the first failure of a step is logged, nothing held back before it (" + first + ", " + h1 + ")", first && h1 == 0);
            check(P + "WT-3 [red] after the window it is logged again and says how many were held back (logged " + later + ", held back " + h4 + ")", later && h4 == 2);
            check(P + "WT-4 [green] another step is independent (" + other + ", " + h5 + ")", other && h5 == 0);
        }

        // ================================================================== I-1: mojibake in a source file

        /// <summary>
        /// ENC-1. A UTF-8 character read as Windows-1252 and saved again leaves a trail (an em dash becomes the three characters a-circumflex, euro
        /// sign, right double quote). The move of the idle tick did that to a comment (delta review I-1). Scans EVERY .cs file under the source tree
        /// (not bin, obj or .git) for that trail and for the replacement character, and prints file:line. In Revit the base directory is the Revit folder,
        /// not a checkout: there it prints one named line and does not fail (the check runs headless, where the tree is always found).
        /// </summary>
        private static void NoMojibake(Action<string, bool?> check)
        {
            // Built from escapes so this file does not contain what it looks for.
            string detail;
            bool? verdict = NoMojibakeVerdict(AppDomain.CurrentDomain.BaseDirectory, out detail);
            if (verdict == null) { check(P + "ENC-1 [red] no .cs file holds mojibake (" + (Headless ? "the source tree was not found" : "runs headless only: needs the source tree") + ")", EncResult(null, Headless)); return; }
            check(P + "ENC-1 [red] no .cs file holds mojibake (" + detail + ")", verdict.Value);
        }

        /// <summary>
        /// The mojibake scan as a function of where it starts: true (the tree was found, more than 100 files were scanned, none holds the trail),
        /// false (it does), null (no source tree above <paramref name="startDir"/>: in Revit the base directory is the Revit folder). The check then prints a named line instead of failing.
        /// </summary>
        private static bool? NoMojibakeVerdict(string startDir, out string detail)
        {
            string[] marks = { new string(new[] { (char)0xE2, (char)0x20AC }), new string(new[] { (char)0xC3, (char)0xA2 }), new string(new[] { (char)0xFFFD }) };
            string root = RepoRootFrom(startDir);
            if (root == null) { detail = "the source tree was not found from " + startDir; return null; }

            var hits = new List<string>();
            int files = 0;
            ScanDir(new System.IO.DirectoryInfo(root), root, marks, hits, ref files);
            detail = files + " files scanned" + (hits.Count == 0 ? "" : "; found " + hits.Count + ": " + string.Join("; ", hits.GetRange(0, Math.Min(10, hits.Count))));
            return files > 100 && hits.Count == 0;
        }

        private static void ScanDir(System.IO.DirectoryInfo dir, string root, string[] marks, List<string> hits, ref int files)
        {
            System.IO.FileInfo[] cs;
            System.IO.DirectoryInfo[] subs;
            try { cs = dir.GetFiles("*.cs"); subs = dir.GetDirectories(); }
            catch { return; }

            foreach (var f in cs)
            {
                files++;
                string[] lines;
                try { lines = System.IO.File.ReadAllLines(f.FullName); }
                catch { continue; }
                for (int i = 0; i < lines.Length; i++)
                    foreach (var m in marks)
                        if (lines[i].Contains(m)) { hits.Add(f.FullName.Substring(root.Length).TrimStart((char)92) + ":" + (i + 1)); break; }
            }
            foreach (var d in subs)
            {
                string n = d.Name;
                if (n == "bin" || n == "obj" || n == ".git" || n == ".vs" || n == "node_modules") continue;
                ScanDir(d, root, marks, hits, ref files);
            }
        }
    }
}
