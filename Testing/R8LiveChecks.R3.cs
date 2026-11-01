using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8, builder "live", third part (after the second review; predictions: tests\evidence\liveclash\r8-live\PREDICTIONS-3.md, a13e9f9):
    /// the identity test of fix B (R2-3) and the rule for an element that vanished (M-2 / P9-1). Revit-free.
    /// </summary>
    public static partial class R8LiveChecks
    {
        /// <summary>A stand-in for a Revit Document wrapper: two instances with the same Id are Equal and are not the same reference.</summary>
        private sealed class Wrapper
        {
            private readonly int _id;
            public Wrapper(int id) { _id = id; }
            public override bool Equals(object obj) { var w = obj as Wrapper; return w != null && w._id == _id; }
            public override int GetHashCode() { return _id; }
        }

        private static void SameObjectByEquals(Action<string, bool?> check)
        {
            var a = new Wrapper(1);
            var a2 = new Wrapper(1);
            var b = new Wrapper(2);
            check(P + "SO-1 [red] two different wrapper objects that are Equal are the same document (a fresh wrapper for it)", SessionGuard.IsSameObject(a, a2));
            check(P + "SO-2 [green] different documents are not the same", !SessionGuard.IsSameObject(a, b));
            check(P + "SO-3 [green] null on either side is not the same, even null with null", !SessionGuard.IsSameObject(null, a) && !SessionGuard.IsSameObject(a, null) && !SessionGuard.IsSameObject(null, null));
            check(P + "SO-4 [green] the same reference is the same", SessionGuard.IsSameObject(a, a));
        }

        private static void VanishedElementRule(Action<string, bool?> check)
        {
            check(P + "UP-1 [red] an element that vanished because the transaction that created it was UNDONE is not a fix", !UndoPolicy.DeletedElementIsAFix(ChangeOrigin.Undone));
            check(P + "UP-2 [red] ...nor one that vanished because a transaction or group that created it was ROLLED BACK", !UndoPolicy.DeletedElementIsAFix(ChangeOrigin.RolledBack));
            check(P + "UP-3 [green] a deletion that is being REDONE is a fix", UndoPolicy.DeletedElementIsAFix(ChangeOrigin.Redone));
            check(P + "UP-4 [green] a deletion in a COMMITTED transaction is a fix", UndoPolicy.DeletedElementIsAFix(ChangeOrigin.Committed));
        }
    }
}
