using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The Revit-free half of the second-pass review's SP1: what the panel says when a button is pressed while the
    /// document that is open is not the session's. The decision itself (<see cref="SessionGuard.ShouldActOn"/>) is
    /// held by F1's checks; the in-Revit half, which presses the buttons with a family document active, is
    /// <see cref="SessionHandlerReviewChecks"/>.
    /// </summary>
    public static class SessionHandlerLogicChecks
    {
        private const string P = "session handlers logic: ";

        private const string Tail = ": this document (a family being edited, or another model) is not the one Live Clash lists clashes for."
                                  + " Nothing was changed.";

        public static void RunAll(Action<string, bool?> check)
        {
            string a = SessionGuard.Refusal("recheck");
            check(P + "SP1: the refusal tells the user to open the project and what to do there (\"" + a + "\")",
                a == "Open the project to recheck" + Tail);

            string b = SessionGuard.Refusal("run a full check");
            check(P + "SP1: ...with the action in its words (\"" + b + "\")",
                b == "Open the project to run a full check" + Tail);

            check(P + "SP1: ...and with no action it still says what to do",
                SessionGuard.Refusal(null) == "Open the project" + Tail && SessionGuard.Refusal("") == "Open the project" + Tail);

            DroppedRows(check);
        }

        // ------------------------------------------------------------------ SP4 / DM3

        private static ClashResult Row(string key)
        {
            return new ClashResult { Key = key, ModelName = "Structure", Severity = ClashSeverity.Hard, HostElementName = "h " + key, OtherElementName = "o " + key };
        }

        /// <summary>DM3: pairs dropped because their link instance was deleted are held, and listed again (Unverified)
        /// when an undo brings the instance back. Each check uses its own document key.</summary>
        private static void DroppedRows(Action<string, bool?> check)
        {
            const string S = "SP4: ";

            var r1 = Row("1|7|2");
            DroppedLinkRows.Hold("sp4-A", 7, new[] { r1 });
            var back = DroppedLinkRows.TakeBack("sp4-A", id => id == 7);
            check(P + S + "a held pair comes back as an Unverified copy when its instance is back",
                back.Count == 1 && back[0].Key == "1|7|2" && back[0].Severity == ClashSeverity.Unverified
                && r1.Severity == ClashSeverity.Hard && !ReferenceEquals(back[0], r1) && DroppedLinkRows.Count("sp4-A") == 0);
            check(P + S + "...and only once: a second look finds nothing",
                DroppedLinkRows.TakeBack("sp4-A", id => true).Count == 0);

            DroppedLinkRows.Hold("sp4-B", 8, new[] { Row("1|8|2") });
            check(P + S + "a pair whose instance is still gone stays held",
                DroppedLinkRows.TakeBack("sp4-B", id => false).Count == 0 && DroppedLinkRows.Count("sp4-B") == 1);
            check(P + S + "...and comes back later, when the instance does",
                DroppedLinkRows.TakeBack("sp4-B", id => id == 8).Count == 1);

            DroppedLinkRows.Hold("sp4-C", 1, new[] { Row("1|1|2") });
            DroppedLinkRows.Hold("sp4-C", 2, new[] { Row("1|2|2") });
            var only = DroppedLinkRows.TakeBack("sp4-C", id => id == 2);
            check(P + S + "only the instance that is back brings its pairs",
                only.Count == 1 && only[0].Key == "1|2|2" && DroppedLinkRows.Count("sp4-C") == 1);

            DroppedLinkRows.Hold("sp4-D", 3, new[] { Row("1|3|2") });
            check(P + S + "a lookup that throws is 'not back': the pair stays held",
                DroppedLinkRows.TakeBack("sp4-D", id => { throw new InvalidOperationException("lookup failed"); }).Count == 0
                && DroppedLinkRows.Count("sp4-D") == 1);

            DroppedLinkRows.Hold("sp4-E1", 1, new[] { Row("1|1|9") });
            check(P + S + "pairs are held per document: another document's instance brings nothing back",
                DroppedLinkRows.TakeBack("sp4-E2", id => true).Count == 0 && DroppedLinkRows.Count("sp4-E1") == 1);

            var twice = Row("1|4|2");
            DroppedLinkRows.Hold("sp4-F", 4, new[] { twice });
            DroppedLinkRows.Hold("sp4-F", 4, new[] { Row("1|4|2") });
            check(P + S + "a pair dropped twice is held once and comes back once",
                DroppedLinkRows.Count("sp4-F") == 1 && DroppedLinkRows.TakeBack("sp4-F", id => true).Count == 1);

            DroppedLinkRows.Hold("sp4-G", 5, new[] { Row("1|5|2") });
            int before = DroppedLinkRows.Count("sp4-G");
            DroppedLinkRows.Forget("sp4-G");
            check(P + S + "Forget lets go of a document's pairs (" + before + " before)",
                before == 1 && DroppedLinkRows.Count("sp4-G") == 0 && DroppedLinkRows.TakeBack("sp4-G", id => true).Count == 0);

            check(P + S + "the panel says how many came back (\"" + DroppedLinkRows.RelistedSentence(2) + "\")",
                DroppedLinkRows.RelistedSentence(2) == "2 pair(s) listed again as Unverified: their link came back.");

            foreach (var k in new[] { "sp4-A", "sp4-B", "sp4-C", "sp4-D", "sp4-E1", "sp4-E2", "sp4-F", "sp4-G" }) DroppedLinkRows.Forget(k);
        }
    }
}
