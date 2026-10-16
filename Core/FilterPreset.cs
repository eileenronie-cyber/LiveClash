using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// A filter, in a file you can hand to someone.
    ///
    /// Settings are per-model on purpose — the exclusions that fit one project
    /// silently misfit the next, which is a bug this tool has already had once. But
    /// per-model storage makes the OPPOSITE case awkward: two people on the same
    /// project, or the same person on next year's tower for the same client, both
    /// want the filter that took an afternoon to work out. Copying a file from
    /// %LOCALAPPDATA% and renaming it to match a model key is not a workflow.
    ///
    /// So the filter travels as a preset: categories and rules only, no results, no
    /// decisions, no model identity. Import lands it in the current model's settings
    /// and it becomes that model's own from then on.
    /// </summary>
    public static class FilterPreset
    {
        public const string Extension = ".lcfilter";
        private const string Magic = "# LiveClash filter preset v1";

        /// <summary>Whole-tool keys a preset carries — everything ELSE (budget, slice sizes,
        /// grouping, the live on/off switch) is how this machine likes to work, not part of a
        /// shared filter.</summary>
        private static readonly HashSet<string> CarryKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "clearance", "suppress", "hostvshost", "hostvslinks", "auditdrop", "activeset"
        };

        /// <summary>
        /// The lines of <c>s.ToLines()</c> a preset actually carries: every set block verbatim,
        /// plus the whole-tool keys that describe WHAT counts as a clash rather than how this
        /// machine likes to work. Shared by <see cref="Export"/> and by <see cref="RulesFingerprint"/>,
        /// so a fingerprint changes exactly when an exported preset would — the identity claim
        /// ModelIdentity writes onto the settings file is not among them: that says WHICH model
        /// owns the file, never a rule, and must never travel through a preset or into a fingerprint.
        /// </summary>
        public static List<string> ShareableLines(ClashSettings s)
        {
            var kept = new List<string>();
            if (s == null) return kept;

            bool inSet = false;
            foreach (var l in s.ToLines())
            {
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                var key = l.Substring(0, eq);

                if (key == "set.begin") { inSet = true; kept.Add(l); continue; }
                if (key == "set.end") { inSet = false; kept.Add(l); continue; }

                if (inSet || CarryKeys.Contains(key)) kept.Add(l);
            }
            return kept;
        }

        public static void Export(string path, ClashSettings s, string fromModel)
        {
            if (string.IsNullOrEmpty(path) || s == null) return;

            var lines = new List<string>();
            lines.Add(Magic);
            lines.Add("# exported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                      + (string.IsNullOrEmpty(fromModel) ? "" : " from " + fromModel));
            lines.Add("#");
            lines.Add("# Categories and filter rules only. Nothing here identifies a model,");
            lines.Add("# and importing never touches clash results or review decisions.");
            lines.Add("");

            // EVERY SET, through the settings file's own serializer.
            //
            // This used to write s.SourceCategories, s.SourceFilter and
            // s.Active.LinkExclusions — all compatibility accessors onto the ACTIVE set.
            // Someone who had built a three-set clash matrix and exported it to hand to a
            // colleague sent one set, silently, and the import mirrored the same limit. It
            // was also a second, weaker implementation of a format that already exists:
            // ToLines/FromLines round-trip every set and are covered by the headless tests.
            //
            // Only the whole-tool tuning is dropped, because a preset is about WHAT gets
            // reported, not how fast the machine you sent it to should work: budget, slice
            // sizes, grouping and the live on/off switch stay local. Everything inside a
            // set block travels verbatim.
            lines.AddRange(ShareableLines(s));

            File.WriteAllLines(path, lines.ToArray(), new System.Text.UTF8Encoding(true));
            Log.Info("Filter preset exported to " + path + " — " + s.Sets.Count + " set(s).");
        }

        /// <summary>What an import would change, without changing it.</summary>
        public class Summary
        {
            public int SourceCategories;
            public int TargetCategories;
            public int AuditDrops;
            public string SourceRules = "";
            public string TargetRules = "";
            public string ExportedFrom = "";
            public bool Valid;
            public string Problem = "";

            /// <summary>How many clash sets the preset carries. The category and rule
            /// counts above describe the ACTIVE one; this says how many more there are,
            /// so importing a matrix cannot look like importing a single filter.</summary>
            public int Sets = 1;

            /// <summary>
            /// How many set blocks the FILE named, which exceeds <see cref="Sets"/> when
            /// the loader capped it at <see cref="ClashSettings.MaxSets"/>. Zero on a
            /// legacy preset with no blocks at all. Kept apart from Sets so the import
            /// dialog can say "six named, four imported" rather than quietly showing four.
            /// </summary>
            public int DeclaredSets;
        }

        public static Summary Read(string path, out ClashSettings loaded)
        {
            loaded = null;
            var sum = new Summary();

            try
            {
                if (!File.Exists(path)) { sum.Problem = "That file no longer exists."; return sum; }

                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || !lines[0].TrimStart('﻿').StartsWith("# LiveClash filter preset"))
                {
                    sum.Problem = "That is not a Live Clash filter preset.";
                    return sum;
                }

                // Parse with the SETTINGS format's own reader, so a preset and the settings
                // file cannot disagree about the format they share. This was a second,
                // hand-rolled parser that understood only the active set; FromLines reads
                // every set block, and treats a preset with no blocks as one legacy set,
                // so presets written before this still import.
                var s = ClashSettings.FromLines(lines);

                // Only the header comment identifies where it came from, and FromLines
                // skips any line without '=' — so scrape it here.
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] != '#') continue;
                    int at = line.IndexOf(" from ", StringComparison.Ordinal);
                    if (at > 0) { sum.ExportedFrom = line.Substring(at + 6).Trim(); break; }
                }

                if (s.SourceCategories.Count == 0 || s.TargetCategories.Count == 0)
                {
                    sum.Problem = "That preset has an empty side — a clash is a pair, so "
                                + "nothing could ever be reported from it.";
                    return sum;
                }

                // An OLD preset carries its audit drops as per-set sxtype/txtype; fold them
                // up into the global list (and drop new-format auditdrop= lines land there
                // directly), so ApplyTo below merges one global list, never per-set drops.
                s.FoldAuditDropsGlobal();

                sum.SourceCategories = s.SourceCategories.Count;
                sum.TargetCategories = s.TargetCategories.Count;
                sum.AuditDrops = s.AuditDrops.Count;
                sum.SourceRules = s.SourceFilter.Summary();
                sum.TargetRules = s.TargetFilter.Summary();
                sum.Sets = s.Sets.Count;

                // HOW MANY THE FILE NAMED, which is not always how many we kept: FromLines
                // caps at ClashSettings.MaxSets. Deliberately NOT sum.Problem — that field
                // is only ever shown when Valid is false, so putting it there would have
                // been a message nobody could ever see. This one is real and the preset is
                // still importable; the import dialog says so before replacing anything.
                foreach (var raw in lines)
                    if (raw != null && raw.TrimStart().StartsWith("set.begin=")) sum.DeclaredSets++;

                sum.Valid = true;
                loaded = s;
                return sum;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read the filter preset " + path, ex);
                sum.Problem = "That file could not be read: " + ex.Message;
                return sum;
            }
        }

        /// <summary>
        /// Copy a preset into the live settings. Deliberately narrow: categories and
        /// rules only. Timings, grouping distance and the verification cadence are how
        /// THIS machine likes to work, not part of a shared filter — but a set's own
        /// clearance override and insulation-aware flag ARE part of it, on purpose:
        /// they answer "what counts as a clash for this pairing", the same question
        /// categories/rules/link scoping already answer, not "how this machine likes to
        /// work". They live on ClashSet and travel wholesale with it below for that
        /// reason, same as everything else scoping already carries.
        /// </summary>
        public static void ApplyTo(ClashSettings target, ClashSettings preset)
        {
            if (target == null || preset == null) return;

            // EVERY SET replaces every set. Categories, rules and per-link scoping all
            // live on the ClashSet, so taking the list wholesale carries them together —
            // and a preset built from a three-set matrix arrives as three sets rather
            // than quietly collapsing to one. The preset object is discarded straight
            // after this, so its sets can be adopted rather than cloned.
            //
            // Replace, not merge: the preset's scoping IS the thing being imported, and
            // a merge would leave the importer's old sets running beside it.
            if (preset.Sets.Count > 0)
            {
                target.Sets.Clear();
                foreach (var set in preset.Sets) target.Sets.Add(set);
                target.ActiveSet = preset.ActiveSet;   // the Active getter clamps a stale index
            }

            // Audit drops are global. Merge (de-duped), not replace: importing a filter adds
            // its known-noise drops without discarding drops the target already had for its
            // other sets. The preset's drops are global by the time Read has folded them.
            target.MergeAuditDrops(preset.AuditDrops);

            target.ClearanceFeet = preset.ClearanceFeet;
            target.SuppressConnectedPairs = preset.SuppressConnectedPairs;
            target.CheckHostVsHost = preset.CheckHostVsHost;
            target.CheckHostVsLinks = preset.CheckHostVsLinks;
        }

    }
}
