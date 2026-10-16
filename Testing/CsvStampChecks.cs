using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The export stamp (Core\CsvStamp.cs) and the quoting every CSV shares (Core\CsvText.cs): each exported
    /// row says which model, when, who and under which rules, in columns that survive sorting, filtering and
    /// pasting two files under one header. Revit-free, so both tiers run it; ModalChecks proves the two real
    /// exports actually carry it.
    /// </summary>
    public static class CsvStampChecks
    {
        public static void RunAll(Action<string, bool?> check)
        {
            // ---- quoting, moved out of CsvExport unchanged: now asserted, not only relied on
            check("csv quote: a plain value is written as it is", CsvText.Q("Pipes") == "Pipes" && CsvText.Q("150 mm") == "150 mm");
            check("csv quote: null and empty are an empty field", CsvText.Q(null) == "" && CsvText.Q("") == "");
            check("csv quote: a comma is quoted, so it cannot split the field", CsvText.Q("Pipe, 150") == "\"Pipe, 150\"");
            check("csv quote: a quote is doubled inside a quoted field", CsvText.Q("12\" pipe") == "\"12\"\" pipe\"");
            check("csv quote: a newline becomes a space, so a row stays one line", CsvText.Q("a\r\nb\nc\rd") == "a b c d");
            check("csv quote: a leading = + - @ is forced to text, so Excel never evaluates it",
                CsvText.Q("=1+1") == "'=1+1" && CsvText.Q("+A1") == "'+A1" && CsvText.Q("-100mm DROP") == "'-100mm DROP" && CsvText.Q("@SUM") == "'@SUM");
            check("csv quote: the formula guard and the comma quoting combine", CsvText.Q("=a,b") == "\"'=a,b\"");

            // ---- the stamp's columns
            var header = CsvStamp.HeaderCells();
            check("csv stamp: the header adds exactly four named columns, in order (" + header + ")",
                header == ",Exported from,Exported,Exported by,Rules");

            var stamp = new CsvStamp { Model = "MEP-Tower A", Exported = new DateTime(2026, 9, 23, 14, 5, 0), By = "jtorres", Rules = "a1b2c3d4" };
            var cells = SplitRow("x" + stamp.RowCells());
            check("csv stamp: a row gets one cell per added column (" + (cells.Count - 1) + ")", cells.Count == 1 + CsvStamp.Columns.Length);
            check("csv stamp: ...holding the model, the time, the user and the rules, in that order",
                cells.Count == 5 && cells[1] == "MEP-Tower A" && cells[2] == "2026-09-23 14:05" && cells[3] == "jtorres" && cells[4] == "a1b2c3d4");

            var awkward = new CsvStamp { Model = "Tower A, \"Level 3\"", Exported = new DateTime(2026, 1, 2, 3, 4, 0), By = "=cmd", Rules = "" };
            var ac = SplitRow("x" + awkward.RowCells());
            check("csv stamp: a model name with commas and quotes stays in its own column",
                ac.Count == 5 && ac[1] == "Tower A, \"Level 3\"");
            check("csv stamp: a user name that looks like a formula is forced to text", ac.Count == 5 && ac[3] == "'=cmd");

            var blank = new CsvStamp();
            var bc = SplitRow("x" + blank.RowCells());
            check("csv stamp: a stamp with nothing known still writes four (empty) cells, never shifting the row",
                bc.Count == 5 && bc[1] == "" && bc[2] == "" && bc[3] == "" && bc[4] == "");

            // The time is the one field a machine setting could change. It must read the same on every seat.
            var was = Thread.CurrentThread.CurrentCulture;
            string underGerman, underArabic, underThai;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); underGerman = stamp.ExportedText;
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA"); underArabic = stamp.ExportedText;
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH"); underThai = stamp.ExportedText;
            }
            finally { Thread.CurrentThread.CurrentCulture = was; }
            check("csv stamp: the time reads yyyy-MM-dd HH:mm whatever the machine's culture, calendar included (" + underGerman + " | " + underArabic + " | " + underThai + ")",
                underGerman == "2026-09-23 14:05" && underArabic == "2026-09-23 14:05" && underThai == "2026-09-23 14:05");

            // ---- the default file name
            var when = new DateTime(2026, 9, 23, 14, 5, 0);
            check("csv stamp: the file name leads with the model, then the kind and the time (" + CsvStamp.FileName("clashes", "MEP", when) + ")",
                CsvStamp.FileName("clashes", "MEP", when) == "MEP-clashes-20260923-1405.csv");
            check("csv stamp: characters Windows refuses in a file name become _ (" + CsvStamp.FileName("clash-record", "A:B/C\\D*E?F\"G<H>I|J", when) + ")",
                CsvStamp.FileName("clash-record", "A:B/C\\D*E?F\"G<H>I|J", when) == "A_B_C_D_E_F_G_H_I_J-clash-record-20260923-1405.csv");
            check("csv stamp: no model name still gives a usable file name",
                CsvStamp.FileName("clashes", null, when) == "LiveClash-clashes-20260923-1405.csv"
                && CsvStamp.FileName("clashes", "   ", when) == "LiveClash-clashes-20260923-1405.csv");
            check("csv stamp: the collision guard's suffix survives into the name, so two same-titled models' files stay apart",
                CsvStamp.FileName("clashes", "MEP~3fa9c2d1", when) == "MEP~3fa9c2d1-clashes-20260923-1405.csv");
        }

        /// <summary>
        /// One CSV line split into its fields, RFC 4180 style (quoted fields, doubled quotes). Test-only: enough
        /// to prove a row keeps its columns, not a general reader.
        /// </summary>
        public static List<string> SplitRow(string line)
        {
            var fields = new List<string>();
            if (line == null) return fields;
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            fields.Add(cur.ToString());
            return fields;
        }
    }
}
