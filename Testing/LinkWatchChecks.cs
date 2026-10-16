using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The link-change notice's own logic: what each change is CALLED, how old it reads,
    /// and the latest-wins / forget-on-delete state machine.
    ///
    /// WHY THIS EXISTS. LinkWatch shipped (9c67db1) with no automated checks at all and
    /// was found that way by mutation-testing the clearance work — there was nothing to
    /// mutate. It is exactly the kind of feature that regresses quietly: event-driven,
    /// stateful, and its failure mode is a warning that silently stops appearing.
    ///
    /// These run on the pure half (Core\LinkWatchState.cs), so both tiers run them. They
    /// touch the same static state the live add-in uses, so every check block saves it
    /// first and restores it after — inside a running Revit that is the real notice the
    /// user may be looking at.
    ///
    /// What is NOT here: <c>Observe</c> against real Revit ids, and above all the
    /// unload-vs-load-echo discrimination. That needs a real RevitLinkType, so it lives
    /// in SelfTest.LinkWatchLive.
    /// </summary>
    public static class LinkWatchChecks
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        private static string AgeAfter(TimeSpan elapsed)
        {
            var n = new LinkWatch.Notice { Kind = LinkWatch.Kind.LoadedOrReloaded, LinkName = "x", WhenUtc = T0 };
            return n.AgeTextAt(T0 + elapsed);
        }

        public static void RunAll(Action<string, bool?> check)
        {
            var saved = LinkWatch.Snapshot();
            try
            {
                Wording(check);
                Age(check);
                Trim(check);
                StateMachine(check);
            }
            finally { LinkWatch.Restore(saved); }
        }

        private static void Wording(Action<string, bool?> check)
        {
            // Exhaustive over the enum, not over a list written here: a Kind added later
            // without a verb falls to the "link changed" default, and this is what fails.
            var all = (LinkWatch.Kind[])Enum.GetValues(typeof(LinkWatch.Kind));
            var verbs = new HashSet<string>();
            bool anyDefault = false;
            foreach (var k in all)
            {
                var v = new LinkWatch.Notice { Kind = k }.VerbText;
                if (v == "link changed") anyDefault = true;
                verbs.Add(v);
            }
            check("linkwatch: every Kind has its own verb (none falls to the default)", !anyDefault);
            check("linkwatch: ...and no two Kinds share one", verbs.Count == all.Length);
            check("linkwatch: verbs read as the panel sentence expects",
                new LinkWatch.Notice { Kind = LinkWatch.Kind.LoadedOrReloaded }.VerbText == "link loaded or reloaded"
                && new LinkWatch.Notice { Kind = LinkWatch.Kind.Unloaded }.VerbText == "link unloaded"
                && new LinkWatch.Notice { Kind = LinkWatch.Kind.InstanceAdded }.VerbText == "link added"
                && new LinkWatch.Notice { Kind = LinkWatch.Kind.InstanceMoved }.VerbText == "link moved"
                && new LinkWatch.Notice { Kind = LinkWatch.Kind.InstanceDeleted }.VerbText == "link removed");
            check("linkwatch: an unknown Kind still says something rather than nothing",
                new LinkWatch.Notice { Kind = (LinkWatch.Kind)99 }.VerbText == "link changed");
        }

        private static void Age(Action<string, bool?> check)
        {
            check("linkwatch age: zero elapsed is 'just now'", AgeAfter(TimeSpan.Zero) == "just now");
            check("linkwatch age: 59 s is still 'just now'", AgeAfter(TimeSpan.FromSeconds(59)) == "just now");
            check("linkwatch age: exactly 1 min flips to minutes", AgeAfter(TimeSpan.FromMinutes(1)) == "1 min ago");
            check("linkwatch age: 59 min 59 s is still minutes",
                AgeAfter(new TimeSpan(0, 59, 59)) == "59 min ago");
            check("linkwatch age: exactly 1 hr flips to hours", AgeAfter(TimeSpan.FromHours(1)) == "1 hr ago");
            check("linkwatch age: 23 hr 59 min is still hours",
                AgeAfter(new TimeSpan(23, 59, 0)) == "23 hr ago");
            check("linkwatch age: exactly 24 hr flips to days, singular", AgeAfter(TimeSpan.FromHours(24)) == "1 day ago");
            check("linkwatch age: 47 hr is still '1 day'", AgeAfter(TimeSpan.FromHours(47)) == "1 day ago");
            check("linkwatch age: 48 hr is '2 days', plural", AgeAfter(TimeSpan.FromHours(48)) == "2 days ago");
            check("linkwatch age: a timestamp in the future (clock skew) reads 'just now', not negative",
                AgeAfter(TimeSpan.FromMinutes(-30)) == "just now");
        }

        private static void Trim(Action<string, bool?> check)
        {
            check("linkwatch trim: strips a trailing .rvt", LinkWatch.TrimRvt("HVAC.rvt") == "HVAC");
            check("linkwatch trim: ...case-insensitively", LinkWatch.TrimRvt("HVAC.RVT") == "HVAC");
            check("linkwatch trim: leaves a name without it alone", LinkWatch.TrimRvt("HVAC") == "HVAC");
            check("linkwatch trim: does not eat a name that merely CONTAINS .rvt",
                LinkWatch.TrimRvt("a.rvt.bak") == "a.rvt.bak");
            check("linkwatch trim: strips only ONE suffix", LinkWatch.TrimRvt("x.rvt.rvt") == "x.rvt");
            check("linkwatch trim: a bare '.rvt' becomes empty, not a crash", LinkWatch.TrimRvt(".rvt") == "");
        }

        private static void StateMachine(Action<string, bool?> check)
        {
            // ---- latest wins ---------------------------------------------------------
            LinkWatch.Adopt(new Dictionary<long, string>());
            LinkWatch.RecordLoadedOrReloaded("Alpha");
            LinkWatch.RecordUnloaded("Beta");
            var cur = LinkWatch.Current;
            check("linkwatch state: a second change REPLACES the first rather than queuing",
                cur != null && cur.LinkName == "Beta" && cur.Kind == LinkWatch.Kind.Unloaded);

            LinkWatch.RecordLoadedOrReloaded(null);
            check("linkwatch state: a null link name reads 'A link'", LinkWatch.Current.LinkName == "A link");
            LinkWatch.RecordLoadedOrReloaded("");
            check("linkwatch state: ...and so does an empty one", LinkWatch.Current.LinkName == "A link");

            var before = DateTime.UtcNow;
            LinkWatch.RecordLoadedOrReloaded("Stamp");
            var w = LinkWatch.Current.WhenUtc;
            check("linkwatch state: a notice is stamped with now (UTC)",
                w >= before.AddSeconds(-1) && w <= DateTime.UtcNow.AddSeconds(1));

            // ---- Clear vs Adopt ------------------------------------------------------
            LinkWatch.Adopt(new Dictionary<long, string> { { 5, "Kept" } });
            LinkWatch.RecordLoadedOrReloaded("X");
            LinkWatch.Clear();
            check("linkwatch state: Clear removes the notice", LinkWatch.Current == null);
            check("linkwatch state: ...but keeps the name map (a dismissed notice must not forget a link)",
                LinkWatch.KnownName(5) == "Kept");

            LinkWatch.RecordLoadedOrReloaded("Y");
            LinkWatch.Adopt(new Dictionary<long, string> { { 9, "Fresh" } });
            check("linkwatch state: Adopt (a new model) clears the notice too", LinkWatch.Current == null);
            check("linkwatch state: ...and REPLACES the map, so the old model's ids are forgotten",
                LinkWatch.KnownName(5) == null && LinkWatch.KnownName(9) == "Fresh");
            LinkWatch.Adopt(null);
            check("linkwatch state: Adopt(null) is an empty map, not a crash", LinkWatch.KnownName(9) == null);

            // ---- instances: add, move, delete ---------------------------------------
            LinkWatch.Adopt(new Dictionary<long, string>());
            LinkWatch.NoteInstance(10, "Struct", false);
            check("linkwatch instance: a new instance is reported as added",
                LinkWatch.Current.Kind == LinkWatch.Kind.InstanceAdded && LinkWatch.Current.LinkName == "Struct");
            LinkWatch.NoteInstance(10, "Struct", true);
            check("linkwatch instance: a modified one is reported as moved",
                LinkWatch.Current.Kind == LinkWatch.Kind.InstanceMoved);
            LinkWatch.NoteInstance(10, "Structure", true);
            check("linkwatch instance: re-learning an id picks up a renamed link type",
                LinkWatch.KnownName(10) == "Structure");

            // The reason the map exists: by the time the delete arrives the element is
            // gone and cannot be asked its name.
            LinkWatch.Clear();
            bool named = LinkWatch.NoteDeleted(10);
            check("linkwatch delete: a link learned earlier is still NAMED once it is deleted",
                named && LinkWatch.Current.Kind == LinkWatch.Kind.InstanceDeleted
                && LinkWatch.Current.LinkName == "Structure");
            check("linkwatch delete: ...and is forgotten afterwards", LinkWatch.KnownName(10) == null);

            LinkWatch.Clear();
            bool again = LinkWatch.NoteDeleted(10);
            check("linkwatch delete: deleting it a second time reports nothing", !again && LinkWatch.Current == null);

            LinkWatch.Adopt(new Dictionary<long, string> { { 1, "One" }, { 2, "Two" } });
            bool unknown = LinkWatch.NoteDeleted(999);
            check("linkwatch delete: an id that was never a link produces NO notice",
                !unknown && LinkWatch.Current == null);
            LinkWatch.NoteDeleted(1);
            check("linkwatch delete: deleting one link leaves the others named",
                LinkWatch.KnownName(2) == "Two" && LinkWatch.KnownName(1) == null);

            // ---- Snapshot/Restore is what protects a live session ---------------------
            LinkWatch.Adopt(new Dictionary<long, string> { { 3, "Live" } });
            LinkWatch.RecordLoadedOrReloaded("LiveNotice");
            var snap = LinkWatch.Snapshot();
            LinkWatch.Adopt(new Dictionary<long, string>());
            LinkWatch.Restore(snap);
            check("linkwatch state: Snapshot/Restore puts back the notice and the map",
                LinkWatch.Current != null && LinkWatch.Current.LinkName == "LiveNotice"
                && LinkWatch.KnownName(3) == "Live");
        }
    }
}
