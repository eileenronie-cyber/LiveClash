using System;
using System.Collections.Generic;
using System.IO;

namespace LiveClash.Core
{
    /// <summary>
    /// The pure rules behind a Save As, in one place (LiveClash review-fix round 8, "files" builder):
    ///  - DL-f: which key the saved document had BEFORE the save;
    ///  - D3-2(b)(c): the link pairs held for an undo follow the document to its new key, and are let go when it closes;
    ///  - SP7: a store whose file could not be read does not abandon that file's content at Save As, and when the content
    ///    cannot be carried the user is told.
    /// Revit-free, so each rule is asserted headlessly. The hooks into the add-in's event handlers are one line each.
    /// </summary>
    public static class SaveAsCarry
    {
        /// <summary>
        /// DL-f. The key the saved document had before the save. The add-in captures <c>ClashEngine.DocKey(doc)</c> in DocumentSavingAs
        /// (for every document, not only a never-saved one): it is the very function that produced the stores' loaded-for key and the
        /// queue's keys, so both sides are spelled alike. The event's <c>OriginalPath</c> can be spelled differently (cloud, UNC, a mapped
        /// drive) and the carry was then refused; it is only the fallback now. Both empty gives "", which the provenance rule refuses.
        /// </summary>
        public static string FromKey(string captured, string originalPath)
        {
            if (!string.IsNullOrWhiteSpace(captured)) return captured;
            return originalPath ?? "";
        }

        /// <summary>
        /// D3-2(b). The document was saved under a new name: its held link rows (DroppedLinkRows) move to the new key, grouped by link
        /// instance as before, so an undo of the deletion still lists the pairs again. Rows already held under the new key are merged (a pair
        /// is held once). Uses only DroppedLinkRows's public members. The moved copies are listed as Unverified when they come back,
        /// which is how they come back anyway. Returns how many rows moved; nothing for a missing key or the same key. Never throws.
        /// </summary>
        public static int RekeyHeldRows(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.Ordinal)) return 0;
            try
            {
                if (DroppedLinkRows.Count(from) == 0) return 0;

                // First pass: learn which instances hold rows (nothing "exists", so nothing leaves).
                var ids = new List<long>();
                DroppedLinkRows.TakeBack(from, id => { ids.Add(id); return false; });

                int moved = 0;
                foreach (var id in ids)
                {
                    long wanted = id;
                    var rows = DroppedLinkRows.TakeBack(from, x => x == wanted);
                    if (rows.Count == 0) continue;
                    DroppedLinkRows.Hold(to, wanted, rows);
                    moved += rows.Count;
                }
                return moved;
            }
            catch (Exception ex)
            {
                Log.Warn("Save As: could not move the held link pairs to the new name", ex);
                return 0;
            }
        }

        /// <summary>D3-2(c). The document closed: its held link rows go with it, so a reopen (the link instance is back) cannot say
        /// "their link came back" about a model just opened.</summary>
        public static void ForgetHeldRows(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return;
            DroppedLinkRows.Forget(docKey);
        }

        /// <summary>
        /// SP7, a whole-file store (the settings, the audit labels) that is flagged unreadable at Save As. The parsed content is not there to
        /// carry, so the FILE is copied as it is (<see cref="RewrittenFile.CarryUnreadableTo"/>); the store then points at the copy and its
        /// own retry reads it. When the copy cannot be made (a lock blocks a copy too) nothing is carried, and the user is TOLD through
        /// <paramref name="say"/>: the new name starts on the defaults and the original is untouched. Returns whether the content was carried.
        /// </summary>
        public static bool CarryUnreadable(RewrittenFile file, string newPath, string identity, string what, Action<string> say)
        {
            bool carried = false;
            try { carried = file != null && file.CarryUnreadableTo(newPath, identity); }
            catch (Exception ex) { Log.Warn("Save As: could not carry the unreadable " + what + " file", ex); }
            if (!carried) Tell(say, what);
            return carried;
        }

        /// <summary>
        /// SP7, a ledger (triage, resolved record) whose load failed: the same raw copy of the file, by path. A source that does not exist
        /// has nothing to lose, so it is not carried and nothing is said.
        /// </summary>
        public static bool CarryUnreadable(string fromFile, string toFile, string identity, string what, Action<string> say)
        {
            bool carried = false;
            try
            {
                if (string.IsNullOrEmpty(fromFile) || !File.Exists(fromFile)) return false;
                carried = LedgerFile.CopyOver(fromFile, toFile, identity);
            }
            catch (Exception ex) { Log.Warn("Save As: could not carry the unreadable " + what + " file", ex); }
            if (!carried) Tell(say, what);
            return carried;
        }

        /// <summary>
        /// Round 8, addendum 2, ledgers: a file that WAS read, copied to the new name. When the copy fails (held at that instant, a full disk)
        /// the user is told through <paramref name="say"/>, not left to find an empty record under the new name. A source that does not
        /// exist has nothing to lose: <c>CopyOver</c> then sets the target aside and reports success, and nothing is said.
        /// </summary>
        public static bool CarryReadable(string fromFile, string toFile, string identity, string what, Action<string> say)
        {
            bool carried = false;
            try { carried = LedgerFile.CopyOver(fromFile, toFile, identity); }
            catch (Exception ex) { Log.Warn("Save As: could not carry the " + what + " file", ex); }
            if (!carried) TellCopyFailed(say, what);
            return carried;
        }

        /// <summary>
        /// Round 8, addendum 2, the whole-file stores (settings, audit labels): the parsed content of a store that was read is written under
        /// the new name (<see cref="RewrittenFile.CarryTo"/>). When that write fails the user is told, as above.
        /// </summary>
        public static bool CarryParsed(RewrittenFile file, string newPath, IList<string> lines, string what, Action<string> say)
        {
            bool carried = false;
            try { carried = file != null && file.CarryTo(newPath, lines); }
            catch (Exception ex) { Log.Warn("Save As: could not carry the " + what + " file", ex); }
            if (!carried) TellCopyFailed(say, what);
            return carried;
        }

        private static void Tell(Action<string> say, string what)
        {
            Log.Warn("Save As: the " + what + " file could not be read and could not be copied; nothing was carried to the new name.");
            Say(say, SaveNotice.CarryRefused(what));
        }

        private static void TellCopyFailed(Action<string> say, string what)
        {
            Log.Warn("Save As: the " + what + " file could not be copied; nothing was carried to the new name.");
            Say(say, SaveNotice.CopyFailed(what));
        }

        private static void Say(Action<string> say, string text)
        {
            if (say != null && !string.IsNullOrEmpty(text)) { try { say(text); } catch { } }
        }
    }
}
