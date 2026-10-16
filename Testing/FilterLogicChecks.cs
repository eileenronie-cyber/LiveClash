using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The filter's serialization and legacy-to-rules migration checks, kept in one
    /// place so they run in TWO contexts from a SINGLE copy of the assertions:
    ///   - inside Revit, via <see cref="SelfTest"/> over the dev bridge, and
    ///   - headless in CI, via the LiveClash.LogicTests console runner.
    ///
    /// Everything here is Revit-FREE at runtime: it only constructs the filter's own
    /// data types (ParameterFilter / Rule / ValuePick / TypeKey) and round-trips them
    /// through Serialize/ApplyLine/TryToRules, which touch no Element and no document.
    /// That is what lets the same code execute on a build machine with no Revit
    /// installed. This is where the "dispatch switch eats new keys" bug lived four
    /// separate times, so it is the part most worth a real regression gate.
    /// </summary>
    public static class FilterLogicChecks
    {
        /// <summary>Run every block against the supplied pass/fail sink.</summary>
        public static void RunAll(Action<string, bool?> check)
        {
            Serialization(check);
            RuleIdentity(check);
            IndexBounds(check);
            Migration(check);
            DisplayRules(check);
            Groups(check);
            Dispatch(check);
            AuditDropFormat(check);
        }

        /// <summary>
        /// AN INDEX IN A FILE IS JUST DIGITS, AND IT SIZES AN ALLOCATION.
        ///
        /// Indexed rule and group lines are addressed by position, so reading one pads
        /// the list up to that index. Unbounded, `rule=2000000|...` — one line — builds
        /// two million Rule objects (~145 MB), and int.MaxValue is a thousand times that
        /// again. The padding is inert rather than corrupting, so the failure is
        /// resource exhaustion, not a wrong filter.
        ///
        /// The way in is a SHARED .lcfilter preset — the file this feature exists to
        /// hand to a colleague, opened behind a dialog people click through — so it is
        /// worth a bound even though nobody here would type it.
        ///
        /// Asserted at the boundary rather than at the extreme: a check that only fed it
        /// 2,000,000 would still pass with the bound set absurdly high, and would take
        /// 400 ms doing it.
        /// </summary>
        public static void IndexBounds(Action<string, bool?> check)
        {
            var f = new ParameterFilter();
            f.ApplyLine("rule", "1000|Type|Contains|Description|ok");
            check("bounds: the largest allowed rule index still loads", f.Rules.Count == 1001);

            var over = new ParameterFilter();
            over.ApplyLine("rule", "1001|Type|Contains|Description|nope");
            check("bounds: one past it is dropped", over.Rules.Count == 0);

            var wild = new ParameterFilter();
            wild.ApplyLine("rule", "2000000|Type|Contains|Description|nope");
            check("bounds: a two-million index allocates nothing", wild.Rules.Count == 0);

            // A negative index is not treated as an index (the discriminator requires
            // >= 0), so the line falls to the legacy shape — where the first field has
            // to be a ParamScope name. "-5" is not one, so the line is dropped whole.
            // Safe either way; asserted so the reason is on record.
            var negative = new ParameterFilter();
            negative.ApplyLine("rule", "-5|Type|Contains|Description|x");
            check("bounds: a negative index yields no rule", negative.Rules.Count == 0);

            var g = new ParameterFilter();
            g.ApplyLine("group", "2000000|And");
            check("bounds: a wild GROUP index is dropped", g.Groups.Count == 0);

            var gr = new ParameterFilter();
            gr.ApplyLine("grule", "2000000|0|Type|Contains|Description|nope");
            check("bounds: a wild group index on a grule is dropped", gr.Groups.Count == 0);

            var gr2 = new ParameterFilter();
            gr2.ApplyLine("grule", "0|2000000|Type|Contains|Description|nope");
            check("bounds: a wild RULE index on a grule is dropped", gr2.Groups.Count == 0);
            check("bounds: ...and it leaves no empty group behind",
                gr2.Groups.Count == 0);

            // The bound must not cost anything a real filter does. The largest on this
            // machine declares 68 rule lines; this is an order past that.
            var real = new ParameterFilter();
            for (int i = 0; i < 200; i++)
                real.ApplyLine("rule", i + "|Type|Contains|Description|v" + i);
            check("bounds: a 200-rule filter is unaffected", real.Rules.Count == 200);
        }

        /// <summary>
        /// TWO RULES THAT LOOK ALIKE MUST STAY TWO RULES.
        ///
        /// Values within one rule are OR; separate rules are AND. So a reader that
        /// cannot tell "two rules, one value each" from "one rule, two values" does not
        /// merely lose a detail — it turns an AND into an OR and silently WIDENS the
        /// filter, every time the file is read. That is what happened before rules
        /// carried an index: the only thing to match a value line against was
        /// (scope, operator, parameter).
        ///
        /// The existing Serialization block does not catch this, and it is worth saying
        /// why: its three rules happen to differ in OPERATOR (Is vs IsNotEmpty), so the
        /// merge key differs and the bug never fires. It passed throughout.
        ///
        /// Deliberately paired with legacy checks. The index changed the on-disk format,
        /// and every filter and preset already saved is in the old one, so "the new
        /// format is exact" is only half the requirement.
        /// </summary>
        public static void RuleIdentity(Action<string, bool?> check)
        {
            // The exact shape from the report: same scope, same operator, same
            // parameter, different values. AND of two Contains.
            var f = new ParameterFilter();
            f.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type,
                                   Op = RuleOp.Contains, Values = { "chilled" } });
            f.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type,
                                   Op = RuleOp.Contains, Values = { "supply" } });

            var back = Reread(f, "s");

            check("two rules sharing scope+op+parameter stay TWO rules", back.Rules.Count == 2);
            check("...and neither absorbed the other's value",
                back.Rules.Count == 2 && back.Rules[0].Values.Count == 1 && back.Rules[1].Values.Count == 1);
            check("...keeping AND rather than widening to OR",
                back.Rules.Count == 2
                && back.Rules[0].Values.Count == 1 && back.Rules[0].Values[0] == "chilled"
                && back.Rules[1].Values.Count == 1 && back.Rules[1].Values[0] == "supply");

            // A genuine multi-value rule is the case the merge was built to serve, and
            // it still has to round-trip as ONE rule.
            var m = new ParameterFilter();
            m.Rules.Add(new Rule { ParameterName = "System Type", Scope = ParamScope.Either,
                                   Op = RuleOp.Is, Values = { "CHW Flow", "CHW Return" } });
            var mBack = Reread(m, "s");
            check("one rule with two values is still ONE rule",
                mBack.Rules.Count == 1 && mBack.Rules[0].Values.Count == 2);

            // Same defect, same fix, inside a group.
            var g = new ParameterFilter();
            var grp = new RuleGroup { Join = RuleJoin.And };
            grp.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type,
                                     Op = RuleOp.Contains, Values = { "chilled" } });
            grp.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type,
                                     Op = RuleOp.Contains, Values = { "supply" } });
            g.Groups.Add(grp);

            var gBack = Reread(g, "s");
            check("a group's two look-alike rules stay two",
                gBack.Groups.Count == 1 && gBack.Groups[0].Rules.Count == 2);
            check("...and keep one value each",
                gBack.Groups.Count == 1 && gBack.Groups[0].Rules.Count == 2
                && gBack.Groups[0].Rules[0].Values.Count == 1
                && gBack.Groups[0].Rules[1].Values.Count == 1);

            // LEGACY, un-indexed. Every filter saved before this format change looks
            // like this, and must still load — merged, because for these lines the
            // distinction genuinely is not recoverable.
            var old = new ParameterFilter();
            old.ApplyLine("rule", "Type|Contains|Description|chilled");
            old.ApplyLine("rule", "Type|Contains|Description|supply");
            check("legacy un-indexed rule lines still load",
                old.Rules.Count == 1 && old.Rules[0].Values.Count == 2);
            check("...with their operator and parameter intact",
                old.Rules.Count == 1 && old.Rules[0].Op == RuleOp.Contains
                && old.Rules[0].ParameterName == "Description");

            var oldG = new ParameterFilter();
            oldG.ApplyLine("group", "0|And");
            oldG.ApplyLine("grule", "0|Type|Contains|Description|chilled");
            check("legacy un-indexed group rule still loads",
                oldG.Groups.Count == 1 && oldG.Groups[0].Rules.Count == 1
                && oldG.Groups[0].Rules[0].Values.Count == 1);

            // A value-less operator carries no value field, so it is the line most
            // likely to be mis-split by an off-by-one in the index handling.
            var e = new ParameterFilter();
            e.Rules.Add(new Rule { ParameterName = "Mark", Scope = ParamScope.Instance, Op = RuleOp.IsNotEmpty });
            e.Rules.Add(new Rule { ParameterName = "Mark", Scope = ParamScope.Instance, Op = RuleOp.IsNotEmpty });
            var eBack = Reread(e, "s");
            check("two identical value-less rules stay two",
                eBack.Rules.Count == 2 && eBack.Rules[0].Op == RuleOp.IsNotEmpty);
        }

        /// <summary>Serialize, then read the lines back through the real ApplyLine.</summary>
        private static ParameterFilter Reread(ParameterFilter f, string prefix)
        {
            var back = new ParameterFilter();
            foreach (var line in f.Serialize(prefix))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq);
                if (key.Length > 0 && key[0] == prefix[0])
                    back.ApplyLine(key.Substring(1), line.Substring(eq + 1));
            }
            return back;
        }

        /// <summary>
        /// Global audit drops share the filter's family+type line format
        /// (<see cref="ParameterFilter.SerializeTypeKey"/> / <see cref="ParameterFilter.ParseTypeKey"/>).
        /// ClashSettings writes them as top-level auditdrop= lines and the engine applies
        /// them to every set, so the LINE format is the one persistence unit that is
        /// Revit-free and can be gated headless. The per-set -> global migration itself
        /// touches BuiltInCategory, so it is verified in Revit over the bridge, not here.
        /// </summary>
        public static void AuditDropFormat(Action<string, bool?> check)
        {
            var k = new TypeKey { Family = "B&W_Access Zone Side", Type = "B&W_61_Access Zone Side" };
            var back = ParameterFilter.ParseTypeKey(ParameterFilter.SerializeTypeKey(k));
            check("audit drop round-trips family+type",
                back != null && back.Family == k.Family && back.Type == k.Type);

            // Empty type means the whole family, and must survive as empty (not null),
            // or a deliberate family-wide drop would silently become a no-op on reload.
            var fam = new TypeKey { Family = "ABC_MEC_Generic_Fire", Type = "" };
            var famBack = ParameterFilter.ParseTypeKey(ParameterFilter.SerializeTypeKey(fam));
            check("family-wide drop (empty type) round-trips",
                famBack != null && famBack.Family == fam.Family && famBack.Type == "");

            // A '|' inside a name must not corrupt the two-field split: escaped to '/',
            // the same one-way escape every other field in this format uses.
            var piped = new TypeKey { Family = "Fam|With|Pipes", Type = "T|X" };
            var pipedBack = ParameterFilter.ParseTypeKey(ParameterFilter.SerializeTypeKey(piped));
            check("pipe in a name does not break the field split",
                pipedBack != null && pipedBack.Family == "Fam/With/Pipes" && pipedBack.Type == "T/X");

            // A malformed line yields null rather than a half-built drop.
            check("malformed audit drop line parses to null",
                ParameterFilter.ParseTypeKey("nofieldsep") == null);
        }

        /// <summary>
        /// The one that would have caught the "dispatch switch eats new keys" bug all
        /// FOUR times. The earlier Serialization block bypasses the readers -- it strips
        /// the prefix and calls ApplyLine directly -- so it could not see a key that
        /// Serialize emits but a reader drops. This exercises the REAL shared router
        /// (ParameterFilter.ApplyFilterLine, which both ClashSettings.Load and
        /// FilterPreset.Read now route through) and a completeness check on the key set.
        /// </summary>
        public static void Dispatch(Action<string, bool?> check)
        {
            var f = FullyPopulated();

            // 1. Completeness: every stem Serialize emits must be a recognized filter key,
            //    or a reader would silently drop it. This is the guard that locks the last
            //    hand-maintained point (the SerialKeys set vs what Serialize actually emits).
            bool allKnown = true;
            foreach (var line in f.Serialize("s"))
            {
                var eq = line.IndexOf('='); if (eq < 1) continue;
                var stem = line.Substring(1, eq - 1);   // drop the 's' prefix, up to '='
                if (!ParameterFilter.IsFilterKey(stem)) { allKnown = false; }
            }
            check("every serialized key is a recognized filter key", allKnown);

            // 2. Round trip THROUGH the shared router, exactly as Load/Read do.
            var back = new ParameterFilter();
            var scratch = new ParameterFilter();
            bool clS = false, clT = false;
            foreach (var line in f.Serialize("s"))
            {
                var eq = line.IndexOf('='); if (eq < 0) continue;
                var key = line.Substring(0, eq);
                var val = line.Substring(eq + 1);
                bool handled = ParameterFilter.ApplyFilterLine(back, scratch, key, val, ref clS, ref clT);
                check("router handled '" + key + "'", handled);
            }

            check("router round-trip: keyword", back.Keyword == "sys");
            check("router round-trip: exclude keyword", back.ExcludeKeyword == "temp");
            check("router round-trip: picks", back.Picked.Count == 1 && back.Excluded.Count == 1 && back.OnlyValues.Count == 1);
            check("router round-trip: audit types", back.ExcludedTypes.Count == 1);
            check("router round-trip: flat rule", back.Rules.Count == 1 && back.Rules[0].Values.Count == 2);
            check("router round-trip: OR group", back.Groups.Count == 1 && back.Groups[0].Join == RuleJoin.Or
                                                  && back.Groups[0].Rules.Count == 2);

            // 3. The prefix guard: the settings key "group" (a bool, GroupResults) must NOT
            //    be mistaken for the filter's "sgroup"/"tgroup".
            bool clS2 = false, clT2 = false;
            var g1 = new ParameterFilter(); var g2 = new ParameterFilter();
            check("non-filter key 'group' is not routed to a filter",
                ParameterFilter.ApplyFilterLine(g1, g2, "group", "true", ref clS2, ref clT2) == false);
            check("non-filter key 'source' is not routed to a filter",
                ParameterFilter.ApplyFilterLine(g1, g2, "source", "OST_PipeCurves", ref clS2, ref clT2) == false);
        }

        /// <summary>A filter exercising every serialized field, for the dispatch tests.</summary>
        private static ParameterFilter FullyPopulated()
        {
            var f = new ParameterFilter();
            f.Keyword = "sys";
            f.ExcludeKeyword = "temp";
            f.Parameters.Add(new ParamRef { Name = "System Type", Scope = ParamScope.Either });
            f.Picked.Add(new ValuePick { ParameterName = "System Type", Scope = ParamScope.Either, Value = "55 - CHW Flow" });
            f.Excluded.Add(new ValuePick { ParameterName = "Workset", Scope = ParamScope.Instance, Value = "MEP_ZZ_Typical" });
            f.OnlyValues.Add(new ValuePick { ParameterName = "Level", Scope = ParamScope.Instance, Value = "03" });
            f.ExcludedTypes.Add(new TypeKey { Family = "B&W_Access Zone Side", Type = "B&W_61_Access Zone Side" });
            f.Rules.Add(new Rule { ParameterName = "Workset", Scope = ParamScope.Instance, Op = RuleOp.Is,
                                   Values = { "MEP_ZZ_MEPServices", "MEP_02_BWIC" } });
            var g = new RuleGroup { Join = RuleJoin.Or };
            g.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type, Op = RuleOp.Is, Values = { "LV Ladder" } });
            g.Rules.Add(new Rule { ParameterName = "System Type", Scope = ParamScope.Instance, Op = RuleOp.Is, Values = { "55 - CHW Flow" } });
            f.Groups.Add(g);
            return f;
        }

        /// <summary>
        /// Rule GROUPS -- the nested "(A OR B) AND C" model -- must survive a save/load
        /// round trip with their join, rules and values intact, and must be routed by
        /// the dispatch switches (the recurring "new key eaten" trap).
        /// </summary>
        public static void Groups(Action<string, bool?> check)
        {
            var f = new ParameterFilter();
            // Flat rule (the "AND C" part).
            f.Rules.Add(new Rule { ParameterName = "Workset", Scope = ParamScope.Instance,
                                   Op = RuleOp.Is, Values = { "MEP_ZZ_MEPServices" } });
            // One OR group (the "(A OR B)" part): Description is cable-tray OR System Type is CHW.
            var g = new RuleGroup { Join = RuleJoin.Or };
            g.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type,
                                   Op = RuleOp.Is, Values = { "LV Ladder", "Comms Cable Tray" } });
            g.Rules.Add(new Rule { ParameterName = "System Type", Scope = ParamScope.Instance,
                                   Op = RuleOp.Is, Values = { "55 - CHW Flow" } });
            f.Groups.Add(g);

            var back = new ParameterFilter();
            foreach (var line in f.Serialize("s"))
            {
                var eq = line.IndexOf('='); if (eq < 0) continue;
                var key = line.Substring(0, eq);
                if (key.Length > 0 && key[0] == 's') back.ApplyLine(key.Substring(1), line.Substring(eq + 1));
            }

            check("flat rule survives alongside a group", back.Rules.Count == 1);
            check("group survives serialize->parse", back.Groups.Count == 1);
            check("group join (OR) survives", back.Groups.Count == 1 && back.Groups[0].Join == RuleJoin.Or);
            check("group's two rules survive", back.Groups.Count == 1 && back.Groups[0].Rules.Count == 2);
            check("group rule values survive",
                back.Groups.Count == 1 && back.Groups[0].Rules.Count == 2
                && back.Groups[0].Rules[0].Values.Count == 2
                && back.Groups[0].Rules[1].Values.Count == 1);

            // An AND group round-trips too, and a filter with no groups yields none.
            var f2 = new ParameterFilter();
            var g2 = new RuleGroup { Join = RuleJoin.And };
            g2.Rules.Add(new Rule { ParameterName = "P", Scope = ParamScope.Type, Op = RuleOp.IsNotEmpty });
            f2.Groups.Add(g2);
            var back2 = new ParameterFilter();
            foreach (var line in f2.Serialize("t"))
            {
                var eq = line.IndexOf('='); if (eq < 0) continue;
                var key = line.Substring(0, eq);
                if (key.Length > 0 && key[0] == 't') back2.ApplyLine(key.Substring(1), line.Substring(eq + 1));
            }
            check("AND group + value-less op round-trips",
                back2.Groups.Count == 1 && back2.Groups[0].Join == RuleJoin.And
                && back2.Groups[0].Rules.Count == 1 && back2.Groups[0].Rules[0].Op == RuleOp.IsNotEmpty);
            check("no groups -> none parsed", new ParameterFilter().Groups.Count == 0);
        }

        /// <summary>
        /// The rule-display path (ParameterFilter.RulesForDisplay) must surface a
        /// new-format filter's own Rules, and only fall back to legacy migration when
        /// there are none. This is the exact regression guard for the FilterWindow bug
        /// where a rule-bearing filter displayed empty and was then saved blank.
        /// </summary>
        public static void DisplayRules(Action<string, bool?> check)
        {
            // New format: rules live in f.Rules. Must display verbatim, NOT be lost.
            var nf = new ParameterFilter();
            nf.Rules.Add(new Rule { ParameterName = "Workset", Scope = ParamScope.Instance,
                                    Op = RuleOp.Is, Values = { "MEP_ZZ_MEPServices" } });
            nf.Rules.Add(new Rule { ParameterName = "Description", Scope = ParamScope.Type,
                                    Op = RuleOp.Is, Values = { "LV Ladder", "Comms Cable Tray", "MV Ladder" } });
            var shown = nf.RulesForDisplay();
            check("new-format rules display verbatim (not empty)", shown.Count == 2);
            check("new-format rule keeps its values",
                shown.Count == 2 && shown[1].ParameterName == "Description" && shown[1].Values.Count == 3);

            // Legacy filter: no Rules, only picks. Must still migrate for display.
            var legacy = new ParameterFilter();
            legacy.Excluded.Add(new ValuePick { ParameterName = "Workset", Scope = ParamScope.Instance, Value = "MEP_ZZ_Typical" });
            var lshown = legacy.RulesForDisplay();
            check("legacy filter still migrates for display", lshown.Count == 1 && lshown[0].Op == RuleOp.IsNot);

            // Empty filter: nothing to show.
            check("empty filter displays no rules", new ParameterFilter().RulesForDisplay().Count == 0);

            // Editing the returned rows must not mutate the live filter before commit.
            var iso = new ParameterFilter();
            iso.Rules.Add(new Rule { ParameterName = "P", Scope = ParamScope.Type, Op = RuleOp.Is, Values = { "x" } });
            iso.RulesForDisplay().Clear();
            check("RulesForDisplay returns a detached copy", iso.Rules.Count == 1);
        }

        /// <summary>
        /// A filter must survive a save/load byte for byte: rules, operators, values,
        /// and the audit family+type pairs.
        /// </summary>
        public static void Serialization(Action<string, bool?> check)
        {
            var f = new ParameterFilter();
            f.Rules.Add(new Rule { ParameterName = "Workset", Scope = ParamScope.Instance,
                                   Op = RuleOp.IsNot, Values = { "MEP_ZZ_PlantReplacement" } });
            f.Rules.Add(new Rule { ParameterName = "System Type", Scope = ParamScope.Either,
                                   Op = RuleOp.Is, Values = { "55 - CHW Flow", "55 - CHW Return" } });
            f.Rules.Add(new Rule { ParameterName = "System Type", Scope = ParamScope.Either,
                                   Op = RuleOp.IsNotEmpty });
            f.ExcludedTypes.Add(new TypeKey { Family = "B&W_Access Zone Side", Type = "B&W_61_Access Zone Side" });

            var lines = f.Serialize("s");
            var g = new ParameterFilter();
            foreach (var line in lines)
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq);
                if (key.Length > 0 && key[0] == 's') g.ApplyLine(key.Substring(1), line.Substring(eq + 1));
            }

            check("rules survive serialize->parse", g.Rules.Count == 3);
            check("audit pair survives serialize->parse", g.ExcludedTypes.Count == 1);

            bool valuesOk = g.Rules.Count == 3
                && g.Rules[1].Values.Count == 2
                && g.Rules[1].Op == RuleOp.Is
                && g.Rules[2].Op == RuleOp.IsNotEmpty;
            check("operators and multi-values survive", valuesOk);

            // Every value-based operator, one round trip each.
            foreach (var op in new[] { RuleOp.Is, RuleOp.IsNot, RuleOp.Contains, RuleOp.DoesNotContain })
            {
                var one = new ParameterFilter();
                one.Rules.Add(new Rule { ParameterName = "P", Scope = ParamScope.Type, Op = op, Values = { "x" } });
                var back = new ParameterFilter();
                foreach (var line in one.Serialize("t"))
                {
                    var eq = line.IndexOf('='); if (eq < 0) continue;
                    var key = line.Substring(0, eq);
                    if (key.Length > 0 && key[0] == 't') back.ApplyLine(key.Substring(1), line.Substring(eq + 1));
                }
                check("operator " + op + " round-trips",
                    back.Rules.Count == 1 && back.Rules[0].Op == op && back.Rules[0].Values.Count == 1);
            }
        }

        /// <summary>
        /// The legacy Keep/Drop/Only fields must convert to the equivalent flat rules,
        /// and the mapping must be exact for the shapes real filters use.
        /// </summary>
        public static void Migration(Action<string, bool?> check)
        {
            var f = new ParameterFilter();
            f.Excluded.Add(new ValuePick { ParameterName = "Workset", Scope = ParamScope.Instance, Value = "MEP_ZZ_Typical" });
            f.Excluded.Add(new ValuePick { ParameterName = "Workset", Scope = ParamScope.Instance, Value = "MEP_02_BWIC" });
            f.Picked.Add(new ValuePick { ParameterName = "System Type", Scope = ParamScope.Either, Value = "55 - CHW Flow" });
            f.OnlyValues.Add(new ValuePick { ParameterName = "Level", Scope = ParamScope.Instance, Value = "03" });

            List<Rule> rules;
            bool exact = f.TryToRules(out rules);

            check("single-source-per-shape migration is exact", exact);

            bool hasIsNotWorkset = false, hasIsPicked = false, hasOnlyLevel = false;
            foreach (var r in rules)
            {
                if (r.ParameterName == "Workset" && r.Op == RuleOp.IsNot && r.Values.Count == 2) hasIsNotWorkset = true;
                if (r.ParameterName == "System Type" && r.Op == RuleOp.Is) hasIsPicked = true;
                if (r.ParameterName == "Level" && r.Op == RuleOp.Is) hasOnlyLevel = true;
            }
            check("excluded picks -> 'is not', grouped", hasIsNotWorkset);
            check("kept picks -> 'is'", hasIsPicked);
            check("only values -> 'is'", hasOnlyLevel);

            // A cross-parameter OR on the include side cannot be a flat AND list, so it
            // must report NOT exact rather than silently mangling the filter.
            var f2 = new ParameterFilter();
            f2.Picked.Add(new ValuePick { ParameterName = "System Type", Scope = ParamScope.Either, Value = "A" });
            f2.Picked.Add(new ValuePick { ParameterName = "Comments", Scope = ParamScope.Instance, Value = "B" });
            List<Rule> r2;
            check("cross-parameter OR is flagged inexact", f2.TryToRules(out r2) == false);
        }
    }
}
