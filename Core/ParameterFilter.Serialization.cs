using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// The filter's serialization I/O, split out of ParameterFilter.cs so the big
    /// class is not also the persistence format. Serialize + ApplyLine are the
    /// round trip; ApplyFilterLine + IsFilterKey are the ONE dispatch path both
    /// ClashSettings.Load and FilterPreset.Read share. Revit-free, so it links into
    /// the headless CI test project. Same partial class -- no API change.
    /// </summary>
    public partial class ParameterFilter
    {
        /// <summary>
        /// One family+type pair as "family|type", with any '|' inside a name escaped to
        /// '/' (the same one-way escape every other field in this format uses). Shared by
        /// the filter's own xtype lines and ClashSettings' global auditdrop lines, so both
        /// sides use ONE format and ONE parser. Revit-free, so the headless gate can
        /// exercise it.
        /// </summary>
        public static string SerializeTypeKey(TypeKey k)
        {
            if (k == null) return "|";
            return (k.Family ?? "").Replace('|', '/') + "|" + (k.Type ?? "").Replace('|', '/');
        }

        /// <summary>Inverse of <see cref="SerializeTypeKey"/>. Null if the line has no field separator.</summary>
        public static TypeKey ParseTypeKey(string value)
        {
            var p = (value ?? "").Split('|');
            if (p.Length < 2) return null;
            return new TypeKey { Family = p[0], Type = p[1] };
        }

        public List<string> Serialize(string prefix)
        {
            var lines = new List<string>();
            lines.Add(prefix + "kw=" + (Keyword ?? "").Replace('|', '/'));
            lines.Add(prefix + "xkw=" + (ExcludeKeyword ?? "").Replace('|', '/'));
            foreach (var p in Parameters)
                lines.Add(prefix + "param=" + p.Scope + "|" + (p.Name ?? "").Replace('|', '/'));
            foreach (var v in Picked)
                lines.Add(prefix + "pick=" + v.Scope + "|" + (v.ParameterName ?? "").Replace('|', '/')
                          + "|" + (v.Value ?? "").Replace('|', '/'));
            foreach (var v in Excluded)
                lines.Add(prefix + "xpick=" + v.Scope + "|" + (v.ParameterName ?? "").Replace('|', '/')
                          + "|" + (v.Value ?? "").Replace('|', '/'));
            foreach (var v in ExcludedTypes)
                lines.Add(prefix + "xtype=" + SerializeTypeKey(v));
            foreach (var v in OnlyValues)
                lines.Add(prefix + "only=" + v.Scope + "|" + (v.ParameterName ?? "").Replace('|', '/')
                          + "|" + (v.Value ?? "").Replace('|', '/'));
            // The leading index is what keeps two rules apart when they share a scope,
            // an operator and a parameter. Without it the reader had nothing else to
            // match on, so two rules collapsed into one with two values on reload —
            // and two values on one rule are an OR, not an AND. See ApplyLine.
            for (int ri = 0; ri < Rules.Count; ri++)
            {
                var r = Rules[ri];
                var head = prefix + "rule=" + ri + "|" + r.Scope + "|" + r.Op + "|"
                         + (r.ParameterName ?? "").Replace('|', '/') + "|";
                if (r.Op == RuleOp.IsEmpty || r.Op == RuleOp.IsNotEmpty || r.Values.Count == 0)
                    lines.Add(head);
                else
                    foreach (var v in r.Values) lines.Add(head + (v ?? "").Replace('|', '/'));
            }

            // Groups: one "group=<i>|<Join>" declaring the set, then its rules as
            // "grule=<gi>|<ri>|Scope|Op|Param|Value" (one value per line, same as flat
            // rules). The rule index is needed here for exactly the reason it is needed
            // above — two rules inside one group could share a scope, operator and
            // parameter just as easily as two at the top level.
            for (int gi = 0; gi < Groups.Count; gi++)
            {
                var grp = Groups[gi];
                lines.Add(prefix + "group=" + gi + "|" + grp.Join);
                for (int ri = 0; ri < grp.Rules.Count; ri++)
                {
                    var r = grp.Rules[ri];
                    var head = prefix + "grule=" + gi + "|" + ri + "|" + r.Scope + "|" + r.Op + "|"
                             + (r.ParameterName ?? "").Replace('|', '/') + "|";
                    if (r.Op == RuleOp.IsEmpty || r.Op == RuleOp.IsNotEmpty || r.Values.Count == 0)
                        lines.Add(head);
                    else
                        foreach (var v in r.Values) lines.Add(head + (v ?? "").Replace('|', '/'));
                }
            }
            return lines;
        }

        /// <summary>
        /// The largest rule or group index a line may name.
        ///
        /// Indexed lines are addressed BY POSITION, so reading one pads the list up to
        /// that index — and an index is just digits in a text file. Unbounded, a single
        /// line saying `rule=2000000|...` allocates two million Rule objects (measured:
        /// ~145 MB, 415 ms) and `int.MaxValue` is a thousand times that again. The padded
        /// rules are inert rather than corrupting, so this is a resource failure, not a
        /// wrong answer — but a resource failure is still a failure.
        ///
        /// The realistic route in is not a hand-edited settings file, it is a SHARED
        /// .lcfilter preset: the one file this feature exists to hand to a colleague,
        /// opened behind a "Replace the current filter?" dialog people click through.
        ///
        /// 1,000 is far past anything usable — the largest real filter on this machine
        /// declares 68 rule lines, and a filter with a thousand rules is unreadable in
        /// the window long before it is slow. A line beyond it is dropped like any other
        /// malformed line: it cannot be what somebody meant.
        /// </summary>
        private const int MaxIndex = 1000;

        public void ApplyLine(string key, string value)
        {
            try
            {
                if (key == "kw") { Keyword = value; return; }
                if (key == "xkw") { ExcludeKeyword = value; return; }

                var p = value.Split('|');
                if (key == "rule" && p.Length >= 3)
                {
                    // rule=<i>|Scope|Op|Param|Value   current
                    // rule=Scope|Op|Param|Value       before rules carried an index
                    //
                    // Both are read, and the FIRST FIELD says which: an index is an
                    // integer, a ParamScope is a name, and no version of Serialize has
                    // ever written a scope numerically. That keeps every filter and
                    // preset already on disk loading exactly as it did.
                    //
                    // Why the index exists: without it the only thing to match a value
                    // line against was (scope, operator, parameter), so two DISTINCT
                    // rules sharing that triple were indistinguishable from one rule
                    // with two values. "Description contains chilled" AND "Description
                    // contains supply" came back as a single rule holding both — an OR.
                    // The filter widened itself every time it was reloaded, and nothing
                    // said so. Legacy lines below still merge, because for them it is
                    // genuinely unknowable; anything written since is exact.
                    int at;
                    bool indexed = int.TryParse(p[0], out at) && at >= 0;
                    if (indexed && at > MaxIndex) return;   // see MaxIndex
                    int f = indexed ? 1 : 0;
                    if (p.Length < f + 3) return;

                    var scope = (ParamScope)Enum.Parse(typeof(ParamScope), p[f]);
                    var op = (RuleOp)Enum.Parse(typeof(RuleOp), p[f + 1]);
                    var name = p[f + 2];
                    var val = p.Length >= f + 4 ? p[f + 3] : "";

                    Rule found = null;

                    if (indexed)
                    {
                        // Addressed by position, so repeated lines for one index build up
                        // that rule's values and a new index starts a new rule.
                        while (Rules.Count <= at) Rules.Add(new Rule());
                        found = Rules[at];
                        found.Scope = scope;
                        found.Op = op;
                        found.ParameterName = name;
                    }
                    else
                    {
                        foreach (var r in Rules)
                            if (r.Scope == scope && r.Op == op
                                && string.Equals(r.ParameterName, name, StringComparison.OrdinalIgnoreCase))
                            { found = r; break; }
                        if (found == null)
                        {
                            found = new Rule { Scope = scope, Op = op, ParameterName = name };
                            Rules.Add(found);
                        }
                    }

                    if (val.Length > 0) found.Values.Add(val);
                }
                else if (key == "group" && p.Length >= 2)
                {
                    // group=<index>|<Join>
                    int gi = int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (gi < 0 || gi > MaxIndex) return;   // see MaxIndex
                    while (Groups.Count <= gi) Groups.Add(new RuleGroup());
                    Groups[gi].Join = (RuleJoin)Enum.Parse(typeof(RuleJoin), p[1]);
                }
                else if (key == "grule" && p.Length >= 4)
                {
                    // grule=<gi>|<ri>|Scope|Op|Param|Value   current
                    // grule=<gi>|Scope|Op|Param|Value        before group rules were indexed
                    //
                    // Same discriminator as flat rules, one field along: the group index
                    // is always first, and what follows is either a rule index (integer)
                    // or a scope (name). Same bug, same fix — two rules in one group
                    // sharing a scope, operator and parameter used to merge into one.
                    int gi = int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (gi < 0 || gi > MaxIndex) return;   // see MaxIndex

                    int at;
                    bool indexed = int.TryParse(p[1], out at) && at >= 0;
                    if (indexed && at > MaxIndex) return;
                    int f = indexed ? 2 : 1;
                    if (p.Length < f + 3) return;

                    // Padded only after both indexes are known good, so a bad rule index
                    // cannot leave empty groups behind on its way out.
                    while (Groups.Count <= gi) Groups.Add(new RuleGroup());

                    var gscope = (ParamScope)Enum.Parse(typeof(ParamScope), p[f]);
                    var gop = (RuleOp)Enum.Parse(typeof(RuleOp), p[f + 1]);
                    var gname = p[f + 2];
                    var gval = p.Length >= f + 4 ? p[f + 3] : "";

                    Rule gfound = null;

                    if (indexed)
                    {
                        while (Groups[gi].Rules.Count <= at) Groups[gi].Rules.Add(new Rule());
                        gfound = Groups[gi].Rules[at];
                        gfound.Scope = gscope;
                        gfound.Op = gop;
                        gfound.ParameterName = gname;
                    }
                    else
                    {
                        foreach (var r in Groups[gi].Rules)
                            if (r.Scope == gscope && r.Op == gop
                                && string.Equals(r.ParameterName, gname, StringComparison.OrdinalIgnoreCase))
                            { gfound = r; break; }
                        if (gfound == null)
                        {
                            gfound = new Rule { Scope = gscope, Op = gop, ParameterName = gname };
                            Groups[gi].Rules.Add(gfound);
                        }
                    }

                    if (gval.Length > 0) gfound.Values.Add(gval);
                }
                else if (key == "xtype" && p.Length >= 2)
                {
                    ExcludedTypes.Add(new TypeKey { Family = p[0], Type = p[1] });
                }
                else if (key == "param" && p.Length >= 2)
                {
                    Parameters.Add(new ParamRef
                    {
                        Scope = (ParamScope)Enum.Parse(typeof(ParamScope), p[0]),
                        Name = p[1]
                    });
                }
                else if ((key == "pick" || key == "xpick" || key == "only") && p.Length >= 3)
                {
                    var vp = new ValuePick
                    {
                        Scope = (ParamScope)Enum.Parse(typeof(ParamScope), p[0]),
                        ParameterName = p[1],
                        Value = p[2]
                    };
                    if (key == "only") OnlyValues.Add(vp);
                    else if (key == "xpick") Excluded.Add(vp);
                    else Picked.Add(vp);
                }
            }
            catch { /* a malformed line is dropped, not fatal */ }
        }

        /// <summary>
        /// The key stems <see cref="Serialize"/> emits and <see cref="ApplyLine"/>
        /// understands, WITHOUT the s/t side prefix. The single source of truth for
        /// "is this a filter key", so a reader asks here instead of hardcoding the set.
        ///
        /// The whole "dispatch switch eats new keys" trap — four separate incidents —
        /// existed only because two readers (ClashSettings.Load, FilterPreset.Read) each
        /// kept their own copy of this list and drifted. They now both route through
        /// <see cref="ApplyFilterLine"/>, which consults this; a completeness test
        /// (serialize emits nothing IsFilterKey rejects) locks the last hand-sync point.
        /// </summary>
        private static readonly HashSet<string> SerialKeys = new HashSet<string>(StringComparer.Ordinal)
        { "kw", "xkw", "param", "pick", "xpick", "only", "xtype", "rule", "group", "grule" };

        public static bool IsFilterKey(string stem) { return stem != null && SerialKeys.Contains(stem); }

        /// <summary>
        /// Route one persisted line to the right side's <see cref="ApplyLine"/> if it is a
        /// filter line ("s.../t..."), clearing that side's default Parameters on first use.
        /// Returns true when it handled the line, false to let the caller's own switch try
        /// the non-filter keys. THE one place both the settings reader and the preset
        /// reader share, so a new key can never be handled by one and dropped by the other.
        ///
        /// Keys off the s/t PREFIX, not a substring, so the settings key "group"
        /// (GroupResults) is never mistaken for the filter's "sgroup"/"tgroup".
        /// </summary>
        public static bool ApplyFilterLine(ParameterFilter source, ParameterFilter target,
            string key, string value, ref bool clearedSource, ref bool clearedTarget)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 2) return false;
            char side = key[0];
            if (side != 's' && side != 't') return false;
            string stem = key.Substring(1);
            if (!IsFilterKey(stem)) return false;

            if (side == 's')
            {
                if (!clearedSource) { source.Parameters.Clear(); clearedSource = true; }
                source.ApplyLine(stem, value);
            }
            else
            {
                if (!clearedTarget) { target.Parameters.Clear(); clearedTarget = true; }
                target.ApplyLine(stem, value);
            }
            return true;
        }
    }
}
