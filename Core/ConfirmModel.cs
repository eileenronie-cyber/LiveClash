using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    public enum ConfirmKind { RemoveResolved, RemoveAll, RemoveParameters, CreateNonWorkshared }

    /// <summary>A confirmation that starts unticked (E9): what will happen, a box, and OK only once it is ticked. Pure.</summary>
    public sealed class ConfirmModel
    {
        private bool _valid = true;

        private static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        private const string HeldLine = "Markers held by another user are passed over.";
        private const string UndoLine = "One Undo. Your local record keeps every decision.";

        public ConfirmKind Kind { get; private set; }
        public string Title { get; private set; } = "";
        public List<string> Lines { get; private set; } = new List<string>();
        public string CheckText { get; private set; } = "";
        public string OkText { get; private set; } = "";
        public string CancelText { get; private set; } = "Cancel";
        public bool Ticked { get; set; }

        /// <summary>OK needs the box ticked, and something to do.</summary>
        public bool CanOk { get { return Ticked && _valid; } }

        /// <summary>The dialog's answer: yes only when OK was pressed with the box ticked.</summary>
        public bool Accepts(bool okPressed) { return okPressed && CanOk; }

        public static ConfirmModel RemoveResolved(int resolved)
        {
            var m = new ConfirmModel { Kind = ConfirmKind.RemoveResolved, Title = "Remove resolved markers", CheckText = "I understand these markers are deleted", OkText = "Remove" };
            if (resolved <= 0)
            {
                m._valid = false;
                m.Lines.Add("No markers are resolved.");
                return m;
            }
            m.Lines.Add(resolved == 1 ? "1 resolved marker will be deleted from the model." : N(resolved) + " resolved markers will be deleted from the model.");
            m.Lines.Add(HeldLine);
            m.Lines.Add(UndoLine);
            return m;
        }

        public static ConfirmModel RemoveAll(int markers)
        {
            var m = new ConfirmModel { Kind = ConfirmKind.RemoveAll, Title = "Remove all markers", CheckText = "I understand this deletes every clash marker", OkText = "Remove all" };
            if (markers <= 0)
            {
                m._valid = false;
                m.Lines.Add("There are no markers to remove.");
                return m;
            }
            const string rest = " the schedule, the marker type, the settings record and the counter records will be deleted.";
            m.Lines.Add(markers == 1 ? "The 1 marker," + rest : "All " + N(markers) + " markers," + rest);
            m.Lines.Add("The LiveClash parameters and workset stay. Remove the parameters separately if you no longer need them.");
            m.Lines.Add(HeldLine);
            m.Lines.Add(UndoLine);
            return m;
        }

        public static ConfirmModel RemoveParameters()
        {
            var m = new ConfirmModel { Kind = ConfirmKind.RemoveParameters, Title = "Remove LiveClash parameters", CheckText = "I understand the parameters are removed from the model", OkText = "Remove" };
            m.Lines.Add("No clash marker is left in the model.");
            m.Lines.Add("Removing the shared parameters deletes their values everywhere in this model, including in other schedules that use them.");
            m.Lines.Add("One Undo.");
            return m;
        }

        public static ConfirmModel CreateNonWorkshared()
        {
            var m = new ConfirmModel { Kind = ConfirmKind.CreateNonWorkshared, Title = "Create markers in a model without worksets", CheckText = "I understand markers show in views and on sheets", OkText = "Continue" };
            m.Lines.Add("This model is not workshared, so the markers cannot go on a hidden workset.");
            m.Lines.Add("They show in every view where Specialty Equipment shows, and they print.");
            return m;
        }
    }
}
