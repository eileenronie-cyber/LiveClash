using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 4a: the pure half of the Revit side (spec 4, 7, 8): the parameter table and the
    /// record-to-parameter text, the section-7 refusal texts, the ownership classification and the write filter.
    /// Revit-free. Red-first: BUILD-LOG-C.md.
    /// </summary>
    public static class MarkerRevitLogicChecks
    {
        private const string P = "markers params: ";
        private const string R = "markers refuse: ";
        private const string O = "markers owner: ";
        private const string F = "markers filter: ";

        public static void RunAll(Action<string, bool?> check)
        {
            Params(check);
            Build(check);
            Refusals(check);
            Owner(check);
            Filter(check);
            Switch(check);
            Shown(check);
            Undo(check);
        }

        private const string SH = "markers shown: ";

        private static RefreshFingerprint Fp(int creations, int removals, int offers)
        {
            return new RefreshFingerprint { Creations = creations, Removals = removals, RetentionOffers = offers };
        }

        private static void Shown(Action<string, bool?> check)
        {
            var shown = Fp(10, 1, 3);
            check(SH + "a plan that is the one shown needs no refusal", RefreshFingerprint.Changed(shown, Fp(10, 1, 3), false) == null);
            check(SH + "more creations than the dialog showed are refused, naming both numbers",
                RefreshFingerprint.Changed(shown, Fp(12, 1, 3), false)
                == "The clashes changed since the plan was shown: it would now create 12 markers, not 10. Review the plan again. Nothing was changed.");
            check(SH + "fewer creations are fine", RefreshFingerprint.Changed(shown, Fp(8, 1, 3), false) == null);
            check(SH + "more removals (merged duplicates) than shown are refused",
                RefreshFingerprint.Changed(shown, Fp(10, 2, 3), false)
                == "The clashes changed since the plan was shown: it would now remove 2 markers, not 1. Review the plan again. Nothing was changed."
                && RefreshFingerprint.Changed(shown, Fp(10, 5, 3), true) != null);   // ticked: 1 + 3 may go, 5 may not
            check(SH + "with retention ticked the removals may grow by exactly the offers shown, and no further",
                RefreshFingerprint.Changed(shown, Fp(10, 4, 3), true) == null);
            check(SH + "more retention offers than shown are refused, ticked or not",
                RefreshFingerprint.Changed(shown, Fp(10, 1, 5), false)
                == "The clashes changed since the plan was shown: 5 markers are now old enough to remove, not 3. Review the plan again. Nothing was changed."
                && RefreshFingerprint.Changed(shown, Fp(10, 1, 5), true) != null);
            check(SH + "nothing shown yet (an apply with no plan before it) needs no refusal", RefreshFingerprint.Changed(null, Fp(99, 9, 9), true) == null);

            // Delta review D-L1: changes, lost decisions, and who is in the plan.
            var chShown = FpFull(10, 1, 3, 2, 0, new List<string> { "a", "b" }, new List<string> { "m1", "m2" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" });
            check(SH + "more changes than shown are refused, naming both numbers",
                RefreshFingerprint.Changed(chShown, FpFull(10, 1, 3, 5, 0, new List<string> { "a", "b" }, new List<string> { "m1", "m2", "m3", "m4", "m5" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" }), false)
                == "The clashes changed since the plan was shown: it would now change 5 markers, not 2. Review the plan again. Nothing was changed.");
            check(SH + "fewer changes are fine",
                RefreshFingerprint.Changed(chShown, FpFull(10, 1, 3, 1, 0, new List<string> { "a", "b" }, new List<string> { "m1" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" }), false) == null);
            check(SH + "more lost decisions than shown are refused, naming both numbers",
                RefreshFingerprint.Changed(chShown, FpFull(10, 1, 3, 2, 2, new List<string> { "a", "b" }, new List<string> { "m1", "m2" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" }), false)
                == "The clashes changed since the plan was shown: it would now lose 2 decisions, not 0. Review the plan again. Nothing was changed.");
            check(SH + "a swap at the same count is refused: other markers would be created than the ones shown",
                RefreshFingerprint.Changed(chShown, FpFull(10, 1, 3, 2, 0, new List<string> { "a", "x" }, new List<string> { "m1", "m2" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" }), false) != null);
            check(SH + "a subset of the creations shown is fine",
                RefreshFingerprint.Changed(chShown, FpFull(9, 1, 3, 2, 0, new List<string> { "a" }, new List<string> { "m1", "m2" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" }), false) == null);
            check(SH + "with retention ticked the offers shown may go, another marker may not",
                RefreshFingerprint.Changed(chShown, FpFull(10, 4, 3, 2, 0, new List<string> { "a", "b" }, new List<string> { "m1", "m2" }, new List<string> { "r1", "o1", "o2" }, new List<string> { "o1", "o2", "o3" }), true) == null
                && RefreshFingerprint.Changed(chShown, FpFull(10, 4, 3, 2, 0, new List<string> { "a", "b" }, new List<string> { "m1", "m2" }, new List<string> { "r1", "o1", "zz" }, new List<string> { "o1", "o2", "o3" }), true) != null);
            check(SH + "a swap of the changed markers at the same count is refused",
                RefreshFingerprint.Changed(chShown, FpFull(10, 1, 3, 2, 0, new List<string> { "a", "b" }, new List<string> { "m1", "m9" }, new List<string> { "r1" }, new List<string> { "o1", "o2", "o3" }), false) != null);
        }

        private static RefreshFingerprint FpFull(int creations, int removals, int offers, int changes, int lost, IEnumerable<string> cids, IEnumerable<string> chids, IEnumerable<string> rids, IEnumerable<string> oids)
        {
            return new RefreshFingerprint
            {
                Creations = creations, Removals = removals, RetentionOffers = offers, Changes = changes, LostDecisions = lost,
                CreationIds = new HashSet<string>(cids), ChangeIds = new HashSet<string>(chids), RemovalIds = new HashSet<string>(rids), OfferIds = new HashSet<string>(oids)
            };
        }

        private const string UN = "markers undo: ";

        private static void Undo(Action<string, bool?> check)
        {
            check(UN + "no names, or none at all, concern no marker", !MarkerUndo.Concerns(null) && !MarkerUndo.Concerns(Array.Empty<string>()));
            check(UN + "each of the marker transactions concerns the markers",
                MarkerUndo.Concerns(new List<string> { "LiveClash: set clash status" }) && MarkerUndo.Concerns(new List<string> { "LiveClash: refresh clash markers" })
                && MarkerUndo.Concerns(new List<string> { "LiveClash: remove all clash markers" }));
            check(UN + "other transactions do not", !MarkerUndo.Concerns(new List<string> { "Move", "Delete" }));
            check(UN + "one of ours among others does", MarkerUndo.Concerns(new List<string> { "Move", "LiveClash: set clash status", "Delete" }));
            check(UN + "the match is exact: a lower-cased copy is not ours", !MarkerUndo.Concerns(new List<string> { "liveclash: set clash status" }));
        }

        private const string SW = "markers switch: ";
        private const string OffText = "Clash markers are switched off on this PC. Nothing was changed.";

        private static void Switch(Action<string, bool?> check)
        {
            check(SW + "switched off, the refusal is the switch's text", MarkerRefusals.Check(new MarkerEnv { SwitchedOff = true }) == OffText);
            check(SW + "...and it comes first, whatever else is set",
                MarkerRefusals.Check(new MarkerEnv
                {
                    SwitchedOff = true, IsFamily = true, IsLinked = true, IsReadOnly = true, Workshared = true, WorksetClosed = true,
                    NotSessionDocument = true, SettingsHeldBy = "kdoe", ForeignParameterName = "LC Status"
                }) == OffText);
        }

        // The spec's column order (section 4), and the frozen GUIDs. Typed out here on purpose: a changed literal in the
        // product code must fail a check (the public copy keeps the same GUIDs; changing one orphans the binding in every model).
        private static readonly string[] SpecTextNames =
        {
            "LC Issue No", "LC Clash Id", "LC Status", "LC Severity", "LC Set", "LC Also Caught By", "LC Group",
            "LC A Id", "LC A Name", "LC A Category", "LC B Id", "LC B Name", "LC B Category", "LC B Model", "LC Level",
            "LC Assigned To", "LC Reported By", "LC Reported", "LC Changed By", "LC Changed", "LC Note", "LC Last Checked"
        };

        private static readonly string[] FrozenTextGuids =
        {
            "2f42272f-f5d5-4014-bd3b-f168f587d9e1", "a39fd53c-3701-4bf4-bdfd-c74376ab3d77", "1db0abb4-fe36-4ae4-af98-2569787c4524",
            "a803407d-7d5e-4721-85bf-5341f006f975", "69ce33a8-1a37-4d75-8f36-bea0a3e731d7", "035032f8-dee2-488a-a7aa-3c5626048c46",
            "87653ea6-ab15-455b-95ef-c963acba1c58", "988a9708-b5dc-496b-af42-bd4310cf48dc", "3afe2f72-8a2a-4f78-b70b-2833ebaa0326",
            "6159beee-877f-450a-9635-08ca93ce3fb0", "4d194777-bafa-483e-8558-d9cf8a7e6209", "9c0cdfc7-bee1-4ce7-895d-66dc19a93a91",
            "f882682d-33a6-4380-b296-9f9b29f118ee", "b07666d2-6b4f-46c4-91a8-fd8fd0e858c6", "ed63dcca-7914-48bd-aa88-87e7428159bc",
            "2d535f69-e98e-4a93-b4e8-4abd500a96c1", "aaa49466-d3be-43bc-a743-fca369334610", "d1caebfb-e35f-4d4c-bda3-46f3812b6d82",
            "ecc2c3de-313d-44af-bcc2-9d98480d4405", "4b62b25f-0895-4276-b57b-b7b089a62c0b", "9eade995-09b5-4e13-8e8f-c45adb0a79b1",
            "6c8a8f48-88c7-4a46-a751-7c213dec4ce2"
        };

        private const string FrozenOrderGuid = "c4a57272-9940-4070-a22a-66da1cf9c734";

        private static readonly string[] ShownNames = { "LC A Name", "LC A Category", "LC B Name", "LC B Category", "LC B Model", "LC Level" };

        // ------------------------------------------------------------------ the parameter table

        private static void Params(Action<string, bool?> check)
        {
            var defs = MarkerParameterMap.Definitions;
            var text = defs.Where(d => !d.IsInteger).ToList();
            check(P + "22 Text parameters, in the spec's column order",
                text.Select(d => d.Name).SequenceEqual(SpecTextNames));
            check(P + "every name starts with 'LC ' and is unique (case-insensitive)",
                defs.Count > 0 && defs.All(d => d.Name.StartsWith("LC ", StringComparison.Ordinal))
                && defs.Select(d => d.Name.ToLowerInvariant()).Distinct().Count() == defs.Count);
            check(P + "every GUID is non-zero and unique",
                defs.Count > 0 && defs.All(d => d.ParameterGuid != Guid.Empty) && defs.Select(d => d.ParameterGuid).Distinct().Count() == defs.Count);
            check(P + "the 22 Text GUIDs are the frozen ones, in order",
                text.Select(d => d.ParameterGuid.ToString()).SequenceEqual(FrozenTextGuids));
            var ints = defs.Where(d => d.IsInteger).ToList();
            check(P + "the one Integer parameter is 'LC Status Order' with its frozen GUID (the schedule sorts on it, hidden)",
                ints.Count == 1 && ints[0].Name == "LC Status Order" && ints[0].ParameterGuid.ToString() == FrozenOrderGuid
                && MarkerParameterMap.StatusOrderName == "LC Status Order");
            check(P + "'LC Last Checked Num' is off until probe P24 asks for it",
                !MarkerParameterMap.IncludeLastCheckedNum && defs.All(d => d.Name != "LC Last Checked Num"));
            check(P + "the six display names are the ones read from the 'shown' map, none of them from the record",
                MarkerParameterMap.ShownNames.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(ShownNames.OrderBy(x => x, StringComparer.Ordinal))
                && MarkerParameterMap.RecordDerivedNames.Count == 16
                && MarkerParameterMap.RecordDerivedNames.All(n => SpecTextNames.Contains(n) && !ShownNames.Contains(n)));
            check(P + "status order: New 1, Pending 2, Reopened 3, Coordinated 4, Ignored 5, Resolved 6",
                MarkerStatusText.Order(MarkerStatus.New) == 1 && MarkerStatusText.Order(MarkerStatus.Pending) == 2
                && MarkerStatusText.Order(MarkerStatus.Reopened) == 3 && MarkerStatusText.Order(MarkerStatus.Coordinated) == 4
                && MarkerStatusText.Order(MarkerStatus.Ignored) == 5 && MarkerStatusText.Order(MarkerStatus.Resolved) == 6);
        }

        // ------------------------------------------------------------------ record + shown -> parameter text

        private static MarkerRecord Full()
        {
            return new MarkerRecord
            {
                Key = "k", HostUid = "h-uid", OtherUid = "o-uid", HostId = 1234567, OtherId = 7654321,
                ClashId = "LC-0a1b2c3d", IssueNo = "JD-0042", Status = MarkerStatus.Pending, Note = "pipe through beam",
                AssignedTo = "kdoe", ReportedBy = "jdoe", ReportedAt = new DateTimeOffset(2026, 10, 31, 14, 5, 59, TimeSpan.FromHours(8)),
                ChangedBy = "kdoe", ChangedAt = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(-5.5)),
                Severity = "Hard", SetName = "MEP vs STR", AlsoSets = new List<string> { "MEP vs MEP", "ARC vs STR" },
                GroupLabel = "JD-0042 (+13)", LastCheckedAt = new DateTimeOffset(2026, 11, 3, 23, 59, 0, TimeSpan.FromHours(8)), LastCheckedBy = "jdoe"
            };
        }

        private static Dictionary<string, string> FullShown()
        {
            return new Dictionary<string, string>
            {
                { "LC A Name", "Pipe: Steel 100 [1234567]" }, { "LC A Category", "Pipes" }, { "LC B Name", "Beam: W200 [7654321]" },
                { "LC B Category", "Structural Framing" }, { "LC B Model", "Current model" }, { "LC Level", "Level 2" }
            };
        }

        private static string V(Dictionary<string, string> m, string name)
        {
            string v;
            return m != null && m.TryGetValue(name, out v) ? v : null;
        }

        private static void Build(Action<string, bool?> check)
        {
            var m = MarkerParameterMap.Build(Full(), FullShown());
            check(P + "a full record gives all 22 names, no more",
                m != null && m.Count == 22 && SpecTextNames.All(n => m.ContainsKey(n)));
            check(P + "the record's own columns: issue no, clash id, status, severity, set, group, assigned, reported by, changed by, note",
                V(m, "LC Issue No") == "JD-0042" && V(m, "LC Clash Id") == "LC-0a1b2c3d" && V(m, "LC Status") == "Pending"
                && V(m, "LC Severity") == "Hard" && V(m, "LC Set") == "MEP vs STR" && V(m, "LC Group") == "JD-0042 (+13)"
                && V(m, "LC Assigned To") == "kdoe" && V(m, "LC Reported By") == "jdoe" && V(m, "LC Changed By") == "kdoe"
                && V(m, "LC Note") == "pipe through beam");
            check(P + "'Also caught by' joins the other sets with a comma and a space",
                V(m, "LC Also Caught By") == "MEP vs MEP, ARC vs STR");
            check(P + "A id and B id are the host and the other element's ids as whole numbers",
                V(m, "LC A Id") == "1234567" && V(m, "LC B Id") == "7654321");
            check(P + "the display columns come from the shown map",
                V(m, "LC A Name") == "Pipe: Steel 100 [1234567]" && V(m, "LC A Category") == "Pipes" && V(m, "LC B Name") == "Beam: W200 [7654321]"
                && V(m, "LC B Category") == "Structural Framing" && V(m, "LC B Model") == "Current model" && V(m, "LC Level") == "Level 2");
            check(P + "reported and changed print the writer's own UTC offset (L3): +08:00 and -05:30",
                V(m, "LC Reported") == "2026-10-31 14:05 +08:00" && V(m, "LC Changed") == "2026-11-02 09:30 -05:30");
            check(P + "an offset of zero prints +00:00",
                V(MarkerParameterMap.Build(new MarkerRecord { ReportedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero) }, null), "LC Reported") == "2026-01-02 03:04 +00:00");
            check(P + "'last checked' is the date only, in the writer's own calendar day",
                V(m, "LC Last Checked") == "2026-11-03");
            var empty = MarkerParameterMap.Build(new MarkerRecord(), null);
            check(P + "an empty record gives all 22 names, each empty except the status, which is New",
                empty != null && empty.Count == 22 && SpecTextNames.All(n => V(empty, n) == (n == "LC Status" ? "New" : "")));
            check(P + "a null record and a null shown map never throw and give the same 22 values as an empty record",
                Safe(() => { var x = MarkerParameterMap.Build(null, null); return x != null && x.Count == 22 && SpecTextNames.All(n => V(x, n) == V(empty, n)); }));
            check(P + "ids above 2^31 print whole (64-bit, RF10)",
                V(MarkerParameterMap.Build(new MarkerRecord { HostId = 9007199254740993L, OtherId = long.MaxValue }, null), "LC A Id") == "9007199254740993"
                && V(MarkerParameterMap.Build(new MarkerRecord { HostId = 9007199254740993L, OtherId = long.MaxValue }, null), "LC B Id") == "9223372036854775807");
            var tricky = FullShown();
            tricky["LC Status"] = "Resolved"; tricky["LC Note"] = "x"; tricky["LC A Id"] = "999";
            var m2 = MarkerParameterMap.Build(Full(), tricky);
            check(P + "a shown value never overrides a record-derived column",
                V(m2, "LC Status") == "Pending" && V(m2, "LC Note") == "pipe through beam" && V(m2, "LC A Id") == "1234567");
            var partial = MarkerParameterMap.Build(Full(), new Dictionary<string, string> { { "LC Level", "Level 9" } });
            check(P + "a missing shown entry is empty, a present one is kept",
                V(partial, "LC Level") == "Level 9" && V(partial, "LC A Name") == "" && V(partial, "LC B Model") == "");
            check(P + "no value is ever null",
                m != null && m.Values.All(s => s != null) && empty != null && empty.Values.All(s => s != null));

            // Culture: a Buddhist-calendar, comma-decimal culture must not change a digit.
            var saved = Thread.CurrentThread.CurrentCulture;
            Dictionary<string, string> th = null;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                th = MarkerParameterMap.Build(Full(), FullShown());
            }
            catch (CultureNotFoundException) { }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
            check(P + "the times print the same under a Buddhist-calendar culture",
                th != null ? V(th, "LC Reported") == "2026-10-31 14:05 +08:00" && V(th, "LC Last Checked") == "2026-11-03" : (bool?)null);
        }

        private static bool Safe(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        // ------------------------------------------------------------------ section 7 refusals

        private static void Refusals(Action<string, bool?> check)
        {
            check(R + "a clean environment has no refusal", MarkerRefusals.Check(new MarkerEnv()) == null);
            check(R + "a null environment never throws and has no refusal", Safe(() => MarkerRefusals.Check(null) == null));
            check(R + "a family document: 'Clash markers live in project models only.'",
                MarkerRefusals.Check(new MarkerEnv { IsFamily = true }) == "Clash markers live in project models only.");
            check(R + "a linked document has its own refusal (never offered, SessionGuard)",
                !string.IsNullOrEmpty(MarkerRefusals.Check(new MarkerEnv { IsLinked = true }))
                && MarkerRefusals.Check(new MarkerEnv { IsLinked = true }) != MarkerRefusals.Check(new MarkerEnv { IsFamily = true }));
            check(R + "a read-only model: 'This model is read-only.'",
                MarkerRefusals.Check(new MarkerEnv { IsReadOnly = true }) == "This model is read-only.");
            check(R + "a workshared model with the LiveClash workset closed",
                MarkerRefusals.Check(new MarkerEnv { Workshared = true, WorksetClosed = true })
                == "The LiveClash workset is closed. Open it (Manage Worksets) and try again.");
            check(R + "a closed-workset flag in a model that is not workshared is no refusal",
                MarkerRefusals.Check(new MarkerEnv { Workshared = false, WorksetClosed = true }) == null);
            check(R + "not the session's document: 'Switch to the model LiveClash is checking.'",
                MarkerRefusals.Check(new MarkerEnv { NotSessionDocument = true }) == "Switch to the model LiveClash is checking.");
            check(R + "the settings record held by another user names them",
                MarkerRefusals.Check(new MarkerEnv { SettingsHeldBy = "kdoe" })
                == "kdoe is changing the clash schedule set-up. Try again after they synchronize.");
            check(R + "an empty or blank holder name is no refusal",
                MarkerRefusals.Check(new MarkerEnv { SettingsHeldBy = "" }) == null && MarkerRefusals.Check(new MarkerEnv { SettingsHeldBy = "  " }) == null);
            check(R + "a parameter with LiveClash's name but another GUID names the parameter",
                MarkerRefusals.Check(new MarkerEnv { ForeignParameterName = "LC Status" })
                == "LC Status already exists in this model but is not LiveClash's. Rename it first.");
            check(R + "with everything set, the first in the spec's order wins (family)",
                MarkerRefusals.Check(new MarkerEnv
                {
                    IsFamily = true, IsLinked = true, IsReadOnly = true, Workshared = true, WorksetClosed = true,
                    NotSessionDocument = true, SettingsHeldBy = "kdoe", ForeignParameterName = "LC Status"
                }) == "Clash markers live in project models only.");
            check(R + "the order of the rest: read-only, then closed workset, then session, then holder, then parameter",
                MarkerRefusals.Check(new MarkerEnv { IsReadOnly = true, Workshared = true, WorksetClosed = true, NotSessionDocument = true }) == "This model is read-only."
                && MarkerRefusals.Check(new MarkerEnv { Workshared = true, WorksetClosed = true, NotSessionDocument = true, SettingsHeldBy = "kdoe" })
                    == "The LiveClash workset is closed. Open it (Manage Worksets) and try again."
                && MarkerRefusals.Check(new MarkerEnv { NotSessionDocument = true, SettingsHeldBy = "kdoe", ForeignParameterName = "LC Note" })
                    == "Switch to the model LiveClash is checking."
                && MarkerRefusals.Check(new MarkerEnv { SettingsHeldBy = "kdoe", ForeignParameterName = "LC Note" })
                    == "kdoe is changing the clash schedule set-up. Try again after they synchronize.");
            check(R + "newer-format note: none for 0, singular for 1, plural for 5",
                MarkerRefusals.NewerFormatNote(0) == "" && MarkerRefusals.NewerFormatNote(-3) == ""
                && MarkerRefusals.NewerFormatNote(1) == "1 marker was written by a newer LiveClash; update to change it."
                && MarkerRefusals.NewerFormatNote(5) == "5 markers were written by a newer LiveClash; update to change them.");
            check(R + "the cap message groups the thousands the same on every culture",
                MarkerRefusals.CapRefusal(2350, 2000)
                == "2,350 markers would be created. Narrow the sets, or create them in two runs (Refresh again after this one).");
            check(R + "the cap is over only above it: 2,000 is fine, 2,001 is not",
                !MarkerRefusals.IsOverCap(2000, 2000) && MarkerRefusals.IsOverCap(2001, 2000) && !MarkerRefusals.IsOverCap(0, 2000));
        }

        // ------------------------------------------------------------------ ownership classification

        private static void Owner(Action<string, bool?> check)
        {
            var free = MarkerOwnershipRules.Classify(MarkerOwner.Unowned, "", MarkerCentral.Current, false);
            check(O + "an unowned, current marker outside a group is free to write",
                free != null && free.Kind == MarkerAccess.Free && free.CanWrite && free.Reason == "");
            var mine = MarkerOwnershipRules.Classify(MarkerOwner.OwnedByMe, "jdoe", MarkerCentral.Current, false);
            check(O + "a marker this user already holds is free to write", mine.Kind == MarkerAccess.Free && mine.CanWrite);
            var other = MarkerOwnershipRules.Classify(MarkerOwner.OwnedByOther, "kdoe", MarkerCentral.Current, false);
            check(O + "a marker another user holds is passed over and names them",
                other.Kind == MarkerAccess.HeldByOther && !other.CanWrite && other.HeldBy == "kdoe" && other.Reason == "held by kdoe");
            var anon = MarkerOwnershipRules.Classify(MarkerOwner.OwnedByOther, "", MarkerCentral.Current, false);
            check(O + "a holder with no name is 'held by another user'",
                anon.Kind == MarkerAccess.HeldByOther && anon.Reason == "held by another user" && anon.HeldBy == "");
            var upd = MarkerOwnershipRules.Classify(MarkerOwner.Unowned, "", MarkerCentral.UpdatedInCentral, false);
            var del = MarkerOwnershipRules.Classify(MarkerOwner.Unowned, "", MarkerCentral.DeletedInCentral, false);
            check(O + "updated or deleted in central: passed over, 'Reload Latest first'",
                upd.Kind == MarkerAccess.ChangedInCentral && del.Kind == MarkerAccess.ChangedInCentral && !upd.CanWrite && !del.CanWrite
                && upd.Reason == "changed in central: Reload Latest first");
            var grp = MarkerOwnershipRules.Classify(MarkerOwner.Unowned, "", MarkerCentral.Current, true);
            check(O + "a marker inside a group is passed over, never edited",
                grp.Kind == MarkerAccess.InsideGroup && !grp.CanWrite && grp.Reason == "inside a group");
            check(O + "priority: a group beats a holder, a holder beats a change in central",
                MarkerOwnershipRules.Classify(MarkerOwner.OwnedByOther, "kdoe", MarkerCentral.UpdatedInCentral, true).Kind == MarkerAccess.InsideGroup
                && MarkerOwnershipRules.Classify(MarkerOwner.OwnedByOther, "kdoe", MarkerCentral.UpdatedInCentral, false).Kind == MarkerAccess.HeldByOther
                && MarkerOwnershipRules.Classify(MarkerOwner.OwnedByMe, "jdoe", MarkerCentral.UpdatedInCentral, false).Kind == MarkerAccess.ChangedInCentral);
            check(O + "only Free can be written: the other five kinds cannot",
                NonWritable
                    .All(k => !MarkerOwnershipRules.CanWrite(k)) && MarkerOwnershipRules.CanWrite(MarkerAccess.Free));
            check(O + "a null holder name never throws", Safe(() => MarkerOwnershipRules.Classify(MarkerOwner.OwnedByOther, null, MarkerCentral.Current, false).Reason == "held by another user"));
        }

        // ------------------------------------------------------------------ the write filter

        private static readonly MarkerAccess[] NonWritable =
            { MarkerAccess.HeldByOther, MarkerAccess.ChangedInCentral, MarkerAccess.InsideGroup, MarkerAccess.NewerFormat, MarkerAccess.Unreadable };
        private static readonly string[] KeptTags = { "w0", "w1", "w3", "w4" };
        private static readonly string[] DroppedTags = { "w2", "w5" };
        private static readonly string[] KeptOrder = { "c", "b" };
        private static readonly string[] KeptCase = { "Ab" };

        private sealed class W
        {
            public string Uid;      // null = a create
            public string Tag;
            public override string ToString() { return Tag; }
        }

        private static List<W> Items(params string[] uids)
        {
            return uids.Select((u, i) => new W { Uid = u, Tag = "w" + i }).ToList();
        }

        private static HashSet<string> Set(params string[] s)
        {
            return new HashSet<string>(s, StringComparer.Ordinal);
        }

        private static void Filter(Action<string, bool?> check)
        {
            var items = Items("a", null, "b", "c", null, "d");
            var s = MarkerWriteFilter.Apply(items, w => w.Uid, Set("b", "d"));
            check(F + "passed-over markers are dropped, the rest kept",
                s.Kept.Select(w => w.Tag).SequenceEqual(KeptTags)
                && s.Dropped.Select(w => w.Tag).SequenceEqual(DroppedTags));
            var creates = MarkerWriteFilter.Apply(Items(null, "", null), w => w.Uid, Set("", "x"));
            check(F + "a create (no uid) is never dropped, even when the set holds an empty string",
                creates.Kept.Count == 3 && creates.Dropped.Count == 0);
            check(F + "the order of the kept items is the order given",
                MarkerWriteFilter.Apply(Items("c", "a", "b"), w => w.Uid, Set("a")).Kept.Select(w => w.Uid).SequenceEqual(KeptOrder));
            check(F + "an empty or null passed-over set keeps everything",
                MarkerWriteFilter.Apply(items, w => w.Uid, Set()).Kept.Count == 6 && MarkerWriteFilter.Apply(items, w => w.Uid, null).Kept.Count == 6);
            check(F + "null or empty input gives empty lists, never a throw",
                Safe(() => { var x = MarkerWriteFilter.Apply<W>(null, w => w.Uid, Set("a")); return x.Kept.Count == 0 && x.Dropped.Count == 0; })
                && MarkerWriteFilter.Apply(new List<W>(), w => w.Uid, Set("a")).Kept.Count == 0);
            check(F + "uids compare exactly: case matters",
                MarkerWriteFilter.Apply(Items("Ab", "ab"), w => w.Uid, Set("ab")).Kept.Select(w => w.Uid).SequenceEqual(KeptCase));
            check(F + "everything dropped leaves nothing kept, and that is a no-op without set-up",
                MarkerWriteFilter.IsNoOp(MarkerWriteFilter.Apply(Items("a", "b"), w => w.Uid, Set("a", "b")).Kept.Count, false));
            check(F + "nothing to write but set-up needed is not a no-op; something to write is not a no-op",
                !MarkerWriteFilter.IsNoOp(0, true) && !MarkerWriteFilter.IsNoOp(1, false) && !MarkerWriteFilter.IsNoOp(1, true) && MarkerWriteFilter.IsNoOp(0, false));

            var rng = new Random(20261031);
            bool partition = true, idem = true, noThrow = true;
            for (int run = 0; run < 200; run++)
            {
                var list = Items(Enumerable.Range(0, rng.Next(0, 30)).Select(i => rng.Next(0, 4) == 0 ? null : "u" + rng.Next(0, 12)).ToArray());
                var hold = Set(Enumerable.Range(0, rng.Next(0, 8)).Select(i => "u" + rng.Next(0, 12)).ToArray());
                try
                {
                    var once = MarkerWriteFilter.Apply(list, w => w.Uid, hold);
                    var twice = MarkerWriteFilter.Apply(once.Kept, w => w.Uid, hold);
                    partition &= once.Kept.Count + once.Dropped.Count == list.Count
                                 && once.Kept.Concat(once.Dropped).All(list.Contains)
                                 && once.Dropped.All(w => w.Uid != null && hold.Contains(w.Uid))
                                 && once.Kept.All(w => w.Uid == null || !hold.Contains(w.Uid));
                    idem &= twice.Kept.Count == once.Kept.Count && twice.Dropped.Count == 0;
                }
                catch { noThrow = false; }
            }
            check(F + "property (200 random lists): kept and dropped partition the input, nothing held is kept, only held ones are dropped",
                noThrow && partition);
            check(F + "property (200 random lists): filtering a second time drops nothing more", noThrow && idem);
        }
    }
}
