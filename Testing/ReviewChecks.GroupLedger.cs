using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiveClash.Testing
{
    /// <summary>
    /// The Revit-free rules of <see cref="OpenGroupLedger"/>: a transaction group a check left open is named and rolled back,
    /// and every group is held until then so it cannot be finalized at a random moment (the overnight
    /// ModificationForbiddenException of the merged tree). The groups here are plain stand-ins reached through the ledger's
    /// three delegates. The Revit end (<c>TestTx.NewGroup</c>, <c>TestTx.AuditGroups</c>) is checked in Revit by the
    /// "harness: no check left a TransactionGroup open" lines.
    /// </summary>
    public static class GroupLedgerChecks
    {
        private const string P = "harness ledger: ";

        private sealed class FakeGroup
        {
            public bool Open = true;
            public bool Disposed;
            public bool CannotEnd;
            public readonly string Name;
            public FakeGroup(string name) { Name = name; }
        }

        private static FakeGroup Track(OpenGroupLedger ledger, string name, List<string> endedOrder = null)
        {
            var g = new FakeGroup(name);
            ledger.Track(name, () => g.Open,
                () =>
                {
                    if (g.CannotEnd) throw new InvalidOperationException("an open child group");
                    g.Open = false;
                    if (endedOrder != null) endedOrder.Add(name);
                },
                () => { g.Disposed = true; });
            return g;
        }

        public static void RunAll(Action<string, bool?> check)
        {
            // ---- a group left open is named, and ended
            var a = new OpenGroupLedger();
            var left = Track(a, "left open");
            var leaks = a.EndLeaked();
            check(P + "a group left open is named and rolled back (" + string.Join(",", leaks.ToArray()) + ")",
                leaks.Count == 1 && leaks[0] == "left open" && !left.Open);

            // ---- a group that ended is not reported
            var b = new OpenGroupLedger();
            var done = Track(b, "ended");
            done.Open = false;
            check(P + "a group that ended is not reported", b.EndLeaked().Count == 0);

            // ---- innermost first
            var c = new OpenGroupLedger();
            var order = new List<string>();
            Track(c, "outer", order);
            Track(c, "inner", order);
            var named = c.EndLeaked();
            check(P + "the innermost open group is ended first (" + string.Join(",", order.ToArray()) + ")",
                order.Count == 2 && order[0] == "inner" && order[1] == "outer" && named.Count == 2 && named[0] == "inner");

            // ---- one that cannot be ended does not stop the rest
            var d = new OpenGroupLedger();
            var first = Track(d, "first");
            var stuck = Track(d, "stuck");
            stuck.CannotEnd = true;
            var reasons = d.EndLeaked();
            check(P + "a group that cannot be ended is still named, with the reason, and the others are ended (" + string.Join("|", reasons.ToArray()) + ")",
                reasons.Count == 2 && reasons[0].StartsWith("stuck", StringComparison.Ordinal) && reasons[0].Contains("InvalidOperationException")
                && reasons[1] == "first" && !first.Open);

            // ---- the ledger forgets what it ended, and disposes every group
            var e = new OpenGroupLedger();
            var g1 = Track(e, "one");
            var g2 = Track(e, "two");
            g2.Open = false;
            int before = e.Count;
            e.EndLeaked();
            check(P + "every group is disposed, ended or not, and the ledger is empty afterwards (" + before + " held before)",
                before == 2 && g1.Disposed && g2.Disposed && e.Count == 0 && e.EndLeaked().Count == 0);

            // ---- held until ended: a reference is kept, so the group cannot be finalized behind the run's back
            var f = new OpenGroupLedger();
            var weak = TrackAndForget(f);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            check(P + "a tracked group stays alive although nothing else refers to it", weak.IsAlive);
            f.EndLeaked();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            check(P + "...and can be collected once the ledger has let it go", !weak.IsAlive);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference TrackAndForget(OpenGroupLedger ledger)
        {
            var g = new FakeGroup("forgotten");
            ledger.Track("forgotten", () => g.Open, () => { g.Open = false; }, () => { g.Disposed = true; });
            return new WeakReference(g);
        }
    }
}
