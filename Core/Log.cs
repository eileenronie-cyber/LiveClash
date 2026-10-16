using System;
using System.IO;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>
    /// Deliberately dumb file logger. Nothing in the updater path is ever allowed to
    /// throw or pop a dialog, so failures go here instead of at the user.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

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

        public static void Info(string message)
        {
            Write("INFO ", message, null);
        }

        public static void Warn(string message, Exception ex = null)
        {
            Write("WARN ", message, ex);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
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
                    File.AppendAllText(Path, sb.ToString() + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never be the thing that breaks the add-in.
            }
        }

        /// <summary>Roll past this size. Big enough to hold a long session, small enough
        /// to open in a text editor.</summary>
        private const long MaxBytes = 4L * 1024 * 1024;

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
    }
}
