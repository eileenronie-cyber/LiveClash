using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// "What changed" after a link is reloaded, moved, loaded or unloaded.
    ///
    /// LinkWatch already notices the change and names the link; what was missing was the answer to the question a
    /// coordinator actually has next: which of my clashes are NEW because of it, and which have GONE. This keeps the
    /// clash list as it stood when the first unreconciled change landed (the BASELINE), and when a check finishes
    /// compares it with what is listed then, for the rows of the changed link(s) only.
    ///
    ///   - Several changes before a check (A reloaded, then B moved) share ONE baseline - the state before the first -
    ///     and the comparison covers every link named since.
    ///   - A RECHECK re-tests only the elements that already clash, so it sees what has cleared but can miss a new clash
    ///     somewhere else; it produces a provisional summary and keeps the baseline. A FULL CHECK sweeps everything: it
    ///     produces the final summary and consumes the baseline.
    ///   - The wait ends with a full check. Dismissing the link NOTICE does not end it (that hides a warning; the baseline
    ///     is the one thing that cannot be rebuilt later). A change that discards the results - the filter, the audit
    ///     drops, the clearance - or switching model forgets both the wait and the summary: they describe a list that is
    ///     gone. A later full check with no link change waiting puts an old summary away, so it never outlives the list
    ///     it describes.
    ///
    /// The clash list is reached through <see cref="SnapshotSource"/>, set by the add-in at start-up, so this file has no
    /// Revit calls and is tested headless.
    /// </summary>
    public static class ClashChangeTracker
    {
        /// <summary>The clash list as the add-in holds it now. Set once at start-up; while null, nothing is tracked.</summary>
        public static Func<List<ClashResult>> SnapshotSource;

        /// <summary>The list as it stood when the first unreconciled link change landed, and which links have changed since.</summary>
        public sealed class Baseline
        {
            public List<ClashResult> Before = new List<ClashResult>();
            public List<string> LinkNames = new List<string>();
            public HashSet<string> Models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Every change so far was an unload or a delete: the link's clashes are gone because the link is, not because they were fixed.</summary>
            public bool AllGone = true;
            public DateTime SinceUtc;

            /// <summary>A copy that later changes to this one (another link noted) do not reach.</summary>
            internal Baseline Copy()
            {
                var b = new Baseline { AllGone = AllGone, SinceUtc = SinceUtc };
                b.Before = new List<ClashResult>(Before);
                b.LinkNames = new List<string>(LinkNames);
                b.Models = new HashSet<string>(Models, StringComparer.OrdinalIgnoreCase);
                return b;
            }
        }

        /// <summary>The comparison, once a check has finished.</summary>
        public sealed class Summary
        {
            public ClashChangeSet Set;
            public List<string> LinkNames = new List<string>();
            public bool AfterFullCheck;
            public bool AllGone;
            public DateTime AtUtc;

            public bool IsNew(string key) { return Set != null && key != null && Set.NewKeys.Contains(key); }

            /// <summary>"Structure", "Structure and Architecture", "3 links".</summary>
            public string LinkText { get { return NamesText(LinkNames); } }

            /// <summary>The one line the panel shows.</summary>
            public string Headline
            {
                get
                {
                    var s = Set;
                    if (AllGone)
                    {
                        int gone = s.Cleared.Count;
                        return LinkText + (LinkNames.Count > 1 ? " are" : " is") + " no longer there: "
                             + (gone == 0 ? "it had no clashes listed." : Plural(gone, "clash") + " with " + (LinkNames.Count > 1 ? "them" : "it") + " left the list.");
                    }
                    var parts = new List<string>();
                    if (s.New.Count > 0) parts.Add(s.New.Count + " new");
                    if (s.Cleared.Count > 0) parts.Add(s.Cleared.Count + " cleared");
                    if (s.SeverityChanged.Count > 0) parts.Add(s.SeverityChanged.Count + " changed severity");
                    if (parts.Count == 0)
                    {
                        // Nothing compared is not the same as nothing changed: no row on either side belonged to the named
                        // link(s). Say that, rather than reassure.
                        if (s.BeforeInScope == 0 && s.AfterInScope == 0)
                            return "Since " + LinkText + " changed: no clashes with " + (LinkNames.Count > 1 ? "them" : "it") + " are listed, before or after.";
                        return "Since " + LinkText + " changed: no change to the clashes with " + (LinkNames.Count > 1 ? "them" : "it") + ".";
                    }
                    return "Since " + LinkText + " changed: " + string.Join(", ", parts.ToArray()) + ".";
                }
            }

            /// <summary>What the line cannot say yet.</summary>
            public string Caveat
            {
                get { return AfterFullCheck || AllGone ? "" : "A recheck only re-tests what already clashes - run a full check to catch new clashes elsewhere."; }
            }
        }

        private static Baseline _pending;
        private static Summary _summary;

        /// <summary>Raised whenever the pending state or the summary changed. The panel listens.</summary>
        public static event Action Changed;

        public static Baseline Pending { get { return _pending; } }
        public static Summary Current { get { return _summary; } }

        /// <summary>Is <paramref name="key"/> one of the new clashes in the current summary?</summary>
        public static bool IsNew(string key)
        {
            var s = _summary;
            return s != null && s.IsNew(key);
        }

        /// <summary>
        /// A link changed. The first change takes the baseline; later ones only add the link to it.
        /// Does nothing until the add-in has given it a list to snapshot.
        /// </summary>
        public static void NoteLinkChange(LinkWatch.Kind kind, string linkName)
        {
            var source = SnapshotSource;
            if (source == null) return;

            // Raise only when something changed. LinkWatch records a notice for every modified link instance, and dragging
            // one fires many; the panel rebuilds its whole list on each Raise.
            bool changed = false;
            if (_pending == null)
            {
                List<ClashResult> now;
                try { now = source(); }
                catch { return; }
                // Cloned, not the live rows themselves: ClashResultStore.UpdateSeverity mutates a
                // row's Severity in place on the SAME object this snapshot would otherwise still be
                // holding, so a severity change made by the background verifier while this baseline
                // is pending would silently rewrite "before" to already match "after" — the exact
                // change this baseline exists to detect would then compare as no change at all.
                var before = new List<ClashResult>();
                if (now != null) foreach (var r in now) if (r != null) before.Add(r.Clone());
                _pending = new Baseline { Before = before, SinceUtc = DateTime.UtcNow };
                changed = true;
            }

            var display = ClashChange.Norm(string.IsNullOrEmpty(linkName) ? "A link" : linkName);
            if (!_pending.Models.Contains(display))
            {
                _pending.Models.Add(display);
                _pending.LinkNames.Add(display);
                changed = true;
            }
            if (_pending.AllGone && kind != LinkWatch.Kind.Unloaded && kind != LinkWatch.Kind.InstanceDeleted)
            {
                _pending.AllGone = false;
                changed = true;
            }
            if (changed) Raise();
        }

        /// <summary>
        /// A check has finished and the list holds its result. With a baseline waiting, compare and publish; a full check
        /// consumes the baseline, a recheck keeps it (it cannot see new clashes). Without one, nothing happens.
        /// </summary>
        public static void Complete(IEnumerable<ClashResult> after, bool fullCheck)
        {
            var p = _pending;
            if (p == null)
            {
                // A full check with nothing waiting: whatever summary is still up describes an older list.
                if (fullCheck && _summary != null) { _summary = null; Raise(); }
                return;
            }

            _summary = new Summary
            {
                Set = ClashChange.Compare(p.Before, after, p.Models),
                LinkNames = new List<string>(p.LinkNames),
                AfterFullCheck = fullCheck,
                AllGone = p.AllGone,
                AtUtc = DateTime.UtcNow
            };
            if (fullCheck) _pending = null;
            Raise();
        }

        /// <summary>Put the summary away.</summary>
        public static void DismissSummary()
        {
            if (_summary == null) return;
            _summary = null;
            Raise();
        }

        /// <summary>
        /// Forget everything: a different model was activated, or the results were discarded because a setting changed
        /// (the filter, the audit drops, the clearance) - either way the baseline and the summary describe a list that is gone.
        /// </summary>
        public static void Reset()
        {
            if (_pending == null && _summary == null) return;
            _pending = null;
            _summary = null;
            Raise();
        }

        private static void Raise()
        {
            var h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        // ---------------------------------------------------------------- wording

        internal static string NamesText(List<string> names)
        {
            if (names == null || names.Count == 0) return "the link";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return names[0] + " and " + names[1];
            return names.Count + " links";
        }

        internal static string Plural(int n, string noun)
        {
            return n + " " + (n == 1 ? noun : noun + (noun.EndsWith("sh") || noun.EndsWith("s") ? "es" : "s"));
        }

        // ---------------------------------------------------------------- tests

        internal sealed class State { internal Baseline Pending; internal Summary Summary; internal Func<List<ClashResult>> Source; }

        /// <summary>Everything mutable, so a test inside a LIVE session can put the real state back exactly as it found it.</summary>
        internal static State Snapshot() { return new State { Pending = _pending == null ? null : _pending.Copy(), Summary = _summary, Source = SnapshotSource }; }

        internal static void Restore(State s)
        {
            _pending = s == null || s.Pending == null ? null : s.Pending.Copy();     // copied again: the state handed back may be restored twice
            _summary = s == null ? null : s.Summary;
            SnapshotSource = s == null ? null : s.Source;
        }
    }
}
