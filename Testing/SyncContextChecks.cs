using System;
using System.Diagnostics;
using System.Threading;

namespace LiveClash.Testing
{
    /// <summary>
    /// The schedule-flow checks must not depend on the thread that runs them being free to pump (overnight Revit runs, 2026-11-01). Inside Revit
    /// the self-test runs in an ExternalEvent on the UI thread, whose SynchronizationContext posts to a message loop that is not running: the
    /// controller's awaits resume on that context, so every async flow sat in the check's <c>Task.Wait</c> until its timeout (about 5 s each,
    /// 307 s in all), the self-test took 390-430 s against the harness's 420 s limit, and the continuations that had not run reported false.
    /// This check runs the flows on a thread whose context is never pumped and asks that they still finish at once, and pass.
    /// </summary>
    internal static class SyncContextChecks
    {
        private const string P = "sync context: ";

        /// <summary>A context that takes a post and never runs it: the UI thread of a Revit self-test while it is inside Execute.</summary>
        private sealed class NoPump : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state) { }
            public override void Send(SendOrPostCallback d, object state) { d(state); }
        }

        /// <summary>One run of a body on a raw background thread whose context nobody pumps, with the shared wait shortened for it.</summary>
        private sealed class PumplessRun
        {
            public Thread Thread;
            public Exception Thrown;
            public long Ms;
            public bool ContextKept;

            public static PumplessRun Start(Action body, int savedWait)
            {
                var run = new PumplessRun();
                run.Thread = new Thread(() =>
                {
                    var blocked = new NoPump();
                    SynchronizationContext.SetSynchronizationContext(blocked);
                    ScheduleFlowChecks.WaitMs = 200;   // a flow that cannot finish is given up on quickly, so a failure here costs seconds, not minutes
                    var watch = Stopwatch.StartNew();
                    try { body(); }
                    catch (Exception ex) { run.Thrown = ex; }   // an exception on a raw thread ends the process (Revit: NO RESULT): keep it for a named line
                    finally
                    {
                        watch.Stop();
                        run.Ms = watch.ElapsedMilliseconds;
                        run.ContextKept = ReferenceEquals(SynchronizationContext.Current, blocked);
                        ScheduleFlowChecks.WaitMs = savedWait;
                        SynchronizationContext.SetSynchronizationContext(null);
                    }
                });
                run.Thread.IsBackground = true;
                run.Thread.Start();
                return run;
            }
        }

        internal static void RunAll(Action<string, bool?> check)
        {
            int passed = 0, failed = 0;
            int savedWait = ScheduleFlowChecks.WaitMs;
            var run = PumplessRun.Start(() => ScheduleFlowChecks.RunAll((name, ok) => { if (ok == true) passed++; else if (ok == false) failed++; }), savedWait);
            bool joined = run.Thread.Join(60000);

            var thrown = run.Thrown;
            check(P + "the schedule-flow checks ran to the end on that thread, without an exception and within a minute"
                + (thrown != null ? " (" + thrown.GetType().Name + ": " + thrown.Message + ")" : joined ? "" : " (still running after 60 s)"), thrown == null && joined);
            check(P + "the schedule-flow checks finish at once on a thread whose context nobody pumps (" + run.Ms + " ms)", run.Ms < 6000);
            check(P + "and every one of them passes (" + passed + " passed, " + failed + " failed)", failed == 0 && passed > 100);
            check(P + "the caller's synchronization context is back as it was afterwards", run.ContextKept);

            AfterATimeout(check, savedWait);
        }

        /// <summary>
        /// Delta review 3, DR3-L5. When the Join times out, the worker goes on in the background with the shortened wait and its own count of unfinished
        /// flows. The checks that run next must not see either. The worker here is held at a gate on purpose, so the timeout is real and short.
        /// </summary>
        private static void AfterATimeout(Action<string, bool?> check, int savedWait)
        {
            ScheduleFlowChecks.ResetCountsForTest();
            var started = new ManualResetEventSlim(false);
            var gate = new ManualResetEventSlim(false);
            PumplessRun stuck = null;
            int waitDuring = -1, countDuring = -1;
            bool joined = true, ended = false;
            int waitAfter = -1, countAfter = -1;
            try
            {
                stuck = PumplessRun.Start(() => { ScheduleFlowChecks.CountUnfinishedForTest(); started.Set(); gate.Wait(30000); }, savedWait);
                started.Wait(10000);
                joined = stuck.Thread.Join(100);   // times out: the gate is shut
                waitDuring = ScheduleFlowChecks.WaitMs;
                countDuring = ScheduleFlowChecks.UnfinishedCountForTest;
            }
            finally
            {
                gate.Set();
                if (stuck != null) ended = stuck.Thread.Join(10000);
                waitAfter = ScheduleFlowChecks.WaitMs;
                countAfter = ScheduleFlowChecks.UnfinishedCountForTest;
                ScheduleFlowChecks.ResetCountsForTest();
            }

            check(P + "L5 a Join that timed out leaves the caller's shared wait as it was while the worker still runs (" + waitDuring + " ms, not the worker's 200)",
                !joined && waitDuring == savedWait);
            check(P + "L5 ...and the caller sees none of the worker's unfinished-flow count (" + countDuring + ")", countDuring == 0);
            check(P + "L5 ...and after the worker has ended both are still as they were (" + waitAfter + " ms, " + countAfter + ")",
                ended && waitAfter == savedWait && countAfter == 0);

            int fresh = -1;
            var reader = new Thread(() => { fresh = ScheduleFlowChecks.WaitMs; }) { IsBackground = true };
            reader.Start();
            bool read = reader.Join(10000);
            check(P + "L5 a thread that never touched the checks reads the default wait of " + savedWait + " ms (an initialiser on a thread-static field gives 0 on every thread but the first) (" + fresh + ")",
                read && fresh == savedWait);
        }
    }
}
