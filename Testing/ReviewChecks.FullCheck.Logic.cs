using System;
using System.Collections.Generic;
using System.Linq;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The 2026-10-31 review's full-check and verification findings, on the Revit-free rules (F2).
    /// The in-Revit half is <see cref="FullCheckReviewChecks"/>.
    ///
    ///   M2    a full check drops a listed pair it could not re-test (a link not loaded). It must stay listed,
    ///         as Unverified - and a re-test that throws for one pair must not cost the others.
    ///   M10   the link verifier returned Resolved after one of the host's solids failed to test.
    ///   LOW-8 Recheck's "N resolved" was a net count: one fixed plus one new read "0", and the new one went unsaid.
    ///
    /// Each was seen red at abe7dfd's behaviour first (tests\evidence\liveclash\REVIT-RUNS-F2.md).
    /// </summary>
    public static class FullCheckReviewLogicChecks
    {
        private const string P = "fullcheck logic: ";

        private static readonly string[] ChangedKeys = { "m1", "s2" };
        private static readonly string[] OnlyStructure = { "Structure" };

        public static void RunAll(Action<string, bool?> check)
        {
            Unreported(check);
            SolidTests(check);
            RecheckSummary(check);
            RemovedLink(check);
            KeptRowsNotChecked(check);
            LivePathAndRecheck(check);
        }

        // ------------------------------------------------------------------ M2

        private static ClashResult Row(string key, ClashSeverity sev)
        {
            return new ClashResult { Key = key, Severity = sev, HostElementName = "h " + key, OtherElementName = "o " + key };
        }

        private static void Unreported(Action<string, bool?> check)
        {
            var fixedRow = Row("1|0|2", ClashSeverity.Hard);
            var awayRow = Row("3|0|4", ClashSeverity.Hard);
            var unloadedRow = Row("5|9|6", ClashSeverity.Hard);
            var stillRow = Row("7|0|8", ClashSeverity.Clearance);
            var before = new List<ClashResult> { fixedRow, awayRow, unloadedRow, stillRow, Row("", ClashSeverity.Hard) };
            var now = new List<ClashResult> { Row("7|0|8", ClashSeverity.Hard) };

            var asked = new List<string>();
            Func<ClashResult, PairVerifier.Verdict> verify = r =>
            {
                asked.Add(r.Key);
                if (r.Key == "1|0|2") return PairVerifier.Verdict.Resolved;
                if (r.Key == "3|0|4") return PairVerifier.Verdict.StillClashing;
                return PairVerifier.Verdict.Unknown;
            };

            var o = UnreportedPairs.Sort(before, now, verify);
            check(P + "only the listed pairs the sweep no longer reports are re-tested (" + string.Join(",", asked.ToArray()) + ")",
                asked.Count == 3 && o.Missing == 3 && !asked.Contains("7|0|8"));
            check(P + "a pair verified gone is recorded as fixed", o.Fixed.Count == 1 && o.Fixed[0].Key == "1|0|2");
            check(P + "a pair still clashing outside the current scope is counted, not kept or recorded", o.OutOfScope == 1);
            check(P + "M2: a pair that could not be re-tested (Unknown) stays listed (" + o.Unmeasured.Count + ")",
                o.Unmeasured.Count == 1 && o.Unmeasured[0].Key == "5|9|6");
            check(P + "M2: ...as Unverified",
                o.Unmeasured.Count == 1 && o.Unmeasured[0].Severity == ClashSeverity.Unverified);
            check(P + "M2: ...without rewriting the row the snapshot holds (it is a copy)",
                unloadedRow.Severity == ClashSeverity.Hard && (o.Unmeasured.Count == 0 || !ReferenceEquals(o.Unmeasured[0], unloadedRow)));

            // A re-test that throws for one pair: that pair is unmeasured, and the others are still sorted.
            // The warning it logs goes to a throwaway file, never the real liveclash.log.
            UnreportedPairs t = null;
            Exception thrown = null;
            var logFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lc-fullcheck-logic-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (Log.RedirectForTests(logFile))
                    t = UnreportedPairs.Sort(new List<ClashResult> { Row("1|0|2", ClashSeverity.Hard), Row("5|9|6", ClashSeverity.Hard) },
                        null, r =>
                        {
                            if (r.Key == "1|0|2") throw new InvalidOperationException("re-test failed");
                            return PairVerifier.Verdict.Resolved;
                        });
            }
            catch (Exception ex) { thrown = ex; }
            finally { try { System.IO.File.Delete(logFile); } catch { } }
            check(P + "M2: a re-test that throws keeps that pair listed as Unverified" + (thrown == null ? "" : " (threw " + thrown.GetType().Name + ")"),
                t != null && t.Unmeasured.Count == 1 && t.Unmeasured[0].Key == "1|0|2" && t.Unmeasured[0].Severity == ClashSeverity.Unverified);
            check(P + "M2: ...and the other pairs are still sorted",
                t != null && t.Fixed.Count == 1 && t.Fixed[0].Key == "5|9|6");

            var none = UnreportedPairs.Sort(null, now, verify);
            check(P + "nothing listed before: nothing to sort", none.Missing == 0 && none.Fixed.Count == 0 && none.Unmeasured.Count == 0);

            // What the panel says: the kept pairs are named, so they are not taken for fresh findings.
            check(P + "M2: the panel note names the pairs that could not be checked (\"" + o.Note() + "\")",
                o.Note() == "  1 listed pair(s) could not be checked (a link not loaded, or not measurable) and stay listed as Unverified.");
            check(P + "M2: ...and says nothing when every pair was checked", none.Note() == "");
        }

        // ------------------------------------------------------------------ M10

        private static void SolidTests(Action<string, bool?> check)
        {
            check(P + "M10: a solid that failed to test, beside one that tested clean, is Unknown, never Resolved",
                PairVerifier.AfterSolidTests(true, true) == PairVerifier.Verdict.Unknown);
            check(P + "every solid tested clean: Resolved",
                PairVerifier.AfterSolidTests(true, false) == PairVerifier.Verdict.Resolved);
            check(P + "nothing tested: Unknown",
                PairVerifier.AfterSolidTests(false, false) == PairVerifier.Verdict.Unknown
                && PairVerifier.AfterSolidTests(false, true) == PairVerifier.Verdict.Unknown);
        }

        // ------------------------------------------------------------------ LOW-8

        private static void RecheckSummary(Action<string, bool?> check)
        {
            // One fixed and one new: the net is 0, and both happened.
            var both = CheckOutcome.RecheckMessage(7, 12, 5, 1, 1);
            check(P + "LOW-8: Recheck reports a fixed pair even when a new one keeps the total the same (\"" + both + "\")",
                both.Contains("1 resolved") && both.Contains("5 clash(es) still live"));
            check(P + "LOW-8: ...and reports the new pair",
                both.Contains("1 new"));

            // Two fewer rows, one verified fixed: the summary claims the one it verified.
            var net = CheckOutcome.RecheckMessage(7, 12, 3, 1, 0);
            check(P + "LOW-8: Recheck's \"resolved\" is the pairs it verified fixed, not the drop in the total (\"" + net + "\")",
                net.Contains("1 resolved") && !net.Contains("2 resolved") && !net.Contains("new"));

            var quiet = CheckOutcome.RecheckMessage(7, 12, 5, 0, 0);
            check(P + "nothing fixed and nothing new: neither is mentioned (\"" + quiet + "\")",
                quiet == "Rechecked 7 element(s) in 12 ms — 5 clash(es) still live.");
        }

        // ------------------------------------------------------------------ round 2b: auto-verify and Recheck

        /// <summary>
        /// r2b: the same rules as the full check, on the two other paths that keep Unknown rows. Auto-verify drops a
        /// row whose link instance is gone (and only then); Recheck puts an unmeasurable pair back as Unverified
        /// instead of at the severity it no longer has, and says how many it kept and dropped.
        /// </summary>
        private static void LivePathAndRecheck(Action<string, bool?> check)
        {
            int asked = 0;
            Func<bool> gone = () => { asked++; return true; };
            Func<bool> there = () => { asked++; return false; };

            check(P + "r2b: an Unknown verdict whose link instance is gone drops the row",
                PairVerifier.DropsRow(PairVerifier.Verdict.Unknown, gone));
            check(P + "r2b: ...but an Unknown verdict of a link that is only unloaded keeps it",
                !PairVerifier.DropsRow(PairVerifier.Verdict.Unknown, there));
            asked = 0;
            bool resolvedDrops = PairVerifier.DropsRow(PairVerifier.Verdict.Resolved, gone);
            bool stillDrops = PairVerifier.DropsRow(PairVerifier.Verdict.StillClashing, gone);
            check(P + "r2b: only an Unknown verdict asks, and no other verdict drops (asked " + asked + " times)",
                !resolvedDrops && !stillDrops && asked == 0);

            check(P + "r2b: a pair nobody could re-test goes back at Unverified, not at the severity it was listed at",
                CheckOutcome.RestoredSeverity(PairVerifier.Verdict.Unknown, ClashSeverity.Hard, ClashSeverity.Clearance) == ClashSeverity.Unverified);
            check(P + "r2b: ...whichever severity that was",
                CheckOutcome.RestoredSeverity(PairVerifier.Verdict.Unknown, ClashSeverity.Clearance, ClashSeverity.Hard) == ClashSeverity.Unverified);
            check(P + "r2b: a pair that still clashes goes back at the severity just verified",
                CheckOutcome.RestoredSeverity(PairVerifier.Verdict.StillClashing, ClashSeverity.Hard, ClashSeverity.Clearance) == ClashSeverity.Clearance);
            check(P + "r2b: any other verdict leaves the listed severity alone",
                CheckOutcome.RestoredSeverity(PairVerifier.Verdict.Resolved, ClashSeverity.Hard, ClashSeverity.Clearance) == ClashSeverity.Hard);

            var both = CheckOutcome.RecheckMessage(7, 12, 5, 0, 0, 2, 1);
            check(P + "r2b: Recheck says how many pairs could not be checked and how many were dropped with their link (\"" + both + "\")",
                both == "Rechecked 7 element(s) in 12 ms — 5 clash(es) still live."
                      + "  2 listed pair(s) could not be checked (a link not loaded, or not measurable) and stay listed as Unverified."
                      + "  1 pair(s) dropped: their link was removed from the model.");
            var kept = CheckOutcome.RecheckMessage(7, 12, 5, 1, 0, 1, 0);
            check(P + "r2b: ...only the sentence that applies (\"" + kept + "\")",
                kept == "Rechecked 7 element(s) in 12 ms — 1 resolved, 5 clash(es) still live."
                      + "  1 listed pair(s) could not be checked (a link not loaded, or not measurable) and stay listed as Unverified.");
            var dropped = CheckOutcome.RecheckMessage(7, 12, 5, 0, 0, 0, 3);
            check(P + "r2b: ...and the one for dropped pairs on its own (\"" + dropped + "\")",
                dropped == "Rechecked 7 element(s) in 12 ms — 5 clash(es) still live.  3 pair(s) dropped: their link was removed from the model.");
            check(P + "r2b: with neither, the message is the one it was",
                CheckOutcome.RecheckMessage(7, 12, 5, 0, 0, 0, 0) == "Rechecked 7 element(s) in 12 ms — 5 clash(es) still live.");
        }

        // ------------------------------------------------------------------ round 2: DN1, DL1

        private static ClashResult InModel(string key, string model, ClashSeverity sev)
        {
            return new ClashResult { Key = key, ModelName = model, Severity = sev, HostElementName = "h " + key, OtherElementName = "o " + key };
        }

        /// <summary>DN1: a pair whose link instance was REMOVED is dropped and said so; an unloaded link's stays.</summary>
        private static void RemovedLink(Action<string, bool?> check)
        {
            var fixedRow = Row("1|0|2", ClashSeverity.Hard);
            var removedRow = Row("5|9|6", ClashSeverity.Hard);
            var unloadedRow = Row("5|8|6", ClashSeverity.Hard);
            var before = new List<ClashResult> { fixedRow, removedRow, unloadedRow };

            var asked = new List<string>();
            Func<ClashResult, PairVerifier.Verdict> verify = r =>
            {
                asked.Add(r.Key);
                return r.Key == "1|0|2" ? PairVerifier.Verdict.Resolved : PairVerifier.Verdict.Unknown;
            };
            Func<ClashResult, bool> removed = r => r.Key == "5|9|6";

            var o = UnreportedPairs.Sort(before, new List<ClashResult>(), verify, removed);
            check(P + "DN1: a pair whose link was removed is not re-tested (asked: " + string.Join(",", asked.ToArray()) + ")",
                asked.Count == 2 && !asked.Contains("5|9|6"));
            check(P + "DN1: ...it is dropped and counted, with the other two still counted as missing",
                o.Removed.Count == 1 && o.Removed[0].Key == "5|9|6" && o.Missing == 3);
            check(P + "DN1: ...not recorded as fixed and not kept: the unloaded link's pair is the only one kept",
                o.Fixed.Count == 1 && o.Fixed[0].Key == "1|0|2" && o.Unmeasured.Count == 1 && o.Unmeasured[0].Key == "5|8|6");
            check(P + "DN1: the panel says both: what could not be checked, and what was dropped because its link was removed (\"" + o.Note() + "\")",
                o.Note() == "  1 listed pair(s) could not be checked (a link not loaded, or not measurable) and stay listed as Unverified."
                          + "  1 pair(s) dropped: their link was removed from the model.");

            var onlyRemoved = UnreportedPairs.Sort(new List<ClashResult> { removedRow }, new List<ClashResult>(), verify, removed);
            check(P + "DN1: with only a removed link's pair, the panel says only that (\"" + onlyRemoved.Note() + "\")",
                onlyRemoved.Note() == "  1 pair(s) dropped: their link was removed from the model.");

            // The predicate asks Revit, so it can throw: that pair is then just one nobody could re-test.
            var logFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lc-fullcheck-r2-" + Guid.NewGuid().ToString("N") + ".log");
            UnreportedPairs t = null;
            try
            {
                using (Log.RedirectForTests(logFile))
                    t = UnreportedPairs.Sort(new List<ClashResult> { removedRow }, new List<ClashResult>(), verify,
                        r => { throw new InvalidOperationException("lookup failed"); });
            }
            finally { try { System.IO.File.Delete(logFile); } catch { } }
            check(P + "DN1: a lookup that throws leaves the pair listed as Unverified, never dropped",
                t != null && t.Removed.Count == 0 && t.Unmeasured.Count == 1 && t.Unmeasured[0].Severity == ClashSeverity.Unverified);

            var plain = UnreportedPairs.Sort(new List<ClashResult> { unloadedRow }, new List<ClashResult>(), verify);
            check(P + "DN1: without the lookup an unloadable pair is kept, as before",
                plain.Removed.Count == 0 && plain.Unmeasured.Count == 1);
        }

        /// <summary>DL1: rows of a link that was not loaded come back from the full check as Unverified. That is not a
        /// "changed severity", and not "no clashes listed": the summary says they were not checked.</summary>
        private static void KeptRowsNotChecked(Action<string, bool?> check)
        {
            var s0 = LinkWatch.Snapshot();
            try
            {
                // Only the one link unloaded: one kept row, one dropped.
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>
                {
                    InModel("s1", "Structure", ClashSeverity.Hard), InModel("s2", "Structure", ClashSeverity.Hard)
                };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "Structure");
                ClashChangeTracker.Complete(new[] { InModel("s1", "Structure", ClashSeverity.Unverified) }, true);
                var a = ClashChangeTracker.Current;
                check(P + "DL1: a kept Unverified row of an unloaded link is counted as not checked, not as a changed severity",
                    a != null && a.NotChecked.Count == 1 && a.NotChecked[0].Key == "s1" && a.Set.SeverityChanged.Count == 0);
                check(P + "DL1: ...and the headline says what left the list and what was kept (\"" + (a == null ? "" : a.Headline) + "\")",
                    a != null && a.Headline == "Structure is no longer there: 1 clash with it left the list"
                                               + " and 1 clash with it could not be checked and stays listed as Unverified.");

                ClashChangeTracker.Restore(null);   // clears the source too
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>
                {
                    InModel("s1", "Structure", ClashSeverity.Hard), InModel("s2", "Structure", ClashSeverity.Hard)
                };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "Structure");
                ClashChangeTracker.Complete(new[] { InModel("s1", "Structure", ClashSeverity.Unverified), InModel("s2", "Structure", ClashSeverity.Unverified) }, true);
                var b = ClashChangeTracker.Current;
                check(P + "DL1: with every row kept, the headline does not claim it had no clashes (\"" + (b == null ? "" : b.Headline) + "\")",
                    b != null && b.Headline == "Structure is no longer there: 2 clashes with it could not be checked and stay listed as Unverified.");

                // A reload of another link first: the "since A and B changed" form.
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult>
                {
                    InModel("m1", "Mech", ClashSeverity.Hard), InModel("s1", "Structure", ClashSeverity.Hard), InModel("s2", "Structure", ClashSeverity.Hard)
                };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Mech");
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "Structure");
                ClashChangeTracker.Complete(new[]
                {
                    InModel("m1", "Mech", ClashSeverity.Unverified),
                    InModel("s1", "Structure", ClashSeverity.Unverified),
                    InModel("s2", "Structure", ClashSeverity.Clearance)
                }, true);
                var c = ClashChangeTracker.Current;
                check(P + "DL1: with another link also changed, the kept row of the unloaded one is not checked (" + (c == null ? "no summary" : c.NotChecked.Count + " not checked") + ")",
                    c != null && c.NotChecked.Count == 1 && c.NotChecked[0].Key == "s1");
                check(P + "DL1: ...only an Unverified row moves: the other severity changes stay as they were",
                    c != null && c.Set.SeverityChanged.Select(p => p.Key.Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(ChangedKeys));
                check(P + "DL1: ...and the headline reads \"" + (c == null ? "" : c.Headline) + "\"",
                    c != null && c.Headline == "Since Mech and Structure changed: 2 changed severity, 1 not checked (Structure not loaded).");
                check(P + "DL1: the unloaded link is the one named as not loaded",
                    c != null && c.NotLoadedLinks.SequenceEqual(OnlyStructure));
            }
            finally
            {
                // The tracker is cleared first, then LinkWatch puts back the state it carried (the tracker's too).
                ClashChangeTracker.Restore(null);
                LinkWatch.Restore(s0);
            }
        }
    }
}
