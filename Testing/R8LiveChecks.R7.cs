using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8, builder "live", eighth part: where a model-bound Revit check may FAIL and where it must only SKIP by name. A check that needs the
    /// generated fixture FAILS ("COULD NOT RUN: reason") only on the fixture, the model it is meant for, when something it needs is missing there (an
    /// insulation type, a level, a system). On any other model it prints one named line and skips. Revit-free, so the rule is asserted headlessly.
    /// </summary>
    public static partial class R8LiveChecks
    {
        internal enum ModelScope { Run, Skip, CouldNotRun }

        /// <summary>What a model-bound check does: run, skip by name (it is not the model it is meant for), or fail because the model it is meant for lacks something.</summary>
        internal static ModelScope ScopeFor(bool onTargetModel, string missingReason)
        {
            if (!onTargetModel) return ModelScope.Skip;
            return missingReason == null ? ModelScope.Run : ModelScope.CouldNotRun;
        }

        /// <summary>Why M1 cannot be built in this model, or null when everything it needs is there.</summary>
        internal static string M1MissingReason(bool hasLevel, bool hasPipeSystem, bool hasPipeType, bool hasDuctSystem, bool hasDuctType, bool hasPipeInsulationType, bool hasDuctInsulationType)
        {
            if (!hasLevel) return "the model has no level";
            if (!hasPipeSystem || !hasPipeType) return "the model has no piping system or pipe type";
            if (!hasDuctSystem || !hasDuctType) return "the model has no mechanical system or duct type";
            if (!hasPipeInsulationType || !hasDuctInsulationType) return "the template has no pipe or duct insulation type to insulate with";
            return null;
        }

        /// <summary>The models the M2 probe measures on: Snowdon and testproject (testproject's host holds the large target categories Snowdon lacks).</summary>
        internal static bool IsBigTargetModel(string title)
        {
            return !string.IsNullOrEmpty(title)
                && System.Text.RegularExpressions.Regex.IsMatch(title, "snowdon|testproject", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static void ModelScopeRules(Action<string, bool?> check)
        {
            check(P + "MS-5 [green] the M2 probe's models are Snowdon and testproject, in any case, and no other",
                IsBigTargetModel("Snowdon_model") && IsBigTargetModel("TESTPROJECT") && IsBigTargetModel("x-testproject-y")
                && !IsBigTargetModel("fx-host") && !IsBigTargetModel("") && !IsBigTargetModel(null));
            check(P + "MS-1 [green] a check on a model it is not meant for skips by name, whether or not something is missing",
                ScopeFor(false, null) == ModelScope.Skip && ScopeFor(false, "the template has no pipe or duct insulation type to insulate with") == ModelScope.Skip);
            check(P + "MS-2 [green] on its own model with nothing missing it runs", ScopeFor(true, M1MissingReason(true, true, true, true, true, true, true)) == ModelScope.Run);

            // The planted case: the fixture's template without an insulation type must FAIL, not skip.
            string noInsulation = M1MissingReason(true, true, true, true, true, false, true);
            check(P + "MS-3 [green] planted: on its own model, a template with no pipe insulation type is COULD NOT RUN (a failure), with the reason (" + (noInsulation ?? "null") + ")",
                noInsulation != null && noInsulation.Contains("insulation") && ScopeFor(true, noInsulation) == ModelScope.CouldNotRun);

            int wrong = 0;
            for (int mask = 0; mask < 128; mask++)
            {
                bool[] have = new bool[7];
                for (int i = 0; i < 7; i++) have[i] = (mask & (1 << i)) != 0;
                string r = M1MissingReason(have[0], have[1], have[2], have[3], have[4], have[5], have[6]);
                bool allThere = mask == 127;
                if ((r == null) != allThere) wrong++;
                if (ScopeFor(true, r) != (allThere ? ModelScope.Run : ModelScope.CouldNotRun)) wrong++;
                if (ScopeFor(false, r) != ModelScope.Skip) wrong++;
            }
            check(P + "MS-4 [green] over all 128 combinations of what the model has: a reason exists exactly when something is missing, and the scope follows (" + wrong + " wrong)", wrong == 0);
        }
    }
}
