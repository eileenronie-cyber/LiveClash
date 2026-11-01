using System;
using System.Collections.Generic;

namespace LiveClash.Testing
{
    /// <summary>
    /// Every transaction group a check starts, held until the run says it is done with them, so none can be finalized by
    /// accident and none can stay open unnoticed.
    ///
    /// WHY (the overnight failure on the merged tree, 2026-10-31). A check started a <c>TransactionGroup</c> and never ended
    /// it. Nothing referred to the object any more, so .NET finalized it at an unpredictable moment, and Revit's own
    /// <c>purgeReleasedAPIObjects</c> then ran the group's native destructor, which rolls it back, while the next API call
    /// was raising DocumentChanged: "ModificationForbiddenException: Illegal attempt to modify document. Reason: Raising
    /// DocumentChanged event". It surfaced in whichever check happened to be running (the log shows it in
    /// FocusHandler's transaction in one run and in the link-undo check in another), so the same code failed in one run
    /// and passed in the next. The stack ends in <c>MostlyDeadType&lt;TransactionGroup&gt;.{dtor}</c>.
    ///
    /// WHAT. <see cref="Track"/> keeps a strong reference to each group (so it can never be finalized behind a check's
    /// back), and <see cref="EndLeaked"/> - called at known points, never inside an event - names every group still open,
    /// rolls it back (the innermost first), disposes every group and forgets them. Pure: the group is reached through three
    /// delegates, so the rules are asserted headlessly. The Revit end of it is <c>TestTx.NewGroup</c> and
    /// <c>TestTx.AuditGroups</c>.
    /// </summary>
    internal sealed class OpenGroupLedger
    {
        /// <summary>How many groups are held.</summary>
        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>Hold a group. <paramref name="isOpen"/> says whether it is started and not ended, <paramref name="end"/>
        /// rolls it back, <paramref name="dispose"/> releases it.</summary>
        public void Track(string name, Func<bool> isOpen, Action end, Action dispose)
        {
            lock (_gate) _entries.Add(new Entry { Name = name, IsOpen = isOpen, End = end, Dispose = dispose });
        }

        /// <summary>
        /// The groups that were still open, named (with the reason when one could not be rolled back); each is rolled back,
        /// innermost (last started) first. Every held group is then disposed and forgotten, open or not. A group whose
        /// state cannot be read counts as ended.
        /// </summary>
        public List<string> EndLeaked()
        {
            Entry[] held;
            lock (_gate)
            {
                held = _entries.ToArray();
                _entries.Clear();
            }

            var leaks = new List<string>();
            for (int i = held.Length - 1; i >= 0; i--)   // the last started is the innermost
            {
                var e = held[i];
                bool open;
                try { open = e.IsOpen(); }
                catch { open = false; }   // unreadable: not reported as open, only released

                if (open)
                {
                    try
                    {
                        e.End();
                        leaks.Add(e.Name);
                    }
                    catch (Exception ex)
                    {
                        leaks.Add(e.Name + " (could not be rolled back: " + ex.GetType().Name + ")");
                    }
                }

                try { e.Dispose(); }
                catch { /* a group Revit already let go of; nothing to release */ }
            }
            return leaks;
        }

        private sealed class Entry
        {
            public string Name;
            public Func<bool> IsOpen;
            public Action End;
            public Action Dispose;
        }

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
    }
}
