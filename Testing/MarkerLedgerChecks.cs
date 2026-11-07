using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 3a: the new ledger events and marker-to-ledger reconcile (spec 6.3, 6.6, 6.7,
    /// 6.10; RF4). Revit-free, so both tiers run it. Red-first: tests/evidence/clash-schedule/BUILD-LOG.md.
    /// </summary>
    public static class MarkerLedgerChecks
    {
        private const string L = "markers ledger: ";
        private const string R = "markers reconcile: ";
        private static readonly DateTime T0 = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);

        public static void RunAll(Action<string, bool?> check)
        {
            Writers(check);
            Reading(check);
            OldReplay(check);
            LedgerFuzz(check);
            Direction(check);
            Fields(check);
            Clock(check);
            ReconcileFuzz(check);
        }

        // ------------------------------------------------------------------ helpers

        private static DateTime T(int minutes) { return T0.AddMinutes(minutes); }

        private static string Old(string key, string kind, DateTime when, string by = "kdoe", string note = "")
        {
            return string.Join("\t", new[] { key, kind, when.ToString("o", CultureInfo.InvariantCulture), by, note });
        }

        private static string[] Split(string line) { return line.Split('\t'); }

        private static Dictionary<string, ClashStatusEntry> Replay(IEnumerable<string> lines, out int events)
        {
            var d = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int legacy, repaired;
            ClashStatusReplay.Replay(lines, d, out events, out legacy, out repaired);
            return d;
        }

        private static Dictionary<string, ClashStatusEntry> Replay(IEnumerable<string> lines)
        {
            int e;
            return Replay(lines, out e);
        }

        private static string DecisionsText(Dictionary<string, ClashStatusEntry> d)
        {
            return string.Join("\n", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                kv.Key + "|" + kv.Value.Kind + "|" + kv.Value.WhenUtc.Ticks + "|" + kv.Value.By + "|" + kv.Value.Note + "|" + kv.Value.Gone + "|" + kv.Value.GoneUtc.Ticks + "|" + kv.Value.HostName));
        }

        private static string ViewText(LedgerExtras.View v)
        {
            var sb = new StringBuilder();
            foreach (var kv in v.Assigned.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append("A|").Append(kv.Key).Append('|').Append(kv.Value.Assignee).Append('|').Append(kv.Value.By).Append('|').Append(kv.Value.WhenUtc.Ticks).Append('\n');
            foreach (var kv in v.Pending.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append("P|").Append(kv.Key).Append('|').Append(kv.Value.SeqAtPending).Append('|').Append(kv.Value.Status).Append('|').Append(kv.Value.Note ?? "~").Append('|').Append(kv.Value.AssignedTo ?? "~").Append('|').Append(kv.Value.By).Append('|').Append(kv.Value.WhenUtc.Ticks).Append('\n');
            foreach (var kv in v.Issues.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append("I|").Append(kv.Key).Append('|').Append(kv.Value.IssueNo).Append('|').Append(kv.Value.WhenUtc.Ticks).Append('\n');
            foreach (var kv in v.Reconciled.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append("R|").Append(kv.Key).Append('|').Append(kv.Value.Source).Append('|').Append(kv.Value.Event).Append('|').Append(kv.Value.WhenUtc.Ticks).Append('\n');
            foreach (var d in v.Duplicates) sb.Append("D|").Append(d.Key).Append('|').Append(d.RemovedIssueNo).Append('|').Append(d.KeptIssueNo).Append('|').Append(d.Status).Append('|').Append(d.Note).Append('|').Append(d.WhenUtc.Ticks).Append('\n');
            return sb.ToString();
        }

        private static string[] Texts(params string[] items)
        {
            return items;
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }

        private static MarkerReconcile.ModelMarker M(string key, MarkerStatus status, string note = "", string assigned = "", string by = "kdoe", string issue = "")
        {
            return new MarkerReconcile.ModelMarker { Key = key, Status = status, Note = note, AssignedTo = assigned, ChangedBy = by, ReportedBy = "jdoe", IssueNo = issue };
        }

        private static MarkerReconcile.Result Plan(IEnumerable<MarkerReconcile.ModelMarker> markers, IEnumerable<string> ledger, DateTime now)
        {
            var lines = ledger.ToList();
            return MarkerReconcile.Plan(markers, Replay(lines), LedgerExtras.Read(lines), now);
        }

        // ------------------------------------------------------------------ writers and round trips

        private static void Writers(Action<string, bool?> check)
        {
            var a = LedgerExtras.AssignedLine("k1|0|k2", T(5), "jdoe", "asmith");
            var af = Split(a);
            check(L + "an Assigned line is key, event, time, author, assignee, and reads back",
                af.Length == 5 && af[0] == "k1|0|k2" && af[1] == "Assigned" && af[3] == "jdoe" && af[4] == "asmith"
                && LedgerExtras.Read(new[] { a }).Assigned["k1|0|k2"].Assignee == "asmith" && LedgerExtras.Read(new[] { a }).Assigned["k1|0|k2"].WhenUtc == T(5));

            var cases = new[]
            {
                new { St = (MarkerStatus?)MarkerStatus.Ignored, Note = (string)null, As = (string)null },
                new { St = (MarkerStatus?)null, Note = (string)"a note", As = (string)null },
                new { St = (MarkerStatus?)null, Note = (string)null, As = (string)"asmith" },
                new { St = (MarkerStatus?)null, Note = (string)null, As = (string)"" },
                new { St = (MarkerStatus?)MarkerStatus.Coordinated, Note = (string)"", As = (string)"jdoe" }
            };
            bool allBack = true;
            foreach (var c in cases)
            {
                var line = LedgerExtras.PendingLine("k", T(2), "jdoe", 7, c.St, c.Note, c.As);
                var back = LedgerExtras.Read(new[] { line });
                PendingBack(back, out var p);
                allBack &= p != null && p.SeqAtPending == 7 && p.Status == c.St && p.Note == c.Note && p.AssignedTo == c.As && p.By == "jdoe" && p.WhenUtc == T(2);
            }
            check(L + "a PendingToModel line carries the marker's Seq and exactly the fields the triage set (status, note, assignee, or an unassign), and reads back", allBack);

            var done = LedgerExtras.DoneLine("k", T(3), "jdoe");
            check(L + "a PendingDone line closes a pending entry no later than itself",
                Split(done)[1] == "PendingDone" && LedgerExtras.Read(new[] { LedgerExtras.PendingLine("k", T(2), "jdoe", 1, MarkerStatus.Ignored, null, null), done }).Pending.Count == 0);

            var issue = LedgerExtras.IssueLine("k", T(1), "jdoe", "JD-0042");
            check(L + "an Issue line remembers the quotable number", Split(issue)[1] == "Issue" && LedgerExtras.Read(new[] { issue }).Issues["k"].IssueNo == "JD-0042");

            var dup = LedgerExtras.DuplicateLine("k", T(4), "jdoe", "JD-0007", "KD-0003", MarkerStatus.Ignored, "not kept", "kdoe");
            var dv = LedgerExtras.Read(new[] { dup }).Duplicates;
            check(L + "a DuplicateNotKept line keeps the lost decision as history",
                Split(dup)[1] == "DuplicateNotKept" && dv.Count == 1 && dv[0].Key == "k" && dv[0].KeptIssueNo == "JD-0007" && dv[0].RemovedIssueNo == "KD-0003" && dv[0].Status == MarkerStatus.Ignored
                && dv[0].Note == "not kept" && dv[0].AssignedTo == "kdoe" && dv[0].By == "jdoe" && dv[0].WhenUtc == T(4));

            var rec = LedgerExtras.ReconciledLine("k", T(6), "kdoe", "model", "Ignored");
            var rv = LedgerExtras.Read(new[] { rec, LedgerExtras.ReconciledLine("k", T(5), "kdoe", "model", "Pending") });
            var rf = Split(rec);
            check(L + "a Reconciled line is five fields (key, Reconciled, time, author, \"model <event>\") and the latest per key reads back",
                rf.Length == 5 && rf[1] == "Reconciled" && rf[3] == "kdoe" && rf[4] == "model Ignored" && rv.Reconciled.ContainsKey("k")
                && rv.Reconciled["k"].Source == "model" && rv.Reconciled["k"].Event == "Ignored" && rv.Reconciled["k"].WhenUtc == T(6));

            var nasty = "line one\ttab\r\nline two";
            var lines = new[]
            {
                LedgerExtras.AssignedLine("k", T(1), "j\tdoe", "as\tmith\n"), LedgerExtras.PendingLine("k", T(1), "jdoe", 1, MarkerStatus.Ignored, nasty, "a\tb"),
                LedgerExtras.IssueLine("k", T(1), "jdoe", "JD\t1"), LedgerExtras.DuplicateLine("k", T(1), "jdoe", "a\tb", "c\nd", MarkerStatus.Pending, nasty, "x\ty"), LedgerExtras.DoneLine("k", T(1), "j\ndoe")
            };
            check(L + "tabs and line breaks in any text become spaces: every line is still one line with its own fields",
                lines.All(x => x.Length > 0 && x.IndexOf('\n') < 0 && x.IndexOf('\r') < 0) && Split(lines[1]).Length == 9 && Split(lines[0]).Length == 5 && Split(lines[3]).Length == 9);
        }

        private static void PendingBack(LedgerExtras.View v, out LedgerExtras.PendingEntry p)
        {
            v.Pending.TryGetValue("k", out p);
        }

        // ------------------------------------------------------------------ reading rules

        private static void Reading(Action<string, bool?> check)
        {
            var assigned = new[]
            {
                LedgerExtras.AssignedLine("k", T(1), "a", "first"), LedgerExtras.AssignedLine("k", T(3), "b", "third"), LedgerExtras.AssignedLine("k", T(2), "c", "second"),
                LedgerExtras.AssignedLine("m", T(1), "a", "asmith"), LedgerExtras.AssignedLine("m", T(2), "a", "")
            };
            bool agree = true; string first = null;
            foreach (var perm in MarkerIdentityChecks.Permutations(assigned.Take(4).ToArray()))
            {
                var t = ViewText(LedgerExtras.Read(perm));
                if (first == null) first = t; else agree &= t == first;
            }
            var av = LedgerExtras.Read(assigned);
            check(L + "Assigned: the latest clock wins whatever the file order, and a newer empty assignee is an unassign",
                agree && av.Assigned.ContainsKey("k") && av.Assigned["k"].Assignee == "third" && av.Assigned["k"].By == "b" && av.Assigned.ContainsKey("m") && av.Assigned["m"].Assignee == "");

            Func<LedgerExtras.View, bool> open = v => v.Pending.ContainsKey("k");
            var p1 = LedgerExtras.PendingLine("k", T(1), "jdoe", 5, MarkerStatus.Ignored, null, null);
            var p3 = LedgerExtras.PendingLine("k", T(3), "jdoe", 6, MarkerStatus.Pending, "later", null);
            check(L + "Pending: open until a PendingDone no earlier than it; a Done before it, or none, leaves it open",
                open(LedgerExtras.Read(new[] { p1 })) && open(LedgerExtras.Read(new[] { p1, LedgerExtras.DoneLine("k", T(0), "j") }))
                && !open(LedgerExtras.Read(new[] { p1, LedgerExtras.DoneLine("k", T(1), "j") })) && !open(LedgerExtras.Read(new[] { LedgerExtras.DoneLine("k", T(2), "j"), p1 })));
            var reopened = LedgerExtras.Read(new[] { p3, LedgerExtras.DoneLine("k", T(2), "j"), p1 });
            check(L + "Pending: a newer pending after a Done is open with its own values, and of two open ones the latest clock wins",
                reopened.Pending.ContainsKey("k") && reopened.Pending["k"].SeqAtPending == 6 && reopened.Pending["k"].Note == "later" && reopened.Pending["k"].Status == MarkerStatus.Pending
                && LedgerExtras.Read(new[] { p1, p3 }).Pending.ContainsKey("k") && LedgerExtras.Read(new[] { p1, p3 }).Pending["k"].SeqAtPending == 6);
            bool pendAgree = true; string pfirst = null;
            foreach (var perm in MarkerIdentityChecks.Permutations(new[] { p1, p3, LedgerExtras.DoneLine("k", T(2), "j"), LedgerExtras.IssueLine("k", T(1), "j", "JD-1"), LedgerExtras.ReconciledLine("k", T(2), "j", "model", "Ignored") }))
            {
                var t = ViewText(LedgerExtras.Read(perm));
                if (pfirst == null) pfirst = t; else pendAgree &= t == pfirst;
            }
            check(L + "Pending, Done, Issue and Reconciled lines in any of the 120 file orders give the same view", pendAgree && pfirst.Length > 0 && pfirst.Contains("R|k|model|Ignored"));

            var issues = LedgerExtras.Read(new[] { LedgerExtras.IssueLine("k", T(2), "a", "JD-0002"), LedgerExtras.IssueLine("k", T(1), "a", "JD-0001") });
            check(L + "Issue: the latest clock wins", issues.Issues.ContainsKey("k") && issues.Issues["k"].IssueNo == "JD-0002");

            var dups = LedgerExtras.Read(new[]
            {
                LedgerExtras.DuplicateLine("k2", T(2), "a", "x", "y", MarkerStatus.Ignored, "", ""), LedgerExtras.DuplicateLine("k1", T(2), "a", "x", "z", MarkerStatus.Pending, "", ""),
                LedgerExtras.DuplicateLine("k1", T(1), "a", "x", "w", MarkerStatus.Pending, "", "")
            });
            check(L + "DuplicateNotKept: every line is history, listed by clock then key", dups.Duplicates.Select(d => d.Key + "@" + d.RemovedIssueNo).SequenceEqual(Texts("k1@w", "k1@z", "k2@y")));

            var junk = new string[] { null, "", "# comment", "k", "k\tAssigned", "k\tAssigned\tnot a date\tby\tx", "k\tPendingToModel\t" + T(1).ToString("o", CultureInfo.InvariantCulture) + "\tby\tnote\tnotanumber\tS\tIgnored\t",
                                      "k\tPendingToModel\t" + T(1).ToString("o", CultureInfo.InvariantCulture) + "\tby\tnote\t5\tS\tBogus\t", "k\tIssue", "k\tDuplicateNotKept\t" + T(1).ToString("o", CultureInfo.InvariantCulture) };
            LedgerExtras.View jv = null;
            check(L + "junk and short lines never throw and read as nothing", NoThrow(() => { jv = LedgerExtras.Read(junk); LedgerExtras.Read(null); }) && jv != null && jv.Pending.Count == 0 && jv.Issues.Count == 0);
        }

        // ------------------------------------------------------------------ today's replay is untouched

        private static void OldReplay(Action<string, bool?> check)
        {
            var oldLines = new[]
            {
                Old("a|0|b", "Ignored", T(1), "kdoe", "false positive"), Old("c|0|d", "Pending", T(2)), Old("c|0|d", "Gone", T(3)), Old("e|0|f", "Coordinated", T(1), "jdoe", "sleeve"),
                string.Join("\t", new[] { "g|0|h", "Pending", T(1).ToString("o", CultureInfo.InvariantCulture), "kdoe", "", "11", "Pipe", "Pipes", "22", "Wall", "Walls", "Current model" }),
                Old("g|0|h", "Ignored", T(4))
            };
            var newLines = new[]
            {
                LedgerExtras.AssignedLine("a|0|b", T(5), "kdoe", "asmith"), LedgerExtras.PendingLine("c|0|d", T(5), "kdoe", 3, MarkerStatus.Ignored, "x", "y"), LedgerExtras.DoneLine("c|0|d", T(6), "kdoe"),
                LedgerExtras.IssueLine("e|0|f", T(5), "kdoe", "JD-0001"), LedgerExtras.DuplicateLine("g|0|h", T(5), "kdoe", "JD-0001", "KD-0001", MarkerStatus.Ignored, "n", ""),
                LedgerExtras.ReconciledLine("a|0|b", T(5), "kdoe", "model", "Assigned")
            };
            int e1, e2;
            var alone = Replay(oldLines, out e1);
            var mixed = Replay(oldLines.Concat(newLines).Concat(Enumerable.Reverse(newLines)), out e2);
            check(L + "today's replay gives exactly the same decisions and the same event count with the new lines in the ledger", alone.Count == 4 && DecisionsText(alone) == DecisionsText(mixed) && e1 == e2);
            check(L + "...and the new lines do not disturb the element detail it carries (names stay on the decision)", mixed["g|0|h"].HostName == "Pipe" && mixed["c|0|d"].Gone);
            // The reader that SHIPPED (abe7dfd), copied verbatim: every line the schedule can write, with the ledger clean, is neither
            // dropped, nor "repaired" (a 10-field line), nor taken for element detail (12 fields or more).
            int se1, sl1, sr1, se2, sl2, sr2;
            var shippedAlone = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            ShippedClashStatusReplay.Replay(oldLines, shippedAlone, out se1, out sl1, out sr1);
            var reconcileNow = MarkerReconcile.Plan(new[] { M("m|0|n", MarkerStatus.Ignored, "model note", "asmith", "kdoe", "JD-0009"), M("c|0|d", MarkerStatus.Resolved), M("a|0|b", MarkerStatus.Reopened) },
                Replay(oldLines), LedgerExtras.Read(oldLines), T(60));
            var shippedMixed = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            ShippedClashStatusReplay.Replay(oldLines.Concat(newLines).Concat(reconcileNow.Lines), shippedMixed, out se2, out sl2, out sr2);
            var decisionOnly = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            int se3, sl3, sr3;
            ShippedClashStatusReplay.Replay(oldLines.Concat(reconcileNow.Lines.Where(x => { var ev = Split(x)[1]; return ev == "Gone" || ev == "Pending" || ev == "Ignored" || ev == "Coordinated" || ev == "New"; })), decisionOnly, out se3, out sl3, out sr3);
            check(L + "the shipped reader (abe7dfd, verbatim copy): none of the new lines, nor the lines reconcile writes, is dropped, repaired or read as detail: 0 legacy, 0 repaired, the same decisions and event count as with only the status lines",
                sl2 == 0 && sr2 == 0 && sl1 == 0 && sr1 == 0 && DecisionsText(shippedMixed) == DecisionsText(decisionOnly) && se2 == se3);
            check(L + "...and every line reconcile writes has a field count outside the shipped reader's repair (10) and detail (12 or more) zones",
                reconcileNow.Lines.All(x => { int n = Split(x).Length; return n != 10 && n < 12; }));
            check(L + "...and the status lines are byte for byte today's five-field layout", reconcileNow.Decisions.Where(x => Split(x)[1] != "Assigned" && Split(x)[1] != "Issue").All(x => Split(x).Length == 5));
            check(L + "reading only the new events ignores the old ones", ViewText(LedgerExtras.Read(oldLines)) == "" && ViewText(LedgerExtras.Read(oldLines.Concat(newLines))) == ViewText(LedgerExtras.Read(newLines)));
        }

        private static void LedgerFuzz(Action<string, bool?> check)
        {
            var rnd = new Random(20261103);
            int bad = 0, badOld = 0;
            string[] keys = { "a|0|b", "c|0|d", "e|0|f" };
            for (int i = 0; i < 300; i++)
            {
                var lines = new List<string>();
                int n = rnd.Next(1, 25);
                for (int j = 0; j < n; j++)
                {
                    string key = keys[rnd.Next(keys.Length)];
                    var when = T(rnd.Next(0, 12));
                    switch (rnd.Next(8))
                    {
                        case 0: lines.Add(Old(key, "Pending", when)); break;
                        case 1: lines.Add(Old(key, "Gone", when)); break;
                        case 2: lines.Add(LedgerExtras.AssignedLine(key, when, "u" + rnd.Next(3), rnd.Next(2) == 0 ? "" : "as" + rnd.Next(3))); break;
                        case 3: lines.Add(LedgerExtras.PendingLine(key, when, "u", rnd.Next(1, 4), rnd.Next(2) == 0 ? MarkerStatus.Ignored : (MarkerStatus?)null, rnd.Next(2) == 0 ? "n" : null, null)); break;
                        case 4: lines.Add(LedgerExtras.DoneLine(key, when, "u")); break;
                        case 5: lines.Add(LedgerExtras.IssueLine(key, when, "u", "JD-000" + rnd.Next(1, 4))); break;
                        case 6: lines.Add(LedgerExtras.DuplicateLine(key, when, "u", "x" + rnd.Next(2), "y" + rnd.Next(2), MarkerStatus.Ignored, "", "")); break;
                        default: lines.Add(Old(key, "Ignored", when, "u" + rnd.Next(2), "n")); break;
                    }
                }
                var shuffled = lines.OrderBy(_ => rnd.Next()).ToList();
                // Two issue lines with the same clock and different numbers are ambiguous by design: the later in ordinal text wins, so the view is still order-independent.
                if (ViewText(LedgerExtras.Read(lines)) != ViewText(LedgerExtras.Read(shuffled))) bad++;
                var oldOnly = lines.Where(x => { var ev = Split(x)[1]; return ev == "Pending" || ev == "Gone" || ev == "Ignored"; }).ToList();
                if (DecisionsText(Replay(lines)) != DecisionsText(Replay(oldOnly))) badOld++;
            }
            check(L + "fuzz: 300 mixed histories read the same in any order (seed 20261103)", bad == 0);
            check(L + "fuzz: today's replay of a mixed history equals its replay of the old lines alone", badOld == 0);
        }

        // ------------------------------------------------------------------ reconcile: direction and mapping

        private static void Direction(Action<string, bool?> check)
        {
            var now = T(60);
            var made = Plan(new[] { M("k", MarkerStatus.Ignored, "n", "", "kdoe") }, Array.Empty<string>(), now);
            var line = made.Decisions.Count == 1 ? made.Decisions[0] : "";
            var f = Split(line);
            var entry = line.Length > 0 ? Replay(made.Lines) : new Dictionary<string, ClashStatusEntry>();
            check(R + "a decision only the model has is appended in today's five-field layout: the marker's status, note and author, at this PC's now",
                made.Decisions.Count == 1 && f[0] == "k" && f[1] == "Ignored" && f[2] == now.ToString("o", CultureInfo.InvariantCulture) && f[3] == "kdoe" && f[4] == "n" && f.Length == 5
                && entry["k"].Kind == ClashStatusKind.Ignored && entry["k"].By == "kdoe" && entry["k"].Note == "n" && made.Overridden == 0);
            var pairLines = made.Lines.Count == 2 ? new[] { Split(made.Lines[0]), Split(made.Lines[1]) } : null;
            check(R + "each decision line is followed by one Reconciled line, five fields, at the same time, saying where it came from (\"model Ignored\")",
                pairLines != null && pairLines[1].Length == 5 && pairLines[1][0] == "k" && pairLines[1][1] == "Reconciled" && pairLines[1][2] == pairLines[0][2] && pairLines[1][3] == "kdoe"
                && pairLines[1][4] == "model Ignored" && LedgerExtras.Read(made.Lines).Reconciled["k"].Event == "Ignored");
            var three = Plan(new[] { M("k", MarkerStatus.Ignored, "n", "asmith", "kdoe", "JD-0003") }, Array.Empty<string>(), now);
            check(R + "a status, an assignee and an issue number are three decision lines, each followed by its own Reconciled line, in that order",
                three.Decisions.Count == 3 && three.Lines.Select(x => Split(x)[1]).SequenceEqual(Texts("Ignored", "Reconciled", "Assigned", "Reconciled", "Issue", "Reconciled")));
            var goneLines = Plan(new[] { M("k", MarkerStatus.Resolved) }, new[] { Old("k", "Pending", T(1)) }, now);
            check(R + "a Gone line is today's five-field Gone line and is followed by Reconciled \"model Gone\"",
                goneLines.Lines.Count == 2 && Split(goneLines.Lines[0]).Length == 5 && Split(goneLines.Lines[0])[1] == "Gone" && Split(goneLines.Lines[1])[4] == "model Gone");

            bool mapOk = true;
            var expectKind = new Dictionary<MarkerStatus, string>
            {
                { MarkerStatus.Pending, "Pending" }, { MarkerStatus.Coordinated, "Coordinated" }, { MarkerStatus.Ignored, "Ignored" }, { MarkerStatus.Reopened, "Pending" }
            };
            foreach (var kv in expectKind)
            {
                var r = Plan(new[] { M("k", kv.Key) }, Array.Empty<string>(), now);
                mapOk &= r.Decisions.Count == 1 && Split(r.Decisions[0])[1] == kv.Value;
            }
            mapOk &= Plan(new[] { M("k", MarkerStatus.New) }, Array.Empty<string>(), now).Decisions.Count == 0;
            mapOk &= Plan(new[] { M("k", MarkerStatus.Resolved) }, Array.Empty<string>(), now).Decisions.Count == 0;
            check(R + "status mapping: Pending, Coordinated, Ignored as they are, Reopened to Pending; New with nothing, and Resolved with no decision in the ledger, write nothing (D27, D28)", mapOk);
            check(R + "a New marker that carries a note is a decision (the ledger keeps New with a note)", Plan(new[] { M("k", MarkerStatus.New, "just a note") }, Array.Empty<string>(), now).Decisions.Count == 1);

            var pendingLedger = new[] { Old("k", "Pending", T(1), "jdoe") };
            var res = Plan(new[] { M("k", MarkerStatus.Resolved) }, pendingLedger, now);
            var gone = res.Decisions.Count == 1 ? Replay(pendingLedger.Concat(res.Lines)) : null;
            check(R + "a Resolved marker over a ledger decision appends a Gone line, once, and is not counted as a disagreement (D27)",
                res.Decisions.Count == 1 && Split(res.Decisions[0])[1] == "Gone" && gone["k"].Gone && res.Overridden == 0
                && Plan(new[] { M("k", MarkerStatus.Resolved) }, pendingLedger.Concat(res.Lines), now.AddMinutes(5)).Decisions.Count == 0);

            bool equalOk = true;
            foreach (var s in new[] { MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored })
                equalOk &= Plan(new[] { M("k", s, "same") }, new[] { Old("k", s.ToString(), T(1), "jdoe", "same") }, now).Decisions.Count == 0;
            check(R + "when marker and ledger agree nothing is written, for every status", equalOk);

            bool table = true;
            var human = new[] { MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored };
            foreach (var led in human)
                foreach (var mk in human.Concat(new[] { MarkerStatus.Reopened }))
                {
                    var ledger = new[] { Old("k", led.ToString(), T(1), "jdoe", "ledger note") };
                    var r = Plan(new[] { M("k", mk, "model note", "", "kdoe") }, ledger, now);
                    var after = Replay(ledger.Concat(r.Lines));
                    table &= r.Decisions.Count == 1 && r.Overridden == 1 && after["k"].Note == "model note" && after["k"].By == "kdoe"
                             && after["k"].Kind == MarkerReconcile.KindOf(mk);
                }
            check(R + "the model wins: for every ledger status against every different marker status the ledger ends up holding the marker's, and the disagreement is counted (RF4)", table);

            var overridden = Plan(new[] { M("a", MarkerStatus.Pending), M("b", MarkerStatus.Pending), M("c", MarkerStatus.Pending) },
                new[] { Old("a", "Ignored", T(1)), Old("b", "Pending", T(1), "kdoe", ""), Old("zz", "Ignored", T(1)) }, now);
            check(R + "only a key whose ledger decision differed counts as overridden: a differing one yes, an equal one no, a new one no", overridden.Overridden == 1 && overridden.Decisions.Count == 2);

            var pend = Plan(new[] { M("k", MarkerStatus.Ignored, "model"), M("m", MarkerStatus.Ignored, "model") },
                new[] { Old("k", "Pending", T(1)), LedgerExtras.PendingLine("k", T(2), "jdoe", 3, MarkerStatus.Coordinated, null, null) }, now);
            check(R + "a key with an open pending-to-model entry is skipped (its decision stays shown); others are still reconciled",
                pend.Decisions.Count == 1 && Split(pend.Decisions[0])[0] == "m" && pend.Overridden == 0);
            var closed = Plan(new[] { M("k", MarkerStatus.Ignored, "model") },
                new[] { Old("k", "Pending", T(1)), LedgerExtras.PendingLine("k", T(2), "jdoe", 3, MarkerStatus.Coordinated, null, null), LedgerExtras.DoneLine("k", T(3), "jdoe") }, now);
            check(R + "...and once the pending entry is closed it is reconciled like any other", closed.Decisions.Count == 1);

            var reopen = Plan(new[] { M("k", MarkerStatus.Reopened, "") }, new[] { Old("k", "Pending", T(1)), Old("k", "Gone", T(2)) }, now);
            var reopened = reopen.Decisions.Count == 1 ? Replay(new[] { Old("k", "Pending", T(1)), Old("k", "Gone", T(2)) }.Concat(reopen.Lines)) : null;
            check(R + "a Reopened marker over a ledger decision that was Gone writes a newer Pending, so the pair is outstanding again (D28)",
                reopen.Decisions.Count == 1 && Split(reopen.Decisions[0])[1] == "Pending" && !reopened["k"].Gone);
            check(R + "a Pending marker over a ledger decision this PC saw fixed (Gone) with the same decision writes nothing: the local observation stands",
                Plan(new[] { M("k", MarkerStatus.Pending, "") }, new[] { Old("k", "Pending", T(1)), Old("k", "Gone", T(2)) }, now).Decisions.Count == 0);
        }

        // ------------------------------------------------------------------ reconcile: assignee and issue number

        private static void Fields(Action<string, bool?> check)
        {
            var now = T(60);
            var assign = Plan(new[] { M("k", MarkerStatus.New, "", "asmith") }, Array.Empty<string>(), now);
            var af = assign.Decisions.Count == 1 ? Split(assign.Decisions[0]) : Array.Empty<string>();
            check(R + "an assignee only the model has is appended as an Assigned line by the marker's author",
                af.Length == 5 && af[1] == "Assigned" && af[3] == "kdoe" && af[4] == "asmith" && LedgerExtras.Read(assign.Lines).Assigned["k"].Assignee == "asmith");
            var cleared = Plan(new[] { M("k", MarkerStatus.New, "", "") }, new[] { LedgerExtras.AssignedLine("k", T(1), "jdoe", "asmith") }, now);
            check(R + "an assignee cleared in the model clears it in the ledger; equal assignees, and none on both sides, write nothing",
                cleared.Decisions.Count == 1 && LedgerExtras.Read(new[] { LedgerExtras.AssignedLine("k", T(1), "jdoe", "asmith") }.Concat(cleared.Lines)).Assigned["k"].Assignee == ""
                && Plan(new[] { M("k", MarkerStatus.New, "", "asmith") }, new[] { LedgerExtras.AssignedLine("k", T(1), "jdoe", "asmith") }, now).Decisions.Count == 0
                && Plan(new[] { M("k", MarkerStatus.New) }, Array.Empty<string>(), now).Decisions.Count == 0);

            var issue = Plan(new[] { M("k", MarkerStatus.New, "", "", "kdoe", "JD-0042") }, Array.Empty<string>(), now);
            var ifields = issue.Decisions.Count == 1 ? Split(issue.Decisions[0]) : Array.Empty<string>();
            check(R + "a marker's issue number is written into the ledger once, and again only if it changes",
                issue.Decisions.Count == 1 && ifields[1] == "Issue" && ifields[4] == "JD-0042"
                && Plan(new[] { M("k", MarkerStatus.New, "", "", "kdoe", "JD-0042") }, issue.Lines, now.AddMinutes(1)).Decisions.Count == 0
                && Plan(new[] { M("k", MarkerStatus.New, "", "", "kdoe", "JD2-0042") }, issue.Lines, now.AddMinutes(1)).Decisions.Count == 1);

            var by = Plan(new[] { new MarkerReconcile.ModelMarker { Key = "k", Status = MarkerStatus.Ignored, ChangedBy = "", ReportedBy = "jdoe" } }, Array.Empty<string>(), now);
            check(R + "a marker with no ChangedBy is written under the reporter", by.Decisions.Count == 1 && Split(by.Decisions[0])[3] == "jdoe");
        }

        // ------------------------------------------------------------------ reconcile: the clock

        private static void Clock(Action<string, bool?> check)
        {
            var now = T(60);
            var future = new[] { Old("k", "Ignored", T(300), "jdoe", "ledger") };
            var r = Plan(new[] { M("k", MarkerStatus.Pending, "model") }, future, now);
            var when = r.Decisions.Count == 1 ? DateTime.Parse(Split(r.Decisions[0])[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : DateTime.MinValue;
            var replayed = Replay(future.Concat(r.Lines));
            check(R + "this PC's clock is the line's time, except that it is never earlier than one tick after the latest ledger event for the key (D30)",
                r.Decisions.Count == 1 && when == T(300).AddTicks(1) && replayed["k"].Kind == ClashStatusKind.Pending && replayed["k"].Note == "model"
                && Split(Plan(new[] { M("k", MarkerStatus.Pending, "model") }, new[] { Old("k", "Ignored", T(1)) }, now).Decisions[0])[2] == now.ToString("o", CultureInfo.InvariantCulture));
            check(R + "a second reconcile after the lines were appended finds nothing, even with the ledger's clock ahead", Plan(new[] { M("k", MarkerStatus.Pending, "model") }, future.Concat(r.Lines), now).Decisions.Count == 0);
            var orphan = new[] { Old("k", "Gone", T(300)) };
            var o = Plan(new[] { M("k", MarkerStatus.Reopened) }, orphan, now);
            var ow = o.Decisions.Count == 1 ? DateTime.Parse(Split(o.Decisions[0])[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : DateTime.MinValue;
            check(R + "a Gone with no decision (the replay drops it) still sets the floor: the line is timed after it, so the next reconcile has nothing to do",
                o.Decisions.Count == 1 && ow == T(300).AddTicks(1) && LedgerExtras.Read(orphan).GoneAt["k"] == T(300) && Plan(new[] { M("k", MarkerStatus.Reopened) }, orphan.Concat(o.Lines), now).Decisions.Count == 0);
            var edgeNow = T(60);
            var withNullDecisions = MarkerReconcile.Plan(new[] { M("k", MarkerStatus.Ignored) }, null, new LedgerExtras.View(), edgeNow);
            var withNullExtras = MarkerReconcile.Plan(new[] { M("k", MarkerStatus.Ignored) }, new Dictionary<string, ClashStatusEntry>(), null, edgeNow);
            check(R + "no decisions or no extras at all is the same as empty ones", withNullDecisions.Decisions.Count == 1 && withNullExtras.Decisions.Count == 1);

            var sameClock = new[] { Old("k", "Pending", T(1)), Old("k", "Gone", T(1)) };
            var sc = Plan(new[] { M("k", MarkerStatus.Reopened) }, sameClock, edgeNow);
            check(R + "a Gone at the very clock of its decision still counts as Gone, and a Reopened marker reopens it", sc.Decisions.Count == 1 && Replay(sameClock.Concat(sc.Lines))["k"].Gone == false);

            var cleared = Plan(new[] { M("k", MarkerStatus.New, "") }, new[] { Old("k", "Ignored", T(1), "jdoe", "") }, edgeNow);
            var clearedKept = Replay(new[] { Old("k", "Ignored", T(1), "jdoe", "") }.Concat(cleared.Lines));
            check(R + "a marker back to New with no note clears the ledger decision (the model wins) and counts as a disagreement",
                cleared.Decisions.Count == 1 && Split(cleared.Decisions[0])[1] == "New" && cleared.Overridden == 1 && !clearedKept.ContainsKey("k"));
            var noteOnly = new[] { Old("k", "New", T(1), "jdoe", "x") };
            check(R + "a New decision that carries a note is compared by its note: same note nothing, another note a line",
                Plan(new[] { M("k", MarkerStatus.New, "x") }, noteOnly, edgeNow).Decisions.Count == 0 && Plan(new[] { M("k", MarkerStatus.New, "y") }, noteOnly, edgeNow).Decisions.Count == 1);

            var handBuilt = new Dictionary<string, ClashStatusEntry> { { "k", new ClashStatusEntry { Kind = ClashStatusKind.Pending, WhenUtc = T(1), By = "u", Note = null } } };
            var handExtras = new LedgerExtras.View();
            handExtras.Assigned["k"] = new LedgerExtras.AssignedEntry { Assignee = null, By = "u", WhenUtc = T(1) };
            check(R + "a hand-built decision with no note, and an assignee entry with no name, count as empty",
                MarkerReconcile.Plan(new[] { M("k", MarkerStatus.Pending, "") }, handBuilt, handExtras, edgeNow).Decisions.Count == 0
                && MarkerReconcile.Plan(new[] { M("k", MarkerStatus.Pending, "", "asmith") }, handBuilt, handExtras, edgeNow).Decisions.Count == 1);

            check(R + "null inputs, markers with no key and null text never throw",
                NoThrow(() =>
                {
                    MarkerReconcile.Plan(null, null, null, now);
                    MarkerReconcile.Plan(new MarkerReconcile.ModelMarker[] { null, new MarkerReconcile.ModelMarker { Key = null }, new MarkerReconcile.ModelMarker { Key = "k", Note = null, AssignedTo = null, ChangedBy = null, ReportedBy = null, IssueNo = null } },
                        new Dictionary<string, ClashStatusEntry>(), new LedgerExtras.View(), now);
                }) && MarkerReconcile.Plan(null, null, null, now).Decisions.Count == 0);
        }

        // ------------------------------------------------------------------ reconcile: fuzz

        private static void ReconcileFuzz(Action<string, bool?> check)
        {
            var rnd = new Random(20261104);
            string whyIdem = "", whyFollow = "";
            int notIdem = 0, notFollowed = 0, thrown = 0, notShipped = 0;
            string[] keys = { "a|0|b", "c|0|d", "e|0|f", "g|0|h" };
            for (int i = 0; i < 300; i++)
            {
                var ledger = new List<string>();
                for (int j = 0, n = rnd.Next(0, 10); j < n; j++)
                {
                    string key = keys[rnd.Next(keys.Length)];
                    var when = T(rnd.Next(0, 400));
                    switch (rnd.Next(6))
                    {
                        case 0: ledger.Add(Old(key, "Pending", when, "u", "n" + rnd.Next(2))); break;
                        case 1: ledger.Add(Old(key, "Ignored", when, "u")); break;
                        case 2: ledger.Add(Old(key, "Gone", when)); break;
                        case 3: ledger.Add(LedgerExtras.AssignedLine(key, when, "u", rnd.Next(2) == 0 ? "" : "as")); break;
                        case 4: ledger.Add(LedgerExtras.PendingLine(key, when, "u", 1, MarkerStatus.Ignored, null, null)); break;
                        default: ledger.Add(LedgerExtras.IssueLine(key, when, "u", "JD-000" + rnd.Next(1, 3))); break;
                    }
                }
                var markers = new List<MarkerReconcile.ModelMarker>();
                foreach (var key in keys.Where(_ => rnd.Next(4) != 0))
                    markers.Add(M(key, (MarkerStatus)rnd.Next(6), rnd.Next(2) == 0 ? "" : "m" + rnd.Next(2), rnd.Next(2) == 0 ? "" : "as", "kdoe", rnd.Next(2) == 0 ? "" : "JD-000" + rnd.Next(1, 3)));
                try
                {
                    var now = T(rnd.Next(0, 200));
                    var plan = Plan(markers, ledger, now);
                    var after = ledger.Concat(plan.Lines).ToList();
                    int se, sl, sr;
                    ShippedClashStatusReplay.Replay(after, new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal), out se, out sl, out sr);
                    if (sl != 0 || sr != 0) notShipped++;
                    var again = Plan(markers, after, now);
                    if (again.Decisions.Count != 0)
                    {
                        notIdem++;
                        if (whyIdem.Length == 0) whyIdem = "case " + i + " markers " + string.Join(",", markers.Select(x => x.Key + ":" + x.Status + ":" + x.Note + ":" + x.AssignedTo + ":" + x.IssueNo)) + " ledger " + string.Join(" / ", ledger.Select(x => x.Replace("\t", ">"))) + " first " + string.Join(" / ", plan.Lines.Select(x => x.Replace("\t", ">"))) + " again " + string.Join(" / ", again.Lines.Select(x => x.Replace("\t", ">")));
                    }

                    var decisions = Replay(after);
                    var extras = LedgerExtras.Read(after);
                    foreach (var m in markers)
                    {
                        if (extras.Pending.ContainsKey(m.Key)) continue;
                        if (m.Status == MarkerStatus.Resolved) continue;
                        var kind = MarkerReconcile.KindOf(m.Status);
                        ClashStatusEntry e;
                        bool has = decisions.TryGetValue(m.Key, out e);
                        bool wantsNone = kind == ClashStatusKind.New && m.Note == "";
                        bool ok = wantsNone ? !has : has && e.Kind == kind && e.Note == m.Note;
                        if (!ok) notFollowed++;
                    }
                }
                catch (Exception) { thrown++; }
            }
            check(R + "fuzz: 300 random ledgers and markers (clocks ahead and behind, open pending entries): reconcile never throws (seed 20261104)", thrown == 0);
            check(R + "fuzz: after the lines are appended, reconciling again writes nothing (idempotent, T6)" + (notIdem == 0 ? "" : " [" + whyIdem + "]"), notIdem == 0);
            check(R + "fuzz: the shipped reader (abe7dfd copy) reads every appended ledger with nothing dropped as legacy and nothing repaired", notShipped == 0);
            check(R + "fuzz: after the lines are appended, the ledger holds each non-pending, non-resolved marker's decision exactly", notFollowed == 0);
        }
    }
}
