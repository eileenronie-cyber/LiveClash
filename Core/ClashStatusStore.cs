using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Per-model store of review decisions, persisted beside the log.
    ///
    /// APPEND-ONLY, and that is not a detail. Writing the whole file on every change
    /// means two writers silently clobber each other — a second Revit session, a
    /// crash mid-write, or a colleague on a shared folder, and someone's triage is
    /// simply gone. This project has already had that bug once in its other clash
    /// tooling and fixed it exactly this way. Each decision is one appended line;
    /// load replays them and the latest timestamp per key wins. Concurrency becomes
    /// safe, and an audit trail falls out for free.
    ///
    /// Row key is <see cref="ClashResult.Key"/>, built from ElementIds and therefore
    /// stable across sessions. Deleting and redrawing an element loses its status —
    /// correct, because that is a different element.
    ///
    /// The MODEL key comes from <see cref="ModelKey"/>, never doc.Title directly.
    /// </summary>
    public class ClashStatusStore
    {
        public static ClashStatusStore Instance { get; } = new ClashStatusStore();

        // The line format belongs to ClashStatusReplay, which reads it. Aliased rather
        // than redeclared so the writer here can never drift from the reader there.
        private const string GoneEvent = ClashStatusReplay.GoneEvent;
        private const char Sep = ClashStatusReplay.Sep;

        private readonly object _gate = new object();
        private readonly Dictionary<string, ClashStatusEntry> _byKey =
            new Dictionary<string, ClashStatusEntry>();

        private string _modelKey;

        /// <summary>This model's identity per <see cref="ModelIdentity"/>, or "" for a standalone
        /// or detached model. Carried into <see cref="Header"/> so a FRESH file records it.</summary>
        private string _identity = "";

        // The (title, identity) pair the collision guard last resolved a file for, and what it
        // resolved to. Lets LoadFor skip the guard's disk I/O entirely when neither has changed
        // since last time — see the class comment on LoadFor for why that matters.
        private string _guardTitleSeen, _guardIdentitySeen, _guardResolvedKey;

        /// <summary>Raised on the main thread whenever a decision changes.</summary>
        public event EventHandler Changed;

        public static string Folder
        {
            get
            {
                var dir = Path.Combine(
                    LocalState.Root,   // the real local application data, or a dev job's scratch root (DM2)
                    "LiveClash", "status");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>The key (ClashEngine.DocKey) of the document these were last loaded for. A Save As carries them to the new
        /// name only if this is the document being saved (LedgerProvenance, round 3b of the 2026-10-31 review).</summary>
        public string LoadedForDocKey { get; private set; }

        public string CurrentFile
        {
            get
            {
                lock (_gate)
                {
                    return string.IsNullOrEmpty(_modelKey)
                        ? null
                        : Path.Combine(Folder, _modelKey + ".txt");
                }
            }
        }

        public int Count { get { lock (_gate) return _byKey.Count; } }

        public ClashStatusKind Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return ClashStatusKind.New;
            lock (_gate)
            {
                ClashStatusEntry e;
                return _byKey.TryGetValue(key, out e) ? e.Kind : ClashStatusKind.New;
            }
        }

        public ClashStatusEntry Entry(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_gate)
            {
                ClashStatusEntry e;
                return _byKey.TryGetValue(key, out e) ? e : null;
            }
        }

        /// <summary>A snapshot of every current decision, keyed by <see cref="ClashResult.Key"/>.</summary>
        public List<KeyValuePair<string, ClashStatusEntry>> All()
        {
            lock (_gate) return new List<KeyValuePair<string, ClashStatusEntry>>(_byKey);
        }

        public string NoteOf(string key)
        {
            var e = Entry(key);
            return e == null ? null : e.Note;
        }

        /// <summary>
        /// The pair carries a decision, was later seen to stop clashing, and is being
        /// asked about again — so it has come back after being fixed.
        ///
        /// This is why a decision is not simply reapplied: something you accepted,
        /// then fixed, then re-broke is a NEW situation, and silently re-hiding it
        /// under the old decision would bury it. Stays flagged until re-triaged.
        /// </summary>
        public bool IsReopened(string key)
        {
            var e = Entry(key);
            return e != null && e.Gone
                && (e.Kind == ClashStatusKind.Coordinated || e.Kind == ClashStatusKind.Ignored);
        }

        // ------------------------------------------------------------------ writing

        public void Set(IEnumerable<string> keys, ClashStatusKind kind)
        {
            Set(keys, kind, null);
        }

        /// <summary>
        /// Attach a note WITHOUT changing status — each key keeps its OWN current decision,
        /// including "New". <see cref="Set"/> takes one kind for every key, which is correct when
        /// the caller really is marking a uniform decision (Coordinated/Ignored, right after the
        /// click that made it) but wrong for the plain Note button: a GROUP header's Status is the
        /// worst status among its members (see CsvExport's StatusMix for the same rule stated
        /// elsewhere), so passing it into Set for every member downgraded already-decided ones back
        /// to whatever the worst one was. Found reviewing the whole codebase, 2026-09-26.
        /// </summary>
        public void SetNote(IEnumerable<string> keys, string note)
        {
            if (keys == null) return;
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue;
                Set(new[] { k }, Get(k), note);
            }
        }

        /// <summary>
        /// Record a decision. <paramref name="note"/> null leaves any existing note
        /// alone; empty string clears it.
        /// </summary>
        public void Set(IEnumerable<string> keys, ClashStatusKind kind, string note)
        {
            if (keys == null) return;

            // Enrich with element detail from the live results, so a decided pair still
            // reads with names in the record after it stops clashing. A pair not currently
            // live keeps whatever detail an earlier line captured.
            var live = new Dictionary<string, ClashResult>();
            foreach (var r in ClashResultStore.Instance.Snapshot())
                if (!string.IsNullOrEmpty(r.Key) && !live.ContainsKey(r.Key)) live[r.Key] = r;

            var lines = new List<string>();
            var when = DateTime.UtcNow;
            var by = SafeUser();
            bool dirty = false;

            lock (_gate)
            {
                foreach (var k in keys)
                {
                    if (string.IsNullOrEmpty(k)) continue;

                    ClashStatusEntry existing;
                    _byKey.TryGetValue(k, out existing);

                    var keptNote = note ?? (existing != null ? existing.Note : null);
                    if (existing != null && existing.Kind == kind && !existing.Gone
                        && string.Equals(existing.Note ?? "", keptNote ?? "", StringComparison.Ordinal))
                        continue;   // nothing actually changed

                    ClashResult r;
                    live.TryGetValue(k, out r);

                    if (kind == ClashStatusKind.New && string.IsNullOrEmpty(keptNote))
                    {
                        // "New" with no note is the absence of anything worth remembering. The
                        // event is still appended so other readers see the clearing, but nothing
                        // is held in memory - matching what a reload would produce.
                        _byKey.Remove(k);
                    }
                    else
                    {
                        var e = new ClashStatusEntry
                        {
                            Kind = kind,
                            WhenUtc = when,
                            By = by,
                            Note = keptNote,
                            // A fresh decision supersedes an earlier "it went away".
                            Gone = false
                        };
                        CopyDetail(e, r, existing);
                        _byKey[k] = e;
                    }
                    lines.Add(Line(k, kind.ToString(), when, by, keptNote, r));
                    dirty = true;
                }
            }

            if (!dirty) return;
            Append(lines);
            Raise();
        }

        /// <summary>Take element detail from the live result if present, else carry forward
        /// whatever an earlier decision had captured.</summary>
        private static void CopyDetail(ClashStatusEntry into, ClashResult r, ClashStatusEntry prev)
        {
            if (r != null)
            {
                into.HostId = IdUtil.Value(r.HostElementId);
                into.HostName = r.HostElementName;
                into.HostCategory = r.HostCategory;
                into.OtherId = IdUtil.Value(r.OtherElementId);
                into.OtherName = r.OtherElementName;
                into.OtherCategory = r.OtherCategory;
                into.ModelName = r.ModelName;
            }
            else if (prev != null && prev.HasDetail)
            {
                into.HostId = prev.HostId; into.HostName = prev.HostName; into.HostCategory = prev.HostCategory;
                into.OtherId = prev.OtherId; into.OtherName = prev.OtherName; into.OtherCategory = prev.OtherCategory;
                into.ModelName = prev.ModelName;
            }
        }

        /// <summary>
        /// Record that these pairs have stopped clashing.
        ///
        /// Only pairs that carry a decision are written — there is nothing to say
        /// about an undecided clash that simply got fixed, and writing one would grow
        /// the file for no reason. Call this only from a path that has genuinely
        /// re-tested the pair, never from the speculative removal the updater does
        /// before re-checking an element.
        /// </summary>
        public void NoteGone(IEnumerable<string> keys)
        {
            if (keys == null) return;

            var lines = new List<string>();
            var when = DateTime.UtcNow;
            var by = SafeUser();

            lock (_gate)
            {
                foreach (var k in keys)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    ClashStatusEntry e;
                    if (!_byKey.TryGetValue(k, out e)) continue;   // no decision to preserve
                    if (e.Gone) continue;                          // already recorded

                    e.Gone = true;
                    e.GoneUtc = when;
                    lines.Add(Line(k, GoneEvent, when, by, null));
                }
            }

            if (lines.Count > 0) Append(lines);
        }

        private static string Line(string key, string ev, DateTime when, string by, string note)
        {
            return Line(key, ev, when, by, note, null);
        }

        /// <summary>
        /// One event, TAB-separated.
        ///
        /// BUILT WITH string.Join OVER STRINGS, deliberately, the same way ResolvedLog
        /// does it. The previous version concatenated with `+` and Sep is a CHAR, so
        /// `Sep + IdUtil.Value(...)` was char + long — INTEGER ADDITION, not
        /// concatenation. Tab is 9, so the host id was written 18 too high and ran into
        /// the host name with both separators swallowed:
        ///
        ///     ...ADMIN\t74691449LV Ladder [74691431]\tCable Trays...
        ///                ^^^^^^^^ 74691431 + 9 + 9, glued to the name
        ///
        /// That produced ten fields instead of twelve, put element text where the note
        /// belongs, and — worse than the display — stored an id no element has, so
        /// clicking a historical row selected the wrong thing or nothing. Only the first
        /// two fields were damaged: once the expression became a string, the rest
        /// concatenated correctly, which is why it looked like a cosmetic column bug.
        ///
        /// Joining an explicitly-typed string[] makes the mistake unrepresentable.
        /// </summary>
        private static string Line(string key, string ev, DateTime when, string by, string note, ClashResult r)
        {
            var head = new[]
            {
                key,                                            // written raw — its pipes are part of it
                ev,
                when.ToString("o", CultureInfo.InvariantCulture),
                Clean(by),
                Clean(note)
            };

            // Element detail, appended so older 5-field lines still parse. Only for real
            // decisions that had a live result to read from.
            if (r == null) return string.Join(Sep.ToString(), head);

            var tail = new[]
            {
                IdUtil.Value(r.HostElementId).ToString(CultureInfo.InvariantCulture),
                Clean(r.HostElementName),
                Clean(r.HostCategory),
                IdUtil.Value(r.OtherElementId).ToString(CultureInfo.InvariantCulture),
                Clean(r.OtherElementName),
                Clean(r.OtherCategory),
                Clean(r.ModelName)
            };

            return string.Join(Sep.ToString(), head) + Sep + string.Join(Sep.ToString(), tail);
        }

        /// <summary>Tabs and newlines would break the one-line-per-event format.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string SafeUser()
        {
            return RevitUser.Current;
        }

        /// <summary>
        /// Append with a short retry: another Revit session may hold the file for the
        /// instant it takes to write its own line.
        /// </summary>
        private void Append(List<string> lines)
        {
            var path = CurrentFile;
            if (path == null || lines == null || lines.Count == 0) return;

            var text = new StringBuilder();
            foreach (var l in lines) text.AppendLine(l);

            // Retried, header on a fresh file, and a newline first after a half-written last line
            // (review 2026-10-31, LOW-3) - see LedgerFile.Append. It logs its own failure.
            LedgerFile.Append(path, Header(), text.ToString());
        }

        /// <summary>
        /// The lines of the current ledger file, for the readers of the model events (<see cref="LedgerExtras.Read"/>) and the decision replay;
        /// null when there is no file for this model or it cannot be read.
        /// </summary>
        internal List<string> ReadLines()
        {
            var path = CurrentFile;
            if (path == null) return null;
            LedgerFile.ReadState state;
            var lines = LedgerFile.Read(path, out state);
            return lines == null ? null : new List<string>(lines);
        }

        /// <summary>
        /// Appends already-formatted event lines (the clash schedule's model events, and the decision lines copied from the markers) through the
        /// same write as every other event, and folds them into the decisions held in memory as a reload would, so the panel shows them at
        /// once. Nothing happens for an empty list.
        /// </summary>
        internal void AppendLines(IEnumerable<string> lines)
        {
            if (lines == null) return;
            var list = new List<string>();
            foreach (var l in lines) if (!string.IsNullOrEmpty(l)) list.Add(l);
            if (list.Count == 0) return;
            Append(list);
            lock (_gate)
            {
                int events, legacy, repaired;
                ClashStatusReplay.Replay(list, _byKey, out events, out legacy, out repaired);
            }
            Raise();
        }

        private string Header()
        {
            lock (_gate)
            {
                var id = string.IsNullOrEmpty(_identity) ? "" : ModelIdentity.Line(_identity) + Environment.NewLine;
                return "# LiveClash status — append-only, one event per line (TAB-separated)" + Environment.NewLine
                     + "# key\tevent\tutc\tby\tnote\t[hostId\thostName\thostCat\totherId\totherName\totherCat\tmodel]" + Environment.NewLine
                     + "# model=" + _modelKey + Environment.NewLine
                     + id;
            }
        }

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// Switch to a document, replaying its decisions. Safe to call repeatedly — and cheap to,
        /// on purpose: this runs on the background verifier's 5-second tick for the active
        /// document (LiveClashApp.TryVerifySlice), not just on document-open. An unchanged
        /// document must cost nothing beyond a couple of string compares, the same as before the
        /// collision guard existed — so the guard's own disk I/O (below) only runs when the
        /// (title, identity) PAIR actually changed since the call before; the fast path is
        /// answered from the last time it did.
        /// </summary>
        public void LoadFor(Document doc)
        {
            RevitUser.Note(doc);
            if (doc != null) LoadedForDocKey = ClashEngine.DocKey(doc);

            string titleKey, identity;
            string[] titleLines;
            string key = ResolveKey(doc, out titleKey, out identity, out titleLines);
            Load(key, identity, titleKey, titleLines);
        }

        /// <summary>
        /// The file key a document's decisions live under: its title, unless the collision guard gives
        /// it a key of its own. <paramref name="titleLines"/> is the title file when the guard read it.
        /// </summary>
        private string ResolveKey(Document doc, out string titleKey, out string identity, out string[] titleLines)
        {
            titleKey = doc == null ? null : ModelKey.FileSafe(doc);
            identity = doc == null ? null : ModelIdentity.Compute(doc);
            titleLines = null;
            string key;

            if (titleKey == _guardTitleSeen && identity == _guardIdentitySeen)
            {
                key = _guardResolvedKey;
            }
            else
            {
                key = titleKey;

                // The collision guard: two DIFFERENT workshared models sharing a title must not
                // share this file. See ModelIdentity's header comment for the full rule (and its
                // limits).
                if (!string.IsNullOrEmpty(titleKey) && !string.IsNullOrEmpty(identity))
                {
                    var titlePath = Path.Combine(Folder, titleKey + ".txt");
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
            if (!string.IsNullOrEmpty(titleKey)) Log.NoteSensitive(titleKey);
            if (!string.IsNullOrEmpty(key)) Log.NoteSensitive(key);
            return key;
        }

        /// <summary>
        /// The file of the last load could not be read (another session held it past the retry). The
        /// decisions in memory are then missing, Gone events are not written for them, and reopened
        /// clashes stay hidden - so the next LoadFor for the same model reads it again instead of
        /// answering "already loaded" for the rest of the session (review 2026-10-31, LOW-12).
        /// </summary>
        private bool _loadFailed;

        // How soon a failed read is tried again. A ledger that stays locked used to cost its full retry (about 300 ms)
        // on EVERY 5-second tick, on the UI thread (review 2026-10-31 round 2, DL9).
        private readonly RetryBackoff _retryBackoff = new RetryBackoff();

        private void Load(string key, string identity, string titleKey, string[] titleLines)
        {
            bool retry;
            lock (_gate)
            {
                // NOTE: a _rawTitle field used to be captured here, to "make title drift
                // visible in the file" — but Header() only ever emitted _modelKey, so it
                // recorded nothing and no one read it. Removed rather than left looking
                // like a working diagnostic. If recording the raw doc.Title is wanted (it
                // is relevant to how detached/local suffixes fork a ledger), the missing
                // half is in Header(), not here.
                if (_modelKey == key && !_loadFailed) return;   // same model, nothing to reload
                if (_modelKey == key && !_retryBackoff.Due()) return;   // failed recently: not yet
                retry = _modelKey == key;
                if (!retry)
                {
                    _modelKey = key;
                    _identity = identity ?? "";
                    _byKey.Clear();
                    _retryBackoff.Succeeded();
                }
                // A retry replays INTO what is held: anything decided since was appended to the same
                // file (or, if that failed too, is still only here), and the replay's clock rule merges.
                _loadFailed = false;
            }

            if (string.IsNullOrEmpty(key)) return;

            try
            {
                var path = CurrentFile;
                int events, legacy, repaired;

                // Reuse the title file's lines already read above when the resolved key IS the
                // title key (the common, no-collision case) — the collision path reads its own,
                // different file. A failed guard read leaves them null and the file is read here.
                var state = LedgerFile.ReadState.Read;
                var lines = (key == titleKey && titleLines != null) ? titleLines : LedgerFile.Read(path, out state, retry ? 1 : 5);   // a retry on a tick makes one attempt
                if (lines == null)
                {
                    if (state == LedgerFile.ReadState.Failed)
                    {
                        lock (_gate) { _loadFailed = true; _retryBackoff.Failed(); }
                        if (!retry) Log.Warn("Clash status: could not read the file for this model; its decisions are missing until it can be read, tried again on the next load.");
                    }
                    else Log.Info("Clash status: no file yet for this model.");
                    return;
                }

                // The replay itself lives in ClashStatusReplay: it is pure, and the
                // ordering rules in it are the part that has repeatedly been wrong, so it
                // is worth being able to assert without a file. What stays here is what
                // genuinely belongs to a store — the path, the lock and the event.
                lock (_gate)
                {
                    _retryBackoff.Succeeded();   // under the lock Failed() runs under (delta review round 2, DL-k)
                    ClashStatusReplay.Replay(
                        lines, _byKey, out events, out legacy, out repaired);
                }

                Log.Info("Clash status: replayed " + events + " event(s) -> "
                         + Count + " decision(s) for '" + key + "'."
                         + (retry ? " (read on a retry)" : "")
                         + (repaired > 0
                            ? "  " + repaired + " line(s) repaired on read (char+long separator bug; "
                              + "host ids were stored 18 too high and glued to the name)."
                            : ""));
                if (retry) Raise();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not load clash status", ex);
            }
        }

        /// <summary>
        /// The ACTIVE model was saved under a new name (Save As). Its decisions go with it: the file is
        /// carried to the new key (LedgerFile.CopyOver: atomic, and a file already at the new name is
        /// kept as .bak, never merged), and what is held in memory stays. Before this the store
        /// re-keyed to an empty file and every decision vanished from the panel (review 2026-10-31,
        /// M4). Falls back to an ordinary load of the new name when the file cannot be carried, or was
        /// never read.
        /// </summary>
        [RevitBound]
        public void FollowSaveAs(Document doc)
        {
            if (doc != null) LoadedForDocKey = ClashEngine.DocKey(doc);   // ModelLedgers.FollowSaveAs has shown these are the saved document's
            string titleKey, identity;
            string[] titleLines;
            string key = ResolveKey(doc, out titleKey, out identity, out titleLines);
            string from;
            bool failed;
            lock (_gate)
            {
                if (string.IsNullOrEmpty(key) || key == _modelKey) return;
                from = CurrentFile;
                failed = _loadFailed;
            }
            var dest = Path.Combine(Folder, key + ".txt");

            if (!failed)
            {
                if (from != null && SaveAsCarry.CarryReadable(from, dest, identity, "triage", ClashSettings.SayToPanel))
                {
                    lock (_gate)
                    {
                        _modelKey = key;
                        _identity = identity ?? "";
                    }
                    Log.Info("Clash status: " + Count + " decision(s) carried to the model's new name after Save As.");
                    return;
                }
            }
            else if (SaveAsCarry.CarryUnreadable(from, dest, identity, "triage", ClashSettings.SayToPanel))
            {
                // SP7 (round 8): the load had failed, so the decisions are not in memory; the FILE is copied as it is. The store stays flagged,
                // and the next LoadFor of the new name reads the copy (the back-off decides when).
                lock (_gate)
                {
                    _modelKey = key;
                    _identity = identity ?? "";
                }
                Log.Info("Clash status: the unreadable triage file was copied to the model's new name after Save As; it is read again on the retry.");
                return;
            }
            Load(key, identity, titleKey, titleLines);
        }

        private void Raise()
        {
            var h = Changed;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }
    }
}
