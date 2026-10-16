using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveClash.Core
{
    /// <summary>
    /// What a person decided about a clash — as opposed to whether it still
    /// geometrically exists, which the engine recomputes and never stores.
    ///
    /// Keeping those two apart is the whole design. A clash that is fixed simply
    /// stops being found and vanishes on its own; there is deliberately no manual
    /// "Resolved", because it would start lying the moment somebody dragged the pipe
    /// back. What genuinely has to persist is the opposite case: a clash that is
    /// real, will stay real, and has been accepted anyway.
    /// </summary>
    public enum ClashStatusKind
    {
        /// <summary>Never reviewed. The default for anything newly found.</summary>
        New = 0,

        /// <summary>Reviewed, still needs fixing.</summary>
        Pending = 1,

        /// <summary>
        /// Deliberately accepted — a pipe through a designed sleeve, say. Still a
        /// genuine intersection, so it is never auto-removed, but it stops nagging:
        /// hidden unless "Show all" is on.
        /// </summary>
        Coordinated = 2,

        /// <summary>A false positive the suppressor did not catch. Hidden.</summary>
        Ignored = 3,
    }

    public class ClashStatusEntry
    {
        public ClashStatusKind Kind;
        public DateTime WhenUtc;
        public string By;
        public string Note;

        /// <summary>
        /// Element detail captured when the decision was made, so the pair still reads with
        /// names in the record after it stops clashing and drops out of the live results.
        /// Zero/empty on a decision recorded before this was added, or one made while the
        /// pair was not in the live set.
        /// </summary>
        public long HostId, OtherId;
        public string HostName, HostCategory, OtherName, OtherCategory, ModelName;

        public bool HasDetail { get { return HostId != 0 || OtherId != 0; } }

        /// <summary>
        /// The pair was last observed to have stopped clashing. If it is back in the
        /// results while this is set, it came back after being fixed — see
        /// ClashStatusStore.IsReopened. (Named in prose, not a cref: this file is
        /// linked into the headless test project, where that type does not exist.)
        /// </summary>
        public bool Gone;
        public DateTime GoneUtc;
    }

    /// <summary>
    /// Turning an append-only ledger into the set of decisions currently in force.
    ///
    /// SPLIT OUT OF ClashStatusStore SO IT CAN BE TESTED. The store owns the file, the
    /// lock and the change event; this owns the line format and the ordering rules, and
    /// touches nothing else — no Revit type, no disk, no clock. That matters because the
    /// hard part here is not parsing, it is what happens when two people's events arrive
    /// interleaved, and every bug this code has had was an ordering bug invisible to a
    /// check that only ever fed it a tidy file. Reachable only through a path, it could
    /// not be asserted at all without writing into somebody's real triage record.
    ///
    /// THE ORDERING RULES, which are the whole point:
    ///
    ///   - Decisions replay by CLOCK, not by file position. The newest timestamp for a
    ///     key wins no matter where in the file it sits.
    ///   - Gone events are collected during the pass and applied after it, against
    ///     whichever decision won. Applied inline they depended on file order twice
    ///     over: a Gone before its own decision had nothing to mark, and a decision
    ///     replayed after a Gone overwrote the entry and lost it.
    ///   - A Gone no older than its decision resolves the pair. A decision made AFTER a
    ///     Gone is somebody reopening it, and must stay outstanding.
    /// </summary>
    public static class ClashStatusReplay
    {
        /// <summary>
        /// TAB, not a pipe.
        ///
        /// This matters more than it looks: ClashResult.Key is itself built as
        /// "lo|0|hi", so a pipe-separated file splits the KEY into three fields.
        /// The first version of this store did exactly that — every row loaded back
        /// with a truncated key and "0" parsed as the status (Enum.Parse accepts
        /// numeric strings, so it silently became New). Decisions were written and
        /// then never matched anything on reload, so status persistence looked
        /// implemented and did nothing at all.
        ///
        /// A key can never contain a tab, so this separator cannot collide.
        /// </summary>
        public const char Sep = '\t';

        /// <summary>Written into the file for a "no longer clashing" observation.</summary>
        public const string GoneEvent = "Gone";

        /// <summary>
        /// Replay every event into <paramref name="byKey"/>, leaving the decisions in
        /// force. The dictionary is added to, not cleared — the caller decides whether
        /// this is a fresh load or a merge.
        /// </summary>
        /// <param name="lines">The ledger, in file order. Order does not matter to the
        /// result — that is the point of this class — but it is what the file gives.</param>
        /// <param name="byKey">Populated with the decisions in force. Added to, never
        /// cleared: a fresh load hands in an empty dictionary, a merge does not.</param>
        /// <param name="events">Lines that carried a real event.</param>
        /// <param name="legacy">Lines from the broken pipe-separated format, skipped.</param>
        /// <param name="repaired">Lines recovered from the char+long bug.</param>
        public static void Replay(
            IEnumerable<string> lines,
            Dictionary<string, ClashStatusEntry> byKey,
            out int events, out int legacy, out int repaired)
        {
            events = 0; legacy = 0; repaired = 0;
            if (lines == null || byKey == null) return;

            // The LATEST Gone seen per key. Held back until every decision has been
            // replayed — see the class note for what applying it inline cost.
            var goneAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            // The NEWEST decision line that carried element detail, per key, and whether the line
            // that won each key carried its own. Element detail used to be carried forward inline,
            // from whatever entry happened to be in the dictionary when a detail-less line arrived -
            // so it depended on file order, which is exactly what this class exists to remove. Found
            // by the shuffled-history fuzz check (FuzzChecks), on its first random history.
            var latestDetail = new Dictionary<string, ClashStatusEntry>(StringComparer.Ordinal);
            var winnerHadDetail = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (line == null || line.Length == 0 || line[0] == '#') continue;

                var p = line.Split(Sep);
                if (p.Length < 3)
                {
                    // A line from the broken pipe-separated format. It cannot be
                    // recovered — the key was mangled on the way in — so it is skipped
                    // rather than loaded as a phantom decision.
                    legacy++;
                    continue;
                }

                var k = p[0];
                var ev = p[1];

                DateTime when;
                if (!DateTime.TryParse(p[2], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                    when = DateTime.MinValue;

                var by = p.Length > 3 ? p[3] : "";
                var note = p.Length > 4 ? p[4] : "";

                // RECOVER LINES DAMAGED BY THE char+long BUG. See ClashStatusStore.Line():
                // the separator is a char, so `Sep + hostId` added 9 to the id rather than
                // writing a tab, twice — leaving TEN fields where there should be twelve,
                // and field 4 holding "<hostId+18><hostName>" instead of the note.
                //
                // The damage is exactly reversible: leading digits are the id plus 18, the
                // remainder is the name, and the note was empty (an event with a note took
                // the same path, so its note is lost — but an empty note is by far the
                // common case and the ids matter more). Repaired on READ so existing
                // ledgers keep working; nothing rewrites the file.
                bool damaged = p.Length == 10 && note.Length > 0 && char.IsDigit(note[0]);
                string recoveredHostName = null;
                long recoveredHostId = 0;
                if (damaged)
                {
                    int d = 0;
                    while (d < note.Length && char.IsDigit(note[d])) d++;
                    long rawId;
                    if (long.TryParse(note.Substring(0, d), NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out rawId))
                    {
                        recoveredHostId = rawId - 2 * (long)Sep;   // two swallowed tabs
                        recoveredHostName = note.Substring(d);
                        note = "";                                 // it was never a note
                    }
                    else damaged = false;
                }

                // Element detail (fields 5..11), present on lines written after it was
                // added. Absent on older lines and on Gone events.
                long hid = 0, oid = 0;
                string hn = "", hc = "", on = "", oc = "", mn = "";
                if (p.Length >= 12)
                {
                    _ = long.TryParse(p[5], out hid);   // 0 (no detail) when unreadable
                    hn = p[6]; hc = p[7];
                    _ = long.TryParse(p[8], out oid);
                    on = p[9]; oc = p[10]; mn = p[11];
                }
                else if (damaged)
                {
                    // Ten fields: [4] held the glued id+name, so everything after it sits
                    // two places earlier than it should.
                    hid = recoveredHostId;
                    hn = recoveredHostName ?? "";
                    hc = p[5];
                    _ = long.TryParse(p[6], out oid);
                    on = p[7]; oc = p[8]; mn = p[9];
                    repaired++;
                }

                ClashStatusEntry e;
                byKey.TryGetValue(k, out e);

                if (ev == GoneEvent)
                {
                    // Remembered, not applied — the decision it refers to may not have
                    // been replayed yet. Keep the latest; an older Gone can never win
                    // once a newer one exists.
                    DateTime seen;
                    if (!goneAt.TryGetValue(k, out seen) || when > seen) goneAt[k] = when;
                    events++;
                    continue;
                }

                // Initialised although every path assigns it: mutation testing rewrites the try below,
                // and a mutant that cannot compile is a mutant that cannot be tested.
                ClashStatusKind kind = ClashStatusKind.New;
                try { kind = (ClashStatusKind)Enum.Parse(typeof(ClashStatusKind), ev); }
                catch { continue; }

                // Replay in timestamp order, not file order: with two writers appending,
                // the file is interleaved and only the clock says which decision is
                // current.
                // `damaged` matters here too: a repaired ten-field line carries exactly
                // the same detail as a healthy twelve-field one, so guarding on the raw
                // field count alone would parse the recovery and then throw it away.
                bool hasDetail = p.Length >= 12 || damaged;

                // An entry already in the dictionary before this replay began (a merge) is history
                // too: its detail can be carried just like a line's.
                if (e != null && e.HasDetail && !winnerHadDetail.ContainsKey(k) && !latestDetail.ContainsKey(k))
                    latestDetail[k] = e;

                if (hasDetail)
                {
                    ClashStatusEntry ld = null;   // initialised so mutants of the condition still compile
                    if (!latestDetail.TryGetValue(k, out ld) || when > ld.WhenUtc)
                        latestDetail[k] = new ClashStatusEntry
                        {
                            WhenUtc = when, HostId = hid, HostName = hn, HostCategory = hc,
                            OtherId = oid, OtherName = on, OtherCategory = oc, ModelName = mn
                        };
                }

                if (e != null && e.WhenUtc > when) { events++; continue; }

                var ne = new ClashStatusEntry
                {
                    Kind = kind,
                    WhenUtc = when,
                    By = by,
                    Note = note,
                    Gone = false
                };
                if (hasDetail)
                {
                    ne.HostId = hid; ne.HostName = hn; ne.HostCategory = hc;
                    ne.OtherId = oid; ne.OtherName = on; ne.OtherCategory = oc; ne.ModelName = mn;
                }
                byKey[k] = ne;
                winnerHadDetail[k] = hasDetail;
                events++;
            }

            // A winning line without detail (older format, or a decision made while the pair was not
            // in the live set) takes the detail of the NEWEST line that had some, so the record still
            // reads with names. Done after the pass, so it is the newest by clock, not the latest read.
            // Keys whose winner was already in the dictionary before this replay (a merge) count too, when
            // that entry has no detail of its own.
            foreach (var d in latestDetail)
            {
                ClashStatusEntry we = null;   // initialised so mutants of the conditions still compile
                bool had = false;
                if (!byKey.TryGetValue(d.Key, out we) || ReferenceEquals(we, d.Value)) continue;
                if (winnerHadDetail.TryGetValue(d.Key, out had) ? had : we.HasDetail) continue;
                var ld = d.Value;
                we.HostId = ld.HostId; we.HostName = ld.HostName; we.HostCategory = ld.HostCategory;
                we.OtherId = ld.OtherId; we.OtherName = ld.OtherName; we.OtherCategory = ld.OtherCategory;
                we.ModelName = ld.ModelName;
            }

            // NOW apply the Gone events, against the decision that actually won.
            //
            // Still ">=": a Gone no older than the decision it lands on resolves it,
            // while a decision made AFTER a Gone is someone reopening the pair, and must
            // leave it outstanding. That test only means what it says once every decision
            // has been replayed, which is why it happens here rather than in the loop.
            foreach (var g in goneAt)
            {
                ClashStatusEntry ge;
                if (!byKey.TryGetValue(g.Key, out ge)) continue;   // no decision: nothing to resolve
                if (g.Value >= ge.WhenUtc) { ge.Gone = true; ge.GoneUtc = g.Value; }
            }

            // A decision of New is the absence of a decision — drop it so the set holds
            // only real ones.
            var doomed = new List<string>();
            foreach (var kv in byKey) if (kv.Value.Kind == ClashStatusKind.New) doomed.Add(kv.Key);
            foreach (var d in doomed) byKey.Remove(d);
        }
    }
}
