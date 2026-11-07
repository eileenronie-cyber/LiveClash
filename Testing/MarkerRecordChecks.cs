using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// Clash schedule, slice 1: the marker record, its frozen field list and the one reader over every
    /// known format (spec 5.1, 5.2, rule M4, RF10, RF19). Revit-free. Red-first: BUILD-LOG.md.
    /// </summary>
    public static class MarkerRecordChecks
    {
        private const string P = "markers record: ";

        // The frozen field list of format 1. A format change gets a NEW schema GUID; this list never grows.
        private static readonly string[] Frozen1 =
        {
            "Key", "HostUid", "LinkUid", "OtherUid", "HostId", "LinkId", "OtherId", "ClashId", "IssueNo", "MergedIssueNos",
            "Status", "Note", "AssignedTo", "ReportedBy", "ReportedAt", "ChangedBy", "ChangedAt", "Severity", "Insulated",
            "ClearanceMm", "SetName", "AlsoSets", "GroupLabel", "ResolvedBy", "ResolvedAt", "X", "Y", "Z",
            "LastCheckedAt", "LastCheckedBy", "Seq"
        };

        private static readonly MarkerStatus[] AllStatuses =
        {
            MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Coordinated, MarkerStatus.Ignored, MarkerStatus.Reopened, MarkerStatus.Resolved
        };

        public static void RunAll(Action<string, bool?> check)
        {
            Constants(check);
            RoundTrips(check);
            Formats(check);
            Junk(check);
            Statuses(check);
        }

        // ------------------------------------------------------------------ constants

        private static void Constants(Action<string, bool?> check)
        {
            check(P + "the frozen field list of format 1 is exactly the 31 fields of spec 5.2, in order",
                MarkerFormats.Fields1.SequenceEqual(Frozen1));
            check(P + "the identity constants are the spec's (vendor LCLS, ApplicationId LiveClash, marker/1)",
                MarkerFormats.Vendor == "LCLS" && MarkerFormats.ApplicationId == "LiveClash" && MarkerFormats.ApplicationDataId == "marker/1");
            check(P + "format 1's schema GUID is the frozen one and the writer uses the newest format",
                MarkerFormats.Format1 == new Guid("7b0c4a52-91d3-4e68-a5f7-3c2e18d94b06") && MarkerFormats.Current == MarkerFormats.Format1);

            string manifest = FindManifest();
            if (manifest == null) { check(P + "the manifest's VendorId equals MarkerFormats.Vendor (manifest not found from here)", null); }
            else
            {
                var m = Regex.Match(File.ReadAllText(manifest), "<VendorId>([^<]*)</VendorId>");
                check(P + "the manifest's VendorId equals MarkerFormats.Vendor (changing it orphans every record)",
                    m.Success && m.Groups[1].Value == MarkerFormats.Vendor && MarkerFormats.Vendor.Length > 0);
            }
        }

        private static string FindManifest()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                try
                {
                    var d = new DirectoryInfo(start);
                    while (d != null)
                    {
                        var f = Path.Combine(d.FullName, "LiveClash.addin");
                        if (File.Exists(f)) return f;
                        d = d.Parent;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        // ------------------------------------------------------------------ round trips

        private static MarkerRecord Full()
        {
            return new MarkerRecord
            {
                Key = "123456789012|0|9876543210987",
                HostUid = "a1b2c3-0001", LinkUid = "link-uid-7", OtherUid = "d4e5f6-0002",
                HostId = 4294967297L, LinkId = 55, OtherId = long.MaxValue,
                ClashId = "LC-1a2b3c4d", IssueNo = "JD-0042", MergedIssueNos = new List<string> { "KD-0003", "JD2-0001" },
                Status = MarkerStatus.Ignored, Note = "raise duct 150\tmm; see \"RFI\"\nline 2 é中", AssignedTo = "asmith",
                ReportedBy = "jdoe", ReportedAt = new DateTimeOffset(2026, 10, 31, 14, 5, 9, TimeSpan.FromHours(8)),
                ChangedBy = "kdoe", ChangedAt = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(-5)),
                Severity = "Clearance", Insulated = true, ClearanceMm = 100.5,
                SetName = "MEP vs STR", AlsoSets = new List<string> { "MEP vs MEP", "a,b;c" }, GroupLabel = "JD-0042 (+13)",
                ResolvedBy = "jdoe", ResolvedAt = new DateTimeOffset(2026, 11, 3, 0, 0, 1, TimeSpan.Zero),
                X = 123.456789012345, Y = -0.000001234, Z = 1e-9 + 3.0,
                LastCheckedAt = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.FromHours(8)), LastCheckedBy = "jdoe", Seq = 4294967299L
            };
        }

        private static bool RecEq(MarkerRecord a, MarkerRecord b)
        {
            if (a == null || b == null) return false;
            Func<DateTimeOffset?, DateTimeOffset?, bool> dt = (x, y) => x.HasValue == y.HasValue && (!x.HasValue || (x.Value == y.Value && x.Value.Offset == y.Value.Offset));
            return a.Key == b.Key && a.HostUid == b.HostUid && a.LinkUid == b.LinkUid && a.OtherUid == b.OtherUid
                && a.HostId == b.HostId && a.LinkId == b.LinkId && a.OtherId == b.OtherId && a.ClashId == b.ClashId
                && a.IssueNo == b.IssueNo && a.MergedIssueNos.SequenceEqual(b.MergedIssueNos) && a.Status == b.Status
                && a.Note == b.Note && a.AssignedTo == b.AssignedTo && a.ReportedBy == b.ReportedBy && dt(a.ReportedAt, b.ReportedAt)
                && a.ChangedBy == b.ChangedBy && dt(a.ChangedAt, b.ChangedAt) && a.Severity == b.Severity && a.Insulated == b.Insulated
                && a.ClearanceMm.Equals(b.ClearanceMm) && a.SetName == b.SetName && a.AlsoSets.SequenceEqual(b.AlsoSets)
                && a.GroupLabel == b.GroupLabel && a.ResolvedBy == b.ResolvedBy && dt(a.ResolvedAt, b.ResolvedAt)
                && a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z) && dt(a.LastCheckedAt, b.LastCheckedAt)
                && a.LastCheckedBy == b.LastCheckedBy && a.Seq == b.Seq;
        }

        private static void RoundTrips(Action<string, bool?> check)
        {
            var rec = Full();
            var fields = MarkerFormats.Write(rec);
            var back = MarkerReader.Default.Read(MarkerFormats.Current, fields);
            check(P + "write then read gives the same record, every field (RF19)", back.Status == MarkerReadStatus.Ok && RecEq(rec, back.Record));
            check(P + "the written fields are exactly the frozen list (no extra, none missing)",
                fields.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(Frozen1.OrderBy(k => k, StringComparer.Ordinal)));
            check(P + "ids beyond 32 bits survive (RF10): 4294967297 and long.MaxValue",
                back.Record != null && back.Record.HostId == 4294967297L && back.Record.OtherId == long.MaxValue && back.Record.Seq == 4294967299L);
            check(P + "the writer's UTC offset is kept with the time (+08:00 and -05:00)",
                back.Record != null && back.Record.ReportedAt.HasValue && back.Record.ReportedAt.Value.Offset == TimeSpan.FromHours(8)
                && back.Record.ChangedAt.HasValue && back.Record.ChangedAt.Value.Offset == TimeSpan.FromHours(-5));

            bool allStatus = true;
            foreach (var s in AllStatuses)
            {
                var r = Full(); r.Status = s;
                var rb = MarkerReader.Default.Read(MarkerFormats.Current, MarkerFormats.Write(r));
                allStatus &= rb.Status == MarkerReadStatus.Ok && rb.Record.Status == s;
            }
            check(P + "all six statuses round trip", allStatus);

            var empty = new MarkerRecord();
            var eb = MarkerReader.Default.Read(MarkerFormats.Current, MarkerFormats.Write(empty));
            check(P + "an empty record round trips, with no dates staying null", eb.Status == MarkerReadStatus.Ok && RecEq(empty, eb.Record)
                && !eb.Record.ReportedAt.HasValue && !eb.Record.ResolvedAt.HasValue && !eb.Record.LastCheckedAt.HasValue);

            var nulls = new MarkerRecord { Key = null, Note = null, MergedIssueNos = null, AlsoSets = null, SetName = null, AssignedTo = null };
            MarkerReadResult nb = null;
            bool threw = false;
            try { nb = MarkerReader.Default.Read(MarkerFormats.Current, MarkerFormats.Write(nulls)); } catch (Exception) { threw = true; }
            check(P + "null strings and null lists write without throwing and read back as empty", !threw && nb != null && nb.Status == MarkerReadStatus.Ok
                && nb.Record.Note == "" && nb.Record.Key == "" && nb.Record.AlsoSets.Count == 0 && nb.Record.MergedIssueNos.Count == 0);

            var old = Thread.CurrentThread.CurrentCulture;
            bool cultureOk;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var r = Full(); r.X = 1234.5678; r.ClearanceMm = 99.25;
                var f = MarkerFormats.Write(r);
                var rb = MarkerReader.Default.Read(MarkerFormats.Current, f);
                cultureOk = rb.Status == MarkerReadStatus.Ok && rb.Record.X == 1234.5678 && rb.Record.ClearanceMm == 99.25
                    && f["X"].IndexOf(',') < 0 && RecEq(r, rb.Record);
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
            check(P + "numbers are written and read in the invariant culture (a de-DE seat writes 1234.5678, not 1234,5678)", cultureOk);

            var rnd = new Random(20261031);
            int bad = 0;
            for (int i = 0; i < 300; i++)
            {
                var r = Full();
                r.HostId = NextLong(rnd); r.OtherId = NextLong(rnd); r.Seq = Math.Abs(NextLong(rnd) % 1000000);
                r.X = (rnd.NextDouble() - 0.5) * 1e6; r.Y = rnd.NextDouble() * 1e-3; r.Z = (rnd.NextDouble() - 0.5) * 1e3;
                r.ClearanceMm = Math.Round(rnd.NextDouble() * 300, 3);
                r.Status = (MarkerStatus)rnd.Next(6);
                r.Note = new string(Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => (char)rnd.Next(32, 0x2fff)).Where(c => !char.IsSurrogate(c)).ToArray());
                var rb = MarkerReader.Default.Read(MarkerFormats.Current, MarkerFormats.Write(r));
                if (rb.Status != MarkerReadStatus.Ok || !RecEq(r, rb.Record)) bad++;
            }
            check(P + "fuzz: 300 random records (64-bit ids, odd doubles, any text) round trip exactly (seed 20261031)", bad == 0);
        }

        private static long NextLong(Random r)
        {
            var b = new byte[8]; r.NextBytes(b); return BitConverter.ToInt64(b, 0);
        }

        // ------------------------------------------------------------------ formats

        /// <summary>A pretend older format, to prove the mechanism: it knew fewer fields and a different name for the note.</summary>
        private sealed class V0Format : IMarkerFormat
        {
            public static readonly Guid Id = new Guid("11111111-2222-3333-4444-555555555555");
            public Guid Schema { get { return Id; } }
            public int Version { get { return 0; } }
            public bool TryRead(IDictionary<string, string> f, out MarkerRecord record)
            {
                record = null;
                string key, st, note;
                if (!f.TryGetValue("Key", out key) || !f.TryGetValue("Status", out st)) return false;
                MarkerStatus s;
                if (!MarkerStatusText.TryParse(st, out s)) return false;
                f.TryGetValue("Comment", out note);
                record = new MarkerRecord { Key = key, Status = s, Note = note ?? "" };
                return true;
            }
        }

        private sealed class ThrowingFormat : IMarkerFormat
        {
            public static readonly Guid Id = new Guid("99999999-2222-3333-4444-555555555555");
            public Guid Schema { get { return Id; } }
            public int Version { get { return 0; } }
            public bool TryRead(IDictionary<string, string> f, out MarkerRecord record) { throw new InvalidOperationException("boom"); }
        }

        private static void Formats(Action<string, bool?> check)
        {
            var reader = new MarkerReader(new V0Format());
            var oldFields = new Dictionary<string, string> { { "Key", "k|0|o" }, { "Status", "Pending" }, { "Comment", "from v0" } };
            var r0 = reader.Read(V0Format.Id, oldFields);
            check(P + "an old-format record reads into the current shape, with the new fields at their defaults (RF19)",
                r0.Status == MarkerReadStatus.Ok && r0.Record != null && r0.Record.Key == "k|0|o" && r0.Record.Status == MarkerStatus.Pending
                && r0.Record.Note == "from v0" && r0.Record.SetName == "" && r0.Record.AlsoSets.Count == 0 && r0.Record.Seq == 0);
            check(P + "...and is flagged for upgrade, version 0", r0.NeedsUpgrade && r0.FormatVersion == 0);

            var r1 = reader.Read(MarkerFormats.Current, MarkerFormats.Write(Full()));
            check(P + "the current format is not flagged for upgrade, version 1", r1.Status == MarkerReadStatus.Ok && !r1.NeedsUpgrade && r1.FormatVersion == 1);
            check(P + "the default reader knows format 1 and nothing older",
                MarkerReader.Default.Read(MarkerFormats.Current, MarkerFormats.Write(Full())).Status == MarkerReadStatus.Ok
                && MarkerReader.Default.Read(V0Format.Id, oldFields).Status == MarkerReadStatus.NewerFormat);

            var unknown = MarkerReader.Default.Read(new Guid("deadbeef-dead-beef-dead-beefdeadbeef"), MarkerFormats.Write(Full()));
            check(P + "an unknown schema is passed over: NewerFormat, no record, 'written by a newer LiveClash' (RF19)",
                unknown.Status == MarkerReadStatus.NewerFormat && unknown.Record == null && unknown.Message.Contains("newer LiveClash"));

            var boom = new MarkerReader(new ThrowingFormat()).Read(ThrowingFormat.Id, oldFields);
            check(P + "a format whose reader throws gives Junk, not an exception", boom.Status == MarkerReadStatus.Junk && boom.Record == null);
        }

        // ------------------------------------------------------------------ junk

        private static void Junk(Action<string, bool?> check)
        {
            var good = MarkerFormats.Write(Full());

            Func<Dictionary<string, string>, MarkerReadResult> read = f => MarkerReader.Default.Read(MarkerFormats.Current, f);

            var noStatus = new Dictionary<string, string>(good); noStatus.Remove("Status");
            var badStatus = new Dictionary<string, string>(good); badStatus["Status"] = "Closed";
            var lower = new Dictionary<string, string>(good); lower["Status"] = "new";
            check(P + "a missing or unknown status is Junk (never guessed into New)",
                read(noStatus).Status == MarkerReadStatus.Junk && read(badStatus).Status == MarkerReadStatus.Junk && read(lower).Status == MarkerReadStatus.Junk);

            var badId = new Dictionary<string, string>(good); badId["HostId"] = "12x";
            var badDate = new Dictionary<string, string>(good); badDate["ReportedAt"] = "yesterday";
            var badNum = new Dictionary<string, string>(good); badNum["X"] = "1,5e";
            check(P + "a present but unreadable id, date or number is Junk",
                read(badId).Status == MarkerReadStatus.Junk && read(badDate).Status == MarkerReadStatus.Junk && read(badNum).Status == MarkerReadStatus.Junk);

            var sparse = new Dictionary<string, string> { { "Status", "New" } };
            var sp = read(sparse);
            check(P + "absent optional fields take their defaults (only the status is required)", sp.Status == MarkerReadStatus.Ok && sp.Record.HostId == 0 && sp.Record.Note == "");

            check(P + "a null field set is Junk and does not throw", NoThrow(() => MarkerReader.Default.Read(MarkerFormats.Current, null)) && MarkerReader.Default.Read(MarkerFormats.Current, null).Status == MarkerReadStatus.Junk);

            var rnd = new Random(31102026);
            string[] garbage = { "", " ", "x", "\u0000", "-1", "9999999999999999999999", "NaN", "1e999", "2026-13-45", "\t\n", "😀", new string('z', 5000) };
            int thrown = 0, inconsistent = 0;
            for (int i = 0; i < 500; i++)
            {
                var f = new Dictionary<string, string>();
                foreach (var name in Frozen1)
                {
                    string gv;
                    int roll = rnd.Next(4);
                    if (roll == 0) continue;
                    f[name] = roll == 1 ? garbage[rnd.Next(garbage.Length)] : (roll == 2 ? null : (good.TryGetValue(name, out gv) ? gv : "x"));
                }
                try
                {
                    var res = MarkerReader.Default.Read(MarkerFormats.Current, f);
                    bool ok = (res.Status == MarkerReadStatus.Ok && res.Record != null) || (res.Status == MarkerReadStatus.Junk && res.Record == null);
                    if (!ok) inconsistent++;
                }
                catch (Exception) { thrown++; }
            }
            check(P + "fuzz: junk record fields never throw (500 sets, seed 31102026)", thrown == 0);
            check(P + "fuzz: every junk set is either Ok with a record or Junk without one", inconsistent == 0);
        }

        private static void Statuses(Action<string, bool?> check)
        {
            var names = AllStatuses.Select(MarkerStatusText.Format).ToArray();
            check(P + "the status set is exactly New, Pending, Coordinated, Ignored, Reopened, Resolved",
                names.SequenceEqual(new[] { "New", "Pending", "Coordinated", "Ignored", "Reopened", "Resolved" }));
            MarkerStatus s;
            check(P + "status text parses exactly (case and spaces matter)",
                MarkerStatusText.TryParse("Reopened", out s) && s == MarkerStatus.Reopened && !MarkerStatusText.TryParse("reopened", out s)
                && !MarkerStatusText.TryParse(" New", out s) && !MarkerStatusText.TryParse("", out s) && !MarkerStatusText.TryParse(null, out s));
        }

        private static bool NoThrow(Action a)
        {
            try { a(); return true; } catch (Exception) { return false; }
        }
    }
}
