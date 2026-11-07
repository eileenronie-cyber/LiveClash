using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    public enum MarkerStatus
    {
        New,
        Pending,
        Coordinated,
        Ignored,
        Reopened,
        Resolved
    }

    public static class MarkerStatusText
    {
        private static readonly MarkerStatus[] All =
        {
            MarkerStatus.New, MarkerStatus.Pending, MarkerStatus.Coordinated,
            MarkerStatus.Ignored, MarkerStatus.Reopened, MarkerStatus.Resolved
        };

        /// <summary>The sort order of a status in the schedule: 1 New, 2 Pending, 3 Reopened, 4 Coordinated, 5 Ignored, 6 Resolved.</summary>
        public static int Order(MarkerStatus s)
        {
            switch (s)
            {
                case MarkerStatus.New: return 1;
                case MarkerStatus.Pending: return 2;
                case MarkerStatus.Reopened: return 3;
                case MarkerStatus.Coordinated: return 4;
                case MarkerStatus.Ignored: return 5;
                default: return 6;
            }
        }

        public static string Format(MarkerStatus s)
        {
            return s.ToString();
        }

        public static bool TryParse(string text, out MarkerStatus status)
        {
            status = MarkerStatus.New;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var s in All)
                if (string.Equals(text, s.ToString(), StringComparison.Ordinal)) { status = s; return true; }
            return false;
        }
    }

    /// <summary>One marker's machine record in the current shape (spec 5.2). Every format reads into this.</summary>
    public sealed class MarkerRecord
    {
        public string Key { get; set; } = "";
        public string HostUid { get; set; } = "";
        public string LinkUid { get; set; } = "";
        public string OtherUid { get; set; } = "";
        public long HostId { get; set; }
        public long LinkId { get; set; }
        public long OtherId { get; set; }
        public string ClashId { get; set; } = "";
        public string IssueNo { get; set; } = "";
        public List<string> MergedIssueNos { get; set; } = new List<string>();
        public MarkerStatus Status { get; set; } = MarkerStatus.New;
        public string Note { get; set; } = "";
        public string AssignedTo { get; set; } = "";
        public string ReportedBy { get; set; } = "";
        public DateTimeOffset? ReportedAt { get; set; }
        public string ChangedBy { get; set; } = "";
        public DateTimeOffset? ChangedAt { get; set; }
        public string Severity { get; set; } = "";
        public bool Insulated { get; set; }
        public double ClearanceMm { get; set; }
        public string SetName { get; set; } = "";
        public List<string> AlsoSets { get; set; } = new List<string>();
        public string GroupLabel { get; set; } = "";
        public string ResolvedBy { get; set; } = "";
        public DateTimeOffset? ResolvedAt { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }
        public string LastCheckedBy { get; set; } = "";
        public long Seq { get; set; }
    }

    public enum MarkerReadStatus
    {
        Ok,
        /// <summary>A schema this build does not know: read-only, passed over ("written by a newer LiveClash").</summary>
        NewerFormat,
        /// <summary>Known schema, unreadable content.</summary>
        Junk
    }

    public sealed class MarkerReadResult
    {
        public MarkerReadStatus Status { get; set; }
        public MarkerRecord Record { get; set; }
        public int FormatVersion { get; set; }
        /// <summary>Read from an older format: upgraded only when the marker is written anyway.</summary>
        public bool NeedsUpgrade { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>One record format: its schema GUID, version, and how its fields map to the current shape.</summary>
    public interface IMarkerFormat
    {
        Guid Schema { get; }
        int Version { get; }
        bool TryRead(IDictionary<string, string> fields, out MarkerRecord record);
    }

    /// <summary>
    /// The identity constants and the writer of the newest record format. An Extensible Storage schema
    /// cannot change once created, so every format has its own GUID and a frozen field list: a change
    /// gets a NEW GUID, never new fields under an old one (spec 5.1, rule M4). The writer writes only
    /// the newest format; <see cref="MarkerReader"/> reads every format it knows.
    /// </summary>
    public static class MarkerFormats
    {
        /// <summary>The manifest's VendorId, a compatibility constant: changing it orphans every record.</summary>
        public const string Vendor = "LCLS";
        public const string ApplicationId = "LiveClash";
        public const string ApplicationDataId = "marker/1";

        /// <summary>Format 1's schema GUID. Frozen: the field list below is the spec's (5.2), so Debug and Release share it.</summary>
        public static readonly Guid Format1 = new Guid("7b0c4a52-91d3-4e68-a5f7-3c2e18d94b06");

        /// <summary>The version number of the newest format; an older record is flagged for upgrade.</summary>
        public const int CurrentVersion = 1;

        public static Guid Current { get { return Format1; } }

        /// <summary>The frozen field list of format 1, in spec order. Every value is a string; ids are 64-bit text.</summary>
        public static readonly string[] Fields1 =
        {
            "Key", "HostUid", "LinkUid", "OtherUid", "HostId", "LinkId", "OtherId", "ClashId", "IssueNo", "MergedIssueNos",
            "Status", "Note", "AssignedTo", "ReportedBy", "ReportedAt", "ChangedBy", "ChangedAt", "Severity", "Insulated",
            "ClearanceMm", "SetName", "AlsoSets", "GroupLabel", "ResolvedBy", "ResolvedAt", "X", "Y", "Z",
            "LastCheckedAt", "LastCheckedBy", "Seq"
        };

        internal const char ListSeparator = '\u001f';

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The record as format-1 field text, culture-independent. Never throws; null text and lists are written empty.</summary>
        public static Dictionary<string, string> Write(MarkerRecord r)
        {
            r = r ?? new MarkerRecord();
            Func<string, string> s = x => x ?? "";
            Func<long, string> l = x => x.ToString(Inv);
            Func<double, string> d = x => x.ToString("R", Inv);
            Func<DateTimeOffset?, string> t = x => x.HasValue ? x.Value.ToString("o", Inv) : "";
            Func<List<string>, string> list = x => x == null ? "" : string.Join(ListSeparator.ToString(), x.Where(i => !string.IsNullOrEmpty(i)));
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Key", s(r.Key) }, { "HostUid", s(r.HostUid) }, { "LinkUid", s(r.LinkUid) }, { "OtherUid", s(r.OtherUid) },
                { "HostId", l(r.HostId) }, { "LinkId", l(r.LinkId) }, { "OtherId", l(r.OtherId) },
                { "ClashId", s(r.ClashId) }, { "IssueNo", s(r.IssueNo) }, { "MergedIssueNos", list(r.MergedIssueNos) },
                { "Status", MarkerStatusText.Format(r.Status) }, { "Note", s(r.Note) }, { "AssignedTo", s(r.AssignedTo) },
                { "ReportedBy", s(r.ReportedBy) }, { "ReportedAt", t(r.ReportedAt) },
                { "ChangedBy", s(r.ChangedBy) }, { "ChangedAt", t(r.ChangedAt) },
                { "Severity", s(r.Severity) }, { "Insulated", r.Insulated ? "1" : "0" }, { "ClearanceMm", d(r.ClearanceMm) },
                { "SetName", s(r.SetName) }, { "AlsoSets", list(r.AlsoSets) }, { "GroupLabel", s(r.GroupLabel) },
                { "ResolvedBy", s(r.ResolvedBy) }, { "ResolvedAt", t(r.ResolvedAt) },
                { "X", d(r.X) }, { "Y", d(r.Y) }, { "Z", d(r.Z) },
                { "LastCheckedAt", t(r.LastCheckedAt) }, { "LastCheckedBy", s(r.LastCheckedBy) }, { "Seq", l(r.Seq) }
            };
        }
    }

    /// <summary>Format 1's reader. Only the status is required; a present but unreadable value makes the record Junk.</summary>
    internal sealed class MarkerFormat1 : IMarkerFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public Guid Schema { get { return MarkerFormats.Format1; } }
        public int Version { get { return 1; } }

        private static string Get(IDictionary<string, string> f, string name)
        {
            string v;
            return f.TryGetValue(name, out v) && v != null ? v : "";
        }

        private static bool Long(IDictionary<string, string> f, string name, out long value)
        {
            var v = Get(f, name);
            value = 0;
            return string.IsNullOrWhiteSpace(v) || long.TryParse(v, NumberStyles.AllowLeadingSign, Inv, out value);
        }

        private static bool Dbl(IDictionary<string, string> f, string name, out double value)
        {
            var v = Get(f, name);
            value = 0;
            return string.IsNullOrWhiteSpace(v) || double.TryParse(v, NumberStyles.Float, Inv, out value);
        }

        private static bool When(IDictionary<string, string> f, string name, out DateTimeOffset? value)
        {
            var v = Get(f, name);
            value = null;
            if (string.IsNullOrWhiteSpace(v)) return true;
            DateTimeOffset t;
            if (!DateTimeOffset.TryParse(v, Inv, DateTimeStyles.None, out t)) return false;
            value = t;
            return true;
        }

        private static List<string> Items(IDictionary<string, string> f, string name)
        {
            return Get(f, name).Split(MarkerFormats.ListSeparator).Where(i => i.Length > 0).ToList();
        }

        public bool TryRead(IDictionary<string, string> fields, out MarkerRecord record)
        {
            record = null;
            MarkerStatus status;
            if (!MarkerStatusText.TryParse(Get(fields, "Status"), out status)) return false;

            bool insulated = false;
            var ins = Get(fields, "Insulated").Trim();
            if (ins == "1" || string.Equals(ins, "true", StringComparison.OrdinalIgnoreCase)) insulated = true;
            else if (ins.Length > 0 && ins != "0" && !string.Equals(ins, "false", StringComparison.OrdinalIgnoreCase)) return false;

            long hostId = 0, linkId = 0, otherId = 0, seq = 0;
            double clearance = 0, x = 0, y = 0, z = 0;
            DateTimeOffset? reportedAt = null, changedAt = null, resolvedAt = null, checkedAt = null;
            if (!Long(fields, "HostId", out hostId) || !Long(fields, "LinkId", out linkId) || !Long(fields, "OtherId", out otherId) || !Long(fields, "Seq", out seq)) return false;
            if (!Dbl(fields, "ClearanceMm", out clearance) || !Dbl(fields, "X", out x) || !Dbl(fields, "Y", out y) || !Dbl(fields, "Z", out z)) return false;
            if (!When(fields, "ReportedAt", out reportedAt) || !When(fields, "ChangedAt", out changedAt)
                || !When(fields, "ResolvedAt", out resolvedAt) || !When(fields, "LastCheckedAt", out checkedAt)) return false;

            record = new MarkerRecord
            {
                Key = Get(fields, "Key"), HostUid = Get(fields, "HostUid"), LinkUid = Get(fields, "LinkUid"), OtherUid = Get(fields, "OtherUid"),
                HostId = hostId, LinkId = linkId, OtherId = otherId,
                ClashId = Get(fields, "ClashId"), IssueNo = Get(fields, "IssueNo"), MergedIssueNos = Items(fields, "MergedIssueNos"),
                Status = status, Note = Get(fields, "Note"), AssignedTo = Get(fields, "AssignedTo"),
                ReportedBy = Get(fields, "ReportedBy"), ReportedAt = reportedAt, ChangedBy = Get(fields, "ChangedBy"), ChangedAt = changedAt,
                Severity = Get(fields, "Severity"), Insulated = insulated, ClearanceMm = clearance,
                SetName = Get(fields, "SetName"), AlsoSets = Items(fields, "AlsoSets"), GroupLabel = Get(fields, "GroupLabel"),
                ResolvedBy = Get(fields, "ResolvedBy"), ResolvedAt = resolvedAt, X = x, Y = y, Z = z,
                LastCheckedAt = checkedAt, LastCheckedBy = Get(fields, "LastCheckedBy"), Seq = seq
            };
            return true;
        }
    }

    /// <summary>
    /// The one reader over every format this build knows (format 1 now, older ones passed to the
    /// constructor). A schema it does not know is a newer format: read-only, passed over, never rewritten.
    /// </summary>
    public sealed class MarkerReader
    {
        private readonly Dictionary<Guid, IMarkerFormat> _formats = new Dictionary<Guid, IMarkerFormat>();

        public MarkerReader(params IMarkerFormat[] olderFormats)
        {
            var one = new MarkerFormat1();
            _formats[one.Schema] = one;
            if (olderFormats != null)
                foreach (var f in olderFormats)
                    if (f != null && !_formats.ContainsKey(f.Schema)) _formats[f.Schema] = f;
        }

        public static MarkerReader Default { get { return new MarkerReader(); } }

        /// <summary>Reads one marker's fields. Never throws: unreadable content is Junk, an unknown schema is NewerFormat.</summary>
        public MarkerReadResult Read(Guid schema, IDictionary<string, string> fields)
        {
            IMarkerFormat format;
            if (!_formats.TryGetValue(schema, out format))
                return new MarkerReadResult { Status = MarkerReadStatus.NewerFormat, Message = "Written by a newer LiveClash (record format " + schema + "); update to change it." };
            if (fields == null)
                return new MarkerReadResult { Status = MarkerReadStatus.Junk, FormatVersion = format.Version, Message = "No fields." };

            try
            {
                MarkerRecord record;
                if (!format.TryRead(fields, out record) || record == null)
                    return new MarkerReadResult { Status = MarkerReadStatus.Junk, FormatVersion = format.Version, Message = "The record could not be read." };
                return new MarkerReadResult
                {
                    Status = MarkerReadStatus.Ok, Record = record, FormatVersion = format.Version,
                    NeedsUpgrade = format.Version < MarkerFormats.CurrentVersion
                };
            }
            catch (Exception ex)
            {
                return new MarkerReadResult { Status = MarkerReadStatus.Junk, FormatVersion = format.Version, Message = "The record could not be read: " + ex.GetType().Name };
            }
        }
    }
}
