using System;
using System.Diagnostics;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The progress window for the clash-schedule PLAN step on a big model (clash schedule at scale, 2026-11-02: 12,497 clashes froze Revit for
    /// ~13 minutes with nothing on screen). The same never-throws window as a full check, titled for what this is. No Cancel: the plan cannot be
    /// interrupted, and a button that does nothing is worse than none (a spec question).
    /// </summary>
    internal sealed class PlanProgress
    {
        /// <summary>More clashes than this and the plan shows the window (about 5 s of work at the rate measured after the fix).</summary>
        internal const int Threshold = 1500;

        private readonly FullCheckProgress _window;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private PlanProgress(FullCheckProgress window) { _window = window; }

        internal static bool ShouldShow(int clashes) { return clashes > Threshold; }

        /// <summary>The window for a plan over <paramref name="clashes"/> clashes, or null when that is a small one.</summary>
        internal static PlanProgress StartFor(int clashes)
        {
            if (!ShouldShow(clashes)) return null;
            try
            {
                var w = FullCheckProgress.Start(FullCheckProgress.RevitWindow());
                if (!w.IsAlive)
                {
                    Log.Info("The plan's progress window did not start; planning goes on without it.");
                    w.Close();   // a window that was only slow must not appear after the plan is over
                    return null;
                }
                w.SetKind("Live Clash \u2014 clash schedule", "Clash schedule",
                    "Reading " + clashes.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " clashes and planning the markers\u2026", "clashes");
                return new PlanProgress(w);
            }
            catch (Exception ex) { Log.Warn("The plan's progress window could not be started", ex); return null; }
        }

        internal void Report(int done, int total)
        {
            long elapsed = _clock.ElapsedMilliseconds;
            long remaining = done > 0 && total > done ? elapsed * (total - done) / done : -1;
            _window.Report(done, total, elapsed, remaining);
        }

        internal void Close() { _window.Close(); }
    }
}