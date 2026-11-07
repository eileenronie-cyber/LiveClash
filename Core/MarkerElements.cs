using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>The category the markers live in (spec Q1): one place, so probe P1 can change it with one line.</summary>
    [RevitBound]
    public static class MarkerCategory
    {
        public const BuiltInCategory Built = BuiltInCategory.OST_SpecialityEquipment;

        public static ElementId Id
        {
            get { return new ElementId(Built); }
        }
    }

    /// <summary>One marker found in a model, with what the planner needs to know about its element.</summary>
    [RevitBound]
    public sealed class MarkerFound
    {
        public DirectShape Element { get; set; }
        public ElementId Id { get; set; }
        public string Uid { get; set; }
        public MarkerReadResult Read { get; set; }
        /// <summary>The marker is a member of a group (never edited, T11).</summary>
        public bool Grouped { get; set; }
        /// <summary>The centre of the element's bounding box (the octahedron is symmetric about its origin); null when it has no geometry.</summary>
        public XYZ Center { get; set; }
        /// <summary>The workset id, or -1 when the model is not workshared.</summary>
        public int Workset { get; set; }
    }

    /// <summary>
    /// The marker element: one <see cref="DirectShapeType"/> "LiveClash clash marker" (a 300 mm octahedron), instances of it at the
    /// clash points, pinned (spec 3, 6.2, 10.6). Identity is ApplicationId "LiveClash", ApplicationDataId "marker/N" and the
    /// stored record, never the parameter text (5.1, RF12).
    /// </summary>
    [RevitBound]
    public static class MarkerElements
    {
        public const string TypeName = "LiveClash clash marker";

        private const double HalfMm = 150.0;

        private static double Mm(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        private static IList<GeometryObject> Octahedron()
        {
            double r = Mm(HalfMm);
            var px = new XYZ(r, 0, 0); var nx = new XYZ(-r, 0, 0);
            var py = new XYZ(0, r, 0); var ny = new XYZ(0, -r, 0);
            var pz = new XYZ(0, 0, r); var nz = new XYZ(0, 0, -r);
            var b = new TessellatedShapeBuilder();
            b.OpenConnectedFaceSet(true);
            Action<XYZ, XYZ, XYZ> tri = (a, c, e) => b.AddFace(new TessellatedFace(new List<XYZ> { a, c, e }, ElementId.InvalidElementId));
            tri(pz, px, py); tri(pz, py, nx); tri(pz, nx, ny); tri(pz, ny, px);
            tri(nz, py, px); tri(nz, nx, py); tri(nz, ny, nx); tri(nz, px, ny);
            b.CloseConnectedFaceSet();
            b.Target = TessellatedShapeBuilderTarget.Solid;
            b.Fallback = TessellatedShapeBuilderFallback.Abort;
            b.Build();
            return b.GetBuildResult().GetGeometricalObjects();
        }

        /// <summary>The marker type's id, or null when the model has none yet.</summary>
        public static ElementId FindType(Document doc)
        {
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(DirectShapeType)))
                if (t.Name == TypeName && t.Category != null && IdUtil.Value(t.Category.Id) == IdUtil.Value(MarkerCategory.Id)) return t.Id;
            return null;
        }

        /// <summary>The marker type, made once. Needs an open transaction.</summary>
        public static ElementId EnsureType(Document doc)
        {
            var found = FindType(doc);
            if (found != null) return found;
            var type = DirectShapeType.Create(doc, TypeName, MarkerCategory.Id);
            type.SetShape(Octahedron());
            var library = DirectShapeLibrary.GetDirectShapeLibrary(doc);
            if (!library.Contains(TypeName)) library.AddDefinitionType(TypeName, type.Id);
            return type.Id;
        }

        /// <summary>
        /// A new marker at <paramref name="point"/>, pinned, with LiveClash's application ids, on workset <paramref name="worksetId"/>
        /// (-1 = none, a model that is not workshared). Needs an open transaction. The record and parameters are written by the caller.
        /// </summary>
        public static DirectShape Create(Document doc, ElementId typeId, XYZ point, int worksetId)
        {
            // The fourth argument is the id of a shape definition stored in the DirectShapeLibrary (EnsureType adds it under TypeName), not an application data id.
            var ds = DirectShape.CreateElementInstance(doc, typeId, MarkerCategory.Id, TypeName, Transform.CreateTranslation(point));
            if (ds == null) throw new InvalidOperationException("Revit did not make the clash marker (the shape definition was not found).");
            ds.ApplicationId = MarkerFormats.ApplicationId;
            ds.ApplicationDataId = MarkerFormats.ApplicationDataId;
            if (worksetId >= 0)
            {
                var p = ds.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (p != null && !p.IsReadOnly) p.Set(worksetId);
            }
            ds.Pinned = true;
            return ds;
        }

        /// <summary>Runs <paramref name="act"/> with the element unpinned, and pins it again whatever happens.</summary>
        internal static void WhilePinnedOff(Element e, Action act)
        {
            if (e != null && e.Pinned)
            {
                e.Pinned = false;
                try { act(); }
                finally { e.Pinned = true; }
            }
            else act();
        }

        /// <summary>Moves the marker so its centre is at <paramref name="to"/>: unpin, move, pin again (works whatever the API does for a pinned move, P6). Nothing happens when it is already there. Needs an open transaction.</summary>
        public static void Move(Document doc, DirectShape marker, XYZ to)
        {
            var from = Center(marker);
            if (from == null || to == null) return;
            var delta = to - from;
            if (delta.GetLength() < Mm(0.01)) return;
            WhilePinnedOff(marker, () => ElementTransformUtils.MoveElement(doc, marker.Id, delta));
        }

        /// <summary>The centre of the element's bounding box, or null when it has none.</summary>
        public static XYZ Center(Element e)
        {
            var box = e == null ? null : e.get_BoundingBox(null);
            return box == null ? null : (box.Min + box.Max) * 0.5;
        }

        /// <summary>
        /// A marker: a DirectShape with LiveClash's ApplicationId and "marker/N" as its data id, AND a stored record. For
        /// "marker/1" the record is format 1; for any other number it is a record in a schema this build does not know (a newer
        /// format, passed over). The parameter text is never consulted.
        /// </summary>
        public static bool IsMarker(Element e)
        {
            var ds = e as DirectShape;
            if (ds == null || ds.ApplicationId != MarkerFormats.ApplicationId) return false;
            var data = ds.ApplicationDataId ?? "";
            if (!data.StartsWith("marker/", StringComparison.Ordinal)) return false;
            try
            {
                if (data == MarkerFormats.ApplicationDataId) return MarkerStorage.HasRecord(ds);
                return ds.GetEntitySchemaGuids().Count > 0;
            }
            catch { return false; }
        }

        private static Guid ClashIdGuid()
        {
            return MarkerParameterMap.Definitions.First(d => d.Name == "LC Clash Id").ParameterGuid;
        }

        /// <summary>Specialty Equipment that is not a marker but has "LC-" in its Clash Id parameter (a user's own element, T11): named in the summary.</summary>
        public static int CountLookalikes(Document doc)
        {
            var guid = ClashIdGuid();
            int n = 0;
            foreach (var e in new FilteredElementCollector(doc).OfCategory(MarkerCategory.Built).WhereElementIsNotElementType())
            {
                if (IsMarker(e)) continue;
                var p = e.get_Parameter(guid);
                var text = p == null ? null : p.AsString();
                if (text != null && text.StartsWith("LC-", StringComparison.Ordinal)) n++;
            }
            return n;
        }

        /// <summary>Every marker in the model (every one, duplicates included), read, in the order the collector gives them. Markers on a closed workset are not returned (P17).</summary>
        public static List<MarkerFound> Collect(Document doc)
        {
            var found = new List<MarkerFound>();
            foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).OfCategory(MarkerCategory.Built))
            {
                var ds = e as DirectShape;
                if (ds == null || !IsMarker(ds)) continue;
                int workset = -1;
                try { workset = ds.WorksetId.IntegerValue; } catch { }
                found.Add(new MarkerFound
                {
                    Element = ds, Id = ds.Id, Uid = ds.UniqueId, Read = MarkerStorage.Read(ds),
                    Grouped = ds.GroupId != null && ds.GroupId != ElementId.InvalidElementId,
                    Center = Center(ds), Workset = workset
                });
            }
            return found;
        }
    }
}
