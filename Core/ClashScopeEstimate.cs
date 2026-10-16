using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>How costly a full check is about to be, at a glance.</summary>
    public enum ScopeBand
    {
        /// <summary>Seconds. Just run it.</summary>
        Green = 0,
        /// <summary>Tens of seconds. Worth a light heads-up.</summary>
        Yellow = 1,
        /// <summary>Minutes, or a source/comparison count that could freeze Revit
        /// regardless. A strong warning, defaulting to cancel.</summary>
        Red = 2,
    }

    /// <summary>
    /// A cheap, geometry-free estimate of how big a full check is about to be, so the
    /// user can be warned before an accidental all-categories run freezes Revit for
    /// minutes. The full check is synchronous on the main thread with no time budget:
    /// all-55-categories each side on a federated model is ~12,700 sources against
    /// ~41,800 targets across five documents, and nothing repaints until it finishes.
    ///
    /// Only element COUNTS are read (FilteredElementCollector.GetElementCount, one pass
    /// per document), not geometry — the whole estimate is a fraction of a second even
    /// on a large model. Categories are UNIONed across every enabled set: for "how much
    /// will this touch" the distinct element counts are the honest number, and it is far
    /// cheaper than measuring each set. Parameter rules narrow the real scope further, so
    /// this is an upper bound — which is the safe direction for a warning.
    /// </summary>
    public class ClashScopeEstimate
    {
        /// <summary>Host elements in the union of every enabled set's source categories.</summary>
        public long SourceElements;

        /// <summary>Target elements summed over the host (host-vs-host) and each loaded
        /// link (host-vs-links), in the union of every enabled set's target categories.</summary>
        public long TargetElements;

        /// <summary>Documents a source is checked against: 1 host, plus each loaded link
        /// when link checking is on.</summary>
        public int DocumentsScanned;

        public int EnabledSets;

        /// <summary>Source x target — the naive comparison count, before the spatial index
        /// prunes it. A scary number on purpose: it is what an all-vs-all run signs up for.</summary>
        public long Comparisons { get { return SourceElements * TargetElements; } }

        // Tuned to the known freeze: PRJ01 at ~1,400 sources takes ~5 s, so ~6,000 sources
        // against a dense target set is already tens of seconds to minutes. The comparison
        // trigger catches a dense target even when the source pool is only moderate.
        public const long HeavySources = 6_000;
        public const long HeavyComparisons = 100_000_000;

        // Wall-clock model, calibrated: PRJ01 completes a full check of ~1,400 sources in
        // ~5.5 s -> ~4 ms/source at a normal MEP target density. A denser target pool (all
        // categories, or several links) raises the narrow-phase survivor count per source,
        // so the per-source rate climbs gently with total targets. Rough by nature —
        // narrow-phase cost swings with geometry — so it leans high and is always "about".
        private const double BaseMsPerSource = 4.0;
        private const double MsPerSourcePerTarget = 1.0 / 15_000.0;   // +1 ms/source per 15k targets
        // A ~15 s freeze is where "did it hang?" starts; ~90 s is "go do something else,
        // or narrow it". The IsHeavy size guards are a Red floor under both, but only
        // until a timed sample replaces the formula — see Band.
        public const double YellowSeconds = 15.0;
        public const double RedSeconds = 90.0;

        /// <summary>
        /// Host target elements that are ALSO in the source scope — the pairs a sweep can
        /// reach from both ends. See <see cref="DedupFactor"/>.
        /// </summary>
        public long BothWaysElements;

        /// <summary>
        /// How much of the work the sweep's own de-duplication removes.
        ///
        /// In sweep mode <see cref="ClashEngine"/> evaluates a pair from ONE end only when
        /// both elements are sources and each survives the other's target rules
        /// (ReverseWillCoverIt): the lower element id does the work, the higher one skips.
        /// So pairs that are reachable both ways cost about HALF. With source and target
        /// scopes identical — the all-categories case — that is nearly every pair.
        ///
        /// 1.0 = no overlap, nothing deduped. 0.5 = fully overlapping scopes.
        /// </summary>
        public double DedupFactor
        {
            get
            {
                if (TargetElements <= 0 || BothWaysElements <= 0) return 1.0;
                double overlap = (double)BothWaysElements / TargetElements;
                if (overlap > 1.0) overlap = 1.0;
                return 1.0 - (0.5 * overlap);
            }
        }

        /// <summary>Rough wall-clock the sweep will take. 0 when a side is empty.</summary>
        public double EstimatedSeconds
        {
            get
            {
                // A timed sample beats any formula - see MeasureSampled.
                if (WasSampled) return _sampledSeconds;
                if (SourceElements <= 0 || TargetElements <= 0) return 0;
                double msPerSource = BaseMsPerSource + TargetElements * MsPerSourcePerTarget;
                return SourceElements * msPerSource * DedupFactor / 1000.0;
            }
        }

        /// <summary>
        /// Green / Yellow / Red.
        ///
        /// The raw size guards are a Red floor under the FORMULA only. They exist because
        /// the formula is a guess and a wrong guess on the low side costs a frozen Revit —
        /// so when all we have is a guess, sheer size is enough to warn on.
        ///
        /// A TIMED SAMPLE OVERRIDES THAT FLOOR. Once <see cref="WasSampled"/> is set the
        /// band comes from the measurement alone. Keeping the floor over a measurement
        /// produced a dialog that contradicted itself — Snowdon Towers has 6,164 sources,
        /// 2.7% over HeavySources, and the sample correctly measured the run as under a
        /// second, yet the guard announced "This is a large full check — under a second"
        /// and defaulted to Cancel. A red light on a sub-second job is one people learn to
        /// click through, which is exactly how the guard stops working on the run that
        /// really would freeze.
        /// </summary>
        public ScopeBand Band
        {
            get
            {
                if ((IsHeavy && !WasSampled) || EstimatedSeconds >= RedSeconds) return ScopeBand.Red;
                if (EstimatedSeconds >= YellowSeconds) return ScopeBand.Yellow;
                return ScopeBand.Green;
            }
        }

        /// <summary>"about 8 seconds" / "about 2.5 minutes". Empty when there is nothing to run.</summary>
        public string EstimatedText
        {
            get
            {
                // Worded as an UPPER BOUND, because that is what it is. Per-source cost
                // varies by an order of magnitude with the configuration: measured 4.3
                // ms/source on a narrow filter (1,406 sources, 2026-08-23) but 0.39
                // ms/source on all-categories (32,008 sources, 12.5 s total, 2026-08-30) —
                // wide source scopes make both sides sources, and ReverseWillCoverIt then
                // dedups most pairs away. The model stays calibrated to the SLOW case on
                // purpose: over-estimating costs a cautious dialog, under-estimating costs
                // the surprise freeze this guard exists to prevent. Saying "up to" keeps
                // that honest instead of promising a figure it cannot hit.
                double s = EstimatedSeconds;
                if (s <= 0) return "";

                // A MEASURED figure is stated plainly; only the formula hedges with
                // "up to", because only the formula is a worst case.
                string lead = WasSampled ? "about " : "up to about ";
                // Both arms of this used to read "under a second", which quietly broke
                // the rule stated just above: an unsampled figure is a worst-case guess
                // and must not be reported as flatly as a measured one.
                if (s < 1) return WasSampled ? "under a second" : "up to about a second";
                if (s < 90)
                {
                    int n = (int)Math.Round(s);
                    return lead + n + " second" + (n == 1 ? "" : "s");
                }
                double min = s / 60.0;
                return min < 10
                    ? lead + min.ToString("0.0") + " minutes"
                    : lead + (int)Math.Round(min) + " minutes";
            }
        }

        /// <summary>
        /// True when this run is big enough, by raw size alone, to be worth MEASURING
        /// before asking the user anything.
        ///
        /// This is the trigger for taking a timed sample, and a Red floor only while no
        /// sample has been taken — see <see cref="Band"/>. It is deliberately a pure size
        /// test: it has to be answerable without doing any of the work.
        /// </summary>
        public bool IsHeavy
        {
            get { return SourceElements >= HeavySources || Comparisons >= HeavyComparisons; }
        }

        public string Summary()
        {
            return "~" + SourceElements.ToString("N0") + " source vs ~"
                 + TargetElements.ToString("N0") + " target element(s) across "
                 + DocumentsScanned + " model" + (DocumentsScanned == 1 ? "" : "s")
                 + (EnabledSets > 1 ? ", " + EnabledSets + " clash sets" : "");
        }

        /// <summary>
        /// Measure the scope of the full check <paramref name="settings"/> would run on
        /// <paramref name="doc"/>. Revit-bound (reads collectors), so exercised by the
        /// in-Revit checks rather than in the headless gate.
        /// </summary>
        public static ClashScopeEstimate Measure(Document doc, ClashSettings settings)
        {
            var est = new ClashScopeEstimate();
            if (doc == null || !doc.IsValidObject || settings == null) return est;

            var srcCats = new HashSet<BuiltInCategory>();
            var tgtCats = new HashSet<BuiltInCategory>();
            foreach (var set in settings.Sets)
            {
                if (set == null || !set.Enabled) continue;
                est.EnabledSets++;
                foreach (var c in set.SourceCategories) srcCats.Add(c);
                foreach (var c in set.TargetCategories) tgtCats.Add(c);
            }
            if (est.EnabledSets == 0) return est;

            var links = new List<LinkContext>();
            if (settings.CheckHostVsLinks)
                foreach (var lc in LinkContext.CollectFrom(doc))
                    if (lc.Document != null && lc.Document.IsValidObject) links.Add(lc);

            var src = new List<BuiltInCategory>(srcCats);
            var tgt = new List<BuiltInCategory>(tgtCats);

            // Sources are always host-document elements — see ClashEngine.RunFullCheckCore.
            est.SourceElements = CountIn(doc, src);

            // Host elements in BOTH scopes: the pairs the sweep can reach from either end,
            // and therefore evaluates once instead of twice. Only the host can contribute
            // these — a link element is never a source. One more counting pass.
            var both = new List<BuiltInCategory>();
            foreach (var c in tgtCats) if (srcCats.Contains(c)) both.Add(c);
            est.BothWaysElements = CountIn(doc, both);

            int docsScanned = 1;   // the host is always scanned for the source pool
            if (settings.CheckHostVsHost) est.TargetElements += CountIn(doc, tgt);
            if (settings.CheckHostVsLinks)
                foreach (var lc in links)
                {
                    // Per-link control is per-SET, so a link contributes whatever the union
                    // of the enabled sets asks of it: a category counts if any enabled set
                    // would collect it from this link, and a link is skipped only when every
                    // enabled set has switched it off or excluded everything.
                    var linkCats = new HashSet<BuiltInCategory>();
                    foreach (var set in settings.Sets)
                    {
                        if (set == null || !set.Enabled) continue;
                        if (set.DisabledLinks.Contains(lc.Name)) continue;

                        HashSet<BuiltInCategory> excl;
                        set.LinkExclusions.TryGetValue(lc.Name, out excl);
                        foreach (var c in set.TargetCategories)
                            if (excl == null || !excl.Contains(c)) linkCats.Add(c);
                    }
                    if (linkCats.Count == 0) continue;   // link contributes nothing

                    docsScanned++;
                    est.TargetElements += CountIn(lc.Document, new List<BuiltInCategory>(linkCats));
                }
            est.DocumentsScanned = docsScanned;

            return est;
        }

        /// <summary>How many source elements to time. Big enough to average out cheap
        /// isolated sources against expensive congested ones, small enough to stay ~a second.</summary>
        public const int SampleSize = 250;

        /// <summary>Set when <see cref="MeasureSampled"/> has replaced the guess with a
        /// timed measurement; <see cref="EstimatedSeconds"/> then returns that instead.</summary>
        public bool WasSampled { get; private set; }
        private double _sampledSeconds;


        /// <summary>
        /// MEASURE the sweep instead of predicting it: build the real scan context, time a
        /// random sample of sources through the real engine, and scale up.
        ///
        /// Random, not the first N: element ids come out grouped by creation order and so
        /// by category and area, and a leading slice would land entirely in one part of the
        /// building — the cheap isolated elements or the congested plant room, either way
        /// unrepresentative.
        ///
        /// Costs about a second, which is why it belongs on the Full check pre-flight and
        /// NOT on the live panel indicator — that keeps the cheap count-based guess.
        /// Falls back silently to the guess if anything goes wrong.
        /// </summary>
        public void MeasureSampled(Document doc, ClashSettings settings)
        {
            if (doc == null || !doc.IsValidObject || settings == null) return;
            if (SourceElements <= 0 || TargetElements <= 0) return;

            try
            {
                double total = 0;

                // The set is passed to each call rather than assigned to
                // settings.ActiveSet round the loop — see ClashEngine.CheckElementsAllSets.
                foreach (var set in settings.Sets)
                {
                    if (!set.Enabled) continue;

                    var ids = ClashEngine.SourceIdsFor(doc, settings, set);
                    if (ids.Count == 0) continue;

                    var sample = PickRandom(ids, SampleSize);
                    long ctxMs;
                    int timed;
                    long sampleMs = ClashEngine.TimeSample(doc, settings, sample, out ctxMs, out timed, set);
                    if (timed <= 0) continue;

                    // Context is paid once per set; the per-source cost scales. Extrapolate
                    // from the TIMED count, not the whole sample - the warm-up was discarded.
                    total += ctxMs + (sampleMs * (double)ids.Count / timed);
                }

                if (total <= 0) return;
                _sampledSeconds = total / 1000.0;
                WasSampled = true;
            }
            catch (Exception ex) { Log.Warn("Sampled scope estimate failed; using the count-based guess", ex); }
        }

        /// <summary>An evenly spread subset — a stride, not a leading slice. See MeasureSampled.</summary>
        private static List<ElementId> PickRandom(IList<ElementId> ids, int want)
        {
            var outp = new List<ElementId>();
            if (ids.Count <= want) { foreach (var id in ids) outp.Add(id); return outp; }
            double step = (double)ids.Count / want;
            for (int i = 0; i < want; i++)
            {
                int ix = (int)(i * step);
                if (ix >= 0 && ix < ids.Count) outp.Add(ids[ix]);
            }
            return outp;
        }

        private static long CountIn(Document d, List<BuiltInCategory> cats)
        {
            if (d == null || !d.IsValidObject || cats == null || cats.Count == 0) return 0;
            try
            {
                return new FilteredElementCollector(d)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(cats))
                    .GetElementCount();
            }
            catch { return 0; }
        }
    }
}
