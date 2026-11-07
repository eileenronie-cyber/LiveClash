using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule UI, slice B2: the confirmations that start unticked (E9): Remove resolved, Remove all,
    /// Remove parameters, and the non-workshared Create (Q5). Revit-free. Red-first: BUILD-LOG-UI.md.
    /// </summary>
    public static class ScheduleConfirmChecks
    {
        private const string P = "schedule confirm: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Gate(check);
            Texts(check);
            Edges(check);
        }

        private static List<ConfirmModel> All()
        {
            return new List<ConfirmModel> { ConfirmModel.RemoveResolved(8), ConfirmModel.RemoveAll(140), ConfirmModel.RemoveParameters(), ConfirmModel.CreateNonWorkshared() };
        }

        private static void Gate(Action<string, bool?> check)
        {
            bool unticked = All().All(m => !m.Ticked && !m.CanOk);
            check(P + "every kind starts unticked with OK off (E9)", unticked);
            var c = ConfirmModel.RemoveAll(140);
            check(P + "unticked: OK is off and pressing it accepts nothing", !c.CanOk && !c.Accepts(true));
            c.Ticked = true;
            check(P + "ticked: OK is on, and only pressing it accepts", c.CanOk && c.Accepts(true) && !c.Accepts(false));
            c.Ticked = false;
            check(P + "ticking then unticking turns OK off again", !c.CanOk && !c.Accepts(true));
            check(P + "the kinds are what they were made as",
                ConfirmModel.RemoveResolved(1).Kind == ConfirmKind.RemoveResolved && ConfirmModel.RemoveAll(1).Kind == ConfirmKind.RemoveAll
                && ConfirmModel.RemoveParameters().Kind == ConfirmKind.RemoveParameters && ConfirmModel.CreateNonWorkshared().Kind == ConfirmKind.CreateNonWorkshared);
        }

        private static void Texts(Action<string, bool?> check)
        {
            var r = ConfirmModel.RemoveResolved(8);
            check(P + "remove resolved: title, the count in plural, the check text",
                r.Title == "Remove resolved markers" && r.Lines.Contains("8 resolved markers will be deleted from the model.") && r.CheckText == "I understand these markers are deleted" && r.OkText == "Remove");
            check(P + "...and says held markers are passed over and what stays",
                r.Lines.Any(l => l.Contains("held by another user")) && r.Lines.Contains("One Undo. Your local record keeps every decision."));
            check(P + "...singular for one", ConfirmModel.RemoveResolved(1).Lines.Contains("1 resolved marker will be deleted from the model."));

            var a = ConfirmModel.RemoveAll(140);
            check(P + "remove all: title and the count",
                a.Title == "Remove all markers" && a.Lines.Contains("All 140 markers, the schedule, the marker type, the settings record and the counter records will be deleted.") && a.OkText == "Remove all");
            check(P + "...says what stays: the parameters and the workset", a.Lines.Any(l => l.Contains("parameters") && l.Contains("workset") && l.Contains("stay")));
            check(P + "...and the check text is its own", a.CheckText == "I understand this deletes every clash marker");
            check(P + "...singular for one", ConfirmModel.RemoveAll(1).Lines.Contains("The 1 marker, the schedule, the marker type, the settings record and the counter records will be deleted."));

            var p = ConfirmModel.RemoveParameters();
            check(P + "remove parameters: title, the warning about data everywhere, the check text",
                p.Title == "Remove LiveClash parameters" && p.Lines.Any(l => l.Contains("deletes their values")) && p.CheckText == "I understand the parameters are removed from the model" && p.OkText == "Remove");

            var n = ConfirmModel.CreateNonWorkshared();
            check(P + "non-workshared create: the check text, exactly (Q5)", n.CheckText == "I understand markers show in views and on sheets");
            check(P + "...and the lines say there is no hidden workset and markers would print", n.Title == "Create markers in a model without worksets" && n.Lines.Any(l => l.Contains("not workshared")) && n.Lines.Any(l => l.Contains("print")) && n.OkText == "Continue");
        }

        private static void Edges(Action<string, bool?> check)
        {
            var none = ConfirmModel.RemoveResolved(0);
            none.Ticked = true;
            check(P + "nothing to remove: it says so and OK stays off even when ticked", none.Lines.Contains("No markers are resolved.") && !none.CanOk && !none.Accepts(true));
            var neg = ConfirmModel.RemoveAll(-4);
            neg.Ticked = true;
            check(P + "no markers to remove (negative count): the same", neg.Lines.Contains("There are no markers to remove.") && !neg.CanOk);

            bool texts = true, buttons = true;
            foreach (var m in All().Concat(new[] { ConfirmModel.RemoveResolved(0), ConfirmModel.RemoveAll(0), ConfirmModel.RemoveResolved(1234567) }))
            {
                var t = new List<string> { m.Title, m.CheckText, m.OkText, m.CancelText };
                t.AddRange(m.Lines);
                texts &= t.All(x => !string.IsNullOrEmpty(x) && !x.Contains('{') && !x.Contains('}') && !x.Contains("null"));
                buttons &= m.OkText != m.CancelText;
            }
            check(P + "no text is empty, null or holds a stray brace", texts);
            check(P + "OK and Cancel never read the same", buttons);
            check(P + "big counts get a thousands separator", ConfirmModel.RemoveAll(1234567).Lines.Any(l => l.Contains("1,234,567")));
        }
    }
}
