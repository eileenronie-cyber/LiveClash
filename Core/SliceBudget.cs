using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    /// <summary>What a time-boxed slice did.</summary>
    public sealed class SliceResult
    {
        public int Done { get; set; }
        public bool StoppedByTime { get; set; }
        public long Ms { get; set; }
    }

    /// <summary>
    /// A slice of work capped by ELAPSED TIME as well as by count (round 8, SP8 / E7): the idle reconcile asks up to 25 queued fixes a tick and
    /// up to 500 on a rollback, each a solid test on the UI thread. Oldest first, always at least one (so a tiny budget still makes progress),
    /// and it stops when the budget is spent. What it did not reach stays first in line for the next slice. Pure and clock-injected.
    /// </summary>
    public static class SliceBudget
    {
        /// <summary>
        /// Process <paramref name="items"/> in order. The first is always processed; each later one only while the time spent so far (by
        /// <paramref name="nowMs"/>) is under <paramref name="budgetMs"/>. What is not reached is left for the next slice.
        /// </summary>
        public static SliceResult Run<T>(IList<T> items, long budgetMs, Func<long> nowMs, Action<T> processOne)
        {
            long t0 = nowMs();
            int done = 0;
            bool stopped = false;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0 && nowMs() - t0 >= budgetMs) { stopped = true; break; }
                    processOne(items[i]);
                    done++;
                }
            }
            return new SliceResult { Done = done, StoppedByTime = stopped, Ms = nowMs() - t0 };
        }

        /// <summary>
        /// The one log line for a slice: <c>Reconcile tick: asked 25 of 80, done 12, 150 ms, stopped by time yes</c>. <paramref name="kind"/> is
        /// "tick" or "rollback"; <paramref name="queued"/> is how many fixes the document has waiting, <paramref name="asked"/> how many this slice took.
        /// </summary>
        public static string TimingLine(string kind, int asked, int queued, int done, long ms, bool stoppedByTime)
        {
            return "Reconcile " + kind + ": asked " + asked.ToString(CultureInfo.InvariantCulture) + " of " + queued.ToString(CultureInfo.InvariantCulture)
                   + ", done " + done.ToString(CultureInfo.InvariantCulture) + ", " + ms.ToString(CultureInfo.InvariantCulture)
                   + " ms, stopped by time " + (stoppedByTime ? "yes" : "no");
        }

        /// <summary>Does a tick's slice get a log line? When it asked anything and (took 25 ms or more, stopped by time, or no line for 60 s).</summary>
        public static bool ShouldLogTick(int asked, long ms, bool stoppedByTime, double secondsSinceLastLine)
        {
            if (asked <= 0) return false;
            return ms >= 25 || stoppedByTime || secondsSinceLastLine >= 60;
        }
    }

    /// <summary>
    /// The reconcile slices' limits in one place, and the arithmetic of draining a backlog (round 8, delta review L-3). A rollback leaves up to
    /// 500 queued fixes to be asked again; a tick asks as many as fit in its time box. The figures here are a MODEL (a fake clock and a fixed
    /// cost per entry); the real ones are in the "Reconcile tick|rollback" log lines.
    /// </summary>
    public static class ReconcilePlan
    {
        public const int TickMaxItems = 25;
        public const int TickBudgetMs = 150;
        public const int RollbackMaxItems = 500;
        public const int RollbackBudgetMs = 1000;

        /// <summary>The idle verify pass runs at most this often (ClashSettings.VerifyIntervalSeconds default).</summary>
        public const double VerifyIntervalSeconds = 5;


        /// <summary>How many slices of at most <paramref name="maxItems"/> entries and <paramref name="budgetMs"/> it takes to ask <paramref name="backlog"/> entries that cost <paramref name="perItemMs"/> each; also the longest slice.</summary>
        public static int SlicesToDrain(int backlog, long perItemMs, int maxItems, long budgetMs, out long longestSliceMs)
        {
            longestSliceMs = 0;
            int slices = 0;
            int remaining = backlog;
            while (remaining > 0 && slices < 100000)
            {
                int take = Math.Min(remaining, maxItems);
                long t = 0;
                var items = new int[take];
                var res = SliceBudget.Run(items, budgetMs, () => t, x => { t += perItemMs; });
                remaining -= res.Done;
                slices++;
                if (res.Ms > longestSliceMs) longestSliceMs = res.Ms;
            }
            return slices;
        }

        /// <summary>Wall seconds to run <paramref name="slices"/> slices of <paramref name="sliceMs"/> with <paramref name="gapSeconds"/> between them.</summary>
        public static double DrainSeconds(int slices, long sliceMs, double gapSeconds)
        {
            return slices * (sliceMs / 1000.0 + gapSeconds);
        }
    }
}