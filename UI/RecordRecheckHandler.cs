using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Re-tests every pair in the RECORD, rather than every pair on the panel.
    ///
    /// The two are different sets and that is the point. The panel lists what currently
    /// clashes; the record lists what somebody DECIDED about — including pairs that have
    /// long since dropped out of the live results. Those are exactly the ones nobody
    /// re-examines: a clash accepted in June is invisible on the panel, so if it quietly
    /// got fixed, the record goes on claiming it is an accepted live clash forever.
    ///
    /// This asks the opposite question to a full check. Not "what clashes?" but "is what
    /// I decided about still true?" — so it needs no broad phase at all. PairVerifier
    /// answers one pair at a time, which is why this stays affordable on a record with
    /// hundreds of decisions where a full sweep costs 12 s.
    ///
    /// Runs as an ExternalEvent because the record window is not API context, the same
    /// as selection and focus.
    /// </summary>
    public class RecordRecheckHandler : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }

        public void Execute(UIApplication app)
        {
            var sw = Stopwatch.StartNew();
            string summary;

            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc != null ? uidoc.Document : null;
                if (doc == null || !doc.IsValidObject)
                {
                    Report("Rechecking the record needs an open model.");
                    return;
                }

                var decided = ClashStatusStore.Instance.All();
                if (decided.Count == 0)
                {
                    Report("Nothing in the record to recheck.");
                    return;
                }

                var settings = ClashSettings.Current;
                var links = PairVerifier.LinkLookup(doc);

                var gone = new List<string>();
                var goneResults = new List<ClashResult>();
                int stillClashing = 0, unknown = 0, checkedCount = 0;

                foreach (var kv in decided)
                {
                    var entry = kv.Value;
                    if (entry == null || entry.Kind == ClashStatusKind.New) continue;

                    // A pair already observed gone stays gone until it comes back through
                    // the live path; re-verifying it every time would be work for nothing.
                    if (entry.Gone) continue;

                    var pair = FromKey(kv.Key, entry);
                    if (pair == null) continue;

                    checkedCount++;
                    ClashSeverity sev;
                    var verdict = PairVerifier.Verify(doc, pair, settings, links, out sev);

                    switch (verdict)
                    {
                        case PairVerifier.Verdict.Resolved:
                            gone.Add(kv.Key);
                            goneResults.Add(pair);
                            break;
                        case PairVerifier.Verdict.StillClashing:
                            stillClashing++;
                            break;
                        default:
                            // Unmeasurable is NOT resolved. Same rule as everywhere else:
                            // a pair we cannot answer for keeps its decision rather than
                            // being quietly retired.
                            unknown++;
                            break;
                    }
                }

                if (gone.Count > 0)
                {
                    // Note it against the decision BEFORE recording the resolution, so a
                    // pair that was accepted and has since been fixed reads as fixed —
                    // and shows as Reopened if it ever comes back.
                    ClashStatusStore.Instance.NoteGone(gone);
                    ResolvedLog.Add(goneResults);
                }

                // ---- and the RESOLUTIONS, which nothing has ever revisited
                int withdrawn, confirmed, unsure, reoccurred;
                AuditResolutions(doc, settings, links, out withdrawn, out confirmed, out unsure, out reoccurred);

                sw.Stop();
                summary = "Record recheck: " + checkedCount + " decided pair(s) re-tested in "
                        + sw.ElapsedMilliseconds + " ms — " + gone.Count + " now fixed, "
                        + stillClashing + " still clashing"
                        + (unknown > 0 ? ", " + unknown + " unmeasurable (left alone)" : "") + "."
                        + "  Resolutions: " + confirmed + " confirmed"
                        + (withdrawn > 0 ? ", " + withdrawn + " WITHDRAWN (never actually fixed)" : "")
                        + (reoccurred > 0 ? ", " + reoccurred + " clashing again since being fixed (left as fixed - see Reopened)" : "")
                        + (unsure > 0 ? ", " + unsure + " unmeasurable" : "") + ".";
                Log.Info(summary);
                Report(summary);
            }
            catch (Exception ex)
            {
                Log.Warn("Record recheck failed", ex);
                Report("Record recheck failed — see " + Log.Path);
            }
        }

        private void Report(string text)
        {
            if (Panel != null) Panel.SetStatus(text);
        }

        /// <summary>
        /// Re-test every RESOLUTION in the ledger and withdraw the ones that were never
        /// fixes.
        ///
        /// This is the half nothing else covers. The decisions above get looked at because
        /// somebody chose them; a resolved pair is by definition the thing nobody revisits,
        /// so a wrong entry there is permanent. And wrong entries exist: PairVerifier read
        /// the host's solids at Medium detail, found none on the ~43% of elements whose
        /// geometry only exists at Fine, and returned Resolved from a test that never ran.
        /// The background verifier filed each one as fixed. That is corrected going
        /// forward; this corrects what is already on disk.
        ///
        /// A pair that is STILL CLASHING and was NEVER SEEN TO REOCCUR (see below) was never
        /// fixed, so its occurrence is withdrawn. Resolved confirms it. Unknown is left
        /// completely alone — an unmeasurable pair is an unanswered question, and swapping
        /// one false claim for another would be no better than the bug.
        ///
        /// STILL CLASHING DOES NOT ALWAYS MEAN "NEVER FIXED". A pair can be fixed for real,
        /// then break again later — a genuinely different event, not evidence the ORIGINAL
        /// entry was wrong. This audit cannot tell the two apart from a bare re-verification
        /// alone, but ClashStatusStore already can, for a pair that had a decision:
        /// IsReopened is exactly "accepted or ignored, then seen gone, then seen again" — the
        /// live pipeline's own record of a genuine reoccurrence. Retracting the original
        /// Resolved entry in that case would erase a fix that genuinely happened; it is left
        /// alone (and counted separately) instead. An UNDECIDED pair carries no such record,
        /// so it keeps the older, narrower behaviour — still an improvement over retracting
        /// every reoccurrence, not a claim the ambiguity is fully resolved. Found reviewing
        /// the whole codebase, 2026-09-26.
        ///
        /// Cheap for the same reason the decisions pass is: PairVerifier asks about one
        /// known pair, so there is no broad phase and no index.
        /// </summary>
        private static void AuditResolutions(
            Document doc, ClashSettings settings, Dictionary<long, LinkContext> links,
            out int withdrawn, out int confirmed, out int unsure, out int reoccurred)
        {
            withdrawn = 0;
            confirmed = 0;
            unsure = 0;
            reoccurred = 0;

            try
            {
                // Standing resolutions only: an occurrence already withdrawn is settled,
                // and re-testing it every pass would be work for nothing.
                var standing = ResolvedLog.Snapshot();
                if (standing.Count == 0) return;

                var wrong = new List<ResolvedRecord>();

                foreach (var rec in standing)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.Key)) continue;

                    var pair = FromKey(rec.Key, rec.HostName, rec.HostCategory,
                                       rec.OtherName, rec.OtherCategory, rec.ModelName);
                    if (pair == null) continue;

                    ClashSeverity sev;
                    switch (PairVerifier.Verify(doc, pair, settings, links, out sev))
                    {
                        case PairVerifier.Verdict.StillClashing:
                            if (ClashStatusStore.Instance.IsReopened(rec.Key)) reoccurred++;
                            else wrong.Add(rec);   // it never stopped; the entry claiming otherwise is false
                            break;
                        case PairVerifier.Verdict.Resolved:
                            confirmed++;
                            break;
                        default:
                            unsure++;
                            break;
                    }
                }

                if (wrong.Count > 0)
                {
                    withdrawn = ResolvedLog.Retract(wrong);
                    Log.Warn("Record recheck WITHDREW " + withdrawn + " resolution(s): the pair is"
                           + " still clashing, so it was never fixed. Most likely written by the"
                           + " Medium-detail VerifyLink bug. The ledger keeps the withdrawal as"
                           + " its own line; nothing was rewritten.", null);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Auditing the resolved ledger failed; entries left as they were", ex);
            }
        }

        /// <summary>
        /// Rebuild a verifiable pair from a record key plus the detail the decision
        /// captured.
        ///
        /// The key IS the pair — ClashResult.MakeKey writes "host|linkInstance|other",
        /// with a zero link for a host-vs-host pair — so a decision can be re-tested
        /// even when it is nowhere in the live results, which is the whole reason this
        /// handler exists. Element NAMES come from the stored detail because the
        /// elements may since have been deleted.
        /// </summary>
        private static ClashResult FromKey(string key, ClashStatusEntry e)
        {
            return FromKey(key,
                e != null ? e.HostName : null, e != null ? e.HostCategory : null,
                e != null ? e.OtherName : null, e != null ? e.OtherCategory : null,
                e != null ? e.ModelName : null);
        }

        /// <summary>
        /// As above, from loose detail — the resolved ledger stores the same fields on its
        /// own record type rather than on a ClashStatusEntry. One key parser, two callers.
        /// </summary>
        private static ClashResult FromKey(string key, string hostName, string hostCat,
                                           string otherName, string otherCat, string model)
        {
            if (string.IsNullOrEmpty(key)) return null;

            var parts = key.Split('|');
            if (parts.Length != 3) return null;

            long host, link, other;
            if (!long.TryParse(parts[0], out host)) return null;
            if (!long.TryParse(parts[1], out link)) return null;
            if (!long.TryParse(parts[2], out other)) return null;

            return new ClashResult
            {
                Key = key,
                // IdUtil.From, not `new ElementId(...)`: before Revit 2024 there is no
                // long constructor and a bare `new ElementId(host)` binds to the
                // BuiltInParameter overload instead, which broke the 2022/2023 build.
                HostElementId = IdUtil.From(host),
                OtherElementId = IdUtil.From(other),
                LinkInstanceId = link == 0 ? null : IdUtil.From(link),
                HostElementName = hostName,
                OtherElementName = otherName,
                HostCategory = hostCat,
                OtherCategory = otherCat,
                ModelName = model,
                Severity = ClashSeverity.Hard
            };
        }

        public string GetName() { return "LiveClash.RecordRecheck"; }
    }
}
