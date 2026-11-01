namespace LiveClash.Core
{
    /// <summary>
    /// What to tell the user when a settings save was refused (delta review round 2, DL-e, the second half of DN2). Save() returns false
    /// when the model's settings file could not be read (nothing is written over it) or when the object is the defaults a re-read has replaced;
    /// Filter, Audit and the panel used to ignore that and say "updated", so an edit was lost without a word.
    /// </summary>
    public static class SaveNotice
    {
        /// <param name="saved">What <c>ClashSettings.Save()</c> returned.</param>
        /// <param name="discarded">The settings object was the defaults a failed read left running, and the real settings have been
        /// read since: its edits are gone, not just unsaved.</param>
        /// <returns>The sentence for the panel, or null when the save worked.</returns>
        public static string After(bool saved, bool discarded)
        {
            if (saved) return null;
            if (discarded)
                return "Not saved: your settings were read again after this window opened, so these edits were discarded. Open it again to make them.";
            return "Not saved: your settings file could not be read. This change is kept only until the file is read again (it is tried every few seconds); "
                   + "then the file's own settings come back and this change is lost.";
        }

        /// <summary>
        /// Round 8, addendum 2. A Save As could not COPY a per-model file that could be read (held at that instant, a full disk): nothing
        /// reaches the new name, and the user is told so, never left to find defaults. Not the SP7 sentence, which says the file could not be
        /// read. <paramref name="what"/> is "settings", "triage", "resolved record" or "audit labels".
        /// </summary>
        public static string CopyFailed(string what)
        {
            if (string.IsNullOrWhiteSpace(what)) what = "per-model";
            return "Save As: your " + what + " file could not be copied to the new name, so the new name starts on the defaults. "
                   + "The original file is unchanged under the old name.";
        }

        /// <summary>
        /// SP7 (round 8). A Save As could not carry a per-model file whose read had failed: nothing is copied to the new name, and the user
        /// is told so, never left to find defaults. <paramref name="what"/> is "settings", "triage", "resolved record" or "audit labels".
        /// </summary>
        public static string CarryRefused(string what)
        {
            if (string.IsNullOrWhiteSpace(what)) what = "per-model";
            return "Save As: your " + what + " file could not be read, so it was not copied to the new name. The new name starts on the defaults. "
                   + "The original file is unchanged under the old name.";
        }
    }
}
