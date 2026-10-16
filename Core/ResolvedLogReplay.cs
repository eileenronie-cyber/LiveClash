using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    /// <summary>One clash that stopped existing, with enough detail to read later.</summary>
    public class ResolvedRecord
    {
        public string Key;
        public DateTime WhenUtc;
        public string By;

        public long HostId;
        public string HostName;
        public string HostCategory;

        public long OtherId;
        public string OtherName;
        public string OtherCategory;

        public string ModelName;

        /// <summary>True when this pair is clashing again right now.</summary>
        public bool ClashingAgain;

        /// <summary>
        /// This entry has been WITHDRAWN: re-testing the pair showed it never stopped
        /// clashing, so the fix it claims never happened.
        ///
        /// Not the same thing as <see cref="ClashingAgain"/>, and the difference is the
        /// whole reason this flag exists. ClashingAgain is real churn worth seeing — the
        /// pair was fixed, and something has since re-broken it. Retracted means the
        /// entry was wrong when it was written. Reporting the second as the first would
        /// blame a modeller for breaking something nobody ever fixed.
        /// </summary>
        public bool Retracted;

        /// <summary>When the retraction was recorded. Default when not retracted.</summary>
        public DateTime RetractedUtc;

        public string WhenText { get { return WhenUtc.ToLocalTime().ToString("dd MMM HH:mm"); } }
        public string HostText { get { return HostCategory + ": " + HostName; } }
        public string OtherText { get { return OtherCategory + ": " + OtherName; } }
    }

    /// <summary>
    /// The resolved ledger's line format and replay rules, with no file and no Revit.
    ///
    /// SPLIT OUT FOR THE SAME REASON AS <see cref="ClashStatusReplay"/>: the hard part is
    /// not parsing a line, it is what the file means when two writers' events arrive
    /// interleaved. A retraction can legitimately be appended BEFORE the resolution it
    /// withdraws — another session writes it while this one is mid-append, or the file is
    /// on a shared folder — and a replay that applied retractions inline would silently
    /// keep a withdrawn entry. That is exactly the bug the status ledger already had, and
    /// it was invisible to every check that only ever fed it a tidy file.
    ///
    /// THE RULES:
    ///   - A resolution is one occurrence. Every occurrence is kept: a pair fixed, broken
    ///     and fixed again is two pieces of history and collapsing them would hide the
    ///     churn worth noticing.
    ///   - A retraction names the occurrence it withdraws by (key, WhenUtc) — the pair
    ///     alone is not enough, because the same pair can appear many times and only one
    ///     of those occurrences may be false.
    ///   - Retractions are collected during the pass and applied after it, so file order
    ///     cannot change the answer.
    /// </summary>
    public static class ResolvedLogReplay
    {
        /// <summary>TAB, as in the status ledger: a ClashResult.Key contains pipes.</summary>
        internal const char Sep = '\t';

        /// <summary>Field 10. Absent on every line written before retraction existed,
        /// which is why its absence has to mean <see cref="Resolution"/>.</summary>
        public const string Resolution = "Resolved";
        public const string Retraction = "Retracted";

        /// <param name="modelKey">The file's own model key (may carry a collision suffix — see
        /// ModelIdentity), recorded so the file names itself, same as the status ledger.</param>
        /// <param name="identity">This model's ModelIdentity, or "" for a standalone or detached
        /// model — appended as a claim line so a later load can tell two same-titled models apart.</param>
        public static string Header(string modelKey, string identity)
        {
            var id = string.IsNullOrEmpty(identity) ? "" : ModelIdentity.Line(identity) + Environment.NewLine;
            return "# LiveClash resolved log - append-only" + Environment.NewLine
                 + "# key\tutc\tby\thostId\thostName\thostCat\totherId\totherName\totherCat\tmodel"
                 + "\t[event\tretractedUtc]" + Environment.NewLine
                 + "# event: absent or 'Resolved' = the pair stopped clashing. 'Retracted' ="
                 + " re-testing showed it never did; utc names the occurrence withdrawn."
                 + Environment.NewLine
                 + "# model=" + modelKey + Environment.NewLine
                 + id;
        }

        /// <summary>One resolution line. <paramref name="ev"/> distinguishes a retraction.</summary>
        public static string Line(ResolvedRecord r, string ev, DateTime retractedUtc)
        {
            if (r == null) return "";
            var fields = new[]
            {
                r.Key ?? "",
                r.WhenUtc.ToString("o", CultureInfo.InvariantCulture),
                Clean(r.By),
                r.HostId.ToString(CultureInfo.InvariantCulture),
                Clean(r.HostName),
                Clean(r.HostCategory),
                r.OtherId.ToString(CultureInfo.InvariantCulture),
                Clean(r.OtherName),
                Clean(r.OtherCategory),
                Clean(r.ModelName),
                ev ?? Resolution,
                ev == Retraction ? retractedUtc.ToString("o", CultureInfo.InvariantCulture) : ""
            };
            return string.Join(Sep.ToString(), fields);
        }

        /// <summary>Tabs and newlines would break the one-line-per-event format.</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>Identity of one occurrence: the pair AND when it was recorded.</summary>
        private static string OccurrenceId(string key, DateTime whenUtc)
        {
            return (key ?? "") + "@" + whenUtc.ToString("o", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Replay a ledger's lines into <paramref name="into"/>, newest-first ordering left
        /// to the caller. Retracted occurrences are present but flagged, so a caller can
        /// count them and the audit trail is not silently thinned.
        /// </summary>
        public static void Replay(IEnumerable<string> lines, List<ResolvedRecord> into,
                                  out int events, out int retracted)
        {
            events = 0;
            retracted = 0;
            if (into == null || lines == null) return;

            // occurrence id -> when the retraction was recorded. Latest wins; a second
            // retraction of the same occurrence is not a second withdrawal.
            var retractions = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var byOccurrence = new Dictionary<string, ResolvedRecord>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (line == null || line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(Sep);
                if (p.Length < 10) continue;

                DateTime when;
                if (!DateTime.TryParse(p[1], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                    when = DateTime.MinValue;

                // Field 10 is absent on every line written before retraction existed, so
                // absence MUST read as a plain resolution or the whole existing ledger
                // would replay as nothing at all.
                var ev = p.Length > 10 && p[10].Length > 0 ? p[10] : Resolution;
                var id = OccurrenceId(p[0], when);

                if (ev == Retraction)
                {
                    DateTime rwhen = when;   // initialised so mutants of the condition below still compile
                    if (p.Length <= 11 || !DateTime.TryParse(p[11], CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out rwhen))
                        rwhen = when;

                    // Remembered, not applied: the occurrence may not have been read yet.
                    DateTime seen;
                    if (!retractions.TryGetValue(id, out seen) || rwhen > seen)
                        retractions[id] = rwhen;
                    events++;
                    continue;
                }

                long hid, oid;
                _ = long.TryParse(p[3], out hid);   // 0 when unreadable: the entry still reads by name
                _ = long.TryParse(p[6], out oid);

                var rec = new ResolvedRecord
                {
                    Key = p[0],
                    WhenUtc = when,
                    By = p[2],
                    HostId = hid,
                    HostName = p[4],
                    HostCategory = p[5],
                    OtherId = oid,
                    OtherName = p[7],
                    OtherCategory = p[8],
                    ModelName = p[9]
                };
                into.Add(rec);

                // Two identical occurrences cannot be told apart, so the first wins the
                // slot; a retraction naming it withdraws that one.
                if (!byOccurrence.ContainsKey(id)) byOccurrence[id] = rec;
                events++;
            }

            // NOW apply the retractions, so file order cannot change the answer.
            foreach (var kv in retractions)
            {
                ResolvedRecord rec;
                if (!byOccurrence.TryGetValue(kv.Key, out rec)) continue;   // nothing to withdraw
                if (rec.Retracted) continue;
                rec.Retracted = true;
                rec.RetractedUtc = kv.Value;
                retracted++;
            }
        }
    }
}
