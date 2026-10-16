using System;
using System.Collections.Generic;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>
    /// The panel's search box: which rows a typed query keeps.
    ///
    /// The rule, in one sentence: every WORD you type has to appear somewhere in the row (in an element name, a
    /// category, the model, the set, the note or the status), in any order, in any capitals. "pump mech" keeps a row
    /// with a pump in a mechanical model; "chilled 150" keeps the one pipe that is both. A phrase in double quotes is
    /// one word ("chilled water" finds those two words side by side and nothing else).
    ///
    /// It is a plain substring match, not a pattern: "M-EL-01" and "Pipe (12)" mean exactly what they say.
    ///
    /// Pure, and Revit-free on purpose, so the rule is tested headless and the panel only has to say which strings
    /// belong to a row.
    /// </summary>
    public static class ClashSearch
    {
        /// <summary>Is there anything to filter by? Blank or only spaces means show everything.</summary>
        public static bool IsActive(string query)
        {
            return Terms(query).Count > 0;
        }

        /// <summary>The words of a query: split on whitespace, with a double-quoted phrase kept as one word.</summary>
        public static List<string> Terms(string query)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return terms;

            var word = new StringBuilder();
            bool inQuote = false;
            bool hadQuote = false;
            foreach (char c in query)
            {
                if (c == '"')
                {
                    inQuote = !inQuote;
                    hadQuote = true;
                    continue;
                }
                if (char.IsWhiteSpace(c) && !inQuote)
                {
                    Flush(terms, word, hadQuote);
                    hadQuote = false;
                    continue;
                }
                word.Append(c);
            }
            Flush(terms, word, hadQuote);   // an unclosed quote still counts as what was typed so far
            return terms;
        }

        private static void Flush(List<string> terms, StringBuilder word, bool hadQuote)
        {
            var w = hadQuote ? word.ToString() : word.ToString().Trim();
            word.Length = 0;
            if (w.Trim().Length > 0) terms.Add(w);
        }

        /// <summary>
        /// Does a row made of these <paramref name="fields"/> survive <paramref name="query"/>? A blank query keeps
        /// everything; otherwise every word must be found in at least one field.
        /// </summary>
        public static bool Matches(string query, IEnumerable<string> fields)
        {
            var terms = Terms(query);
            if (terms.Count == 0) return true;

            var list = new List<string>();
            if (fields != null)
                foreach (var f in fields)
                    if (!string.IsNullOrEmpty(f)) list.Add(f);

            foreach (var t in terms)
            {
                bool found = false;
                foreach (var f in list)
                    if (f.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
    }
}
