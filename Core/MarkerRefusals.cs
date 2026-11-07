using System.Globalization;

namespace LiveClash.Core
{
    /// <summary>What the Refresh pre-flight read about the document (the Revit-bound half fills this in). The defaults mean "all clear".</summary>
    public sealed class MarkerEnv
    {
        public bool IsFamily { get; set; }
        public bool IsLinked { get; set; }
        public bool IsReadOnly { get; set; }
        public bool Workshared { get; set; }
        public bool WorksetClosed { get; set; }
        public bool NotSessionDocument { get; set; }
        /// <summary>The user holding the settings record (first Create, Remove all); empty when nobody else does.</summary>
        public string SettingsHeldBy { get; set; } = "";
        /// <summary>A parameter in the model with a LiveClash name but another GUID; empty when there is none.</summary>
        public string ForeignParameterName { get; set; } = "";
        /// <summary>The per-PC kill switch is off (spec 12.2): every marker write refuses.</summary>
        public bool SwitchedOff { get; set; }
    }

    /// <summary>The "refuse rather than half-commit" cases of spec section 7 that are decided before any transaction, with their exact texts. Pure.</summary>
    public static class MarkerRefusals
    {
        /// <summary>The first refusal in the spec's order, or null when the document may be written. Never throws.</summary>
        public static string Check(MarkerEnv env)
        {
            if (env == null) return null;
            if (env.SwitchedOff) return "Clash markers are switched off on this PC. Nothing was changed.";
            if (env.IsFamily) return "Clash markers live in project models only.";
            if (env.IsLinked) return "A linked model cannot hold clash markers. Open it on its own.";
            if (env.IsReadOnly) return "This model is read-only.";
            if (env.Workshared && env.WorksetClosed) return "The LiveClash workset is closed. Open it (Manage Worksets) and try again.";
            if (env.NotSessionDocument) return "Switch to the model LiveClash is checking.";
            if (!string.IsNullOrWhiteSpace(env.SettingsHeldBy))
                return env.SettingsHeldBy + " is changing the clash schedule set-up. Try again after they synchronize.";
            if (!string.IsNullOrWhiteSpace(env.ForeignParameterName))
                return env.ForeignParameterName + " already exists in this model but is not LiveClash's. Rename it first.";
            return null;
        }

        /// <summary>"N markers were written by a newer LiveClash..." for the summary; empty for none.</summary>
        public static string NewerFormatNote(int count)
        {
            if (count <= 0) return "";
            return count == 1
                ? "1 marker was written by a newer LiveClash; update to change it."
                : count.ToString("N0", CultureInfo.InvariantCulture) + " markers were written by a newer LiveClash; update to change them.";
        }

        /// <summary>The plan-above-the-cap message (spec 7).</summary>
        public static string CapRefusal(int count, int cap)
        {
            return count.ToString("N0", CultureInfo.InvariantCulture)
                + " markers would be created. Narrow the sets, or create them in two runs (Refresh again after this one).";
        }

        public static bool IsOverCap(int count, int cap)
        {
            return count > cap;
        }
    }
}
