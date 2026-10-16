using System;
using LiveClash.Testing;

namespace LiveClash.LogicTests
{
    /// <summary>
    /// Console entry point for the headless logic gate. Runs the shared checks and exits
    /// with the failure count so CI can gate on it.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            int pass, fail, skip;
            RunShared(Console.WriteLine, out pass, out fail, out skip);

            Console.WriteLine();
            Console.WriteLine((fail == 0 ? "ALL PASS" : fail + " FAILED")
                              + "  (" + pass + " passed, " + fail + " failed, " + skip + " skipped)");
            return fail;
        }

        /// <summary>
        /// The one run of the shared list, used by both this console and the MSTest wrapper
        /// (SharedChecksTest), so the two can never disagree about what was checked.
        /// </summary>
        internal static void RunShared(Action<string> line, out int pass, out int fail, out int skip)
        {
            // Three outcomes, matching the in-Revit checks: null means the check could not run
            // here, and a skip is never counted as a pass. None of the headless checks
            // skip today - they need no model - but the sink must agree with the shared
            // signature, and a suite that later gains a skip must not start quietly
            // inflating its own pass count.
            int p = 0, f = 0, s = 0;
            Action<string, bool?> check = (name, ok) =>
            {
                if (!ok.HasValue) { s++; line("SKIP  " + name); return; }
                if (ok.Value) p++; else f++;
                line((ok.Value ? "PASS  " : "FAIL  ") + name);
            };

            try
            {
                // One list, shared with the in-Revit checks — see SharedChecks.
                SharedChecks.RunAll(check);
            }
            catch (Exception ex)
            {
                f++;
                line("FAIL  logic checks threw: " + ex);
            }

            pass = p; fail = f; skip = s;
        }
    }
}
