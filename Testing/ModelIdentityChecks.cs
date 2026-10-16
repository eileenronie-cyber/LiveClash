using System;
using System.IO;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The sync-study collision guard (Core\ModelIdentity.cs): the pure decision of which file a
    /// model actually uses, and whether it must claim one. Revit-free — <see cref="ModelIdentity.Compute"/>
    /// needs a live Document and is exercised only in-Revit (ChangeChecks' identity block).
    /// </summary>
    public static class ModelIdentityChecks
    {
        public static void RunAll(Action<string, bool?> check)
        {
            const string T = "MEP"; // a title two different projects could share

            check("identity: a standalone/detached model (no identity) always uses the plain title, never claims",
                Resolves(T, false, "", null) == T && !Claims(T, false, "", null)
                && Resolves(T, true, "someone-else", null) == T && !Claims(T, true, "someone-else", null));

            check("identity: a brand-new file (none exists yet) uses the plain title; its own header will stamp it - no separate claim",
                Resolves(T, false, "", "A") == T && !Claims(T, false, "", "A"));

            check("identity: an existing file with NO identity recorded is unclaimed - use it, and claim it for this identity",
                Resolves(T, true, "", "A") == T && Claims(T, true, "", "A"));

            check("identity: the SAME identity as last time just uses the title again, no re-claim",
                Resolves(T, true, "A", "A") == T && !Claims(T, true, "A", "A"));

            check("identity: a DIFFERENT identity than what the title file already claims gets its own file, and does not touch the claim",
                Resolves(T, true, "A", "B") == T + "~" + ShortHash.Of("B") && !Claims(T, true, "A", "B"));

            check("identity: two different identities collide to two different suffixes, deterministically (same input, same output)",
                Resolves(T, true, "A", "B") == Resolves(T, true, "A", "B")
                && Resolves(T, true, "A", "B") != Resolves(T, true, "A", "C")
                && ShortHash.Of("B") == ShortHash.Of("B"));

            // ModelIdentity.Compute is deliberately NOT called here, even with a null Document:
            // it takes a live Document by type, and merely JITting a call to it needs the real
            // RevitAPI assembly loaded - which this headless runner does not have. It is
            // exercised only in-Revit (ChangeChecks' identity block).
            check("identity: an empty title yields no file to resolve",
                ModelIdentity.Resolve("", true, "", "A", out _) == "");

            // ---- RecordedIn: pulling a claim back out of a store's lines ----
            check("identity: no lines, or none of them a claim, records nothing",
                ModelIdentity.RecordedIn(null) == "" && ModelIdentity.RecordedIn(new string[0]) == ""
                && ModelIdentity.RecordedIn(new[] { "# LiveClash status", "k\tPending\tnow\tme\t" }) == "");
            check("identity: a claim is found wherever it sits in the file - header, or appended later",
                ModelIdentity.RecordedIn(new[] { "# model=MEP", ModelIdentity.Line("A"), "k\tNew\tnow\tme\t" }) == "A"
                && ModelIdentity.RecordedIn(new[] { "# model=MEP", "k\tNew\tnow\tme\t", ModelIdentity.Line("A") }) == "A");
            check("identity: the LAST claim line wins - an append-only file can only add one, never edit it",
                ModelIdentity.RecordedIn(new[] { ModelIdentity.Line("A"), ModelIdentity.Line("B") }) == "B");
            check("identity: leading whitespace before the '#' does not hide a claim, but a plain data line never counts as one",
                ModelIdentity.RecordedIn(new[] { "   " + ModelIdentity.Line("A") }) == "A"
                && ModelIdentity.RecordedIn(new[] { "identity=A" }) == "");

            check("identity: the claim line format round-trips through RecordedIn",
                ModelIdentity.RecordedIn(new[] { ModelIdentity.Line("cafe1234") }) == "cafe1234");

            check("identity: a short hash is deterministic, 8 hex digits, and empty only for an empty identity",
                ShortHash.Of("central:c:\\jobs\\mep.rvt") == ShortHash.Of("central:c:\\jobs\\mep.rvt")
                && ShortHash.Of("central:c:\\jobs\\mep.rvt").Length == 8
                && ShortHash.Of("") == "" && ShortHash.Of(null) == "");

            IoRoundTrip(check);
            RetryUnderLock(check);
        }

        /// <summary>
        /// ReadRecorded/Claim are the one shared implementation of the guard's actual file I/O
        /// (all four stores used to carry their own copy). They take a plain path, not a
        /// Document, so - unlike Compute - they are safe to exercise headlessly, against a
        /// throwaway temp file, never a real per-model ledger.
        /// </summary>
        private static void IoRoundTrip(Action<string, bool?> check)
        {
            var path = Path.Combine(Path.GetTempPath(), "liveclash-identity-test-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                check("identity: ReadRecorded on a file that does not exist yet is unclaimed, not an error",
                    ModelIdentity.ReadRecorded(path) == "");

                File.WriteAllLines(path, new[] { "# a pre-existing file, no claim", "k\tPending\tnow\tme\t" });
                check("identity: ReadRecorded finds nothing in a real, legacy-shaped file with no claim",
                    ModelIdentity.ReadRecorded(path) == "");

                ModelIdentity.Claim(path, "real-write-A");
                check("identity: Claim genuinely appends to the file - ReadRecorded reads its own write back",
                    ModelIdentity.ReadRecorded(path) == "real-write-A");

                var before = File.ReadAllLines(path);
                check("identity: ...without touching the lines that were already there",
                    before.Length == 3 && before[0] == "# a pre-existing file, no claim" && before[1] == "k\tPending\tnow\tme\t");

                ModelIdentity.Claim(path, "real-write-B");
                check("identity: a second Claim on the same file is ANOTHER append (the guard decides whether to call it, not this method) - the LAST one wins",
                    ModelIdentity.ReadRecorded(path) == "real-write-B" && File.ReadAllLines(path).Length == 4);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        /// <summary>
        /// Found by the codebase-wide review of 2026-09-26: every store did its own bare,
        /// unretried File.ReadAllLines for this check — a read that landed at the exact moment
        /// another session was mid-append could make the guard think a real claim did not exist.
        /// ReadLines/ReadRecorded/Claim now share ONE retry-protected implementation; this proves
        /// the retry itself does something, against a REAL, briefly-held exclusive lock — not just
        /// that the happy path (no contention at all) works, which IoRoundTrip above already covers.
        /// </summary>
        private static void RetryUnderLock(Action<string, bool?> check)
        {
            var path = Path.Combine(Path.GetTempPath(), "liveclash-identity-lock-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                Action<Action> underLock = body =>
                {
                    using (var gate = new System.Threading.ManualResetEventSlim(false))
                    {
                        var t = new System.Threading.Thread(() =>
                        {
                            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                            {
                                gate.Set();
                                System.Threading.Thread.Sleep(60);   // well inside the 5-attempt, 20/40/60/80/100 ms backoff
                            }
                        });
                        t.IsBackground = true;
                        t.Start();
                        gate.Wait(2000);
                        try { body(); } finally { t.Join(2000); }
                    }
                };

                File.WriteAllLines(path, new[] { ModelIdentity.Line("already-claimed") });
                string readWhileLocked = null;
                underLock(() => readWhileLocked = ModelIdentity.ReadRecorded(path));
                check("identity: ReadRecorded retries past a real, brief exclusive lock rather than giving up immediately (\""
                      + readWhileLocked + "\")", readWhileLocked == "already-claimed");

                File.WriteAllLines(path, new[] { "# nothing yet" });
                underLock(() => ModelIdentity.Claim(path, "claimed-under-lock"));
                check("identity: Claim retries past a real, brief exclusive lock and its write survives",
                    ModelIdentity.ReadRecorded(path) == "claimed-under-lock");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        private static string Resolves(string title, bool exists, string recorded, string identity)
        {
            string claim;
            return ModelIdentity.Resolve(title, exists, recorded, identity, out claim);
        }

        private static bool Claims(string title, bool exists, string recorded, string identity)
        {
            string claim;
            ModelIdentity.Resolve(title, exists, recorded, identity, out claim);
            return claim != null;
        }
    }
}
