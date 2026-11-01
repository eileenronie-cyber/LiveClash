using System;

namespace LiveClash.Core
{
    /// <summary>
    /// How often a read that failed is tried again (review 2026-10-31 round 2, DL9 and DN2).
    ///
    /// The stores re-read a file that was locked when the model loaded, on the 5-second verify tick, which
    /// runs on Revit's UI thread. A ledger that stays locked used to cost its full retry (about 300 ms) on
    /// every tick. So a failure closes the gate for a while: 5 s, then 10, 20, 40, and never more than 60 s;
    /// a success opens it and starts the steps over. Revit-free, so the headless gate checks it.
    /// </summary>
    public sealed class RetryBackoff
    {
        /// <summary>The clock. A field so a check can move time without sleeping.</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        private const int FirstStepSeconds = 5;
        private const int LongestStepSeconds = 60;

        private int _failures;
        private DateTime _openAt = DateTime.MinValue;

        /// <summary>May the read be tried again now?</summary>
        public bool Due()
        {
            return Clock() >= _openAt;
        }

        /// <summary>The read failed: close the gate for the next step.</summary>
        public void Failed()
        {
            if (_failures < 30) _failures++;   // the step is capped long before this; the bound only stops the shift overflowing
            double step = Math.Min((double)FirstStepSeconds * (1L << (_failures - 1)), LongestStepSeconds);
            _openAt = Clock().AddSeconds(step);
        }

        /// <summary>The read worked: open the gate and forget the failures.</summary>
        public void Succeeded()
        {
            _failures = 0;
            _openAt = DateTime.MinValue;
        }

        /// <summary>A copy in the same state, for a check that must put a live store back.</summary>
        internal RetryBackoff Copy()
        {
            return new RetryBackoff { _failures = _failures, _openAt = _openAt };
        }
    }
}
