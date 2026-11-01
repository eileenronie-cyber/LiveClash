using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// What a full check does with the pairs that were listed before it and that it no longer reports.
    ///
    /// Absence from a sweep is not the same as fixed (see FullCheckHandler.RecordResolved), so each
    /// missing pair is re-tested and sorted by the verdict. Revit-free: the re-test comes in as a
    /// function, so the sorting rules run in the headless gate.
    /// </summary>
    public sealed class UnreportedPairs
    {
        /// <summary>Listed before, not reported now.</summary>
        public int Missing { get; private set; }

        /// <summary>Verified gone: recorded as fixed.</summary>
        public List<ClashResult> Fixed { get; } = new List<ClashResult>();

        /// <summary>Still clashing, only outside the current scope: nothing to record or keep.</summary>
        public int OutOfScope { get; private set; }

        /// <summary>Could not be re-tested (most often a link that is not loaded): kept on the list.</summary>
        public List<ClashResult> Unmeasured { get; } = new List<ClashResult>();

        /// <summary>Listed before, not reported now, and their link was removed from the model: dropped, not kept.</summary>
        public List<ClashResult> Removed { get; } = new List<ClashResult>();

        /// <summary>
        /// Sort the rows of <paramref name="before"/> whose key is not among <paramref name="now"/>
        /// by what <paramref name="verify"/> says of each. A pair for which <paramref name="isLinkRemoved"/> says
        /// its link instance no longer exists is dropped without a re-test: there is nothing left to measure it
        /// against, and unlike a link that is only unloaded it will not come back (delta review 2026-10-31, DN1).
        /// </summary>
        public static UnreportedPairs Sort(
            IEnumerable<ClashResult> before, IEnumerable<ClashResult> now, Func<ClashResult, PairVerifier.Verdict> verify,
            Func<ClashResult, bool> isLinkRemoved = null)
        {
            var outcome = new UnreportedPairs();
            if (before == null) return outcome;

            var still = new HashSet<string>(StringComparer.Ordinal);
            if (now != null)
                foreach (var r in now)
                    if (r != null && !string.IsNullOrEmpty(r.Key)) still.Add(r.Key);

            foreach (var r in before)
            {
                if (r == null || string.IsNullOrEmpty(r.Key) || still.Contains(r.Key)) continue;
                outcome.Missing++;

                // Asked first. A lookup that cannot answer (it throws) is "not known to be removed": the pair
                // goes on to the re-test and, failing that, stays listed - a wrong drop would under-report.
                if (isLinkRemoved != null)
                {
                    bool gone;
                    try { gone = isLinkRemoved(r); }
                    catch (Exception ex)
                    {
                        // Stryker disable once String : log text, not behaviour
                        Log.Warn("Full check: could not tell whether the link of listed pair " + r.Key + " was removed; it stays listed", ex);
                        gone = false;
                    }
                    if (gone) { outcome.Removed.Add(r); continue; }
                }

                // A re-test that throws is one that could not answer: Unknown for THIS pair, not the end
                // of the step for every pair after it.
                PairVerifier.Verdict verdict;
                try { verdict = verify(r); }
                catch (Exception ex)
                {
                    // Stryker disable once String : log text, not behaviour
                    Log.Warn("Full check: could not re-test listed pair " + r.Key + "; it stays listed", ex);
                    verdict = PairVerifier.Verdict.Unknown;
                }

                if (verdict == PairVerifier.Verdict.Resolved) outcome.Fixed.Add(r);
                else if (verdict == PairVerifier.Verdict.StillClashing) outcome.OutOfScope++;
                else
                {
                    // Under-reporting is the one failure that must not happen: a pair nobody could measure
                    // stays on the list, as Unverified (review 2026-10-31, M2; Recheck already kept these).
                    // A copy, so the snapshot the caller holds keeps the severity it had.
                    var kept = r.Clone();
                    kept.Severity = ClashSeverity.Unverified;
                    outcome.Unmeasured.Add(kept);
                }
            }
            return outcome;
        }

        /// <summary>What the panel adds to the full check's summary for the pairs it kept and the ones it dropped
        /// because their link was removed: empty when there are none, so a full check that measured everything
        /// reads as before.</summary>
        public string Note()
        {
            return CheckOutcome.PairsNote(Unmeasured.Count, Removed.Count);
        }
    }

    /// <summary>The summary lines the checks put on the panel. Revit-free, so the headless gate holds
    /// each number to what happened (MATRIX C5).</summary>
    public static class CheckOutcome
    {
        /// <summary>
        /// The Recheck summary: <paramref name="after"/> is the rows listed after it,
        /// <paramref name="fixedCount"/> the pairs it verified fixed and <paramref name="newCount"/> the
        /// pairs it listed that were not listed before.
        /// </summary>
        /// <remarks>
        /// Not the drop in the total: that is a net count, so one fixed and one new read as nothing
        /// happening and the new clash went unsaid (review 2026-10-31, LOW-8). Each number is what
        /// happened.
        /// </remarks>
        public static string RecheckMessage(int elements, long ms, int after, int fixedCount, int newCount,
            int notChecked = 0, int removed = 0, IList<string> notCollected = null)
        {
            return "Rechecked " + elements + " element(s) in " + ms + " ms — "
                 + (fixedCount > 0 ? fixedCount + " resolved, " : "")
                 + (newCount > 0 ? newCount + " new, " : "")
                 + after + " clash(es) still live."
                 + PairsNote(notChecked, removed)
                 + NotCollectedNote(notCollected);
        }

        /// <summary>
        /// What a check says about the listed pairs it could not measure (<paramref name="kept"/>, still listed as
        /// Unverified) and the ones it dropped because their link was removed (<paramref name="removed"/>): the same
        /// words on the full check, on Recheck and on auto-verify. Empty when both are zero.
        /// </summary>
        public static string PairsNote(int kept, int removed)
        {
            var note = "";
            if (kept > 0)
                note += "  " + kept + " listed pair(s) could not be checked (a link not loaded, or not measurable)"
                      + " and stay listed as Unverified.";
            if (removed > 0)
                note += "  " + RemovedSentence(removed);
            return note;
        }

        /// <summary>"N pair(s) dropped: their link was removed from the model."</summary>
        public static string RemovedSentence(int removed)
        {
            return removed + " pair(s) dropped: their link was removed from the model.";
        }

        /// <summary>
        /// The severity a re-tested pair is put back at when the recheck's coarse pass dropped it: the verified one
        /// for a pair that still clashes, and Unverified for one nobody could re-test (a link not loaded). The
        /// row is still listed either way; what changes is that it no longer claims the old severity, which nothing
        /// measured (delta review 2026-10-31, DL1; the full check does the same since the M2 fix).
        /// </summary>
        public static ClashSeverity RestoredSeverity(PairVerifier.Verdict verdict, ClashSeverity listed, ClashSeverity verified)
        {
            if (verdict == PairVerifier.Verdict.StillClashing) return verified;
            return verdict == PairVerifier.Verdict.Unknown ? ClashSeverity.Unverified : listed;
        }
        /// <summary>
        /// What a check adds to its summary for a host or link whose elements could not be collected (LOW-12): empty when none.
        /// A collection that threw used to leave that link out of the sweep with only a log line, and the check ended as
        /// "checked and clean"; this says it was not checked and that the result is not a clean one.
        /// </summary>
        public static string NotCollectedNote(IList<string> names)
        {
            if (names == null || names.Count == 0) return "";
            return "  Not checked: " + string.Join(", ", names)
                 + " (its elements could not be read; see the log). Clashes there, if any, are not listed: this is not a clean result.";
        }

        /// <summary>Did a check leave part of the model or a link unread (so an empty result is not a clean one)? Chooses the empty-state banner.</summary>
        public static bool HasGaps(IList<string> notCollected)
        {
            return notCollected != null && notCollected.Count > 0;
        }

        /// <summary>
        /// The sentences that were said while nobody listened (ClashSettings.TakeUndeliveredNotices), as one status line, oldest first;
        /// a null or blank sentence is skipped; empty when there are none (round 8 delta review, L-4).
        /// </summary>
        public static string JoinNotices(IList<string> sentences)
        {
            var line = "";
            if (sentences == null) return line;
            foreach (var s in sentences)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                line += (line.Length > 0 ? "  " : "") + s;
            }
            return line;
        }

        /// <summary>
        /// What the panel says, between checks, about a host or link that could not be read: live checking against it is off
        /// until it reads again (LOW-12, live path). Empty when none.
        /// </summary>
        public static string StandingNote(IList<string> names)
        {
            if (names == null || names.Count == 0) return "";
            var note = "";
            foreach (var n in names)
                note += (note.Length > 0 ? "  " : "") + n + " could not be read: live checking against it is off until it reads again.";
            return note;
        }
    }


    /// <summary>
    /// Which collections failed (LOW-12). BuildContext collects the host's and each link's target elements through
    /// <see cref="Collect{T}"/>; a collector that throws used to leave that link out of the check with only a log line, and the check
    /// ended as "checked and clean". Revit-free, so a collector that throws can be tested headless.
    ///
    /// Two views of the same failures. The WINDOW of one check (<see cref="Begin"/> ... <see cref="End"/>: a full check or a recheck says
    /// what it did not check). And, for the live path, the caller's own UNREAD list: each live context keeps the names its build could not
    /// read (<see cref="Collect{T}"/>'s <c>unread</c>), and the panel shows the <see cref="Union"/> over the contexts that exist - so a
    /// failure belongs to the context that left the link out, goes with it when it is rebuilt or dropped, and is not hidden by a success
    /// somewhere else (round 8 delta review, L-2). Names are the caller's labels ("link 'X'"); the log scrubs link names.
    /// </summary>
    public static class CollectionFailures
    {
        private static readonly object Gate = new object();
        private static bool _recording;
        private static List<string> _window = new List<string>();

        /// <summary>A context that left something unread is asked again after this, not after the normal expiry.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

        /// <summary>Start recording the failures of one check (a full check or a recheck).</summary>
        public static void Begin()
        {
            lock (Gate) { _recording = true; _window = new List<string>(); }
        }

        /// <summary>Stop recording and return the distinct names that failed since <see cref="Begin"/>.</summary>
        public static List<string> End()
        {
            lock (Gate)
            {
                var r = _window;
                _window = new List<string>();
                _recording = false;
                return r;
            }
        }

        /// <summary>Record, in the current check's window, that <paramref name="what"/> could not be collected.</summary>
        public static void Note(string what)
        {
            if (string.IsNullOrEmpty(what)) return;
            lock (Gate)
            {
                if (_recording && !_window.Contains(what)) _window.Add(what);
            }
        }

        /// <summary>
        /// Run a collector. A throw is logged and returns null, and the failure is recorded in the current check's window and, when given,
        /// in the caller's own <paramref name="unread"/> list (once).
        /// </summary>
        public static T Collect<T>(string what, Func<T> collect, ICollection<string> unread = null) where T : class
        {
            try { return collect(); }
            catch (Exception ex)
            {
                // Stryker disable once String : log text, not behaviour
                Log.Warn("Collection failed: " + what + " - it will not be checked against.", ex);
                Note(what);
                if (unread != null && !unread.Contains(what)) unread.Add(what);
                return null;
            }
        }

        /// <summary>The distinct names across several contexts' unread lists, in the order first met. A null list counts as empty.</summary>
        public static List<string> Union(IEnumerable<IList<string>> lists)
        {
            var all = new List<string>();
            if (lists == null) return all;
            foreach (var list in lists)
            {
                if (list == null) continue;
                foreach (var name in list)
                    if (!string.IsNullOrEmpty(name) && !all.Contains(name)) all.Add(name);
            }
            return all;
        }

        /// <summary>How long a context lives: the normal expiry, or <see cref="RetryAfter"/> when it left something unread (if that is shorter).</summary>
        public static TimeSpan ExpiryFor(int unreadCount, TimeSpan normal)
        {
            return unreadCount > 0 && RetryAfter < normal ? RetryAfter : normal;
        }

        /// <summary>Forget the current check's window (tests).</summary>
        public static void Reset()
        {
            lock (Gate) { _recording = false; _window = new List<string>(); }
        }
    }

    /// <summary>
    /// The status line's memory of sentences said close together (round 8 second review, R2-2). A Save As that cannot copy several files says so
    /// once for each, in a row; the status line used to show only the last. A burst joins what <see cref="Add"/> is given within
    /// <see cref="Window"/> of the previous one, oldest first. It joins NOTICE sentences only: any other status (a Recheck result, a Full
    /// Check summary) goes through <see cref="Other"/>, replaces the line as before and ends the burst, so a stale sentence is never glued onto
    /// a fresh result. Revit-free, with the clock passed in, so the rule is tested headless.
    /// </summary>
    public sealed class NoticeBurst
    {
        /// <summary>A sentence joins the one before when it comes within this time of it.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        /// <summary>The most sentences kept in one line; the oldest go first.</summary>
        public const int Capacity = 8;

        private readonly object _gate = new object();
        private readonly List<string> _lines = new List<string>();
        private DateTime _lastUtc = DateTime.MinValue;

        /// <summary>A notice sentence: the whole line to show now, or null for a blank sentence (nothing to show).</summary>
        public string Add(string sentence, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(sentence)) return null;
            lock (_gate)
            {
                if (_lines.Count > 0 && nowUtc - _lastUtc > Window) _lines.Clear();   // too long after the previous one: a new line
                if (_lines.Count == 0 || !string.Equals(_lines[_lines.Count - 1], sentence, StringComparison.Ordinal))
                {
                    _lines.Add(sentence);
                    while (_lines.Count > Capacity) _lines.RemoveAt(0);
                }
                _lastUtc = nowUtc;
                return CheckOutcome.JoinNotices(_lines);
            }
        }

        /// <summary>Any other status line: shown as it is, and the burst ends.</summary>
        public string Other(string status)
        {
            lock (_gate) _lines.Clear();
            return status;
        }
    }
}
