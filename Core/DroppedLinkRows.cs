using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// The pairs that were dropped from the list because their link INSTANCE was gone, kept for the session so that an
    /// undo of that deletion can put them back (delta review 2026-10-31, DM3; second-pass SP4).
    ///
    /// A link instance deleted in a view with the Delete key is an ordinary undoable deletion. Auto-verify, Recheck and
    /// the full check dropped its pairs (DN1: a removed link's rows would otherwise stay for ever), and nothing brought
    /// them back when Ctrl+Z restored the instance with the same id: the undo handler re-checks only touched ids, a link
    /// instance is not a source, and the live updater fires only on host edits. So the link was back and its clashes
    /// were missing from the panel until a full check. Holding the rows and re-listing them when the instance exists
    /// again closes that without bringing back the over-reporting DN1 removed.
    ///
    /// Pure and keyed by the document (<c>ClashEngine.DocKey</c>) and the instance's id value, so the rule is
    /// asserted headlessly; the part that asks a Revit document whether the instance exists is in
    /// DroppedLinkRows.Revit.cs.
    /// </summary>
    public static partial class DroppedLinkRows
    {
        /// <summary>Keep <paramref name="rows"/>, dropped because the instance <paramref name="instanceId"/> of the
        /// document <paramref name="docKey"/> is gone. A pair held twice is held once.</summary>
        public static void Hold(string docKey, long instanceId, IEnumerable<ClashResult> rows)
        {
            if (docKey == null || rows == null) return;
            lock (Gate)
            {
                Dictionary<long, Dictionary<string, ClashResult>> byInstance;
                if (!Held.TryGetValue(docKey, out byInstance)) Held[docKey] = byInstance = new Dictionary<long, Dictionary<string, ClashResult>>();
                Dictionary<string, ClashResult> byKey;
                if (!byInstance.TryGetValue(instanceId, out byKey)) byInstance[instanceId] = byKey = new Dictionary<string, ClashResult>(StringComparer.Ordinal);

                foreach (var r in rows)
                    if (r != null && !string.IsNullOrEmpty(r.Key) && !byKey.ContainsKey(r.Key))
                        byKey[r.Key] = r.Clone();   // a copy: the row the caller holds goes on to be changed or forgotten
            }
        }

        /// <summary>
        /// The held rows of <paramref name="docKey"/> whose instance <paramref name="instanceExists"/> says is back, as
        /// copies listed at Unverified (nothing has measured them since). They leave the holder: each comes back once.
        /// A lookup that throws is "not back".
        /// </summary>
        public static List<ClashResult> TakeBack(string docKey, Func<long, bool> instanceExists)
        {
            var back = new List<ClashResult>();
            if (docKey == null || instanceExists == null) return back;
            lock (Gate)
            {
                Dictionary<long, Dictionary<string, ClashResult>> byInstance;
                if (!Held.TryGetValue(docKey, out byInstance)) return back;

                var returned = new List<long>();
                foreach (var kv in byInstance)
                {
                    bool exists;
                    try { exists = instanceExists(kv.Key); }
                    catch { exists = false; }   // not knowing is "not back": the pair stays held, never guessed onto the panel
                    if (!exists) continue;

                    foreach (var held in kv.Value.Values)
                    {
                        var copy = held.Clone();
                        copy.Severity = ClashSeverity.Unverified;
                        back.Add(copy);
                    }
                    returned.Add(kv.Key);
                }
                foreach (var id in returned) byInstance.Remove(id);
                if (byInstance.Count == 0) Held.Remove(docKey);
            }
            return back;
        }

        /// <summary>
        /// Does an element the document handed back for a held instance's id count as the instance being BACK (an undo of its deletion)?
        /// Only a link instance that is still a valid object. A just-deleted id can hand back a stale element that still IS a
        /// RevitLinkInstance by type (delta review 3, D3-3); counting it re-listed the pairs, auto-verify dropped them again, and so on.
        /// </summary>
        public static bool CountsAsBack(bool isLinkInstance, bool isValidObject)
        {
            return isLinkInstance && isValidObject;
        }

        /// <summary>How many rows are held for <paramref name="docKey"/>.</summary>
        public static int Count(string docKey)
        {
            if (docKey == null) return 0;
            lock (Gate)
            {
                Dictionary<long, Dictionary<string, ClashResult>> byInstance;
                if (!Held.TryGetValue(docKey, out byInstance)) return 0;
                int n = 0;
                foreach (var kv in byInstance) n += kv.Value.Count;
                return n;
            }
        }

        /// <summary>Let go of everything held for <paramref name="docKey"/>.</summary>
        public static void Forget(string docKey)
        {
            if (docKey == null) return;
            lock (Gate) Held.Remove(docKey);
        }

        /// <summary>What the panel says when rows come back: "N pair(s) listed again as Unverified: their link came back."</summary>
        public static string RelistedSentence(int count)
        {
            return count + " pair(s) listed again as Unverified: their link came back.";
        }

        // docKey -> link instance id value -> pair key -> the pair as it was dropped. Everything on Revit's main thread
        // in practice; the lock is for the headless checks and costs nothing.
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Dictionary<long, Dictionary<string, ClashResult>>> Held =
            new Dictionary<string, Dictionary<long, Dictionary<string, ClashResult>>>(StringComparer.Ordinal);
    }
}
