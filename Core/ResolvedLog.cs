using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    // ResolvedRecord moved to ResolvedLogReplay.cs with the line format and replay rules
    // it belongs to — that file is Revit-free, so the headless gate can link it.

    /// <summary>
    /// A record of clashes that have been fixed.
    ///
    /// The panel already flashes a resolved row green for a few seconds, which tells
    /// you your edit landed — but it is gone a moment later, so there is no answer to
    /// "what have we actually cleared this week". This is that answer, and it is the
    /// natural other half of the status store: decisions are what people CHOSE,
    /// this is what the model now says was FIXED.
    ///
    /// Append-only, same as the status store and for the same reason — see
    /// <see cref="ClashStatusStore"/>. Written only from paths that have genuinely
    /// established a pair is gone:
    ///
    ///   - the background verifier and Recheck, which RE-TEST the pair;
    ///   - the full check's RecordResolved, which re-verifies anything that dropped
    ///     out of the sweep before believing it;
    ///   - a DELETED element, via ClashUpdater.FlushDeletedResolutions. No re-test is
    ///     possible or needed there — the element is gone, which is the verdict — but
    ///     the write waits until Idling so an undone or rolled-back delete never
    ///     reaches this file.
    ///
    /// NEVER from the updater's speculative removal before re-checking a modified
    /// element, which fires constantly and would fill this with noise. That remains
    /// the line: a removal is not a resolution.
    ///
    /// Every occurrence is kept rather than one row per pair: a clash that was fixed,
    /// came back and was fixed again is two pieces of history, and collapsing them
    /// would hide exactly the churn worth noticing.
    /// </summary>
    public static class ResolvedLog
    {
        private const char Sep = ResolvedLogReplay.Sep;

        private static readonly object Gate = new object();
        private static readonly List<ResolvedRecord> Records = new List<ResolvedRecord>();
        private static string _modelKey;

        /// <summary>This model's identity per <see cref="ModelIdentity"/>, or "" for a standalone
        /// or detached model. Carried into <see cref="ResolvedLogReplay.Header"/> so a FRESH file
        /// records it.</summary>
        private static string _identity = "";

        // The (title, identity) pair the collision guard last resolved a file for, and what it
        // resolved to — see ClashStatusStore.LoadFor's class comment for why LoadFor must skip
        // the guard's disk I/O when neither has changed since last time.
        private static string _guardTitleSeen, _guardIdentitySeen, _guardResolvedKey;

        /// <summary>Resolutions still standing — retracted occurrences are not fixes.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    int n = 0;
                    foreach (var r in Records) if (!r.Retracted) n++;
                    return n;
                }
            }
        }

        /// <summary>Occurrences withdrawn by <see cref="Retract"/>, for reporting.</summary>
        public static int RetractedCount
        {
            get
            {
                lock (Gate)
                {
                    int n = 0;
                    foreach (var r in Records) if (r.Retracted) n++;
                    return n;
                }
            }
        }

        public static string CurrentFile
        {
            get
            {
                lock (Gate)
                {
                    return string.IsNullOrEmpty(_modelKey)
                        ? null
                        : Path.Combine(ClashStatusStore.Folder, _modelKey + ".resolved.txt");
                }
            }
        }

        /// <summary>
        /// Newest first, with "is it back?" filled in from the live results.
        ///
        /// RETRACTED OCCURRENCES ARE EXCLUDED. A retraction says the entry was wrong when
        /// it was written — the pair never stopped clashing — so leaving it in the record
        /// would go on claiming a fix that never happened, which is the whole thing being
        /// corrected. They stay in the FILE (it is append-only, and the withdrawal is
        /// itself part of the history); they just stop counting as fixes.
        /// Use <see cref="SnapshotIncludingRetracted"/> to see them.
        /// </summary>
        public static List<ResolvedRecord> Snapshot()
        {
            var all = SnapshotIncludingRetracted();
            var live = new List<ResolvedRecord>(all.Count);
            foreach (var r in all) if (!r.Retracted) live.Add(r);
            return live;
        }

        /// <summary>Every occurrence, withdrawn ones included. For the correction pass
        /// and for anything auditing the ledger rather than reporting from it.</summary>
        public static List<ResolvedRecord> SnapshotIncludingRetracted()
        {
            var live = new HashSet<string>();
            foreach (var r in ClashResultStore.Instance.Snapshot())
                if (!string.IsNullOrEmpty(r.Key)) live.Add(r.Key);

            lock (Gate)
            {
                var copy = new List<ResolvedRecord>(Records);
                foreach (var r in copy) r.ClashingAgain = r.Key != null && live.Contains(r.Key);
                copy.Sort(delegate (ResolvedRecord a, ResolvedRecord b)
                {
                    return b.WhenUtc.CompareTo(a.WhenUtc);
                });
                return copy;
            }
        }

        /// <summary>
        /// Switch to a document, replaying its resolutions. Cheap to call repeatedly, on purpose —
        /// see ClashStatusStore.LoadFor's class comment: this runs on the same 5-second background
        /// tick, and an unchanged document must cost nothing beyond a couple of string compares.
        /// </summary>
        public static void LoadFor(Document doc)
        {
            RevitUser.Note(doc);

            string titleKey = doc == null ? null : ModelKey.FileSafe(doc);
            string identity = doc == null ? null : ModelIdentity.Compute(doc);
            string key;
            string[] titleLines = null;

            if (titleKey == _guardTitleSeen && identity == _guardIdentitySeen)
            {
                key = _guardResolvedKey;
            }
            else
            {
                key = titleKey;

                // The collision guard: see ModelIdentity's header comment (and its limits).
                if (!string.IsNullOrEmpty(titleKey) && !string.IsNullOrEmpty(identity))
                {
                    var titlePath = Path.Combine(ClashStatusStore.Folder, titleKey + ".resolved.txt");
                    bool exists = File.Exists(titlePath);
                    if (exists) titleLines = ModelIdentity.ReadLines(titlePath);   // retried — see its own comment
                    string recorded = titleLines == null ? "" : ModelIdentity.RecordedIn(titleLines);

                    string claim;
                    key = ModelIdentity.Resolve(titleKey, exists, recorded, identity, out claim);
                    if (claim != null) ModelIdentity.Claim(titlePath, claim);
                }

                _guardTitleSeen = titleKey;
                _guardIdentitySeen = identity;
                _guardResolvedKey = key;
            }

            lock (Gate)
            {
                if (_modelKey == key) return;
                _modelKey = key;
                _identity = identity ?? "";
                Records.Clear();
            }
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                var path = CurrentFile;
                if (path == null || !File.Exists(path)) return;

                int events, retracted;

                // Reuse the title file's lines already read above when the resolved key IS the
                // title key (the common, no-collision case).
                var lines = (key == titleKey && titleLines != null) ? titleLines : File.ReadAllLines(path);

                // The replay lives in ResolvedLogReplay: it is pure, so the ordering rule
                // that matters (a retraction appended BEFORE the occurrence it withdraws
                // must still apply) can be asserted headlessly instead of only ever being
                // exercised against somebody's real ledger.
                lock (Gate)
                    ResolvedLogReplay.Replay(lines, Records, out events, out retracted);

                Log.Info("Resolved log: replayed " + events + " event(s) -> " + Count
                         + " fix(es) for '" + key + "'."
                         + (retracted > 0
                            ? "  " + retracted + " occurrence(s) withdrawn on re-test and no"
                              + " longer counted as fixes."
                            : ""));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not load the resolved log", ex);
            }
        }

        /// <summary>
        /// Record clashes that have been re-tested and found gone. Call only from a
        /// path that actually re-tested them.
        /// </summary>
        public static void Add(IEnumerable<ClashResult> resolved)
        {
            if (resolved == null) return;

            var when = DateTime.UtcNow;
            string by = RevitUser.Current;

            var lines = new StringBuilder();
            int n = 0;

            lock (Gate)
            {
                foreach (var r in resolved)
                {
                    if (r == null || string.IsNullOrEmpty(r.Key)) continue;

                    var rec = new ResolvedRecord
                    {
                        Key = r.Key,
                        WhenUtc = when,
                        By = by,
                        HostId = IdUtil.Value(r.HostElementId),
                        HostName = Clean(r.HostElementName),
                        HostCategory = Clean(r.HostCategory),
                        OtherId = IdUtil.Value(r.OtherElementId),
                        OtherName = Clean(r.OtherElementName),
                        OtherCategory = Clean(r.OtherCategory),
                        ModelName = Clean(r.ModelName)
                    };
                    Records.Add(rec);

                    // ONE line writer, shared with the replay that reads it back.
                    lines.AppendLine(ResolvedLogReplay.Line(
                        rec, ResolvedLogReplay.Resolution, default(DateTime)));
                    n++;
                }
            }

            if (n == 0) return;
            Append(lines.ToString());

            var h = Changed;
            if (h != null) { try { h(null, EventArgs.Empty); } catch { } }
        }

        /// <summary>
        /// WITHDRAW occurrences that re-testing shows were never fixes.
        ///
        /// This exists because the ledger was written to by a path that could be wrong.
        /// PairVerifier.VerifyLink read the host's solids at MEDIUM detail, and roughly
        /// 43% of source elements here have no solids at Medium at all — so it measured
        /// nothing, concluded Resolved, and the background verifier duly filed the pair as
        /// fixed. The verifier is fixed; the entries it already wrote are still on disk,
        /// claiming fixes that never happened. Nothing else in the tool will ever revisit
        /// them, because a resolved pair is exactly what nobody looks at again.
        ///
        /// APPEND, NEVER REWRITE. The file is append-only by design (see the class
        /// comment) and that is not negotiable just because some of it is wrong: two
        /// writers rewriting a shared ledger is how triage gets silently lost, and the
        /// withdrawal is itself a fact worth keeping. So a retraction is another line, and
        /// the replay applies it afterwards.
        ///
        /// Call ONLY with occurrences a real re-test found still clashing. An unmeasurable
        /// pair is not a false entry — it is an unanswered question, and retracting it
        /// would trade one wrong claim for another.
        /// </summary>
        /// <returns>How many occurrences were withdrawn.</returns>
        public static int Retract(IEnumerable<ResolvedRecord> wrong)
        {
            if (wrong == null) return 0;

            var when = DateTime.UtcNow;
            var lines = new StringBuilder();
            int n = 0;

            lock (Gate)
            {
                foreach (var w in wrong)
                {
                    if (w == null || string.IsNullOrEmpty(w.Key)) continue;

                    // Withdraw the OCCURRENCE, not the pair. The same pair can appear many
                    // times and only one of those may be false, so match on the recorded
                    // time too — the identity the replay uses.
                    ResolvedRecord rec = null;
                    foreach (var r in Records)
                        if (r.Key == w.Key && r.WhenUtc == w.WhenUtc) { rec = r; break; }
                    if (rec == null || rec.Retracted) continue;

                    rec.Retracted = true;
                    rec.RetractedUtc = when;
                    lines.AppendLine(ResolvedLogReplay.Line(
                        rec, ResolvedLogReplay.Retraction, when));
                    n++;
                }
            }

            if (n == 0) return 0;
            Append(lines.ToString());

            var h = Changed;
            if (h != null) { try { h(null, EventArgs.Empty); } catch { } }
            return n;
        }

        /// <summary>Raised when records are added, so an open window can refresh.</summary>
        public static event EventHandler Changed;

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static void Append(string text)
        {
            // path, modelKey and identity as ONE consistent snapshot: a LoadFor for a different
            // document racing between two separate reads here could otherwise pair this model's
            // path with the NEXT model's key/identity in a freshly-stamped header.
            string path, modelKey, identity;
            lock (Gate)
            {
                modelKey = _modelKey;
                identity = _identity;
                path = string.IsNullOrEmpty(modelKey) ? null : Path.Combine(ClashStatusStore.Folder, modelKey + ".resolved.txt");
            }
            if (path == null) return;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        File.AppendAllText(path, ResolvedLogReplay.Header(modelKey, identity));
                    File.AppendAllText(path, text);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(20 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not write the resolved log", ex);
                    return;
                }
            }
        }
    }
}
