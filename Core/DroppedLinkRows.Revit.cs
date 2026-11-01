using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using LiveClash.UI;

namespace LiveClash.Core
{
    /// <summary>The Revit-bound half of <see cref="DroppedLinkRows"/>: grouping rows by their link instance, and asking
    /// the document whether an instance is back. Thin on purpose; the rule is in DroppedLinkRows.cs.</summary>
    public static partial class DroppedLinkRows
    {
        /// <summary>Keep <paramref name="rows"/> (pairs just dropped because their link instance is gone), grouped by
        /// instance. Never throws.</summary>
        internal static void HoldFor(Document doc, IEnumerable<ClashResult> rows)
        {
            if (doc == null || rows == null) return;
            try
            {
                string key = ClashEngine.DocKey(doc);
                var byInstance = new Dictionary<long, List<ClashResult>>();
                foreach (var r in rows)
                {
                    if (r == null || r.LinkInstanceId == null) continue;
                    long id = IdUtil.Value(r.LinkInstanceId);
                    List<ClashResult> list;
                    if (!byInstance.TryGetValue(id, out list)) byInstance[id] = list = new List<ClashResult>();
                    list.Add(r);
                }
                foreach (var kv in byInstance) Hold(key, kv.Key, kv.Value);
            }
            catch (Exception ex) { Log.Warn("Could not keep the dropped link pairs", ex); }
        }

        /// <summary>
        /// Put back the held pairs whose link instance exists again (an undo of the deletion), as Unverified, and say so
        /// once on the panel. The next verify pass re-tests them. Never throws. Returns how many came back.
        /// </summary>
        internal static int RelistFor(Document doc)
        {
            if (doc == null) return 0;
            try
            {
                string key = ClashEngine.DocKey(doc);
                if (Count(key) == 0) return 0;

                var back = TakeBack(key, id =>
                {
                    try
                    {
                        var e = doc.GetElement(IdUtil.From(id)) as RevitLinkInstance;
                        return CountsAsBack(e != null, e != null && e.IsValidObject);   // a stale deleted instance is not "back" (D3-3)
                    }
                    catch { return false; }
                });
                if (back.Count == 0) return 0;

                ClashResultStore.Instance.AddRange(back);
                string said = RelistedSentence(back.Count);
                Log.Info("Link pairs back: " + said);
                var panel = ClashPaneProvider.Panel;
                if (panel != null) panel.SetStatus(said);
                return back.Count;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not list the dropped link pairs again", ex);
                return 0;
            }
        }
    }
}
