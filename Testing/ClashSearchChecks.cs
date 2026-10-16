using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The panel search's rule (Core\ClashSearch.cs): every word must be found in the row, in any order and any case;
    /// a quoted phrase is one word; blank keeps everything; it is a substring, never a pattern.
    /// Revit-free, so both tiers run it.
    /// </summary>
    public static class ClashSearchChecks
    {
        public static void RunAll(Action<string, bool?> check)
        {
            var row = new[] { "Pipes: Pipe Types - Chilled water 150 (412331)", "Walls: Basic Wall - Generic 200 (98122)", "M-ME-01", "Elec vs Mech 1", "agreed sleeve, see RFI 114" };

            check("search: a blank query keeps every row (empty, spaces, null)",
                ClashSearch.Matches("", row) && ClashSearch.Matches("   ", row) && ClashSearch.Matches(null, row)
                && !ClashSearch.IsActive("") && !ClashSearch.IsActive("  \t ") && !ClashSearch.IsActive(null) && ClashSearch.IsActive(" x "));
            check("search: a word found in any field keeps the row, in any capitals",
                ClashSearch.Matches("chilled", row) && ClashSearch.Matches("CHILLED", row) && ClashSearch.Matches("wall", row) && ClashSearch.Matches("m-me-01", row));
            check("search: a word found nowhere drops the row", !ClashSearch.Matches("sprinkler", row));
            check("search: several words must ALL be found (any order, in different fields)",
                ClashSearch.Matches("chilled wall", row) && ClashSearch.Matches("wall chilled", row) && ClashSearch.Matches("m-me-01 rfi", row)
                && !ClashSearch.Matches("chilled sprinkler", row));
            check("search: a quoted phrase is ONE word - the words side by side, not just both present",
                ClashSearch.Matches("\"chilled water\"", row) && !ClashSearch.Matches("\"water chilled\"", row)
                && ClashSearch.Matches("\"agreed sleeve\" rfi", row) && ClashSearch.Terms("\"chilled water\" 150").Count == 2);
            check("search: an unclosed quote counts as what was typed so far", ClashSearch.Terms("\"chilled wat").Count == 1 && ClashSearch.Matches("\"chilled wat", row));
            check("search: it is a substring, not a pattern - brackets, dots, stars and dashes mean what they say",
                ClashSearch.Matches("(412331)", row) && ClashSearch.Matches("m-me-01", row) && !ClashSearch.Matches("M.ME.01", row)
                && !ClashSearch.Matches("chil*", row) && !ClashSearch.Matches("[a-z]", row));
            check("search: an element id finds the row it is in", ClashSearch.Matches("412331", row) && !ClashSearch.Matches("412332", row));
            check("search: null and empty fields are ignored, and a row with no fields matches only a blank query",
                ClashSearch.Matches("pipe", new[] { null, "", "Pipe 1" }) && !ClashSearch.Matches("pipe", new string[0]) && !ClashSearch.Matches("pipe", null)
                && ClashSearch.Matches("", new string[0]) && ClashSearch.Matches("", null));
            check("search: extra spaces between words do not matter", ClashSearch.Matches("  chilled    wall  ", row) && ClashSearch.Terms("  a    b ").Count == 2);
            check("search: the same word twice is still just that word", ClashSearch.Matches("wall wall", row));
        }
    }
}
