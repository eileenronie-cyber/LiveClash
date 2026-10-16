using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The collision guard for the four per-model stores (status, resolved log, settings, audit
    /// labels). All four key their file on <see cref="ModelKey"/>'s normalised TITLE, by design —
    /// see that class's header comment on why: it is what keeps a local copy (_username) and a
    /// detached copy (_detached) sharing one file. But two DIFFERENT workshared models with the
    /// same title, opened on the same PC, share it too — which is a real bug, not the sharing
    /// ModelKey exists for.
    ///
    /// The guard: a per-model IDENTITY, recorded in the title-keyed file the first time an
    /// identified model uses it, checked on every later load. A standalone or detached model has
    /// no identity and never triggers the guard — breaking THAT sharing is exactly the failure
    /// this must not cause.
    ///
    /// <see cref="Compute"/> needs a live Document and cannot be exercised headlessly; everything
    /// else here is pure, so the guard's actual decision — which file, and whether to claim one —
    /// is proven without Revit. <see cref="ReadRecorded"/>/<see cref="Claim"/> are the one, shared
    /// implementation of the I/O around that decision, used by all four stores instead of each
    /// carrying its own copy.
    ///
    /// WHAT THIS DOES NOT CLOSE. The decision is made once, at LoadFor time, from whatever the
    /// title file says THEN — it is not re-checked when a real write (Append/Save) actually
    /// happens later, and it is a plain check-then-act with no cross-process lock. So: two
    /// DIFFERENT, never-before-seen same-titled models, both opened before either has ever
    /// written anything, both see an unclaimed (or nonexistent) file and both settle in memory on
    /// the plain title for the rest of their session; whichever writes first claims it, and the
    /// second one's writes land in the first one's file with no fork and no warning — the exact
    /// failure this guard exists to prevent, just moved from "always happens" to "a race between
    /// two sessions' first-ever write, on the same file, before this guard has anything to check
    /// against." Once a claim genuinely exists on disk, every LATER load correctly forks a
    /// different identity onto its own file — the gap is only the window before a title's very
    /// first claim is written.
    /// </summary>
    public static class ModelIdentity
    {
        private const string Prefix = "identity=";

        /// <summary>
        /// The model's cross-session identity, or null for a standalone or detached model (never
        /// workshared, so it has none — and must never trigger the guard).
        ///
        /// <see cref="Document.WorksharingCentralGUID"/> covers a cloud-hosted and a file-based
        /// central with the SAME API: one GUID per central, assigned once and unchanged by every
        /// local and every colleague's local after it. That is a better fit here than branching
        /// on <see cref="Document.IsModelInCloud"/> to read a cloud project/model GUID pair versus
        /// a file path — both identify the same thing this needs, and the central GUID does it in
        /// one call that already exists in Revit 2024, the oldest version LiveClash targets.
        /// </summary>
        public static string Compute(Document doc)
        {
            if (doc == null) return null;
            try
            {
                if (!doc.IsValidObject || !doc.IsWorkshared) return null;
                var guid = doc.WorksharingCentralGUID;
                return guid == Guid.Empty ? null : guid.ToString("N");
            }
            catch { return null; }
        }

        /// <summary>
        /// Pull the identity recorded among a store's lines, if any. The LAST "# identity=..."
        /// line wins — an append-only store can only ADD a claim, never edit one in place, so a
        /// claim written after the file already existed lands further down, not at the top.
        /// Empty string (not null) means the lines exist but recorded no identity: an unclaimed
        /// legacy file, or one from before this guard existed.
        /// </summary>
        public static string RecordedIn(IEnumerable<string> lines)
        {
            var found = "";
            if (lines == null) return found;
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                var s = line.TrimStart();
                if (s.Length == 0 || s[0] != '#') continue;
                s = s.Substring(1).TrimStart();
                if (s.StartsWith(Prefix, StringComparison.Ordinal))
                    found = s.Substring(Prefix.Length).Trim();
            }
            return found;
        }

        /// <summary>The comment line a store appends (to claim an unclaimed file) or writes into
        /// a fresh file's header, recording which identity now owns a title-keyed file.</summary>
        public static string Line(string identity) { return "# " + Prefix + identity; }

        /// <summary>
        /// A title-keyed file's lines, or null if it does not exist, or is still locked after a
        /// short retry — the same retry a real append already needs, because another Revit session
        /// may hold the file for the instant it takes to write its own line, and that instant is
        /// exactly when this guard is most likely to be asked. Found by the codebase-wide review of
        /// 2026-09-26: every store used its OWN bare, unretried File.ReadAllLines here — this is the
        /// one place that read now happens, so the retry actually reaches it.
        /// </summary>
        public static string[] ReadLines(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { return File.Exists(path) ? File.ReadAllLines(path) : null; }
                catch (IOException) { System.Threading.Thread.Sleep(20 * (attempt + 1)); }
                catch (Exception ex) { Log.Warn("Could not read " + path, ex); return null; }
            }
            Log.Warn("Could not read " + path + ": file stayed locked", null);
            return null;
        }

        /// <summary>
        /// The identity a title-keyed file has recorded, or "" if it does not exist, has none, or
        /// could not be read even after <see cref="ReadLines"/>'s retry. A read that is still locked
        /// is treated the same as "no claim recorded" — the guard's existing, accepted risk
        /// tolerance (see <see cref="Claim"/>), not a new one.
        /// </summary>
        public static string ReadRecorded(string path)
        {
            var lines = ReadLines(path);
            return lines == null ? "" : RecordedIn(lines);
        }

        /// <summary>
        /// Append <see cref="Line"/> to a title-keyed file, claiming it for <paramref name="identity"/> —
        /// with the same short retry every real append to one of these files already uses (see
        /// e.g. ClashStatusStore.Append): another Revit session may hold the file for the instant
        /// it takes to write its own line.
        /// </summary>
        public static void Claim(string path, string identity)
        {
            var line = Line(identity) + Environment.NewLine;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { File.AppendAllText(path, line); return; }
                catch (IOException) { System.Threading.Thread.Sleep(20 * (attempt + 1)); }
                catch (Exception ex) { Log.Warn("Could not claim " + path + " for this model", ex); return; }
            }
            Log.Warn("Could not claim " + path + " for this model: file stayed locked", null);
        }

        /// <summary>
        /// Decide which file a model actually reads and writes, and whether this load must CLAIM
        /// the title-keyed file for the current identity.
        /// </summary>
        /// <param name="titleKey">The file name <see cref="ModelKey"/> would use alone.</param>
        /// <param name="fileExists">Whether a file already sits at <paramref name="titleKey"/>.</param>
        /// <param name="recorded">What <see cref="RecordedIn"/> found there — "" if the file does
        /// not exist or has no identity recorded.</param>
        /// <param name="currentIdentity"><see cref="Compute"/> for the model loading now.</param>
        /// <param name="claimIdentity">Set to <paramref name="currentIdentity"/> when the caller
        /// must append <see cref="Line"/> to the title-keyed file before anything else is written
        /// to it; null otherwise.</param>
        /// <returns>
        /// The file name to actually use — <paramref name="titleKey"/> unless a DIFFERENT
        /// identity already claimed it, in which case a short, deterministic suffix keeps this
        /// model's own file apart from it.
        /// </returns>
        public static string Resolve(string titleKey, bool fileExists, string recorded,
                                      string currentIdentity, out string claimIdentity)
        {
            claimIdentity = null;
            if (string.IsNullOrEmpty(currentIdentity) || string.IsNullOrEmpty(titleKey))
                return titleKey;                                     // standalone/detached: unaffected

            if (!fileExists) return titleKey;                        // a fresh file stamps its own header

            if (string.IsNullOrEmpty(recorded))
            {
                claimIdentity = currentIdentity;                     // unclaimed: claim it, from now on
                return titleKey;
            }

            if (string.Equals(recorded, currentIdentity, StringComparison.Ordinal))
                return titleKey;                                     // the same model as last time

            // A DIFFERENT model already claimed the title. The file that exists keeps whoever
            // claimed it — it cannot be split automatically — and this model starts its own.
            return titleKey + "~" + ShortHash.Of(currentIdentity);
        }
    }
}
