using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Decides whether two intersecting elements are actually a problem.
    /// Without this you drown: every duct touches its own fitting, every pipe
    /// touches its tee, every sprinkler touches the branch it hangs off.
    /// Only applies within a single document — link geometry can't be joined
    /// or connected to host geometry, so cross-document pairs are always real.
    /// </summary>
    public static class PairSuppressor
    {
        public static bool ShouldSuppress(Document doc, Element a, Element b)
        {
            if (a == null || b == null) return true;
            if (a.Id == b.Id) return true;

            if (!ClashSettings.Current.SuppressConnectedPairs) return false;

            // NO HOSTED-PAIR SUPPRESSION. Do not reintroduce it.
            //
            // Equipment recessed or face-hosted into an architectural or structural
            // wall MUST be reported. A penetration through someone else's wall is
            // precisely what the architect and engineer need to agree; suppressing it
            // means it reaches site undiscussed. The same filter was built into
            // Federated Clash on 2026-08-14, called out as backwards, and reverted --
            // then reappeared here. Removed 2026-08-23.
            //
            // Under-reporting is the dangerous direction for a clash tool, because a
            // clean run looks like good news.
            if (ShareSuperComponent(a, b)) return true;
            if (AreJoined(doc, a, b)) return true;
            if (AreConnected(a, b)) return true;

            return false;
        }

        /// <summary>Nested family parts of the same assembly.</summary>
        private static bool ShareSuperComponent(Element a, Element b)
        {
            var fa = a as FamilyInstance;
            var fb = b as FamilyInstance;
            try
            {
                var sa = fa != null ? fa.SuperComponent : null;
                var sb = fb != null ? fb.SuperComponent : null;

                if (sa != null && sa.Id == b.Id) return true;
                if (sb != null && sb.Id == a.Id) return true;
                if (sa != null && sb != null && sa.Id == sb.Id) return true;
            }
            catch { }
            return false;
        }

        private static bool AreJoined(Document doc, Element a, Element b)
        {
            try { return JoinGeometryUtils.AreElementsJoined(doc, a, b); }
            catch { return false; }
        }

        /// <summary>Physically connected MEP: duct to fitting, pipe to tee, and so on.</summary>
        private static bool AreConnected(Element a, Element b)
        {
            var mgr = GetConnectorManager(a);
            if (mgr == null) return false;

            try
            {
                foreach (Connector c in mgr.Connectors)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r.Owner != null && r.Owner.Id == b.Id) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static ConnectorManager GetConnectorManager(Element e)
        {
            try
            {
                var curve = e as MEPCurve;              // duct, pipe, cable tray, conduit
                if (curve != null) return curve.ConnectorManager;

                var fi = e as FamilyInstance;           // fittings, equipment, accessories
                if (fi != null && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The MEP centre-line categories -- OST_PipeCurvesCenterLine and its twelve
        /// siblings -- collected once from the enum rather than hand-listed, so a
        /// discipline nobody thought of is covered too.
        ///
        /// These are the drawn centre line of a pipe or duct, not the pipe. There are
        /// 16,999 of them in PRJ01 and not one carries solid geometry; Revit throws
        /// "the category of the element is not supported for element intersection
        /// filters" for every one. Left in, they behave exactly like the Model Groups
        /// did: unanswerable, so every candidate in the bounding box is reported
        /// Unverified. 35 rows in the register came from four of them.
        /// </summary>
        private static readonly HashSet<int> CentreLineCategories = BuildCentreLineCategories();

        private static HashSet<int> BuildCentreLineCategories()
        {
            var set = new HashSet<int>();
            foreach (var name in Enum.GetNames(typeof(BuiltInCategory)))
            {
                if (!name.EndsWith("CenterLine", StringComparison.Ordinal)) continue;
                try { set.Add((int)(BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), name)); }
                catch { }
            }
            return set;
        }

        /// <summary>
        /// Elements that should never raise a clash at all, regardless of pairing.
        /// </summary>
        public static bool IsIgnorableElement(Element e)
        {
            // A null or invalid element cannot take part in a clash, so it IS
            // ignorable. This read "false" — "go ahead and use it" — which was only
            // harmless because every caller happens to null-check first.
            if (e == null || !e.IsValidObject) return true;

            // A Group is a container, not geometry, and it cannot clash with anything.
            //
            // It has no solids of its own (0 of 20 model groups in PRJ01 yield any),
            // its bounding box spans every member it holds (up to 13,935 m3 here), and
            // Revit refuses it outright: ElementIntersectsElementFilter throws "The
            // category of the element is not supported for element intersection
            // filters" for all 20. The engine's never-silently-drop rule then did the
            // honest thing with an unanswerable question and marked every candidate
            // inside that enormous box Unverified -- ONE group, 75959499, produced 185
            // rows, 57% of the whole register.
            //
            // Skipping it loses no coverage. Group members are ordinary elements, are
            // collected by the same document query, and are checked individually; the
            // group could only ever duplicate them. Detail groups are already caught by
            // the OwnerViewId test below, but this covers them without depending on it.
            if (e is Group) return true;

            // The drawn centre line of a pipe or duct is not the pipe. See
            // CentreLineCategories -- no solids, and Revit refuses them for
            // intersection filtering, so they can only ever produce Unverified noise.
            {
                var cl = GetBuiltInCategory(e);
                if (cl != BuiltInCategory.INVALID && CentreLineCategories.Contains((int)cl))
                    return true;
            }

            // Insulation and lining are modelled as separate elements and would
            // otherwise clash with the very duct/pipe they wrap.
            var bic = GetBuiltInCategory(e);
            switch (bic)
            {
                case BuiltInCategory.OST_DuctInsulations:
                case BuiltInCategory.OST_DuctLinings:
                case BuiltInCategory.OST_PipeInsulations:
                case BuiltInCategory.OST_RoomSeparationLines:
                case BuiltInCategory.OST_Lines:
                case BuiltInCategory.OST_Rooms:
                case BuiltInCategory.OST_MEPSpaces:
                case BuiltInCategory.OST_Mass:
                // Areas and HVAC zones are the same kind of thing as a room: a solid
                // the size of a floorplate that nobody can ever bump into. Absent from
                // PRJ01, but this list is the only thing standing between the next
                // project and every element in a zone clashing with the zone.
                case BuiltInCategory.OST_Areas:
                case BuiltInCategory.OST_HVAC_Zones:
                    return true;
            }

            // View-specific things — detail items, drafting, and views themselves
            // (a Sheet is a View) — exist only on a drawing. They report a bounding
            // box, so nothing else here catches them, but nothing in 3D can collide
            // with a detail component. Left in, they cost real narrow-phase time on
            // geometry that cannot clash: 4,611 Detail Items and 532 Sheets on this
            // model alone.
            try
            {
                if (e is View) return true;
                if (e.OwnerViewId != null && e.OwnerViewId != ElementId.InvalidElementId) return true;
            }
            catch { }

            // Elements inside a design option that isn't primary would report
            // clashes against geometry that will never be built together.
            try
            {
                var opt = e.DesignOption;
                if (opt != null && !opt.IsPrimary) return true;
            }
            catch { }

            return false;
        }

        public static BuiltInCategory GetBuiltInCategory(Element e)
        {
            try
            {
                if (e.Category == null) return BuiltInCategory.INVALID;
#if R24_PLUS
                return e.Category.BuiltInCategory;
#else
                return (BuiltInCategory)e.Category.Id.IntegerValue;
#endif
            }
            catch { return BuiltInCategory.INVALID; }
        }

        public static string CategoryName(Element e)
        {
            try { return e != null && e.Category != null ? e.Category.Name : "(no category)"; }
            catch { return "(no category)"; }
        }

        public static string ElementLabel(Element e)
        {
            if (e == null) return "(missing)";
            try
            {
                string name = e.Name;
                return string.IsNullOrEmpty(name)
                    ? "Id " + IdUtil.Value(e.Id)
                    : name + " [" + IdUtil.Value(e.Id) + "]";
            }
            catch { return "Id " + IdUtil.Value(e.Id); }
        }
    }
}
