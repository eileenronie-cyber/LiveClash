using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    /// <summary>
    /// The version of the filter text format, and the preset header that goes with it (review 2026-10-31
    /// round 2, DL7).
    ///
    /// Version 2 is when a picked or rule VALUE stopped being cut at its first '|' and learned to carry line
    /// breaks (LOW-4). A build that predates it (abe7dfd and earlier, the 0.9.0 pilot among them) reads such a line without
    /// complaint and silently keeps only part of the value, which changes what the filter matches. Nothing
    /// in a line can warn it. So:
    ///
    ///   - the settings file carries <see cref="Line"/>, which old builds ignore (an unknown key) and new
    ///     builds check: a file that declares a NEWER version is read as well as it can be, with a warning;
    ///   - a PRESET, the file that is handed to other people and other builds, gets a header an old build
    ///     refuses ("not a Live Clash filter preset") when, and only when, it holds a value the old reader
    ///     would cut. A preset with plain values keeps the old header and opens everywhere.
    ///
    /// A downgrade of a person's own settings file cannot be guarded this way (it is read at once, as
    /// settings, not behind a header); the pilot guide says what to check.
    /// </summary>
    public static class FilterFormat
    {
        public const int Version = 2;
        public const string Key = "lcformat";
        public const string Line = Key + "=2";
        public const string V1Header = "# LiveClash filter preset v1";

        /// <summary>The v2 header. It deliberately does NOT start with "# LiveClash filter preset" (note the
        /// hyphens): that prefix is the whole of the old build's check.</summary>
        public const string V2Header = "# LiveClash-filter-preset v2";

        private const string OldPrefix = "# LiveClash filter preset";

        /// <summary>The stems whose last field is a value, and how many '|' fields they have when the value
        /// holds no '|' of its own (names are escaped, so only the value can add fields).</summary>
        private static readonly Dictionary<string, int> ValueStems = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "pick", 3 }, { "xpick", 3 }, { "only", 3 }, { "rule", 5 }, { "grule", 6 }
        };

        /// <summary>The version a set of lines declares; 0 when none does (a file from before the line).</summary>
        public static int Declared(IEnumerable<string> lines)
        {
            int found = 0;
            if (lines == null) return found;
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                var l = raw.Trim();
                if (!l.StartsWith(Key + "=", StringComparison.Ordinal)) continue;
                int v;
                if (int.TryParse(l.Remove(0, Key.Length + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > found) found = v;
            }
            return found;
        }

        /// <summary>A warning when the lines were written by a newer format than this build reads; otherwise null.</summary>
        public static string WarnIfNewer(IEnumerable<string> lines)
        {
            int v = Declared(lines);
            return v > Version
                ? "This file was written in a newer format (" + v + ") than this build reads (" + Version + "). Lines it does not understand are ignored."
                : null;
        }

        /// <summary>
        /// Do these filter lines ("s..." and "t..." prefixed, as <c>ParameterFilter.Serialize</c> writes them) hold
        /// a value an old build would read wrongly: one with a '|' in it (more fields than its stem has), or an
        /// escape (CR, LF and ESC are written as ESC sequences)?
        /// </summary>
        public static bool NeedsV2(IEnumerable<string> lines)
        {
            if (lines == null) return false;
            foreach (var l in lines)
            {
                if (string.IsNullOrEmpty(l)) continue;
                int eq = l.IndexOf('=');
                if (eq < 2) continue;
                if (l[0] != 's' && l[0] != 't') continue;
                int arity;
                if (!ValueStems.TryGetValue(l.Substring(1, eq - 1), out arity)) continue;
                var value = l.Remove(0, eq + 1);
                int fields = 1;
                foreach (var c in value)
                {
                    if (c == '\u001b') return true;   // a CR, LF or ESC in the value was written as an ESC sequence
                    if (c == '|') fields++;
                }
                if (fields > arity) return true;
            }
            return false;
        }

        /// <summary>The first line of a preset holding these filter lines.</summary>
        public static string PresetHeader(IEnumerable<string> lines)
        {
            return NeedsV2(lines) ? V2Header : V1Header;
        }

        /// <summary>The rule an OLD build (abe7dfd and earlier, the 0.9.0 pilot among them) applies to a preset's first line.</summary>
        public static bool OldReaderAccepts(string first)
        {
            return first != null && first.TrimStart('﻿').StartsWith(OldPrefix, StringComparison.Ordinal);
        }

        /// <summary>Does this build accept the preset's first line, and which version does it declare?</summary>
        public static bool Accepts(string first, out int version)
        {
            version = 0;
            if (first == null) return false;
            var h = first.TrimStart('﻿');
            if (h.StartsWith(V2Header, StringComparison.Ordinal)) { version = 2; return true; }
            if (h.StartsWith(OldPrefix, StringComparison.Ordinal)) { version = 1; return true; }
            return false;
        }
    }
}
