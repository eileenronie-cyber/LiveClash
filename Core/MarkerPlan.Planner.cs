using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    public static partial class MarkerPlan
    {
        /// <summary>Where a marker ended up in the plan. Only three outcomes change what the per-set counts say; every marker that is simply counted (written, resolved or left alone) is <c>Counted</c>.</summary>
        private enum Bucket
        {
            Counted,
            Removed,
            PassedOver,
            NotRetested
        }

        /// <summary>The plan, worked out in one pass over sorted inputs (every order is explicit and ordinal, so the answer does not depend on the order the inputs came in).</summary>
        private sealed class Planner
        {
            private static readonly StringComparer Ord = StringComparer.Ordinal;

            private readonly Input _input;
            private readonly Options _opt;
            private readonly string _user;
            private readonly DateTimeOffset _today;
            private readonly Result _result = new Result();
            private readonly List<Existing> _markers;
            private readonly List<Row> _sortedRows;
            private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>(Ord);
            private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(Ord);
            private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>(Ord);
            private readonly HashSet<string> _used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _handledPending = new HashSet<string>(Ord);
            private readonly Dictionary<string, string> _labels = new Dictionary<string, string>(Ord);
            private readonly Dictionary<string, Tuple<string, string>> _renumber = new Dictionary<string, Tuple<string, string>>(Ord);
            private IssueNumbers.PrefixMove _ownMove;

            public Planner(Input input)
            {
                _input = input ?? new Input();
                _opt = _input.Options ?? new Options();
                _user = _opt.UserName ?? "";
                _today = _opt.Today;
                _sortedRows = (_input.Rows ?? new List<Row>()).Where(r => r != null).OrderBy(RowText, Ord).ToList();
                _markers = (_input.Markers ?? new List<Existing>()).Where(m => m != null && m.MarkerUid != null).OrderBy(m => m.MarkerUid, Ord).ToList();
            }

            // ------------------------------------------------------------------ text

            private static string AccessReason(Existing m)
            {
                switch (m.Access)
                {
                    case MarkerAccess.HeldByOther: return "held by " + (string.IsNullOrEmpty(m.HeldBy) ? "another user" : m.HeldBy);
                    case MarkerAccess.ChangedInCentral: return "changed in central";
                    case MarkerAccess.InsideGroup: return "inside a group";
                    case MarkerAccess.NewerFormat: return "written by a newer LiveClash";
                    case MarkerAccess.Unreadable: return "unreadable record";
                    default: return "";
                }
            }

            /// <summary>
            /// Every field of a row as text, so two rows that tie on key and severity still sort the same way (D23). Each field that can hold free text is written as
            /// its length and then itself, so two different lists of fields never give one text, whatever a set name, a reason or a display value holds (a '|', say).
            /// An unknown (null) list, label, hash, key, map or seed is "~", which no length text starts with.
            /// </summary>
            private static string RowText(Row r)
            {
                Func<double, string> d = x => x.ToString("R", CultureInfo.InvariantCulture);
                var s = r.Seed;
                return string.Concat(
                    Len(r.Key), Len(r.Severity), Len(r.Verdict.ToString()), Len(r.NotRetestedReason), r.Live ? "1" : "0", Len(d(r.RuleClearanceMm)), r.RuleInsulated ? "1" : "0",
                    r.HasPoint ? "1" : "0", Len(d(r.X)), Len(d(r.Y)), Len(d(r.Z)), Len(r.SetName), Opt(r.GroupLabel), Opt(r.ShownHash), Len(r.HostUid), Len(r.LinkUid), Len(r.OtherUid),
                    Len(r.HostId.ToString(CultureInfo.InvariantCulture)), Len(r.LinkId.ToString(CultureInfo.InvariantCulture)), Len(r.OtherId.ToString(CultureInfo.InvariantCulture)),
                    r.AlsoSets == null ? "~" : Len(r.AlsoSets.Count.ToString(CultureInfo.InvariantCulture)) + string.Concat(r.AlsoSets.OrderBy(x => x, Ord).Select(Len)), Len(r.RememberedIssueNo),
                    Opt(r.GroupKey), r.Shown == null ? "~" : Len(r.Shown.Count.ToString(CultureInfo.InvariantCulture)) + string.Concat(r.Shown.OrderBy(kv => kv.Key, Ord).Select(kv => Len(kv.Key) + Len(kv.Value))),
                    s == null ? "~" : Len(s.Status.ToString()) + Len(s.Note) + Len(s.AssignedTo) + Len(s.By) + Len(s.At.HasValue ? s.At.Value.UtcTicks.ToString(CultureInfo.InvariantCulture) : ""));
            }

            /// <summary>A text as its length (ten digits) and then itself; a missing text is the empty one.</summary>
            private static string Len(string text)
            {
                text = text ?? "";
                return text.Length.ToString("D10", CultureInfo.InvariantCulture) + text;
            }

            /// <summary>A text that may be unknown: "~" for null, else its length text.</summary>
            private static string Opt(string text)
            {
                return text == null ? "~" : Len(text);
            }

            private static Dictionary<string, string> CopyOf(Dictionary<string, string> map)
            {
                return map == null ? null : new Dictionary<string, string>(map, StringComparer.Ordinal);
            }

            private static string TripleOf(MarkerRecord r)
            {
                // Stryker disable once String : TripleOf(null) is used only after a Record != null check, or looked up with a non-empty triple
                return r == null ? "" : ClashIdOf.Triple(r.HostUid, r.LinkUid, r.OtherUid);
            }

            private static MarkerRecord Clone(MarkerRecord r)
            {
                return new MarkerRecord
                {
                    Key = r.Key ?? "", HostUid = r.HostUid ?? "", LinkUid = r.LinkUid ?? "", OtherUid = r.OtherUid ?? "",
                    HostId = r.HostId, LinkId = r.LinkId, OtherId = r.OtherId, ClashId = r.ClashId ?? "", IssueNo = r.IssueNo ?? "",
                    MergedIssueNos = new List<string>(r.MergedIssueNos ?? new List<string>()), Status = r.Status, Note = r.Note ?? "", AssignedTo = r.AssignedTo ?? "",
                    ReportedBy = r.ReportedBy ?? "", ReportedAt = r.ReportedAt, ChangedBy = r.ChangedBy ?? "", ChangedAt = r.ChangedAt,
                    Severity = r.Severity ?? "", Insulated = r.Insulated, ClearanceMm = r.ClearanceMm, SetName = r.SetName ?? "",
                    AlsoSets = new List<string>(r.AlsoSets ?? new List<string>()), GroupLabel = r.GroupLabel ?? "", ResolvedBy = r.ResolvedBy ?? "", ResolvedAt = r.ResolvedAt,
                    X = r.X, Y = r.Y, Z = r.Z, LastCheckedAt = r.LastCheckedAt, LastCheckedBy = r.LastCheckedBy ?? "", Seq = r.Seq
                };
            }

            /// <summary>Pending, Coordinated and Ignored are people's decisions; Resolved and Reopened are what a re-test found (D22).</summary>
            private static bool Human(MarkerStatus s)
            {
                return s == MarkerStatus.Pending || s == MarkerStatus.Coordinated || s == MarkerStatus.Ignored;
            }

            private static bool HasDecision(MarkerRecord r)
            {
                return Human(r.Status) || !string.IsNullOrEmpty(r.Note) || !string.IsNullOrEmpty(r.AssignedTo);
            }

            private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b)
            {
                return a.Distinct(Ord).OrderBy(x => x, Ord).SequenceEqual(b.Distinct(Ord).OrderBy(x => x, Ord), Ord);
            }

            // ------------------------------------------------------------------ run

            public Result Run()
            {
                _result.ExistingMarkers = _markers.Count;
                IndexRows();
                IndexPending();
                var counter = CloneCounter();

                foreach (var m in _markers)
                    if (m.Record != null)
                    {
                        // Stryker disable once String : _used is asked only with formatted numbers (prefix-digits), never "" or a word
                        _used.Add(m.Record.IssueNo ?? "");
                        // Stryker disable once String : _used is asked only with formatted numbers (prefix-digits), never "" or a word
                        foreach (var n in m.Record.MergedIssueNos ?? new List<string>()) _used.Add(n ?? "");
                    }
                // Stryker disable once Statement,String : _used is asked only with formatted numbers (prefix-digits), never "" or a word
                _used.Remove("");
                RepairIssueNumbers(counter);

                var candidates = Candidates();
                var creations = MakeCreations(candidates, counter);
                AssignGroupLabels(creations);
                var collisions = ClashIds(creations);

                var groups = new Dictionary<string, List<Existing>>(Ord);
                foreach (var m in _markers)
                {
                    string t = TripleOf(m.Record);
                    if (m.Record == null || t.Length == 0) continue;
                    List<Existing> list;
                    if (!groups.TryGetValue(t, out list)) groups[t] = list = new List<Existing>();
                    list.Add(m);
                }

                var absorbed = new Dictionary<string, MarkerRecord>(Ord);
                var removedDup = new HashSet<string>(Ord);
                // Stryker disable once Linq : groups are disjoint and Finish sorts every result list again
                var orderedGroups = groups.OrderBy(g => g.Key, Ord);
                foreach (var kv in orderedGroups)
                    if (kv.Value.Count > 1) Merge(kv.Value, absorbed, removedDup);

                foreach (var m in _markers)
                {
                    // Stryker disable once Statement : a removed duplicate also carries Bucket.Removed, which the next line skips
                    if (removedDup.Contains(m.MarkerUid)) continue;
                    if (_buckets.ContainsKey(m.MarkerUid)) continue;
                    PlanMarker(m, absorbed, collisions);
                }

                // Stryker disable once Linq : pending triples are unique and Finish sorts them
                foreach (var p in _pending.Values.OrderBy(p => p.Triple, Ord))
                    if (!_handledPending.Contains(p.Triple))
                        _result.Pending.Add(new PendingOutcome { Triple = p.Triple, Kind = PendingKind.Dropped, Message = "Your pending triage was dropped: the marker is no longer there." });

                Finish(counter);
                return _result;
            }

            private IssueNumbers.Counter CloneCounter()
            {
                var c = _input.Counter;
                var copy = new IssueNumbers.Counter
                {
                    Uid = c == null ? "" : c.Uid, UserName = c == null ? _user : c.UserName,
                    Prefix = c == null || string.IsNullOrEmpty(c.Prefix) ? IssueNumbers.PrefixOf(_user) : c.Prefix,
                    Next = c == null ? 1 : c.Next
                };
                return copy;
            }

            private void IndexRows()
            {
                foreach (var row in _sortedRows)
                {
                    string t = ClashIdOf.Triple(row.HostUid, row.LinkUid, row.OtherUid);
                    if (t.Length == 0 || _rows.ContainsKey(t)) continue;
                    _rows[t] = row;
                }
            }

            private void IndexPending()
            {
                var pendings = (_input.Pending ?? new List<Pending>()).Where(p => p != null && !string.IsNullOrEmpty(p.Triple));
                // Stryker disable once Linq : turning the primary key round keeps the order inside a triple (stable sorts)
                var byTriple = pendings.OrderBy(p => p.Triple, Ord);
                foreach (var p in byTriple.ThenByDescending(p => p.SeqAtPending).ThenBy(p => p.By ?? "", Ord).ThenBy(p => p.Note ?? "", Ord))
                    if (!_pending.ContainsKey(p.Triple)) _pending[p.Triple] = p;
            }

            // ------------------------------------------------------------------ creations

            private sealed class Candidate
            {
                public Row Row;
                public string Triple;
                public int Rank;
            }

            private static int Rank(string severity)
            {
                switch (severity)
                {
                    case "Hard": return 0;
                    case "Clearance": return 1;
                    case "Unverified": return 2;
                    default: return 3;
                }
            }

            private List<Candidate> Candidates()
            {
                var list = new List<Candidate>();
                var existing = new HashSet<string>(_markers.Select(m => TripleOf(m.Record)).Where(t => t.Length > 0), Ord);
                var seen = new HashSet<string>(Ord);
                foreach (var row in _sortedRows)
                {
                    bool live = row.Live && row.Verdict == PlanVerdict.Clashing;
                    bool decided = !live && row.Seed != null && row.Verdict != PlanVerdict.NoLongerClashes;
                    if (!live && !decided) continue;
                    string t = ClashIdOf.Triple(row.HostUid, row.LinkUid, row.OtherUid);
                    if (t.Length == 0)
                    {
                        _result.NotMarked.Add(new Reasoned { Key = row.Key, Reason = "no UniqueId" });
                        continue;
                    }
                    if (existing.Contains(t) || !seen.Add(t)) continue;
                    if (!row.HasPoint)
                    {
                        _result.NotMarked.Add(new Reasoned { Key = row.Key, Reason = "no position" });
                        continue;
                    }
                    list.Add(new Candidate { Row = row, Triple = t, Rank = Rank(row.Severity) });
                }
                return list.OrderBy(c => c.Rank).ThenBy(c => c.Row.Key ?? "", Ord).ThenBy(c => c.Triple, Ord).ToList();
            }

            private List<Creation> MakeCreations(List<Candidate> candidates, IssueNumbers.Counter counter)
            {
                var hash = _opt.Hash ?? ShortHash.Of;
                int cap = Math.Max(0, _opt.Cap);
                _result.OverCap = candidates.Count > cap;
                var made = new List<Creation>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (i >= cap)
                    {
                        _result.NotMarked.Add(new Reasoned { Key = c.Row.Key, Reason = "over the cap of " + cap.ToString("N0", CultureInfo.InvariantCulture) });
                        continue;
                    }
                    var creation = MakeCreation(c, counter, hash);
                    if (creation == null)
                    {
                        _result.NotMarked.Add(new Reasoned { Key = c.Row.Key, Reason = "no free issue number" });
                        continue;
                    }
                    made.Add(creation);
                }
                return made;
            }

            private Creation MakeCreation(Candidate c, IssueNumbers.Counter counter, Func<string, string> hash)
            {
                var row = c.Row;
                bool replaced = false;
                string number = null;
                string remembered = row.RememberedIssueNo, p = null;
                int n = 0;
                if (!string.IsNullOrEmpty(remembered) && IssueNumbers.TryParse(remembered, out p, out n))
                {
                    string canonical = IssueNumbers.Format(p, n);
                    if (!_used.Contains(canonical)) number = canonical;
                    else replaced = true;
                }
                if (number == null)
                {
                    bool last;   // Take does not advance past int.MaxValue, so the number taken while Next was int.MaxValue is the last one to try (DR3-L3)
                    do { last = counter.Next == int.MaxValue; number = IssueNumbers.Take(counter); } while (_used.Contains(number) && !last);
                }
                // The counter stopped at its last number and that number is held: there is no free one, so no marker is made (never one with a held number).
                if (_used.Contains(number)) return null;
                _used.Add(number);

                var seed = row.Seed;
                var rec = new MarkerRecord
                {
                    Key = row.Key ?? "", HostUid = row.HostUid ?? "", LinkUid = row.LinkUid ?? "", OtherUid = row.OtherUid ?? "",
                    HostId = row.HostId, LinkId = row.LinkId, OtherId = row.OtherId,
                    ClashId = ClashIdOf.Of(row.HostUid, row.LinkUid, row.OtherUid, hash), IssueNo = number,
                    Status = seed != null ? seed.Status : MarkerStatus.New, Note = seed != null ? seed.Note ?? "" : "", AssignedTo = seed != null ? seed.AssignedTo ?? "" : "",
                    ReportedBy = _user, ReportedAt = _today, ChangedBy = seed != null ? seed.By ?? "" : "", ChangedAt = seed != null ? seed.At ?? _today : (DateTimeOffset?)null,
                    Severity = row.Severity ?? "", Insulated = row.RuleInsulated, ClearanceMm = row.RuleClearanceMm,
                    SetName = string.IsNullOrEmpty(row.SetName) ? NoSetName : row.SetName,
                    AlsoSets = (row.AlsoSets ?? new List<string>()).Distinct(Ord).OrderBy(x => x, Ord).ToList(), GroupLabel = row.GroupLabel ?? "",
                    X = row.X, Y = row.Y, Z = row.Z, LastCheckedAt = _today, LastCheckedBy = _user, Seq = 1
                };
                if (rec.Status == MarkerStatus.Resolved) { rec.ResolvedBy = seed.By ?? ""; rec.ResolvedAt = seed.At ?? _today; }
                return new Creation { Triple = c.Triple, Record = rec, ShownHash = row.ShownHash, Shown = CopyOf(row.Shown), StatusOrder = MarkerStatusText.Order(rec.Status), IssueNoReplaced = replaced };
            }

            /// <summary>
            /// Two users who made their first markers before either synchronized took the same prefix. When the refreshing
            /// user's own counter is the one that loses (the higher UniqueId), her own markers on the old prefix take the new
            /// prefix with the same number, the old number goes into MergedIssueNos, and her counter moves, so what she
            /// creates next is already clear of the other. Another user's markers are never touched here. Then the residual
            /// rule (D43): an own marker on a prefix other than the counter's whose number another marker also holds is
            /// renamed to the counter's prefix, which repairs what a held marker left behind.
            /// </summary>
            private void RepairIssueNumbers(IssueNumbers.Counter counter)
            {
                var readable = _markers.Where(m => m.Record != null).ToList();
                var all = (_input.AllCounters ?? new List<IssueNumbers.Counter>()).Where(c => c != null && !string.IsNullOrEmpty(c.Prefix)).ToList();
                string ownUid = _input.Counter == null ? null : _input.Counter.Uid;
                if (!string.IsNullOrEmpty(ownUid) && all.Count > 0 && !all.Any(c => c.Uid == ownUid) && !string.IsNullOrEmpty(_input.Counter.Prefix))
                    all.Add(new IssueNumbers.Counter { Uid = ownUid, UserName = _input.Counter.UserName, Prefix = _input.Counter.Prefix, Next = _input.Counter.Next });

                if (all.Count > 1 && !string.IsNullOrEmpty(ownUid))
                {
                    var move = IssueNumbers.PrefixMoves(all).FirstOrDefault(m => m.Uid == ownUid);
                    if (move != null)
                    {
                        _ownMove = move;
                        counter.Prefix = move.NewPrefix;
                        var issueMarkers = readable.Select(m => new IssueNumbers.IssueMarker { MarkerUid = m.MarkerUid, IssueNo = m.Record.IssueNo, ReportedBy = m.Record.ReportedBy }).ToList();
                        foreach (var r in IssueNumbers.RenumberPlan(all, issueMarkers))
                        {
                            // Stryker disable once Linq : every r.MarkerUid comes from readable
                            var marker = readable.First(m => m.MarkerUid == r.MarkerUid);
                            string p = null; int n = 0;
                            // Stryker disable once String : RenumberPlan skips markers with a null reporter
                            if (string.Equals(marker.Record.ReportedBy ?? "", _user, StringComparison.OrdinalIgnoreCase) && IssueNumbers.TryParse(r.NewIssueNo, out p, out n)
                                && string.Equals(p, move.NewPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                _renumber[r.MarkerUid] = Tuple.Create(marker.Record.IssueNo, r.NewIssueNo);
                                _used.Add(r.NewIssueNo);
                            }
                        }
                    }
                }

                // The residual rule.
                // Stryker disable once String : holders is asked only with a parsable IssueNo
                var holders = readable.GroupBy(m => m.Record.IssueNo ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(m => TripleOf(m.Record)).Distinct(Ord).Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var m in readable)
                {
                    if (_renumber.ContainsKey(m.MarkerUid) || !string.Equals(m.Record.ReportedBy ?? "", _user, StringComparison.OrdinalIgnoreCase)) continue;
                    string p = null; int n = 0;
                    if (!IssueNumbers.TryParse(m.Record.IssueNo, out p, out n) || string.Equals(p, counter.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    int held = 0;
                    if (!holders.TryGetValue(m.Record.IssueNo, out held) || held < 2) continue;
                    string candidate = IssueNumbers.Format(counter.Prefix, n);
                    if (_used.Contains(candidate)) continue;
                    _renumber[m.MarkerUid] = Tuple.Create(m.Record.IssueNo, candidate);
                    _used.Add(candidate);
                }
                _result.PrefixMove = _ownMove;
            }

            /// <summary>
            /// The group labels (spec 6.10): the lowest issue number in a proximity group and how many others it holds. They
            /// can only be worked out here, because a new marker gets its number inside the plan. Members are the group's
            /// Live, Clashing rows that have a marker or get one; a row with no group key keeps the label it carries.
            /// </summary>
            private void AssignGroupLabels(List<Creation> creations)
            {
                var numberOf = new Dictionary<string, string>(Ord);
                foreach (var m in _markers)
                {
                    string t = TripleOf(m.Record);
                    Tuple<string, string> renamed;
                    if (m.Record != null && t.Length > 0 && !numberOf.ContainsKey(t)) numberOf[t] = _renumber.TryGetValue(m.MarkerUid, out renamed) ? renamed.Item2 : m.Record.IssueNo ?? "";
                }
                foreach (var c in creations) numberOf[c.Triple] = c.Record.IssueNo;

                var groups = new Dictionary<string, List<string>>(Ord);
                // Stryker disable once Linq : GroupLabel.Of does not depend on the order of its members
                foreach (var kv in _rows.OrderBy(k => k.Key, Ord))
                {
                    var row = kv.Value;
                    string no = null;
                    if (!row.Live || row.Verdict != PlanVerdict.Clashing || string.IsNullOrEmpty(row.GroupKey) || !numberOf.TryGetValue(kv.Key, out no)) continue;
                    List<string> members = null;
                    if (!groups.TryGetValue(row.GroupKey, out members)) groups[row.GroupKey] = members = new List<string>();
                    members.Add(no);
                }
                foreach (var kv in _rows)
                {
                    var row = kv.Value;
                    if (!row.Live || row.Verdict != PlanVerdict.Clashing || row.GroupKey == null) continue;
                    List<string> members = null;
                    _labels[kv.Key] = row.GroupKey.Length > 0 && groups.TryGetValue(row.GroupKey, out members) ? GroupLabel.Of(members) : "";
                }
                foreach (var c in creations)
                {
                    string label;
                    c.Record.GroupLabel = _labels.TryGetValue(c.Triple, out label) ? label : c.Record.GroupLabel;
                }
            }

            /// <summary>The label this row wants on its marker, or null when it does not know (no group key and no label, or not a live clashing row in a keyed group).</summary>
            private string LabelFor(string triple, Row row)
            {
                if (row.GroupKey == null) return row.GroupLabel;
                string label;
                return _labels.TryGetValue(triple, out label) ? label : null;
            }

            /// <summary>Gives every id its collision suffix (the new markers sort after the existing ones, so they take the suffix) and returns the rewrites existing markers need.</summary>
            private Dictionary<string, string> ClashIds(List<Creation> creations)
            {
                var entries = new List<ClashIdOf.Entry>();
                foreach (var m in _markers)
                {
                    string t = TripleOf(m.Record);
                    if (m.Record != null && t.Length > 0) entries.Add(new ClashIdOf.Entry { MarkerUid = m.MarkerUid, ClashId = m.Record.ClashId, Triple = t });
                }
                foreach (var c in creations) entries.Add(new ClashIdOf.Entry { MarkerUid = "~new~" + c.Triple, ClashId = c.Record.ClashId, Triple = c.Triple });
                var fixes = ClashIdOf.ResolveCollisions(entries);
                foreach (var c in creations)
                {
                    string wanted;
                    if (fixes.TryGetValue("~new~" + c.Triple, out wanted)) c.Record.ClashId = wanted;
                }
                _result.Creations.AddRange(creations);
                return fixes;
            }

            // ------------------------------------------------------------------ duplicates

            private void Merge(List<Existing> group, Dictionary<string, MarkerRecord> absorbed, HashSet<string> removed)
            {
                var byUid = group.OrderBy(m => m.MarkerUid, Ord);
                // Stryker disable once Linq : a duplicate group has more than one member
                var kept = byUid.First();
                var keptRec = Clone(kept.Record);
                // Stryker disable once Boolean : with nothing absorbed, absorbed[kept] equals the clone PlanMarker makes
                bool anyAbsorbed = false;
                foreach (var dup in group.OrderBy(m => m.MarkerUid, Ord).Skip(1))
                {
                    if (kept.Access != MarkerAccess.Free || dup.Access != MarkerAccess.Free)
                    {
                        string why = dup.Access != MarkerAccess.Free ? AccessReason(dup) : "kept marker " + AccessReason(kept);
                        Bucketed(dup.MarkerUid, Bucket.PassedOver);
                        _result.PassedOver.Add(new Reasoned { MarkerUid = dup.MarkerUid, Key = dup.Record.Key, Reason = "duplicate: " + why });
                        continue;
                    }
                    Absorb(kept, keptRec, dup);
                    anyAbsorbed = true;
                    // Stryker disable once Statement : the removedDup skip and the Bucket.Removed skip do the same job
                    removed.Add(dup.MarkerUid);
                    // Stryker disable once Statement : the removedDup skip and the Bucket.Removed skip do the same job
                    Bucketed(dup.MarkerUid, Bucket.Removed);
                    _result.Merges++;
                    _result.Removals.Add(new Removal { MarkerUid = dup.MarkerUid, Reason = "merged duplicate", KeptUid = kept.MarkerUid });
                }
                if (anyAbsorbed) absorbed[kept.MarkerUid] = keptRec;
            }

            private void Absorb(Existing kept, MarkerRecord keptRec, Existing dupEx)
            {
                var dup = dupEx.Record;
                foreach (var number in new[] { dup.IssueNo }.Concat(dup.MergedIssueNos ?? new List<string>()))
                    if (!string.IsNullOrEmpty(number) && !string.Equals(number, keptRec.IssueNo, StringComparison.OrdinalIgnoreCase)
                        && !keptRec.MergedIssueNos.Contains(number, StringComparer.OrdinalIgnoreCase))
                        keptRec.MergedIssueNos.Add(number);

                if (!HasDecision(dup)) return;
                if (!HasDecision(keptRec))
                {
                    if (Human(dup.Status))
                    {
                        if (keptRec.Status == MarkerStatus.Resolved) { keptRec.ResolvedBy = ""; keptRec.ResolvedAt = null; }
                        keptRec.Status = dup.Status;
                    }
                    keptRec.Note = dup.Note ?? ""; keptRec.AssignedTo = dup.AssignedTo ?? "";
                    keptRec.ChangedBy = dup.ChangedBy ?? ""; keptRec.ChangedAt = dup.ChangedAt;
                    // Stryker disable once Statement : the comparison after the copy finds the two equal and reports nothing
                    return;
                }
                // Stryker disable once String : the kept record has no null texts (Clone)
                string keptNote = keptRec.Note ?? "", keptAssignedTo = keptRec.AssignedTo ?? "";
                if ((Human(keptRec.Status) ? keptRec.Status : MarkerStatus.New) != (Human(dup.Status) ? dup.Status : MarkerStatus.New) || keptNote != (dup.Note ?? "") || keptAssignedTo != (dup.AssignedTo ?? ""))
                    _result.LostDecisions.Add(new LostDecision
                    {
                        RemovedUid = dupEx.MarkerUid, RemovedIssueNo = dup.IssueNo, KeptUid = kept.MarkerUid, KeptIssueNo = keptRec.IssueNo,
                        Status = dup.Status, Note = dup.Note ?? "", AssignedTo = dup.AssignedTo ?? "", By = string.IsNullOrEmpty(dup.ChangedBy) ? dup.ReportedBy ?? "" : dup.ChangedBy
                    });
            }

            // ------------------------------------------------------------------ one marker

            private void Bucketed(string uid, Bucket b)
            {
                _buckets[uid] = b;
            }

            private void PlanMarker(Existing m, Dictionary<string, MarkerRecord> absorbed, Dictionary<string, string> collisions)
            {
                string uid = m.MarkerUid;
                if (m.Record == null)
                {
                    Pass(m, AccessReason(m).Length > 0 ? AccessReason(m) : "unreadable record");
                    return;
                }

                string triple = TripleOf(m.Record);
                Row row = null;
                Pending pend = null;
                if (triple.Length > 0) { _rows.TryGetValue(triple, out row); _pending.TryGetValue(triple, out pend); }
                bool free = m.Access == MarkerAccess.Free;
                var before = m.Record;
                MarkerRecord keptAbsorbed;
                var after = absorbed.TryGetValue(uid, out keptAbsorbed) ? keptAbsorbed : Clone(before);
                var fields = new SortedSet<string>(Ord);

                if (free && Retention(m, after, row, pend))
                    return;

                MergeFields(fields, before, after);
                Tuple<string, string> renumbered;
                if (_renumber.TryGetValue(uid, out renumbered))
                {
                    after.IssueNo = renumbered.Item2;
                    if (!string.IsNullOrEmpty(renumbered.Item1) && !after.MergedIssueNos.Contains(renumbered.Item1, StringComparer.OrdinalIgnoreCase)) after.MergedIssueNos.Add(renumbered.Item1);
                    fields.Add("IssueNo");
                    fields.Add("MergedIssueNos");
                }

                string pendingMessage = null;
                if (pend != null)
                {
                    _handledPending.Add(pend.Triple);
                    if (!free)
                    {
                        _result.Pending.Add(new PendingOutcome { Triple = pend.Triple, MarkerUid = uid, Kind = PendingKind.Kept, Message = "Your pending triage waits: " + AccessReason(m) + "." });
                        // Stryker disable once String : pendingMessage is tested only for != null
                        pendingMessage = "kept";
                    }
                    else
                        PlanPending(pend, uid, before, after, fields);
                }

                string notRetested = null;
                bool resolved = false, reopened = false, moved = false, rekeyed = false;
                string shown = null;
                if (row != null && row.Live)
                {
                    bool sameRule = Math.Abs(row.RuleClearanceMm - before.ClearanceMm) < 1e-6 && row.RuleInsulated == before.Insulated;
                    if (row.Verdict == PlanVerdict.NotRetested) notRetested = string.IsNullOrEmpty(row.NotRetestedReason) ? "not re-tested" : row.NotRetestedReason;
                    else if (!sameRule) notRetested = "rule differs";
                    else if (row.Verdict == PlanVerdict.NoLongerClashes)
                    {
                        if (after.Status != MarkerStatus.Resolved)
                        {
                            after.Status = MarkerStatus.Resolved; after.ResolvedBy = _user; after.ResolvedAt = _today;
                            after.ChangedBy = _user; after.ChangedAt = _today;
                            fields.Add("Status");
                            resolved = true;
                        }
                    }
                    else
                    {
                        if (after.Status == MarkerStatus.Resolved)
                        {
                            after.Status = MarkerStatus.Reopened; after.ResolvedBy = ""; after.ResolvedAt = null;
                            after.ChangedBy = _user; after.ChangedAt = _today;
                            fields.Add("Status");
                            reopened = true;
                        }
                        Differences(row, LabelFor(triple, row), m, after, fields, ref moved, ref rekeyed, ref shown);
                    }
                }

                bool putBack = m.OffPlace, worksetFix = m.WrongWorkset;
                if (putBack) fields.Add("Location");
                if (worksetFix) fields.Add("Workset");
                string wanted;
                if (collisions.TryGetValue(uid, out wanted))
                {
                    after.ClashId = wanted;
                    fields.Add("ClashId");
                }

                if (fields.Count > 0)
                {
                    if (!free) { Pass(m, AccessReason(m)); return; }
                    after.LastCheckedAt = _today; after.LastCheckedBy = _user; after.Seq = before.Seq + 1;
                    _result.Changes.Add(new Change
                    {
                        MarkerUid = uid, Before = before, After = after, Fields = fields.ToList(), Resolved = resolved, Reopened = reopened,
                        Moved = moved, PutBack = putBack, WorksetFix = worksetFix, Rekeyed = rekeyed, ShownHash = shown,
                        Shown = row != null && row.Live && row.Verdict != PlanVerdict.NotRetested ? CopyOf(row.Shown) : null, StatusOrder = MarkerStatusText.Order(after.Status)
                    });
                    Bucketed(uid, Bucket.Counted);
                    return;
                }
                if (pendingMessage != null) { Pass(m, AccessReason(m)); return; }
                if (notRetested != null)
                {
                    Bucketed(uid, Bucket.NotRetested);
                    _result.NotRetested.Add(new Reasoned { MarkerUid = uid, Key = before.Key, Reason = notRetested });
                    return;
                }
                Bucketed(uid, Bucket.Counted);
                _result.Unchanged++;
            }

            private void Pass(Existing m, string reason)
            {
                Bucketed(m.MarkerUid, Bucket.PassedOver);
                _result.PassedOver.Add(new Reasoned { MarkerUid = m.MarkerUid, Key = m.Record == null ? null : m.Record.Key, Reason = reason });
            }

            /// <summary>The fields an absorbed duplicate changed on the kept marker.</summary>
            private static void MergeFields(SortedSet<string> fields, MarkerRecord before, MarkerRecord after)
            {
                if (!SameSet(before.MergedIssueNos ?? new List<string>(), after.MergedIssueNos)) fields.Add("MergedIssueNos");
                if (before.Status != after.Status) fields.Add("Status");
                if ((before.Note ?? "") != after.Note) fields.Add("Note");
                if ((before.AssignedTo ?? "") != after.AssignedTo) fields.Add("AssignedTo");
            }

            /// <summary>
            /// A free marker's pending triage (D-L4): each field is applied only while the marker is still at the Seq that field was set at (a plan
            /// Pending with no per-field Seq uses SeqAtPending for all of them). Nothing applicable: Dropped, the model wins. Some: Applied, with the
            /// fields left out named. An entry that set no field is compared on SeqAtPending.
            /// </summary>
            private void PlanPending(Pending pend, string uid, MarkerRecord before, MarkerRecord after, SortedSet<string> fields)
            {
                long current = before.Seq;
                bool hasStatus = pend.Status.HasValue, hasNote = pend.Note != null, hasAssignee = pend.AssignedTo != null;
                bool statusOk = hasStatus && (pend.StatusSeq ?? pend.SeqAtPending) == current;
                bool noteOk = hasNote && (pend.NoteSeq ?? pend.SeqAtPending) == current;
                bool assigneeOk = hasAssignee && (pend.AssignedSeq ?? pend.SeqAtPending) == current;
                bool anySet = hasStatus || hasNote || hasAssignee;
                bool apply = anySet ? statusOk || noteOk || assigneeOk : pend.SeqAtPending == current;
                if (!apply)
                {
                    _result.Pending.Add(new PendingOutcome
                    {
                        Triple = pend.Triple, MarkerUid = uid, Kind = PendingKind.Dropped,
                        Message = "Your pending triage was not applied: the marker was changed by someone else first (the model wins); yours is in the Record."
                    });
                    return;
                }
                ApplyPending(after, pend, statusOk, noteOk, assigneeOk, fields);
                var left = new List<string>();
                if (hasStatus && !statusOk) left.Add("status");
                if (hasNote && !noteOk) left.Add("note");
                if (hasAssignee && !assigneeOk) left.Add("assignee");
                var outcome = new PendingOutcome
                {
                    Triple = pend.Triple, MarkerUid = uid, Kind = PendingKind.Applied,
                    Message = left.Count == 0 ? "Your pending triage was written to the marker."
                        : "Part of your pending triage was not applied: the " + string.Join(", ", left) + " changed under you (the model wins); the rest was written to the marker."
                };
                outcome.NotApplied.AddRange(left);
                _result.Pending.Add(outcome);
            }

            private void ApplyPending(MarkerRecord after, Pending pend, bool statusOk, bool noteOk, bool assigneeOk, SortedSet<string> fields)
            {
                bool changed = false;
                if (statusOk && pend.Status.Value != after.Status) { after.Status = pend.Status.Value; fields.Add("Status"); changed = true; }
                if (noteOk && pend.Note != after.Note) { after.Note = pend.Note; fields.Add("Note"); changed = true; }
                if (assigneeOk && pend.AssignedTo != after.AssignedTo) { after.AssignedTo = pend.AssignedTo; fields.Add("AssignedTo"); changed = true; }
                if (changed) { after.ChangedBy = string.IsNullOrEmpty(pend.By) ? _user : pend.By; after.ChangedAt = _today; }
            }

            private static void Differences(Row row, string label, Existing m, MarkerRecord after, SortedSet<string> fields, ref bool moved, ref bool rekeyed, ref string shown)
            {
                if (!string.IsNullOrEmpty(row.Severity) && row.Severity != after.Severity) { after.Severity = row.Severity; fields.Add("Severity"); }
                if (row.HasPoint && (Math.Abs(row.X - after.X) > PointTolerance || Math.Abs(row.Y - after.Y) > PointTolerance || Math.Abs(row.Z - after.Z) > PointTolerance))
                {
                    after.X = row.X; after.Y = row.Y; after.Z = row.Z;
                    fields.Add("Point");
                    moved = true;
                }
                if (!string.IsNullOrEmpty(row.SetName) && row.SetName != after.SetName) { after.SetName = row.SetName; fields.Add("SetName"); }
                if (row.AlsoSets != null && !SameSet(row.AlsoSets, after.AlsoSets))
                {
                    after.AlsoSets = row.AlsoSets.Distinct(Ord).OrderBy(x => x, Ord).ToList();
                    fields.Add("AlsoSets");
                }
                if (label != null && label != after.GroupLabel) { after.GroupLabel = label; fields.Add("GroupLabel"); }
                if (row.HostId != 0 && row.HostId != after.HostId) { after.HostId = row.HostId; fields.Add("HostId"); rekeyed = true; }
                if (row.LinkId != 0 && row.LinkId != after.LinkId) { after.LinkId = row.LinkId; fields.Add("LinkId"); rekeyed = true; }
                if (row.OtherId != 0 && row.OtherId != after.OtherId) { after.OtherId = row.OtherId; fields.Add("OtherId"); rekeyed = true; }
                if (!string.IsNullOrEmpty(row.Key) && row.Key != after.Key) { after.Key = row.Key; fields.Add("Key"); rekeyed = true; }
                if (row.ShownHash != null && row.ShownHash != m.ShownHash) { fields.Add("Parameters"); }
                if (row.ShownHash != null) shown = row.ShownHash;
            }

            // ------------------------------------------------------------------ retention

            /// <summary>
            /// Resolved for more than the retention days, readable, free, no open pending triage and no row
            /// saying the pair clashes again. Offered always; removed only when ticked. Returns true when removed.
            /// </summary>
            private bool Retention(Existing m, MarkerRecord rec, Row row, Pending pend)
            {
                if (rec.Status != MarkerStatus.Resolved || !rec.ResolvedAt.HasValue || pend != null) return false;
                if (row != null && row.Live && row.Verdict == PlanVerdict.Clashing) return false;
                if (_today - rec.ResolvedAt.Value <= TimeSpan.FromDays(_opt.RetentionDays)) return false;
                _result.RetentionOffers.Add(new Removal { MarkerUid = m.MarkerUid, Reason = "retention" });
                if (!_opt.RemoveRetention) return false;
                // Stryker disable once Statement : the removedDup skip and the Bucket.Removed skip do the same job
                Bucketed(m.MarkerUid, Bucket.Removed);
                _result.Removals.Add(new Removal { MarkerUid = m.MarkerUid, Reason = "retention" });
                return true;
            }

            // ------------------------------------------------------------------ finish

            private void Finish(IssueNumbers.Counter counter)
            {
                _result.CounterAfter = counter;
                _result.Changes = _result.Changes.OrderBy(c => c.MarkerUid, Ord).ToList();
                _result.Removals = _result.Removals.OrderBy(r => r.MarkerUid, Ord).ToList();
                _result.RetentionOffers = _result.RetentionOffers.OrderBy(r => r.MarkerUid, Ord).ToList();
                _result.PassedOver = _result.PassedOver.OrderBy(r => r.MarkerUid, Ord).ToList();
                _result.NotRetested = _result.NotRetested.OrderBy(r => r.MarkerUid, Ord).ToList();
                _result.NotMarked = _result.NotMarked.OrderBy(r => r.Key ?? "", Ord).ThenBy(r => r.Reason, Ord).ToList();
                _result.Pending = _result.Pending.OrderBy(p => p.Triple, Ord).ToList();
                _result.LostDecisions = _result.LostDecisions.OrderBy(l => l.RemovedUid, Ord).ToList();

                int total = _result.ExistingMarkers + _result.Creations.Count;
                _result.OverWarn = total > _opt.WarnAt;
                _result.NeedsDialog = _result.Creations.Count > _opt.DialogCreateThreshold
                                      || (_result.ExistingMarkers == 0 && _result.Creations.Count > 0)
                                      || _result.PassedOver.Count > 0 || _result.RetentionOffers.Count > 0 || _result.OverWarn
                                      || _result.OverCap || _opt.SetupNeeded;
                _result.SetCounts = SetCountsOf();
            }

            private List<SetCount> SetCountsOf()
            {
                var names = new SortedSet<string>(Ord);
                var inPanel = new Dictionary<string, int>(Ord);
                foreach (var row in _rows.Values)
                {
                    if (!row.Live || row.Verdict != PlanVerdict.Clashing) continue;
                    string s = string.IsNullOrEmpty(row.SetName) ? NoSetName : row.SetName;
                    names.Add(s);
                    int n = 0; inPanel.TryGetValue(s, out n); inPanel[s] = n + 1;
                }

                var upToDate = new Dictionary<string, int>(Ord);
                var notLive = new Dictionary<string, int>(Ord);
                var held = new Dictionary<string, int>(Ord);
                var changes = _result.Changes.ToDictionary(c => c.MarkerUid, c => c, Ord);
                foreach (var m in _markers)
                {
                    Bucket b = Bucket.Counted;
                    if (m.Record == null || !_buckets.TryGetValue(m.MarkerUid, out b) || b == Bucket.Removed) continue;
                    Change ch = null;
                    string set = changes.TryGetValue(m.MarkerUid, out ch) ? ch.After.SetName : m.Record.SetName;
                    if (string.IsNullOrEmpty(set)) continue;
                    names.Add(set);
                    int h = 0; held.TryGetValue(set, out h); held[set] = h + 1;
                    if (b == Bucket.PassedOver || b == Bucket.NotRetested) continue;
                    int u = 0; upToDate.TryGetValue(set, out u); upToDate[set] = u + 1;
                    Row r = null;
                    if (!_rows.TryGetValue(TripleOf(m.Record), out r) || !r.Live || r.Verdict != PlanVerdict.Clashing)
                    { int l = 0; notLive.TryGetValue(set, out l); notLive[set] = l + 1; }
                }
                foreach (var c in _result.Creations)
                {
                    names.Add(c.Record.SetName);
                    int u; upToDate.TryGetValue(c.Record.SetName, out u); upToDate[c.Record.SetName] = u + 1;
                }

                var disabled = new HashSet<string>((_opt.DisabledSets ?? new List<string>()).Where(s => s != null), StringComparer.OrdinalIgnoreCase);
                var list = new List<SetCount>();
                foreach (var name in names)
                {
                    int ip = 0, ud = 0, nl = 0, hd = 0;
                    inPanel.TryGetValue(name, out ip); upToDate.TryGetValue(name, out ud); notLive.TryGetValue(name, out nl); held.TryGetValue(name, out hd);
                    bool off = disabled.Contains(name);
                    list.Add(new SetCount { Name = name, InPanel = ip, UpToDate = off ? 0 : ud, NotLive = off ? 0 : nl, SetOff = off, NotRetested = off ? hd : 0 });
                }
                return list;
            }

            // ------------------------------------------------------------------ counts

            internal static MarkerSummary.Counts CountsOf(Result r)
            {
                Func<List<Reasoned>, List<MarkerSummary.Reason>> group = list => list
                    .GroupBy(x => x.Reason ?? "", Ord).OrderBy(g => g.Key, Ord)
                    .Select(g => new MarkerSummary.Reason { Text = g.Key, Count = g.Count() }).ToList();
                return new MarkerSummary.Counts
                {
                    Clashes = r.ExistingMarkers + r.Creations.Count + r.NotMarked.Count,
                    Unchanged = r.Unchanged, Created = r.Creations.Count,
                    Updated = r.Changes.Count(c => !c.Resolved), Resolved = r.Changes.Count(c => c.Resolved),
                    Removed = r.Removals.Count, Merged = r.Merges, Moved = r.Changes.Count(c => c.Moved || c.PutBack),
                    PendingApplied = r.Pending.Count(p => p.Kind == PendingKind.Applied), LostDecisions = r.LostDecisions.Count,
                    PassedOver = group(r.PassedOver), NotRetested = group(r.NotRetested), NotMarked = group(r.NotMarked)
                };
            }
        }
    }
}
