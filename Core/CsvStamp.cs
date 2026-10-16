using System;
using System.Globalization;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>
    /// Where an exported CSV came from: which model, when, who, and under which rules.
    ///
    /// WHY. A CSV that leaves the machine - dropped in an ACC folder, attached to a meeting invite,
    /// pasted next to a colleague's - no longer says anything about itself. Under the one-seat-per-
    /// model setup this tool is built for, the MEP modeller's register and the Architecture
    /// modeller's register are both "the clash list", each from its own side, and nothing in the
    /// rows told them apart. The rules fingerprint says whether two lists are even comparable: two
    /// seats that disagree about a clash while running different rules are not disagreeing.
    ///
    /// WHY COLUMNS, NOT A PREAMBLE. Lines above the header break what people do with a CSV in
    /// Excel: sort, filter, paste two files under one header. Four extra columns repeated on every
    /// row survive all three, so a row copied out of the file still says where it came from.
    ///
    /// Pure and Revit-free: the values are gathered by CsvExport.StampNow, and everything here is
    /// proven by the headless gate (Testing\CsvStampChecks.cs).
    /// </summary>
    public sealed class CsvStamp
    {
        /// <summary>The model the export was made from (not the model a row's other side is in).</summary>
        public string Model;

        /// <summary>Local time of the export.</summary>
        public DateTime Exported;

        /// <summary>Who exported it - the Revit username, the same name the ledgers record.</summary>
        public string By;

        /// <summary><see cref="RulesFingerprint"/> of the settings the rows were found under.</summary>
        public string Rules;

        /// <summary>The added column names, in order.</summary>
        public static readonly string[] Columns = { "Exported from", "Exported", "Exported by", "Rules" };

        /// <summary>"yyyy-MM-dd HH:mm" whatever the machine's culture, so it sorts and reads the same on every seat.</summary>
        public string ExportedText
        {
            get { return Exported == default(DateTime) ? "" : Exported.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        }

        /// <summary>The header cells to append to an existing header line, starting with the separating comma.</summary>
        public static string HeaderCells()
        {
            var sb = new StringBuilder();
            foreach (var c in Columns) sb.Append(',').Append(c);
            return sb.ToString();
        }

        /// <summary>This stamp's cells to append to a row, starting with the separating comma, quoted as every other field is.</summary>
        public string RowCells()
        {
            return "," + CsvText.Q(Model) + "," + CsvText.Q(ExportedText) + "," + CsvText.Q(By) + "," + CsvText.Q(Rules);
        }

        /// <summary>
        /// The default file name: "{model}-{kind}-{yyyyMMdd-HHmm}.csv". The model comes first so a folder of
        /// several seats' exports sorts by model; characters Windows refuses in a file name become "_".
        /// </summary>
        public static string FileName(string kind, string model, DateTime when)
        {
            var m = Safe(string.IsNullOrWhiteSpace(model) ? "LiveClash" : model.Trim());
            var k = Safe(string.IsNullOrWhiteSpace(kind) ? "export" : kind.Trim());
            return m + "-" + k + "-" + when.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".csv";
        }

        private static string Safe(string s)
        {
            // Fixed list rather than Path.GetInvalidFileNameChars(): that one differs by platform, and the
            // headless gate runs where Windows' rules would not otherwise be checked.
            const string bad = "<>:\"/\\|?*";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(c < 32 || bad.IndexOf(c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
