using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    public sealed class ScheduleMenuItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public bool Enabled { get; set; }
        public string Tooltip { get; set; }
        public bool SeparatorBefore { get; set; }
    }

    public sealed class ScheduleMenu
    {
        public string ButtonText { get; set; } = "";
        public string ButtonTooltip { get; set; } = "";
        public List<ScheduleMenuItem> Items { get; set; } = new List<ScheduleMenuItem>();

        public ScheduleMenuItem Item(string id)
        {
            foreach (var i in Items) if (i.Id == id) return i;
            return null;
        }
    }

    /// <summary>The "Clash schedule" split button and its menu, from the snapshot (spec 9). Pure.</summary>
    public static class ScheduleMenuModel
    {
        public const string Refresh = "refresh";
        public const string Open = "open";
        public const string Focus = "focus";
        public const string RemoveResolved = "removeResolved";
        public const string RemoveAll = "removeAll";
        public const string RemoveParameters = "removeParameters";

        private const string ButtonName = "Clash schedule";

        private static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        private static ScheduleMenuItem Item(string id, string text, bool enabled, string tooltip, bool separator = false)
        {
            return new ScheduleMenuItem { Id = id, Text = text, Enabled = enabled, Tooltip = tooltip ?? "", SeparatorBefore = separator };
        }

        public static ScheduleMenu Build(ScheduleSnapshot s)
        {
            var menu = new ScheduleMenu { ButtonTooltip = "Put every clash in the model as a marker that a Revit schedule lists, shared with the team" };
            if (s == null)
            {
                const string off = "The clash schedule is not available yet.";
                menu.ButtonText = ButtonName;
                menu.Items.Add(Item(Refresh, "Create markers", false, off));
                menu.Items.Add(Item(Open, "Open the schedule", false, off));
                menu.Items.Add(Item(Focus, "Show markers in Focus view", false, off));
                menu.Items.Add(Item(RemoveResolved, "Remove resolved", false, off, true));
                menu.Items.Add(Item(RemoveAll, "Remove all markers...", false, off));
                return menu;
            }

            int todo = Math.Max(0, s.OutOfDate);
            int resolved = Math.Max(0, s.ResolvedCount);
            bool has = s.HasMarkers;
            string why = s.Refused ? s.RefusalReason : null;

            menu.ButtonText = has && todo > 0 ? ButtonName + " (" + N(todo) + ")" : ButtonName;

            string refresh = !has ? "Create markers" : todo > 0 ? "Refresh markers (" + N(todo) + " to do)" : "Refresh markers";
            menu.Items.Add(Item(Refresh, refresh, !s.Refused, why));

            string open = s.HasSchedule && has ? "Open the schedule (" + N(s.MarkerCount) + ")" : "Open the schedule";
            string openWhy = s.HasSchedule ? null : has ? "The schedule is missing. Refresh makes it again." : "There is no schedule yet. Create markers first.";
            menu.Items.Add(Item(Open, open, s.HasSchedule, openWhy));

            menu.Items.Add(Item(Focus, "Show markers in Focus view", has && !s.Refused, why ?? (has ? null : "There are no markers yet.")));
            menu.Items.Add(Item(RemoveResolved, resolved > 0 ? "Remove resolved (" + N(resolved) + ")" : "Remove resolved",
                has && resolved > 0 && !s.Refused, why ?? (resolved > 0 ? null : "Nothing is resolved yet."), true));
            menu.Items.Add(Item(RemoveAll, "Remove all markers...", has && !s.Refused, why ?? (has ? null : "There are no markers to remove.")));
            if (s.ParametersPresent && !has) menu.Items.Add(Item(RemoveParameters, "Remove LiveClash parameters", !s.Refused, why));
            return menu;
        }
    }
}
