using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// "What changed" after a link change: the comparison of the clash list from before with the one after
    /// (Core\ClashChange.cs) and the state machine around it (Core\ClashChangeTracker.cs) - which baseline is kept, what a
    /// recheck may and may not say, what dismissing and switching model do, and what the summary reads like.
    /// Revit-free, so both tiers run it. It touches the same static state the live add-in uses, so it saves it first and
    /// puts it back (LinkWatch.Snapshot now carries the tracker's state too).
    /// </summary>
    public static class ClashChangeChecks
    {
        private static ClashResult R(string key, string model, ClashSeverity sev = ClashSeverity.Hard)
        {
            return new ClashResult { Key = key, ModelName = model, Severity = sev, HostElementName = "Pipe " + key, OtherElementName = "Wall " + key };
        }

        public static void RunAll(Action<string, bool?> check)
        {
            var saved = LinkWatch.Snapshot();
            try
            {
                Compare(check);
                Tracker(check);
                MutationImmunity(check);
                Announces(check);
                Wording(check);
                Wiring(check);
            }
            finally { LinkWatch.Restore(saved); }
        }

        private static void Compare(Action<string, bool?> check)
        {
            var before = new[] { R("a", "Structure"), R("b", "Structure"), R("c", "Structure", ClashSeverity.Hard), R("host", "Current model") };
            var after = new[] { R("b", "Structure"), R("c", "Structure", ClashSeverity.Clearance), R("d", "Structure"), R("host2", "Current model") };
            var set = ClashChange.Compare(before, after, new[] { "Structure" });

            check("what changed: a pair only in the later list is new, only in the earlier is cleared",
                set.New.Select(r => r.Key).SequenceEqual(new[] { "d" }) && set.Cleared.Select(r => r.Key).SequenceEqual(new[] { "a" }) && set.NewKeys.Contains("d"));
            check("what changed: a pair in both at a different severity is changed, not new or cleared",
                set.SeverityChanged.Count == 1 && set.SeverityChanged[0].Key.Key == "c" && set.SeverityChanged[0].Key.Severity == ClashSeverity.Hard
                && set.SeverityChanged[0].Value.Severity == ClashSeverity.Clearance);
            check("what changed: a pair in both at the same severity is just counted", set.Unchanged == 1 && !set.IsEmpty);
            check("what changed: it says how many rows it compared on each side (after the link restriction)", set.BeforeInScope == 3 && set.AfterInScope == 3
                && ClashChange.Compare(before, after, new[] { "No such link" }).BeforeInScope == 0);
            check("what changed: only the changed link's rows are compared - clashes in your own model are not the link's doing",
                !set.New.Any(r => r.Key == "host2") && !set.Cleared.Any(r => r.Key == "host"));
            var all = ClashChange.Compare(before, after, null);
            check("what changed: with no model named, every row is compared", all.New.Count == 2 && all.Cleared.Count == 2);
            var norm = ClashChange.Compare(new[] { R("a", "Structure.rvt") }, new[] { R("z", "structure") }, new[] { "STRUCTURE" });
            check("what changed: a link name is compared without .rvt and without regard to case", norm.Cleared.Count == 1 && norm.New.Count == 1);
            var odd = ClashChange.Compare(new[] { R("", "S"), R(null, "S"), R("k", "S"), R("k", "S") }, new[] { R("k", "S"), null }, null);
            check("what changed: a row with no key or no row is ignored, and a key seen twice counts once", odd.New.Count == 0 && odd.Cleared.Count == 0 && odd.Unchanged == 1);
            check("what changed: two empty lists change nothing", ClashChange.Compare(null, null, null).IsEmpty && ClashChange.Compare(new ClashResult[0], new ClashResult[0], new[] { "x" }).IsEmpty);
            check("what changed: an unchanged list is empty of changes",
                ClashChange.Compare(before, before, new[] { "Structure" }).IsEmpty);
        }

        private static void Tracker(Action<string, bool?> check)
        {
            var list = new List<ClashResult> { R("a", "Structure"), R("b", "Structure") };
            int asked = 0;
            int changed = 0;
            Action onChanged = () => changed++;
            ClashChangeTracker.Changed += onChanged;
            try
            {
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure");
                check("what changed: with no list to snapshot, nothing is tracked", ClashChangeTracker.Pending == null && changed == 0);

                ClashChangeTracker.SnapshotSource = () => { asked++; return new List<ClashResult>(list); };
                ClashChangeTracker.Complete(list, true);
                check("what changed: a check with no link change waiting says nothing", ClashChangeTracker.Current == null);

                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure.rvt");
                var p = ClashChangeTracker.Pending;
                check("what changed: the first link change takes the baseline - the list as it stood, and the link's name without .rvt",
                    p != null && p.Before.Count == 2 && p.LinkNames.SequenceEqual(new[] { "Structure" }) && asked == 1);

                list.Add(R("c", "Structure"));     // the list moves on (live edits) ...
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceMoved, "Architecture");
                check("what changed: a second change keeps the FIRST baseline and adds its link (the list is not re-read)",
                    ClashChangeTracker.Pending.Before.Count == 2 && ClashChangeTracker.Pending.LinkNames.SequenceEqual(new[] { "Structure", "Architecture" }) && asked == 1);
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "structure");
                check("what changed: the same link twice is one link", ClashChangeTracker.Pending.LinkNames.Count == 2);

                var after = new List<ClashResult> { R("b", "Structure"), R("n1", "Structure"), R("n2", "Architecture"), R("mine", "Current model") };
                ClashChangeTracker.Complete(after, false);
                var s1 = ClashChangeTracker.Current;
                check("what changed: a recheck gives a provisional summary and keeps waiting for a full check",
                    s1 != null && !s1.AfterFullCheck && ClashChangeTracker.Pending != null && s1.Set.New.Count == 2 && s1.Set.Cleared.Count == 1
                    && s1.Caveat.Length > 0);
                check("what changed: a row asks whether it is new (and one that is not, or has no key, is not)",
                    ClashChangeTracker.IsNew("n1") && ClashChangeTracker.IsNew("n2") && !ClashChangeTracker.IsNew("b") && !ClashChangeTracker.IsNew("mine") && !ClashChangeTracker.IsNew(null));

                ClashChangeTracker.Complete(after, true);
                var s2 = ClashChangeTracker.Current;
                check("what changed: a full check gives the final summary, replaces the provisional one, and ends the wait",
                    s2 != null && s2.AfterFullCheck && !ReferenceEquals(s1, s2) && ClashChangeTracker.Pending == null && s2.Caveat.Length == 0);

                // Nothing waiting: a recheck leaves an old summary alone, but a FULL check has replaced the list it described.
                ClashChangeTracker.Complete(after, false);
                bool kept = ReferenceEquals(ClashChangeTracker.Current, s2);
                ClashChangeTracker.Complete(after, true);
                check("what changed: a recheck with nothing waiting leaves the old summary; a full check with nothing waiting puts it away",
                    kept && ClashChangeTracker.Current == null);

                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Mech");
                ClashChangeTracker.Complete(after, true);
                bool had = ClashChangeTracker.Current != null;
                ClashChangeTracker.DismissSummary();
                check("what changed: dismissing the summary puts it away", had && ClashChangeTracker.Current == null);

                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure");
                ClashChangeTracker.Complete(after, false);
                ClashChangeTracker.Reset();
                check("what changed: switching model forgets both the wait and the summary", ClashChangeTracker.Pending == null && ClashChangeTracker.Current == null);

                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "Structure");
                bool allGone1 = ClashChangeTracker.Pending.AllGone;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceDeleted, "Architecture");
                bool allGone2 = ClashChangeTracker.Pending.AllGone;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure");
                check("what changed: while every change is an unload or a delete the clashes are 'gone', and one reload says otherwise",
                    allGone1 && allGone2 && !ClashChangeTracker.Pending.AllGone);
            }
            finally
            {
                ClashChangeTracker.Changed -= onChanged;
                ClashChangeTracker.Restore(null);
            }
        }

        /// <summary>
        /// ClashResultStore.UpdateSeverity mutates a row's Severity IN PLACE on the same object the
        /// live store holds (found reviewing the whole codebase, 2026-09-27) - so if the baseline
        /// held that exact object, a background severity change made while the baseline is pending
        /// would silently rewrite "before" to already match "after", and Complete() would never see
        /// the change. NoteLinkChange must clone what SnapshotSource hands it.
        /// </summary>
        private static void MutationImmunity(Action<string, bool?> check)
        {
            try
            {
                ClashChangeTracker.Restore(null);
                var live = R("a", "Structure", ClashSeverity.Hard);
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult> { live };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure");

                // Exactly what UpdateSeverity does to a live row once the baseline has been taken.
                live.Severity = ClashSeverity.Clearance;

                check("what changed: the baseline is immune to a later in-place mutation of the row it was taken from",
                    ClashChangeTracker.Pending.Before[0].Severity == ClashSeverity.Hard);

                var after = new List<ClashResult> { R("a", "Structure", ClashSeverity.Clearance) };
                ClashChangeTracker.Complete(after, true);
                var s = ClashChangeTracker.Current;
                check("what changed: ...so a real severity change during the wait is still reported, not masked",
                    s != null && s.Set.SeverityChanged.Count == 1 && s.Set.SeverityChanged[0].Key.Key == "a");
            }
            finally { ClashChangeTracker.Restore(null); }
        }

        /// <summary>
        /// LinkWatch records a notice for every modified link instance, and dragging one fires many. The panel rebuilds its
        /// whole list each time the tracker announces, so it must announce only when something actually changed.
        /// </summary>
        private static void Announces(Action<string, bool?> check)
        {
            int changed = 0;
            Action on = () => changed++;
            ClashChangeTracker.Changed += on;
            try
            {
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>();
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceMoved, "S");
                int first = changed;
                for (int i = 0; i < 5; i++) ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceMoved, "S");
                int repeats = changed;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceMoved, "T");
                int newLink = changed;
                check("what changed: the first change announces itself, the same link moving again announces nothing, a new link does ("
                      + first + ", " + repeats + ", " + newLink + ")", first == 1 && repeats == 1 && newLink == 2);

                ClashChangeTracker.Restore(null);
                changed = 0;
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>();
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "S");
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "S");
                int gone = changed;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "S");
                int flipped = changed;
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "S");
                check("what changed: ...and a reload after an unload announces, because it changes what the summary will say ("
                      + gone + ", " + flipped + ", then " + changed + ")", gone == 1 && flipped == 2 && changed == 2);
            }
            finally
            {
                ClashChangeTracker.Changed -= on;
                ClashChangeTracker.Restore(null);
            }
        }

        private static void Wording(Action<string, bool?> check)
        {
            Func<int, int, int, bool, List<string>, bool, ClashChangeTracker.Summary> make = (nNew, nCleared, nSev, full, names, gone) =>
            {
                var set = new ClashChangeSet { BeforeInScope = 5, AfterInScope = 5 };     // something was compared
                for (int i = 0; i < nNew; i++) set.New.Add(R("n" + i, "S"));
                for (int i = 0; i < nCleared; i++) set.Cleared.Add(R("c" + i, "S"));
                for (int i = 0; i < nSev; i++) set.SeverityChanged.Add(new KeyValuePair<ClashResult, ClashResult>(R("s" + i, "S"), R("s" + i, "S", ClashSeverity.Clearance)));
                return new ClashChangeTracker.Summary { Set = set, LinkNames = names, AfterFullCheck = full, AllGone = gone };
            };
            var one = new List<string> { "Structure" };

            check("what changed: the line names the link and the counts (\"" + make(5, 3, 0, true, one, false).Headline + "\")",
                make(5, 3, 0, true, one, false).Headline == "Since Structure changed: 5 new, 3 cleared.");
            check("what changed: a count of nothing is left out, and severity changes are counted",
                make(0, 2, 0, true, one, false).Headline == "Since Structure changed: 2 cleared."
                && make(1, 0, 4, true, one, false).Headline == "Since Structure changed: 1 new, 4 changed severity.");
            check("what changed: no difference says so", make(0, 0, 0, true, one, false).Headline == "Since Structure changed: no change to the clashes with it.");
            var nothing = make(0, 0, 0, true, one, false);
            nothing.Set.BeforeInScope = 0; nothing.Set.AfterInScope = 0;
            check("what changed: a comparison that saw no clashes with the link either time says so, not 'no change' (\"" + nothing.Headline + "\")",
                nothing.Headline == "Since Structure changed: no clashes with it are listed, before or after.");
            check("what changed: two links read as 'A and B', three as a count",
                make(1, 0, 0, true, new List<string> { "A", "B" }, false).Headline == "Since A and B changed: 1 new."
                && make(1, 0, 0, true, new List<string> { "A", "B", "C" }, false).Headline == "Since 3 links changed: 1 new.");
            check("what changed: an unloaded link's clashes are 'gone', not 'cleared'",
                make(0, 3, 0, true, one, true).Headline == "Structure is no longer there: 3 clashes with it left the list."
                && make(0, 1, 0, true, one, true).Headline == "Structure is no longer there: 1 clash with it left the list."
                && make(0, 0, 0, true, one, true).Headline == "Structure is no longer there: it had no clashes listed.");
            check("what changed: a provisional summary carries the caveat, a final one and an unload do not",
                make(1, 0, 0, false, one, false).Caveat.Length > 0 && make(1, 0, 0, true, one, false).Caveat.Length == 0 && make(0, 1, 0, false, one, true).Caveat.Length == 0);
        }

        private static void Wiring(Action<string, bool?> check)
        {
            var list = new List<ClashResult> { R("a", "Structure") };
            var s0 = LinkWatch.Snapshot();
            try
            {
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>(list);

                LinkWatch.RecordLoadedOrReloaded("Structure");
                check("what changed: recording a link notice starts the wait, on the list as it stands",
                    ClashChangeTracker.Pending != null && ClashChangeTracker.Pending.Before.Count == 1 && ClashChangeTracker.Pending.LinkNames.SequenceEqual(new[] { "Structure" }));

                // A recheck keeps the wait. Then the notice is dismissed: that hides a warning, it must not throw away the
                // baseline (the one thing that cannot be rebuilt later) or the summary.
                ClashChangeTracker.Complete(new[] { R("b", "Structure") }, false);
                bool stillWaiting = ClashChangeTracker.Pending != null;
                LinkWatch.Clear();
                check("what changed: dismissing the link notice keeps the baseline and the summary",
                    stillWaiting && LinkWatch.Current == null && ClashChangeTracker.Pending != null && ClashChangeTracker.Current != null);

                LinkWatch.RecordLoadedOrReloaded("Structure");
                LinkWatch.Adopt(new Dictionary<long, string>());
                check("what changed: adopting another model forgets it all", ClashChangeTracker.Pending == null && ClashChangeTracker.Current == null);

                // A test that runs inside a live session must be able to put the real state back.
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>(list);
                LinkWatch.RecordLoadedOrReloaded("Real link");
                var mine = LinkWatch.Snapshot();
                LinkWatch.RecordLoadedOrReloaded("Test link");
                ClashChangeTracker.Complete(new ClashResult[0], true);
                LinkWatch.Restore(mine);
                check("what changed: restoring LinkWatch's state puts the tracker's back too (the real wait, not the test's)",
                    ClashChangeTracker.Pending != null && ClashChangeTracker.Pending.LinkNames.SequenceEqual(new[] { "Real link" }) && ClashChangeTracker.Current == null);
            }
            finally
            {
                LinkWatch.Restore(s0);
                ClashChangeTracker.Restore(null);
            }
        }
    }
}
