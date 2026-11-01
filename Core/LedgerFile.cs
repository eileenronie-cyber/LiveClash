using System;
using System.Collections.Generic;
using System.IO;

namespace LiveClash.Core
{
    /// <summary>
    /// The disk half of every per-model file: the append-only ledgers (triage, resolved) and the
    /// files rewritten whole (settings, audit labels). One copy of the retry, the append and the
    /// write, so the rules below hold for all of them instead of for whichever store remembered.
    ///
    ///   - A READ tells "no file" apart from "a file I could not read". The second must never be
    ///     treated as the first: that is how defaults got written over a real settings file (review
    ///     2026-10-31, M9).
    ///   - An APPEND starts on a line of its own. A crash mid-append leaves half a line with no
    ///     newline; the next record used to join it and be skipped on every load (LOW-3).
    ///   - A WHOLE WRITE goes to a temp file beside the target and is swapped in, so a crash, a full
    ///     disk or a second writer leaves the old file or the new one, never a torn one (M9).
    ///
    /// Revit-free, so the headless gate tests it against real temp files.
    /// </summary>
    public static class LedgerFile
    {
        /// <summary>What a read found.</summary>
        public enum ReadState
        {
            /// <summary>No file: a model that has never written one.</summary>
            Missing,
            /// <summary>Read in full.</summary>
            Read,
            /// <summary>The file exists but could not be read, even after the retry. NOT the same as Missing.</summary>
            Failed
        }

        private const int Attempts = 5;

        /// <summary>
        /// A file's lines, with the same short retry the appends use (another Revit session may hold
        /// the file for the instant it takes to write its own line). Null unless <paramref name="state"/>
        /// is <see cref="ReadState.Read"/>. <paramref name="attempts"/> is how many times to try (default five, with a
        /// short sleep between); a retry on a tick makes one. Silent: a failed read is retried on every load, so the caller says
        /// so once rather than this saying it each time.
        /// </summary>
        public static string[] Read(string path, out ReadState state, int attempts = Attempts)
        {
            state = ReadState.Failed;
            if (string.IsNullOrEmpty(path)) return null;
            if (attempts < 1) attempts = 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) { state = ReadState.Missing; return null; }
                    var lines = File.ReadAllLines(path);
                    state = ReadState.Read;
                    return lines;
                }
                catch (IOException) { if (attempt < attempts - 1) System.Threading.Thread.Sleep(20 * (attempt + 1)); }
                catch (Exception) { return null; }
            }
            return null;
        }

        /// <summary>
        /// Append <paramref name="text"/> (whole lines, each ending in a newline), writing
        /// <paramref name="headerIfFresh"/> first when the file does not exist yet. Retried. When the
        /// file does not end in a newline (a crash mid-append), one is written first, so the half
        /// line stays a junk line of its own and this record is read.
        /// </summary>
        public static bool Append(string path, string headerIfFresh, string text, string failure = null)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(text)) return false;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    // One handle for the check and the write, so nothing lands between them.
                    using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        string lead = "";
                        if (fs.Length == 0) lead = headerIfFresh ?? "";
                        else
                        {
                            fs.Seek(-1, SeekOrigin.End);
                            int last = fs.ReadByte();
                            if (last != '\n' && last != '\r') lead = Environment.NewLine;
                        }
                        fs.Seek(0, SeekOrigin.End);
                        var bytes = new System.Text.UTF8Encoding(false).GetBytes(lead + text);
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    return true;
                }
                catch (IOException) { System.Threading.Thread.Sleep(20 * (attempt + 1)); }
                catch (Exception ex) { Log.Warn(failure ?? "Could not append to " + path, ex); return false; }
            }
            Log.Warn((failure ?? "Could not append to " + path) + ": file stayed locked", null);
            return false;
        }

        /// <summary>
        /// Replace the file's whole content with <paramref name="lines"/>, atomically: written to a temp
        /// file in the same folder, then swapped in (<see cref="File.Replace(string, string, string)"/>, or a
        /// move when there is no file yet). When <paramref name="backupPath"/> is given and a file is
        /// replaced, the old content is kept there. A failure leaves the old file exactly as it was.
        /// </summary>
        public static bool WriteWhole(string path, IList<string> lines, string backupPath = null)
        {
            if (string.IsNullOrEmpty(path) || lines == null) return false;
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Remove(8);
            try
            {
                using (var w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
                {
                    int n = 0;
                    foreach (var l in lines)
                    {
                        if (DieAfterLinesForTests >= 0 && n >= DieAfterLinesForTests) throw new IOException("injected: the write died part-way");
                        w.WriteLine(l);
                        n++;
                    }
                }

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(tmp, path, backupPath, true);
                        else File.Move(tmp, path);
                        return true;
                    }
                    catch (IOException) when (attempt < Attempts - 1)
                    {
                        System.Threading.Thread.Sleep(20 * (attempt + 1));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not write " + path + "; the file on disk is unchanged", ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// Make <paramref name="to"/> hold exactly what <paramref name="from"/> holds: the model was saved
        /// under a new name and its ledger goes with it (review 2026-10-31, M4). A file already at
        /// <paramref name="to"/> belonged to whatever had that name before; it is kept as <c>.bak</c>,
        /// never merged in (its decisions are about a different model). When <paramref name="from"/> does
        /// not exist (nothing recorded yet), <paramref name="to"/> is set aside the same way, so this
        /// model does not inherit another one's record. Atomic: copied to a temp file, then swapped in.
        /// </summary>
        public static bool CopyOver(string from, string to) { return CopyOver(from, to, null); }

        /// <summary>As the two-argument form, and the carried file records <paramref name="newIdentity"/> (round 2, DN3).</summary>
        public static bool CopyOver(string from, string to, string newIdentity)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return false;
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
            string bak = to + ".bak";
            string tmp = to + ".tmp-" + Guid.NewGuid().ToString("N").Remove(8);
            try
            {
                if (!File.Exists(from))
                {
                    if (File.Exists(to))
                    {
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(to, bak);
                    }
                    return true;
                }
                File.Copy(from, tmp, true);
                // The bytes carry the OLD "# identity=" header, and RecordedIn reads the LAST such line. Without the new
                // one, a Save As that gave the model a new central GUID found a mismatch on the next open and forked to an
                // empty title~hash file (review 2026-10-31 round 2, DN3). Appended through Append, so it starts on a line of its own.
                if (!string.IsNullOrEmpty(newIdentity) && !Append(tmp, null, ModelIdentity.Line(newIdentity) + Environment.NewLine)) return false;
                if (File.Exists(to)) File.Replace(tmp, to, bak, true);
                else File.Move(tmp, to);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not carry " + from + " to " + to, ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>TESTS ONLY: make <see cref="WriteWhole"/> die after this many lines, as a crash or a
        /// full disk would. -1 is off. A field, not a debug-only block, so the headless gate can reach it (as Log.MaxBytes).</summary>
        internal static int DieAfterLinesForTests = -1;
    }
}
