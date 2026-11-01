using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 3b of the 2026-10-31 review (F3): the Save As carry must not reach another model's records.
    /// The rule that decides it, <see cref="LedgerProvenance"/>, is Revit-free and asserted here. The predictions were
    /// committed before this file existed: tests\evidence\liveclash\REVIT-RUNS-F3-r3.md (8a756a8). The in-Revit half is
    /// the S4b, S4d and S4e scenarios of Testing\SaveChecks.cs; TESTINGS runs them.
    /// </summary>
    public static class LedgerReviewR3Checks
    {
        private const string P = "review F3 r3: ";
        private const int Seed = 20261102;

        private const string ProjectPath = @"C:\Jobs\Tower\Tower_ST.rvt";
        private const string FamilyKey = "unsaved::Family1";

        public static void RunAll(Action<string, bool?> check)
        {
            check(P + "LP-1 [green] the stores loaded for this document: Carry",
                LedgerProvenance.Decide(ProjectPath, ProjectPath) == LedgerProvenance.Verdict.Carry);
            check(P + "LP-2 [green] ...ignoring the case of the path",
                LedgerProvenance.Decide(ProjectPath, ProjectPath.ToUpperInvariant()) == LedgerProvenance.Verdict.Carry);

            check(P + "LP-3 [red] the stores loaded for the project, the document saved is a family: Refuse",
                LedgerProvenance.Decide(ProjectPath, FamilyKey) == LedgerProvenance.Verdict.Refuse
                && LedgerProvenance.Decide(ProjectPath, @"C:\Jobs\Tower\Other.rvt") == LedgerProvenance.Verdict.Refuse);

            check(P + "LP-4 [red] the stores have loaded nothing yet: LoadFresh (nothing to carry)",
                LedgerProvenance.Decide(null, ProjectPath) == LedgerProvenance.Verdict.LoadFresh
                && LedgerProvenance.Decide("", ProjectPath) == LedgerProvenance.Verdict.LoadFresh);

            check(P + "LP-5 [red] the saved document's old key is unknown: Refuse (provenance cannot be shown)",
                LedgerProvenance.Decide(ProjectPath, null) == LedgerProvenance.Verdict.Refuse
                && LedgerProvenance.Decide(ProjectPath, "") == LedgerProvenance.Verdict.Refuse
                && LedgerProvenance.Decide(ProjectPath, "   ") == LedgerProvenance.Verdict.Refuse
                && LedgerProvenance.Decide(null, null) == LedgerProvenance.Verdict.Refuse);

            // LP-6: property.
            var r = new Random(Seed);
            var keys = new[] { null, "", "  ", ProjectPath, ProjectPath.ToLowerInvariant(), FamilyKey, @"C:\Jobs\Tower\Other.rvt", "unsaved::Project1" };
            string first = null;
            for (int i = 0; i < 500 && first == null; i++)
            {
                var loaded = keys[r.Next(keys.Length)];
                var from = keys[r.Next(keys.Length)];
                bool hasLoaded = !string.IsNullOrWhiteSpace(loaded), hasFrom = !string.IsNullOrWhiteSpace(from);
                LedgerProvenance.Verdict want =
                    !hasFrom ? LedgerProvenance.Verdict.Refuse
                    : !hasLoaded ? LedgerProvenance.Verdict.LoadFresh
                    : string.Equals(loaded.Trim(), from.Trim(), StringComparison.OrdinalIgnoreCase) ? LedgerProvenance.Verdict.Carry
                    : LedgerProvenance.Verdict.Refuse;
                var got = LedgerProvenance.Decide(loaded, from);
                if (got != want) first = "pair " + i + ": loaded '" + loaded + "' from '" + from + "': wanted " + want + ", got " + got;
            }
            check(P + "LP-6 [red] property: 500 random pairs follow the rule - Refuse without a key, LoadFresh without loaded stores, Carry when equal ignoring case, else Refuse (seed " + Seed + ")"
                  + (first == null ? "" : " - FIRST " + first), first == null);

            bool threw = false;
            try
            {
                foreach (var a in keys) foreach (var b in keys) LedgerProvenance.Decide(a, b);
            }
            catch { threw = true; }
            check(P + "LP-7 [green] never throws for null and blanks", !threw);
        }
    }
}
