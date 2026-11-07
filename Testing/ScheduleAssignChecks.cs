using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B2: the Assign dialog's model (spec 9, Q4, 6.7). Revit-free.
    /// Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class ScheduleAssignChecks
    {
        private const string P = "schedule assign: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Heading(check);
            Candidates(check);
            Search(check);
            Choice(check);
            Typed(check);
            Pending(check);
            Fallbacks(check);
            Robust(check);
        }

        // Branch coverage, added at green: empty reasons, a null entry, and the hint with nothing usable typed.
        private static void Fallbacks(Action<string, bool?> check)
        {
            var a = new AssignModel(Rows("JD-0042"), new[] { C("kdoe", ""), null, C("KDOE", "owns Workset1") }, "", null);
            check(P + "a duplicate whose first reason was empty takes the later one, and a null entry is skipped", a.Visible.Count == 1 && a.Visible[0].Reason == "owns Workset1");
            var b = new AssignModel(Rows("JD-0042"), new[] { C("jdoe", "") }, "jdoe", null);
            check(P + "this user named with no reason is just \"you\"", b.Visible.Count == 1 && b.Visible[0].Reason == "you");
            var c = Make("asmith");
            c.SelectOther();
            check(P + "no hint while nothing is typed", c.OtherHint == "" && c.Problem == "");
            c.OtherName = "bad\tname";
            check(P + "no hint for a name that cannot be used", c.OtherHint == "" && c.Problem != "");
            var d = Make("asmith");
            d.OtherName = "newhire";
            d.SelectCandidate("asmith");
            check(P + "no hint and no problem unless \"other\" is the choice", d.OtherHint == "" && d.Problem == "");
        }

        private static AssignCandidate C(string name, string reason = "") { return new AssignCandidate { Name = name, Reason = reason }; }

        private static List<string> Rows(params string[] rows) { return rows.ToList(); }

        private static AssignModel Make(params string[] names)
        {
            return new AssignModel(Rows("JD-0042"), names.Select(n => C(n, "seen")).ToList(), "jdoe", null);
        }

        private static void Heading(Action<string, bool?> check)
        {
            check(P + "heading for one row: \"Assign JD-0042\"", new AssignModel(Rows("JD-0042"), null, "jdoe", null).Heading == "Assign JD-0042");
            check(P + "heading for three rows: \"Assign JD-0042 (and 2 more)\"", new AssignModel(Rows("JD-0042", "JD-0043", "JD-0044"), null, "jdoe", null).Heading == "Assign JD-0042 (and 2 more)");
            check(P + "heading without issue numbers: \"Assign 3 clashes\", \"Assign 1 clash\"",
                new AssignModel(Rows("", null, " "), null, "jdoe", null).Heading == "Assign 3 clashes" && new AssignModel(Rows(""), null, "jdoe", null).Heading == "Assign 1 clash");
            check(P + "heading uses the first row that has a number", new AssignModel(Rows("", "JD-0007", "JD-0008"), null, "jdoe", null).Heading == "Assign JD-0007 (and 2 more)");
            var none = new AssignModel(Rows(), new[] { C("asmith") }, "jdoe", null);
            none.SelectCandidate("asmith");
            check(P + "no rows: nothing can be assigned", !none.CanAssign && none.Result() == null && none.Heading == "Assign");
            check(P + "null rows are no rows", !new AssignModel(null, null, "jdoe", null).CanAssign && new AssignModel(null, null, "jdoe", null).Heading == "Assign");
        }

        private static void Candidates(Action<string, bool?> check)
        {
            var m = new AssignModel(Rows("JD-0042"), new[] { C("kdoe", "last changed 6 of these elements"), C("asmith", "created 41 of the A elements"), C("KDOE", "owns Workset1"), C("Bob") }, "zed", null);
            var names = m.Visible.Select(v => v.Name).ToList();
            check(P + "names are sorted ignoring capitals, duplicates merged, this user added: asmith, Bob, kdoe, zed", string.Join(",", names) == "asmith,Bob,kdoe,zed");
            var k = m.Visible.FirstOrDefault(v => v.Name == "kdoe");
            check(P + "...a merged name keeps the first spelling and joins the reasons", k != null && k.Reason == "last changed 6 of these elements; owns Workset1");
            check(P + "...this user is listed with \"you\" even if nobody named them", m.Visible.Any(v => v.Name == "zed" && v.Reason == "you"));
            var me = new AssignModel(Rows("JD-0042"), new[] { C("JDoe", "owns Workset1") }, "jdoe", null);
            check(P + "...and \"you\" comes first in the reason when they were named", me.Visible.Count == 1 && me.Visible[0].Reason == "you; owns Workset1" && me.Visible[0].Name == "JDoe");
            var blank = new AssignModel(Rows("JD-0042"), new[] { C(null), C(""), C("  "), C("asmith") }, "", null);
            check(P + "blank names are skipped and an empty user is not added", blank.Visible.Count == 1 && blank.Visible[0].Name == "asmith");
            check(P + "no candidates at all is an empty list", new AssignModel(Rows("JD-0042"), null, null, null).Visible.Count == 0);
        }

        private static void Search(Action<string, bool?> check)
        {
            var m = new AssignModel(Rows("JD-0042"), new[] { C("asmith"), C("jdoe"), C("kdoe"), C("Ana") }, "jdoe", null);
            m.Search = "as";
            check(P + "search keeps names that contain the text, any capitals", string.Join(",", m.Visible.Select(v => v.Name)) == "asmith");
            m.Search = "  DOE ";
            check(P + "...trimmed", string.Join(",", m.Visible.Select(v => v.Name)) == "jdoe,kdoe");
            m.Search = "";
            check(P + "...empty shows everyone", m.Visible.Count == 4);
            m.Search = null;
            check(P + "...so does null", m.Visible.Count == 4);
            m.Search = "zzz";
            check(P + "...and a search nobody matches shows nobody", m.Visible.Count == 0);

            m.Search = "";
            m.SelectCandidate("kdoe");
            m.Search = "do";
            check(P + "a selection survives a search that still shows it", m.Mode == AssignMode.Candidate && m.SelectedName == "kdoe" && m.CanAssign);
            m.Search = "as";
            check(P + "...and is cleared when the search hides it", m.Mode == AssignMode.None && m.SelectedName == "" && !m.CanAssign);
            m.Search = "";
            m.SelectUnassign();
            m.Search = "zzz";
            check(P + "searching never changes an Unassign choice", m.Mode == AssignMode.Unassign && m.CanAssign);
        }

        private static void Choice(Action<string, bool?> check)
        {
            var m = Make("asmith", "kdoe");
            check(P + "nothing selected: nothing to assign", m.Mode == AssignMode.None && !m.CanAssign && m.Result() == null);
            check(P + "selecting a candidate returns true", m.SelectCandidate("kdoe"));
            var r = m.Result();
            check(P + "...and gives its name, not flagged", m.CanAssign && r != null && r.User == "kdoe" && !r.NotSeen && !r.Unassign);
            check(P + "selecting is case-insensitive and gives the listed spelling", m.SelectCandidate("ASMITH") && m.Result().User == "asmith");

            bool before = m.SelectedName == "asmith";
            check(P + "an unknown name cannot be selected and changes nothing", !m.SelectCandidate("nobody") && m.SelectedName == "asmith" && before && m.Mode == AssignMode.Candidate);
            m.Search = "kd";
            check(P + "a hidden name cannot be selected either", !m.SelectCandidate("asmith"));
            m.Search = "";

            m.SelectUnassign();
            var u = m.Result();
            check(P + "unassign gives an empty user and the Unassign flag", m.CanAssign && u != null && u.Unassign && u.User == "" && !u.NotSeen);
            check(P + "picking a candidate after unassign leaves it", m.SelectCandidate("kdoe") && m.Mode == AssignMode.Candidate && !m.Result().Unassign);
        }

        private static void Typed(Action<string, bool?> check)
        {
            var m = Make("asmith");
            m.SelectOther();
            check(P + "other selected with nothing typed: cannot assign", m.Mode == AssignMode.Other && !m.CanAssign && m.Result() == null);
            m.OtherName = "   ";
            check(P + "...whitespace is nothing", !m.CanAssign);
            m.OtherName = "  newhire ";
            var r = m.Result();
            check(P + "a typed name not in the list is trimmed and flagged not seen", m.CanAssign && r != null && r.User == "newhire" && r.NotSeen && !r.Unassign);
            check(P + "...with the hint \"not seen in this model\"", m.OtherHint == "not seen in this model");
            m.OtherName = "ASMITH";
            var s = m.Result();
            check(P + "a typed name equal to a candidate gives that candidate, not flagged", s != null && s.User == "asmith" && !s.NotSeen && m.OtherHint == "");
            m.OtherName = "jdoe";
            check(P + "...this user typed is a candidate too", m.Result() != null && m.Result().User == "jdoe" && !m.Result().NotSeen);

            m.OtherName = "bad\tname";
            check(P + "a control character is refused with a message", !m.CanAssign && m.Problem == "A name cannot hold control characters." && m.Result() == null);
            m.OtherName = new string('a', 101);
            check(P + "more than 100 characters is refused with a message", !m.CanAssign && m.Problem == "A name can be at most 100 characters.");
            m.OtherName = new string('a', 100);
            check(P + "exactly 100 characters is fine", m.CanAssign && m.Problem == "" && m.Result().User.Length == 100);

            var t = Make("asmith");
            t.OtherName = "newhire";
            check(P + "typing in the other-name field selects \"other\"", t.Mode == AssignMode.Other && t.CanAssign);
            t.SelectCandidate("asmith");
            check(P + "...picking a candidate afterwards leaves it, the typed text is kept", t.Mode == AssignMode.Candidate && t.OtherName == "newhire" && t.Result().User == "asmith");
            t.OtherName = "";
            check(P + "clearing the field does not take the candidate away", t.Mode == AssignMode.Candidate && t.CanAssign);
            t.SelectOther();
            t.SelectUnassign();
            check(P + "unassign leaves other", t.Mode == AssignMode.Unassign && t.OtherHint == "");
        }

        private static void Pending(Action<string, bool?> check)
        {
            var none = new AssignModel(Rows("JD-0042"), null, "jdoe", null);
            check(P + "no pending lines: no pending heading", none.PendingHeading == "" && none.PendingLines.Count == 0);
            var some = new AssignModel(Rows("JD-0042", "JD-0043"), null, "jdoe", Rows("JD-0042: held by kdoe", "", null, "JD-0043: held by asmith"));
            check(P + "pending lines get a heading; blank lines are dropped",
                some.PendingHeading == "These will wait as pending, because another user holds them:" && some.PendingLines.Count == 2 && some.PendingLines[0] == "JD-0042: held by kdoe");
        }

        private static void Robust(Action<string, bool?> check)
        {
            var rnd = new Random(20261031);
            bool ok = true, consistent = true;
            var names = new[] { "asmith", "kdoe", "Ana", "", null, "jdoe" };
            for (int i = 0; i < 300; i++)
            {
                try
                {
                    var m = new AssignModel(Rows("JD-0042", null), names.Select(n => C(n)).ToList(), rnd.Next(2) == 0 ? "jdoe" : null, null);
                    for (int k = 0; k < 12; k++)
                    {
                        switch (rnd.Next(6))
                        {
                            case 0: m.Search = names[rnd.Next(names.Length)]; break;
                            case 1: m.SelectCandidate(names[rnd.Next(names.Length)]); break;
                            case 2: m.SelectUnassign(); break;
                            case 3: m.SelectOther(); break;
                            case 4: m.OtherName = names[rnd.Next(names.Length)] ?? "x\u0001y"; break;
                            default: m.Search = ""; break;
                        }
                        consistent &= (m.Result() != null) == m.CanAssign;
                    }
                }
                catch (Exception) { ok = false; }
            }
            check(P + "fuzz (300 dialogs, random operations): never throws", ok);
            check(P + "fuzz: the result exists exactly when Assign is allowed", consistent);
        }
    }
}
