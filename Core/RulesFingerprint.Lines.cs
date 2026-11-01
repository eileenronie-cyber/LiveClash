using System;
using System.Collections.Generic;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>
    /// The pure half of <see cref="RulesFingerprint"/>: lines in, tag out. Revit-free, so the headless
    /// gate can assert that the same rules give the same tag on every runtime.
    /// </summary>
    public static partial class RulesFingerprint
    {
        /// <summary>The fingerprint of a preset's shareable lines (<c>FilterPreset.ShareableLines</c>).</summary>
        public static string OfLines(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            if (lines == null) return ShortHash.Of(sb.ToString());
            foreach (var l in lines)
            {
                if (l == null) continue;
                // "activeset" is which tab happens to be selected in the Filter window — a
                // navigation preference, not a rule. Every ENABLED set runs regardless of which
                // one is active, so switching tabs used to change the fingerprint of two panels
                // running IDENTICAL rules, which is exactly what this exists to rule out. Found
                // reviewing the whole codebase, 2026-09-27 (my own gap from the round that added
                // this file).
                if (l.StartsWith("activeset=", StringComparison.Ordinal)) continue;
                sb.Append(Canonical(l)); sb.Append('\n');
            }
            return ShortHash.Of(sb.ToString());
        }

        /// <summary>The length keys, whose value is a double in feet.</summary>
        private static readonly string[] LengthKeys = { "clearance=", "setclearance=", "groupdist=" };

        /// <summary>
        /// One line as it is hashed. A length is hashed as whole micrometres, not as written: the file
        /// holds double.ToString(), which keeps 15 significant digits on .NET Framework (Revit 2024) and
        /// the shortest round-trip string on .NET 8/10 (2025/2026). 50 mm is "0.164041994750656" on one
        /// and "0.1640419947506562" on the other, so identical rules fingerprinted differently across
        /// versions (review 2026-10-31, LOW-10). The file itself is unchanged.
        /// </summary>
        internal static string Canonical(string line)
        {
            foreach (var k in LengthKeys)
            {
                if (!line.StartsWith(k, StringComparison.Ordinal)) continue;
                double ft;
                if (!double.TryParse(line.Remove(0, k.Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out ft)
                    || double.IsNaN(ft) || Math.Abs(ft) > 1e9) return line;   // junk or absurd: as written (a long cast would differ by runtime)
                long um = (long)Math.Round(ft * 304800.0, MidpointRounding.AwayFromZero);
                return k + "um:" + um.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return line;
        }
    }
}
