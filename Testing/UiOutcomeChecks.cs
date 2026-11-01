using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// What the panel says after Focus (review 2026-10-31, M7; rule C5: a
    /// summary says what actually happened). Revit-free, so both tiers run it. The in-Revit half -
    /// that Focus really rolls back and shows no dialog - is ReviewChecksUi.
    /// </summary>
    public static class UiOutcomeChecks
    {
        public static void RunAll(Action<string, bool?> check)
        {
            Focus(check);
            R8Checks(check);
        }

        // ------------------------------------------------------------------------------ round 8 (ui builder)

        private const string R8 = "r8 ui: ";

        private static void R8Checks(Action<string, bool?> check)
        {
            ActiveView(check);
            Refusals(check);
            Strikes(check);
            PassedOverText(check);
            CollectionLow12(check);
            StandingLow12(check);
            StripAndBanner(check);
            Burst(check);
        }

        // R2-2: sentences said close together are all on the status line, oldest first; nothing but a notice is ever joined.
        private static void Burst(Action<string, bool?> check)
        {
            var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            const string a = "Your settings file could not be copied.", b = "Your triage file could not be copied.", c = "Your resolved record could not be copied.";

            var burst = new NoticeBurst();
            burst.Add(a, t0);
            burst.Add(b, t0.AddMilliseconds(100));
            var three = burst.Add(c, t0.AddMilliseconds(200));
            check(R8 + "R2-2: three sentences in a row (a read-only folder) are all in the line, oldest first (\"" + three + "\")",
                three == a + "  " + b + "  " + c);

            var later = new NoticeBurst();
            later.Add(a, t0);
            check(R8 + "R2-2: ...a sentence more than a second after the previous one starts a new line",
                later.Add(b, t0.AddSeconds(2)) == b);

            var twice = new NoticeBurst();
            twice.Add(a, t0);
            check(R8 + "R2-2: ...the same sentence twice in a row is kept once", twice.Add(a, t0.AddMilliseconds(50)) == a);

            var many = new NoticeBurst();
            string last = null;
            for (int i = 1; i <= 10; i++) last = many.Add("sentence " + i + ".", t0.AddMilliseconds(i));
            check(R8 + "R2-2: ...at most " + NoticeBurst.Capacity + " are kept, the oldest dropped (\"" + last + "\")",
                last == string.Join("  ", Enumerable.Range(3, 8).Select(i => "sentence " + i + ".").ToArray()));

            var blank = new NoticeBurst();
            check(R8 + "R2-2: ...a blank sentence shows nothing", blank.Add("", t0) == null && blank.Add("  ", t0) == null && blank.Add(null, t0) == null);

            // Only notices are joined: a check result inside the second replaces the line, and ends the burst.
            var mixed = new NoticeBurst();
            mixed.Add(a, t0);
            const string result = "Full check: 3 clash(es) in 120 ms.";
            string shown = mixed.Other(result);
            string next = mixed.Add(b, t0.AddMilliseconds(300));
            check(R8 + "R2-2: ...a check result within a second of a notice replaces the line (only the result is shown), and a notice after it starts afresh",
                shown == result && next == b);
        }

        // DL-h: an active view that cannot be read is neither "switched" nor "not switched".
        private static void ActiveView(Action<string, bool?> check)
        {
            Func<long?, Func<long?>> reads = id => () => id;
            check(R8 + "DL-h: an active view that cannot be read is Unknown, not the focus view",
                FocusOutcome.Classify(() => { throw new InvalidOperationException("injected"); }, 5) == FocusOutcome.ActiveState.Unknown);
            check(R8 + "DL-h: no active view is Unknown too",
                FocusOutcome.Classify(reads(null), 5) == FocusOutcome.ActiveState.Unknown);
            check(R8 + "DL-h: the focus view being active is Focus",
                FocusOutcome.Classify(reads(5), 5) == FocusOutcome.ActiveState.Focus);
            check(R8 + "DL-h: another view being active is Other",
                FocusOutcome.Classify(reads(6), 5) == FocusOutcome.ActiveState.Other);

            const string own = "LiveClash — Clash Focus (jdoe)";
            var unknown = FocusOutcome.Describe(new FocusOutcome.Result { Committed = true, Selected = true, ActiveUnknown = true, ViewName = own });
            check(R8 + "DL-h: the message says it could not confirm the switch when the active view could not be read (\"" + unknown + "\")",
                unknown.StartsWith("Framed", StringComparison.Ordinal) && unknown.Contains("could not confirm"));
            check(R8 + "DL-h: ...and says where to open the view",
                unknown.Contains("Project Browser") && unknown.Contains("\"" + own + "\""));
            var unknownUnsel = FocusOutcome.Describe(new FocusOutcome.Result { Committed = true, Selected = false, ActiveUnknown = true, ViewName = own });
            check(R8 + "DL-h: ...and still says what was selected, and what could not be",
                unknown.Contains("Both sides are selected") && unknownUnsel.Contains("Not every side")
                && unknownUnsel.IndexOf("are selected", StringComparison.Ordinal) < 0);
            var refusedUnknown = FocusOutcome.Describe(new FocusOutcome.Result { Committed = false, ActiveUnknown = true });
            check(R8 + "DL-h: a refusal does not talk about the active view (\"" + refusedUnknown + "\")",
                refusedUnknown.IndexOf("confirm", StringComparison.Ordinal) < 0 && refusedUnknown.IndexOf("Framed", StringComparison.Ordinal) < 0);
            var log = FocusOutcome.UnreadableLog("InvalidOperationException", "the document is not active");
            check(R8 + "DL-h: the log line names the exception and never the view (\"" + log + "\")",
                log.Contains("InvalidOperationException") && log.Contains("the document is not active")
                && log.IndexOf("LiveClash", StringComparison.Ordinal) < 0 && log.IndexOf("jdoe", StringComparison.Ordinal) < 0);
        }

        // Focus on a view Revit refused to edit (second pass suspicion): passed over, per document.
        private static void Refusals(Action<string, bool?> check)
        {
            FocusRefusals.Forget("docA"); FocusRefusals.Forget("docB");
            try
            {
                FocusRefusals.Note("docA", 7);
                check(R8 + "focus non-editable: a view Revit refused is remembered as refused", FocusRefusals.IsRefused("docA", 7));
                check(R8 + "focus non-editable: ...per document: another document's same id is not refused",
                    FocusRefusals.IsRefused("docA", 7) && !FocusRefusals.IsRefused("docB", 7));
                check(R8 + "focus non-editable: ...an unrefused view is not refused", !FocusRefusals.IsRefused("docA", 8));
                bool before = FocusRefusals.IsRefused("docA", 7);
                FocusRefusals.Forget("docA");
                check(R8 + "focus non-editable: ...Forget lets go of a document's refusals", before && !FocusRefusals.IsRefused("docA", 7));
                FocusRefusals.Forget("docC");
                for (long i = 1; i <= 5; i++) FocusRefusals.Note("docC", 100 + i);
                check(R8 + "focus non-editable: ...at most " + FocusRefusals.MaxPerDocument + " views per document are passed over, so unrelated failures cannot fill the model with views",
                    FocusRefusals.IsRefused("docC", 101) && FocusRefusals.IsRefused("docC", 103) && !FocusRefusals.IsRefused("docC", 104) && !FocusRefusals.IsRefused("docC", 105));
            }
            finally { FocusRefusals.Forget("docA"); FocusRefusals.Forget("docB"); FocusRefusals.Forget("docC"); }
        }

        // The Focus regression (`focus: ...the box moved to the new clash`): a one-off refusal must not pass a view over; two in a row do.
        private static void Strikes(Action<string, bool?> check)
        {
            FocusRefusals.Forget("docS"); FocusRefusals.Forget("docT");
            try
            {
                bool first = FocusRefusals.NoteFailure("docS", 1);
                check(R8 + "focus non-editable: a single refusal does not pass a view over (it may be a one-off)", !first && !FocusRefusals.IsRefused("docS", 1));
                bool second = FocusRefusals.NoteFailure("docS", 1);
                check(R8 + "focus non-editable: ...a second refusal in a row of the same view does", second && FocusRefusals.IsRefused("docS", 1));

                FocusRefusals.Forget("docS");
                FocusRefusals.NoteFailure("docS", 1);
                FocusRefusals.NoteSuccess("docS", 1);
                bool again = FocusRefusals.NoteFailure("docS", 1);
                check(R8 + "focus non-editable: ...a success in between forgives the first (one-off errors never pin a view)", !again && !FocusRefusals.IsRefused("docS", 1));

                FocusRefusals.Forget("docS");
                FocusRefusals.NoteFailure("docS", 1);
                FocusRefusals.NoteFailure("docT", 1);
                FocusRefusals.NoteFailure("docS", 2);
                check(R8 + "focus non-editable: ...strikes are per document and per view (one each on two documents and two views pins none)",
                    !FocusRefusals.IsRefused("docS", 1) && !FocusRefusals.IsRefused("docT", 1) && !FocusRefusals.IsRefused("docS", 2));
            }
            finally { FocusRefusals.Forget("docS"); FocusRefusals.Forget("docT"); }
        }

        // The sentence on the refusal that passes a view over.
        private static void PassedOverText(Action<string, bool?> check)
        {
            const string sentence = "Next time Focus makes a new view.";
            var withText = FocusOutcome.Describe(new FocusOutcome.Result { Committed = false, Refused = { "Element is not up to date." }, PassedOver = true });
            var noText = FocusOutcome.Describe(new FocusOutcome.Result { Committed = false, PassedOver = true });
            check(R8 + "focus text: a refusal that passed the view over ends with the plain sentence that the next Focus makes a new view (\"" + withText + "\")",
                withText.EndsWith(sentence, StringComparison.Ordinal) && withText.Contains("Nothing was changed") && noText.EndsWith(sentence, StringComparison.Ordinal));
            var notPassed = FocusOutcome.Describe(new FocusOutcome.Result { Committed = false, Refused = { "Element is not up to date." } });
            var framed = FocusOutcome.Describe(new FocusOutcome.Result { Committed = true, Selected = true, PassedOver = true });
            check(R8 + "focus text: ...a refusal that did not pass the view over does not say it, and neither does a framed one",
                !notPassed.Contains("new view") && !framed.Contains("Next time Focus"));
        }

        // LOW-12: a collector that throws is a link that was not checked.
        private static void CollectionLow12(Action<string, bool?> check)
        {
            Func<object> boom = () => { throw new InvalidOperationException("injected collector failure"); };
            CollectionFailures.Reset();
            try
            {
                CollectionFailures.Begin();
                var got = CollectionFailures.Collect<object>("link 'A'", boom);
                var names = CollectionFailures.End();
                check(R8 + "LOW-12: a link collector that throws is recorded as not checked (" + string.Join(",", names.ToArray()) + ")",
                    got == null && names.Count == 1 && names[0] == "link 'A'");

                CollectionFailures.Reset();
                CollectionFailures.Begin();
                var fine = CollectionFailures.Collect<object>("link 'B'", () => new object());
                CollectionFailures.Collect<object>("link 'A'", boom);
                names = CollectionFailures.End();
                check(R8 + "LOW-12: ...the other links still collect, and are not named", fine != null && !names.Contains("link 'B'"));

                CollectionFailures.Reset();
                CollectionFailures.Begin();
                CollectionFailures.Collect<object>("link 'A'", boom);
                CollectionFailures.Collect<object>("link 'A'", boom);
                names = CollectionFailures.End();
                check(R8 + "LOW-12: ...the same link failing in two sets is named once (" + names.Count + ")", names.Count == 1);

                CollectionFailures.Reset();
                CollectionFailures.Collect<object>("link 'A'", boom);   // no check is recording
                CollectionFailures.Begin();
                names = CollectionFailures.End();
                check(R8 + "LOW-12: ...a failure outside a check window is not recorded in that check", names.Count == 0);

                CollectionFailures.Reset();
                CollectionFailures.Begin();
                CollectionFailures.Collect<object>("link 'A'", () => new object());
                names = CollectionFailures.End();
                check(R8 + "LOW-12: ...nothing failed: no note, so a clean full check reads as before",
                    names.Count == 0 && CheckOutcome.NotCollectedNote(names) == "" && CheckOutcome.NotCollectedNote(null) == ""
                    && CheckOutcome.RecheckMessage(3, 10, 2, 0, 0, 0, 0, names) == CheckOutcome.RecheckMessage(3, 10, 2, 0, 0));

                var note = CheckOutcome.NotCollectedNote(new List<string> { "link 'A'" });
                check(R8 + "LOW-12: ...the note says the link was not checked and that the result is not clean (\"" + note + "\")",
                    note.Contains("link 'A'") && note.Contains("Not checked")
                    && note.Contains("not a clean result"));

                var msg = CheckOutcome.RecheckMessage(3, 10, 2, 0, 0, 0, 0, new List<string> { "link 'A'" });
                check(R8 + "LOW-12: ...the recheck message carries the note (\"" + msg + "\")",
                    msg.Contains("link 'A'") && msg.Contains("Not checked"));
            }
            finally { CollectionFailures.Reset(); }
        }

        // LOW-12 on the live path: each live context keeps the labels its own build could not read; the strip shows the union over the
        // contexts that exist. A failure belongs to the context that left the link out (round 8 delta review, L-2).
        private static void StandingLow12(Action<string, bool?> check)
        {
            Func<object> boom = () => { throw new InvalidOperationException("injected collector failure"); };
            CollectionFailures.Reset();
            try
            {
                var contextA = new List<string>();
                CollectionFailures.Collect<object>("link 'A'", boom, contextA);
                check(R8 + "LOW-12 live: a failure in a context's own build is kept as that context's unread (" + string.Join(",", contextA.ToArray()) + ")",
                    contextA.Count == 1 && contextA[0] == "link 'A'");

                var rebuilt = new List<string>();
                CollectionFailures.Collect<object>("link 'A'", () => new object(), rebuilt);
                check(R8 + "LOW-12 live: ...and a context rebuilt without it shows nothing",
                    contextA.Count == 1 && rebuilt.Count == 0 && CollectionFailures.Union(new IList<string>[] { rebuilt }).Count == 0);

                var note = CheckOutcome.StandingNote(new List<string> { "link 'A'" });
                check(R8 + "LOW-12 live: ...the standing note says live checking against it is off until it reads again (\"" + note + "\")",
                    note.Contains("link 'A'") && note.Contains("could not be read")
                    && note.Contains("live checking against it is off until it reads again"));
                check(R8 + "LOW-12 live: nothing standing: no note",
                    CheckOutcome.StandingNote(CollectionFailures.Union(new IList<string>[] { new List<string>(), new List<string>() })) == ""
                    && CheckOutcome.StandingNote(null) == "");
            }
            finally { CollectionFailures.Reset(); }

            UnionAndExpiry(check);
            UndeliveredLow(check);
        }

        // L-2: a failure still shows when another context built fine; named once; and a context that left a link out is asked again soon.
        private static void UnionAndExpiry(Action<string, bool?> check)
        {
            var failing = new List<string> { "link 'X'" };
            var fine = new List<string>();
            check(R8 + "L-2: a failure in one context still shows when another context built fine",
                string.Join("|", CollectionFailures.Union(new IList<string>[] { failing, fine }).ToArray()) == "link 'X'"
                && string.Join("|", CollectionFailures.Union(new IList<string>[] { fine, failing }).ToArray()) == "link 'X'");
            check(R8 + "L-2: ...the same link unread in two contexts is named once, in order",
                string.Join("|", CollectionFailures.Union(new IList<string>[] { failing, new List<string> { "link 'X'", "link 'Y'" }, null }).ToArray())
                == "link 'X'|link 'Y'");

            var ten = TimeSpan.FromMinutes(10);
            check(R8 + "L-2: a context that left a link out is asked again after a minute, not ten",
                CollectionFailures.ExpiryFor(2, ten) == CollectionFailures.RetryAfter && CollectionFailures.RetryAfter < ten);
            check(R8 + "L-2: ...a context that left nothing out keeps its expiry, and a shorter one is kept",
                CollectionFailures.ExpiryFor(0, ten) == ten && CollectionFailures.ExpiryFor(3, TimeSpan.FromSeconds(30)) == TimeSpan.FromSeconds(30));
        }

        // L-4: what was said while nobody listened is shown together, oldest first, when the panel appears.
        private static void UndeliveredLow(Action<string, bool?> check)
        {
            check(R8 + "L-4: undelivered sentences are shown together in the status line, oldest first",
                CheckOutcome.JoinNotices(new List<string> { "first sentence.", "second sentence." }) == "first sentence.  second sentence.");
            check(R8 + "L-4: ...none (null or empty) shows nothing",
                CheckOutcome.JoinNotices(null) == "" && CheckOutcome.JoinNotices(new List<string>()) == "");
            check(R8 + "L-4: ...a blank or null sentence is skipped",
                CheckOutcome.JoinNotices(new List<string> { "a", " ", null, "b" }) == "a  b");
        }

        // The panel strip's text is CheckOutcome.StandingNote(<the unread labels of the live contexts>) and the banner is chosen by
        // CheckOutcome.HasGaps(notCollected): what the panel draws is only as right as these two (the pictures are in R8PanelChecks).
        private static void StripAndBanner(Action<string, bool?> check)
        {
            Func<object> boom = () => { throw new InvalidOperationException("injected collector failure"); };
            CollectionFailures.Reset();
            try
            {
                var context = new List<string>();
                CollectionFailures.Collect<object>("link 'Strip A'", boom, context);
                var text = CheckOutcome.StandingNote(CollectionFailures.Union(new IList<string>[] { context }));
                check(R8 + "strip: the strip's text is the unread link, named, and says live checking is off (\"" + text + "\")",
                    text.Contains("link 'Strip A'") && text.Contains("live checking against it is off until it reads again"));
                var rebuilt = new List<string>();
                CollectionFailures.Collect<object>("link 'Strip A'", () => new object(), rebuilt);
                check(R8 + "strip: ...and it is empty again once the context is rebuilt and the link reads",
                    text != "" && CheckOutcome.StandingNote(CollectionFailures.Union(new IList<string>[] { rebuilt })) == "");
            }
            finally { CollectionFailures.Reset(); }

            check(R8 + "banner: gaps are reported only when a link was not collected (names present)",
                CheckOutcome.HasGaps(new List<string> { "link 'A'" }) && CheckOutcome.HasGaps(new List<string> { "the host model", "link 'B'" }));
            check(R8 + "banner: ...and not when nothing failed (empty list, null)",
                !CheckOutcome.HasGaps(new List<string>()) && !CheckOutcome.HasGaps(null));
        }

        // ------------------------------------------------------------------------------ Focus

        private static void Focus(Action<string, bool?> check)
        {
            Func<FocusOutcome.Result, string> say = FocusOutcome.Describe;

            var refused = say(new FocusOutcome.Result
            {
                Committed = false,
                Refused = { "Element is owned by jdoe and cannot be edited." }
            });
            check("focus text: a change Revit refused does not say Framed (\"" + refused + "\")",
                refused.IndexOf("Framed", StringComparison.Ordinal) < 0);
            check("focus text: ...names what Revit refused", refused.Contains("owned by jdoe"));
            check("focus text: ...and says nothing was changed", refused.Contains("Nothing was changed"));

            var two = say(new FocusOutcome.Result
            {
                Committed = false,
                Refused = { "First error.", "Second error." }
            });
            check("focus text: two refusals name the first and count the rest (\"" + two + "\")",
                two.Contains("First error") && two.Contains("1 more") && two.IndexOf("Second error", StringComparison.Ordinal) < 0);

            var rolled = say(new FocusOutcome.Result { Committed = false });
            check("focus text: a rollback with no error text still says nothing was changed (\"" + rolled + "\")",
                rolled.IndexOf("Framed", StringComparison.Ordinal) < 0 && rolled.Contains("Nothing was changed"));

            var threw = say(new FocusOutcome.Result { Failure = "injected", LogPath = @"C:\log\LiveClash.log" });
            check("focus text: a throw names what went wrong, says nothing was changed and where the log is (\"" + threw + "\")",
                threw.Contains("injected") && threw.Contains("Nothing was changed") && threw.Contains(@"C:\log\LiveClash.log")
                && threw.IndexOf("Framed", StringComparison.Ordinal) < 0);

            var late = say(new FocusOutcome.Result { Failure = "late", LogPath = "L", Committed = true });
            check("focus text: a throw after the commit does not claim nothing was changed (\"" + late + "\")",
                late.Contains("late") && late.IndexOf("Nothing was changed", StringComparison.Ordinal) < 0);

            var framed = say(new FocusOutcome.Result { Committed = true, AnyLink = true, Selected = true });
            check("focus text: a committed focus says Framed, in the focus view (\"" + framed + "\")",
                framed.StartsWith("Framed", StringComparison.Ordinal) && framed.Contains(FocusOutcome.ViewName));
            check("focus text: ...says link geometry is included when a side is in a link", framed.Contains("link geometry included"));
            check("focus text: ...and that both sides are selected when they were", framed.Contains("Both sides are selected"));

            var unselected = say(new FocusOutcome.Result { Committed = true, Selected = false });
            check("focus text: it does not claim a selection it could not make (\"" + unselected + "\")",
                unselected.StartsWith("Framed", StringComparison.Ordinal)
                && unselected.IndexOf("are selected", StringComparison.Ordinal) < 0
                && unselected.IndexOf("link geometry", StringComparison.Ordinal) < 0);


            // DL5 (delta review): the view is "<plain name> (user)" in a workshared model, so the message names
            // the view it was given, not the plain name.
            const string mine = "LiveClash — Clash Focus (jdoe)";
            var named = say(new FocusOutcome.Result { Committed = true, Selected = true, ViewName = mine });
            check("focus text: a framed message names the view it was given (\"" + named + "\")", named.Contains("\"" + mine + "\""));
            check("focus text: ...and not the plain name when the view has another",
                named.IndexOf("\"" + FocusOutcome.ViewName + "\"", StringComparison.Ordinal) < 0);
            var unsel = say(new FocusOutcome.Result { Committed = true, Selected = false, ViewName = mine });
            check("focus text: a message about a selection it could not make names that view too (\"" + unsel + "\")",
                unsel.Contains("\"" + mine + "\""));
            var lateNamed = say(new FocusOutcome.Result { Failure = "late", LogPath = "L", Committed = true, ViewName = mine });
            check("focus text: a throw after the commit names that view too (\"" + lateNamed + "\")", lateNamed.Contains("\"" + mine + "\""));
            var plain = say(new FocusOutcome.Result { Committed = true, Selected = true });
            check("focus text: no view name given: the plain name, as before", plain.Contains("\"" + FocusOutcome.ViewName + "\""));
            var refusedNamed = say(new FocusOutcome.Result { Committed = false, ViewName = mine });
            check("focus text: a refusal does not name a view (nothing was framed)", refusedNamed.IndexOf(mine, StringComparison.Ordinal) < 0);

            // DL5b: Focus says "Framed" only for a view the user is actually in; otherwise it says what to open.
            const string own = "LiveClash — Clash Focus (jdoe)";
            var stuck = say(new FocusOutcome.Result { Committed = true, Selected = true, NotSwitched = true, ViewName = own });
            check("focus text: a view that was not switched to says so (\"" + stuck + "\")", stuck.Contains("did not switch"));
            check("focus text: ...and says where to open it", stuck.Contains("Project Browser") && stuck.Contains("\"" + own + "\""));
            check("focus text: ...naming the per-user view, not the plain name",
                stuck.IndexOf("\"" + FocusOutcome.ViewName + "\"", StringComparison.Ordinal) < 0 && stuck.Contains(own));
            var stuckUnsel = say(new FocusOutcome.Result { Committed = true, Selected = false, NotSwitched = true, ViewName = own });
            check("focus text: ...and still says what could not be selected (\"" + stuckUnsel + "\")", stuckUnsel.Contains("Not every side"));
            var switched = say(new FocusOutcome.Result { Committed = true, Selected = true, ViewName = own });
            check("focus text: a view that was switched to does not say it was not (\"" + switched + "\")",
                switched.IndexOf("did not switch", StringComparison.Ordinal) < 0);
            var refusedStuck = say(new FocusOutcome.Result { Committed = false, NotSwitched = true });
            check("focus text: a refusal does not talk about switching",
                refusedStuck.IndexOf("switch", StringComparison.Ordinal) < 0);
            var threwStuck = say(new FocusOutcome.Result { Failure = "boom", LogPath = "L", Committed = false, NotSwitched = true });
            check("focus text: a throw before the commit does not talk about switching",
                threwStuck.IndexOf("switch", StringComparison.Ordinal) < 0);
            var logLine = FocusOutcome.SwitchLog("InvalidOperationException", "the document is not active");
            check("focus text: the log line names the exception the setter threw (\"" + logLine + "\")",
                logLine.Contains("InvalidOperationException") && logLine.Contains("the document is not active"));
            var quiet = FocusOutcome.SwitchLog(null, null);
            check("focus text: ...says so when the setter threw nothing and the view still did not change (\"" + quiet + "\")",
                quiet.Contains("threw nothing") && quiet.Contains("did not change"));
            check("focus text: ...and never names the view (a user name must not reach the log)",
                logLine.IndexOf("LiveClash", StringComparison.Ordinal) < 0 && quiet.IndexOf("LiveClash", StringComparison.Ordinal) < 0
                && logLine.IndexOf("jdoe", StringComparison.Ordinal) < 0);
            check("focus text: no result says nothing at all", FocusOutcome.Describe(null) == "");
        }
    }
}
