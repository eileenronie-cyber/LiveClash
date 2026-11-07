using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, mutation batch 9: the dialogs' models, the record reader and writer, the reconcile rules and the small files, whose defaults,
    /// texts and edge cases the second Stryker run showed no check pinned down. Revit-free, so both tiers run it.
    /// Predictions and results: tests/evidence/clash-schedule/MUTATION-LOG.md.
    /// </summary>
    public static class MarkerMutationModelChecks
    {
        private const string P = "markers mutation models: ";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        private static readonly string[] OneIssue = { "JD-0001" };

        public static void RunAll(Action<string, bool?> check)
        {
            Confirm(check);
            Assign(check);
            PlanModel(check);
            Record(check);
            Reader(check);
            Reconcile(check);
            Small(check);
            RowBuild(check);
        }

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }
        private static string[] Texts(params string[] items) { return items; }

        // ------------------------------------------------------------------ the confirmations

        private static void Confirm(Action<string, bool?> check)
        {
            var fresh = new ConfirmModel();
            check(P + "a confirmation nobody built starts with empty texts, Cancel as its cancel text and no lines, unticked",
                fresh.Title == "" && fresh.CheckText == "" && fresh.OkText == "" && fresh.CancelText == "Cancel" && fresh.Lines.Count == 0 && !fresh.Ticked && !fresh.CanOk);

            const string Held = "Markers held by another user are passed over.";
            const string Undo = "One Undo. Your local record keeps every decision.";
            check(P + "remove resolved: one and several markers, and none (which can never be accepted)",
                ConfirmModel.RemoveResolved(1).Lines.SequenceEqual(Texts("1 resolved marker will be deleted from the model.", Held, Undo))
                && ConfirmModel.RemoveResolved(2000).Lines.SequenceEqual(Texts("2,000 resolved markers will be deleted from the model.", Held, Undo))
                && ConfirmModel.RemoveResolved(0).Lines.SequenceEqual(Texts("No markers are resolved.")));
            var none = ConfirmModel.RemoveAll(0);
            none.Ticked = true;
            const string Rest = " the schedule, the marker type, the settings record and the counter records will be deleted.";
            const string Params = "The LiveClash parameters and workset stay. Remove the parameters separately if you no longer need them.";
            check(P + "remove all: none can never be accepted; one and several markers say what goes",
                none.Lines.SequenceEqual(Texts("There are no markers to remove.")) && !none.CanOk
                && ConfirmModel.RemoveAll(1).Lines.SequenceEqual(Texts("The 1 marker," + Rest, Params, Held, Undo))
                && ConfirmModel.RemoveAll(1234).Lines.SequenceEqual(Texts("All 1,234 markers," + Rest, Params, Held, Undo)));
            check(P + "remove parameters and create without worksets: their lines, in order",
                ConfirmModel.RemoveParameters().Lines.SequenceEqual(Texts("No clash marker is left in the model.", "Removing the shared parameters deletes their values everywhere in this model, including in other schedules that use them.", "One Undo."))
                && ConfirmModel.CreateNonWorkshared().Lines.SequenceEqual(Texts("This model is not workshared, so the markers cannot go on a hidden workset.", "They show in every view where Specialty Equipment shows, and they print.")));
        }

        // ------------------------------------------------------------------ the assign dialog

        private static void Assign(Action<string, bool?> check)
        {
            check(P + "an option and a result nobody filled have empty names and no flags", new AssignOption().Name == "" && new AssignOption().Reason == "" && new AssignResult().User == "" && !new AssignResult().Unassign && !new AssignResult().NotSeen);
            var candidates = new[]
            {
                new AssignCandidate { Name = "asmith", Reason = "seen" }, new AssignCandidate { Name = "ASMITH", Reason = "" }, new AssignCandidate { Name = "asmith", Reason = "again" }
            };
            var model = new AssignModel(OneIssue, candidates, null, null);
            check(P + "the reasons of one name are joined with '; ', an empty reason adds nothing", model.Visible.Count == 1 && model.Visible[0].Reason == "seen; again");
            check(P + "nothing is selected at first; the search is kept trimmed", model.SelectedName == "" && model.Mode == AssignMode.None);
            model.Search = "  asm ";
            check(P + "...and the search text is kept trimmed", model.Search == "asm");

            model.Search = "";
            model.SelectCandidate("asmith");
            string afterCandidate = model.SelectedName;
            model.OtherName = "someone new";
            string afterOther = model.SelectedName;
            model.SelectCandidate("asmith");
            model.SelectUnassign();
            string afterUnassign = model.SelectedName;
            model.SelectCandidate("asmith");
            model.SelectOther();
            string afterSelectOther = model.SelectedName;
            check(P + "picking a name, typing another, unassigning or choosing 'other' each clears the picked name", afterCandidate == "asmith" && afterOther == "" && afterUnassign == "" && afterSelectOther == "");
        }

        // ------------------------------------------------------------------ the Refresh plan dialog

        private static void PlanModel(Action<string, bool?> check)
        {
            var none = new RefreshPlanModel(null);
            check(P + "no plan at all: the Refresh title, one Undo, no refusal and no notes", none.Title == "Refresh clash markers" && none.Footer == "One Undo." && none.Refusal == "" && none.CapMessage == "" && none.Warning == "" && none.NothingNote == "");
            check(P + "a line nobody filled has an empty label and detail", new PlanLine().Label == "" && new PlanLine().Detail == "");

            Func<string, string, string> detail = (first, last) =>
            {
                var model = new RefreshPlanModel(new RefreshPlanView { Create = 3, IsCreate = true, FirstIssueNo = first, LastIssueNo = last });
                return model.Lines.Single(l => l.Label == "Create").Detail;
            };
            check(P + "the create line names the numbers: a range, one number whichever end is missing, or none",
                detail("JD-0001", "JD-0003") == "new clashes (JD-0001 to JD-0003)" && detail("JD-0001", "JD-0001") == "new clashes (JD-0001)" && detail("JD-0001", "") == "new clashes (JD-0001)"
                && detail("", "JD-0003") == "new clashes (JD-0003)" && detail("", "") == "new clashes");
        }

        // ------------------------------------------------------------------ the record: defaults, written text, read rules

        private static void Record(Action<string, bool?> check)
        {
            var r = new MarkerRecord();
            check(P + "a marker record nobody filled has empty texts, no times and no list entries",
                r.Key == "" && r.HostUid == "" && r.LinkUid == "" && r.OtherUid == "" && r.ClashId == "" && r.IssueNo == "" && r.Note == "" && r.AssignedTo == "" && r.ReportedBy == "" && r.ChangedBy == "" && r.Severity == ""
                && r.SetName == "" && r.GroupLabel == "" && r.ResolvedBy == "" && r.LastCheckedBy == "" && r.ReportedAt == null && r.MergedIssueNos.Count == 0 && r.AlsoSets.Count == 0 && new MarkerReadResult().Message == "");

            var w = MarkerFormats.Write(new MarkerRecord { Key = null, ClearanceMm = 25.5, X = 1.25, Insulated = true, Seq = 7, ReportedAt = null, ChangedAt = new DateTimeOffset(T(1)) });
            var w2 = MarkerFormats.Write(new MarkerRecord { Insulated = false });
            check(P + "written text: a missing text and a missing time are empty, numbers print whole and in the invariant culture, a flag is 1 or 0, a time prints as round-trip text",
                w["Key"] == "" && w["ReportedAt"] == "" && w["ClearanceMm"] == "25.5" && w["X"] == "1.25" && w["Insulated"] == "1" && w2["Insulated"] == "0" && w["Seq"] == "7" && w["ChangedAt"] == new DateTimeOffset(T(1)).ToString("o", Inv));
        }

        private static Dictionary<string, string> Good()
        {
            return MarkerFormats.Write(new MarkerRecord { Key = "k", HostUid = "h", OtherUid = "o", Status = MarkerStatus.Pending, ClearanceMm = 25, Seq = 3, ReportedAt = new DateTimeOffset(T(1)) });
        }

        private static void Reader(Action<string, bool?> check)
        {
            var format = new MarkerFormat1();
            MarkerRecord rec;
            Func<Action<Dictionary<string, string>>, bool> reads = edit => { var f = Good(); edit(f); MarkerRecord got; bool ok = format.TryRead(f, out got); return ok && got != null; };
            Func<Action<Dictionary<string, string>>, bool> refuses = edit => { var f = Good(); edit(f); MarkerRecord got; bool ok = format.TryRead(f, out got); return !ok && got == null; };
            check(P + "a record is read, and each field that is present but unreadable (status, the four ids, the four numbers, the four times, the flag) makes it unreadable",
                reads(f => { }) && refuses(f => f["Status"] = "nope") && refuses(f => f["HostId"] = "x") && refuses(f => f["LinkId"] = "x") && refuses(f => f["OtherId"] = "x") && refuses(f => f["Seq"] = "x")
                && refuses(f => f["ClearanceMm"] = "x") && refuses(f => f["X"] = "x") && refuses(f => f["Y"] = "x") && refuses(f => f["Z"] = "x")
                && refuses(f => f["ReportedAt"] = "x") && refuses(f => f["ChangedAt"] = "x") && refuses(f => f["ResolvedAt"] = "x") && refuses(f => f["LastCheckedAt"] = "x") && refuses(f => f["Insulated"] = "maybe"));
            Func<string, bool?> insulated = text =>
            {
                var f = Good(); f["Insulated"] = text;
                MarkerRecord got;
                return format.TryRead(f, out got) ? (bool?)got.Insulated : null;
            };
            check(P + "the flag reads 1 and true (any case) as set, and 0, false, FALSE and nothing as not set",
                insulated("1") == true && insulated("true") == true && insulated("TRUE") == true && insulated("0") == false && insulated("false") == false && insulated("FALSE") == false && insulated("") == false && insulated("maybe") == null);
            var nullValue = Good(); nullValue["Key"] = null; nullValue["Note"] = null;
            check(P + "a field whose value is missing reads as an empty text", format.TryRead(nullValue, out rec) && rec.Key == "" && rec.Note == "");

            var reader = MarkerReader.Default;
            var other = Guid.NewGuid();
            check(P + "an unknown format says it was written by a newer LiveClash, naming the format",
                reader.Read(other, Good()).Message == "Written by a newer LiveClash (record format " + other + "); update to change it." && reader.Read(other, Good()).Status == MarkerReadStatus.NewerFormat);
            check(P + "no fields, or fields that cannot be read, say so in these words",
                reader.Read(MarkerFormats.Format1, null).Message == "No fields." && reader.Read(MarkerFormats.Format1, new Dictionary<string, string> { { "Status", "nope" } }).Message == "The record could not be read.");
            var throwing = new Throwing();
            check(P + "a format that throws is unreadable and names the exception", new MarkerReader(throwing).Read(throwing.Schema, Good()).Message == "The record could not be read: InvalidOperationException");
        }

        private sealed class Throwing : IMarkerFormat
        {
            public Guid Schema { get; } = new Guid("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f");
            public int Version { get { return 1; } }
            public bool TryRead(IDictionary<string, string> fields, out MarkerRecord record) { throw new InvalidOperationException("boom"); }
        }

        // ------------------------------------------------------------------ marker to ledger

        private static MarkerReconcile.ModelMarker M(string key, MarkerStatus status, string note = null, string assigned = null, string changedBy = null, string reportedBy = null, string issue = null)
        {
            return new MarkerReconcile.ModelMarker { Key = key, Status = status, Note = note, AssignedTo = assigned, ChangedBy = changedBy, ReportedBy = reportedBy, IssueNo = issue };
        }

        private static string[] Fields(string line) { return line.Split((char)9); }

        private static void Reconcile(Action<string, bool?> check)
        {
            var two = MarkerReconcile.Plan(new[] { M("b", MarkerStatus.Ignored, "n"), M("a", MarkerStatus.Ignored, "n") }, null, null, T(10));
            check(P + "reconcile: the lines come in the order of the keys, each decision followed by its model note", two.Lines.Select(l => Fields(l)[0]).SequenceEqual(Texts("a", "a", "b", "b")) && Fields(two.Lines[1])[1] == "Reconciled");
            var nobody = MarkerReconcile.Plan(new[] { M("a", MarkerStatus.Pending, null, null, "", null) }, null, null, T(10));
            check(P + "...a marker with no author and no note gives a decision line with an empty author and an empty note", Fields(nobody.Lines[0]).Length == 5 && Fields(nobody.Lines[0])[3] == "" && Fields(nobody.Lines[0])[4] == "");

            var resolved = MarkerReconcile.Plan(new[] { M("a", MarkerStatus.Resolved, null, null, "kdoe") }, new Dictionary<string, ClashStatusEntry> { { "a", new ClashStatusEntry { Kind = ClashStatusKind.Ignored, WhenUtc = T(1), By = "u", Note = "x" } } }, null, T(10));
            var gone = Fields(resolved.Lines[0]);
            check(P + "...a resolved marker over a live decision gives today's five-field Gone line: key, event, time, author and an empty last field",
                gone.Length == 5 && gone[0] == "a" && gone[1] == "Gone" && gone[2] == T(10).ToString("o", Inv) && gone[3] == "kdoe" && gone[4] == "" && Fields(resolved.Lines[1])[4] == "model Gone");

            var back = new ClashStatusEntry { Kind = ClashStatusKind.Pending, WhenUtc = T(5), By = "u", Note = "", Gone = true, GoneUtc = T(20) };
            var reopened = MarkerReconcile.Plan(new[] { M("a", MarkerStatus.Reopened, "") }, new Dictionary<string, ClashStatusEntry> { { "a", back } }, null, T(10));
            check(P + "a reopened marker over a decision that went Gone is timed one tick after the Gone", Fields(reopened.Lines[0])[2] == T(20).AddTicks(1).ToString("o", Inv));
            var odd = new ClashStatusEntry { Kind = ClashStatusKind.Ignored, WhenUtc = T(5), By = "u", Note = "x", Gone = false, GoneUtc = T(50) };
            var differs = MarkerReconcile.Plan(new[] { M("a", MarkerStatus.Ignored, "y") }, new Dictionary<string, ClashStatusEntry> { { "a", odd } }, null, T(10));
            check(P + "...but a Gone time on a decision that is not Gone does not count", Fields(differs.Lines[0])[2] == T(10).ToString("o", Inv));
            var late = new ClashStatusEntry { Kind = ClashStatusKind.Ignored, WhenUtc = T(30), By = "u", Note = "x", Gone = true, GoneUtc = T(20) };
            var older = MarkerReconcile.Plan(new[] { M("a", MarkerStatus.Ignored, "y") }, new Dictionary<string, ClashStatusEntry> { { "a", late } }, null, T(10));
            check(P + "...and a decision made after it went Gone counts from its own time", Fields(older.Lines[0])[2] == T(30).AddTicks(1).ToString("o", Inv));
        }

        // ------------------------------------------------------------------ the small files

        private static void Small(Action<string, bool?> check)
        {
            var all = new List<ClashResult>();
            var index = new Dictionary<string, int>();
            var first = new ClashResult { Key = "k", SetName = "x", Severity = ClashSeverity.Hard, AlsoSets = new List<string> { "Z" } };
            ClashDedupe.Add(all, index, first, "Set A");
            check(P + "a first row is stamped with its set and keeps the other sets it already listed", all.Count == 1 && all[0].SetName == "Set A" && all[0].AlsoSets != null && all[0].AlsoSets.SequenceEqual(Texts("Z")));
            var same = new ClashResult { Key = "k", Severity = ClashSeverity.Hard };
            ClashDedupe.Add(all, index, same, "Set A");
            check(P + "...a second row from the same set adds no 'also caught by' name, and leaves none rather than an empty list", all.Count == 1 && all[0].AlsoSets != null && all[0].AlsoSets.SequenceEqual(Texts("Z")));
            var lone = new List<ClashResult>();
            var loneIndex = new Dictionary<string, int>();
            ClashDedupe.Add(lone, loneIndex, new ClashResult { Key = "q", Severity = ClashSeverity.Hard }, "Set A");
            ClashDedupe.Add(lone, loneIndex, new ClashResult { Key = "q", Severity = ClashSeverity.Hard }, "Set A");
            check(P + "...nothing to name: the list is null, not empty", lone[0].AlsoSets == null);

            var writes = new[] { "a", "b" };
            var split = MarkerWriteFilter.Apply(writes, null, new HashSet<string> { "a" });
            check(P + "with no way to name an existing marker every write is kept", split.Kept.SequenceEqual(writes) && split.Dropped.Count == 0);

            check(P + "the newer-format note names one marker, or a count with thousands separated", MarkerRefusals.NewerFormatNote(1) == "1 marker was written by a newer LiveClash; update to change it."
                && MarkerRefusals.NewerFormatNote(1234) == "1,234 markers were written by a newer LiveClash; update to change them." && MarkerRefusals.NewerFormatNote(0) == "");

            var values = MarkerParameterMap.Build(new MarkerRecord { AlsoSets = null, IssueNo = null, ClashId = null, Severity = null, SetName = null, GroupLabel = null, AssignedTo = null, ReportedBy = null, ChangedBy = null, Note = null },
                new Dictionary<string, string> { { "LC A Name", null }, { "LC Level", "L1" } });
            check(P + "the parameter text of a record with no texts is empty everywhere, a missing display value too, and no value is null",
                values.Values.All(v => v != null) && values["LC Also Caught By"] == "" && values["LC Issue No"] == "" && values["LC Severity"] == "" && values["LC A Name"] == "" && values["LC Level"] == "L1" && values["LC Note"] == "");

            var menu = ScheduleMenuModel.Build(new ScheduleSnapshot { Ready = true, Me = "jdoe", HasSchedule = true, MarkerCount = 3, ResolvedCount = 1, RefusalReason = "   " });
            check(P + "a menu whose snapshot has only blank space for a refusal reason gives its refresh item no reason", menu.Item(ScheduleMenuModel.Refresh).Enabled && menu.Item(ScheduleMenuModel.Refresh).Tooltip == "");
            check(P + "a menu nobody built has empty button texts", new ScheduleMenu().ButtonText == "" && new ScheduleMenu().ButtonTooltip == "");
        }

        // ------------------------------------------------------------------ the row builder

        private static void RowBuild(Action<string, bool?> check)
        {
            check(P + "a source and a row start with a position unless they say otherwise", new RowBuilder.Source().HasPoint && new MarkerPlan.Row().HasPoint);
            var missing = RowBuilder.Build(null, null, null, true);
            check(P + "no source: a row that is not live, not re-tested, for 'no clash'", !missing.Live && missing.Verdict == PlanVerdict.NotRetested && missing.NotRetestedReason == "no clash");

            var bare = RowBuilder.Build(new RowBuilder.Source { Key = "k", HostUid = "h", OtherUid = "o" }, null, null, true);
            check(P + "the display values of a source with no names are empty texts", bare.Shown["LC A Name"] == "" && bare.Shown["LC A Category"] == "" && bare.Shown["LC B Name"] == "" && bare.Shown["LC B Category"] == "" && bare.Shown["LC B Model"] == "" && bare.Shown["LC Level"] == "");

            var assignedOnly = RowBuilder.Build(new RowBuilder.Source { Key = "k", HostUid = "h", OtherUid = "o" }, null, new RowBuilder.Decision { HasStatus = false, Status = MarkerStatus.Ignored, AssignedTo = "asmith" }, true);
            check(P + "a decision with only an assignee seeds a new marker as New, not with the status it did not have", assignedOnly.Seed != null && assignedOnly.Seed.Status == MarkerStatus.New && assignedOnly.Seed.At == null && assignedOnly.Seed.AssignedTo == "asmith");
            var noNames = RowBuilder.DecisionOf(null, new LedgerExtras.AssignedEntry { Assignee = null }, new LedgerExtras.IssueEntry { IssueNo = null });
            check(P + "a ledger decision with no assignee and no issue number has empty ones", noNames.AssignedTo == "" && noNames.IssueNo == "" && noNames.Note == "" && noNames.By == "");
        }
    }
}
