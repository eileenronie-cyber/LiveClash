using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>Round 8, builder "live": the idle tick's order (item 4b), the reset call sites (item 4b) and the time-boxed slice (item 5). Revit-free.</summary>
    public static partial class R8LiveChecks
    {
        // ================================================================== item 4b: the order of the idle tick, and the reset call sites

        private static void IdleOrder(Action<string, bool?> check)
        {
            string order = string.Join(",", Array.ConvertAll(IdleSchedule.Order, s => s.ToString()));
            check(P + "IS-1 [red] the idle tick runs its steps in this order: FollowPath,Triggers,SettingsReset,WarmIndex,RefreshContexts,WarmCounts,ShowScope,VerifySlice,Drain (got " + order + ")",
                order == "FollowPath,Triggers,SettingsReset,WarmIndex,RefreshContexts,WarmCounts,ShowScope,VerifySlice,Drain");

            // D3-1: the reset comes before every step that reads the settings in a way that can swap the rules, so no old row is re-tested under new ones.
            int reset = Array.IndexOf(IdleSchedule.Order, IdleStep.SettingsReset);
            var early = new List<string>();
            for (int i = 0; i < IdleSchedule.Order.Length; i++)
                if (IdleSchedule.ReadsSettings(IdleSchedule.Order[i]) && (reset < 0 || i < reset)) early.Add(IdleSchedule.Order[i].ToString());
            check(P + "IS-2 [red] no step that reads the settings runs before SettingsReset (D3-1)" + (early.Count == 0 ? "" : " (before it: " + string.Join(",", early) + ")"),
                reset >= 0 && early.Count == 0);

            var seen = new HashSet<IdleStep>(IdleSchedule.Order);
            check(P + "IS-3 [green] every step is in the order exactly once",
                IdleSchedule.Order.Length == typeof(IdleStep).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Length && seen.Count == IdleSchedule.Order.Length);
        }

        /// <summary>
        /// CS-1. Every place that throws the results away because the RULES changed goes through ONE method, LiveClashApp.ClearResultsForNewRules,
        /// which also lets go of the held link rows (D3-2). A site reverted to ClashResultStore.Instance.Clear() would leave them held, and an undo
        /// of a link deletion would then list rows of rules that are gone. A SOURCE-STRUCTURE check: it reads the files, it does not drive the
        /// handlers (they are dialogs). The behaviour of the method itself is CR-1, in Revit. Skips where the source tree cannot be found.
        /// </summary>
        private static void ResetSites(Action<string, bool?> check)
        {
            string root = RepoRoot();
            if (root == null)
            {
                check(P + "CS-1 [red-by-mutant] the four reset sites call ClearResultsForNewRules and none calls Clear() (the source tree was not found)", (bool?)null);
                return;
            }

            string[] files = { @"UI\FilterHandler.cs", @"Commands\Commands.cs", @"UI\AuditHandler.cs" };
            foreach (var rel in files)
            {
                string text = ReadSource(root, rel);
                bool calls = text != null && text.Contains("LiveClashApp.ClearResultsForNewRules()");
                bool clears = text != null && text.Contains("ClashResultStore.Instance.Clear()");
                check(P + "CS-1 [red-by-mutant] " + rel.Replace('\\', '/') + " throws results away for new rules through ClearResultsForNewRules and never calls Clear() (calls " + calls + ", Clear() " + clears + ")",
                    calls && !clears);
            }

            string app = ReadSource(root, "LiveClashApp.cs");
            string body = Between(app, "internal static bool HandlePendingRulesReload()", "internal static void RetrySettingsAndReset()");
            bool appCalls = body != null && body.Contains("ClearResultsForNewRules();");
            bool appClears = body != null && body.Contains("ClashResultStore.Instance.Clear()");
            check(P + "CS-1 [red-by-mutant] LiveClashApp.HandlePendingRulesReload (the settings re-read) resets through ClearResultsForNewRules and never calls Clear() (calls " + appCalls + ", Clear() " + appClears + ")",
                appCalls && !appClears);

            string one = Between(app, "internal static void ClearResultsForNewRules()", "/// <summary>");
            check(P + "CS-1 [red-by-mutant] ...and ClearResultsForNewRules itself calls ClashResultStore.ClearForNewRules (the store clear plus the held link rows)",
                one != null && one.Contains("ClashResultStore.Instance.ClearForNewRules("));
        }

        private static string RepoRoot()
        {
            return RepoRootFrom(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string RepoRootFrom(string startDir)
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(startDir);
                while (dir != null)
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "LiveClash.csproj"))
                        && System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "LiveClashApp.cs")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static string ReadSource(string root, string rel)
        {
            try { return System.IO.File.ReadAllText(System.IO.Path.Combine(root, rel)); }
            catch { return null; }
        }

        private static string Between(string text, string from, string to)
        {
            if (text == null) return null;
            int a = text.IndexOf(from, StringComparison.Ordinal);
            if (a < 0) return null;
            int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            return b < 0 ? text.Substring(a) : text.Substring(a, b - a);
        }

        // ================================================================== item 5: SP8 / E7, a slice capped by elapsed time

        private static void TimeBoxedSlice(Action<string, bool?> check)
        {
            // A fake clock that moves 40 ms for every item processed.
            Func<int, SliceResult> run = budget =>
            {
                long t = 0;
                var items = new[] { 1, 2, 3, 4, 5 };
                return SliceBudget.Run(items, budget, () => t, x => { t += 40; });
            };

            var r = run(100);
            check(P + "SB-1 [red] a slice stops when its budget is spent (40 ms per item, budget 100 ms, 5 items: " + r.Done + " done, " + r.Ms + " ms, stopped by time " + r.StoppedByTime + ")",
                r.Done == 3 && r.StoppedByTime && r.Ms == 120);

            var zero = run(0);
            check(P + "SB-2 [red] ...it always does at least one, however small the budget, and then stops (budget 0: " + zero.Done + " done, stopped by time " + zero.StoppedByTime + ")",
                zero.Done == 1 && zero.StoppedByTime);

            var all = run(100000);
            check(P + "SB-3 [green] ...a short list is done in full and not called stopped (" + all.Done + " done, stopped by time " + all.StoppedByTime + ")",
                all.Done == 5 && !all.StoppedByTime);

            var none = SliceBudget.Run(System.Array.Empty<int>(), 100, () => 0, x => { });
            check(P + "SB-3b [green] ...an empty list does nothing and is not called stopped", none.Done == 0 && !none.StoppedByTime);

            check(P + "SB-4 [red] the timing line reads: Reconcile tick: asked 25 of 80, done 12, 150 ms, stopped by time yes",
                SliceBudget.TimingLine("tick", 25, 80, 12, 150, true) == "Reconcile tick: asked 25 of 80, done 12, 150 ms, stopped by time yes"
                && SliceBudget.TimingLine("rollback", 3, 3, 3, 4, false) == "Reconcile rollback: asked 3 of 3, done 3, 4 ms, stopped by time no");

            var rnd = new Random(20261102);
            int wrong = 0;
            string first = null;
            for (int i = 0; i < 300; i++)
            {
                int n = rnd.Next(0, 12);
                long budget = rnd.Next(0, 300);
                int[] cost = new int[n];
                var items = new int[n];
                for (int k = 0; k < n; k++) { cost[k] = rnd.Next(0, 90); items[k] = k; }
                long t = 0;
                int started = 0;
                var res = SliceBudget.Run(items, budget, () => t, k => { t += cost[k]; started++; });

                // The same rule written out: item k is started only while the time spent so far is under the budget, and item 0 always is.
                int expected = 0; long spent = 0;
                for (int k = 0; k < n; k++) { if (k > 0 && spent >= budget) break; spent += cost[k]; expected++; }
                bool ok = res.Done == expected && started == expected && res.StoppedByTime == (expected < n) && res.Ms == spent;
                if (!ok) { wrong++; if (first == null) first = "n=" + n + " budget=" + budget + " done " + res.Done + " expected " + expected; }
            }
            check(P + "SB-5 [red] property: 300 random lists, costs and budgets follow the rule - at least one, then stop once the budget is spent"
                  + (wrong == 0 ? " (seed 20261102)" : " (" + wrong + " wrong, first " + first + ")"), wrong == 0);

            check(P + "SB-6 [green] an empty slice never logs", !SliceBudget.ShouldLogTick(0, 500, true, 1000));
            check(P + "SB-7 [green] a quick, recent tick does not log (asked 3, 5 ms, not stopped, 10 s since the last line)", !SliceBudget.ShouldLogTick(3, 5, false, 10));
            check(P + "SB-8 [red] a slow tick, a tick stopped by time, and a tick 60 s after the last line all log",
                SliceBudget.ShouldLogTick(3, 25, false, 1) && SliceBudget.ShouldLogTick(3, 5, true, 1) && SliceBudget.ShouldLogTick(3, 5, false, 60));
        }
    }
}
