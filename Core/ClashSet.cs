using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// One clash configuration in the matrix: a source scope, a target scope, and the
    /// rules for each. The tool runs a LIST of these, so pairings that need different
    /// rules can run side by side — MEP-vs-MEP wants a System Type rule its targets
    /// carry, MEP-vs-structure wants none because structure has no System Type and
    /// lives on different worksets. One filter cannot hold both; two sets can.
    ///
    /// The audit type-drops are deliberately NOT here — they are model-wide non-clash
    /// geometry (access zones, modelled openings) and apply to every set, so they live
    /// once on <see cref="ClashSettings"/>.
    /// </summary>
    public class ClashSet
    {
        public string Name = "Default";
        public bool Enabled = true;

        /// <summary>
        /// This set's own soft-clash clearance override, in feet — null means "use the
        /// model's clearance" (<see cref="ClashSettings.ClearanceFeet"/>), which is what
        /// every set had before this existed and what a legacy settings file with no
        /// setclearance= line still means. Read through
        /// <see cref="ClashSettings.ClearanceFor"/>, never directly — that is also the
        /// method <see cref="PairVerifier"/> uses to verify against the LARGEST clearance
        /// of any set the pair could belong to, not just the one that found it.
        /// </summary>
        public double? ClearanceFeet;

        /// <summary>
        /// When on, a source pipe or duct checked by this set also has its own
        /// insulation (pipe/duct insulation — never lining, which sits inside the duct
        /// and never enlarges the outer size) tested against the same candidates, ON
        /// BEHALF OF the pipe: a hit reports the PIPE as host, keyed exactly as a
        /// bare-pipe hit would be, so an insulation-only clash never duplicates one the
        /// bare geometry already found. Off by default — it can only ADD rows to what
        /// the bare-pipe check finds, never remove any, so an existing model's results
        /// do not move until someone opts in. See <see cref="ClashResult.Insulated"/>.
        /// </summary>
        public bool InsulationAware;

        public HashSet<BuiltInCategory> SourceCategories = new HashSet<BuiltInCategory>(ClashSettings.Selectable);
        public HashSet<BuiltInCategory> TargetCategories = new HashSet<BuiltInCategory>(ClashSettings.Selectable);

        public ParameterFilter SourceFilter = ParameterFilter.Default();
        public ParameterFilter TargetFilter = ParameterFilter.Default();

        /// <summary>
        /// Per-link target-category exclusions for THIS set: from the link named by the
        /// key, do not collect elements in the listed categories. Per-set (unlike the
        /// model-wide audit drops) because a link's usefulness depends on the pairing —
        /// an MEP-vs-structure set wants the structural link, an MEP-vs-MEP set does not.
        /// Empty = the link contributes every one of this set's target categories.
        /// Persisted inside the set block as sxlinkexcl=&lt;link&gt;|&lt;category&gt;.
        /// </summary>
        public Dictionary<string, HashSet<BuiltInCategory>> LinkExclusions
            = new Dictionary<string, HashSet<BuiltInCategory>>();

        /// <summary>
        /// Links this set does not check against at all — the per-link on/off gate.
        /// A disabled link contributes nothing to this set, whatever its category
        /// exclusions say. Persisted as setlinkoff=&lt;link&gt; inside the set block.
        /// </summary>
        public HashSet<string> DisabledLinks = new HashSet<string>();
    }
}
