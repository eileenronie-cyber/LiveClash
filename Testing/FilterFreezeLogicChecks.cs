using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// U1 (Filter froze Revit), the Revit-free half. Both tiers run these (SharedChecks). The in-Revit half, which presses
    /// Filter and times Revit's UI thread, is Testing\FilterFreezeChecks.cs.
    ///
    /// THE CONTRACTS THESE PIN (written first; the stubs they were seen red against are in git history):
    ///
    ///   WorkPump         runs named steps in order, each given what is left of the tick's budget (at least 1 ms), stops once the budget is
    ///                    spent, always runs at least one step call per tick, reports progress as finished steps / steps, records the FIRST
    ///                    error as "name: message" and carries on with the next step, and does nothing after Cancel.
    ///   SlicedPass       the resumable walk over a list of documents the category count is built on: every item once, in order, whatever
    ///                    the budget; a document that will not open is skipped; a walk that throws part-way is Failed (never a half answer);
    ///                    its SIGNATURE is fixed at construction, so what it finds is filed under the document set it actually walked.
    ///   SignedCache      one entry per signature (not one slot): a lookup under a different document set misses.
    ///   FilterPrep       what the window shows while the work is still arriving: an unknown count NEVER hides a category (under-reporting
    ///                    is the failure that must not happen), the status text, and the parameter names an incoming batch adds (never removes).
    /// </summary>
    public static class FilterFreezeLogicChecks
    {
        private const string P = "filter prep: ";

        private static readonly string[] KnownWF = { "Workset", "Foo" };
        private static readonly string[] BatchMix = { "Mark", "foo", "MARK", "", null, "Comments" };
        private static readonly string[] KnownAB = { "A", "B" };
        private static readonly string[] BatchBA = { "b", "a" };
        private static readonly string[] KnownMy = { "MyTypedName" };
        private static readonly string[] BatchMark = { "Mark" };
        private static readonly string[] KnownA = { "A" };
        private static readonly string[] DocsABC = { "A", "B", "C" };
        private static readonly string[] DocsAN = { "A", "N" };
        private static readonly string[] DocsAC = { "A", "C" };
        private static readonly string[] DocsACD = { "A", "C", "D" };

        public static void RunAll(Action<string, bool?> check)
        {
            Pump(check);
            PumpEdges(check);
            Pass(check);
            Cache(check);
            Visibility(check);
            Status(check);
            Names(check);
            RuleScope(check);
            PassFacts(check);
            PumpFacts(check);
            StatusFacts(check);
            StrykerRound1(check);
        }

        // ------------------------------------------------------------------------------ WorkPump

        private static void Pump(Action<string, bool?> check)
        {
            long t = 0;
            var calls = new List<string>();
            var budgets = new List<int>();
            int a = 0, b = 0;
            var pump = new WorkPump(() => t);
            pump.Add("A", bud => { calls.Add("A"); budgets.Add(bud); t += 10; return ++a % 3 == 0; });
            pump.Add("B", bud => { calls.Add("B"); budgets.Add(bud); t += 10; return ++b % 3 == 0; });

            check(P + "a new pump with two steps is not done, is at fraction 0 and is on step A",
                !pump.IsDone && pump.Fraction == 0.0 && pump.CurrentName == "A" && pump.Ticks == 0 && pump.Error == null);

            bool done1 = pump.Tick(25);
            check(P + "a tick runs steps in order until its budget is spent: A three times (30 ms), B not yet",
                !done1 && string.Join("", calls.ToArray()) == "AAA" && !pump.IsDone);
            check(P + "each call is given what is left of the tick's budget (25, 15, 5)",
                budgets.Count == 3 && budgets[0] == 25 && budgets[1] == 15 && budgets[2] == 5);
            check(P + "with A finished the pump is at fraction 0.5 and on B",
                Math.Abs(pump.Fraction - 0.5) < 1e-9 && pump.CurrentName == "B" && pump.Ticks == 1);

            bool done2 = pump.Tick(25);
            check(P + "the next tick resumes where the last stopped (B three times) and the pump is done",
                done2 && string.Join("", calls.ToArray()) == "AAABBB" && pump.IsDone && pump.Ticks == 2);
            check(P + "a finished pump is at fraction 1, on no step, with no error",
                pump.Fraction == 1.0 && pump.CurrentName == null && pump.Error == null);

            int before = calls.Count;
            bool again = pump.Tick(100);
            check(P + "a tick on a finished pump runs nothing and says done", again && calls.Count == before && pump.Ticks == 2);

            // a tiny budget still makes progress: one call per tick
            long t2 = 0; int n = 0; var perTick = new List<int>();
            var slow = new WorkPump(() => t2);
            slow.Add("S", bud => { t2 += 10; n++; return n == 5; });
            int ticks = 0;
            while (!slow.IsDone && ticks < 50)
            {
                int was = n; slow.Tick(1); ticks++; perTick.Add(n - was);
            }
            check(P + "a 1 ms budget still makes progress: exactly one call per tick, five ticks for a five-call step",
                slow.IsDone && ticks == 5 && perTick.All(x => x == 1));

            // a zero budget is treated as 1 ms, never as "do nothing forever"
            long t3 = 0; var seen = new List<int>();
            var zero = new WorkPump(() => t3);
            zero.Add("Z", bud => { seen.Add(bud); t3 += 1; return true; });
            zero.Tick(0);
            check(P + "a 0 ms budget runs one call and gives it a budget of at least 1", zero.IsDone && seen.Count == 1 && seen[0] >= 1);

            // progress never goes backwards and ends at exactly 1
            long t4 = 0; var rnd = new Random(7); var need = new[] { 3, 1, 4, 1, 5 }; var made = new int[need.Length];
            var prog = new WorkPump(() => t4);
            for (int i = 0; i < need.Length; i++) { int k = i; prog.Add("S" + i, bud => { t4 += 3; return ++made[k] >= need[k]; }); }
            double last = -1; bool monotonic = true; int guard = 0;
            while (!prog.IsDone && guard++ < 1000) { prog.Tick(rnd.Next(1, 12)); if (prog.Fraction < last) monotonic = false; last = prog.Fraction; }
            check(P + "progress never goes backwards over a run of ticks, every step's work is done (" + made.Sum() + " of " + need.Sum() + " calls), and it ends at exactly 1",
                monotonic && prog.IsDone && prog.Fraction == 1.0 && made.Sum() == need.Sum() && guard > 1);
        }

        private static void PumpEdges(Action<string, bool?> check)
        {
            // an error in one step is recorded (the first one, with the step's name) and the next step still runs
            long t = 0; bool yRan = false, zRan = false;
            var pump = new WorkPump(() => t);
            pump.Add("X", bud => { t++; throw new InvalidOperationException("boom"); });
            pump.Add("Y", bud => { t++; yRan = true; return true; });
            pump.Add("Z", bud => { t++; zRan = true; throw new InvalidOperationException("second"); });
            pump.Tick(1000);
            check(P + "a step that throws is recorded by name with its message, and the steps after it still run",
                pump.IsDone && yRan && zRan && pump.Error != null && pump.Error.StartsWith("X: ", StringComparison.Ordinal) && pump.Error.Contains("boom"));
            check(P + "only the FIRST error is kept (a later one does not replace it) and the pump still ends at fraction 1",
                pump.Error != null && !pump.Error.Contains("second") && pump.Fraction == 1.0);

            // Cancel stops everything
            long t2 = 0; int c = 0;
            var cp = new WorkPump(() => t2);
            cp.Add("C", bud => { t2 += 10; c++; return false; });
            cp.Tick(1);
            int atCancel = c;
            cp.Cancel();
            bool r = cp.Tick(1000);
            check(P + "after Cancel the pump is done and a tick runs nothing", cp.IsDone && r && c == atCancel && atCancel == 1);

            // an empty pump is done at once
            var empty = new WorkPump(() => 0);
            check(P + "a pump with no steps is done at once, at fraction 1, and a tick says so",
                empty.IsDone && empty.Fraction == 1.0 && empty.CurrentName == null && empty.Tick(10));
        }

        // ------------------------------------------------------------------------------ SlicedPass

        private static IEnumerator<int> Open(List<int> l) { return ((IEnumerable<int>)l).GetEnumerator(); }

        private static IEnumerator<int> Throwing()
        {
            yield return 10;
            throw new InvalidOperationException("the document went away");
        }

        private static void Pass(Action<string, bool?> check)
        {
            var docs = new List<List<int>> { new List<int> { 1, 2, 3 }, new List<int>(), new List<int> { 4, 5 }, new List<int> { 6 } };

            // one shot
            var seen = new List<string>();
            var one = new SlicedPass<List<int>, int>("sig", docs, Open, (ix, item) => seen.Add(ix + ":" + item), () => 0, 1);
            bool done = one.Step(int.MaxValue);
            check(P + "a pass given all the time visits every item once, in document order, and says done",
                done && string.Join(",", seen.ToArray()) == "0:1,0:2,0:3,2:4,2:5,3:6" && one.Seen == 6 && !one.Failed);

            // the same walk in slices of two items (the clock advances 1 per visit), whatever the slicing
            long t = 0; var seen2 = new List<string>(); int steps = 0, maxInStep = 0; int inStep = 0;
            var sliced = new SlicedPass<List<int>, int>("sig", docs, Open, (ix, item) => { seen2.Add(ix + ":" + item); t++; inStep++; }, () => t, 1);
            bool fin = false;
            while (!fin && steps < 100) { inStep = 0; fin = sliced.Step(2); steps++; if (inStep > maxInStep) maxInStep = inStep; }
            check(P + "the same walk in 2 ms slices gives the same visits in the same order, none missed or repeated",
                fin && string.Join(",", seen2.ToArray()) == "0:1,0:2,0:3,2:4,2:5,3:6" && sliced.Seen == 6);
            check(P + "each 2 ms slice visits at most 2 items, so six items take three slices (" + steps + " steps, most " + maxInStep + ")",
                steps == 3 && maxInStep == 2);

            // a document that will not open is skipped
            var seen3 = new List<string>();
            var skip = new SlicedPass<List<int>, int>("sig",
                new List<List<int>> { null, new List<int> { 1 }, null, new List<int> { 2 } },
                d => { if (d == null) throw new InvalidOperationException("refuses a collector"); return Open(d); },
                (ix, item) => seen3.Add(ix + ":" + item), () => 0, 1);
            bool sdone = skip.Step(int.MaxValue);
            check(P + "a document that refuses to open is skipped and the rest are still walked",
                sdone && string.Join(",", seen3.ToArray()) == "1:1,3:2" && !skip.Failed);

            // a walk that throws part-way is Failed, finished, and visits nothing more
            var seen4 = new List<string>();
            var bad = new SlicedPass<int, int>("sig", new List<int> { 0, 1 },
                d => d == 0 ? Throwing() : Open(new List<int> { 99 }),
                (ix, item) => seen4.Add(ix + ":" + item), () => 0, 1);
            bool bdone = bad.Step(int.MaxValue);
            int visitsAtFail = seen4.Count;
            bool again = bad.Step(int.MaxValue);
            check(P + "a walk that throws part-way is Failed and done (never a half answer) and a later step visits nothing",
                bdone && bad.Failed && again && seen4.Count == visitsAtFail && visitsAtFail == 1);

            // the signature belongs to the walk
            var sg = new SlicedPass<List<int>, int>("A|B", docs, Open, (ix, item) => { }, () => 0, 1);
            check(P + "a pass keeps the signature it was built with, and IsFor is an exact match",
                sg.Signature == "A|B" && sg.IsFor("A|B") && !sg.IsFor("A") && !sg.IsFor("a|b") && !sg.IsFor(null));
        }

        // ------------------------------------------------------------------------------ SignedCache

        private static void Cache(Action<string, bool?> check)
        {
            var cache = new SignedCache<int>();
            int v;
            cache.Put("A", 1);
            check(P + "a cache answers the signature it was filled under", cache.TryGet("A", out v) && v == 1);
            check(P + "a lookup under a different document set misses (a link was loaded: A -> A|B)", !cache.TryGet("A|B", out v));

            cache.Put("A|B", 2);
            check(P + "the cache holds one entry per signature, not one slot: both are served",
                cache.TryGet("A", out v) && v == 1 && cache.TryGet("A|B", out v) && v == 2 && cache.Count == 2);

            // the case that matters: a pass built for {A}, a link loaded before it finished, the counts filed under what it walked
            var c2 = new SignedCache<string>();
            var walked = new SlicedPass<List<int>, int>("A", new List<List<int>> { new List<int> { 1 } }, Open, (ix, item) => { }, () => 0, 1);
            walked.Step(int.MaxValue);
            c2.Put(walked.Signature, "counts for A");
            string got;
            check(P + "counts from a pass built for A are filed under A and are NOT served to a lookup for A|B (a link loaded between slices)",
                c2.TryGet("A", out got) && got == "counts for A" && !c2.TryGet("A|B", out got));

            cache.Clear();
            check(P + "Clear drops every entry", cache.Count == 0 && !cache.TryGet("A", out v) && !cache.TryGet("A|B", out v));
        }

        // ------------------------------------------------------------------------------ what the window shows

        private static void Visibility(Action<string, bool?> check)
        {
            check(P + "an unknown count (-1) is shown, not ticked, 'show empty' off: unknown never hides a category",
                FilterPrep.CategoryVisible(-1, false, false, "Pipes", ""));
            check(P + "an unknown count is shown when ticked too", FilterPrep.CategoryVisible(-1, true, false, "Pipes", ""));
            check(P + "a known count of 0 is hidden when not ticked and 'show empty' is off", !FilterPrep.CategoryVisible(0, false, false, "Pipes", ""));
            check(P + "a known 0 is shown when ticked (a setting the user made is never hidden)", FilterPrep.CategoryVisible(0, true, false, "Pipes", ""));
            check(P + "a known 0 is shown when 'show empty' is on", FilterPrep.CategoryVisible(0, false, true, "Pipes", ""));
            check(P + "a category with elements is shown", FilterPrep.CategoryVisible(12, false, false, "Pipes", ""));
            check(P + "'show empty' applies only to KNOWN counts: it changes nothing for an unknown one, in either state",
                FilterPrep.CategoryVisible(-1, false, true, "Pipes", "") && FilterPrep.CategoryVisible(-1, false, false, "Pipes", ""));
            check(P + "the search box matches any part of the name, any case, and hides the rest (even a ticked one)",
                FilterPrep.CategoryVisible(5, false, false, "Pipe Fittings", "fitt") && !FilterPrep.CategoryVisible(5, true, false, "Pipes", "fitt"));
            check(P + "a blank or space-only search filters nothing, and a search is trimmed",
                FilterPrep.CategoryVisible(5, false, false, "Pipes", "   ") && FilterPrep.CategoryVisible(5, false, false, "Pipes", "  pipe  "));
            check(P + "the search is applied to an unknown count too (no match -> hidden)", !FilterPrep.CategoryVisible(-1, false, false, "Walls", "pipe"));
        }

        private static void Status(Action<string, bool?> check)
        {
            check(P + "while working the text names the step and the percent: 'Counting elements... 41%'",
                FilterPrep.StatusText("Counting elements", 0.41, false, null) == "Counting elements… 41%");
            check(P + "the percent is rounded down and never shows 100% before the work is done",
                FilterPrep.StatusText("Counting elements", 0.999, false, null) == "Counting elements… 99%"
                && FilterPrep.StatusText("Counting elements", 1.0, false, null) == "Counting elements… 99%");
            check(P + "below 0 and above 1 are clamped",
                FilterPrep.StatusText("X", -0.5, false, null) == "X… 0%" && FilterPrep.StatusText("X", 7, false, null) == "X… 99%");
            check(P + "when done without an error the text is empty (the label is gone)", FilterPrep.StatusText("Counting elements", 1.0, true, null) == "");
            string err = FilterPrep.StatusText("Counting elements", 1.0, true, "category count: boom");
            check(P + "when done with an error the text says counts could not be loaded and why", err.Contains("could not be loaded") && err.Contains("boom"));
        }

        private static void Names(Action<string, bool?> check)
        {
            var add = FilterPrep.NamesToAdd(KnownWF, BatchMix);
            check(P + "an incoming batch of parameter names adds only what is new: case-insensitive, deduplicated, blanks dropped, order kept ("
                  + string.Join("|", add.ToArray()) + ")",
                add.Count == 2 && add[0] == "Mark" && add[1] == "Comments");
            check(P + "a batch that is all known names adds nothing", FilterPrep.NamesToAdd(KnownAB, BatchBA).Count == 0);
            check(P + "a batch never removes: a name the user typed that is not in the batch is not in the list to add, and the existing list is untouched",
                FilterPrep.NamesToAdd(KnownMy, BatchMark).SequenceEqual(BatchMark));
            check(P + "an empty batch, or a null one, adds nothing and does not throw",
                FilterPrep.NamesToAdd(KnownA, Array.Empty<string>()).Count == 0 && FilterPrep.NamesToAdd(KnownA, null).Count == 0);
        }

        // ------------------------------------------------------------------------------ the scope of a rule picked before the catalog is complete

        // The catalog the picker's scope comes from is only trustworthy once it is COMPLETE: the blank instance shadow of a Type parameter
        // ("Family Name", "Type Name") is dropped at the end, so a partial catalog can name the wrong scope. A rule picked while it loads
        // therefore stays Either and is marked PENDING, and its scope is worked out again from the finished catalog. A rule whose scope the
        // user (or a saved filter) already set is never pending and never touched.
        private static void RuleScope(Action<string, bool?> check)
        {
            var catalog = new List<KeyValuePair<string, ParamScope>>
            {
                new KeyValuePair<string, ParamScope>("Comments", ParamScope.Instance),
                new KeyValuePair<string, ParamScope>("Comments", ParamScope.Type),
                new KeyValuePair<string, ParamScope>("Type Mark", ParamScope.Type),
            };
            bool pending;
            var sc = FilterPrep.ScopeFor("Type Mark", catalog, true, out pending);
            check(P + "with the catalog complete a Type-only parameter is scoped Type and is not pending", sc == ParamScope.Type && !pending);
            sc = FilterPrep.ScopeFor("Comments", catalog, true, out pending);
            check(P + "with the catalog complete a name in both scopes takes the first entry (the catalog is sorted Instance, then Type)", sc == ParamScope.Instance && !pending);
            sc = FilterPrep.ScopeFor("type MARK", catalog, true, out pending);
            check(P + "the name is matched in any case", sc == ParamScope.Type && !pending);
            sc = FilterPrep.ScopeFor("Nothing Like It", catalog, true, out pending);
            check(P + "with the catalog complete a name that is not in it is Either and nothing is pending (there is nothing to wait for)", sc == ParamScope.Either && !pending);

            sc = FilterPrep.ScopeFor("Type Mark", catalog, false, out pending);
            check(P + "while the catalog is still loading a name that is already in the partial catalog is Either and PENDING (a partial catalog is not trusted)",
                sc == ParamScope.Either && pending);
            sc = FilterPrep.ScopeFor("Not Yet Seen", new List<KeyValuePair<string, ParamScope>>(), false, out pending);
            check(P + "while the catalog is still loading a name that is not in it yet is Either and PENDING", sc == ParamScope.Either && pending);
            sc = FilterPrep.ScopeFor("Not Yet Seen", null, false, out pending);
            check(P + "while loading with no catalog list at all it is Either and PENDING, and nothing throws", sc == ParamScope.Either && pending);

            sc = FilterPrep.ScopeFor("Workset", catalog, false, out pending);
            bool a = sc == ParamScope.Instance && !pending;
            sc = FilterPrep.ScopeFor("workset", catalog, true, out pending);
            check(P + "Workset is always Instance and never pending, loading or not", a && sc == ParamScope.Instance && !pending);

            sc = FilterPrep.ScopeFor("", catalog, false, out pending);
            bool b = sc == ParamScope.Either && !pending;
            sc = FilterPrep.ScopeFor(null, catalog, false, out pending);
            check(P + "a blank or missing name is Either and not pending (no parameter is picked, so nothing to resolve)", b && sc == ParamScope.Either && !pending);
        }

        // ------------------------------------------------------------------------------ U1 review round: what a pass knows about itself, the pump's detail and clock, the status line

        private sealed class TrackedEnumerator : IEnumerator<int>
        {
            private int _i = -1;
            private readonly int _n;
            public bool Disposed;
            public TrackedEnumerator(int n) { _n = n; }
            public int Current { get { return _i; } }
            object System.Collections.IEnumerator.Current { get { return _i; } }
            public bool MoveNext() { return ++_i < _n; }
            public void Reset() { _i = -1; }
            public void Dispose() { Disposed = true; }
        }

        private static void PassFacts(Action<string, bool?> check)
        {
            // a document that will not open: skipped, counted, and the pass is NOT intact (a pass that skipped a document is never filed: review H1)
            long t = 0;
            Func<string, IEnumerator<int>> open = d =>
            {
                if (d == "B") throw new InvalidOperationException("will not open");
                if (d == "N") return null;
                return new TrackedEnumerator(3);
            };
            var visited = 0;
            var p = new SlicedPass<string, int>("S", DocsABC, open, (ix, i) => { visited++; }, () => t, 1);
            bool done = p.Step(int.MaxValue);
            check(P + "a document that refuses to open is counted as skipped, the rest are walked, and the pass is not intact (it must never be filed)",
                done && p.Skipped == 1 && !p.Intact && visited == 6 && !p.Failed);
            var pn = new SlicedPass<string, int>("S", DocsAN, open, (ix, i) => { }, () => t, 1);
            pn.Step(int.MaxValue);
            check(P + "a document whose opener returns nothing is counted as skipped too", pn.Skipped == 1 && !pn.Intact);

            var clean = new SlicedPass<string, int>("S", DocsAC, open, (ix, i) => { }, () => t, 1);
            clean.Step(int.MaxValue);
            check(P + "a pass that walked every document is intact, with nothing skipped", clean.Intact && clean.Skipped == 0 && !clean.Failed && clean.Seen == 6);

            var broken = new SlicedPass<string, int>("S", DocsAC, open, (ix, i) => { if (i == 1) throw new InvalidOperationException("visit failed"); }, () => t, 1);
            broken.Step(int.MaxValue);
            check(P + "a pass whose walk threw part-way is not intact", broken.Failed && !broken.Intact);

            // where the pass is: the document it is on, and how many there are (the counting step's progress)
            long clock = 0;
            var order = new List<int>();
            var q = new SlicedPass<string, int>("S", DocsACD, d => new TrackedEnumerator(4), (ix, i) => { }, () => clock, 1);
            check(P + "a pass that has not started is on document 0 of 3", q.DocIndex == 0 && q.DocCount == 3);
            int guard = 0;
            while (!q.Step(0) && guard++ < 100) order.Add(q.DocIndex);   // a 0 ms budget: one item per step
            bool monotone = true;
            for (int i = 1; i < order.Count; i++) if (order[i] < order[i - 1]) monotone = false;
            check(P + "the document index only ever goes up while a pass is stepped, reaches the last document, and equals the count when it is over (" + string.Join(",", order.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + ")",
                monotone && order.Count > 3 && order.Max() == 2 && q.DocIndex == 3 && q.DocCount == 3);

            // a step with no time at all still visits one item (hand mutant S4 survived without this): "always at least one item per step" is what stops a tiny budget stalling
            var z = new SlicedPass<string, int>("S", KnownA, d => new TrackedEnumerator(5), (ix, i) => { }, () => 0, 1);
            bool zdone = z.Step(0);
            check(P + "a step given a 0 ms budget still visits one item, and says the walk is not over", !zdone && z.Seen == 1);

            // giving up a part-way pass closes the iterator it holds (a closed Filter window leaves one) and ends the walk
            TrackedEnumerator held = null;
            var seen2 = 0;
            var r = new SlicedPass<string, int>("S", DocsAC, d => (held = new TrackedEnumerator(100)), (ix, i) => { seen2++; }, () => clock, 1);
            r.Step(0);
            bool openBefore = held != null && !held.Disposed;
            r.Abandon();
            int atAbandon = seen2;
            bool doneAfter = r.Step(int.MaxValue);
            check(P + "abandoning a part-way pass disposes the iterator it holds and visits nothing more",
                openBefore && held.Disposed && doneAfter && seen2 == atAbandon);
        }

        private static void PumpFacts(Action<string, bool?> check)
        {
            long t = 0;
            int a = 0;
            var pump = new WorkPump(() => t);
            pump.Add("A", bud => { t += 10; return ++a >= 2; }, null, () => "a-detail " + a);
            pump.Add("B", bud => { t += 10; return true; }, null, () => "b-detail");
            pump.Add("C", bud => true);   // no detail
            check(P + "the pump reports the running step's detail", pump.Detail == "a-detail 0");
            pump.Tick(5);
            check(P + "the detail follows the step as it works (" + pump.Detail + ")", pump.Detail == "a-detail 1");
            pump.Tick(100);
            check(P + "the detail moves to the next step when one finishes, and is null for a step without one", pump.IsDone && pump.Detail == null);

            var p2 = new WorkPump(() => t);
            p2.Add("A", bud => true, null, () => "x");
            p2.Add("B", bud => { t += 5; return false; });   // the clock must move, or Tick(1) never sees its budget spent
            p2.Tick(1);
            check(P + "a step without a detail has none (null), not the one the finished step had", !p2.IsDone && p2.CurrentName == "B" && p2.Detail == null);

            var p3 = new WorkPump(() => t);
            p3.Add("A", bud => false, null, () => { throw new InvalidOperationException("detail broke"); });
            string d3 = "unset";
            try { d3 = p3.Detail; } catch { d3 = "THREW"; }
            check(P + "a detail function that throws gives no detail and does not throw", d3 == null);

            // a step whose own progress reading goes DOWN (a counter that restarts) must not move the bar backwards (hand mutant W3 survived without this)
            double reading = 0.8;
            var p4 = new WorkPump(() => t);
            p4.Add("A", bud => false, () => reading);
            p4.Add("B", bud => true);
            double up = p4.Fraction;
            reading = 0.3;
            double down = p4.Fraction;
            reading = 0.5;
            double mid = p4.Fraction;
            check(P + "a step's progress reading that goes backwards does not move the fraction backwards (" + up.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " -> " + down.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " -> " + mid.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")",
                Math.Abs(up - 0.4) < 1e-9 && down >= up - 1e-12 && mid >= up - 1e-12);

            // the default clock has to see a few milliseconds (it was Environment.TickCount, which moves in steps of about 16 ms)
            // Judged on the MINIMUM and the MEDIAN of 41 samples, not on every one: a thread preempted by a build or a Stryker run only makes a few
            // differences LONGER, which neither the minimum nor the median sees, while a clock that moves in 16 ms steps has many differences of 0.
            var d = new List<long>();
            for (int i = 0; i < 41; i++)
            {
                long x = WorkPump.DefaultClockMs();
                var spin = System.Diagnostics.Stopwatch.StartNew();
                while (spin.Elapsed.TotalMilliseconds < 3.0) { }   // a spin, not Thread.Sleep(3): Sleep(3) takes about 16 ms on Windows
                long y = WorkPump.DefaultClockMs();
                d.Add(y - x);
            }
            d.Sort();
            check(P + "the default clock tells 3 ms from nothing (41 samples: minimum " + d[0] + " ms, median " + d[20] + " ms, maximum " + d[40] + " ms)",
                d[0] >= 2 && d[20] <= 12);
        }

        private static void StatusFacts(Action<string, bool?> check)
        {
            // 0.57 * 100 is 56.99999999999999 and 0.29 * 100 is 28.999999999999996: a person reads 57% and 29% (hand mutant F6 survived without this)
            check(P + "a fraction whose percent is a hair under a whole number reads as that number (57% and 29%, not 56% and 28%)",
                FilterPrep.StatusText("X", 0.57, false, null) == "X… 57%" && FilterPrep.StatusText("X", 0.29, false, null) == "X… 29%");
            check(P + "the status line can carry what has been counted so far, after the percent",
                FilterPrep.StatusText("Counting elements", 0.33, false, null, "12,300 elements") == "Counting elements… 33%  (12,300 elements)");
            check(P + "no detail, or a blank one, changes nothing",
                FilterPrep.StatusText("Counting elements", 0.33, false, null, null) == "Counting elements… 33%"
                && FilterPrep.StatusText("Counting elements", 0.33, false, null, "") == "Counting elements… 33%");
            check(P + "when the work is done the detail is not shown", FilterPrep.StatusText("Counting elements", 1.0, true, null, "12,300 elements") == "");
            string working = FilterPrep.StatusText("Counting elements", 0.41, false, "Loading parameters: boom", null);
            check(P + "an error that happened earlier is shown while later steps are still working, with the percent (" + working + ")",
                working.StartsWith("Counting elements… 41%", StringComparison.Ordinal) && working.Contains("could not be loaded") && working.Contains("boom"));
        }

        // ------------------------------------------------------------------------------ Stryker round 1 (U1, 2026-11-08): the survivors that a check can tell apart

        private static void StrykerRound1(Action<string, bool?> check)
        {
            const string S1 = "stryker r1: ";

            // ---- WorkPump
            var wn = new WorkPump(() => 0);
            wn.Add("X", null);
            check(P + S1 + "a step that is null is not added: the pump is done at once and a tick runs nothing (WorkPump.Add l.41)",
                wn.IsDone && wn.Tick(5) && wn.Error == null && wn.Ticks == 0);

            var wm = new WorkPump(() => 0);
            wm.Add(null, bud => false);
            var we = new WorkPump(() => 0);
            we.Add(null, bud => { throw new InvalidOperationException("boom"); });
            we.Tick(5);
            check(P + S1 + "a step added without a name is called \"\" (current name and error text) (WorkPump.Add l.42)",
                wm.CurrentName == "" && we.Error == ": boom");

            long jump = 0; int ranJ = 0;
            var wj = new WorkPump(() => jump += 100);
            wj.Add("A", bud => { ranJ++; return true; });
            wj.Add("B", bud => { ranJ++; return true; });
            wj.Tick(1);
            check(P + S1 + "a tick runs its first step even when the clock has already jumped past the budget, and then stops (ran " + ranJ + ") (WorkPump.Tick l.105)",
                ranJ == 1 && wj.Ticks == 1 && !wj.IsDone);

            long tb = 0; var order = new List<string>();
            var wb = new WorkPump(() => tb);
            wb.Add("A", bud => { order.Add("A"); tb += 5; return true; });
            wb.Add("B", bud => { order.Add("B"); return true; });
            bool firstDone = wb.Tick(5);
            bool onlyA = order.Count == 1;
            bool secondDone = wb.Tick(5);
            check(P + S1 + "a tick stops when exactly its budget is spent: the next step waits for the next tick (WorkPump.Tick l.109)",
                !firstDone && onlyA && secondDone && order.Count == 2);

            // ---- SlicedPass
            long ta = 0;
            var pa = new SlicedPass<string, int>("S", KnownA, d => new TrackedEnumerator(100), (ix, i) => { ta++; }, () => ta, 4);
            bool doneA = pa.Step(2);
            check(P + S1 + "the clock is only read every checkEvery-th item: with checkEvery 4 and a budget of 2 the step stops after 4 items (" + pa.Seen + ") (SlicedPass l.45)",
                !doneA && pa.Seen == 4);

            long tz = 0;
            var pz = new SlicedPass<string, int>("S", KnownA, d => new TrackedEnumerator(100), (ix, i) => { tz++; }, () => tz, 0);
            bool doneZ = pz.Step(1);
            check(P + S1 + "a checkEvery of 0 reads the clock at every item: the step stops (not finished) after " + pz.Seen + " item(s) (SlicedPass l.45)",
                !doneZ && pz.Seen < 100);

            int reads = 0;
            var pe = new SlicedPass<string, int>("S", KnownA, d => new TrackedEnumerator(2), (ix, i) => { }, () => { reads++; return 0; }, 1);
            bool e1 = pe.Step(int.MaxValue);
            int readsAfterFirst = reads;
            bool e2 = pe.Step(int.MaxValue);
            check(P + S1 + "a step after the walk is over answers true without reading the clock or opening anything (reads " + readsAfterFirst + " -> " + reads + ") (SlicedPass.Step l.87)",
                e1 && e2 && reads == readsAfterFirst);

            TrackedEnumerator held = null;
            var pf = new SlicedPass<string, int>("S", KnownA, d => (held = new TrackedEnumerator(5)), (ix, i) => { if (i == 1) throw new InvalidOperationException("visit failed"); }, () => 0, 1);
            pf.Step(int.MaxValue);
            check(P + S1 + "a pass whose visit threw closes the iterator it held (SlicedPass.Step l.109)",
                pf.Failed && held != null && held.Disposed);

            var cn = new SignedCache<int>();
            bool threw = false;
            try { cn.Put(null, 1); } catch { threw = true; }
            check(P + S1 + "a cache entry filed under a null signature is ignored and does not throw (SignedCache.Put l.149)", !threw && cn.Count == 0);
            int none;
            check(P + S1 + "a lookup under a null signature misses (SignedCache.TryGet l.156)", !cn.TryGet(null, out none));

            // ---- FilterPrep
            check(P + S1 + "no search text (null) hides nothing (FilterPrep.CategoryVisible l.22)", FilterPrep.CategoryVisible(3, false, false, "Walls", null));
            check(P + S1 + "a category without a name does not match any search text (FilterPrep.CategoryVisible l.23)", !FilterPrep.CategoryVisible(3, false, false, null, "a"));
            check(P + S1 + "the sentence for an error that has finished the work, exactly (FilterPrep.StatusText l.38)",
                FilterPrep.StatusText("Counting elements", 1.0, true, "boom", null) == "Some data could not be loaded (boom).");
            check(P + S1 + "a status line without a step name is just the percent (FilterPrep.StatusText l.43)",
                FilterPrep.StatusText(null, 0.5, false, null, null) == "… 50%");
            check(P + S1 + "an error is shown after the percent, two spaces apart, exactly (FilterPrep.StatusText l.45)",
                FilterPrep.StatusText("Counting elements", 0.41, false, "boom", null) == "Counting elements… 41%  Some data could not be loaded (boom).");
        }

    }
}
