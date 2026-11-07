using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// Runs a list of named steps a slice at a time, so work that used to happen in one go before the Filter
    /// window showed (U1: Revit grey for seconds to tens of seconds) happens in the gaps while the window is open.
    ///
    /// Revit-free and clock-injected, so the slicing rules are checked headless (Testing\FilterFreezeLogicChecks.cs):
    ///   * Tick(budgetMs) runs steps in the order they were added. Each step call is given what is LEFT of the tick's budget (at
    ///     least 1 ms) and returns true when that step has finished.
    ///   * A tick always runs at least one step call (a tiny budget still makes progress) and stops once the budget is spent.
    ///   * A step that throws is recorded (the FIRST error only, as "name: message"), counted as finished, and the next step still
    ///     runs: one failed count must not stop the parameter list from loading.
    ///   * Fraction is finished steps / steps, so it only ever goes up, and is exactly 1 when the pump is done.
    ///   * Cancel makes the pump done at once; a tick after that runs nothing.
    /// It does no Revit work itself and starts no thread: the caller ticks it from the thread the steps must run on.
    /// </summary>
    public sealed class WorkPump
    {
        private readonly Func<long> _now;
        private readonly List<string> _names = new List<string>();
        private readonly List<Func<int, bool>> _steps = new List<Func<int, bool>>();
        private readonly List<Func<double>> _progress = new List<Func<double>>();
        private readonly List<Func<string>> _detail = new List<Func<string>>();
        private int _current;
        private bool _cancelled;
        private string _error;
        private int _ticks;

        public WorkPump(Func<long> nowMs)
        {
            _now = nowMs ?? DefaultClockMs;
        }

        /// <summary>Add a step. <paramref name="step"/> receives the milliseconds left in this tick and returns true when it has finished.
        /// <paramref name="progress"/> (optional, 0..1) says how far the step that is running has got, for the fraction.</summary>
        public void Add(string name, Func<int, bool> step, Func<double> progress = null, Func<string> detail = null)
        {
            if (step == null) return;
            _names.Add(name ?? "");
            _steps.Add(step);
            _progress.Add(progress);
            _detail.Add(detail);
        }

        /// <summary>A short sentence about the running step's work so far ("12,300 elements"), or null (no detail, no step, or the detail function threw).</summary>
        public string Detail
        {
            get
            {
                if (IsDone) return null;
                var d = _detail[_current];
                if (d == null) return null;
                try { return d(); } catch { return null; }
            }
        }

        /// <summary>Milliseconds from a monotonic clock that tells a few milliseconds apart (Environment.TickCount moves in steps of about 16 ms).</summary>
        public static long DefaultClockMs() { return System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency; }

        /// <summary>True when every step has finished (or failed), or the pump was cancelled. A pump with no steps is done.</summary>
        public bool IsDone { get { return _cancelled || _current >= _steps.Count; } }

        /// <summary>Ticks that ran at least one step call.</summary>
        public int Ticks { get { return _ticks; } }

        /// <summary>Finished steps / steps, 0..1 (plus the running step's own progress when it reports one); exactly 1 only when the
        /// pump is done, and 1 when there are no steps. Never goes backwards: a step's progress is clamped to 0..1 and a reading lower
        /// than the last one is ignored.</summary>
        public double Fraction
        {
            get
            {
                if (IsDone) return 1.0;
                double within = 0.0;
                var p = _progress[_current];
                if (p != null)
                {
                    try { within = Math.Max(0.0, Math.Min(0.999, p())); } catch { within = 0.0; }
                }
                double f = ((double)_current + within) / _steps.Count;
                if (f > _highest) _highest = f;
                return _highest;
            }
        }
        private double _highest;

        /// <summary>The step now running, or null when the pump is done.</summary>
        public string CurrentName { get { return IsDone ? null : _names[_current]; } }

        /// <summary>The first error a step raised, as "name: message", or null.</summary>
        public string Error { get { return _error; } }

        public void Cancel() { _cancelled = true; }

        /// <summary>Advance by at most about <paramref name="budgetMs"/>. Returns <see cref="IsDone"/>.</summary>
        public bool Tick(int budgetMs)
        {
            if (IsDone) return true;
            if (budgetMs < 1) budgetMs = 1;

            long start = _now();
            bool ran = false;
            while (!IsDone)
            {
                long spent = _now() - start;
                if (ran && spent >= budgetMs) break;

                int left = (int)Math.Max(1, budgetMs - spent);
                bool finished;
                try { finished = _steps[_current](left); }
                catch (Exception ex)
                {
                    if (_error == null) _error = _names[_current] + ": " + ex.Message;
                    finished = true;
                }
                ran = true;
                if (finished) _current++;
            }
            if (ran) _ticks++;
            return IsDone;
        }
    }
}
