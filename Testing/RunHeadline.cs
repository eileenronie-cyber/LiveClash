namespace LiveClash.Testing
{
    /// <summary>
    /// The first line of a self-test report (delta review 3, DR3-L4). A run with <c>areas=</c> is a FILTERED run and must not read like a full one.
    /// Revit-free, so the headless gate can check it. A full run's headline is unchanged: <c>ALL PASS  (n passed, f failed, s skipped)</c>; a filtered one is
    /// <c>ALL PASS (areas: x)  (n passed, ...)</c>, which the in-Revit run scripts still read (the counts and the verdict are where they were).
    /// </summary>
    public static class RunHeadline
    {
        public static string Of(int fail, int pass, int skip, string areas)
        {
            return (fail == 0 ? "ALL PASS" : fail + " FAILED")
                 + (string.IsNullOrEmpty(areas) ? "" : " (areas: " + areas + ")")
                 + "  (" + pass + " passed, " + fail + " failed, " + skip + " skipped)";
        }
    }
}