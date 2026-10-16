using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>A parameter to search in, and where to read it from.</summary>
    public class ParamRef
    {
        public string Name = "";
        public ParamScope Scope = ParamScope.Either;

        public override string ToString()
        {
            return Scope == ParamScope.Either ? Name : Name + " (" + Scope + ")";
        }

        public string Key { get { return Scope + "::" + (Name ?? "").ToLowerInvariant(); } }
    }

    /// <summary>
    /// One exact family+type identity, excluded as a unit.
    ///
    /// <see cref="ValuePick"/>s are OR'd against each other, so they cannot say
    /// "family X AND type Y" -- and <see cref="ParameterFilter.ExcludeKeyword"/> is a
    /// SUBSTRING over the family name, which is worse than imprecise: ticking one
    /// Pre Action Valve in the audit wrote the family name ABC_MEC_Generic_Fire and
    /// silently dropped 106 elements, including two 425 m3 fire water tanks, three
    /// pumps, and 62 Fire Hose Reel Cabinets from the unrelated family
    /// ABC_MEC_Generic_FireHoseReelCabinet, which merely starts with the same text.
    ///
    /// An empty <see cref="Type"/> means the whole family, matched exactly --
    /// deliberate family-level drops stay possible, they just stop happening by
    /// accident.
    /// </summary>
    public class TypeKey
    {
        public string Family = "";
        public string Type = "";

        public override string ToString()
        {
            return (Family ?? "") + (string.IsNullOrEmpty(Type) ? "" : " : " + Type);
        }
    }

    /// <summary>How a rule compares a parameter to its values.</summary>
    public enum RuleOp { Is, IsNot, Contains, DoesNotContain, IsEmpty, IsNotEmpty }

    /// <summary>
    /// One row of the redesigned filter: a parameter, an operator, and the values it
    /// is compared against. Rules are ANDed -- an element is kept only if every rule
    /// holds. Within one rule the values OR ("is one of A, B").
    ///
    /// This is the single model the three old boxes collapse into:
    ///   Keep / Only picks -> Is           Keep keyword      -> Contains
    ///   Drop picks        -> IsNot         Drop keyword      -> DoesNotContain
    /// plus IsEmpty / IsNotEmpty for "has no value" / "has any value".
    /// </summary>
    public class Rule
    {
        public string ParameterName = "";
        public ParamScope Scope = ParamScope.Either;
        public RuleOp Op = RuleOp.Is;
        public List<string> Values = new List<string>();

        /// <summary>Groups serialized value-lines back into one rule.</summary>
        public string Key { get { return Scope + "|" + Op + "|" + (ParameterName ?? "").ToLowerInvariant(); } }

        public override string ToString()
        {
            var op = Op == RuleOp.Is ? "is" : Op == RuleOp.IsNot ? "is not"
                   : Op == RuleOp.Contains ? "contains" : Op == RuleOp.DoesNotContain ? "does not contain"
                   : Op == RuleOp.IsEmpty ? "is empty" : "is not empty";
            if (Op == RuleOp.IsEmpty || Op == RuleOp.IsNotEmpty) return ParameterName + " " + op;
            return ParameterName + " " + op + " " + string.Join(", ", Values.ToArray());
        }
    }

    /// <summary>How the members of a set combine.</summary>
    public enum RuleJoin { And, Or }

    /// <summary>
    /// A bracketed set of rules with its own AND/OR, so a filter can say
    /// "(Description is cable-tray OR System Type is CHW)" as ONE unit. Groups are
    /// then ANDed with each other and with the flat <see cref="ParameterFilter.Rules"/>,
    /// which is what gives "(A OR B) AND C" -- the case a flat AND list cannot hold.
    ///
    /// Top level stays AND on purpose (ANDs of ORs): it covers the real filters and
    /// keeps both the matcher and the UI legible. A top-level OR of ANDs is not
    /// expressible, and is deliberately out of scope.
    /// </summary>
    public class RuleGroup
    {
        public RuleJoin Join = RuleJoin.Or;   // an AND group is just flat rules, so OR is the useful default
        public List<Rule> Rules = new List<Rule>();
    }

    /// <summary>One exact value the user ticked in the Browse list.</summary>
    public class ValuePick
    {
        public string ParameterName = "";
        public ParamScope Scope = ParamScope.Either;

        /// <summary>As shown to the user; comparison uses the normalised form.</summary>
        public string Value = "";

        public override string ToString() { return ParameterName + " = " + Value; }
    }

    /// <summary>
    /// What counts as "my discipline" on one side of the check.
    ///
    /// Modelled on the Federated Clash set builder rather than on a stack of
    /// per-parameter conditions, because that is the shape that actually fits the
    /// job: you rarely know the exact string to type, but you do know it when you
    /// see it in a list of what the model contains.
    ///
    /// Two ways in, and they are OR'd, not ANDed:
    ///   - KEYWORDS, comma separated, substring-matched. Good for a one-off.
    ///   - PICKED VALUES, chosen from a Browse list of the distinct values actually
    ///     present, with counts. Exact match.
    /// An element is in the set if any keyword hits OR any picked value matches.
    /// A filter with neither matches everything, so an unconfigured filter can never
    /// silently hide anything.
    /// </summary>
    public partial class ParameterFilter
    {
        /// <summary>
        /// The parameters keywords are searched in. Picked values carry their own
        /// parameter, so this list only governs the keyword box.
        /// </summary>
        public List<ParamRef> Parameters = new List<ParamRef>();

        /// <summary>Comma-separated substrings.</summary>
        public string Keyword = "";

        public List<ValuePick> Picked = new List<ValuePick>();

        /// <summary>
        /// Comma-separated substrings that REMOVE an element from the set.
        ///
        /// Inclusion alone cannot express "everything except X": adding X as a
        /// keyword makes X the only thing checked. On this project every one of
        /// 3,912 clashes was a service against a Generic Model, and 1,978 of those
        /// Generic Models were modelled maintenance-access zones — real geometry,
        /// but a different conversation from pipe-hits-duct, and drowning it out.
        /// Excluding them by family name leaves the other 90 generic models in play.
        /// </summary>
        /// Searched in the same nominated <see cref="Parameters"/> as the include
        /// keywords, so to drop by family name, add Family Name to that list.
        public string ExcludeKeyword = "";

        /// <summary>Exact values that remove an element from the set.</summary>
        public List<ValuePick> Excluded = new List<ValuePick>();

        /// <summary>Exact family+type pairs removed from the set. See <see cref="TypeKey"/>.</summary>
        public List<TypeKey> ExcludedTypes = new List<TypeKey>();

        /// <summary>
        /// "Only" -- a value the element MUST have, ANDed across parameters.
        ///
        /// Keep is an OR: adding Workset = MEP_ZZ_MEPServices to Keep widens the set to
        /// everything on that workset, whatever else was kept. Only is the AND the
        /// filter never had: every distinct parameter named here must match one of its
        /// listed values, or the element is out. Within one parameter the values OR
        /// (Workset = A or B); across parameters they AND (Workset = A AND Level = 03).
        ///
        /// An element with no value for an Only parameter FAILS it -- "only
        /// MEPServices" excludes something with no workset, which is the point. In a
        /// non-workshared model every element has no workset, so an Only-Workset rule
        /// would exclude everything; the window warns rather than silently returning a
        /// clean run.
        ///
        /// Precedence: Drop beats Only beats Keep.
        /// </summary>
        public List<ValuePick> OnlyValues = new List<ValuePick>();

        /// <summary>
        /// The redesigned filter: a flat list of <see cref="Rule"/>s, all ANDed. This is
        /// the model the UI moves to; the fields above are migrated into it. Empty here
        /// means the filter still runs on the legacy fields, so old saved filters keep
        /// working until they are migrated.
        /// </summary>
        public List<Rule> Rules = new List<Rule>();

        /// <summary>
        /// OR/AND-bracketed rule sets, ANDed on top of the flat <see cref="Rules"/>.
        /// Empty by default, so every existing filter is unchanged. This is what lets a
        /// side express "(A OR B) AND C": C is a flat rule, (A OR B) is one group.
        /// </summary>
        public List<RuleGroup> Groups = new List<RuleGroup>();

        public bool IsEmpty
        {
            get
            {
                return ParseKeywords().Count == 0 && Picked.Count == 0
                    && ParseExcludeKeywords().Count == 0 && Excluded.Count == 0
                    && ExcludedTypes.Count == 0 && OnlyValues.Count == 0
                    && Rules.Count == 0 && Groups.Count == 0;
            }
        }

        /// <summary>
        /// Split, cached against the source string.
        ///
        /// Matches runs for every source element AND every narrow-phase candidate, and
        /// this used to re-split and re-lower-case both keyword strings on every one of
        /// those calls. The fields are public and mutated directly by the window and by
        /// the audit, so there is no setter to hook -- comparing the string is the cheap
        /// way to notice a change, and far cheaper than the split it avoids.
        /// </summary>
        private List<string> _kwCache, _xkwCache;
        private string _kwSource, _xkwSource;

        private List<string> CachedKeywords()
        {
            if (_kwCache == null || _kwSource != Keyword)
            {
                _kwSource = Keyword;
                _kwCache = Split(Keyword);
            }
            return _kwCache;
        }

        private List<string> CachedExcludeKeywords()
        {
            if (_xkwCache == null || _xkwSource != ExcludeKeyword)
            {
                _xkwSource = ExcludeKeyword;
                _xkwCache = Split(ExcludeKeyword);
            }
            return _xkwCache;
        }

        // Public callers get a COPY. They are not on the hot path, and handing out the
        // cached list would let a caller mutate it -- AuditWindow keeps the result of
        // ParseExcludeKeywords for the lifetime of the dialog.
        public List<string> ParseKeywords() { return new List<string>(CachedKeywords()); }


        /// <summary>
        /// Split as the user typed it. <see cref="Split"/> lower-cases for comparison,
        /// which is wrong for anything that gets written back to the settings file or
        /// shown in the window -- family names would come back mangled.
        /// </summary>
        public static List<string> SplitKeepingCase(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var raw in s.Split(','))
            {
                var k = raw.Trim();
                if (k.Length > 0) list.Add(k);
            }
            return list;
        }
        public List<string> ParseExcludeKeywords() { return new List<string>(CachedExcludeKeywords()); }

        private static List<string> Split(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var raw in s.Split(','))
            {
                var k = raw.Trim().ToLowerInvariant();
                if (k.Length > 0) list.Add(k);
            }
            return list;
        }

        /// <summary>Trimmed, lower-cased. Comparison form only — never displayed.</summary>
        public static string Norm(string s)
        {
            return s == null ? "" : s.Trim().ToLowerInvariant();
        }

        public bool Matches(Element e)
        {
            if (e == null) return true;

            var keywords = CachedKeywords();
            var excludeWords = CachedExcludeKeywords();
            bool hasInclude = keywords.Count > 0 || Picked.Count > 0;
            bool hasExclude = excludeWords.Count > 0 || Excluded.Count > 0
                              || ExcludedTypes.Count > 0;

            // OnlyValues alone is a real filter, so it has to keep this guard from
            // short-circuiting. Without it, a filter whose ONLY rule was an Only rule
            // passed every element -- the guard fired before Only was ever consulted.
            if (!hasInclude && !hasExclude && OnlyValues.Count == 0
                && Rules.Count == 0 && Groups.Count == 0) return true;

            // One read cache for BOTH passes below. The drop pass and the keep pass
            // nominate overlapping parameters -- always the same Parameters list, often
            // the same picked ones -- and each used to build its own dictionary and read
            // every parameter again from scratch. Reading a Revit parameter is the
            // expensive part of filtering, and this runs on every source element and
            // every narrow-phase candidate.
            //
            // Populated lazily rather than up front, so an element rejected by the drop
            // pass never pays to read parameters only the keep pass would have wanted.
            Dictionary<string, string> read = null;

            // Exclusion is checked FIRST and wins outright. "Everything except X"
            // has to mean exactly that, whatever the include side says.
            if (hasExclude)
            {
                // HitsType first: it reads the element's type, not its parameters, so it
                // is the cheapest way to answer and often answers on its own.
                if (HitsType(e)) return false;
                if (read == null) read = new Dictionary<string, string>();
                if (Hits(e, excludeWords, Excluded, read)) return false;
            }

            // Only: every named parameter must match one of its values. ANDed across
            // parameters, and it beats Keep -- checked before the include pass so a
            // failing Only rejects regardless of what Keep would have said.
            if (OnlyValues.Count > 0)
            {
                if (read == null) read = new Dictionary<string, string>();
                if (!OnlyMatches(e, read)) return false;
            }

            // Rules: the redesigned model. All ANDed; every rule must hold.
            if (Rules.Count > 0)
            {
                if (read == null) read = new Dictionary<string, string>();
                foreach (var rule in Rules)
                    if (!RuleHolds(e, rule, read)) return false;
            }

            // Groups: each is an OR (or AND) of rules, and the groups are ANDed on top
            // of the flat rules. This is the "(A OR B) AND C" the flat list cannot hold.
            if (Groups.Count > 0)
            {
                if (read == null) read = new Dictionary<string, string>();
                foreach (var g in Groups)
                    if (!GroupHolds(e, g, read)) return false;
            }

            // Exclusion on its own means "everything else", so an empty include side
            // is a pass rather than a fail.
            if (!hasInclude) return true;

            if (read == null) read = new Dictionary<string, string>();
            return Hits(e, keywords, Picked, read);
        }

        /// <summary>
        /// Read one parameter through the per-call cache. A miss reads from Revit; a
        /// hit costs a dictionary lookup. Never returns null, so callers do not have to
        /// distinguish "absent" from "empty" -- they never did.
        /// </summary>
        private static string ReadCached(Element e, Dictionary<string, string> cache,
                                         string name, ParamScope scope)
        {
            var key = scope + "::" + (name ?? "").ToLowerInvariant();
            string v;
            if (cache.TryGetValue(key, out v)) return v;

            v = ParameterReader.Read(e, name, scope) ?? "";
            cache[key] = v;
            return v;
        }

        /// <summary>
        /// Does one rule hold for this element? Values within a rule OR; IsEmpty and
        /// IsNotEmpty ignore the values. A rule with no values (for the value-based
        /// operators) is treated as incomplete and passes, so a half-built rule in the
        /// UI never silently excludes everything.
        /// </summary>
        private bool RuleHolds(Element e, Rule r, Dictionary<string, string> read)
        {
            if (r == null) return true;
            var raw = ReadCached(e, read, r.ParameterName, r.Scope);
            var val = Norm(raw);

            switch (r.Op)
            {
                case RuleOp.IsEmpty:      return val.Length == 0;
                case RuleOp.IsNotEmpty:   return val.Length > 0;

                case RuleOp.Is:
                {
                    bool any = false;
                    foreach (var v in r.Values) { var w = Norm(v); if (w.Length == 0) continue; any = true; if (val == w) return true; }
                    return !any;   // no usable values = incomplete rule, do not exclude
                }
                case RuleOp.IsNot:
                {
                    foreach (var v in r.Values) { var w = Norm(v); if (w.Length == 0) continue; if (val == w) return false; }
                    return true;
                }
                case RuleOp.Contains:
                {
                    var low = raw.ToLowerInvariant();
                    bool any = false;
                    foreach (var v in r.Values) { var w = (v ?? "").Trim().ToLowerInvariant(); if (w.Length == 0) continue; any = true; if (low.IndexOf(w, StringComparison.Ordinal) >= 0) return true; }
                    return !any;
                }
                case RuleOp.DoesNotContain:
                {
                    var low = raw.ToLowerInvariant();
                    foreach (var v in r.Values) { var w = (v ?? "").Trim().ToLowerInvariant(); if (w.Length == 0) continue; if (low.IndexOf(w, StringComparison.Ordinal) >= 0) return false; }
                    return true;
                }
            }
            return true;
        }

        /// <summary>
        /// Does this group hold for the element? An OR group holds when ANY of its rules
        /// holds; an AND group when they all do. An empty group holds -- there is nothing
        /// in it to fail, so it never narrows anything (matching the flat-rules guard).
        /// </summary>
        private bool GroupHolds(Element e, RuleGroup g, Dictionary<string, string> read)
        {
            if (g == null || g.Rules.Count == 0) return true;
            if (g.Join == RuleJoin.Or)
            {
                foreach (var r in g.Rules) if (RuleHolds(e, r, read)) return true;
                return false;
            }
            foreach (var r in g.Rules) if (!RuleHolds(e, r, read)) return false;
            return true;
        }

        /// <summary>
        /// Every parameter named in <see cref="OnlyValues"/> must hold one of its
        /// listed values. Values for the same parameter OR together; different
        /// parameters AND. An element lacking a value for a constrained parameter
        /// fails it -- that is what makes "only workset X" exclude the unworkseted.
        /// </summary>
        private bool OnlyMatches(Element e, Dictionary<string, string> read)
        {
            // Group by (scope, parameter). A parameter is constrained only if at least
            // one of its entries has a non-empty value.
            var groups = new Dictionary<string, List<string>>();
            foreach (var pick in OnlyValues)
            {
                var want = Norm(pick.Value);
                if (want.Length == 0) continue;
                var key = pick.Scope + "::" + (pick.ParameterName ?? "").ToLowerInvariant();
                List<string> vals;
                if (!groups.TryGetValue(key, out vals)) { vals = new List<string>(); groups[key] = vals; }
                vals.Add(want);
            }

            foreach (var kv in groups)
            {
                // key is scope::name; recover both to read.
                var sep = kv.Key.IndexOf("::", StringComparison.Ordinal);
                var scopeStr = kv.Key.Substring(0, sep);
                var name = kv.Key.Substring(sep + 2);
                ParamScope scope;
                try { scope = (ParamScope)Enum.Parse(typeof(ParamScope), scopeStr); }
                catch { scope = ParamScope.Either; }

                // The stored name is lower-cased in the key; read the original casing
                // from the first matching OnlyValues entry so ParameterReader sees it
                // exactly as the user picked it.
                string realName = name;
                foreach (var pick in OnlyValues)
                    if ((pick.Scope + "::" + (pick.ParameterName ?? "").ToLowerInvariant()) == kv.Key)
                    { realName = pick.ParameterName; break; }

                var v = Norm(ReadCached(e, read, realName, scope));
                bool matched = false;
                foreach (var want in kv.Value) if (v == want) { matched = true; break; }
                if (!matched) return false;
            }
            return true;
        }

        private bool Hits(Element e, List<string> keywords, List<ValuePick> picks,
                          Dictionary<string, string> read)
        {
            // Keywords search only the nominated parameters.
            if (keywords.Count > 0)
            {
                foreach (var p in Parameters)
                {
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    var v = ReadCached(e, read, p.Name, p.Scope);
                    if (v.Length == 0) continue;

                    var lower = v.ToLowerInvariant();
                    foreach (var k in keywords)
                        if (lower.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
                }
            }

            // Picked values are exact, on their own parameter.
            foreach (var pick in picks)
            {
                // An EMPTY pick would normalise to "" -- and so does every element
                // with no value at all, so one empty pick would quietly select
                // everything that simply lacks the parameter. The Browse scan drops
                // empties, but a hand-edited settings file might not.
                var want = Norm(pick.Value);
                if (want.Length == 0) continue;

                var v = ReadCached(e, read, pick.ParameterName, pick.Scope);
                if (Norm(v) == want) return true;
            }

            return false;
        }

        /// <summary>
        /// Does this element's exact family+type appear in this filter's own
        /// <see cref="ExcludedTypes"/>?
        /// </summary>
        private bool HitsType(Element e)
        {
            return ExcludedTypes.Count > 0 && TypeExcluded(e, ExcludedTypes);
        }

        /// <summary>
        /// Does this element's exact family+type appear in <paramref name="drops"/>?
        /// Compared whole, never as a substring, so a family name that happens to be a
        /// prefix of another family cannot reach it. An empty <see cref="TypeKey.Type"/>
        /// means the whole family -- an explicit choice, not the accidental family-wide
        /// reach keyword matching used to give.
        ///
        /// The single matcher behind both a filter's own <see cref="ExcludedTypes"/> and
        /// the GLOBAL audit drops (<see cref="ClashSettings.AuditDrops"/>). The engine
        /// calls it with the global list for every set, so a family+type ticked once in
        /// the Audit window is dropped across the whole matrix rather than only in the
        /// set that happened to be active -- with no per-set copy to keep in sync.
        /// </summary>
        public static bool TypeExcluded(Element e, List<TypeKey> drops)
        {
            if (e == null || drops == null || drops.Count == 0) return false;

            string famName, typeName;
            try
            {
                var t = e.Document.GetElement(e.GetTypeId()) as ElementType;
                if (t == null) return false;
                famName = t.FamilyName;
                typeName = t.Name;
            }
            catch { return false; }

            var f = Norm(famName);
            var y = Norm(typeName);
            // The TYPE name is what identifies the row; an element with none cannot be
            // matched at all. FamilyName legitimately comes back empty for plenty of
            // system-family types (pipes, ducts, conduits, cable trays have no family
            // grouping in the Project Browser) -- bailing out on an empty FAMILY here
            // used to make every drop on one of those categories a silent no-op, because
            // no drop entry could ever compare equal to it. Found reviewing the whole
            // codebase, 2026-09-27.
            if (y.Length == 0) return false;

            foreach (var k in drops)
            {
                if (k == null || Norm(k.Family) != f) continue;
                var kt = Norm(k.Type);
                if (kt.Length == 0 || kt == y) return true;
            }
            return false;
        }


        public string Summary()
        {
            if (IsEmpty) return "everything in the ticked categories";

            var bits = new List<string>();
            var kw = ParseKeywords();
            if (kw.Count > 0) bits.Add(kw.Count + " keyword" + (kw.Count == 1 ? "" : "s"));
            if (Picked.Count > 0) bits.Add(Picked.Count + " picked value" + (Picked.Count == 1 ? "" : "s"));
            var inc = bits.Count > 0 ? string.Join(" or ", bits.ToArray()) : "everything";

            if (OnlyValues.Count > 0)
                inc += " (only where " + OnlyValues.Count + " rule" + (OnlyValues.Count == 1 ? "" : "s") + " hold)";

            var ex = new List<string>();
            var xkw = ParseExcludeKeywords();
            if (xkw.Count > 0) ex.Add(xkw.Count + " keyword" + (xkw.Count == 1 ? "" : "s"));
            if (Excluded.Count > 0) ex.Add(Excluded.Count + " value" + (Excluded.Count == 1 ? "" : "s"));
            var baseText = ex.Count == 0 ? inc : inc + ", excluding " + string.Join(" or ", ex.ToArray());

            // Rules and groups are the current model; the legacy bits above are usually
            // empty now, so without this the summary reads "everything" for a real filter.
            var rg = new List<string>();
            if (Rules.Count > 0) rg.Add(Rules.Count + " rule" + (Rules.Count == 1 ? "" : "s"));
            if (Groups.Count > 0) rg.Add(Groups.Count + " group" + (Groups.Count == 1 ? "" : "s"));
            if (rg.Count == 0) return baseText;
            var rgText = string.Join(" + ", rg.ToArray());
            return baseText == "everything" ? rgText : baseText + "; " + rgText;
        }

        // ------------------------------------------------------------ persistence

        /// <summary>
        /// Express this filter's legacy fields as an equivalent flat list of Rules.
        ///
        /// The legacy matcher checks exclude, then Only, then include -- and that
        /// precedence (Drop beats Only beats Keep) is exactly what ANDing rules gives,
        /// because every rule must pass. So the mapping is exact for everything except
        /// one shape the flat model cannot hold: an OR across DIFFERENT parameters.
        /// The include side is an OR (keep if any keyword hits OR any pick matches), so
        /// it only converts when it reduces to a single constraint. The exclude and
        /// Only sides are ANDs already and always convert.
        ///
        /// ExcludedTypes (the audit's family+type pairs) are left alone -- they are a
        /// compound identity, not a single-parameter rule, and keep their own path.
        /// </summary>
        /// <returns>true when the produced rules match the legacy fields exactly.</returns>
        public bool TryToRules(out List<Rule> rules)
        {
            rules = new List<Rule>();
            bool exact = true;

            // --- exclude keyword: keep only if NO nominated param contains any of them.
            // Per param, "does not contain {all}" ANDs to exactly that (De Morgan).
            var xkw = SplitKeepingCase(ExcludeKeyword);
            if (xkw.Count > 0)
                foreach (var p in Parameters)
                    if (!string.IsNullOrEmpty(p.Name))
                        rules.Add(new Rule { Scope = p.Scope, Op = RuleOp.DoesNotContain,
                                             ParameterName = p.Name, Values = new List<string>(xkw) });

            // --- excluded picks: "is not", grouped by parameter.
            foreach (var g in GroupPicks(Excluded))
                rules.Add(new Rule { Scope = g.Scope, Op = RuleOp.IsNot,
                                     ParameterName = g.Name, Values = g.Values });

            // --- Only values: "is", grouped by parameter.
            foreach (var g in GroupPicks(OnlyValues))
                rules.Add(new Rule { Scope = g.Scope, Op = RuleOp.Is,
                                     ParameterName = g.Name, Values = g.Values });

            // --- include side (OR): converts only as a single constraint.
            var kw = SplitKeepingCase(Keyword);
            bool hasKw = kw.Count > 0;
            var pickGroups = GroupPicks(Picked);
            bool hasPick = pickGroups.Count > 0;

            if (hasKw && hasPick) exact = false;                 // OR across keyword + picks
            else if (hasPick)
            {
                if (pickGroups.Count > 1) exact = false;         // OR across parameters
                else rules.Add(new Rule { Scope = pickGroups[0].Scope, Op = RuleOp.Is,
                                          ParameterName = pickGroups[0].Name, Values = pickGroups[0].Values });
            }
            else if (hasKw)
            {
                var named = new List<ParamRef>();
                foreach (var p in Parameters) if (!string.IsNullOrEmpty(p.Name)) named.Add(p);
                if (named.Count != 1) exact = false;             // keyword ORs across params
                else rules.Add(new Rule { Scope = named[0].Scope, Op = RuleOp.Contains,
                                          ParameterName = named[0].Name, Values = kw });
            }

            return exact;
        }

        /// <summary>
        /// The rules a rule-list UI should show and edit for this filter.
        ///
        /// A filter saved in the new format carries its rules in <see cref="Rules"/>,
        /// and those are returned verbatim (as a copy, so editing the rows cannot
        /// mutate the live filter before it is committed). Only a purely legacy filter
        /// -- Keep/Drop/Only with no Rules yet -- is migrated via <see cref="TryToRules"/>.
        ///
        /// Reading the migration UNCONDITIONALLY was the FilterWindow bug: TryToRules
        /// never looks at Rules, so a populated new-format filter showed no rows and the
        /// window then saved the blank back over it. Every rule-display path must go
        /// through here, not straight to TryToRules, so that regression cannot recur.
        /// </summary>
        public List<Rule> RulesForDisplay()
        {
            if (Rules.Count > 0) return new List<Rule>(Rules);
            List<Rule> migrated;
            TryToRules(out migrated);
            return migrated;
        }

        private class PickGroup { public string Name; public ParamScope Scope; public List<string> Values = new List<string>(); }

        private static List<PickGroup> GroupPicks(List<ValuePick> picks)
        {
            var groups = new List<PickGroup>();
            var index = new Dictionary<string, PickGroup>();
            foreach (var v in picks)
            {
                if (string.IsNullOrEmpty(Norm(v.Value))) continue;
                var key = v.Scope + "::" + (v.ParameterName ?? "").ToLowerInvariant();
                PickGroup g;
                if (!index.TryGetValue(key, out g))
                {
                    g = new PickGroup { Name = v.ParameterName, Scope = v.Scope };
                    index[key] = g; groups.Add(g);
                }
                g.Values.Add(v.Value);
            }
            return groups;
        }

        /// <summary>
        /// System Type and Description are what a services model is actually labelled
        /// with, so they are the default place keywords look — the same two the
        /// Federated Clash tool uses.
        /// </summary>
        public static ParameterFilter Default()
        {
            var f = new ParameterFilter();
            f.Parameters.Add(new ParamRef { Name = "System Type", Scope = ParamScope.Either });
            f.Parameters.Add(new ParamRef { Name = "Description", Scope = ParamScope.Either });
            return f;
        }
    }
}
