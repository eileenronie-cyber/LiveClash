using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Clicks in a dockable panel arrive outside Revit's API context, so anything
    /// touching the UIDocument has to be marshalled back in through an ExternalEvent.
    ///
    /// This SELECTS the clashing elements and does nothing else. It deliberately does
    /// not call ShowElements and does not zoom: both move the camera, and ShowElements
    /// will happily switch you to a different view. The user drives the view — with
    /// the elements selected, Selection Box (BX) frames the clash in a 3D view on
    /// demand, which is both faster and less disorienting than being teleported.
    ///
    /// Both sides of the pair are selected when both live in the host document, so a
    /// Selection Box wraps the whole clash rather than one half of it. When the other
    /// element sits in a link it cannot be selected from the host, so only the host
    /// element is.
    /// </summary>
    public class ShowElementHandler : IExternalEventHandler
    {
        /// <summary>
        /// Every host-document element to select. For a grouped row this is the whole
        /// issue — all the pipes and everything they hit — so one Selection Box frames
        /// the problem rather than a single element of it.
        /// </summary>
        public List<ElementId> TargetIds { get; set; }

        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                if (doc == null || !doc.IsValidObject) return;

                var wanted = TargetIds;
                if (wanted == null || wanted.Count == 0) return;

                var ids = new List<ElementId>();
                foreach (var id in wanted) AddIfLive(doc, id, ids);

                if (ids.Count == 0) return;

                uidoc.Selection.SetElementIds(ids);
            }
            catch (System.Exception ex)
            {
                Log.Warn("ShowElementHandler failed", ex);
            }
        }

        private static void AddIfLive(Document doc, ElementId id, List<ElementId> sink)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            try
            {
                var el = doc.GetElement(id);
                if (el != null && el.IsValidObject) sink.Add(id);
            }
            catch { }
        }

        public string GetName() { return "LiveClash.ShowElement"; }
    }
}
