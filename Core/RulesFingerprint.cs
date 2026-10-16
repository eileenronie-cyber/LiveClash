using System;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>
    /// A short tag of the settings a <see cref="FilterPreset"/> would export — the RULES that
    /// decide what counts as a clash, not the per-machine tuning (budget, slice sizes, grouping,
    /// the live on/off switch).
    ///
    /// Settings are per-model, and a preset already lets a team share them — but nothing told a
    /// seat that its rules had quietly drifted from a colleague's. Two people whose panels show
    /// the same fingerprint are running the same rules; nothing else needs to be compared by eye.
    /// Built from exactly <see cref="FilterPreset.ShareableLines"/> so it changes precisely when
    /// an exported preset would, and never from anything a preset would not carry — the per-model
    /// identity claim (<see cref="ModelIdentity"/>) included, since that says which model owns the
    /// settings file, not a rule.
    /// </summary>
    public static class RulesFingerprint
    {
        public static string Of(ClashSettings s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (var l in FilterPreset.ShareableLines(s))
            {
                // "activeset" is which tab happens to be selected in the Filter window — a
                // navigation preference, not a rule. Every ENABLED set runs regardless of which
                // one is active, so switching tabs used to change the fingerprint of two panels
                // running IDENTICAL rules, which is exactly what this exists to rule out. Found
                // reviewing the whole codebase, 2026-09-27 (my own gap from the round that added
                // this file).
                if (l.StartsWith("activeset=", StringComparison.Ordinal)) continue;
                sb.Append(l); sb.Append('\n');
            }
            return ShortHash.Of(sb.ToString());
        }
    }
}
