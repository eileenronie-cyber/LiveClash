namespace LiveClash.Core
{
    /// <summary>
    /// How every CSV this tool writes quotes a field. One rule, in one place, and Revit-free, so the
    /// headless gate proves it rather than only ever seeing it through an export.
    ///
    /// Moved here unchanged from CsvExport, whose private Q now calls this.
    /// </summary>
    public static class CsvText
    {
        /// <summary>
        /// RFC 4180 quoting. Family names here contain commas, quotes and the odd
        /// newline, and one unescaped quote shifts every later column on the row.
        /// </summary>
        public static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

            // A field starting =, +, - or @ is a FORMULA to Excel, and this register is
            // written to be opened in Excel at a coordination meeting. A type legitimately
            // named "-100mm DROP" would be evaluated, and a name someone chose to be
            // hostile would be evaluated too. A leading apostrophe is Excel's own escape:
            // it forces text and is not shown in the cell.
            if ("=+-@".IndexOf(s[0]) >= 0) s = "'" + s;

            if (s.IndexOf('"') < 0 && s.IndexOf(',') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
