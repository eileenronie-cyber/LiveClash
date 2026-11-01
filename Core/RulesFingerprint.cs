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
    /// settings file, not a rule. The hashing itself is in RulesFingerprint.Lines.cs.
    /// </summary>
    public static partial class RulesFingerprint
    {
        public static string Of(ClashSettings s)
        {
            if (s == null) return "";
            return OfLines(FilterPreset.ShareableLines(s));
        }
    }
}
