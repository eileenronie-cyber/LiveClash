using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8, builder "live", fifth part (P9-3 and ENC-1 found in Revit at 184890c; predictions: tests\evidence\liveclash\r8-live\PREDICTIONS-5.md,
    /// b65d98e): the close tracker and the source-tree check. Revit-free.
    /// </summary>
    public static partial class R8LiveChecks
    {
        private static void CloseTrackerRules(Action<string, bool?> check)
        {
            var t1 = new CloseTracker(10);
            t1.Closing(1, "A");
            t1.Closing(2, "B");
            string r1 = t1.Closed(1, false);
            check(P + "CT-1 [red] with two documents noted, Closed(first, cancelled) finalises nothing, removes only that note, and the other is still tracked (returned " + (r1 ?? "null") + ", count " + t1.Count + ")",
                r1 == null && t1.Count == 1 && t1.Closed(2, true) == "B");

            var t2 = new CloseTracker(10);
            t2.Closing(1, "A");
            string first = t2.Closed(1, true);
            string again = t2.Closed(1, true);
            check(P + "CT-2 [red] Closed(id, succeeded) returns the noted key exactly once (first " + (first ?? "null") + ", again " + (again ?? "null") + ", count " + t2.Count + ")",
                first == "A" && again == null && t2.Count == 0);

            var t3 = new CloseTracker(10);
            check(P + "CT-3 [green] an id that was never noted finalises nothing, whatever the status", t3.Closed(99, true) == null && t3.Closed(99, false) == null);

            var t4 = new CloseTracker(10);
            t4.Closing(1, "A");
            t4.Closing(2, "B");
            string cancelledA = t4.Closed(1, false);
            string closedB = t4.Closed(2, true);
            check(P + "CT-4 [red] two documents are independent: A's close was cancelled, B's happened (A " + (cancelledA ?? "null") + ", B " + (closedB ?? "null") + ", count " + t4.Count + ")",
                cancelledA == null && closedB == "B" && t4.Count == 0);

            var t5 = new CloseTracker(10);
            t5.Closing(1, "A");
            t5.Closing(1, "B");
            check(P + "CT-5 [red] the same document id noted twice is one entry, the later key winning (count " + t5.Count + ")", t5.Count == 1 && t5.Closed(1, true) == "B");

            // A file closed without saving and opened again is a DIFFERENT document: a new id for the same key.
            var t6 = new CloseTracker(10);
            t6.Closing(1, "K");
            string old = t6.Closed(1, true);
            t6.Closing(2, "K");
            string reopenedCancelled = t6.Closed(2, false);
            check(P + "CT-6 [red] a reopened document is a different entry: the first close finalises K, a cancelled close of the reopened one finalises nothing and K is not finalised again (" + (old ?? "null") + ", "
                  + (reopenedCancelled ?? "null") + ", count " + t6.Count + ", old id again " + (t6.Closed(1, true) ?? "null") + ")",
                old == "K" && reopenedCancelled == null && t6.Count == 0 && t6.Closed(1, true) == null);

            var t7 = new CloseTracker(3);
            for (int i = 1; i <= 5; i++) t7.Closing(i, "k" + i);
            check(P + "CT-7 [red] the table is capped: after five notes with a cap of three the count is 3, the two oldest are gone and the newest is kept (count " + t7.Count + ")",
                t7.Count == 3 && t7.Closed(1, true) == null && t7.Closed(2, true) == null && t7.Closed(5, true) == "k5");
        }

        private static void NoMojibakeNeedsTheTree(Action<string, bool?> check)
        {
            // ENC-2: a folder with no source tree above it. In Revit the base directory is like this; the verdict must say "no tree", not "failed".
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liveclash-enc-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            bool? verdict;
            string detail;
            try { verdict = NoMojibakeVerdict(dir, out detail); }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            check(P + "ENC-2 [red] from a folder with no source tree the verdict is \"no tree\" (null), not a failure (" + (verdict.HasValue ? verdict.Value.ToString() : "null") + ")", verdict == null);

            // ENC-3: from where the runner runs (inside the checkout) the scan runs and is clean. Where that is not a checkout (Revit), it says so by name.
            bool? real = NoMojibakeVerdict(AppDomain.CurrentDomain.BaseDirectory, out detail);
            if (real == null) { check(P + "ENC-3 [green] from the runner's own folder the scan runs and is clean (" + (Headless ? "the source tree was not found" : "runs headless only: needs the source tree") + ")", EncResult(null, Headless)); return; }
            check(P + "ENC-3 [green] from the runner's own folder the scan runs and is clean (" + detail + ")", real.Value);
        }
    }
}
