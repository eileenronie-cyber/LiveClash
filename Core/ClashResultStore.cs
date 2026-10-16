using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Holds the current live clash set. Everything here is called from Revit's
    /// main thread, but the WPF panel reads it too, so every access is locked
    /// and readers always get a snapshot copy.
    /// </summary>
    public class ClashResultStore
    {
        public static ClashResultStore Instance { get; } = new ClashResultStore();

        private readonly object _gate = new object();
        private readonly Dictionary<string, ClashResult> _results = new Dictionary<string, ClashResult>();

        /// <summary>
        /// element id -> the keys of every clash that involves it. Maintained on add and
        /// remove so <see cref="RemoveForElement"/> is a lookup rather than a scan.
        ///
        /// It used to LINQ-scan the whole dictionary, and ClashUpdater.Execute calls it
        /// once per deleted id and once per changed id — up to 40 times per transaction,
        /// inside the 120 ms budget. With a few thousand rows listed that is tens of
        /// thousands of dictionary walks plus the allocation of a Where/Select/ToList
        /// each time, all to find the handful of rows that actually mention the element.
        ///
        /// Same shape, for the same reason, as TargetIndex._slotOf.
        /// </summary>
        private readonly Dictionary<long, HashSet<string>> _byElement = new Dictionary<long, HashSet<string>>();

        /// <summary>
        /// Clashes that have just stopped existing, kept briefly so the panel can show
        /// the fix landing instead of the row silently vanishing.
        ///
        /// Note the updater removes a changed element's hits BEFORE re-checking it, so
        /// a clash that is still there passes through here every time it is touched.
        /// <see cref="AddRange"/> therefore un-marks anything that comes straight back,
        /// otherwise every edit would flash a false "resolved".
        /// </summary>
        private readonly Dictionary<string, KeyValuePair<ClashResult, DateTime>> _resolved =
            new Dictionary<string, KeyValuePair<ClashResult, DateTime>>();

        /// <summary>Raised (on the main thread) whenever the set changes.</summary>
        public event EventHandler Changed;

        // ------------------------------------------------------------ element index
        //
        // Both helpers are called under _gate.

        /// <summary>
        /// Which element ids a row is filed under: the host side always, and the other
        /// side ONLY when it lives in the host document. Linked documents have their own
        /// id space, so a linked element can share a raw id value with an unrelated host
        /// element — filing link ids here would make RemoveForElement delete rows that
        /// merely collide numerically.
        /// </summary>
        private static IEnumerable<long> IndexIdsOf(ClashResult r)
        {
            if (r == null) yield break;
            yield return IdUtil.Value(r.HostElementId);
            if (r.LinkInstanceId == null) yield return IdUtil.Value(r.OtherElementId);
        }

        private void IndexAdd(ClashResult r)
        {
            if (r == null || string.IsNullOrEmpty(r.Key)) return;
            foreach (var id in IndexIdsOf(r))
            {
                HashSet<string> keys;
                if (!_byElement.TryGetValue(id, out keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    _byElement[id] = keys;
                }
                keys.Add(r.Key);
            }
        }

        private void IndexRemove(string key, ClashResult r)
        {
            if (r == null || string.IsNullOrEmpty(key)) return;
            foreach (var id in IndexIdsOf(r))
            {
                HashSet<string> keys;
                if (!_byElement.TryGetValue(id, out keys)) continue;
                keys.Remove(key);
                // Drop the bucket rather than leaving empty sets behind — the store
                // outlives many edits and this is the only thing that trims it.
                if (keys.Count == 0) _byElement.Remove(id);
            }
        }

        public int Count
        {
            get { lock (_gate) return _results.Count; }
        }

        public void AddRange(IEnumerable<ClashResult> results)
        {
            if (results == null) return;
            bool dirty = false;
            lock (_gate)
            {
                foreach (var r in results)
                {
                    if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                    if (!_results.ContainsKey(r.Key)) dirty = true;

                    // Drop the old row's index entries before the overwrite. A shared key
                    // means the same pair, so in practice the ids match — but the index
                    // must not depend on that holding.
                    ClashResult old;
                    if (_results.TryGetValue(r.Key, out old)) IndexRemove(r.Key, old);

                    _results[r.Key] = r;
                    IndexAdd(r);

                    // Came straight back — it was never resolved.
                    _resolved.Remove(r.Key);
                }
            }
            if (dirty) RaiseChanged();
        }

        /// <summary>
        /// Drop every clash that involves this element. Called before re-checking a
        /// modified element and when an element is deleted, so stale hits never linger.
        /// </summary>
        /// <returns>
        /// The rows that were dropped — needed by the DELETION path, which has to
        /// record them as resolved rather than merely forgetting them. Callers that
        /// only want the removal ignore it, which is why this stayed void for so long
        /// and why deleting an element lost the fix instead of recording it.
        /// </returns>
        public List<ClashResult> RemoveForElement(ElementId id)
        {
            var removed = new List<ClashResult>();
            if (id == null) return removed;
            long v = IdUtil.Value(id);
            bool dirty = false;
            lock (_gate)
            {
                // Straight off the index. What goes INTO it — host side always, other side
                // only when it lives in the host document — is decided in IndexAdd, and
                // that rule is the one this method used to spell out inline.
                HashSet<string> hit;
                if (!_byElement.TryGetValue(v, out hit) || hit.Count == 0) return removed;
                var doomed = new List<string>(hit);

                var now = DateTime.UtcNow;
                foreach (var k in doomed)
                {
                    ClashResult gone;
                    if (_results.TryGetValue(k, out gone))
                    {
                        _resolved[k] = new KeyValuePair<ClashResult, DateTime>(gone, now);
                        IndexRemove(k, gone);
                        removed.Add(gone);
                    }
                    _results.Remove(k);
                }
                dirty = doomed.Count > 0;
            }
            if (dirty) RaiseChanged();
            return removed;
        }

        /// <summary>
        /// Remove specific pairs that have been verified as gone. They go through the
        /// same recently-resolved path as an edit-driven removal, so a clash the
        /// background verifier notices still shows its green struck-through row —
        /// the user sees the fix land whether they triggered the check or not.
        /// </summary>
        public void RemoveKeys(ICollection<string> keys)
        {
            if (keys == null || keys.Count == 0) return;
            bool dirty = false;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                foreach (var k in keys)
                {
                    ClashResult gone;
                    if (!_results.TryGetValue(k, out gone)) continue;
                    _resolved[k] = new KeyValuePair<ClashResult, DateTime>(gone, now);
                    _results.Remove(k);
                    IndexRemove(k, gone);
                    dirty = true;
                }
            }
            if (dirty) RaiseChanged();
        }

        /// <summary>
        /// A pair can stay a clash but change severity — a hard hit backed off into
        /// the clearance envelope. Updating in place keeps the row and its status
        /// rather than churning it through resolved-and-rediscovered.
        /// </summary>
        public void UpdateSeverity(string key, ClashSeverity severity)
        {
            if (string.IsNullOrEmpty(key)) return;
            bool dirty = false;
            lock (_gate)
            {
                ClashResult r;
                if (_results.TryGetValue(key, out r) && r.Severity != severity)
                {
                    r.Severity = severity;
                    dirty = true;
                }
            }
            if (dirty) RaiseChanged();
        }

        /// <summary>
        /// Swap the whole list for <paramref name="results"/> in ONE step: under one lock, with one Changed.
        ///
        /// A full check used to Clear() the store and only then run the sweep, so a sweep that failed left
        /// the panel empty and the previous results gone. It now sweeps first and swaps only once it has
        /// something to swap in - the same content as Clear() followed by AddRange, without the window
        /// between them in which the list is empty (and without the two change notifications).
        /// </summary>
        public void Replace(IEnumerable<ClashResult> results)
        {
            bool dirty;
            lock (_gate)
            {
                dirty = _results.Count > 0 || _resolved.Count > 0;
                _results.Clear();
                _resolved.Clear();
                _byElement.Clear();

                if (results != null)
                    foreach (var r in results)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                        ClashResult old;
                        if (_results.TryGetValue(r.Key, out old)) IndexRemove(r.Key, old);
                        _results[r.Key] = r;
                        IndexAdd(r);
                        dirty = true;
                    }
            }
            if (dirty) RaiseChanged();
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_results.Count == 0 && _resolved.Count == 0) return;
                _results.Clear();
                _resolved.Clear();
                _byElement.Clear();
            }
            RaiseChanged();
        }

        /// <summary>
        /// Clashes that disappeared within the given window, so the panel can show
        /// them struck through before dropping them. Prunes anything older.
        /// </summary>
        public List<ClashResult> RecentlyResolved(TimeSpan window)
        {
            var list = new List<ClashResult>();
            var cutoff = DateTime.UtcNow - window;
            lock (_gate)
            {
                if (_resolved.Count == 0) return list;

                var stale = new List<string>();
                foreach (var kv in _resolved)
                {
                    if (kv.Value.Value < cutoff) stale.Add(kv.Key);
                    else list.Add(kv.Value.Key);
                }
                foreach (var k in stale) _resolved.Remove(k);
            }
            return list;
        }

        /// <summary>True while anything is still inside the resolved window.</summary>
        public bool HasRecentlyResolved
        {
            get { lock (_gate) return _resolved.Count > 0; }
        }

        public List<ClashResult> Snapshot()
        {
            lock (_gate)
            {
                return _results.Values
                    .OrderByDescending(r => r.Severity == ClashSeverity.Hard)
                    .ThenByDescending(r => r.DetectedUtc)
                    .ToList();
            }
        }

        private void RaiseChanged()
        {
            var h = Changed;
            if (h != null)
            {
                try { h(this, EventArgs.Empty); } catch { /* never let the UI break the updater */ }
            }
        }
    }
}
