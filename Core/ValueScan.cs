using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>One distinct value found in the model, and how many elements carry it.</summary>
    public class ValueChoice
    {
        public string ParameterName;
        public ParamScope Scope;

        /// <summary>As found in the model — this is what gets stored when picked.</summary>
        public string Display;

        public int Count;

        /// <summary>The model(s) this value was found in — host and/or link names.</summary>
        public HashSet<string> Models = new HashSet<string>();

        public string Label { get { return Display + "   (" + Count + ")"; } }
        public string ParamLabel
        {
            get { return Scope == ParamScope.Either ? ParameterName : ParameterName + " (" + Scope + ")"; }
        }

        /// <summary>Provenance, shown in the Browse list so a link-only value is recognisable.</summary>
        public string ModelsText { get { return LinkContext.FormatModels(Models); } }
    }

    /// <summary>
    /// Scans the model for the distinct values of the nominated parameters, with
    /// counts — what the Browse picker offers.
    ///
    /// The point of browsing rather than typing: you rarely know the exact string,
    /// but you recognise it instantly in a list, and the count tells you whether you
    /// picked the one that covers 900 elements or the one covering 3 that somebody
    /// mistyped.
    ///
    /// Reads through <see cref="ParameterReader"/>, the same path the match test
    /// uses — anything else and a picked value would not compare equal to what the
    /// check reads back.
    ///
    /// Empty values are skipped. An empty pick normalises identically to "element
    /// has no value", so offering one would let the user select everything that
    /// merely lacks the parameter.
    /// </summary>
    public static class ValueScan
    {
        /// <summary>
        /// Scans EVERY element in the ticked categories by default.
        ///
        /// It was capped at 20,000 and that was wrong: this model has 40,175 source
        /// elements, so every count came back an undercount — "Carbon Steel -
        /// Chilled Water Pipes" reported 3,066 when the filter it produced actually
        /// matched 3,733. A count that is quietly short is worse than no count,
        /// because the whole reason to browse rather than type is to see how much a
        /// value covers. Browsing is an explicit, one-off action behind a wait
        /// cursor, so a few seconds is the right trade for a number you can trust.
        /// </summary>
        // ------------------------------------------------------------------ cache

        private static readonly object CacheGate = new object();
        private static string _cacheKey;
        private static List<ValueChoice> _cached;

        /// <summary>
        /// The scan reads every element in the ticked categories — 6.4 s on a
        /// 40,000-element model — and Browse was paying it on every single click,
        /// including reopening the same list a second later. The values do not change
        /// while a dialog is open, so the answer is kept and only recomputed when the
        /// question changes (different document, categories or parameters) or when
        /// the user explicitly asks with Rescan.
        /// </summary>
        public static void Invalidate()
        {
            lock (CacheGate) { _cacheKey = null; _cached = null; }
        }

        private static string KeyFor(List<LinkContext.ScanTarget> scanDocs,
            IEnumerable<BuiltInCategory> categories, IEnumerable<ParamRef> parameters)
        {
            var cats = new List<string>();
            foreach (var c in categories) cats.Add(((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture));
            cats.Sort();

            var prms = new List<string>();
            foreach (var p in parameters) prms.Add(p.Key);
            prms.Sort();

            // The signature of every scanned document (host + loaded links), so loading or
            // unloading a link changes the key and the cache rebuilds on its own.
            string docKey = LinkContext.ScanSignature(scanDocs);

            return docKey + "||" + string.Join(",", cats.ToArray())
                 + "||" + string.Join(",", prms.ToArray());
        }

        public static List<ValueChoice> Collect(
            Document doc,
            IEnumerable<BuiltInCategory> categories,
            IEnumerable<ParamRef> parameters,
            bool forceRescan)
        {
            if (doc == null || categories == null || parameters == null)
                return Collect(doc, categories, parameters);

            // Enumerate the loaded-link set ONCE and thread it through both the cache key
            // and the scan, rather than calling ScanDocuments (a link collector plus a
            // GetLinkDocument per link) twice for the same call.
            var scanDocs = LinkContext.ScanDocuments(doc);
            var key = KeyFor(scanDocs, categories, parameters);
            if (!forceRescan)
            {
                lock (CacheGate)
                    if (_cacheKey == key && _cached != null) return _cached;
            }

            var fresh = Collect(doc, scanDocs, categories, parameters, int.MaxValue);
            lock (CacheGate) { _cacheKey = key; _cached = fresh; }
            return fresh;
        }

        public static List<ValueChoice> Collect(
            Document doc,
            IEnumerable<BuiltInCategory> categories,
            IEnumerable<ParamRef> parameters,
            int maxElements = int.MaxValue)
        {
            return Collect(doc, LinkContext.ScanDocuments(doc), categories, parameters, maxElements);
        }

        private static List<ValueChoice> Collect(
            Document doc,
            List<LinkContext.ScanTarget> scanDocs,
            IEnumerable<BuiltInCategory> categories,
            IEnumerable<ParamRef> parameters,
            int maxElements)
        {
            var counts = new Dictionary<string, int>();
            var display = new Dictionary<string, ValueChoice>();

            if (doc == null || !doc.IsValidObject || categories == null || parameters == null)
                return Sorted(display, counts);

            var paramList = new List<ParamRef>(parameters);
            if (paramList.Count == 0) return Sorted(display, counts);

            var cats = new List<BuiltInCategory>(categories);
            if (cats.Count == 0) return Sorted(display, counts);

            // Scan the host AND every loaded link, merging counts — so a value that only
            // exists on link elements (a discipline in its own linked file) is offered,
            // with a count spanning everything the engine would actually check. maxElements
            // is a TOTAL budget across the documents (default: unbounded).
            int seen = 0;
            foreach (var st in scanDocs)
            {
                if (seen >= maxElements) break;

                FilteredElementIterator it;
                try
                {
                    // Stream elements rather than collecting ids and fetching each one —
                    // this is the whole-model pass, so the round trip is worth avoiding.
                    it = new FilteredElementCollector(st.Doc)
                        .WhereElementIsNotElementType()
                        .WherePasses(new ElementMulticategoryFilter(cats))
                        .GetElementIterator();
                    it.Reset();
                }
                catch (Exception ex)
                {
                    Log.Warn("Value scan: could not collect elements", ex);
                    continue;
                }

                while (it.MoveNext())
                {
                    if (seen++ >= maxElements) break;

                    var e = it.Current as Element;
                    if (e == null || !e.IsValidObject) continue;

                    foreach (var p in paramList)
                    {
                        if (string.IsNullOrEmpty(p.Name)) continue;

                        var raw = ParameterReader.Read(e, p.Name, p.Scope);
                        var norm = ParameterFilter.Norm(raw);
                        if (norm.Length == 0) continue;

                        var key = p.Scope + "::" + p.Name.ToLowerInvariant() + "::" + norm;

                        int n;
                        counts[key] = counts.TryGetValue(key, out n) ? n + 1 : 1;

                        ValueChoice vc;
                        if (!display.TryGetValue(key, out vc))
                        {
                            vc = new ValueChoice
                            {
                                ParameterName = p.Name,
                                Scope = p.Scope,
                                Display = raw.Trim()
                            };
                            display[key] = vc;
                        }
                        // Links only — host is the unlabelled default (see ParameterCatalog).
                        if (!st.IsHost && !string.IsNullOrEmpty(st.ModelName)) vc.Models.Add(st.ModelName);
                    }
                }
            }

            return Sorted(display, counts);
        }

        private static List<ValueChoice> Sorted(
            Dictionary<string, ValueChoice> display, Dictionary<string, int> counts)
        {
            var list = new List<ValueChoice>();
            foreach (var kv in display)
            {
                int n;
                counts.TryGetValue(kv.Key, out n);
                kv.Value.Count = n;
                list.Add(kv.Value);
            }

            // Grouped by parameter, then most common first — the value that covers
            // most of the model is usually the one being looked for.
            list.Sort(delegate (ValueChoice a, ValueChoice b)
            {
                int c = string.Compare(a.ParameterName, b.ParameterName, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                if (a.Count != b.Count) return b.Count - a.Count;
                return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }
    }
}
