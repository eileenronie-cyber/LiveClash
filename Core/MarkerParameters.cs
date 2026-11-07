using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The 22 Text parameters and the one Integer a marker carries (spec 4, 10.1), as shared parameters with fixed GUIDs
    /// (<see cref="MarkerParameterMap"/>), bound to the marker category as instance parameters, <c>UserModifiable = false</c> and
    /// <c>HideWhenNoValue = true</c>. They are made through a TEMPORARY shared-parameter file; the user's own
    /// <c>SharedParametersFilename</c> is put back in a <c>finally</c> and checked byte for byte (RF13). The parameters are a
    /// projection of the record: written from it, never read back as truth (5.1).
    /// </summary>
    [RevitBound]
    public static class MarkerParameters
    {

        private const string GroupName = "LiveClash";

        /// <summary>The words when the user's shared-parameter setting cannot be read: nothing is touched, and an error text is never written back as a path.</summary>
        internal const string UnreadableText = "The shared parameter file setting could not be read. Nothing was changed.";

        /// <summary>The user's shared-parameter file setting, or null when it cannot be read.</summary>
        private static string SafePath(Autodesk.Revit.ApplicationServices.Application app)
        {
            try { return app.SharedParametersFilename ?? ""; } catch { return null; }
        }

        private static string FileHash(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(no file)";
                return Sha256Hex.Of(File.ReadAllBytes(path));
            }
            catch (Exception ex) { return "(unreadable " + ex.GetType().Name + ")"; }
        }

        /// <summary>
        /// Points <c>Application.SharedParametersFilename</c> at a temporary file for the life of the scope and puts it back in
        /// <see cref="Dispose"/>. The user's own file is never opened or written; its hash is kept to prove it.
        /// </summary>
        internal sealed class TempSharedFile : IDisposable
        {
            private readonly Autodesk.Revit.ApplicationServices.Application _app;
            private readonly string _before;
            private readonly string _hashBefore;
            private readonly string _tmp;
            internal string RestoreError;
            internal bool Restored;

            internal TempSharedFile(Autodesk.Revit.ApplicationServices.Application app, string tmpDir)
            {
                _app = app;
                _before = SafePath(app);
                if (_before == null) throw new InvalidOperationException(UnreadableText);
                _hashBefore = FileHash(_before);
                Directory.CreateDirectory(tmpDir);
                _tmp = Path.Combine(tmpDir, "markers-shared-" + Guid.NewGuid().ToString("N") + ".txt");
                var header = "# This is a Revit shared parameter file.\r\n# Do not edit manually.\r\n"
                    + "*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\n"
                    + "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n";
                File.WriteAllText(_tmp, header, new UnicodeEncoding(false, true));
                _app.SharedParametersFilename = _tmp;
            }

            internal DefinitionFile Open()
            {
                return _app.OpenSharedParameterFile();
            }

            public void Dispose()
            {
                try { _app.SharedParametersFilename = _before; }
                catch (Exception ex) { RestoreError = ex.GetType().Name + ": " + ex.Message; }
                Restored = string.Equals(SafePath(_app), _before, StringComparison.Ordinal) && FileHash(_before) == _hashBefore;
                if (RestoreError != null || !Restored)
                    Log.Warn("The shared parameter file setting was not put back as it was" + (RestoreError == null ? "" : " (" + RestoreError + ")") + "; it may still point at a temporary file.");
                try { File.Delete(_tmp); } catch { }
            }
        }

        // ------------------------------------------------------------------ what is bound now

        private sealed class BoundInfo
        {
            public Guid? Guid;
            public bool OnMarkerCategory;
            public Definition Def;
        }

        /// <summary>Every bound parameter whose name is one of LiveClash's, with its GUID (null when it is not a shared parameter) and whether it is bound to the marker category.</summary>
        private static Dictionary<string, BoundInfo> Scan(Document doc)
        {
            var ours = new HashSet<string>(MarkerParameterMap.Definitions.Select(d => d.Name), StringComparer.Ordinal);
            var found = new Dictionary<string, BoundInfo>(StringComparer.Ordinal);
            var it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                var def = it.Key;
                if (def == null || !ours.Contains(def.Name)) continue;
                var info = new BoundInfo { Def = def };
                var ext = def as ExternalDefinition;
                if (ext != null) info.Guid = ext.GUID;
                else
                {
                    var inner = def as InternalDefinition;
                    var spe = inner == null ? null : doc.GetElement(inner.Id) as SharedParameterElement;
                    if (spe != null) info.Guid = spe.GuidValue;
                }
                var binding = it.Current as ElementBinding;
                if (binding != null && binding.Categories != null)
                    foreach (Category c in binding.Categories)
                        if (c != null && IdUtil.Value(c.Id) == IdUtil.Value(MarkerCategory.Id)) { info.OnMarkerCategory = true; break; }
                found[def.Name] = info;
            }
            return found;
        }

        /// <summary>The first LiveClash parameter name that is bound under another GUID (or is not a shared parameter), or null (spec 7).</summary>
        public static string ForeignName(Document doc)
        {
            var scan = Scan(doc);
            foreach (var d in MarkerParameterMap.Definitions)
            {
                BoundInfo info;
                if (scan.TryGetValue(d.Name, out info) && (!info.Guid.HasValue || info.Guid.Value != d.ParameterGuid)) return d.Name;
            }
            return null;
        }

        /// <summary>At least one of LiveClash's parameters is bound under its own GUID.</summary>
        public static bool AnyBound(Document doc)
        {
            var scan = Scan(doc);
            return MarkerParameterMap.Definitions.Any(d => { BoundInfo info; return scan.TryGetValue(d.Name, out info) && info.Guid.HasValue && info.Guid.Value == d.ParameterGuid; });
        }

        /// <summary>Removes the bindings of LiveClash's own parameters (never one with another GUID). Needs an open transaction. Returns how many.</summary>
        public static int RemoveBindings(Document doc)
        {
            var scan = Scan(doc);
            int removed = 0;
            foreach (var d in MarkerParameterMap.Definitions)
            {
                BoundInfo info;
                if (scan.TryGetValue(d.Name, out info) && info.Guid.HasValue && info.Guid.Value == d.ParameterGuid && doc.ParameterBindings.Remove(info.Def)) removed++;
            }
            return removed;
        }

        /// <summary>All 23 parameters are bound to the marker category under LiveClash's GUIDs.</summary>
        public static bool IsBound(Document doc)
        {
            var scan = Scan(doc);
            foreach (var d in MarkerParameterMap.Definitions)
            {
                BoundInfo info;
                if (!scan.TryGetValue(d.Name, out info) || !info.Guid.HasValue || info.Guid.Value != d.ParameterGuid || !info.OnMarkerCategory) return false;
            }
            return true;
        }

        /// <summary>
        /// Makes and binds whatever is missing. Needs an open transaction, and the bindings must be COMMITTED before markers can
        /// take values. Returns null when everything is bound, or the refusal text (a parameter with LiveClash's name but another GUID).
        /// The user's shared-parameter file is restored whatever happens.
        /// </summary>
        public static string EnsureBound(Document doc)
        {
            var foreign = ForeignName(doc);
            if (foreign != null) return MarkerRefusals.Check(new MarkerEnv { ForeignParameterName = foreign });
            if (IsBound(doc)) return null;

            var app = doc.Application;
            if (SafePath(app) == null) return UnreadableText;
            var scan = Scan(doc);
            using (var scope = new TempSharedFile(app, Path.Combine(Path.GetTempPath(), "lc-markers-shared")))
            {
                var file = scope.Open();
                var group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);
                var cats = app.Create.NewCategorySet();
                cats.Insert(doc.Settings.Categories.get_Item(MarkerCategory.Built));
                foreach (var d in MarkerParameterMap.Definitions)
                {
                    BoundInfo have;
                    if (scan.TryGetValue(d.Name, out have) && have.OnMarkerCategory) continue;
                    var options = new ExternalDefinitionCreationOptions(d.Name, d.IsInteger ? SpecTypeId.Int.Integer : SpecTypeId.String.Text)
                    {
                        GUID = d.ParameterGuid, UserModifiable = false, HideWhenNoValue = true
                    };
                    var def = group.Definitions.Create(options) as ExternalDefinition;
                    if (def == null) throw new InvalidOperationException("Could not make the parameter " + d.Name + ".");
                    var binding = app.Create.NewInstanceBinding(cats);
                    bool ok = scan.ContainsKey(d.Name)
                        ? doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Data)
                        : doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data);
                    if (!ok) throw new InvalidOperationException("Revit would not bind the parameter " + d.Name + ".");
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ values

        private static string Text(Parameter p)
        {
            return p == null ? "" : (p.AsString() ?? "");
        }

        /// <summary>The 22 Text parameters as they stand on the element (empty for a missing one).</summary>
        public static Dictionary<string, string> ReadText(Element e)
        {
            var read = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in MarkerParameterMap.Definitions)
                if (!d.IsInteger) read[d.Name] = Text(e.get_Parameter(d.ParameterGuid));
            return read;
        }

        /// <summary>The hidden status-order Integer as it stands (0 when missing).</summary>
        public static int ReadStatusOrder(Element e)
        {
            var d = MarkerParameterMap.Definitions.First(x => x.IsInteger);
            var p = e.get_Parameter(d.ParameterGuid);
            return p == null ? 0 : p.AsInteger();
        }

        /// <summary>
        /// Writes the Text parameters named in <paramref name="text"/> and, when positive, the status order, changing only those
        /// whose value differs. Needs an open transaction and bound parameters (a missing one throws, naming it).
        /// </summary>
        public static void Write(Element e, Dictionary<string, string> text, int statusOrder)
        {
            if (e == null) throw new InvalidOperationException("There is no element to write.");
            MarkerElements.WhilePinnedOff(e, () =>
            {
                foreach (var d in MarkerParameterMap.Definitions)
                {
                    if (d.IsInteger)
                    {
                        if (statusOrder <= 0) continue;
                        var pi = e.get_Parameter(d.ParameterGuid);
                        if (pi == null) throw new InvalidOperationException("The parameter " + d.Name + " is not bound.");
                        if (pi.AsInteger() != statusOrder && !pi.Set(statusOrder)) throw new InvalidOperationException("Revit would not set " + d.Name + ".");
                        continue;
                    }
                    string want;
                    if (text == null || !text.TryGetValue(d.Name, out want)) continue;
                    want = want ?? "";
                    var p = e.get_Parameter(d.ParameterGuid);
                    if (p == null) throw new InvalidOperationException("The parameter " + d.Name + " is not bound.");
                    if (Text(p) == want) continue;
                    if (!p.Set(want)) throw new InvalidOperationException("Revit would not set " + d.Name + ".");
                }
            });
        }

        /// <summary>The display columns the planner compares (the six read from the elements at Refresh). The two id columns come from the record and are checked as record-derived parameters instead.</summary>
        public static readonly string[] HashedNames = { "LC A Id", "LC A Name", "LC A Category", "LC B Id", "LC B Name", "LC B Category", "LC B Model", "LC Level" };

        /// <summary>
        /// The ONE place a display map is hashed: builder A's RowBuilder.HashOf, the same function the plan's row uses (order-independent).
        /// </summary>
        public static string ShownHash(IDictionary<string, string> shown)
        {
            return RowBuilder.HashOf(shown);
        }

        /// <summary>
        /// The hash of the display parameters as they stand on the marker. When a parameter the record decides (or the status order) was
        /// edited by someone, it returns a hash that can never equal a row's, so the planner rewrites the parameters from the record (5.1).
        /// </summary>
        public static string CurrentShownHash(Element e, MarkerRecord record)
        {
            var have = ReadText(e);
            var shown = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in HashedNames) shown[n] = have[n];
            var hash = ShownHash(shown);
            if (record == null) return hash;
            var expected = MarkerParameterMap.Build(record, shown);
            foreach (var n in MarkerParameterMap.RecordDerivedNames)
                if (have[n] != expected[n]) return "edited:" + hash;
            if (ReadStatusOrder(e) != MarkerStatusText.Order(record.Status)) return "edited:" + hash;
            return hash;
        }

        /// <summary>The names of the parameters whose value on the element differs from the wanted text and status order (only the names present in <paramref name="text"/> are compared).</summary>
        public static List<string> Differing(Element e, Dictionary<string, string> text, int statusOrder)
        {
            var differ = new List<string>();
            var have = ReadText(e);
            foreach (var d in MarkerParameterMap.Definitions)
            {
                if (d.IsInteger)
                {
                    if (statusOrder > 0 && ReadStatusOrder(e) != statusOrder) differ.Add(d.Name);
                    continue;
                }
                string want;
                if (text != null && text.TryGetValue(d.Name, out want) && (want ?? "") != have[d.Name]) differ.Add(d.Name);
            }
            return differ;
        }
    }
}
