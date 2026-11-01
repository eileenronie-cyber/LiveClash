using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiveClash.Testing
{
    /// <summary>
    /// What is in a folder, and what changed since: every file's relative path, length and SHA-256. Revit-free, so the rule is asserted
    /// headlessly (LedgerReviewR4Checks). <see cref="RealStateGuard"/> uses it to show that a dev job left the user's real
    /// %LOCALAPPDATA%\LiveClash folder alone (review 2026-10-31, delta review round 2, DM2 / rule E6).
    /// </summary>
    public static class FolderSnapshot
    {
        /// <summary>The difference between two snapshots.</summary>
        public sealed class Diff
        {
            public List<string> Added { get; } = new List<string>();
            public List<string> Changed { get; } = new List<string>();
            public List<string> Removed { get; } = new List<string>();

            public bool Any { get { return Added.Count + Changed.Count + Removed.Count > 0; } }

            public override string ToString()
            {
                return "added " + Added.Count + ", changed " + Changed.Count + ", removed " + Removed.Count
                       + (Any ? ": " + string.Join(", ", First(Added, Changed, Removed)) : "");
            }

            private static IEnumerable<string> First(params List<string>[] lists)
            {
                int n = 0;
                foreach (var l in lists)
                    foreach (var s in l)
                    {
                        if (n++ >= 6) { yield return "..."; yield break; }
                        yield return s;
                    }
            }
        }

        /// <summary>
        /// Every file under <paramref name="dir"/> (recursively), keyed by its path relative to it, with its length and SHA-256.
        /// Files whose NAME starts with one of <paramref name="excludeNamePrefixes"/> are left out. A missing folder gives an empty
        /// snapshot; a file that is locked is recorded by length alone. Never throws.
        /// </summary>
        public static Dictionary<string, string> Take(string dir, params string[] excludeNamePrefixes)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return map;
                var root = Path.GetFullPath(dir).TrimEnd('\\', '/');
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (Excluded(name, excludeNamePrefixes)) continue;
                    map[file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')] = Describe(file);
                }
            }
            catch { }
            return map;
        }

        private static bool Excluded(string name, string[] prefixes)
        {
            if (prefixes == null) return false;
            foreach (var p in prefixes)
                if (!string.IsNullOrEmpty(p) && name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Describe(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(fs);
                    var hex = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    return fs.Length + ":" + hex;
                }
            }
            catch
            {
                try { return new FileInfo(file).Length + ":locked"; } catch { return "?"; }
            }
        }

        /// <summary>What was added, changed and removed between <paramref name="before"/> and <paramref name="after"/>.</summary>
        public static Diff Compare(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var d = new Diff();
            before = before ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            after = after ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in after)
            {
                string was;
                if (!before.TryGetValue(kv.Key, out was)) d.Added.Add(kv.Key);
                else if (was != kv.Value) d.Changed.Add(kv.Key);
            }
            foreach (var kv in before)
                if (!after.ContainsKey(kv.Key)) d.Removed.Add(kv.Key);
            d.Added.Sort(StringComparer.OrdinalIgnoreCase);
            d.Changed.Sort(StringComparer.OrdinalIgnoreCase);
            d.Removed.Sort(StringComparer.OrdinalIgnoreCase);
            return d;
        }
    }
}
