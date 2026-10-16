using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>Why a family type was flagged. Ordered loosely by how confident the test is.</summary>
    public enum ProvisionalKind
    {
        /// <summary>A modelled hole: the service passing through it is the design.</summary>
        Void = 0,

        /// <summary>Reserved space: access, maintenance, clearance, swing.</summary>
        Reservation = 1,

        /// <summary>A stand-in nobody has replaced yet.</summary>
        Placeholder = 2,

        /// <summary>Plant that services terminate in but which carries no connector.</summary>
        NoConnector = 3
    }

    public class ProvisionalFinding
    {
        public BuiltInCategory Category;
        public string CategoryName = "";
        public string FamilyName = "";
        public string TypeName = "";
        public int InstanceCount;
        public ProvisionalKind Kind;

        /// <summary>The evidence, in the words that produced it.</summary>
        public List<string> Reasons = new List<string>();

        /// <summary>Services whose end lands inside this type. Only set for NoConnector.</summary>
        public int Terminations;

        /// <summary>The link(s) this finding's elements are in. Host is the unlabelled
        /// default, so a host finding leaves this empty and a link one is called out.</summary>
        public HashSet<string> Models = new HashSet<string>();

        /// <summary>Provenance for the finding row, e.g. "Architectural, Structural".</summary>
        public string ModelsText { get { return LinkContext.FormatModels(Models); } }

        /// <summary>
        /// What this is CALLED. Seeded from <see cref="Kind"/>, then overridden by
        /// whatever the user assigned last time. The detector guesses; the
        /// coordinator decides, and the decision outlives the guess.
        /// </summary>
        public string Label = "";

        public string Evidence { get { return string.Join("; ", Reasons.ToArray()); } }
    }

    /// <summary>
    /// Finds the geometry that clashes but is not a clash.
    ///
    /// Every coordination model carries some. On this project three classes between
    /// them produced most of the register: 1,978 access zones, 416 BWIC slab
    /// openings, and a pre-action valve family still shipping as a placeholder box
    /// whose own description read "User to modify". All three were found by hand,
    /// by chasing an unexplained clash count down to its source — which works, but
    /// takes an afternoon and only finds what you happen to chase.
    ///
    /// The naming differs on every project, so the vocabularies below will never be
    /// complete. That is fine: this reports candidates and the user decides. It is
    /// deliberately never automatic — silently dropping geometry because its name
    /// contained "opening" is exactly the failure this tool exists to prevent.
    /// </summary>
    public static class ProvisionalAudit
    {
        // Terms are lower-case; matching is substring, case-insensitive.

        /// <summary>Modelled holes. "bwic" is builder's work in concrete; "pfc" is
        /// provision for future connection.</summary>
        private static readonly string[] VoidWords =
        {
            "opening", "penetration", "sleeve", "bwic", "void", "puncture",
            "hole", "cut out", "cutout", "builders work", "builder's work", "pfc"
        };

        /// <summary>Space someone has reserved rather than built.</summary>
        private static readonly string[] ReservationWords =
        {
            "access zone", "access space", "clearance", "maintenance", "keep clear",
            "keep-clear", "swing", "pull space", "working space", "service zone",
            "reserved", "envelope", "exclusion zone"
        };

        /// <summary>A stand-in. These turn up in type names, Description and Type Comments.</summary>
        private static readonly string[] PlaceholderWords =
        {
            "user to modify", "to be confirmed", "tbc", "tbd", "placeholder",
            "wip discussion", "for discussion", "for wip", "dummy", "temporary",
            "temp only", "do not use", "sample only", "indicative", "by others"
        };

        /// <summary>
        /// Categories where a missing connector is worth reporting.
        ///
        /// This listed only mechanical and plumbing, justified as "a lighting fixture
        /// legitimately has none". That reasoning was wrong. It is true of a PIPE
        /// connector, but the test here is not about pipes: it asks whether a routed
        /// service ENDS inside the element, and a cable tray dying inside a switchboard
        /// is the same finding as a pipe dying inside a chiller.
        ///
        /// The cost of the omission was concrete. ABC_ELE_Electrical Encloser had 76
        /// open duct ends landing inside it and no connector on it, and could never be
        /// reported, because Electrical Equipment was not on this list. The three
        /// name-based detectors scan every category; only this one was narrowed, so the
        /// gap was invisible unless you went looking for a specific family.
        ///
        /// Generic Models earns its place for the same reason: on this project it holds
        /// 90 real objects once the access zones are set aside.
        /// </summary>
        private static readonly BuiltInCategory[] PlantCategories =
        {
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_DuctTerminal,
            BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_ConduitFitting
        };

        private class Bucket
        {
            public BuiltInCategory Cat;
            public string CatName = "";
            public string Family = "";
            public string Type = "";
            public int Count;
            public ElementId AnyInstance;
            public bool AnyConnector;
            public bool IsPlant;
            public List<ElementId> Instances = new List<ElementId>();
        }

        /// <summary>
        /// Audit the host AND every loaded link, merged. Modelled openings, reserved space
        /// and placeholders live in the architectural and structural links as often as in
        /// the host — and a host-only scan never saw them. Each document is audited in its
        /// OWN coordinates (the no-connector test correlates a service end with plant in the
        /// same model), then findings for the same family+type are merged, counts summed and
        /// the link provenance unioned. Host is the unlabelled default.
        /// </summary>
        public static List<ProvisionalFinding> Run(Document host)
        {
            var merged = new Dictionary<string, ProvisionalFinding>();
            var order = new List<ProvisionalFinding>();

            foreach (var st in LinkContext.ScanDocuments(host))
            {
                foreach (var f in RunOnDoc(st.Doc))
                {
                    string key = (int)f.Category + "|" + f.FamilyName + "|" + f.TypeName;
                    ProvisionalFinding m;
                    if (!merged.TryGetValue(key, out m))
                    {
                        merged[key] = f; order.Add(f); m = f;
                    }
                    else
                    {
                        m.InstanceCount += f.InstanceCount;
                        m.Terminations += f.Terminations;
                        foreach (var r in f.Reasons) if (!m.Reasons.Contains(r)) m.Reasons.Add(r);
                    }
                    if (!st.IsHost && !string.IsNullOrEmpty(st.ModelName)) m.Models.Add(st.ModelName);
                }
            }

            var findings = order;

            // Name everything, then let the user's own assignments win.
            var store = AuditLabelStore.Instance;
            foreach (var f in findings)
            {
                f.Label = DefaultLabel(f.Kind);
                var chosen = store.AssignedTo(f.Category, f.FamilyName, f.TypeName);
                if (!string.IsNullOrEmpty(chosen)) f.Label = chosen;
            }

            findings.Sort(delegate (ProvisionalFinding a, ProvisionalFinding b)
            {
                if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
                return b.InstanceCount.CompareTo(a.InstanceCount);
            });
            return findings;
        }

        /// <summary>
        /// The audit of ONE document — the host or a single link. Detection is entirely
        /// within this document (names, and services whose ends land in its own plant), so
        /// it is coordinate-correct; <see cref="Run"/> merges the per-document results and
        /// applies labels and ordering.
        /// </summary>
        private static List<ProvisionalFinding> RunOnDoc(Document doc)
        {
            var findings = new List<ProvisionalFinding>();
            if (doc == null || !doc.IsValidObject) return findings;

            var plant = new HashSet<int>();
            foreach (var c in PlantCategories) plant.Add((int)c);

            // ONE streamed pass. Constructing a collector costs O(document), so this
            // must not become one collector per family — see CategoryCatalog.Counts.
            var buckets = new Dictionary<string, Bucket>();
            try
            {
                var it = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .GetElementIterator();
                it.Reset();
                while (it.MoveNext())
                {
                    var e = it.Current as Element;
                    if (e == null || !e.IsValidObject) continue;
                    if (PairSuppressor.IsIgnorableElement(e)) continue;

                    Category c;
                    try { c = e.Category; } catch { continue; }
                    if (c == null) continue;

                    // Only things with geometry can produce a clash, so only those
                    // are worth reporting as noise.
                    try { if (e.get_BoundingBox(null) == null) continue; } catch { continue; }

                    var fi = e as FamilyInstance;
                    string fam = "", typ = "";
                    try
                    {
                        if (fi != null && fi.Symbol != null)
                        {
                            typ = fi.Symbol.Name ?? "";
                            if (fi.Symbol.Family != null) fam = fi.Symbol.Family.Name ?? "";
                        }
                        else
                        {
                            var t = doc.GetElement(e.GetTypeId());
                            if (t != null) typ = t.Name ?? "";
                        }
                    }
                    catch { }

                    int cid;
                    try { cid = (int)IdUtil.Value(c.Id); } catch { continue; }

                    var key = cid + "|" + fam + "|" + typ;
                    Bucket b;
                    if (!buckets.TryGetValue(key, out b))
                    {
                        b = new Bucket
                        {
                            Cat = (BuiltInCategory)cid,
                            CatName = c.Name ?? "",
                            Family = fam,
                            Type = typ,
                            IsPlant = plant.Contains(cid)
                        };
                        buckets[key] = b;
                    }
                    b.Count++;
                    if (b.AnyInstance == null) b.AnyInstance = e.Id;
                    // Sampled, not capped for correctness: the question is only
                    // whether ANY instance has a service ending in it. 4,000 is far
                    // above anything real (the largest type here holds 703) and
                    // exists to stop one pathological family filling the index.
                    if (b.IsPlant && b.Instances.Count < 4000) b.Instances.Add(e.Id);

                    if (b.IsPlant && !b.AnyConnector && fi != null)
                    {
                        try
                        {
                            if (fi.MEPModel != null && fi.MEPModel.ConnectorManager != null
                                && fi.MEPModel.ConnectorManager.Connectors.Size > 0)
                                b.AnyConnector = true;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Provisional audit: scanning the model failed", ex);
                return findings;
            }

            // ---- name-based classes
            foreach (var b in buckets.Values)
            {
                var reasons = new List<string>();
                ProvisionalKind kind;

                if (Hits(VoidWords, out reasons, b.Family, b.Type)) kind = ProvisionalKind.Void;
                else if (Hits(ReservationWords, out reasons, b.Family, b.Type)) kind = ProvisionalKind.Reservation;
                else
                {
                    // Placeholder language hides in the description and comments as
                    // often as in the name, and that is where it is most telling: a
                    // type called "Pre Action Valve" looks entirely legitimate until
                    // you read that its Description says "User to modify".
                    string desc = TypeText(doc, b.AnyInstance);
                    if (Hits(PlaceholderWords, out reasons, b.Family, b.Type, desc))
                        kind = ProvisionalKind.Placeholder;
                    else continue;
                }

                findings.Add(new ProvisionalFinding
                {
                    Category = b.Cat,
                    CategoryName = b.CatName,
                    FamilyName = b.Family,
                    TypeName = b.Type,
                    InstanceCount = b.Count,
                    Kind = kind,
                    Reasons = reasons
                });
            }

            // ---- plant with no connector that services actually run into
            try { AddNoConnector(doc, buckets, findings); }
            catch (Exception ex) { Log.Warn("Provisional audit: connector check failed", ex); }

            // Raw findings for this one document. Labelling, provenance and ordering are
            // applied by Run once the per-document results are merged.
            return findings;
        }

        /// <summary>
        /// Plant is only worth reporting when something actually terminates in it —
        /// a family with no connector and no pipework near it is simply a family with
        /// no connector. The test is the one that diagnosed the real case: does an
        /// MEP curve END inside the equipment rather than pass through?
        /// </summary>
        private static void AddNoConnector(Document doc, Dictionary<string, Bucket> buckets,
                                           List<ProvisionalFinding> findings)
        {
            // A type already flagged by name is not reported twice. A modelled hole
            // has no connector and services end in it BY DESIGN, so listing the BWIC
            // openings again under "no connector" is noise about noise.
            var already = new HashSet<string>();
            foreach (var f in findings)
                already.Add((int)f.Category + "|" + f.FamilyName + "|" + f.TypeName);

            var candidates = new List<Bucket>();
            var ids = new List<ElementId>();
            foreach (var b in buckets.Values)
            {
                if (!b.IsPlant || b.AnyConnector || b.Instances.Count == 0) continue;
                if (already.Contains((int)b.Cat + "|" + b.Family + "|" + b.Type)) continue;
                candidates.Add(b);
                ids.AddRange(b.Instances);
            }
            if (ids.Count == 0) return;

            // Reuse the engine's spatial index rather than a collector per instance.
            var index = TargetIndex.Build(doc, ids);

            var owner = new Dictionary<long, Bucket>();
            foreach (var b in candidates)
                foreach (var id in b.Instances) owner[IdUtil.Value(id)] = b;

            var hits = new Dictionary<Bucket, int>();
            var boxes = new Dictionary<long, BoundingBoxXYZ>();

            var curves = new FilteredElementCollector(doc)
                .OfClass(typeof(MEPCurve))
                .WhereElementIsNotElementType()
                .GetElementIterator();
            curves.Reset();
            while (curves.MoveNext())
            {
                var mc = curves.Current as MEPCurve;
                if (mc == null) continue;
                var lc = mc.Location as LocationCurve;
                if (lc == null) continue;

                Curve crv;
                try { crv = lc.Curve; } catch { continue; }
                if (crv == null) continue;

                for (int end = 0; end < 2; end++)
                {
                    XYZ p;
                    try { p = crv.GetEndPoint(end); } catch { continue; }

                    // A point query: a degenerate outline around the endpoint.
                    var o = new Outline(new XYZ(p.X - 0.01, p.Y - 0.01, p.Z - 0.01),
                                        new XYZ(p.X + 0.01, p.Y + 0.01, p.Z + 0.01));
                    List<ElementId> near;
                    try { near = index.Query(o, mc.Id); } catch { continue; }
                    if (near == null) continue;

                    foreach (var nid in near)
                    {
                        long v = IdUtil.Value(nid);
                        BoundingBoxXYZ bb;
                        if (!boxes.TryGetValue(v, out bb))
                        {
                            var el = doc.GetElement(nid);
                            try { bb = el == null ? null : el.get_BoundingBox(null); } catch { bb = null; }
                            boxes[v] = bb;
                        }
                        if (bb == null) continue;
                        if (p.X < bb.Min.X || p.X > bb.Max.X) continue;
                        if (p.Y < bb.Min.Y || p.Y > bb.Max.Y) continue;
                        if (p.Z < bb.Min.Z || p.Z > bb.Max.Z) continue;

                        Bucket b;
                        if (!owner.TryGetValue(v, out b)) continue;
                        int n;
                        hits[b] = hits.TryGetValue(b, out n) ? n + 1 : 1;
                    }
                }
            }

            foreach (var kv in hits)
            {
                findings.Add(new ProvisionalFinding
                {
                    Category = kv.Key.Cat,
                    CategoryName = kv.Key.CatName,
                    FamilyName = kv.Key.Family,
                    TypeName = kv.Key.Type,
                    InstanceCount = kv.Key.Count,
                    Kind = ProvisionalKind.NoConnector,
                    Terminations = kv.Value,
                    Reasons = new List<string>
                    {
                        kv.Value + " service(s) end inside it, and the family carries no connector"
                    }
                });
            }
        }

        public static string DefaultLabel(ProvisionalKind k)
        {
            switch (k)
            {
                case ProvisionalKind.Void:        return AuditLabelStore.Hole;
                case ProvisionalKind.Reservation: return AuditLabelStore.Reservation;
                case ProvisionalKind.Placeholder: return AuditLabelStore.Placeholder;
                default:                          return AuditLabelStore.NoConnector;
            }
        }

        // ----------------------------------------------------- provisional lookup

        private static readonly object IndexGate = new object();

        /// <summary>
        /// One index PER DOCUMENT. It used to be a single slot plus the key of whichever
        /// document filled it, and that turned a hash lookup into a full model scan on the
        /// link path: ClashEngine.CheckAgainstLink asks
        /// <c>IsProvisional(doc, el) || IsProvisional(link.Document, other)</c> for every
        /// reported clash, so the host call evicted the link's index, the link call evicted
        /// the host's, and the next clash paid for both again.
        ///
        /// Measured on Snowdon Towers, 2026-08-30: 0.0008 ms for a warm hit against 43 ms
        /// while alternating — and its 53 link clashes spent 1.72 s rebuilding, inside a
        /// full check that took 3.0 s in total. Host-vs-host never alternates, which is
        /// why a host-only test shows nothing.
        ///
        /// Same shape as ClashVerify._verifySolids, and for the same reason. Keyed on
        /// ClashEngine.DocKey so an unsaved model cannot collide with a saved one.
        /// </summary>
        private static readonly Dictionary<string, HashSet<long>> _provisionalTypes =
            new Dictionary<string, HashSet<long>>();

        /// <summary>
        /// The TYPE ids whose geometry is provisional, for tagging clash results.
        ///
        /// Separate from <see cref="Run"/> and far cheaper: it walks element TYPES
        /// rather than instances, so it does not need the whole-model pass or the
        /// connector geometry. A clash can then be marked "the footprint of this will
        /// change" without running the full audit on every sweep.
        /// </summary>
        public static HashSet<long> ProvisionalTypeIds(Document doc, bool force)
        {
            if (doc == null || !doc.IsValidObject) return new HashSet<long>();

            string key = ClashEngine.DocKey(doc);

            if (!force)
            {
                lock (IndexGate)
                {
                    HashSet<long> hit;
                    if (_provisionalTypes.TryGetValue(key, out hit) && hit != null) return hit;
                }
            }

            var set = new HashSet<long>();
            try
            {
                var it = new FilteredElementCollector(doc)
                    .WhereElementIsElementType()
                    .GetElementIterator();
                it.Reset();
                while (it.MoveNext())
                {
                    var t = it.Current as ElementType;
                    if (t == null) continue;

                    string fam = "", typ = "";
                    try { typ = t.Name ?? ""; } catch { }
                    try { fam = t.FamilyName ?? ""; } catch { }

                    string desc = "";
                    try
                    {
                        var parts = new List<string>();
                        foreach (var bip in new[] { BuiltInParameter.ALL_MODEL_DESCRIPTION,
                                                    BuiltInParameter.ALL_MODEL_TYPE_COMMENTS })
                        {
                            var p = t.get_Parameter(bip);
                            if (p == null) continue;
                            var v = p.AsString();
                            if (!string.IsNullOrEmpty(v)) parts.Add(v);
                        }
                        desc = string.Join(" | ", parts.ToArray());
                    }
                    catch { }

                    List<string> why;
                    // Voids and reservations are DESIGNED to be there; only a
                    // placeholder means "this shape is not final", which is the
                    // question a clash row needs answered.
                    if (Hits(PlaceholderWords, out why, fam, typ, desc))
                        set.Add(IdUtil.Value(t.Id));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Provisional type index failed", ex);
            }

            lock (IndexGate) _provisionalTypes[key] = set;
            return set;
        }

        /// <summary>Drop every document's index. Called from ClashEngine.InvalidateAll,
        /// which clears the solid and scan caches alongside it.</summary>
        public static void InvalidateTypeIndex()
        {
            lock (IndexGate) _provisionalTypes.Clear();
        }

        /// <summary>Description and Type Comments of the instance's type.</summary>
        private static string TypeText(Document doc, ElementId instanceId)
        {
            if (instanceId == null) return "";
            try
            {
                var e = doc.GetElement(instanceId);
                if (e == null) return "";
                var t = doc.GetElement(e.GetTypeId());
                if (t == null) return "";

                var parts = new List<string>();
                foreach (var bip in new[] { BuiltInParameter.ALL_MODEL_DESCRIPTION,
                                            BuiltInParameter.ALL_MODEL_TYPE_COMMENTS })
                {
                    try
                    {
                        var p = t.get_Parameter(bip);
                        if (p == null) continue;
                        var v = p.AsString();
                        if (!string.IsNullOrEmpty(v)) parts.Add(v);
                    }
                    catch { }
                }
                return string.Join(" | ", parts.ToArray());
            }
            catch { return ""; }
        }

        private static bool Hits(string[] words, out List<string> reasons, params string[] fields)
        {
            reasons = new List<string>();
            foreach (var f in fields)
            {
                if (string.IsNullOrEmpty(f)) continue;
                var lower = f.ToLowerInvariant();
                foreach (var w in words)
                    if (lower.IndexOf(w, StringComparison.Ordinal) >= 0)
                        reasons.Add("\"" + w + "\" in \"" + Trim(f) + "\"");
            }
            return reasons.Count > 0;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 60 ? s : s.Substring(0, 57) + "...";
        }
    }
}
