using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The Revit collector for the Refresh plan (spec 6.1, 6.2): gathers, on the Revit thread and with NO transaction, what builder A's pure
    /// <see cref="MarkerPlanAssembler"/> needs, then assembles and plans. The live clashes become rows through <see cref="RowAdapter"/>; a pair
    /// that already has a readable marker is re-tested under the MARKER'S OWN rule (its recorded clearance and insulation, never this seat's
    /// settings: a seat with a looser rule cannot call a clearance clash fixed); the ledger supplies the decisions and the model events;
    /// the markers come with their access, the counters with every user's. A decided pair that is not live on this seat gets no row here
    /// (a ledger key has no UniqueIds): a named gap.
    /// </summary>
    [RevitBound]
    public static class MarkerCollector
    {
        public sealed class Collected
        {
            public MarkerPlanAssembler.Assembly Assembly { get; set; }
            public MarkerPlan.Result Plan { get; set; }
            public MarkerPlan.Options Options { get; set; }
        }

        private static RowBuilder.Retest RetestOf(Document doc, ClashResult result, Dictionary<long, LinkContext> links, MarkerRecord record)
        {
            ClashSeverity severity;
            var verdict = PairVerifier.Verify(doc, result, links, record.ClearanceMm, record.Insulated, out severity);
            var retest = new RowBuilder.Retest { ClearanceMm = record.ClearanceMm, Insulated = record.Insulated };
            switch (verdict)
            {
                case PairVerifier.Verdict.StillClashing: retest.Verdict = PlanVerdict.Clashing; break;
                case PairVerifier.Verdict.Resolved: retest.Verdict = PlanVerdict.NoLongerClashes; break;
                default: retest.Verdict = PlanVerdict.NotRetested; retest.Reason = "could not be re-tested"; break;
            }
            return retest;
        }

        /// <summary>The last Collect's one-line breakdown (what it also logs), for a check's label.</summary>
        internal static string LastTiming = "";

        public static Collected Collect(Document doc, bool removeRetention)
        {
            return Collect(doc, removeRetention, null);
        }

        /// <summary>
        /// As above; <paramref name="progress"/> (done, total) is called every 64 rows and at the end, so a caller can show a progress window for a big
        /// model. One Info line says where the time went (clash schedule at scale, 2026-11-02: 12,497 clashes took ~13 minutes with nobody able to say why).
        /// </summary>
        public static Collected Collect(Document doc, bool removeRetention, Action<int, int> progress)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long mark = 0;
            Func<long> lap = () => { long now = clock.ElapsedMilliseconds; long d = now - mark; mark = now; return d; };
            RowAdapter.LevelTicks = 0; RowAdapter.RuleTicks = 0; RowAdapter.ReadTicks = 0;
            long retestTicks = 0;

            var settings = ClashSettings.Current;
            var results = ClashResultStore.Instance.Snapshot();
            var links = PairVerifier.LinkLookup(doc);
            int workset = doc.IsWorkshared ? MarkerWorkset.Find(doc) : -1;
            long tLinks = lap();

            var markers = MarkerElements.Collect(doc).Select(f => MarkerOwnership.ToExisting(doc, f, workset)).ToList();
            var byTriple = new Dictionary<string, MarkerRecord>(StringComparer.Ordinal);
            foreach (var m in markers.Where(m => m.Record != null).OrderBy(m => m.MarkerUid, StringComparer.Ordinal))
            {
                var triple = ClashIdOf.Triple(m.Record.HostUid, m.Record.LinkUid, m.Record.OtherUid);
                if (triple.Length > 0 && !byTriple.ContainsKey(triple)) byTriple[triple] = m.Record;
            }
            long tMarkers = lap();

            var groupKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var g in ClashGrouper.Group(results, settings.GroupResults ? settings.GroupingDistanceFeet : 0.0))
                if (g.Members.Count > 1)
                    foreach (var member in g.Members) if (!string.IsNullOrEmpty(member.Key)) groupKey[member.Key] = g.MinKey;
            long tGroup = lap();

            long tLevels0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var levels = RowAdapter.LevelsOf(doc);   // read ONCE, not once per clash
            RowAdapter.LevelTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tLevels0;

            var clashes = new List<MarkerPlanAssembler.Clash>();
            int total = results.Count, done = 0;
            foreach (var r in results)
            {
                string group;
                groupKey.TryGetValue(r.Key ?? "", out group);
                var source = RowAdapter.SourceOf(doc, r, links, settings, group ?? "", levels);
                if (source != null)
                {
                    RowBuilder.Retest retest = null;
                    MarkerRecord record;
                    if (byTriple.TryGetValue(ClashIdOf.Triple(source.HostUid, source.LinkUid, source.OtherUid), out record))
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        retest = RetestOf(doc, r, links, record);
                        retestTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    }
                    clashes.Add(new MarkerPlanAssembler.Clash { Source = source, Retest = retest, Live = true });
                }
                if (progress != null && (++done % 64 == 0 || done == total)) progress(done, total);
            }
            long tRows = lap();

            var store = ClashStatusStore.Instance;
            var decisions = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            foreach (var kv in store.All()) decisions[kv.Key] = kv.Value;
            var lines = store.ReadLines();

            var options = new MarkerPlan.Options
            {
                Today = DateTimeOffset.Now, UserName = MarkerSettings.UserName(doc), RemoveRetention = removeRetention,
                SetupNeeded = MarkerRefresh.SetupNeeded(doc),
                DisabledSets = settings.Sets.Where(s => !s.Enabled).Select(s => s.Name).ToList()
            };
            var request = new MarkerPlanAssembler.Request
            {
                Clashes = clashes, Decisions = decisions, Extras = LedgerExtras.Read(lines ?? new List<string>()), Markers = markers,
                OwnCounter = MarkerSettings.MyCounter(doc), AllCounters = MarkerSettings.ReadCounters(doc), Options = options
            };
            long tLedger = lap();
            var assembly = MarkerPlanAssembler.Assemble(request);
            long tAssemble = lap();
            var plan = MarkerPlan.Build(assembly.Input);
            long tPlan = lap();

            Func<long, long> ms = ticks => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            LastTiming = "links " + tLinks + ", markers " + tMarkers + ", grouping " + tGroup + ", rows " + tRows
                + " (levels " + ms(RowAdapter.LevelTicks) + ", rules " + ms(RowAdapter.RuleTicks) + ", element reads " + ms(RowAdapter.ReadTicks)
                + ", re-tests " + ms(retestTicks) + "), ledger " + tLedger + ", assemble " + tAssemble + ", plan " + tPlan;
            Log.Info("Marker plan: " + total + " clash(es) in " + clock.ElapsedMilliseconds + " ms [" + LastTiming + "]");
            return new Collected { Assembly = assembly, Plan = plan, Options = options };
        }
    }
}
