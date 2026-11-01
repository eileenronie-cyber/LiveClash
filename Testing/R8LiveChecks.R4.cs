using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Round 8, builder "live", fourth part (after the delta check; predictions: tests\evidence\liveclash\r8-live\PREDICTIONS-4.md, 47c675c): the memory of
    /// committed deletions (D-3, D-6). Revit-free; the wiring in ClashUpdater is checked in Revit (ReviewChecks.LivePath.R8b.cs).
    /// </summary>
    public static partial class R8LiveChecks
    {
        private static void CommittedDeletionMemory(Action<string, bool?> check)
        {
            var cd = new CommittedDeletions(3);

            check(P + "CD-3 [green] a document nothing was noted for has nothing deleted and has not overflowed",
                cd.Knowledge("A", 1) == DeletionKnowledge.NotDeleted && !cd.Overflowed("A"));

            bool over0 = cd.Note("A", new long[] { 1, 2 });
            check(P + "CD-1 [red] noted ids are remembered for their document, not for another, and an id never noted is not deleted",
                !over0 && cd.Knowledge("A", 1) == DeletionKnowledge.Deleted && cd.Knowledge("A", 2) == DeletionKnowledge.Deleted
                && cd.Knowledge("A", 9) == DeletionKnowledge.NotDeleted && cd.Knowledge("B", 1) == DeletionKnowledge.NotDeleted);

            // The cap is 3: a third and fourth id make four. The call that overflows says so, once; from then on nothing is known.
            bool over1 = cd.Note("A", new long[] { 3, 4 });
            bool over2 = cd.Note("A", new long[] { 5 });
            check(P + "CD-2 [red] past the cap the document is overflowed (said once), every id is then Unknown - even a noted one - and another document is unaffected",
                over1 && !over2 && cd.Overflowed("A")
                && cd.Knowledge("A", 1) == DeletionKnowledge.Unknown && cd.Knowledge("A", 99) == DeletionKnowledge.Unknown
                && !cd.Overflowed("B") && cd.Knowledge("B", 1) == DeletionKnowledge.NotDeleted);

            cd.Note("C", new long[] { 7 });
            bool before = cd.Knowledge("C", 7) == DeletionKnowledge.Deleted;
            cd.Forget("C");
            cd.Forget("A");
            check(P + "CD-4 [red] Forget empties that document's memory and its overflow, and leaves another's (before " + before + ")",
                before && cd.Knowledge("C", 7) == DeletionKnowledge.NotDeleted && !cd.Overflowed("A") && cd.Knowledge("A", 1) == DeletionKnowledge.NotDeleted);

            var r = new CommittedDeletions(100);
            r.Note("A", new long[] { 1, 2 });
            r.Rekey("A", "B");
            check(P + "CD-5 [red] Rekey moves the memory to the new key and leaves nothing under the old one",
                r.Knowledge("B", 1) == DeletionKnowledge.Deleted && r.Knowledge("B", 2) == DeletionKnowledge.Deleted && r.Knowledge("A", 1) == DeletionKnowledge.NotDeleted);

            var m = new CommittedDeletions(100);
            m.Note("A", new long[] { 1, 2 });
            m.Note("B", new long[] { 7 });
            m.Rekey("A", "B");
            check(P + "CD-6 [red] Rekey into a key that already has a memory keeps BOTH (the union), not only the one it found there (D-6)",
                m.Knowledge("B", 1) == DeletionKnowledge.Deleted && m.Knowledge("B", 2) == DeletionKnowledge.Deleted && m.Knowledge("B", 7) == DeletionKnowledge.Deleted
                && m.Knowledge("A", 1) == DeletionKnowledge.NotDeleted);

            var o = new CommittedDeletions(2);
            o.Note("A", new long[] { 1, 2, 3 });           // overflows
            o.Note("B", new long[] { 7 });
            o.Rekey("A", "B");                              // an overflow follows the document and stays
            var n = new CommittedDeletions(100);
            n.Note("B", new long[] { 7 });
            n.Rekey("Z", "B");                              // nothing under Z
            n.Rekey("B", "B");                              // onto itself
            // CD-8: the edges. Nothing throws and nothing is invented for a missing key; a merge into an overflowed document keeps it overflowed; a
            // merge that would pass the cap overflows.
            var e = new CommittedDeletions(3);
            bool noThrow = true;
            try
            {
                e.Note(null, new long[] { 1 }); e.Note("A", null); e.Forget(null);
                e.Rekey(null, "B"); e.Rekey("A", null); e.Rekey("", "B"); e.Rekey("A", "");
            }
            catch (Exception) { noThrow = false; }
            var intoOver = new CommittedDeletions(2);
            intoOver.Note("B", new long[] { 1, 2, 3 });     // overflowed
            intoOver.Note("A", new long[] { 9 });
            intoOver.Rekey("A", "B");
            var pastCap = new CommittedDeletions(3);
            pastCap.Note("A", new long[] { 1, 2 });
            pastCap.Note("B", new long[] { 3, 4 });
            pastCap.Rekey("A", "B");                         // 4 ids would pass the cap of 3
            check(P + "CD-8 [green] null or empty keys and lists throw nothing and invent nothing; a merge into an overflowed document stays overflowed; a merge past the cap overflows",
                noThrow && !e.Note(null, new long[] { 1 }) && !e.Note("A", null) && !e.Overflowed(null) && e.Knowledge(null, 1) == DeletionKnowledge.NotDeleted
                && intoOver.Overflowed("B") && !intoOver.Overflowed("A") && intoOver.Knowledge("B", 9) == DeletionKnowledge.Unknown
                && pastCap.Overflowed("B") && !pastCap.Overflowed("A"));

            check(P + "CD-7 [red] an overflow moves with the memory and survives a merge; a Rekey from an unknown key, or onto itself, changes nothing",
                o.Overflowed("B") && !o.Overflowed("A") && o.Knowledge("B", 7) == DeletionKnowledge.Unknown
                && n.Knowledge("B", 7) == DeletionKnowledge.Deleted && !n.Overflowed("B"));
        }
    }
}
