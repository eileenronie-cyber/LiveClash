using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// One loaded link instance: its document, its name, and the transform that
    /// maps link coordinates into host coordinates.
    /// This is the "append an NWC" equivalent — every loaded link becomes another
    /// model in the coordination set.
    /// </summary>
    public class LinkContext
    {
        public ElementId InstanceId { get; private set; }
        public Document Document { get; private set; }
        public string Name { get; private set; }

        /// <summary>Link space → host space.</summary>
        public Transform ToHost { get; private set; }

        /// <summary>Host space → link space. Used to push host solids into the link doc.</summary>
        public Transform ToLink { get; private set; }

        /// <summary>One document the filter discovers from, with the name to show as its
        /// provenance in the picker ("This model" for the host, the link name otherwise).</summary>
        public class ScanTarget
        {
            public Document Doc;
            public string ModelName;
            public bool IsHost;
        }

        /// <summary>
        /// The documents the FILTER should discover from — categories, parameters and
        /// values — so a discipline that lives entirely in a link (electrical, HVAC, fire
        /// protection each in its own linked file) is offered in the picker at all. The
        /// clash engine already checks link elements; without this the filter could never
        /// see what to filter them ON, because the host holds none of them.
        ///
        /// Host first, then every loaded link — but only when link checking is on, since a
        /// value picked from a link that is never checked would match nothing and mislead.
        /// Each carries the model name so the picker can show WHERE a choice came from.
        /// </summary>
        public static List<ScanTarget> ScanDocuments(Document host)
        {
            var docs = new List<ScanTarget>();
            if (host == null || !host.IsValidObject) return docs;

            string hostName = "This model";
            try { if (!string.IsNullOrEmpty(host.Title)) hostName = host.Title; } catch { }
            docs.Add(new ScanTarget { Doc = host, ModelName = hostName, IsHost = true });

            bool includeLinks = true;
            try { includeLinks = ClashSettings.Current.CheckHostVsLinks; } catch { }
            if (includeLinks)
                foreach (var lc in CollectFrom(host))
                    if (lc.Document != null && lc.Document.IsValidObject)
                        docs.Add(new ScanTarget { Doc = lc.Document, ModelName = TrimRvt(lc.Name), IsHost = false });

            return docs;
        }

        /// <summary>
        /// Format a provenance set for a picker row: sorted, then "A, B +N more" past two.
        /// The ONE formatter every picker shares (category rows, the parameter picker, the
        /// Browse values, audit findings), so they cannot drift. Empty set → "".
        /// </summary>
        public static string FormatModels(ICollection<string> models)
        {
            if (models == null || models.Count == 0) return "";
            var list = new List<string>(models);
            list.Sort();
            if (list.Count <= 2) return string.Join(", ", list.ToArray());
            return list[0] + ", " + list[1] + " +" + (list.Count - 2) + " more";
        }

        /// <summary>Drop a trailing ".rvt" so the picker shows "Electrical", not
        /// "…Sample Electrical.rvt". Display only — the link's real name is unchanged.</summary>
        private static string TrimRvt(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
        }

        /// <summary>
        /// An order-independent signature of a scan-document set, for cache keys. Folding it
        /// into the key means loading or unloading a link — or toggling link checking —
        /// changes the key and the cached catalog/scan rebuilds on its own, with no explicit
        /// invalidation to remember (ValueScan.Invalidate has no callers by design).
        /// </summary>
        public static string ScanSignature(IEnumerable<ScanTarget> docs)
        {
            var keys = new List<string>();
            if (docs != null)
                foreach (var t in docs)
                {
                    try { keys.Add(string.IsNullOrEmpty(t.Doc.PathName) ? t.Doc.Title : t.Doc.PathName); }
                    catch { keys.Add("?"); }
                }
            keys.Sort();
            return string.Join("|", keys.ToArray());
        }

        public static List<LinkContext> CollectFrom(Document host)
        {
            var list = new List<LinkContext>();
            if (host == null || !host.IsValidObject) return list;

            var instances = new FilteredElementCollector(host)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>();

            foreach (var inst in instances)
            {
                Document linkDoc = null;
                try { linkDoc = inst.GetLinkDocument(); } catch { }

                // Null means the link is unloaded or not found — skip it silently.
                if (linkDoc == null) continue;

                Transform toHost;
                try { toHost = inst.GetTotalTransform(); } catch { toHost = Transform.Identity; }
                if (toHost == null) toHost = Transform.Identity;

                list.Add(new LinkContext
                {
                    InstanceId = inst.Id,
                    Document = linkDoc,
                    Name = DisplayName(host, inst, linkDoc),
                    ToHost = toHost,
                    ToLink = toHost.Inverse
                });
            }

            return list;
        }

        private static string DisplayName(Document host, RevitLinkInstance inst, Document linkDoc)
        {
            try
            {
                var type = host.GetElement(inst.GetTypeId()) as RevitLinkType;
                if (type != null && !string.IsNullOrEmpty(type.Name)) return type.Name;
            }
            catch { }

            try { if (!string.IsNullOrEmpty(linkDoc.Title)) return linkDoc.Title; } catch { }
            return "Linked model";
        }
    }
}
