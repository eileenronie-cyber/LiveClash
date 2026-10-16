using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Runtime settings for the live checker. Deliberately small and mutable —
    /// the filter window writes straight into <see cref="Current"/>.
    ///
    /// The category sets default to EVERYTHING the tool knows how to check, so that
    /// modelling anything is caught by default and the user narrows down rather than
    /// discovering by accident that a discipline was never being looked at. That
    /// default is only affordable because the broad phase is a spatial index — see
    /// <see cref="TargetIndex"/>. With the old per-element Revit collector this same
    /// configuration was the one that ran unbounded.
    /// </summary>
    public class ClashSettings
    {
        private static ClashSettings _current;

        /// <summary>
        /// Lazy on purpose. As a field initializer this ran BEFORE the
        /// <see cref="Selectable"/> array further down the file was initialized —
        /// static initializers run in textual order — so the instance constructor
        /// read a null array and the whole type failed to initialize, taking the
        /// add-in down at startup. Initializing on first use makes the declaration
        /// order irrelevant.
        /// </summary>
        public static ClashSettings Current
        {
            get { return _current ?? (_current = Load(SettingsPath)); }
        }

        /// <summary>Master switch. When false the updater returns immediately.</summary>
        public bool Enabled { get; set; } = true;

        public bool CheckHostVsHost { get; set; } = true;
        public bool CheckHostVsLinks { get; set; } = true;

        /// <summary>
        /// Soft-clash clearance in feet (Revit internal units). 0 = hard clashes only.
        /// NOTE: clearance is evaluated on bounding boxes, not true offset solids —
        /// see README "Soft clash accuracy".
        /// </summary>
        public double ClearanceFeet { get; set; } = 0.0;

        /// <summary>
        /// The clearance to use for <paramref name="set"/>: its own override when it has
        /// one, otherwise the model default. The ONE place either is read for a
        /// detection or verification decision — <see cref="ClashEngine"/> and
        /// <see cref="PairVerifier"/> both call this rather than reading either field
        /// directly, so a set's override cannot drift out of step with what "inherit"
        /// means.
        /// </summary>
        public double ClearanceFor(ClashSet set)
        {
            return set != null && set.ClearanceFeet.HasValue ? set.ClearanceFeet.Value : ClearanceFeet;
        }

        /// <summary>
        /// If a single transaction touches more elements than this, the check is
        /// deferred to idle time instead of running inside the transaction.
        /// Keeps big operations (paste, array, model-wide edits) from stalling Revit.
        /// </summary>
        public int MaxElementsPerUpdate { get; set; } = 40;

        /// <summary>
        /// Wall-clock budget for one in-transaction pass. Anything not finished
        /// inside the budget is pushed to the deferred queue.
        /// </summary>
        public int TimeBudgetMs { get; set; } = 120;

        /// <summary>Suppress pairs that are legitimately touching (joined, connected, hosted).</summary>
        public bool SuppressConnectedPairs { get; set; } = true;

        /// <summary>Collapse nearby clashes into one row per coordination issue.</summary>
        public bool GroupResults { get; set; } = true;

        /// <summary>
        /// Re-verify listed clashes in the background, a slice at a time, so ones
        /// resolved inside a link, by an undo, or while Live was paused drop off
        /// without anyone pressing Recheck. Verification only ever REMOVES pairs;
        /// finding new clashes stays the updater's job.
        /// </summary>
        public bool AutoVerify { get; set; } = true;

        /// <summary>Pairs verified per idle pass. Each is a single intersection test.</summary>
        public int VerifySliceSize { get; set; } = 25;

        /// <summary>Seconds between background verification passes.</summary>
        public int VerifyIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// How close two clashes must be to count as the same issue, in feet.
        /// ~6.5 ft (2 m) suits building services: it follows a duct or pipe run
        /// across a tray run without swallowing a genuinely separate clash in the
        /// next bay. Grouping is single-linkage, so a long run still collapses to one
        /// issue even when its ends are far further apart than this.
        /// </summary>
        public double GroupingDistanceFeet { get; set; } = 6.5;

        /// <summary>
        /// Every category the filter window offers, in display order. Anything not
        /// listed here cannot be checked, so this is the place to add a discipline.
        /// </summary>
        public static readonly BuiltInCategory[] Selectable = new[]
        {
            // Mechanical
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_DuctTerminal,
            BuiltInCategory.OST_MechanicalEquipment,
            // Pipework
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_PlumbingFixtures,
            // Electrical
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            // Structure
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_Floors,
            // Architecture
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_GenericModel,
        };

        /// <summary>
        /// The clash matrix: one or more independent <see cref="ClashSet"/>s, each a
        /// source scope + target scope + rules. Runs every enabled set; results merge
        /// and de-dup. Always holds at least one (the "Default" set), so an unconfigured
        /// install behaves exactly as the old single-filter tool did.
        /// </summary>
        public List<ClashSet> Sets { get; } = new List<ClashSet> { new ClashSet() };

        /// <summary>
        /// The most sets this tool will hold, ENFORCED WHERE THE DATA ENTERS.
        ///
        /// It lived only in FilterWindow, where it greyed out the New Set button — so it
        /// bounded what a person could build and nothing at all about what a FILE could
        /// install. FromLines took as many set.begin blocks as it was given, and that is
        /// the loader for the settings file AND for an imported .lcfilter, which is the
        /// route that matters: a preset is the thing people hand each other, and set
        /// blocks travel in it. The live on/off switch and the time budget deliberately do
        /// not travel; sets do.
        ///
        /// The cost is not hypothetical. CheckElementsAllSets walks every ENABLED set on
        /// every edit, each needing its own ScanContext (~130 ms to build cold), all
        /// sharing one 120 ms in-transaction budget — and RunFullCheckAllSets runs a whole
        /// sweep per set. ClashContext already states the invariant this restores: "the
        /// set cap keeps this dictionary to a handful of entries." It was asserting
        /// something nothing enforced.
        ///
        /// Four, matching what the window has always allowed. The largest file on this
        /// machine holds three, so this truncates nothing that exists.
        /// </summary>
        public const int MaxSets = 4;

        /// <summary>
        /// The set the filter window edits and the compatibility accessors below point
        /// at. The engine iterates every enabled set regardless of this.
        /// </summary>
        public int ActiveSet { get; set; } = 0;

        /// <summary>The active set, clamped so it is never null while Sets is non-empty.</summary>
        public ClashSet Active
        {
            get
            {
                if (Sets.Count == 0) Sets.Add(new ClashSet());
                if (ActiveSet < 0 || ActiveSet >= Sets.Count) ActiveSet = 0;
                return Sets[ActiveSet];
            }
        }

        /// <summary>
        /// Model-wide audit type-drops (access zones, modelled openings — geometry that
        /// clashes but is never a real obstruction). GLOBAL by design: stored once here,
        /// persisted as top-level auditdrop= lines, and consulted DIRECTLY by the engine
        /// for every set (<see cref="ParameterFilter.TypeExcluded"/>) — no per-set copy to
        /// keep in sync. A drop ticked once in the Audit window therefore holds across the
        /// whole clash matrix, not only in whichever set was active when it was ticked.
        /// Legacy per-set drops (sxtype/txtype) are migrated up into this list on load.
        /// </summary>
        public List<TypeKey> AuditDrops { get; } = new List<TypeKey>();

        // ---- compatibility accessors: everything that used to read a single filter
        //      now transparently reads the ACTIVE set, so the UI and engine are unchanged.

        public HashSet<BuiltInCategory> SourceCategories { get { return Active.SourceCategories; } }
        public HashSet<BuiltInCategory> TargetCategories { get { return Active.TargetCategories; } }
        public ParameterFilter SourceFilter { get { return Active.SourceFilter; } }
        public ParameterFilter TargetFilter { get { return Active.TargetFilter; } }

        /// <summary>The active set's per-link category exclusions.</summary>
        public Dictionary<string, HashSet<BuiltInCategory>> LinkExclusions { get { return Active.LinkExclusions; } }

        /// <summary>Links the active set does not check against at all.</summary>
        public HashSet<string> DisabledLinks { get { return Active.DisabledLinks; } }

        // ---------------------------------------------------------------- filters
        //
        // Each of these comes in two forms. The SET-EXPLICIT one is the real method: it
        // says which set it means and cannot be surprised. The parameterless one reads
        // Active, and exists for the UI, which genuinely does mean "the set the user is
        // looking at".
        //
        // The engine must use the explicit form. It used to use the implicit one and
        // assign ActiveSet round its loops instead, which is how F3 happened: the scan
        // context's cache key read Active while a loop moved it underneath, so two sets
        // sharing target categories shared one context and the second silently checked
        // the first one's links.

        /// <summary>Source category filter for one set.</summary>
        public ElementFilter BuildSourceFilter(ClashSet set)
        {
            var s = set ?? Active;
            return new ElementMulticategoryFilter(new List<BuiltInCategory>(s.SourceCategories));
        }

        /// <summary>Source category filter for the ACTIVE set. UI use.</summary>
        public ElementFilter BuildSourceFilter() { return BuildSourceFilter(Active); }

        /// <summary>Target category filter for one set.</summary>
        public ElementFilter BuildTargetFilter(ClashSet set)
        {
            var s = set ?? Active;
            return new ElementMulticategoryFilter(new List<BuiltInCategory>(s.TargetCategories));
        }

        /// <summary>Target category filter for the ACTIVE set. UI use.</summary>
        public ElementFilter BuildTargetFilter() { return BuildTargetFilter(Active); }

        /// <summary>
        /// One set's target categories, minus anything excluded for the link named
        /// <paramref name="linkName"/>. Empty when the link is switched off for that set,
        /// or every target category is excluded for it.
        /// </summary>
        public List<BuiltInCategory> TargetCategoriesForLink(ClashSet set, string linkName)
        {
            var s = set ?? Active;

            if (!string.IsNullOrEmpty(linkName) && s.DisabledLinks.Contains(linkName))
                return new List<BuiltInCategory>();

            var cats = new List<BuiltInCategory>(s.TargetCategories);
            HashSet<BuiltInCategory> excl;
            if (!string.IsNullOrEmpty(linkName) && s.LinkExclusions.TryGetValue(linkName, out excl) && excl.Count > 0)
                cats.RemoveAll(excl.Contains);
            return cats;
        }

        /// <summary>The ACTIVE set's target categories for a link. UI use.</summary>
        public List<BuiltInCategory> TargetCategoriesForLink(string linkName)
        {
            return TargetCategoriesForLink(Active, linkName);
        }

        /// <summary>
        /// Target filter for one link's document, honouring that set's per-link gate and
        /// exclusions. Null when the link contributes nothing — the caller skips building
        /// an index for it.
        /// </summary>
        public ElementFilter BuildTargetFilterForLink(ClashSet set, string linkName)
        {
            var cats = TargetCategoriesForLink(set, linkName);
            return cats.Count == 0 ? null : new ElementMulticategoryFilter(cats);
        }

        /// <summary>Target filter for a link, for the ACTIVE set. UI use.</summary>
        public ElementFilter BuildTargetFilterForLink(string linkName)
        {
            return BuildTargetFilterForLink(Active, linkName);
        }

        /// <summary>Friendly name for a category, without needing a Document.</summary>
        public static string DisplayName(BuiltInCategory bic)
        {
            var s = bic.ToString();
            if (s.StartsWith("OST_", StringComparison.Ordinal)) s = s.Substring(4);

            // Split PascalCase into words: "CableTrayFitting" -> "Cable Tray Fitting".
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------- persistence

        // A filter the user has to re-set on every Revit start is not a filter, so
        // the category selection is written beside the log. Deliberately a plain
        // text format rather than JSON: this project multi-targets net48 and
        // net10.0-windows, and a hand-rolled reader avoids taking a serializer
        // dependency that has to satisfy both.

        /// <summary>
        /// The model these settings belong to, as <see cref="ModelKey"/> names it.
        /// Null before any document has been opened.
        /// </summary>
        private static string _modelKey;

        /// <summary>This model's identity per <see cref="ModelIdentity"/>, or "" for a standalone
        /// or detached model. Written as a comment line whenever this model's file is saved, so a
        /// later load can tell whether the SAME model, or a different one sharing its title, owns it.</summary>
        private static string _identity = "";

        // The (title, identity) pair the collision guard last resolved a file for, and what it
        // resolved to — see ClashStatusStore.LoadFor's class comment for why LoadFor must skip
        // the guard's disk I/O when neither has changed since last time.
        private static string _guardTitleSeen, _guardIdentitySeen, _guardResolvedKey;

        public static string ModelName { get { return _modelKey; } }

        private static string Folder
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiveClash");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>
        /// The settings shared by every model that has never been configured. Also
        /// where settings went before they were per-model, so an existing install
        /// keeps its configuration.
        /// </summary>
        public static string DefaultSettingsPath { get { return Path.Combine(Folder, "settings.txt"); } }

        /// <summary>
        /// Where THIS model's settings live.
        ///
        /// Settings used to be one machine-wide file, which meant opening a second
        /// project silently inherited the first one's filter. That is quiet in the
        /// worst way: an exclusion written for one library's naming ("bwic",
        /// "access zone") either matches nothing in the next project — noisy, you
        /// notice — or matches something real and drops it without a word. Status is
        /// already per-model; the filter that produced it has to be too.
        /// </summary>
        public static string SettingsPath
        {
            get
            {
                if (string.IsNullOrEmpty(_modelKey)) return DefaultSettingsPath;
                return Path.Combine(SettingsDir, _modelKey + ".txt");
            }
        }

        private static string SettingsDir
        {
            get
            {
                var dir = Path.Combine(Folder, "settings");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>
        /// Switch to a document's own settings. A model with none yet starts from the machine
        /// default rather than from nothing — inheriting a sensible starting point is fine, it is
        /// inheriting it SILENTLY AND FOREVER that was wrong. The first Save() writes the model's
        /// own file and the two diverge.
        ///
        /// Cheap to call repeatedly, on purpose — see ClashStatusStore.LoadFor's class comment:
        /// this runs on the same 5-second background tick, and an unchanged document must cost
        /// nothing beyond a couple of string compares.
        /// </summary>
        public static void LoadFor(Document doc)
        {
            RevitUser.Note(doc);

            string titleKey = null;
            try { if (doc != null && doc.IsValidObject) titleKey = ModelKey.FileSafe(doc); }
            catch (Exception ex) { Log.Warn("Could not derive a model key for settings", ex); }
            if (string.IsNullOrEmpty(titleKey)) return;

            string identity = ModelIdentity.Compute(doc);
            string key;
            string[] titleLines = null;
            bool titleExists = false;

            if (titleKey == _guardTitleSeen && identity == _guardIdentitySeen)
            {
                key = _guardResolvedKey;
            }
            else
            {
                key = titleKey;

                // The collision guard: see ModelIdentity's header comment (and its limits).
                if (!string.IsNullOrEmpty(identity))
                {
                    var titlePath = Path.Combine(SettingsDir, titleKey + ".txt");
                    titleExists = File.Exists(titlePath);
                    if (titleExists) titleLines = ModelIdentity.ReadLines(titlePath);   // retried — see its own comment
                    string recorded = titleLines == null ? "" : ModelIdentity.RecordedIn(titleLines);

                    string claim;
                    key = ModelIdentity.Resolve(titleKey, titleExists, recorded, identity, out claim);
                    if (claim != null) ModelIdentity.Claim(titlePath, claim);
                }

                _guardTitleSeen = titleKey;
                _guardIdentitySeen = identity;
                _guardResolvedKey = key;
            }

            if (key == _modelKey) return;               // already on this model

            _modelKey = key;
            _identity = identity ?? "";

            // Reuse the title file's lines already read above when the resolved key IS the title
            // key (the common, no-collision case) and it genuinely exists — avoids reading the
            // same bytes twice, the way ClashStatusStore/ResolvedLog already do.
            if (key == titleKey && titleExists && titleLines != null)
            {
                _current = FromLines(titleLines);
                Log.Info("Settings: loaded this model's own file, " + Path.Combine(SettingsDir, key + ".txt"));
            }
            else
            {
                var path = SettingsPath;
                bool own = File.Exists(path);
                _current = Load(own ? path : DefaultSettingsPath);

                Log.Info("Settings: " + (own
                    ? "loaded this model's own file, " + path
                    : "no file for '" + key + "' yet; seeded from " + DefaultSettingsPath));
            }
        }

        public void Save()
        {
            try
            {
                var lines = ToLines();
                // The identity claim is metadata about WHICH model owns this file, not a shared
                // rule — it must never travel through FilterPreset, which exports exactly
                // ToLines()'s own return value and nothing else.
                if (!string.IsNullOrEmpty(_identity)) lines.Add(ModelIdentity.Line(_identity));
                File.WriteAllLines(SettingsPath, lines.ToArray());
            }
            catch (Exception ex) { Log.Warn("Could not save settings", ex); }
        }

        /// <summary>
        /// The persisted form, as text lines. Split out from <see cref="Save"/> so the
        /// multi-set round trip can be exercised headlessly, with no file and no Revit —
        /// see the FilterLogicChecks persistence block.
        /// </summary>
        public List<string> ToLines()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string>();

            // ---- global (whole-tool) settings: one copy, outside every set ----
            lines.Add("enabled=" + Enabled);
            lines.Add("clearance=" + ClearanceFeet.ToString(inv));
            lines.Add("budget=" + TimeBudgetMs);
            lines.Add("maxperupdate=" + MaxElementsPerUpdate);
            lines.Add("suppress=" + SuppressConnectedPairs);
            lines.Add("group=" + GroupResults);
            lines.Add("autoverify=" + AutoVerify);
            lines.Add("verifyslice=" + VerifySliceSize);
            lines.Add("verifyinterval=" + VerifyIntervalSeconds);
            lines.Add("groupdist=" + GroupingDistanceFeet.ToString(inv));
            lines.Add("hostvshost=" + CheckHostVsHost);
            lines.Add("hostvslinks=" + CheckHostVsLinks);
            lines.Add("activeset=" + ActiveSet);

            // Audit type-drops are GLOBAL: one list, applied to every set by the engine
            // (see AuditDrops). Written here outside the set blocks — a drop belongs to
            // the model, not to a single set.
            foreach (var d in AuditDrops)
                lines.Add("auditdrop=" + ParameterFilter.SerializeTypeKey(d));


            // ---- one block per clash set ----
            // Each set's category and filter lines keep the exact keys the
            // single-set format used ("source", "target", "s.../t..."); the block
            // markers scope them so the reader knows which set they belong to.
            // A file written with a single default set reads back identically to
            // the old format for anything that matters.
            for (int i = 0; i < Sets.Count; i++)
            {
                var set = Sets[i];
                lines.Add("set.begin=" + i);
                lines.Add("setname=" + (set.Name ?? "").Replace('\r', ' ').Replace('\n', ' '));
                lines.Add("setenabled=" + set.Enabled);
                if (set.ClearanceFeet.HasValue)
                    lines.Add("setclearance=" + set.ClearanceFeet.Value.ToString(inv));
                if (set.InsulationAware)
                    lines.Add("setinsulation=" + set.InsulationAware);
                // Sorted, not iterated as-is: SourceCategories/TargetCategories/DisabledLinks are
                // HashSets and LinkExclusions is a Dictionary of HashSets, none with a guaranteed
                // enumeration order — two sessions that ticked the exact same categories in a
                // different order (or just landed on a different hash-bucket layout) wrote out the
                // same rule as a different byte sequence. RulesFingerprint hashes these lines
                // verbatim, so identical rules could fingerprint as different. Sorting here makes
                // the file's own on-disk order deterministic wherever a set is saved, not only when
                // a fingerprint is taken. Found reviewing the whole codebase, 2026-09-27.
                var srcCats = new List<BuiltInCategory>(set.SourceCategories);
                srcCats.Sort((x, y) => ((int)x).CompareTo((int)y));
                foreach (var c in srcCats) lines.Add("source=" + c);
                var tgtCats = new List<BuiltInCategory>(set.TargetCategories);
                tgtCats.Sort((x, y) => ((int)x).CompareTo((int)y));
                foreach (var c in tgtCats) lines.Add("target=" + c);
                lines.AddRange(set.SourceFilter.Serialize("s"));
                lines.AddRange(set.TargetFilter.Serialize("t"));

                // Per-link control, inside the block so it travels with the set.
                // '|' in a link name is escaped to '/' as everywhere else in this format.
                var disabledLinks = new List<string>(set.DisabledLinks);
                disabledLinks.Sort(StringComparer.Ordinal);
                foreach (var ln in disabledLinks)
                    lines.Add("setlinkoff=" + (ln ?? "").Replace('|', '/'));
                var linkNames = new List<string>(set.LinkExclusions.Keys);
                linkNames.Sort(StringComparer.Ordinal);
                foreach (var linkName in linkNames)
                {
                    var cats = new List<BuiltInCategory>(set.LinkExclusions[linkName]);
                    cats.Sort((x, y) => ((int)x).CompareTo((int)y));
                    foreach (var c in cats)
                        lines.Add("setlinkexcl=" + (linkName ?? "").Replace('|', '/') + "|" + c);
                }

                lines.Add("set.end=" + i);
            }

            return lines;
        }

        /// <summary>
        /// Accumulator for one set block while <see cref="Load"/> reads it. Categories
        /// are gathered into lists and only committed at the end of the block, so the
        /// "only replace the defaults if the file actually named some" rule stays
        /// per-set: a block that lists no categories keeps the ClashSet's defaults.
        /// </summary>
        private class SetBlock
        {
            public readonly ClashSet Set = new ClashSet();
            public readonly List<BuiltInCategory> Srcs = new List<BuiltInCategory>();
            public readonly List<BuiltInCategory> Tgts = new List<BuiltInCategory>();
            public bool ClearedSource, ClearedTarget;

            public void Commit()
            {
                if (Srcs.Count > 0) { Set.SourceCategories.Clear(); foreach (var c in Srcs) if (c != BuiltInCategory.INVALID) Set.SourceCategories.Add(c); }
                if (Tgts.Count > 0) { Set.TargetCategories.Clear(); foreach (var c in Tgts) if (c != BuiltInCategory.INVALID) Set.TargetCategories.Add(c); }
            }
        }

        private static ClashSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new ClashSettings();
                return FromLines(File.ReadAllLines(path));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not load settings; using defaults", ex);
                return new ClashSettings();
            }
        }

        /// <summary>
        /// Parse the persisted line form — the inverse of <see cref="ToLines"/>. Split
        /// out from file IO so the multi-set round trip can be checked headlessly.
        ///
        /// Multi-set aware, and backward compatible: a legacy file has no
        /// set.begin/set.end markers, so its bare source/target/filter lines open one
        /// implicit block that becomes Sets[0] — identical to the old single-filter
        /// behaviour. A new file wraps each set in a block.
        /// </summary>
        public static ClashSettings FromLines(IEnumerable<string> rawLines)
        {
            var s = new ClashSettings();
            {
                var built = new List<SetBlock>();
                SetBlock cur = null;
                int pendingActive = 0;

                foreach (var raw in rawLines)
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq);
                    var v = line.Substring(eq + 1);

                    // Block markers. "begin" commits any open block first (defensive —
                    // a well-formed file always emits "end"), then opens a fresh one.
                    if (k == "set.begin")
                    {
                        if (cur != null) { cur.Commit(); built.Add(cur); }
                        cur = new SetBlock();
                        continue;
                    }
                    if (k == "set.end")
                    {
                        if (cur != null) { cur.Commit(); built.Add(cur); cur = null; }
                        continue;
                    }

                    // Per-set keys. Seeing one with no block open means a legacy file:
                    // open an implicit block that will become Sets[0].
                    switch (k)
                    {
                        case "setlinkoff":
                            if (cur == null) cur = new SetBlock();
                            if (!string.IsNullOrEmpty(v)) cur.Set.DisabledLinks.Add(v);
                            continue;
                        case "setlinkexcl":
                            {
                                if (cur == null) cur = new SetBlock();
                                int bar = v.LastIndexOf('|');
                                if (bar > 0)
                                {
                                    var ln = v.Substring(0, bar);
                                    var cat = Parse(v.Substring(bar + 1));
                                    if (cat != BuiltInCategory.INVALID)
                                    {
                                        HashSet<BuiltInCategory> set;
                                        if (!cur.Set.LinkExclusions.TryGetValue(ln, out set))
                                        { set = new HashSet<BuiltInCategory>(); cur.Set.LinkExclusions[ln] = set; }
                                        set.Add(cat);
                                    }
                                }
                            }
                            continue;
                        case "setname":    if (cur == null) cur = new SetBlock(); cur.Set.Name = v; continue;
                        case "setenabled": { if (cur == null) cur = new SetBlock(); bool se; if (bool.TryParse(v, out se)) cur.Set.Enabled = se; } continue;
                        case "setclearance":
                            {
                                if (cur == null) cur = new SetBlock();
                                double sc;
                                if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out sc))
                                    cur.Set.ClearanceFeet = sc;
                            }
                            continue;
                        case "setinsulation":
                            { if (cur == null) cur = new SetBlock(); bool ia; if (bool.TryParse(v, out ia)) cur.Set.InsulationAware = ia; }
                            continue;
                        case "source":     if (cur == null) cur = new SetBlock(); cur.Srcs.Add(Parse(v)); continue;
                        case "target":     if (cur == null) cur = new SetBlock(); cur.Tgts.Add(Parse(v)); continue;
                        case "activeset":  { int ai; if (int.TryParse(v, out ai)) pendingActive = ai; } continue;
                    }

                    // Filter lines (s.../t...) route through the ONE shared path so a
                    // new key can never be handled here and dropped by FilterPreset.Read,
                    // or vice versa. They belong to the current block.
                    if (k.Length >= 2 && (k[0] == 's' || k[0] == 't')
                        && ParameterFilter.IsFilterKey(k.Substring(1)))
                    {
                        if (cur == null) cur = new SetBlock();
                        ParameterFilter.ApplyFilterLine(cur.Set.SourceFilter, cur.Set.TargetFilter, k, v,
                            ref cur.ClearedSource, ref cur.ClearedTarget);
                        continue;
                    }

                    // Everything else is a global (whole-tool) key.
                    switch (k)
                    {
                        case "clearance":
                            double d;
                            if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out d)) s.ClearanceFeet = d;
                            break;
                        case "budget":
                            int b; if (int.TryParse(v, out b)) s.TimeBudgetMs = b; break;
                        case "maxperupdate":
                            int m; if (int.TryParse(v, out m)) s.MaxElementsPerUpdate = m; break;
                        case "enabled":
                            { bool en; if (bool.TryParse(v, out en)) s.Enabled = en; } break;
                        case "suppress":
                            bool sp; if (bool.TryParse(v, out sp)) s.SuppressConnectedPairs = sp; break;
                        case "group":
                            bool gr; if (bool.TryParse(v, out gr)) s.GroupResults = gr; break;
                        case "autoverify":
                            bool av; if (bool.TryParse(v, out av)) s.AutoVerify = av; break;
                        case "verifyslice":
                            int vs; if (int.TryParse(v, out vs) && vs > 0) s.VerifySliceSize = vs; break;
                        case "verifyinterval":
                            int vi; if (int.TryParse(v, out vi) && vi > 0) s.VerifyIntervalSeconds = vi; break;
                        case "groupdist":
                            double gd;
                            if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out gd)) s.GroupingDistanceFeet = gd;
                            break;
                        case "hostvshost":
                            bool hh; if (bool.TryParse(v, out hh)) s.CheckHostVsHost = hh; break;
                        case "hostvslinks":
                            bool hl; if (bool.TryParse(v, out hl)) s.CheckHostVsLinks = hl; break;
                        case "auditdrop":
                            { var ad = ParameterFilter.ParseTypeKey(v); if (ad != null) s.AuditDrops.Add(ad); }
                            break;
                        // NOTE: the older global "linkexcl=" key is deliberately not read.
                        // Per-link control moved into the set block (setlinkexcl/setlinkoff)
                        // before it ever shipped, so there is nothing in the field to migrate.
                    }
                }

                // Close a block left open at EOF (legacy file, or a truncated last block).
                if (cur != null) { cur.Commit(); built.Add(cur); }

                // Install what we built. An empty/corrupt file yields no blocks; keep the
                // constructor's single default set rather than switching the tool off.
                if (built.Count > 0)
                {
                    // CAPPED HERE, because this is where a file becomes state. Anything
                    // past the limit is dropped and SAID SO — silently running eight sets
                    // because a colleague's preset had eight would show up as "live
                    // checking got slow", which is nobody's idea of a diagnosable symptom.
                    int keep = built.Count;
                    if (keep > MaxSets)
                    {
                        Log.Warn("Settings named " + built.Count + " clash set(s); keeping the first "
                                 + MaxSets + " and dropping " + (built.Count - MaxSets)
                                 + ". The limit is " + MaxSets + " — a file naming more was "
                                 + "hand-edited or came from another tool.");
                        keep = MaxSets;
                    }

                    s.Sets.Clear();
                    for (int i = 0; i < keep; i++) s.Sets.Add(built[i].Set);
                }
                s.ActiveSet = (pendingActive >= 0 && pendingActive < s.Sets.Count) ? pendingActive : 0;

                // Fold any per-set audit drops the file still carries (sxtype/txtype) up
                // into the global list. See FoldAuditDropsGlobal.
                s.FoldAuditDropsGlobal();
            }
            return s;
        }

        /// <summary>
        /// Lift every set's own <see cref="ParameterFilter.ExcludedTypes"/> up into the
        /// global <see cref="AuditDrops"/> list (de-duped) and clear them from the sets.
        /// Audit drops are model-wide by design, so a per-set drop — from a legacy settings
        /// file or an old exported preset — becomes a single global one applied to every
        /// set by the engine. Idempotent: sets already clear leave AuditDrops untouched.
        /// </summary>
        public void FoldAuditDropsGlobal()
        {
            var seen = new HashSet<string>();
            foreach (var d in AuditDrops) seen.Add(DropId(d));
            foreach (var set in Sets)
            {
                GatherDrops(set.SourceFilter, AuditDrops, seen);
                GatherDrops(set.TargetFilter, AuditDrops, seen);
            }
        }

        /// <summary>
        /// Add the given drops to <see cref="AuditDrops"/>, skipping any already present by
        /// family+type. Copies each so the lists never share a mutable object. Used to merge
        /// an imported preset's drops into the live global list rather than replacing them.
        /// </summary>
        public void MergeAuditDrops(IEnumerable<TypeKey> incoming)
        {
            if (incoming == null) return;
            var seen = new HashSet<string>();
            foreach (var d in AuditDrops) seen.Add(DropId(d));
            foreach (var d in incoming)
                if (d != null && !string.IsNullOrEmpty(d.Family) && seen.Add(DropId(d)))
                    AuditDrops.Add(new TypeKey { Family = d.Family, Type = d.Type });
        }

        /// <summary>Case/whitespace-insensitive identity of a family+type drop, for de-duping.</summary>
        private static string DropId(TypeKey k)
        {
            if (k == null) return "";
            return (k.Family ?? "").Trim().ToLowerInvariant() + (char)31 + (k.Type ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Lift a filter's own <see cref="ParameterFilter.ExcludedTypes"/> into the global
        /// list (de-duped) and clear them, so a legacy per-set drop becomes a single global
        /// one. Copies the TypeKey so the two lists never share a mutable object.
        /// </summary>
        private static void GatherDrops(ParameterFilter f, List<TypeKey> into, HashSet<string> seen)
        {
            if (f == null || f.ExcludedTypes.Count == 0) return;
            foreach (var d in f.ExcludedTypes)
                if (d != null && !string.IsNullOrEmpty(d.Family) && seen.Add(DropId(d)))
                    into.Add(new TypeKey { Family = d.Family, Type = d.Type });
            f.ExcludedTypes.Clear();
        }

        private static BuiltInCategory Parse(string name)
        {
            try { return (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), name); }
            catch { return BuiltInCategory.INVALID; }
        }
    }
}
