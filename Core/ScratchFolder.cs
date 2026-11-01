#if DEBUG || LOGICTESTS_HEADLESS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace LiveClash.Core
{
    /// <summary>
    /// Is this folder one the undo probe (a dev tool) may write in? The first version compared two path STRINGS and refused
    /// its own scratch folder in a real run (an 8.3 short spelling against a long one, or another TEMP in Revit's process: not measured). So
    /// both sides are made long, full paths first (8.3 names resolved, '..' and '/' resolved, trailing separators dropped, case ignored), the
    /// folder may lie under ANY of several temp roots, and a refusal prints both sides. Revit-free. DEV CODE: it is compiled into the Debug
    /// add-in (the probe and the shared checks) and into the headless test assembly, and NOT into a Release build.
    /// </summary>
    public static class ScratchFolder
    {
        private static readonly char[] Separators = { (char)92, (char)47 };

        // A Windows file name cannot contain these, and a directory search treats them as wildcards.
        private static readonly char[] Wildcards = { (char)42, (char)63, (char)60, (char)62, (char)34 };

        /// <summary>The long spelling of an existing path (8.3 names resolved), or the path itself when it does not exist or cannot be resolved.
        /// Managed: the folder listing matches an entry by its short name too and returns its long one.</summary>
        public static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                string parent = Path.GetDirectoryName(path);
                string leaf = Path.GetFileName(path);
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return path;   // a root or a relative single name
                if (leaf.IndexOfAny(Wildcards) >= 0) return path;   // cannot be a real folder, and the search below would take it as a pattern
                string longParent = LongPath(parent);
                if (Directory.Exists(longParent))
                {
                    var match = Directory.GetFileSystemEntries(longParent, leaf).FirstOrDefault();
                    if (match != null) return Path.Combine(longParent, Path.GetFileName(match));
                }
                return Path.Combine(longParent, leaf);
            }
            catch { return path; }
        }

        /// <summary>
        /// A full path with every part in its long spelling and no trailing separator. <paramref name="toLong"/> (default <see cref="LongPath"/>)
        /// is asked for each prefix in turn, so a part that exists is resolved even when a later part does not exist yet.
        /// </summary>
        public static string Normalize(string path, Func<string, string> toLong)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (toLong == null) toLong = LongPath;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return path.Trim(); }

            string root = Path.GetPathRoot(full);
            string rest = full.Substring(root.Length).Trim('\\', '/');
            string current = root.TrimEnd('\\', '/');
            if (rest.Length == 0) return current.Length == 2 && current[1] == ':' ? current + "\\" : current;

            foreach (var segment in rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current + "\\" + segment;
                string resolved = null;
                try { resolved = toLong(candidate); } catch { }
                current = string.IsNullOrEmpty(resolved) ? candidate : resolved.TrimEnd((char)92, (char)47);   // the resolved prefix replaces the running one
            }
            return current;
        }

        /// <summary>
        /// Is <paramref name="dir"/> a folder whose last part starts with <paramref name="prefix"/> and which lies under one of
        /// <paramref name="roots"/>? All of them are compared as normalised long paths, ignoring case. <paramref name="detail"/> says what was
        /// compared (the folder and every root) and is empty when the folder is accepted.
        /// </summary>
        public static bool Check(string dir, string prefix, IEnumerable<string> roots, Func<string, string> toLong, out string detail)
        {
            string folder = Normalize(dir, toLong);
            var normRoots = new List<string>();
            if (roots != null)
                foreach (var r in roots)
                {
                    string n = Normalize(r, toLong);
                    if (n.Length > 0 && !normRoots.Contains(n, StringComparer.OrdinalIgnoreCase)) normRoots.Add(n);
                }

            string leaf = folder.Length == 0 ? "" : Path.GetFileName(folder);
            bool leafOk = leaf.StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase) && leaf.Length > 0;
            bool under = false;
            foreach (var r in normRoots)
            {
                string withSep = r[r.Length - 1] == (char)92 ? r : r + "\\";
                if (folder.StartsWith(withSep, StringComparison.OrdinalIgnoreCase) && folder.Length > withSep.Length) { under = true; break; }
            }

            if (leafOk && under) { detail = ""; return true; }
            detail = "the folder '" + folder + "'"
                     + (leafOk ? "" : " does not start with '" + prefix + "'")
                     + (under ? "" : " is not under any temp folder compared (" + (normRoots.Count == 0 ? "none" : string.Join("; ", normRoots)) + ")");
            return false;
        }
    }
}
#endif
