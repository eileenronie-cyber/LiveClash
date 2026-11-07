using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The "LiveClash" workset the markers live on (spec 3, 4b.2, 8.6): one workset for all sets, hidden by default so one switch hides
    /// every marker, shown in a LiveClash user's own Focus view. A model that is not workshared has no workset (the id is -1).
    /// </summary>
    [RevitBound]
    public static class MarkerWorkset
    {
        public const string Name = "LiveClash";

        private static Workset FindWorkset(Document doc)
        {
            if (doc == null || !doc.IsWorkshared) return null;
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).FirstOrDefault(w => w.Name == Name);
        }

        /// <summary>The workset's id value, or -1 when the model is not workshared or has no such workset yet.</summary>
        public static int Find(Document doc)
        {
            var ws = FindWorkset(doc);
            return ws == null ? -1 : ws.Id.IntegerValue;
        }

        /// <summary>True when the workset exists but is closed: the collector then returns no markers and a Refresh would make a second set (R-P17, spec 7).</summary>
        public static bool IsClosed(Document doc)
        {
            var ws = FindWorkset(doc);
            return ws != null && !ws.IsOpen;
        }

        /// <summary>The workset's id, made (and hidden by default) when missing; -1 in a model that is not workshared. Needs an open transaction.</summary>
        public static int Ensure(Document doc)
        {
            if (doc == null || !doc.IsWorkshared) return -1;
            var ws = FindWorkset(doc);
            if (ws == null)
            {
                ws = Workset.Create(doc, Name);
                WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc).SetWorksetVisibility(ws.Id, false);
            }
            return ws.Id.IntegerValue;
        }

        /// <summary>Shows the workset in this view (a LiveClash user's Focus view, so Highlight in Model has somewhere to land, P18). Not wired yet: the Focus view belongs to FocusHandler.</summary>
        public static void ShowInView(Document doc, View view)
        {
            var id = Find(doc);
            if (id < 0 || view == null) return;
            view.SetWorksetVisibility(new WorksetId(id), WorksetVisibility.Visible);
        }
    }
}
