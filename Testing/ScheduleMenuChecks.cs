using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B1: the "Clash schedule" split button and its menu (spec 9). Revit-free.
    /// Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class ScheduleMenuChecks
    {
        private const string P = "schedule menu: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Empty(check);
            WithMarkers(check);
            Removals(check);
            Refusals(check);
            Odd(check);
        }

        internal static ScheduleSnapshot Markers(int count = 140, int outOfDate = 12, int resolved = 8, bool schedule = true)
        {
            return new ScheduleSnapshot { Me = "jdoe", Workshared = true, MarkerCount = count, OutOfDate = outOfDate, ResolvedCount = resolved, HasSchedule = schedule, ParametersPresent = true };
        }

        private static bool Disabled(ScheduleMenu m, string id) { var i = m.Item(id); return i != null && !i.Enabled; }
        private static bool Enabled(ScheduleMenu m, string id) { var i = m.Item(id); return i != null && i.Enabled; }

        private static void Empty(Action<string, bool?> check)
        {
            var none = new ScheduleSnapshot { Me = "jdoe", Workshared = true };
            var m = ScheduleMenuModel.Build(none);
            check(P + "no markers: refresh reads \"Create markers\" and is enabled", m.Item("refresh") != null && m.Item("refresh").Text == "Create markers" && m.Item("refresh").Enabled);
            check(P + "no markers: the button is \"Clash schedule\" with no chip", m.ButtonText == "Clash schedule");
            check(P + "no markers: open, focus, remove resolved and remove all exist and are disabled",
                Disabled(m, "open") && Disabled(m, "focus") && Disabled(m, "removeResolved") && Disabled(m, "removeAll"));

            var nul = ScheduleMenuModel.Build(null);
            bool anyOn = nul.Items.Any(i => i.Enabled);
            check(P + "a null snapshot: no throw, \"Create markers\" shown, nothing enabled", nul.Item("refresh") != null && nul.Item("refresh").Text == "Create markers" && !anyOn && nul.ButtonText == "Clash schedule");

            var p = ScheduleMenuModel.Build(new ScheduleSnapshot { ParametersPresent = true });
            check(P + "remove parameters is offered when the parameters exist and no marker is left", p.Item("removeParameters") != null && p.Item("removeParameters").Enabled && p.Item("removeParameters").Text == "Remove LiveClash parameters");
            var q = ScheduleMenuModel.Build(Markers());
            check(P + "...and not while a marker is left", q.Item("removeParameters") == null);
            var r = ScheduleMenuModel.Build(new ScheduleSnapshot { ParametersPresent = false });
            check(P + "...and not when there are no parameters", r.Item("removeParameters") == null && r.Items.Count == 5);
        }

        private static void WithMarkers(Action<string, bool?> check)
        {
            var m = ScheduleMenuModel.Build(Markers());
            check(P + "140 markers, 12 out of date: \"Refresh markers (12 to do)\" and the chip \"Clash schedule (12)\"",
                m.Item("refresh") != null && m.Item("refresh").Text == "Refresh markers (12 to do)" && m.ButtonText == "Clash schedule (12)");
            var z = ScheduleMenuModel.Build(Markers(outOfDate: 0));
            check(P + "nothing out of date: \"Refresh markers\" and no chip", z.Item("refresh") != null && z.Item("refresh").Text == "Refresh markers" && z.ButtonText == "Clash schedule");
            check(P + "open reads \"Open the schedule (140)\"", m.Item("open") != null && m.Item("open").Text == "Open the schedule (140)" && m.Item("open").Enabled);
            var big = ScheduleMenuModel.Build(Markers(count: 1240, outOfDate: 1500));
            check(P + "thousands get a separator: \"(1,240)\" and \"(1,500 to do)\"", big.Item("open") != null && big.Item("open").Text == "Open the schedule (1,240)" && big.Item("refresh").Text == "Refresh markers (1,500 to do)");

            var cult = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var de = ScheduleMenuModel.Build(Markers(count: 1240));
                check(P + "the separator does not follow the machine's culture", de.Item("open") != null && de.Item("open").Text == "Open the schedule (1,240)");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = cult; }

            var gone = ScheduleMenuModel.Build(Markers(schedule: false));
            check(P + "markers but no schedule: open is disabled and its tooltip says Refresh makes it again",
                Disabled(gone, "open") && (gone.Item("open").Tooltip ?? "").Contains("Refresh") && gone.Item("open").Text == "Open the schedule");
            check(P + "focus is enabled only with markers", Enabled(m, "focus") && m.Item("focus").Text == "Show markers in Focus view" && Disabled(ScheduleMenuModel.Build(new ScheduleSnapshot()), "focus"));

            var order = m.Items.Select(i => i.Id).ToList();
            check(P + "the order is refresh, open, focus, remove resolved, remove all",
                string.Join(",", order) == "refresh,open,focus,removeResolved,removeAll");
            var all = ScheduleMenuModel.Build(new ScheduleSnapshot { ParametersPresent = true });
            check(P + "...with remove parameters last when it is offered, and only remove resolved has a separator before it",
                string.Join(",", all.Items.Select(i => i.Id)) == "refresh,open,focus,removeResolved,removeAll,removeParameters"
                && all.Items.Count(i => i.SeparatorBefore) == 1 && all.Item("removeResolved") != null && all.Item("removeResolved").SeparatorBefore);
            var again = ScheduleMenuModel.Build(Markers());
            check(P + "the same snapshot gives the same menu twice",
                again.ButtonText == m.ButtonText && string.Join("|", again.Items.Select(i => i.Id + i.Text + i.Enabled + i.Tooltip)) == string.Join("|", m.Items.Select(i => i.Id + i.Text + i.Enabled + i.Tooltip)));
        }

        private static void Removals(Action<string, bool?> check)
        {
            var m = ScheduleMenuModel.Build(Markers());
            check(P + "\"Remove resolved (8)\" is enabled", m.Item("removeResolved") != null && m.Item("removeResolved").Text == "Remove resolved (8)" && m.Item("removeResolved").Enabled);
            var none = ScheduleMenuModel.Build(Markers(resolved: 0));
            check(P + "with none resolved it reads \"Remove resolved\" and is disabled", none.Item("removeResolved") != null && none.Item("removeResolved").Text == "Remove resolved" && !none.Item("removeResolved").Enabled);
            check(P + "\"Remove all markers...\" is enabled with markers", m.Item("removeAll") != null && m.Item("removeAll").Text == "Remove all markers..." && m.Item("removeAll").Enabled);
        }

        private static void Refusals(Action<string, bool?> check)
        {
            var s = Markers();
            s.RefusalReason = "This model is read-only.";
            var m = ScheduleMenuModel.Build(s);
            bool allOff = Disabled(m, "refresh") && Disabled(m, "focus") && Disabled(m, "removeResolved") && Disabled(m, "removeAll");
            bool tips = m.Item("refresh") != null && m.Item("refresh").Tooltip == "This model is read-only." && m.Item("removeAll") != null && m.Item("removeAll").Tooltip == "This model is read-only.";
            check(P + "a refusal disables refresh, focus and the removals, and the reason is their tooltip", allOff && tips);
            check(P + "...but the schedule can still be opened", Enabled(m, "open"));
            var p = ScheduleMenuModel.Build(new ScheduleSnapshot { ParametersPresent = true, RefusalReason = "The LiveClash workset is closed." });
            check(P + "...and a refusal disables remove parameters too", Disabled(p, "removeParameters"));
        }

        private static void Odd(Action<string, bool?> check)
        {
            var neg = ScheduleMenuModel.Build(Markers(outOfDate: -3));
            check(P + "a negative out-of-date count shows no chip", neg.ButtonText == "Clash schedule" && neg.Item("refresh") != null && neg.Item("refresh").Text == "Refresh markers");

            var rnd = new Random(20261031);
            bool ok = true, ids = true, texts = true, button = true, refuse = true;
            for (int i = 0; i < 400; i++)
            {
                var s = new ScheduleSnapshot
                {
                    Me = rnd.Next(3) == 0 ? null : "jdoe",
                    Workshared = rnd.Next(2) == 0,
                    MarkerCount = rnd.Next(4) == 0 ? 0 : rnd.Next(0, 9000),
                    OutOfDate = rnd.Next(-5, 9000),
                    ResolvedCount = rnd.Next(-2, 100),
                    HasSchedule = rnd.Next(2) == 0,
                    ParametersPresent = rnd.Next(2) == 0,
                    RefusalReason = rnd.Next(3) == 0 ? "No." : (rnd.Next(5) == 0 ? "  " : null)
                };
                try
                {
                    var m = ScheduleMenuModel.Build(s);
                    ids &= m.Items.Select(x => x.Id).Distinct().Count() == m.Items.Count && m.Items.Count >= 5;
                    texts &= m.Items.All(x => !string.IsNullOrEmpty(x.Text) && !x.Text.Contains('{') && !x.Text.Contains('}') && !x.Text.Contains("null"));
                    button &= m.ButtonText != null && m.ButtonText.StartsWith("Clash schedule", StringComparison.Ordinal);
                    if (s.Refused) refuse &= m.Item("refresh") != null && !m.Item("refresh").Enabled;
                }
                catch (Exception) { ok = false; }
            }
            check(P + "fuzz (400 snapshots): never throws", ok);
            check(P + "fuzz: ids are unique and there are at least five items", ids);
            check(P + "fuzz: no item text is empty, null or holds a stray brace", texts);
            check(P + "fuzz: the button always starts \"Clash schedule\"", button);
            check(P + "fuzz: a refusal always disables refresh", refuse);
        }
    }
}
