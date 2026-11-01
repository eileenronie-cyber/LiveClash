using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// The load and save rules for a per-model file that is REWRITTEN WHOLE on every save: the
    /// settings (<c>ClashSettings</c>) and the audit labels (<c>AuditLabelStore</c>). Unlike the
    /// append-only ledgers, a bad write here loses everything in the file, so the rules live in one
    /// place and are tested headless.
    ///
    ///   - A file that EXISTS but cannot be read (another session holding it, a permissions hiccup)
    ///     marks the store <see cref="Unreadable"/>. The caller runs on defaults meanwhile, and
    ///     <see cref="Save"/> refuses: writing those defaults would replace the user's real sets with
    ///     nothing (review 2026-10-31, M9).
    ///   - The file is READ AGAIN by <see cref="TryReopen"/>, which the caller calls on its own tick: at most
    ///     once per back-off step (5 s doubling to 60 s), with one read attempt, so a file that stays locked
    ///     costs next to nothing. Round 2 of the review found that the "next load" retry never ran for the
    ///     settings, because nothing loads them again within a session (DN2).
    ///   - The user is told once when the file goes unreadable, and once when it is read again
    ///     (<see cref="Notice"/>). Nothing about it is a surprise later.
    ///   - Writes are atomic (<see cref="LedgerFile.WriteWhole"/>).
    ///   - Save As carries the content to the new name (<see cref="CarryTo"/>, M4).
    /// </summary>
    public sealed class RewrittenFile
    {
        private readonly string _what;
        private RetryBackoff _backoff = new RetryBackoff();
        private bool _noticed;

        /// <summary>What this file is called in a notice: "Settings", "Audit labels".</summary>
        public RewrittenFile(string what = "This model's file", string resumeAdvice = null) { _what = what; _resumeAdvice = resumeAdvice ?? ""; }

        /// <summary>Added to the 'read again' notice: what the user should do now (the settings say to run a full check).</summary>
        private readonly string _resumeAdvice;

        /// <summary>
        /// Raised with a sentence for the user: once when a read first fails (changes are paused and the file
        /// is not overwritten) and once when it is read again. Never on a repeated failed retry.
        /// </summary>
        public event Action<string> Notice;

        /// <summary>The file this store reads and writes now. Null before any model.</summary>
        public string Path { get; private set; }

        /// <summary>The file exists but could not be read. Saving is refused until a read succeeds.</summary>
        public bool Unreadable { get; private set; }

        /// <summary>Must <see cref="Open"/> run for <paramref name="path"/>? Yes for a new file, and yes
        /// again for the same file while its last read failed, so the failure does not stick.</summary>
        public bool NeedsOpen(string path)
        {
            return path != Path || Unreadable;
        }

        /// <summary>
        /// Point at <paramref name="path"/> and read it. <paramref name="preRead"/> is lines the caller
        /// already read from that same path (null when it read nothing). Returns the lines, or null when
        /// there is nothing to use (the caller then starts from defaults); after a null, check
        /// <see cref="Unreadable"/> to tell "no file yet" from "could not read it".
        /// </summary>
        public string[] Open(string path, string[] preRead)
        {
            bool sameFile = path == Path;
            bool wasUnreadable = Unreadable && sameFile;
            if (!sameFile) { _backoff = new RetryBackoff(); _noticed = false; }
            Path = path;
            Unreadable = false;

            string[] lines = preRead;
            var state = LedgerFile.ReadState.Read;
            if (lines == null) lines = LedgerFile.Read(path, out state);

            if (state == LedgerFile.ReadState.Failed)
            {
                Unreadable = true;
                _backoff.Failed();
                if (!wasUnreadable)   // once, not on every retry
                    Log.Warn("Could not read " + path + ": running on defaults, and NOT saving over it until it can be read.");
                if (!_noticed) { _noticed = true; Say(_what + " for this model could not be read, so the defaults are in use and changes are paused (the file is not overwritten). It is tried again every few seconds."); }
            }
            else
            {
                _backoff.Succeeded();
                if (wasUnreadable) Recovered();
            }
            return lines;
        }

        /// <summary>
        /// The tick's retry of a file that could not be read. Does nothing unless the file is marked unreadable
        /// and the back-off is due; then makes ONE read attempt. Returns the lines when it read the file
        /// (the caller applies them), otherwise null. Clears <see cref="Unreadable"/> and says so when it reads.
        /// </summary>
        public string[] TryReopen()
        {
            if (!Unreadable || string.IsNullOrEmpty(Path) || !_backoff.Due()) return null;

            LedgerFile.ReadState state;
            var lines = LedgerFile.Read(Path, out state, 1);
            if (state == LedgerFile.ReadState.Failed)
            {
                _backoff.Failed();
                return null;
            }

            Unreadable = false;
            _backoff.Succeeded();
            Recovered();
            return lines;
        }

        private void Recovered()
        {
            _noticed = false;
            Log.Info(_what + " for this model was read again.");
            Say(_what + " for this model was read again; changes resume." + _resumeAdvice);
        }

        private void Say(string text)
        {
            var h = Notice;
            if (h != null) { try { h(text); } catch { } }
        }

        /// <summary>Write <paramref name="lines"/> as the whole file. Refused while <see cref="Unreadable"/>.</summary>
        public bool Save(IList<string> lines)
        {
            if (string.IsNullOrEmpty(Path)) return false;
            if (Unreadable)
            {
                Log.Warn("Not saved: " + Path + " could not be read when it was loaded, and saving now would replace it with defaults.");
                return false;
            }
            return LedgerFile.WriteWhole(Path, lines);
        }

        /// <summary>A copy in the same state (its notice handlers too), for a check that must put a live store back.</summary>
        internal RewrittenFile Copy()
        {
            var c = new RewrittenFile(_what, _resumeAdvice) { Path = Path, Unreadable = Unreadable, _backoff = _backoff.Copy(), _noticed = _noticed };
            c.Notice = Notice;
            return c;
        }

        /// <summary>
        /// The model was saved under a new name: write what it holds now under <paramref name="newPath"/>
        /// and use that from now on. A file already at the new name (Save As over an existing file) is
        /// kept beside it as <c>.bak</c>: the file on disk is this model now, but what it replaces may
        /// be someone's work. Refused while <see cref="Unreadable"/>: defaults are not this model's
        /// settings, and carrying them would plant them under the new name.
        /// </summary>
        public bool CarryTo(string newPath, IList<string> lines)
        {
            if (string.IsNullOrEmpty(newPath) || lines == null || Unreadable) return false;
            if (!LedgerFile.WriteWhole(newPath, lines, newPath + ".bak")) return false;
            Path = newPath;
            return true;
        }

        /// <summary>
        /// SP7 (round 8). The model was saved under a new name while this file was flagged <see cref="Unreadable"/>: there are no parsed
        /// lines to carry (<see cref="CarryTo"/> refuses, so defaults are never planted), but the FILE can still be copied as it is, whatever
        /// it holds (a corrupt file included), with the new model identity appended. A file already at the new name is kept as .bak. The
        /// store points at the copy and STAYS flagged: its own retry (<see cref="TryReopen"/>) reads the copy when the back-off is due, says so,
        /// and the rules reset follows. Returns false, having changed nothing, when the source is gone or the copy cannot be made (a lock that
        /// stops the read stops the copy too); the caller then tells the user.
        /// </summary>
        public bool CarryUnreadableTo(string newPath, string identity)
        {
            if (string.IsNullOrEmpty(newPath) || string.IsNullOrEmpty(Path) || !Unreadable) return false;
            if (!System.IO.File.Exists(Path)) return false;
            if (!LedgerFile.CopyOver(Path, newPath, identity)) return false;
            Path = newPath;
            return true;
        }
    }
}
