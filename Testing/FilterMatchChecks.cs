using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The filter's MATCHING decision, headless. ParameterFilter.Matches reads an element's values
    /// through ParameterFilter.IValueSource; here the source is a plain dictionary, so every rule
    /// (keep, drop, only, rules, groups, family+type drops, the per-call read cache and the keyword
    /// cache) runs with no Revit and is covered by the coverage and mutation gates. The in-Revit
    /// checks still match real elements through the same code.
    ///
    /// Each check names the precedence or edge it pins, because those are where this matcher has
    /// been wrong before: an Only rule ignored when it was the only rule, an empty pick selecting
    /// everything with no value, a family-name substring dropping an unrelated family.
    /// </summary>
    public static class FilterMatchChecks
    {
        private const string P = "filter match: ";

        /// <summary>A fake element: parameter values by name (any scope unless a scoped value is set),
        /// a family and type, and counters for what the matcher asked.</summary>
        private sealed class Values
        {
            public readonly Dictionary<string, string> ByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ByScopedName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Family = "";
            public string Type = "";
            public int Reads;
            public int TypeAsks;
            public readonly List<string> Asked = new List<string>();
        }

        private struct Source : ParameterFilter.IValueSource
        {
            private readonly Values _v;
            public Source(Values v) { _v = v; }

            public string Read(string name, ParamScope scope)
            {
                _v.Reads++;
                _v.Asked.Add(scope + ":" + name);
                string s;
                if (_v.ByScopedName.TryGetValue(scope + ":" + name, out s)) return s;
                return _v.ByName.TryGetValue(name ?? "", out s) ? s : null;
            }

            public bool TypeExcluded(List<TypeKey> drops)
            {
                _v.TypeAsks++;
                return ParameterFilter.TypeNamesExcluded(_v.Family, _v.Type, drops);
            }
        }

        private static Values El(params string[] nameValue)
        {
            var v = new Values();
            for (int i = 0; i + 1 < nameValue.Length; i += 2) v.ByName[nameValue[i]] = nameValue[i + 1];
            return v;
        }

        private static bool M(ParameterFilter f, Values v) { return f.Matches(new Source(v)); }

        private static Rule R(string param, RuleOp op, params string[] values)
        {
            return new Rule { ParameterName = param, Op = op, Values = new List<string>(values) };
        }

        public static void RunAll(Action<string, bool?> check)
        {
            Keep(check);
            Drop(check);
            Only(check);
            Rules(check);
            Groups(check);
            TypeDrops(check);
            Caches(check);
            Describe(check);
            Migration(check);
        }

        /// <summary>
        /// TryToRules claims "exact" when the rules it produces match the legacy fields exactly. That is
        /// a claim about MATCHING, so it is checked by matching: the legacy filter and a rules-only filter
        /// built from its output must keep the same elements, over elements chosen to hit every branch.
        /// </summary>
        private static void Migration(Action<string, bool?> check)
        {
            var sample = new[]
            {
                El("System Type", "Chilled Water", "Description", "main", "Workset", "MEP"),
                El("System Type", "Access Zone", "Description", "maintenance", "Workset", "MEP"),
                El("System Type", "Hot Water", "Description", "", "Workset", "ARCH"),
                El("System Type", "", "Description", "chilled spare", "Workset", ""),
                new Values()
            };

            Func<ParameterFilter, bool> equivalent = legacy =>
            {
                List<Rule> rules;
                if (!legacy.TryToRules(out rules)) return false;
                var migrated = new ParameterFilter();
                migrated.Rules.AddRange(rules);
                foreach (var v in sample) if (M(legacy, v) != M(migrated, v)) return false;
                return true;
            };

            var xkw = ParameterFilter.Default(); xkw.ExcludeKeyword = "access, spare";
            check(P + "migration: a drop keyword becomes one 'does not contain' per nominated parameter, and matches the same",
                equivalent(xkw) && xkw.RulesForDisplay().Count == 2 && xkw.RulesForDisplay()[0].Op == RuleOp.DoesNotContain
                && xkw.RulesForDisplay()[0].Values.Count == 2);

            var one = new ParameterFilter { Keyword = "chilled" };
            one.Parameters.Add(new ParamRef { Name = "System Type" });
            one.Parameters.Add(new ParamRef { Name = "" });
            check(P + "migration: a keyword over ONE nominated parameter becomes 'contains', and matches the same",
                equivalent(one) && one.RulesForDisplay().Count == 1 && one.RulesForDisplay()[0].Op == RuleOp.Contains);

            var two = ParameterFilter.Default(); two.Keyword = "chilled";
            List<Rule> ignored;
            check(P + "migration: a keyword over TWO parameters is an OR the flat rules cannot hold - not exact",
                !two.TryToRules(out ignored));

            var picks = new ParameterFilter();
            picks.Picked.Add(new ValuePick { ParameterName = "Workset", Value = "MEP" });
            picks.Picked.Add(new ValuePick { ParameterName = "Workset", Value = "ARCH" });
            picks.Picked.Add(new ValuePick { ParameterName = "Workset", Value = "" });
            check(P + "migration: picks on one parameter become one 'is' rule (blank picks dropped), and match the same",
                equivalent(picks) && picks.RulesForDisplay().Count == 1 && picks.RulesForDisplay()[0].Values.Count == 2);

            var picks2 = new ParameterFilter();
            picks2.Picked.Add(new ValuePick { ParameterName = "Workset", Value = "MEP" });
            picks2.Picked.Add(new ValuePick { ParameterName = "System Type", Value = "Hot Water" });
            check(P + "migration: picks on two parameters are an OR across parameters - not exact", !picks2.TryToRules(out ignored));

            var both = new ParameterFilter { Keyword = "x" };
            both.Parameters.Add(new ParamRef { Name = "Description" });
            both.Picked.Add(new ValuePick { ParameterName = "Workset", Value = "MEP" });
            check(P + "migration: a keyword together with a pick is an OR - not exact", !both.TryToRules(out ignored));

            var excl = ParameterFilter.Default();
            excl.Excluded.Add(new ValuePick { ParameterName = "Workset", Value = "ARCH" });
            excl.OnlyValues.Add(new ValuePick { ParameterName = "Description", Value = "main" });
            excl.OnlyValues.Add(new ValuePick { ParameterName = "Description", Value = "maintenance" });
            check(P + "migration: dropped values become 'is not' and Only values 'is', grouped per parameter, and match the same",
                equivalent(excl) && excl.RulesForDisplay().Count == 2
                && excl.RulesForDisplay()[0].Op == RuleOp.IsNot && excl.RulesForDisplay()[1].Op == RuleOp.Is
                && excl.RulesForDisplay()[1].Values.Count == 2);

            var native = new ParameterFilter { Keyword = "legacy text that must be ignored" };
            native.Rules.Add(R("Workset", RuleOp.Is, "MEP"));
            var shown = native.RulesForDisplay();
            shown.Add(R("x", RuleOp.Is));
            check(P + "migration: a filter that already has rules shows THOSE (as a copy), never a migration of its legacy fields",
                native.RulesForDisplay().Count == 1 && native.RulesForDisplay()[0].ParameterName == "Workset" && native.Rules.Count == 1);
        }

        private static void Keep(Action<string, bool?> check)
        {
            var chw = El("System Type", "Chilled Water Supply", "Description", "CHW main");
            var hw = El("System Type", "Hot Water", "Description", "");

            check(P + "an empty filter keeps everything, and reads nothing to decide it",
                M(new ParameterFilter(), chw) && M(new ParameterFilter(), new Values()) && chw.Reads == 0);

            var kw = ParameterFilter.Default(); kw.Keyword = "chilled";
            check(P + "a keyword keeps an element whose nominated parameter contains it, in any case",
                M(kw, chw) && !M(kw, hw));

            var kw2 = new ParameterFilter { Keyword = "chw" };
            kw2.Parameters.Add(new ParamRef { Name = "System Type" });
            check(P + "a keyword is searched only in the NOMINATED parameters",
                !M(kw2, chw));

            var kw3 = ParameterFilter.Default(); kw3.Keyword = " zzz , chilled ,, ";
            check(P + "several keywords OR, and blanks between commas are ignored", M(kw3, chw) && !M(kw3, hw));

            var pick = new ParameterFilter();
            pick.Picked.Add(new ValuePick { ParameterName = "System Type", Value = "  hot WATER " });
            check(P + "a picked value is an exact match after trim and case, never a substring",
                M(pick, hw) && !M(pick, chw) && !M(pick, El("System Type", "Hot Water Return")));

            var emptyPick = new ParameterFilter();
            emptyPick.Picked.Add(new ValuePick { ParameterName = "System Type", Value = "   " });
            emptyPick.Picked.Add(new ValuePick { ParameterName = "System Type", Value = "Hot Water" });
            check(P + "an empty pick never selects the elements that simply have no value",
                !M(emptyPick, new Values()) && M(emptyPick, hw));

            var either = ParameterFilter.Default(); either.Keyword = "nothing-here";
            either.Picked.Add(new ValuePick { ParameterName = "System Type", Value = "Hot Water" });
            check(P + "keywords and picks OR: either one keeps the element", M(either, hw) && !M(either, chw));

            var emptyNominated = new ParameterFilter { Keyword = "x" };
            emptyNominated.Parameters.Add(new ParamRef { Name = "" });
            emptyNominated.Parameters.Add(new ParamRef { Name = "Description" });
            var x = El("Description", "box");
            check(P + "a blank nominated parameter is skipped rather than read", M(emptyNominated, x) && !x.Asked.Contains("Either:"));
        }

        private static void Drop(Action<string, bool?> check)
        {
            var zone = El("System Type", "Access Zone", "Description", "maintenance access");
            var pipe = El("System Type", "Chilled Water", "Description", "pipe");

            var drop = ParameterFilter.Default(); drop.ExcludeKeyword = "access";
            check(P + "a drop keyword alone means everything except it",
                !M(drop, zone) && M(drop, pipe) && M(drop, new Values()));

            var both = ParameterFilter.Default(); both.Keyword = "access"; both.ExcludeKeyword = "maintenance";
            check(P + "drop beats keep: an element both kept and dropped is dropped", !M(both, zone));

            var dropPick = new ParameterFilter();
            dropPick.Excluded.Add(new ValuePick { ParameterName = "Description", Value = "PIPE" });
            check(P + "a dropped value removes exactly that value", !M(dropPick, pipe) && M(dropPick, zone));

            var dropEmpty = new ParameterFilter();
            dropEmpty.Excluded.Add(new ValuePick { ParameterName = "Description", Value = "" });
            dropEmpty.Excluded.Add(new ValuePick { ParameterName = "Description", Value = "zzz" });
            check(P + "an empty dropped value never drops the elements with no value", M(dropEmpty, new Values()) && M(dropEmpty, pipe));
        }

        private static void Only(Action<string, bool?> check)
        {
            var a = El("Workset", "MEP Services", "Level", "L03");
            var b = El("Workset", "MEP Services", "Level", "L04");
            var c = El("Workset", "Architecture", "Level", "L03");
            var none = El("Level", "L03");

            var only = new ParameterFilter();
            only.OnlyValues.Add(new ValuePick { ParameterName = "Workset", Value = "MEP Services" });
            check(P + "an Only rule on its own is a real filter (it once let everything through)",
                M(only, a) && !M(only, c));
            check(P + "an element with no value for an Only parameter fails it", !M(only, none));

            only.OnlyValues.Add(new ValuePick { ParameterName = "Workset", Value = "Architecture" });
            check(P + "Only values for the SAME parameter OR", M(only, a) && M(only, c));

            only.OnlyValues.Add(new ValuePick { ParameterName = "Level", Value = "l03" });
            check(P + "Only values for DIFFERENT parameters AND", M(only, a) && M(only, c) && !M(only, b));

            var onlyBlank = new ParameterFilter();
            onlyBlank.OnlyValues.Add(new ValuePick { ParameterName = "Workset", Value = " " });
            onlyBlank.Keyword = "";
            check(P + "a blank Only value constrains nothing", M(onlyBlank, c) && M(onlyBlank, none));

            var beatsKeep = ParameterFilter.Default();
            beatsKeep.Picked.Add(new ValuePick { ParameterName = "Level", Value = "L04" });
            beatsKeep.OnlyValues.Add(new ValuePick { ParameterName = "Workset", Value = "MEP Services" });
            check(P + "Only beats Keep: a kept element outside the Only set is out",
                M(beatsKeep, b) && !M(beatsKeep, El("Workset", "Architecture", "Level", "L04")));

            var dropBeatsOnly = new ParameterFilter();
            dropBeatsOnly.OnlyValues.Add(new ValuePick { ParameterName = "Workset", Value = "MEP Services" });
            dropBeatsOnly.Excluded.Add(new ValuePick { ParameterName = "Level", Value = "L03" });
            check(P + "Drop beats Only", !M(dropBeatsOnly, a) && M(dropBeatsOnly, b));

            var scoped = new ParameterFilter();
            scoped.OnlyValues.Add(new ValuePick { ParameterName = "Type Mark", Scope = ParamScope.Type, Value = "T1" });
            var sv = new Values(); sv.ByScopedName["Type:Type Mark"] = "T1";
            check(P + "an Only rule reads with its own scope and the name as the user picked it",
                M(scoped, sv) && sv.Asked.Contains("Type:Type Mark"));

            var badScope = new ParameterFilter();
            badScope.OnlyValues.Add(new ValuePick { ParameterName = "Workset", Scope = (ParamScope)99, Value = "MEP Services" });
            var bs = El("Workset", "MEP Services");
            check(P + "an undefined scope number in a hand-edited file is read as given, without throwing",
                M(badScope, bs) && bs.Asked.Contains("99:Workset"));
        }

        private static void Rules(Action<string, bool?> check)
        {
            var e = El("System Type", "  Chilled Water Supply ", "Mark", "");

            Func<Rule, Values, bool> holds = (r, v) => { var f = new ParameterFilter(); f.Rules.Add(r); return M(f, v); };

            check(P + "rule Is: equal after trim and case, values OR",
                holds(R("System Type", RuleOp.Is, "x", "chilled water supply"), e)
                && !holds(R("System Type", RuleOp.Is, "chilled water"), e));
            check(P + "rule Is with no usable values is incomplete and keeps everything",
                holds(R("System Type", RuleOp.Is), e) && holds(R("System Type", RuleOp.Is, " ", ""), e));
            check(P + "rule Is not: any listed value drops, blanks ignored",
                !holds(R("System Type", RuleOp.IsNot, "Chilled Water Supply"), e)
                && holds(R("System Type", RuleOp.IsNot, "Hot", " "), e) && holds(R("System Type", RuleOp.IsNot), e));
            check(P + "rule Contains: a substring of the value, any case, values OR",
                holds(R("System Type", RuleOp.Contains, "zzz", "WATER"), e)
                && !holds(R("System Type", RuleOp.Contains, "steam"), e));
            check(P + "rule Contains with no usable values is incomplete and keeps everything",
                holds(R("System Type", RuleOp.Contains, "  "), e) && holds(R("System Type", RuleOp.Contains), e));
            check(P + "rule Does not contain: any listed substring drops, blanks ignored",
                !holds(R("System Type", RuleOp.DoesNotContain, "chilled"), e)
                && holds(R("System Type", RuleOp.DoesNotContain, "steam", ""), e));
            check(P + "rule Is empty / Is not empty ignore the values and test for any value at all",
                holds(R("Mark", RuleOp.IsEmpty), e) && !holds(R("Mark", RuleOp.IsNotEmpty), e)
                && !holds(R("System Type", RuleOp.IsEmpty, "x"), e) && holds(R("System Type", RuleOp.IsNotEmpty), e)
                && holds(R("Missing", RuleOp.IsEmpty), e));

            var and = new ParameterFilter();
            and.Rules.Add(R("System Type", RuleOp.Contains, "chilled"));
            and.Rules.Add(R("System Type", RuleOp.Contains, "return"));
            check(P + "flat rules AND: every rule must hold", !M(and, e)
                && M(and, El("System Type", "Chilled Water Return")));

            var nullRule = new ParameterFilter();
            nullRule.Rules.Add(null);
            check(P + "a null rule (hand-edited file) holds rather than throwing", M(nullRule, e));
        }

        private static void Groups(Action<string, bool?> check)
        {
            var tray = El("Description", "cable tray", "System Type", "");
            var chw = El("Description", "pipe", "System Type", "CHW");
            var other = El("Description", "duct", "System Type", "Supply Air");

            var or = new ParameterFilter();
            or.Groups.Add(new RuleGroup { Join = RuleJoin.Or, Rules = { R("Description", RuleOp.Is, "cable tray"), R("System Type", RuleOp.Is, "CHW") } });
            check(P + "an OR group holds when ANY of its rules holds", M(or, tray) && M(or, chw) && !M(or, other));

            var andG = new ParameterFilter();
            andG.Groups.Add(new RuleGroup { Join = RuleJoin.And, Rules = { R("Description", RuleOp.Is, "pipe"), R("System Type", RuleOp.Is, "CHW") } });
            check(P + "an AND group holds only when ALL of its rules hold", M(andG, chw) && !M(andG, tray));

            var empty = new ParameterFilter();
            empty.Groups.Add(new RuleGroup());
            empty.Groups.Add(null);
            check(P + "an empty or null group never narrows anything", M(empty, other));

            var mixed = new ParameterFilter();
            mixed.Groups.Add(new RuleGroup { Join = RuleJoin.Or, Rules = { R("Description", RuleOp.Is, "cable tray"), R("Description", RuleOp.Is, "pipe") } });
            mixed.Rules.Add(R("System Type", RuleOp.IsNotEmpty));
            check(P + "(A OR B) AND C: groups AND with the flat rules", M(mixed, chw) && !M(mixed, tray) && !M(mixed, other));

            var twoGroups = new ParameterFilter();
            twoGroups.Groups.Add(new RuleGroup { Join = RuleJoin.Or, Rules = { R("Description", RuleOp.Is, "pipe") } });
            twoGroups.Groups.Add(new RuleGroup { Join = RuleJoin.Or, Rules = { R("System Type", RuleOp.Is, "Supply Air") } });
            check(P + "two groups AND with each other", !M(twoGroups, chw) && !M(twoGroups, other)
                && M(twoGroups, El("Description", "pipe", "System Type", "Supply Air")));
        }

        private static void TypeDrops(Action<string, bool?> check)
        {
            var drops = new List<TypeKey>
            {
                new TypeKey { Family = "ABC_MEC_Generic_Fire", Type = "Pre Action Valve" },
                new TypeKey { Family = "Access Zone", Type = "" },
                new TypeKey { Family = "", Type = "Default Pipe" },
                null
            };

            check(P + "family+type drop: the exact pair is dropped, compared after trim and case",
                ParameterFilter.TypeNamesExcluded(" abc_mec_generic_fire ", "PRE ACTION VALVE", drops));
            check(P + "family+type drop: another type of the same family is NOT dropped",
                !ParameterFilter.TypeNamesExcluded("ABC_MEC_Generic_Fire", "Gate Valve", drops));
            check(P + "family+type drop: a family that merely STARTS WITH the name is not touched (the fire-tank bug)",
                !ParameterFilter.TypeNamesExcluded("ABC_MEC_Generic_FireHoseReelCabinet", "Pre Action Valve", drops));
            check(P + "family+type drop: an empty type means the whole family, deliberately",
                ParameterFilter.TypeNamesExcluded("Access Zone", "Any Type", drops));
            check(P + "family+type drop: a system type with no family name can still be dropped (pipes, ducts)",
                ParameterFilter.TypeNamesExcluded("", "Default Pipe", drops) && ParameterFilter.TypeNamesExcluded(null, "default pipe", drops)
                && !ParameterFilter.TypeNamesExcluded("", "Other Pipe", drops));
            check(P + "family+type drop: an element with no type name cannot match, and no drops drop nothing",
                !ParameterFilter.TypeNamesExcluded("Access Zone", "", drops) && !ParameterFilter.TypeNamesExcluded("Access Zone", " ", drops)
                && !ParameterFilter.TypeNamesExcluded("Access Zone", "X", null)
                && !ParameterFilter.TypeNamesExcluded("Access Zone", "X", new List<TypeKey>()));

            var f = new ParameterFilter();
            f.ExcludedTypes.Add(new TypeKey { Family = "Access Zone" });
            var zone = new Values { Family = "Access Zone", Type = "1200" };
            var pipe = new Values { Family = "", Type = "Default Pipe" };
            check(P + "a filter's own family+type drops remove the element and keep the rest",
                !M(f, zone) && M(f, pipe));

            var noTypes = ParameterFilter.Default(); noTypes.ExcludeKeyword = "zzz";
            var probe = new Values { Family = "Access Zone", Type = "1200" };
            M(noTypes, probe);
            check(P + "with no family+type drops the element's type is never even looked up", probe.TypeAsks == 0);
        }

        private static void Caches(Action<string, bool?> check)
        {
            // The drop pass and the keep pass nominate the same parameters; one read per parameter per call.
            var f = ParameterFilter.Default();
            f.Keyword = "water"; f.ExcludeKeyword = "steam";
            var e = El("System Type", "Chilled Water", "Description", "x");
            M(f, e);
            int readsFirst = e.Reads;
            check(P + "one Matches call reads each parameter once, however many passes want it",
                readsFirst == 2);

            M(f, e);
            check(P + "the read cache lives for one call only: a second call reads again", e.Reads == readsFirst * 2);

            var changing = El("System Type", "Chilled Water");
            var g = ParameterFilter.Default(); g.Keyword = "chilled";
            bool before = M(g, changing);
            changing.ByName["System Type"] = "Hot Water";
            bool after = M(g, changing);
            check(P + "a value that changes between calls is read fresh (nothing is cached across elements)", before && !after);

            var k = ParameterFilter.Default(); k.Keyword = "chilled";
            var hot = El("System Type", "Hot Water");
            bool first = M(k, hot);
            k.Keyword = "hot";
            bool second = M(k, hot);
            k.ExcludeKeyword = "water";
            bool third = M(k, hot);
            check(P + "editing Keyword or ExcludeKeyword in place takes effect on the next call (the split cache notices)",
                !first && second && !third);

            var copy = ParameterFilter.Default(); copy.Keyword = "a, b"; copy.ExcludeKeyword = "c";
            copy.ParseKeywords().Add("zzz");
            copy.ParseExcludeKeywords().Clear();
            check(P + "ParseKeywords and ParseExcludeKeywords hand out copies, not the cache",
                copy.ParseKeywords().Count == 2 && copy.ParseExcludeKeywords().Count == 1
                && copy.ParseKeywords()[0] == "a" && !M(copy, El("System Type", "zzz c")));

            check(P + "SplitKeepingCase keeps what was typed, trims, and drops blanks",
                string.Join("|", ParameterFilter.SplitKeepingCase(" Fire , ,Hose ").ToArray()) == "Fire|Hose"
                && ParameterFilter.SplitKeepingCase(null).Count == 0 && ParameterFilter.SplitKeepingCase("").Count == 0);
            check(P + "Norm trims and lower-cases, and treats null as empty",
                ParameterFilter.Norm("  AbC ") == "abc" && ParameterFilter.Norm(null) == "");
        }

        private static void Describe(Action<string, bool?> check)
        {
            check(P + "IsEmpty: a new filter is empty", new ParameterFilter().IsEmpty && ParameterFilter.Default().IsEmpty);
            var each = new Func<ParameterFilter>[]
            {
                () => new ParameterFilter { Keyword = "a" },
                () => new ParameterFilter { ExcludeKeyword = "a" },
                () => { var f = new ParameterFilter(); f.Picked.Add(new ValuePick()); return f; },
                () => { var f = new ParameterFilter(); f.Excluded.Add(new ValuePick()); return f; },
                () => { var f = new ParameterFilter(); f.ExcludedTypes.Add(new TypeKey()); return f; },
                () => { var f = new ParameterFilter(); f.OnlyValues.Add(new ValuePick()); return f; },
                () => { var f = new ParameterFilter(); f.Rules.Add(new Rule()); return f; },
                () => { var f = new ParameterFilter(); f.Groups.Add(new RuleGroup()); return f; },
            };
            bool allNonEmpty = true;
            foreach (var mk in each) if (mk().IsEmpty) allNonEmpty = false;
            check(P + "IsEmpty: any one kind of entry makes a filter non-empty", allNonEmpty
                && new ParameterFilter { Keyword = " , " }.IsEmpty);

            var d = ParameterFilter.Default();
            check(P + "Default searches System Type and Description, either scope",
                d.Parameters.Count == 2 && d.Parameters[0].Name == "System Type" && d.Parameters[1].Name == "Description"
                && d.Parameters[0].Scope == ParamScope.Either && d.Parameters[1].Scope == ParamScope.Either);

            check(P + "Summary: an empty filter says it keeps everything in the ticked categories",
                new ParameterFilter().Summary() == "everything in the ticked categories");

            var s1 = new ParameterFilter { Keyword = "a, b", ExcludeKeyword = "c" };
            s1.Picked.Add(new ValuePick { Value = "x" });
            s1.Excluded.Add(new ValuePick { Value = "y" }); s1.Excluded.Add(new ValuePick { Value = "z" });
            s1.OnlyValues.Add(new ValuePick { Value = "w" });
            check(P + "Summary: keep, only and drop counts, singular and plural",
                s1.Summary() == "2 keywords or 1 picked value (only where 1 rule hold), excluding 1 keyword or 2 values");

            var s2 = new ParameterFilter { Keyword = "a" };
            s2.Picked.Add(new ValuePick()); s2.Picked.Add(new ValuePick());
            s2.OnlyValues.Add(new ValuePick()); s2.OnlyValues.Add(new ValuePick());
            check(P + "Summary: plural picks and Only rules",
                s2.Summary() == "1 keyword or 2 picked values (only where 2 rules hold)");

            var s3 = new ParameterFilter();
            s3.Rules.Add(new Rule()); s3.Rules.Add(new Rule()); s3.Groups.Add(new RuleGroup());
            check(P + "Summary: a rules-only filter names its rules and groups, not 'everything'",
                s3.Summary() == "2 rules + 1 group");

            var s4 = new ParameterFilter { ExcludeKeyword = "a" };
            s4.Rules.Add(new Rule()); s4.Groups.Add(new RuleGroup()); s4.Groups.Add(new RuleGroup());
            check(P + "Summary: legacy text and rules together, joined with a semicolon",
                s4.Summary() == "everything, excluding 1 keyword; 1 rule + 2 groups");

            var s5 = new ParameterFilter(); s5.Excluded.Add(new ValuePick { Value = "q" });
            check(P + "Summary: one dropped value is singular", s5.Summary() == "everything, excluding 1 value");

            check(P + "ParamRef: shows the name, plus the scope when it is not Either; the key ignores case",
                new ParamRef { Name = "Mark" }.ToString() == "Mark"
                && new ParamRef { Name = "Mark", Scope = ParamScope.Type }.ToString() == "Mark (Type)"
                && new ParamRef { Name = "MARK", Scope = ParamScope.Type }.Key == new ParamRef { Name = "mark", Scope = ParamScope.Type }.Key
                && new ParamRef { Name = "Mark", Scope = ParamScope.Type }.Key != new ParamRef { Name = "Mark" }.Key
                && new ParamRef { Name = null }.Key == "Either::");
            check(P + "TypeKey shows 'Family : Type', or the family alone",
                new TypeKey { Family = "F", Type = "T" }.ToString() == "F : T" && new TypeKey { Family = "F" }.ToString() == "F"
                && new TypeKey { Family = null, Type = null }.ToString() == "");
            check(P + "ValuePick shows 'Parameter = Value'", new ValuePick { ParameterName = "Level", Value = "L1" }.ToString() == "Level = L1");
            check(P + "Rule reads as a sentence for every operator",
                R("A", RuleOp.Is, "1", "2").ToString() == "A is 1, 2"
                && R("A", RuleOp.IsNot, "1").ToString() == "A is not 1"
                && R("A", RuleOp.Contains, "x").ToString() == "A contains x"
                && R("A", RuleOp.DoesNotContain, "x").ToString() == "A does not contain x"
                && R("A", RuleOp.IsEmpty, "ignored").ToString() == "A is empty"
                && R("A", RuleOp.IsNotEmpty).ToString() == "A is not empty");
            check(P + "Rule key groups by scope, operator and parameter, ignoring case",
                R("Mark", RuleOp.Is).Key == R("MARK", RuleOp.Is).Key && R("Mark", RuleOp.Is).Key != R("Mark", RuleOp.IsNot).Key
                && new Rule { ParameterName = null }.Key == "Either|Is|");
        }
    }
}
