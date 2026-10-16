using System;
using System.IO;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The log is the only diagnostic channel a pilot user has, and its error count is what a
    /// rollout is gated on, so it is tested like the rest: line format, the counters, the session
    /// summary, and the roll at the size limit. Every check writes to a throwaway file through
    /// Log.RedirectForTests, never to the real liveclash.log, and puts the real counts back.
    /// </summary>
    public static class LogChecks
    {
        private const string P = "log: ";

        public static void RunAll(Action<string, bool?> check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "liveclash-logtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "liveclash.log");
            int realErrors = Log.ErrorCount, realWarnings = Log.WarningCount;
            var realPath = Log.Path;
            try
            {
                using (Log.RedirectForTests(file))
                {
                    check(P + "a redirected log starts with zero errors and warnings",
                        Log.ErrorCount == 0 && Log.WarningCount == 0 && Log.Path == file);

                    Log.Info("hello info");
                    Log.Warn("careful");
                    Log.Error("broke", new InvalidOperationException("the reason"));
                    Log.Error("broke again");
                    var text = File.ReadAllText(file);
                    var lines = File.ReadAllLines(file);

                    check(P + "each line is a timestamp, the level and the message",
                        lines.Length >= 4
                        && System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}  INFO   hello info$")
                        && text.Contains("  WARN   careful") && text.Contains("  ERROR  broke again"));
                    check(P + "an exception adds its type and message on the next line",
                        text.Contains("  ERROR  broke" + Environment.NewLine + "    InvalidOperationException: the reason"));
                    check(P + "errors and warnings are counted separately; info is not counted",
                        Log.ErrorCount == 2 && Log.WarningCount == 1);
                    check(P + "the session summary states both counts, plural and singular",
                        Log.SessionSummary().StartsWith("Session summary: 2 errors, 1 warning since "));

                    using (Log.RedirectForTests(Path.Combine(dir, "second.log")))
                    {
                        Log.Error("one");
                        check(P + "the summary says '1 error, 0 warnings' for exactly one error",
                            Log.SessionSummary().StartsWith("Session summary: 1 error, 0 warnings since "));
                    }
                    check(P + "a nested redirect puts the outer file and counts back",
                        Log.Path == file && Log.ErrorCount == 2 && Log.WarningCount == 1);

                    // The roll: past MaxBytes the file becomes .1 (replacing an older .1) and a new file starts.
                    Log.MaxBytes = 64;
                    File.WriteAllText(file + ".1", "an older generation");
                    Log.Info("after the roll");
                    Log.MaxBytes = 1L << 40;
                    var rolled = File.Exists(file + ".1") ? File.ReadAllText(file + ".1") : "";
                    var fresh = File.Exists(file) ? File.ReadAllText(file) : "";
                    check(P + "past the size limit the log rolls to .1, replacing the older generation",
                        rolled.Contains("hello info") && !rolled.Contains("an older generation")
                        && fresh.Contains("after the roll") && !fresh.Contains("hello info"));

                    var small =new FileInfo(file).Length;
                    Log.Info("no roll");
                    check(P + "under the limit nothing rolls", new FileInfo(file).Length > small && File.ReadAllText(file + ".1").Contains("hello info"));

                    SharedFileFailures(check, dir, file);

                    // Count even when the write fails: a log pointed at a folder that cannot be written.
                    using (Log.RedirectForTests(Path.Combine(dir, "no-such-folder", "x", "liveclash.log")))
                    {
                        Log.Error("unwritable");
                        Log.Warn("unwritable");
                        check(P + "a failed write never throws and is still counted", Log.ErrorCount == 1 && Log.WarningCount == 1);
                    }
                }

                check(P + "after the test the real log path and counts are back exactly",
                    Log.Path == realPath && Log.ErrorCount == realErrors && Log.WarningCount == realWarnings);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The ledgers share files between seats, so a read or a claim can meet a file another process
        /// holds. After the short retry gives up, the failure must reach the log as a warning (and the
        /// count), not vanish. Runs inside the caller's redirect, so the warnings land in the test file.
        /// </summary>
        private static void SharedFileFailures(Action<string, bool?> check, string dir, string logFile)
        {
            var held = Path.Combine(dir, "held.txt");
            File.WriteAllText(held, "# identity=abc" + Environment.NewLine);
            try
            {
                int w0 = Log.WarningCount;
                string[] lines;
                using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    lines = ModelIdentity.ReadLines(held);
                    ModelIdentity.Claim(held, "def");
                }
                var text = File.ReadAllText(logFile);
                check(P + "a shared file still locked after the retry reads as nothing, and says so as a warning",
                    lines == null && text.Contains("Could not read " + held + ": file stayed locked"));
                check(P + "a claim on a file still locked after the retry is given up with a warning, not lost silently",
                    text.Contains("Could not claim " + held + " for this model: file stayed locked")
                    && Log.WarningCount == w0 + 2 && !File.ReadAllText(held).Contains("def"));

                ModelIdentity.Claim(dir, "def");   // a folder: not an IOException, so no retry
                check(P + "a claim that cannot be written at all is logged with the reason",
                    File.ReadAllText(logFile).Contains("Could not claim " + dir + " for this model") && Log.WarningCount == w0 + 3);
            }
            finally
            {
            }
        }
    }
}
