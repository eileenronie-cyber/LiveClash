using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiveClash.LogicTests
{
    /// <summary>
    /// The shared headless checks as one MSTest test. It exists for Stryker.NET (ci\mutation.ps1),
    /// which can only drive a VSTest test project: every planted defect reruns this, and the defect
    /// counts as caught when any check fails. Program.Main and this run exactly the same list.
    /// </summary>
    [TestClass]
    public class SharedChecksTest
    {
        [TestMethod]
        public void EveryHeadlessCheckPasses()
        {
            var failures = new List<string>();
            int pass, fail, skip;
            Program.RunShared(l => { if (l.StartsWith("FAIL")) failures.Add(l); }, out pass, out fail, out skip);

            Assert.IsTrue(pass > 0, "no check ran at all");
            Assert.AreEqual(0, fail, string.Join("\n", failures));
        }
    }
}
