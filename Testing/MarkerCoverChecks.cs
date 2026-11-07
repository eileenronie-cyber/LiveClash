using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 2: checks for the slice 1 branches the coverage run showed untouched (null
    /// names and ids, the counter ceiling, formats passed as null). TESTINGS asked for them on 2026-10-31
    /// so every new Core file stays at 97% of branches or more. They test slice 1 code that already exists,
    /// so they pass in the red commit. Revit-free.
    /// </summary>
    public static class MarkerCoverChecks
    {
        private const string P = "markers cover: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Identity(check);
            Record(check);
            Text(check);
            Plan(check);
        }

        private static string[] A(params string[] items)
        {
            return items;
        }

        private static IssueNumbers.Counter C(string uid, string user, string prefix)
        {
            return new IssueNumbers.Counter { Uid = uid, UserName = user, Prefix = prefix };
        }

        private static void Identity(Action<string, bool?> check)
        {
            check(P + "a null base prefix is the empty prefix, and the first free variant of it", IssueNumbers.FreePrefix(null, null) == "" && IssueNumbers.FreePrefix(null, A("")) == "2");

            var top = new IssueNumbers.Counter { Prefix = "JD", Next = int.MaxValue };
            string a = IssueNumbers.Take(top), b = IssueNumbers.Take(top);
            check(P + "a counter at the ceiling does not wrap or throw", a == "JD-2147483647" && b == "JD-2147483647" && top.Next == int.MaxValue);

            var noUid = IssueNumbers.PrefixMoves(new[] { C(null, null, "JD"), C(null, null, "JD") });
            check(P + "two counters with no UniqueId and no user still resolve to one move", noUid.Count == 1 && noUid[0].NewPrefix == "JD2" && noUid[0].Uid == null);

            var counters = new[] { C(null, null, "JD"), C(null, null, "JD") };
            var markers = new[]
            {
                new IssueNumbers.IssueMarker { MarkerUid = "m1", IssueNo = "JD-0001", ReportedBy = "" },
                new IssueNumbers.IssueMarker { MarkerUid = "m2", IssueNo = "JD-0002", ReportedBy = null },
                new IssueNumbers.IssueMarker { MarkerUid = "m3", IssueNo = "JD-0003", ReportedBy = "jdoe" },
                new IssueNumbers.IssueMarker { MarkerUid = null, IssueNo = "JD-0004", ReportedBy = "" }
            };
            var plan = IssueNumbers.RenumberPlan(counters, markers);
            // Delta review 3 (DR3-L2), TESTINGS' decision: this line used to expect m1 to move to JD2-0001. It moved only because the two records with no UniqueId
            // shared one key (null and empty), so BOTH counters counted as moved. The first of two such records is the one that keeps the prefix, as the lower of two
            // real UniqueIds does, and a marker alone on its number does not move (compare the counters c1/c2 below). So the same input now gives an empty plan; the
            // case of the old line (a mover with no user name takes a marker reported by nobody) is pinned by the next line with a second marker on the number.
            check(P + "movers with no user name: one marker alone on its number stays, and markers with a null reporter or uid are skipped (an empty plan)",
                plan.Count == 0);

            // Delta review 4, N1: a marker with no uid also sits on JD-0001. Without the uid guard of RenumberPlan it sorts first (null before every uid), stays,
            // and m1 moves instead of m1b, so this line goes red; with the guard it is skipped.
            var twoOnOne = markers.Concat(new[]
            {
                new IssueNumbers.IssueMarker { MarkerUid = "m1b", IssueNo = "JD-0001", ReportedBy = "" },
                new IssueNumbers.IssueMarker { MarkerUid = null, IssueNo = "JD-0001", ReportedBy = "" }
            }).ToArray();
            var planTwo = IssueNumbers.RenumberPlan(counters, twoOnOne);
            check(P + "movers with no user name take a marker reported by nobody (empty) when two markers hold one number: the lower UniqueId stays, the other moves; markers with a null reporter or uid are skipped",
                planTwo.Count == 1 && planTwo[0].MarkerUid == "m1b" && planTwo[0].NewIssueNo == "JD2-0001");

            var cased = IssueNumbers.RenumberPlan(new[] { C("c1", "other", "JD"), C("c2", "JDoe", "JD") },
                new[] { new IssueNumbers.IssueMarker { MarkerUid = "m1", IssueNo = "JD-0007", ReportedBy = "jdoe" } });
            check(P + "the reporter is matched to the moved user without case", cased.Count == 1 && cased[0].NewIssueNo == "JD2-0007");

            check(P + "the prefix is read without case when matching a mover's markers",
                IssueNumbers.RenumberPlan(new[] { C("c1", "other", "jd"), C("c2", "jane", "JD") },
                    new[] { new IssueNumbers.IssueMarker { MarkerUid = "m1", IssueNo = "jd-0007", ReportedBy = "jane" } }).Count == 1);
        }

        private static void Record(Action<string, bool?> check)
        {
            var nullFields = MarkerFormats.Write(null);
            check(P + "writing no record writes the defaults (31 fields, status New, empty dates)",
                nullFields.Count == 31 && nullFields["Status"] == "New" && nullFields["ReportedAt"] == "" && nullFields["Seq"] == "0");

            var nullFormats = new MarkerReader((IMarkerFormat[])null);
            var withNull = new MarkerReader(new IMarkerFormat[] { null });
            var fields = MarkerFormats.Write(new MarkerRecord());
            check(P + "a reader built with no older formats, or a null one, still reads format 1",
                nullFormats.Read(MarkerFormats.Current, fields).Status == MarkerReadStatus.Ok && withNull.Read(MarkerFormats.Current, fields).Status == MarkerReadStatus.Ok);

            var impostor = new Impostor();
            var dup = new MarkerReader(impostor).Read(MarkerFormats.Format1, fields);
            check(P + "an older format that claims format 1's schema cannot replace the built-in reader", dup.Status == MarkerReadStatus.Ok && dup.FormatVersion == 1 && !impostor.Used);

            var noFields = MarkerReader.Default.Read(MarkerFormats.Current, null);
            check(P + "no fields at all is Junk with the version of the format that was asked for", noFields.Status == MarkerReadStatus.Junk && noFields.FormatVersion == 1 && noFields.Record == null);
        }

        private static MarkerPlan.Input Fresh()
        {
            return new MarkerPlan.Input
            {
                Options = new MarkerPlan.Options { Today = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8)), UserName = "jdoe" },
                Counter = new IssueNumbers.Counter { Uid = "c1", UserName = "jdoe", Prefix = "JD", Next = 1 }
            };
        }

        private static MarkerPlan.Existing Bare(string uid, string host, string other, Action<MarkerRecord> tweak = null)
        {
            var r = new MarkerRecord
            {
                Key = null, HostUid = host, LinkUid = null, OtherUid = other, ClashId = null, IssueNo = null, MergedIssueNos = null, AlsoSets = null,
                Note = null, AssignedTo = null, ReportedBy = null, ChangedBy = null, Severity = null, SetName = null, GroupLabel = null, ResolvedBy = null, LastCheckedBy = null
            };
            if (tweak != null) tweak(r);
            return new MarkerPlan.Existing { MarkerUid = uid, Record = r, Access = MarkerAccess.Free, ShownHash = null };
        }

        /// <summary>Markers and rows whose text fields are null (a record built by hand, not read from the model): the plan must cope and still make sense.</summary>
        private static void Plan(Action<string, bool?> check)
        {
            var input = Fresh();
            var keptBare = Bare("m1", "h1", "o1");
            var dupBare = Bare("m2", "h1", "o1", r => { r.Status = MarkerStatus.Ignored; });
            var listed = Bare("m3", "h3", "o3", r => { r.MergedIssueNos = new List<string> { null, "X-1" }; });
            input.Markers.AddRange(new[] { keptBare, dupBare, listed });
            input.Rows.Add(new MarkerPlan.Row { HostUid = "h1", OtherUid = "o1", Verdict = PlanVerdict.Clashing, ShownHash = null, Severity = "", SetName = null });
            MarkerPlan.Result res = null;
            bool threw = false;
            try { res = MarkerPlan.Build(input); } catch (Exception) { threw = true; }
            var change = res == null ? null : res.Changes.FirstOrDefault();
            check(P + "plan: markers with null text and null lists plan without throwing; a duplicate's decision still moves, with empty text for what is null",
                !threw && res.Merges == 1 && res.Removals.Count == 1 && change != null && change.After.Status == MarkerStatus.Ignored && change.After.Note == "" && change.After.ChangedBy == ""
                && res.Unchanged == 1);

            var unreadable = Fresh();
            unreadable.Markers.Add(new MarkerPlan.Existing { MarkerUid = "x", Record = null, Access = MarkerAccess.Free });
            var ur = MarkerPlan.Build(unreadable);
            check(P + "plan: a marker with no record, even if it looks free, is passed over as unreadable", ur.PassedOver.Count == 1 && ur.PassedOver[0].Reason == "unreadable record");

            var nokey = Fresh();
            nokey.Rows.Add(new MarkerPlan.Row { Key = null, HostUid = "h1", OtherUid = "o1", HasPoint = true, Verdict = PlanVerdict.Clashing });
            nokey.Rows.Add(new MarkerPlan.Row { Key = null, HostUid = "h2", OtherUid = "o2", HasPoint = true, Verdict = PlanVerdict.Clashing });
            var nk = MarkerPlan.Build(nokey);
            check(P + "plan: rows with no key are still created, in a fixed order, with an empty key", nk.Creations.Count == 2 && nk.Creations[0].Record.HostUid == "h1" && nk.Creations[0].Record.Key == "");

            var pend = Fresh();
            pend.Markers.Add(Bare("m1", "h1", "o1", r => { r.Seq = 3; }));
            pend.Pending.Add(new MarkerPlan.Pending { Triple = ClashIdOf.Triple("h1", "", "o1"), SeqAtPending = 3, Status = MarkerStatus.Ignored, By = null });
            var pr = MarkerPlan.Build(pend);
            check(P + "plan: a pending triage with no author is written under the refreshing user", pr.Changes.Count == 1 && pr.Changes[0].After.ChangedBy == "jdoe");

            var link = Fresh();
            var lm = Bare("m1", "h1", "o1", r => { r.LinkUid = "l1"; r.LinkId = 7; r.Severity = "Hard"; r.SetName = "S"; r.GroupLabel = ""; r.AlsoSets = new List<string>(); r.MergedIssueNos = new List<string>(); r.ClashId = "LC-1"; });
            link.Markers.Add(lm);
            link.Rows.Add(new MarkerPlan.Row { HostUid = "h1", LinkUid = "l1", OtherUid = "o1", LinkId = 8, Verdict = PlanVerdict.Clashing, Severity = "Hard", SetName = "S", GroupLabel = "", AlsoSets = new List<string>(), HasPoint = true });
            var lr = MarkerPlan.Build(link);
            check(P + "plan: a link pair whose link instance got a new ElementId is re-keyed on the link id alone", lr.Changes.Count == 1 && lr.Changes[0].Fields.SequenceEqual(A("LinkId")) && lr.Changes[0].After.LinkId == 8);

            var coll = Fresh();
            coll.Markers.Add(Bare("m1", "h1", "o1", r => { r.ClashId = "LC-deadbeef"; }));
            coll.Markers.Add(Bare("m2", "h2", "o2", r => { r.ClashId = "LC-deadbeef-2"; }));
            check(P + "plan: ids already carrying the right collision suffix are left alone", MarkerPlan.Build(coll).Changes.Count == 0);

            var off = Fresh();
            off.Markers.Add(Bare("m1", "h1", "o1", r => { r.ClashId = "LC-1"; }));
            off.Options.DisabledSets = null;
            var o1 = MarkerPlan.Build(off);
            off.Options.DisabledSets = new List<string> { null, "B" };
            var o2 = MarkerPlan.Build(off);
            check(P + "plan: no disabled-set list, or one with a null in it, is the same as none", o1.Unchanged == 1 && o2.Unchanged == 1);

            var seeded = Fresh();
            seeded.Rows.Add(new MarkerPlan.Row { Key = "a", HostUid = "h1", OtherUid = "o1", HasPoint = true, Verdict = PlanVerdict.Clashing, Seed = new MarkerPlan.Seed { Status = MarkerStatus.Resolved } });
            seeded.Rows.Add(new MarkerPlan.Row { Key = "b", HostUid = "h2", OtherUid = "o2", HasPoint = true, Verdict = PlanVerdict.Clashing, Seed = new MarkerPlan.Seed { Status = MarkerStatus.Resolved, By = "kdoe", At = seeded.Options.Today.AddDays(-3) } });
            var sd = MarkerPlan.Build(seeded);
            var s1 = sd.Creations.Count == 2 ? sd.Creations[0].Record : new MarkerRecord();
            var s2 = sd.Creations.Count == 2 ? sd.Creations[1].Record : new MarkerRecord();
            check(P + "plan: a seed that is already Resolved starts the marker Resolved, by the seed's author and time or by nobody, today",
                s1.Status == MarkerStatus.Resolved && s1.ResolvedBy == "" && s1.ResolvedAt == seeded.Options.Today && s1.Note == "" && s1.AssignedTo == "" && s1.ChangedBy == ""
                && s2.ResolvedBy == "kdoe" && s2.ResolvedAt == seeded.Options.Today.AddDays(-3));

            Func<string, string, string, MarkerPlan.Result> lost = (keptNote, dupNote, dupAssigned) =>
            {
                var input2 = Fresh();
                var k = Bare("m1", "h1", "o1", r => { r.Status = MarkerStatus.Ignored; r.Note = keptNote; r.AssignedTo = ""; });
                var d = Bare("m2", "h1", "o1", r => { r.Status = MarkerStatus.Ignored; r.Note = dupNote; r.AssignedTo = dupAssigned; });
                input2.Markers.AddRange(new[] { d, k });
                return MarkerPlan.Build(input2);
            };
            check(P + "plan: two decisions that differ only in the note, or only in who it is assigned to, are different decisions (one is listed as lost)",
                lost("a", "b", "").LostDecisions.Count == 1 && lost("a", "a", "asmith").LostDecisions.Count == 1 && lost("a", "a", "").LostDecisions.Count == 0);

            Func<double, double, double, int> moves = (dx, dy, dz) =>
            {
                var input3 = Fresh();
                var m = Bare("m1", "h1", "o1", r => { r.ClashId = "LC-1"; r.Severity = "Hard"; r.SetName = "S"; r.GroupLabel = ""; r.AlsoSets = new List<string>(); r.MergedIssueNos = new List<string>(); });
                input3.Markers.Add(m);
                input3.Rows.Add(new MarkerPlan.Row { HostUid = "h1", OtherUid = "o1", Verdict = PlanVerdict.Clashing, Severity = "Hard", SetName = "S", GroupLabel = "", AlsoSets = new List<string>(), HasPoint = true, X = dx, Y = dy, Z = dz });
                return MarkerPlan.Build(input3).Changes.Count(c => c.Moved);
            };
            check(P + "plan: a point that moves only in Y, or only in Z, is a move; one that moves less than the tolerance on every axis is not",
                moves(0, 0.01, 0) == 1 && moves(0, 0, 0.01) == 1 && moves(0.01, 0, 0) == 1 && moves(0.0004, 0.0004, 0.0004) == 0);

            var sameLink = Fresh();
            sameLink.Markers.Add(Bare("m1", "h1", "o1", r => { r.LinkUid = "l1"; r.LinkId = 7; r.ClashId = "LC-1"; r.Severity = "Hard"; r.SetName = "S"; r.GroupLabel = ""; r.AlsoSets = new List<string>(); r.MergedIssueNos = new List<string>(); }));
            sameLink.Rows.Add(new MarkerPlan.Row { HostUid = "h1", LinkUid = "l1", OtherUid = "o1", LinkId = 7, Verdict = PlanVerdict.Clashing, Severity = "Hard", SetName = "S", GroupLabel = "", AlsoSets = new List<string>(), HasPoint = true });
            check(P + "plan: a link pair whose link id is unchanged is left alone", MarkerPlan.Build(sameLink).Changes.Count == 0);

            var noUids = Fresh();
            noUids.Markers.Add(Bare("m9", null, null));
            check(P + "plan: a marker with no UniqueIds at all cannot be matched, merged or re-keyed, and is left as it is", MarkerPlan.Build(noUids).Unchanged == 1);

            var manual = new MarkerPlan.Result { PassedOver = { new MarkerPlan.Reasoned { MarkerUid = "a", Reason = null } }, ExistingMarkers = 1 };
            var counts = manual.ToCounts();
            check(P + "plan: a reason with no text is counted under an empty reason", counts.PassedOver.Count == 1 && counts.PassedOver[0].Text == "" && counts.PassedOver[0].Count == 1);
        }

        private sealed class Impostor : IMarkerFormat
        {
            public bool Used { get; private set; }
            public Guid Schema { get { return MarkerFormats.Format1; } }
            public int Version { get { return 0; } }
            public bool TryRead(IDictionary<string, string> fields, out MarkerRecord record) { Used = true; record = new MarkerRecord(); return true; }
        }

        private static void Text(Action<string, bool?> check)
        {
            var nullText = new MarkerSummary.Counts { Clashes = 3, NotRetested = { new MarkerSummary.Reason { Text = null, Count = 3 } } };
            check(P + "a reason with no text reads \"other\"", MarkerSummary.Describe(nullText) == "Clash markers: 3 clashes. 3 not re-tested (other).");

            var several = new MarkerSummary.Counts
            {
                Clashes = 5,
                NotRetested = { new MarkerSummary.Reason { Text = null, Count = 2 }, new MarkerSummary.Reason { Text = "a", Count = 2 }, new MarkerSummary.Reason { Text = "b", Count = 1 } }
            };
            check(P + "reasons with equal counts and a missing text sort without throwing",
                MarkerSummary.Describe(several) == "Clash markers: 5 clashes. 5 not re-tested (2 other, 2 a, 1 b)." || MarkerSummary.Describe(several) == "Clash markers: 5 clashes. 5 not re-tested (2 a, 2 other, 1 b).");

            var nothingToSay = new MarkerSummary.Counts { Clashes = 3 };
            check(P + "clashes with no outcome to report is just the first sentence", MarkerSummary.Describe(nothingToSay) == "Clash markers: 3 clashes.");

            check(P + "a null set name gives no line and a negative count never throws",
                MarkerSummary.SetLine(new MarkerSummary.SetCounts { Name = null }) == "" && MarkerSummary.NothingToDo(-1) == "Markers up to date (-1).");
        }
    }
}
