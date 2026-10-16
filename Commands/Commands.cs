using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;
using LiveClash.UI;

namespace LiveClash.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ShowPanelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var pane = data.Application.GetDockablePane(ClashPaneProvider.PaneId);
                pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Log.Error("ShowPanelCommand failed", ex);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ToggleLiveCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            // Through the panel, so the ribbon and the panel's own Live checkbox are one
            // behaviour: it sets the checkbox, persists the setting and reports it. This
            // used to flip ClashSettings.Enabled directly — no Save, no checkbox — so a
            // pause from the ribbon evaporated at the next start and the two controls
            // could show opposite things.
            var panel = ClashPaneProvider.Panel;
            bool on = !panel.LiveEnabled;
            panel.SetLiveEnabled(on);

            TaskDialog.Show("Live Clash", on
                ? "Live clash checking is ON."
                : "Live clash checking is PAUSED. Existing results are kept.");

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var s = ClashSettings.Current;

            var overriding = OverridingSetNames(s);

            var dlg = new TaskDialog("Live Clash — clearance")
            {
                MainInstruction = "Soft-clash clearance",
                MainContent =
                    "Hard clashes are always reported. A clearance adds a tolerance zone around "
                  + "what you model, so near-misses show up too.\n\n"
                  + "Note: the clearance test uses bounding boxes, not true offset solids, so it is "
                  + "conservative — it will flag some things that are actually clear.\n\n"
                  + "This is the MODEL DEFAULT — a clash set can override it from the Filter window."
                  + (overriding.Count > 0
                        ? " Currently overridden by: " + string.Join(", ", overriding.ToArray()) + "."
                        : "")
                  + "\n\nCurrent: " + FormatFeet(s.ClearanceFeet),
                AllowCancellation = true
            };

            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Off — hard clashes only");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "25 mm (1\")");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "50 mm (2\")");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "100 mm (4\")");

            double before = s.ClearanceFeet;

            switch (dlg.Show())
            {
                case TaskDialogResult.CommandLink1: s.ClearanceFeet = 0.0; break;
                case TaskDialogResult.CommandLink2: s.ClearanceFeet = MmToFeet(25); break;
                case TaskDialogResult.CommandLink3: s.ClearanceFeet = MmToFeet(50); break;
                case TaskDialogResult.CommandLink4: s.ClearanceFeet = MmToFeet(100); break;
                default: return Result.Cancelled;
            }

            s.Save();

            // A verdict below Hard depends on ClearanceFeet — PairVerifier only re-checks
            // the clearance test when s.ClearanceFeet > 0, so a listed Clearance-severity
            // row that survives a lowered setting reads as Resolved (or Unknown for a link
            // pair with no solids), never re-tested against what actually changed: the
            // NUMBER, not the model. Same class of "lies in the permanent record" the full
            // check's own RecordResolved comment warns about for a narrowed filter — a
            // changed clearance is the same kind of change and needs the same treatment
            // Filter/Audit already give it: drop the results rather than let a background
            // verify or full check file every clearance-severity row as fixed with nothing
            // in the model having moved.
            // Skip the clear only when EVERY set — not just the enabled ones named in
            // the dialog above — already carries its own override, since only then is
            // the model default provably inert everywhere a stale row could still read
            // it: PairVerifier.MaxClearanceForPair falls back to it even for a pair a
            // now-DISABLED set once found, which OverridingSetNames (enabled-only, for
            // the dialog text) does not account for. Otherwise stay on the safe side —
            // clearing everything on any change, never just the sets this dialog can
            // prove are affected, since proving that fully needs the same per-pair scope
            // match PairVerifier does, not a per-set flag check.
            if (s.ClearanceFeet != before && s.Sets.Any(set => !set.ClearanceFeet.HasValue))
            {
                ClashEngine.InvalidateAll();
                ClashResultStore.Instance.Clear();
                ClashPaneProvider.Panel.SetEmptyReason(ClashPanel.EmptyReason.ClearanceChanged);
            }

            ClashPaneProvider.Panel.SetStatus("Model clearance set to " + FormatFeet(s.ClearanceFeet)
                + (overriding.Count > 0
                    ? " — " + string.Join(", ", overriding.ToArray()) + " still use their own override."
                    : "")
                + " Run a full check to re-evaluate existing geometry.");
            return Result.Succeeded;
        }

        /// <summary>Names of enabled sets that will NOT be affected by a model-default
        /// clearance change, because they carry their own override — so the dialog and
        /// the status line can say so instead of implying every set just moved.</summary>
        private static List<string> OverridingSetNames(ClashSettings s)
        {
            var names = new List<string>();
            foreach (var set in s.Sets)
                if (set.Enabled && set.ClearanceFeet.HasValue) names.Add(set.Name);
            return names;
        }

        // UnitTypeId replaced DisplayUnitType in the Revit 2021 API, so every version
        // this project builds for has it. That range is 2024, 2025 and 2026 — what
        // ci/gate.ps1 compiles — not the "2022+" this comment used to claim. Older legs
        // still compile, but nothing tests them, and a comment promising a version the
        // gate does not build is how somebody else discovers the gap instead of us.
        private static double MmToFeet(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        private static string FormatFeet(double feet)
        {
            if (feet <= 0) return "off";
            double mm = UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
            return Math.Round(mm) + " mm";
        }
    }

    /// <summary>Opens the read-only "Clash at a glance" summary window.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class GlanceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var win = new LiveClash.UI.GlanceWindow();
                new System.Windows.Interop.WindowInteropHelper(win).Owner =
                    System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                win.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Log.Error("GlanceCommand failed", ex);
                return Result.Failed;
            }
        }
    }
}
