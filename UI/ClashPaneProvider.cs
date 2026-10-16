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

        private static ClashPanel _panel;

        /// <summary>
        /// The pane's content. Created once, lives for the whole Revit session —
        /// Revit keeps a reference to it after RegisterDockablePane.
        /// </summary>
        public static ClashPanel Panel
        {
            get
            {
                if (_panel == null) _panel = new ClashPanel();
                return _panel;
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
