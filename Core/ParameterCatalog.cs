using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>One parameter the user can filter on, as offered by the picker.</summary>
    public class ParameterInfo
    {
        public string Name;
        public ParamScope Scope;          // Instance or Type — where it was found
        public string StorageType;
        public bool IsShared;
        public string SampleValue;        // what it actually reads on a real element
        public HashSet<string> Categories = new HashSet<string>();

        /// <summary>The model(s) this parameter was found in — host and/or link names.</summary>
        public HashSet<string> Models = new HashSet<string>();

        public string ScopeText { get { return Scope == ParamScope.Type ? "Type" : "Instance"; } }

        /// <summary>Provenance, shown in the picker so a link-only parameter is recognisable.</summary>
        public string ModelsText { get { return LinkContext.FormatModels(Models); } }

        public string CategoriesText
        {
            get
            {
                var list = new List<string>(Categories);
                list.Sort();
                if (list.Count <= 3) return string.Join(", ", list.ToArray());
                return list[0] + ", " + list[1] + " +" + (list.Count - 2) + " more";
            }
        }

        public string Detail
        {
            get
            {
                var bits = ScopeText + " · " + StorageType + (IsShared ? " · shared" : "");
                if (!string.IsNullOrEmpty(SampleValue)) bits += "  e.g. \"" + SampleValue + "\"";
                var m = ModelsText;
                if (!string.IsNullOrEmpty(m)) bits += "  · in " + m;
                return bits;
            }
        }
    }

    /// <summary>
    /// Discovers which parameters are actually available on the categories in play,
    /// so the picker offers real choices rather than a list of everything Revit
    /// could theoretically have.
    ///
    /// MUST be built in Revit API context — it reads the document. The filter window
    /// opens from a WPF button, which is not API context, so the catalog is gathered
    /// by an ExternalEvent first and handed to the dialog.
    ///
    /// Sampling, not exhaustive: a handful of elements per category is enough to see
    /// which parameters exist, and enumerating every parameter on 40,000 elements to
    /// populate a dropdown would be absurd. The sample value shown beside each name
    /// is the most useful part — it tells you what to type without guessing at
    /// formatting or units.
    /// </summary>
    public static class ParameterCatalog
    {
        /// <summary>Elements inspected per category.</summary>
        private const int SamplePerCategory = 4;

        public static List<ParameterInfo> Build(Document doc, IEnumerable<BuiltInCategory> categories)
        {
            var byKey = new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);
            if (doc == null || !doc.IsValidObject || categories == null) return Sorted(byKey);

            var cats = new List<BuiltInCategory>(categories);

            // Host plus every loaded link, so a parameter that only exists on link elements
            // (a discipline modelled in its own linked file) is still offered. Sampling is
            // per category PER document, so a link-only category is sampled from the link.
            foreach (var st in LinkContext.ScanDocuments(doc))
                foreach (var bic in cats)
                {
                    List<Element> sample;
                    try
                    {
                        sample = new List<Element>();
                        var it = new FilteredElementCollector(st.Doc)
                            .OfCategory(bic)
                            .WhereElementIsNotElementType()
                            .GetElementIterator();
                        it.Reset();
                        while (it.MoveNext() && sample.Count < SamplePerCategory)
                        {
                            var e = it.Current as Element;
                            if (e != null) sample.Add(e);
                        }
                    }
                    catch { continue; }

                    if (sample.Count == 0) continue;
                    string catName = ClashSettings.DisplayName(bic);
                    try { if (sample[0].Category != null) catName = sample[0].Category.Name; } catch { }

                    // Provenance lists LINKS only — the host is the unlabelled default, so a
                    // host parameter shows no tag and a link-only one is called out.
                    string prov = st.IsHost ? null : st.ModelName;
                    foreach (var e in sample)
                    {
                        Collect(e, ParamScope.Instance, catName, prov, byKey);

                        // Type lives in the ELEMENT's own document — the link's, for a link
                        // element — so resolve it there, not in the host.
                        Element type = null;
                        try
                        {
                            var tid = e.GetTypeId();
                            if (tid != null && tid != ElementId.InvalidElementId)
                                type = st.Doc.GetElement(tid);
                        }
                        catch { }
                        if (type != null) Collect(type, ParamScope.Type, catName, prov, byKey);
                    }
                }

            DropEmptyInstanceShadows(byKey);
            return Sorted(byKey);
        }

        /// <summary>
        /// Remove the blank INSTANCE shadow of a parameter that carries its value on the
        /// TYPE — "Family Name" and "Type Name" are the common case: they exist as empty
        /// instance parameters, so the picker offered an Instance-scoped entry that can
        /// never yield a Browse value and, if picked, silently matches nothing. When the
        /// same name also appears as a Type entry that DOES read a value, the instance
        /// shadow is the trap and the working Type entry is what to keep. An instance entry
        /// that reads a value on any sampled element keeps its own row untouched.
        /// </summary>
        private static void DropEmptyInstanceShadows(Dictionary<string, ParameterInfo> byKey)
        {
            var drop = new List<string>();
            foreach (var kv in byKey)
            {
                var info = kv.Value;
                if (info.Scope != ParamScope.Instance) continue;
                if (!string.IsNullOrEmpty(info.SampleValue)) continue;   // it does yield values

                ParameterInfo typeInfo;
                if (byKey.TryGetValue(ParamScope.Type + "::" + info.Name, out typeInfo)
                    && !string.IsNullOrEmpty(typeInfo.SampleValue))
                    drop.Add(kv.Key);
            }
            foreach (var k in drop) byKey.Remove(k);
        }

        private static void Collect(Element e, ParamScope scope, string catName, string modelName,
                                    Dictionary<string, ParameterInfo> sink)
        {
            ParameterSet ps;
            try { ps = e.Parameters; } catch { return; }
            if (ps == null) return;

            foreach (Parameter p in ps)
            {
                string name;
                try
                {
                    if (p == null || p.Definition == null) continue;
                    name = p.Definition.Name;
                }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;

                // Instance and Type entries are kept apart: "Comments" and
                // "Type Comments" are different parameters, and so is the same name
                // appearing on both.
                string key = scope + "::" + name;

                ParameterInfo info;
                if (!sink.TryGetValue(key, out info))
                {
                    info = new ParameterInfo
                    {
                        Name = name,
                        Scope = scope,
                        StorageType = SafeStorage(p),
                        IsShared = SafeShared(p)
                    };
                    sink[key] = info;
                }

                info.Categories.Add(catName);
                if (!string.IsNullOrEmpty(modelName)) info.Models.Add(modelName);

                if (string.IsNullOrEmpty(info.SampleValue))
                {
                    var v = SafeValue(p, e);
                    if (!string.IsNullOrEmpty(v)) info.SampleValue = Trim(v);
                }
            }
        }

        private static string Trim(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
        }

        private static string SafeStorage(Parameter p)
        {
            try { return p.StorageType.ToString(); } catch { return "?"; }
        }

        private static bool SafeShared(Parameter p)
        {
            try { return p.IsShared; } catch { return false; }
        }

        /// <summary>Same read path as the scan and the match test - see ParameterReader.</summary>
        private static string SafeValue(Parameter p, Element e)
        {
            return ParameterReader.ValueOf(p, e);
        }

        private static List<ParameterInfo> Sorted(Dictionary<string, ParameterInfo> byKey)
        {
            var list = new List<ParameterInfo>(byKey.Values);
            list.Sort(delegate (ParameterInfo a, ParameterInfo b)
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Scope.CompareTo(b.Scope);
            });
            return list;
        }
    }
}
