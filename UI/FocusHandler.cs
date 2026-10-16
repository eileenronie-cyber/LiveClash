using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Frames a clash in 3D with a SECTION BOX, so a pair you cannot Selection-Box —
    /// both sides in links, or a link element that BX would leave out — can still be
    /// viewed. A section box is a view property, not a selection, so it works whatever
    /// can be picked, and link geometry renders in the host 3D view so both sides show.
    ///
    /// It uses a dedicated "LiveClash — Clash Focus" 3D view rather than the one you are
    /// working in, so your model view's section box is never clobbered. Setting the box
    /// (and creating the view) is a model change, so this runs in a transaction — the
    /// panel click arrives outside API context, so it is marshalled here by an
    /// ExternalEvent, exactly like selection.
    /// </summary>
    public class FocusHandler : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }
        public List<ClashResult> Targets { get; set; }

        private const string ViewName = "LiveClash — Clash Focus";

        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc != null ? uidoc.Document : null;
                if (doc == null || !doc.IsValidObject) return;

                var targets = Targets;
                if (targets == null || targets.Count == 0) return;

                var box = ClashFocus.BuildFocusBox(doc, targets);
                if (box == null)
                {
                    if (Panel != null) Panel.SetStatus("Could not frame the clash — no readable geometry on either side.");
                    return;
                }

                View3D view = null;
                using (var t = new Transaction(doc, "LiveClash: focus clash"))
                {
                    t.Start();
                    view = EnsureFocusView(doc);
                    if (view != null)
                    {
                        view.IsSectionBoxActive = true;
                        view.SetSectionBox(box);
                    }
                    t.Commit();
                }

                if (view == null)
                {
                    if (Panel != null) Panel.SetStatus("Could not create the focus view — no 3D view type available.");
                    return;
                }

                // Activating a view is not a model change, so it happens after the commit.
                try { uidoc.ActiveView = view; } catch { }

                bool anyLink = SelectTargets(uidoc, doc, targets);
                ZoomToBox(uidoc, view, box);

                if (Panel != null)
                    Panel.SetStatus("Framed the clash in \"" + ViewName + "\" with a section box"
                        + (anyLink ? " (link geometry included)." : ".")
                        + " Both sides are selected; Selection Box (BX) works here too.");
            }
            catch (Exception ex)
            {
                Log.Warn("Focus clash failed", ex);
                if (Panel != null) Panel.SetStatus("Focus failed — see " + Log.Path);
            }
        }

        /// <summary>
        /// The dedicated focus view, created once and reused.
        ///
        /// IDENTIFYING IT BY NAME ALONE LEAKS VIEWS. The rename can fail — the name may
        /// already belong to another view or a template — and swallowing that left a view
        /// called "3D View 1", which the next lookup could not find, so every Focus click
        /// created ANOTHER permanent view inside a transaction. Repeatedly double-clicking
        /// clashes would quietly fill the model with them.
        ///
        /// So the id of the view we made is remembered for the session and checked first;
        /// the name lookup is only the fallback that finds it again in a later session. If
        /// the rename fails the view is still usable and still tracked — the name is a
        /// convenience, not the identity.
        /// </summary>
        private static readonly Dictionary<string, ElementId> _focusViews =
            new Dictionary<string, ElementId>();

        /// <summary>
        /// Forget a document's focus view. Called from OnDocumentClosing, so an entry
        /// cannot outlive the document whose ElementId it holds.
        ///
        /// Nothing misbehaved without this — EnsureFocusView revalidates the id against
        /// the document before trusting it, so a stale entry is dropped on first use.
        /// But every other id-keyed cache in the tool is dropped on close (see
        /// ClashEngine.InvalidateAll), and this was the one that was not; a cache with no
        /// lifetime is how the next id-keyed thing gets added without one either.
        /// </summary>
        public static void Forget(Document doc)
        {
            if (doc == null) return;
            try { _focusViews.Remove(ClashEngine.DocKey(doc)); }
            catch { }
        }

        private static View3D EnsureFocusView(Document doc)
        {
            var docKey = ClashEngine.DocKey(doc);

            // 1. The one we made earlier this session, if it is still alive.
            ElementId known;
            if (_focusViews.TryGetValue(docKey, out known))
            {
                try
                {
                    var v = doc.GetElement(known) as View3D;
                    if (v != null && v.IsValidObject && !v.IsTemplate) return v;
                }
                catch { }
                _focusViews.Remove(docKey);      // deleted by the user, or stale
            }

            // 2. One left by an earlier session, found by name.
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>())
                if (v != null && !v.IsTemplate && string.Equals(v.Name, ViewName, StringComparison.Ordinal))
                {
                    _focusViews[docKey] = v.Id;
                    return v;
                }

            // 3. Make one.
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) return null;

            var made = View3D.CreateIsometric(doc, vft.Id);
            if (made == null) return null;

            // Remember it BEFORE naming: if the rename throws, the view still exists and
            // must be reused rather than abandoned and re-created on the next click.
            _focusViews[docKey] = made.Id;
            try { made.Name = ViewName; }
            catch (Exception ex)
            {
                Log.Info("Focus view kept Revit's default name ('" + SafeName(made)
                         + "') — the name was rejected, but the view is tracked and reused. " + ex.Message);
            }
            return made;
        }

        private static string SafeName(View3D v)
        {
            try { return v.Name; } catch { return "?"; }
        }

        /// <summary>
        /// Select both sides so highlight and native Selection Box also cover the clash.
        /// Host elements select directly; a linked element selects through a link
        /// reference (which is what makes it pickable from the host at all). Falls back to
        /// element-id selection of the host side if references cannot be built.
        /// </summary>
        private static bool SelectTargets(UIDocument uidoc, Document doc, List<ClashResult> targets)
        {
            var refs = new List<Reference>();
            var hostIds = new List<ElementId>();
            var seen = new HashSet<string>();
            bool anyLink = false;

            foreach (var r in targets)
            {
                if (r == null) continue;
                AddHostRef(doc, r.HostElementId, refs, hostIds, seen);

                if (r.LinkInstanceId != null)
                {
                    if (AddLinkRef(doc, r.LinkInstanceId, r.OtherElementId, refs, seen)) anyLink = true;
                }
                else
                {
                    AddHostRef(doc, r.OtherElementId, refs, hostIds, seen);
                }
            }

            try { if (refs.Count > 0) { uidoc.Selection.SetReferences(refs); return anyLink; } } catch { }
            try { if (hostIds.Count > 0) uidoc.Selection.SetElementIds(hostIds); } catch { }
            return anyLink;
        }

        private static void AddHostRef(Document doc, ElementId id, List<Reference> refs,
                                       List<ElementId> ids, HashSet<string> seen)
        {
            try
            {
                if (id == null || id == ElementId.InvalidElementId) return;
                var el = doc.GetElement(id);
                if (el == null || !el.IsValidObject) return;
                if (!seen.Add("h" + IdUtil.Value(id))) return;
                ids.Add(id);
                var rf = new Reference(el);
                if (rf != null) refs.Add(rf);
            }
            catch { }
        }

        private static bool AddLinkRef(Document doc, ElementId linkInstanceId, ElementId linkedId,
                                       List<Reference> refs, HashSet<string> seen)
        {
            try
            {
                var link = doc.GetElement(linkInstanceId) as RevitLinkInstance;
                if (link == null) return false;
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null || linkedId == null || linkedId == ElementId.InvalidElementId) return false;
                var el = linkDoc.GetElement(linkedId);
                if (el == null || !el.IsValidObject) return false;
                if (!seen.Add("l" + IdUtil.Value(linkInstanceId) + ":" + IdUtil.Value(linkedId))) return true;

                var inLink = new Reference(el);
                if (inLink == null) return false;
                var inHost = inLink.CreateLinkReference(link);
                if (inHost != null) { refs.Add(inHost); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>Frame the box in the newly activated view.</summary>
        private static void ZoomToBox(UIDocument uidoc, View3D view, BoundingBoxXYZ box)
        {
            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                    if (uv.ViewId == view.Id)
                    {
                        uv.ZoomAndCenterRectangle(box.Min, box.Max);
                        return;
                    }
            }
            catch { }
        }

        public string GetName() { return "LiveClash.Focus"; }
    }
}
