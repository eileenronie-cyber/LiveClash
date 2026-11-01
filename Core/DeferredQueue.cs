using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Overflow buffer for the updater.
    ///
    /// IUpdater.Execute runs on Revit's main thread, inside the user's transaction.
    /// Whatever happens in there is time the user spends staring at a frozen cursor,
    /// so a big edit (paste, array, copy-monitor, a model-wide parameter change)
    /// dumps its element ids here instead and they get checked when Revit next goes idle.
    /// </summary>
    public static class DeferredQueue
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, HashSet<long>> Pending =
            new Dictionary<string, HashSet<long>>();

        public static int PendingCount
        {
            get
            {
                lock (Gate)
                {
                    int n = 0;
                    foreach (var kv in Pending) n += kv.Value.Count;
                    return n;
                }
            }
        }

        public static void Enqueue(Document doc, IEnumerable<ElementId> ids)
        {
            if (doc == null || ids == null) return;
            string key = DocKey(doc);

            lock (Gate)
            {
                HashSet<long> set;
                if (!Pending.TryGetValue(key, out set))
                {
                    set = new HashSet<long>();
                    Pending[key] = set;
                }
                foreach (var id in ids) set.Add(IdUtil.Value(id));
            }
        }

        /// <summary>
        /// Pull up to <paramref name="max"/> ids for this document. Returns an empty
        /// list when there is nothing waiting.
        /// </summary>
        public static List<ElementId> Dequeue(Document doc, int max)
        {
            var taken = new List<ElementId>();
            if (doc == null) return taken;
            string key = DocKey(doc);

            lock (Gate)
            {
                HashSet<long> set;
                if (!Pending.TryGetValue(key, out set) || set.Count == 0) return taken;

                var removed = new List<long>();
                foreach (var v in set)
                {
                    taken.Add(IdUtil.From(v));
                    removed.Add(v);
                    if (taken.Count >= max) break;
                }
                foreach (var v in removed) set.Remove(v);
                if (set.Count == 0) Pending.Remove(key);
            }

            return taken;
        }

        /// <summary>Drop a closed document's waiting ids by its key (the Document object is gone by then).</summary>
        public static void ClearKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate) Pending.Remove(key);
        }

        public static void Clear(Document doc)
        {
            lock (Gate)
            {
                if (doc == null) Pending.Clear();
                else Pending.Remove(DocKey(doc));
            }
        }

        /// <summary>
        /// Move a document's waiting ids from one key to another, for the one event that changes a
        /// document's key underneath them: Save As. Left alone, a bulk edit still draining when the
        /// model was saved under a new name was never checked (review 2026-10-31, LOW-2). Merges into
        /// anything already waiting under the new key.
        /// </summary>
        /// <returns>How many ids moved.</returns>
        public static int Rekey(string fromKey, string toKey)
        {
            if (string.IsNullOrEmpty(fromKey) || string.IsNullOrEmpty(toKey) || fromKey == toKey) return 0;
            lock (Gate)
            {
                HashSet<long> from;
                if (!Pending.TryGetValue(fromKey, out from) || from.Count == 0) return 0;
                Pending.Remove(fromKey);
                HashSet<long> to;
                if (!Pending.TryGetValue(toKey, out to)) { Pending[toKey] = from; return from.Count; }
                int moved = 0;
                foreach (var v in from) if (to.Add(v)) moved++;
                return moved;
            }
        }

        /// <summary>
        /// PathName is empty for an unsaved model, so fall back to the title.
        /// Good enough to keep two open documents' queues apart.
        /// </summary>
        private static string DocKey(Document doc)
        {
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName;
                return "unsaved::" + doc.Title;
            }
            catch { return "unknown"; }
        }
    }
}
