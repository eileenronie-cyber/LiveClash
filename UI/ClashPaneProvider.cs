using System;
using Autodesk.Revit.UI;

namespace LiveClash.UI
{
    public class ClashPaneProvider : IDockablePaneProvider
    {
        // Must be stable across sessions — Revit remembers pane docking state by id.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("b57e9d42-3a18-4c76-9f05-1d8b3e6c42aa"));

        public const string PaneTitle = "Live Clash";

        // The settings' holder of sentences said while nobody listened (L-4): Core/NoticeHolder.cs, read through ClashSettings.TakeUndeliveredNotices.
        static ClashPaneProvider()
        {
            ClashPanel.UndeliveredNotices = LiveClash.Core.ClashSettings.TakeUndeliveredNotices;
        }

        private static ClashPanel _panel;

        /// <summary>
        /// The pane's content. Created once, lives for the whole Revit session —
        /// Revit keeps a reference to it after RegisterDockablePane.
        /// </summary>
        public static ClashPanel Panel
        {
            get
            {
                if (_panel == null)
                {
                    _panel = new ClashPanel();
                    _panel.ListenForLife();    // the pane's panel listens for the session first (round 8, M-1)
                    AttachSchedule(_panel);    // then the clash schedule is attached and starts its first read
                }
                return _panel;
            }
        }

        /// <summary>
        /// Gives the panel the Revit side of the clash schedule (spec 9). A failure here must never keep the panel from opening, so it is logged
        /// and the panel stays as it was (the feature stays dark); the per-PC kill switch (spec 12.2) keeps it off. True when it was attached.
        /// </summary>
        internal static bool AttachSchedule(ClashPanel panel)
        {
            try
            {
                bool off = !LiveClash.Core.MarkerSwitch.IsOn;
                if (off)
                {
                    LiveClash.Core.Log.Info("Clash markers are switched off on this PC; the clash schedule is not attached to the panel.");
                    return false;
                }
                var actions = new ClashScheduleActions(panel);
                panel.ScheduleActions = actions;
                actions.Reload();
                return true;
            }
            catch (Exception ex)
            {
                LiveClash.Core.Log.Warn("The clash schedule could not be attached to the panel", ex);
                return false;
            }
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = Panel;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed,
                TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
            };
            data.VisibleByDefault = false;
        }
    }
}
