using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>What is known about an element id in a document.</summary>
    public enum DeletionKnowledge
    {
        /// <summary>Not remembered as deleted by a committed transaction.</summary>
        NotDeleted,
        /// <summary>A committed transaction deleted it.</summary>
        Deleted,
        /// <summary>Too many deletions were seen to remember them all: nothing can be said about any id.</summary>
        Unknown
    }

    /// <summary>
    /// The ids that COMMITTED transactions deleted, per document (round 8, M-2). A rollback lists no ids, so after one the listed rows whose element
    /// is gone are told apart by this memory: an id in it was deleted by a user (a fix, left to the normal route), an id not in it vanished because
    /// its creation was rolled back (dropped, not a fix). Element ids are never reused, so a set only grows. Past the cap the document is marked
    /// OVERFLOWED and its memory freed: <see cref="Knowledge"/> then answers Unknown for every id, and the caller drops nothing for that document
    /// until it is forgotten (closed). A permanent record must never silently lose a real fix; recording a rolled-back draw is the older, known
    /// behaviour (decision of TESTINGS under delegation, 2026-11-01). Pure, so the rules are asserted headlessly.
    /// </summary>
    public sealed class CommittedDeletions
    {
        public const int DefaultCap = 200000;

        /// <summary>The instance the updater uses. Replaceable so a check can run it with a small cap.</summary>
        internal static CommittedDeletions Shared = new CommittedDeletions(DefaultCap);

        private sealed class Entry
        {
            public readonly HashSet<long> Ids = new HashSet<long>();
            public bool Overflowed;
        }

        private readonly int _cap;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _byDoc = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public CommittedDeletions(int cap)
        {
            _cap = cap;
        }

        /// <summary>
        /// Remember that a committed transaction deleted <paramref name="ids"/> in the document <paramref name="key"/>. Past the cap the document is
        /// marked overflowed and its ids are freed. Returns true on the call that makes the document overflow (so the caller says so once).
        /// </summary>
        public bool Note(string key, IEnumerable<long> ids)
        {
            if (key == null || ids == null) return false;
            lock (_gate)
            {
                Entry e;
                if (!_byDoc.TryGetValue(key, out e)) _byDoc[key] = e = new Entry();
                if (e.Overflowed) return false;
                foreach (var id in ids)
                {
                    e.Ids.Add(id);
                    if (e.Ids.Count > _cap) { e.Overflowed = true; e.Ids.Clear(); return true; }
                }
                return false;
            }
        }

        /// <summary>Has the document <paramref name="key"/> overflowed the cap?</summary>
        public bool Overflowed(string key)
        {
            if (key == null) return false;
            lock (_gate)
            {
                Entry e;
                return _byDoc.TryGetValue(key, out e) && e.Overflowed;
            }
        }

        /// <summary>What is known about <paramref name="id"/> in <paramref name="key"/>: Deleted, NotDeleted, or Unknown once the document overflowed.</summary>
        public DeletionKnowledge Knowledge(string key, long id)
        {
            if (key == null) return DeletionKnowledge.NotDeleted;
            lock (_gate)
            {
                Entry e;
                if (!_byDoc.TryGetValue(key, out e)) return DeletionKnowledge.NotDeleted;
                if (e.Overflowed) return DeletionKnowledge.Unknown;
                return e.Ids.Contains(id) ? DeletionKnowledge.Deleted : DeletionKnowledge.NotDeleted;
            }
        }

        /// <summary>Forget a document (it closed), including an overflow.</summary>
        public void Forget(string key)
        {
            if (key == null) return;
            lock (_gate) _byDoc.Remove(key);
        }

        /// <summary>
        /// The document's key changed (Save As, a followed path): its memory moves to the new key. If the new key already has some, the two are
        /// MERGED, and an overflow on either side stays (a merge that would pass the cap overflows too). Nothing moves for a key with no memory, or
        /// onto itself.
        /// </summary>
        public void Rekey(string fromKey, string toKey)
        {
            if (string.IsNullOrEmpty(fromKey) || string.IsNullOrEmpty(toKey) || string.Equals(fromKey, toKey, StringComparison.Ordinal)) return;
            lock (_gate)
            {
                Entry from;
                if (!_byDoc.TryGetValue(fromKey, out from)) return;
                _byDoc.Remove(fromKey);

                Entry to;
                if (!_byDoc.TryGetValue(toKey, out to)) { _byDoc[toKey] = from; return; }

                if (from.Overflowed) { to.Overflowed = true; to.Ids.Clear(); return; }
                if (to.Overflowed) return;
                to.Ids.UnionWith(from.Ids);
                if (to.Ids.Count > _cap) { to.Overflowed = true; to.Ids.Clear(); }
            }
        }
    }
}