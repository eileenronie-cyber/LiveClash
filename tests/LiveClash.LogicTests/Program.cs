using System;
using LiveClash.Testing;

namespace LiveClash.LogicTests
{
    /// <summary>
    /// Console entry point for the headless logic gate. Runs the shared
    /// FilterLogicChecks and exits with the failure count so CI can gate on it.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            // Three outcomes, matching SelfTest: null means the check could not run
            // here, and a skip is never counted as a pass. None of the headless checks
            // skip today - they need no model - but the sink must agree with the shared
            // FilterLogicChecks signature, and a suite that later gains a skip must not
            // start quietly inflating its own pass count.
            int pass = 0, fail = 0, skip = 0;
            Action<string, bool?> check = (name, ok) =>
            {
                if (!ok.HasValue) { skip++; Console.WriteLine("SKIP  " + name); return; }
                if (ok.Value) pass++; else fail++;
                Console.WriteLine((ok.Value ? "PASS  " : "FAIL  ") + name);
            };

            try
            {
                // One list, shared with the in-Revit SelfTest — see SharedChecks.
                SharedChecks.RunAll(check);
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine("FAIL  logic checks threw: " + ex);
            }

            Console.WriteLine();
            Console.WriteLine((fail == 0 ? "ALL PASS" : fail + " FAILED")
                              + "  (" + pass + " passed, " + fail + " failed, " + skip + " skipped)");
            return fail;
        }
    }
}
