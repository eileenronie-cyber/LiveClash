using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LiveClash.Core
{
    /// <summary>
    /// Deliberately dumb file logger. Nothing in the updater path is ever allowed to
    /// throw or pop a dialog, so failures go here instead of at the user.
    ///
    /// It also COUNTS what it writes. A pilot user cannot be asked to read a log, but "3 errors,
    /// 0 warnings this session" in the closing summary line is something a rollout can be gated
    /// on: <see cref="SessionSummary"/> is written at shutdown, and the counts are kept even
    /// when the write itself fails.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static int _errors, _warnings;
        private static DateTime _sessionStart = DateTime.Now;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LiveClash");
                    try { Directory.CreateDirectory(dir); } catch { }
                    _path = System.IO.Path.Combine(dir, "liveclash.log");
                }
                return _path;
            }
        }

        /// <summary>ERROR lines written (or attempted) since the session began.</summary>
        public static int ErrorCount { get { return Volatile.Read(ref _errors); } }

        /// <summary>WARN lines written (or attempted) since the session began.</summary>
        public static int WarningCount { get { return Volatile.Read(ref _warnings); } }

        public static void Info(string message)
        {
            Write("INFO ", message, null);
        }

        public static void Warn(string message, Exception ex = null)
        {
            Interlocked.Increment(ref _warnings);
            Write("WARN ", message, ex);
        }

        public static void Error(string message, Exception ex = null)
        {
            Interlocked.Increment(ref _errors);
            Write("ERROR", message, ex);
        }

        /// <summary>
        /// One line for the end of a session: how long it ran and how many errors and warnings it
        /// logged. The release smoke test and a pilot's log review both read this line.
        /// </summary>
        public static string SessionSummary()
        {
            var e = ErrorCount;
            var w = WarningCount;
            return "Session summary: " + e + " error" + (e == 1 ? "" : "s") + ", " + w + " warning" + (w == 1 ? "" : "s")
                   + " since " + _sessionStart.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".";
        }

        // ------------------------------------------------------------------ scrub
        //
        // The pilot guide asks users to send this file back, and it held model titles, link names and
        // paths with the Windows user name in them (review 2026-10-31, LOW-11; X1/X2). So every line
        // is scrubbed on its way in, in this order:
        //   1. %LOCALAPPDATA% and the profile folder become those words (the user name goes with them);
        //   2. any other absolute path keeps its file name and loses its folders ("<dir>\x.rvt");
        //   3. each name noted as sensitive (the model keys the stores load) becomes "model#" plus a
        //      short hash, the same tag every time, so a log still reads as one story;
        //   4. the Windows user name, as a whole word, becomes "<user>".
        // Link names a store never sees are not known here; they are caught by 2 when they arrive in a path.

        private static string[] _sensitive = Array.Empty<string>();   // copy-on-write, longest first

        /// <summary>A model title, link name or file key that must not reach the log as written. Names
        /// shorter than 3 characters are ignored (they would scrub ordinary words).</summary>
        public static void NoteSensitive(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length < 3) return;
            lock (Gate)
            {
                foreach (var s in _sensitive) if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return;
                var next = new System.Collections.Generic.List<string>(_sensitive) { name };
                next.Sort((a, b) => b.Length.CompareTo(a.Length));
                _sensitive = next.ToArray();
            }
        }

        /// <summary>The tag a sensitive name is logged as.</summary>
        internal static string Tag(string name) { return "model#" + ShortHash.Of((name ?? "").Trim().ToLowerInvariant()); }

        private static readonly System.Text.RegularExpressions.Regex ForeignDirs = new System.Text.RegularExpressions.Regex(
            @"(?:\b[A-Za-z]:\\|\\\\)(?:[^\\/:*?""<>|\r\n']+\\)+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>What a log line says once titles, paths and the user name are taken out (see above).</summary>
        internal static string Scrub(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = ReplaceFolder(s, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
            s = ReplaceFolder(s, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            s = ForeignDirs.Replace(s, "<dir>\\");
            foreach (var name in _sensitive) s = ReplaceWord(s, name, Tag(name));
            s = ReplaceUser(s, Environment.UserName);
            return s;
        }

        /// <summary>The Windows user name as a whole word, unless it is too short to tell from an ordinary word.</summary>
        internal static string ReplaceUser(string s, string user)
        {
            return string.IsNullOrEmpty(user) || user.Length < 3 ? s : ReplaceWord(s, user, "<user>");
        }

        internal static string ReplaceFolder(string s, string folder, string token)
        {
            if (string.IsNullOrEmpty(folder) || folder.Length < 4) return s;
            return System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(folder.TrimEnd('\\')),
                token, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        internal static string ReplaceWord(string s, string word, string token)
        {
            return System.Text.RegularExpressions.Regex.Replace(s,
                @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(word) + @"(?![A-Za-z0-9])",
                token, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                // Invariant: in a custom format ':' is the CULTURE's time separator, and the release
                // smoke test and log reviews parse this stamp. A locale with '.' would break both.
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append("  ").Append(level).Append("  ").Append(message);

                if (ex != null)
                {
                    sb.AppendLine().Append("    ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message);
                    if (ex.StackTrace != null) sb.AppendLine().Append(ex.StackTrace);
                }

                lock (Gate)
                {
                    RollIfBig();
                    File.AppendAllText(Path, Scrub(sb.ToString()) + Environment.NewLine);   // scrubbed: see above
                }
            }
            catch
            {
                // Logging must never be the thing that breaks the add-in.
            }
        }

        /// <summary>Roll past this size. Big enough to hold a long session, small enough
        /// to open in a text editor. A field, not a const, only so a test can roll a small file.</summary>
        internal static long MaxBytes = 4L * 1024 * 1024;

        /// <summary>
        /// Keep one previous generation and start fresh.
        ///
        /// The file grew forever, with a full stack trace on every warning, and the engine
        /// warns per skipped element and per retried batch — so a long session on a messy
        /// model writes a lot. This is the one diagnostic channel that stays readable while
        /// a sweep has Revit frozen, and an unopenable log is no channel at all.
        ///
        /// Called under Gate, and never allowed to throw: if the roll fails, the worst
        /// outcome must be a large log, not a lost message.
        /// </summary>
        private static void RollIfBig()
        {
            try
            {
                if (!File.Exists(Path)) return;
                if (new FileInfo(Path).Length < MaxBytes) return;

                var prev = Path + ".1";
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(Path, prev);
            }
            catch { }
        }

        /// <summary>
        /// TESTS ONLY: point the log at <paramref name="path"/> with fresh counters, and return a
        /// handle that puts the real file and the real counts back when disposed. Checks must
        /// never write into the user's own liveclash.log, or count their deliberate errors in the
        /// session summary a pilot is judged on.
        /// </summary>
        internal static IDisposable RedirectForTests(string path)
        {
            lock (Gate)
            {
                var restore = new Redirect(_path, _errors, _warnings, _sessionStart, MaxBytes);
                _path = path;
                _errors = 0;
                _warnings = 0;
                _sessionStart = DateTime.Now;
                return restore;
            }
        }

        private sealed class Redirect : IDisposable
        {
            private readonly string _p;
            private readonly int _e, _w;
            private readonly DateTime _s;
            private readonly long _max;
            private bool _done;

            public Redirect(string p, int e, int w, DateTime s, long max) { _p = p; _e = e; _w = w; _s = s; _max = max; }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                lock (Gate)
                {
                    _path = _p;
                    _errors = _e;
                    _warnings = _w;
                    _sessionStart = _s;
                    MaxBytes = _max;
                }
            }
        }
    }
}
