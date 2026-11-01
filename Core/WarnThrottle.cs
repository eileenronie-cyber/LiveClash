using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// Lets a line through once per key per window (round 8, delta review I-2). The idle tick's per-step guard logs a Warn with a stack when a
    /// step throws, and a step that keeps throwing would do it every tick (ticks can be raised without delay), pushing everything else out of
    /// the bounded log. The first failure is logged in full; repeats inside the window are counted, and the next line that gets through says how
    /// many were held back. Pure and clock-passed.
    /// </summary>
    public sealed class WarnThrottle
    {
        private readonly TimeSpan _window;
        private readonly Dictionary<string, DateTime> _last = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _held = new Dictionary<string, int>(StringComparer.Ordinal);

        public WarnThrottle(double windowSeconds)
        {
            _window = TimeSpan.FromSeconds(windowSeconds);
        }

        /// <summary>
        /// Should the line for <paramref name="key"/> be written now? <paramref name="heldBack"/> is how many were held back since the last one
        /// that was written.
        /// </summary>
        public bool ShouldLog(string key, DateTime nowUtc, out int heldBack)
        {
            heldBack = 0;
            key = key ?? "";
            DateTime last;
            if (_last.TryGetValue(key, out last) && nowUtc - last < _window)
            {
                int n;
                _held.TryGetValue(key, out n);
                _held[key] = n + 1;
                return false;
            }
            int held;
            if (_held.TryGetValue(key, out held)) { heldBack = held; _held.Remove(key); }
            _last[key] = nowUtc;
            return true;
        }
    }
}
