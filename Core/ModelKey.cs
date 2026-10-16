using System;
using System.Text;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// A stable per-model identity for anything persisted about a document.
    ///
    /// WHY THIS EXISTS AT ALL: Revit's doc.Title changes with HOW the model was
    /// opened. Opening as a local file appends "_&lt;revit username&gt;"; detaching
    /// appends "_detached", sometimes with a number, and detaching a detached copy
    /// appends it again. This very model produced
    ///     ..._jdoe1234ABCD_detached26_detached
    /// and then ..._detached26_detached_detached within one session.
    ///
    /// Keyed on the raw title, every variant gets its OWN status file — so a clash
    /// marked Coordinated under one variant reads as unreviewed under the next, and
    /// the review work silently disappears. That exact failure has already happened
    /// on this machine to another tool's clash ledgers, where one model produced four
    /// title variants in a single day and each grew its own ledger.
    ///
    /// Normalising from the first version is nearly free. Retrofitting it means
    /// writing a migration, and a migration has to be run TWICE to be trusted —
    /// the first pass will find its own output and treat it as fresh input.
    /// </summary>
    public static class ModelKey
    {
        /// <summary>
        /// The normalised title: local-file and detached suffixes stripped, applied
        /// repeatedly because they stack.
        /// </summary>
        public static string Normalize(Document doc)
        {
            if (doc == null) return "unknown";

            string title;
            try { title = doc.Title; } catch { return "unknown"; }
            if (string.IsNullOrEmpty(title)) return "unknown";

            string user = null;
            try { user = doc.Application != null ? doc.Application.Username : null; } catch { }

            return Normalize(title, user);
        }

        /// <summary>Testable form: no Document needed.</summary>
        public static string Normalize(string title, string revitUsername)
        {
            if (string.IsNullOrEmpty(title)) return "unknown";

            var s = title.Trim();

            // Strip a trailing ".rvt" if one ever appears in a title.
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);

            // The suffixes stack in any order, so keep peeling until nothing changes.
            bool changed = true;
            while (changed && s.Length > 0)
            {
                changed = false;

                // "_detached", optionally followed by digits ("_detached26").
                int i = LastIndexOfSuffixToken(s, "_detached");
                if (i >= 0)
                {
                    s = s.Substring(0, i);
                    changed = true;
                    continue;
                }

                // "_<revit username>" appended for a local file.
                if (!string.IsNullOrEmpty(revitUsername))
                {
                    var tail = "_" + revitUsername;
                    if (s.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    {
                        s = s.Substring(0, s.Length - tail.Length);
                        changed = true;
                        continue;
                    }
                }
            }

            s = s.Trim('_', ' ');
            return s.Length == 0 ? "unknown" : s;
        }

        /// <summary>
        /// Index of a trailing "_detached" / "_detached&lt;digits&gt;" token, or -1.
        /// Only matches at the very end so a model genuinely named "..._detached_ph2"
        /// is left alone.
        /// </summary>
        private static int LastIndexOfSuffixToken(string s, string token)
        {
            int idx = s.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            // Everything after the token must be digits only.
            for (int i = idx + token.Length; i < s.Length; i++)
                if (!char.IsDigit(s[i])) return -1;

            return idx;
        }

        /// <summary>The normalised title reduced to something safe for a filename.</summary>
        public static string FileSafe(Document doc)
        {
            var s = Normalize(doc);
            var sb = new StringBuilder(s.Length);
            var bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (var c in s)
            {
                bool ok = true;
                foreach (var b in bad) if (c == b) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            var name = sb.ToString();
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }
    }
}
