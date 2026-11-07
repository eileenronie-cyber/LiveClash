using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    /// <summary>
    /// The shared parameters a marker carries and the text each one shows (spec 4, 5.1, 10.1). Pure, no Revit.
    ///
    /// The GUIDs are FROZEN: they are the binding's identity in every model that has markers, and the public copy keeps
    /// them. Changing one orphans the parameter in every model. 22 Text parameters in the schedule's column order, and one
    /// hidden Integer, <see cref="StatusOrderName"/>, that the schedule sorts on (a Text sort of the status would be alphabetical).
    /// The parameters are a projection of the record and are never read back as truth (5.1).
    /// </summary>
    public static class MarkerParameterMap
    {
        public sealed class Def
        {
            public string Name { get; set; }
            public Guid ParameterGuid { get; set; }
            public bool IsInteger { get; set; }
        }

        public const string StatusOrderName = "LC Status Order";

        /// <summary>Probe P24 decides whether a numeric "Last checked" is needed (SPEC Q18); off until then, so no GUID is bound for it.</summary>
        public static bool IncludeLastCheckedNum { get { return false; } }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static Def T(string name, string guid)
        {
            return new Def { Name = name, ParameterGuid = new Guid(guid) };
        }

        /// <summary>Every parameter, in the schedule's column order, the Integer last.</summary>
        public static readonly IReadOnlyList<Def> Definitions = new List<Def>
        {
            T("LC Issue No", "2f42272f-f5d5-4014-bd3b-f168f587d9e1"),
            T("LC Clash Id", "a39fd53c-3701-4bf4-bdfd-c74376ab3d77"),
            T("LC Status", "1db0abb4-fe36-4ae4-af98-2569787c4524"),
            T("LC Severity", "a803407d-7d5e-4721-85bf-5341f006f975"),
            T("LC Set", "69ce33a8-1a37-4d75-8f36-bea0a3e731d7"),
            T("LC Also Caught By", "035032f8-dee2-488a-a7aa-3c5626048c46"),
            T("LC Group", "87653ea6-ab15-455b-95ef-c963acba1c58"),
            T("LC A Id", "988a9708-b5dc-496b-af42-bd4310cf48dc"),
            T("LC A Name", "3afe2f72-8a2a-4f78-b70b-2833ebaa0326"),
            T("LC A Category", "6159beee-877f-450a-9635-08ca93ce3fb0"),
            T("LC B Id", "4d194777-bafa-483e-8558-d9cf8a7e6209"),
            T("LC B Name", "9c0cdfc7-bee1-4ce7-895d-66dc19a93a91"),
            T("LC B Category", "f882682d-33a6-4380-b296-9f9b29f118ee"),
            T("LC B Model", "b07666d2-6b4f-46c4-91a8-fd8fd0e858c6"),
            T("LC Level", "ed63dcca-7914-48bd-aa88-87e7428159bc"),
            T("LC Assigned To", "2d535f69-e98e-4a93-b4e8-4abd500a96c1"),
            T("LC Reported By", "aaa49466-d3be-43bc-a743-fca369334610"),
            T("LC Reported", "d1caebfb-e35f-4d4c-bda3-46f3812b6d82"),
            T("LC Changed By", "ecc2c3de-313d-44af-bcc2-9d98480d4405"),
            T("LC Changed", "4b62b25f-0895-4276-b57b-b7b089a62c0b"),
            T("LC Note", "9eade995-09b5-4e13-8e8f-c45adb0a79b1"),
            T("LC Last Checked", "6c8a8f48-88c7-4a46-a751-7c213dec4ce2"),
            new Def { Name = StatusOrderName, ParameterGuid = new Guid("c4a57272-9940-4070-a22a-66da1cf9c734"), IsInteger = true }
        };

        /// <summary>The columns re-read from the elements at Refresh (names, categories, model, level): the 'shown' map's keys.</summary>
        public static readonly IReadOnlyList<string> ShownNames = new List<string>
        {
            "LC A Name", "LC A Category", "LC B Name", "LC B Category", "LC B Model", "LC Level"
        };

        /// <summary>The columns that come from the record alone. A mismatch here is tampering (5.1): rewritten from the record.</summary>
        public static readonly IReadOnlyList<string> RecordDerivedNames =
            Definitions.Where(d => !d.IsInteger && !ShownNames.Contains(d.Name)).Select(d => d.Name).ToList();

        private static string Id(long v)
        {
            return v == 0 ? "" : v.ToString(Inv);
        }

        private static string Stamp(DateTimeOffset? t)
        {
            return t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm zzz", Inv) : "";
        }

        private static string Day(DateTimeOffset? t)
        {
            return t.HasValue ? t.Value.ToString("yyyy-MM-dd", Inv) : "";
        }

        /// <summary>
        /// The text of all 22 Text parameters. The record gives its own columns; the six display columns come from
        /// <paramref name="shown"/> (keyed by the parameter name) and are empty when missing. Times print the writer's own UTC
        /// offset (L3); ids print whole, and an unset id (0) prints empty. Never throws; no value is null.
        /// </summary>
        public static Dictionary<string, string> Build(MarkerRecord record, IDictionary<string, string> shown)
        {
            var r = record ?? new MarkerRecord();
            Func<string, string> s = x => x ?? "";
            Func<string, string> show = name =>
            {
                string v = null;
                return shown != null && shown.TryGetValue(name, out v) && v != null ? v : "";
            };
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "LC Issue No", s(r.IssueNo) }, { "LC Clash Id", s(r.ClashId) }, { "LC Status", MarkerStatusText.Format(r.Status) },
                { "LC Severity", s(r.Severity) }, { "LC Set", s(r.SetName) },
                { "LC Also Caught By", r.AlsoSets == null ? "" : string.Join(", ", r.AlsoSets.Where(x => !string.IsNullOrEmpty(x))) },
                { "LC Group", s(r.GroupLabel) },
                { "LC A Id", Id(r.HostId) }, { "LC A Name", show("LC A Name") }, { "LC A Category", show("LC A Category") },
                { "LC B Id", Id(r.OtherId) }, { "LC B Name", show("LC B Name") }, { "LC B Category", show("LC B Category") },
                { "LC B Model", show("LC B Model") }, { "LC Level", show("LC Level") },
                { "LC Assigned To", s(r.AssignedTo) }, { "LC Reported By", s(r.ReportedBy) }, { "LC Reported", Stamp(r.ReportedAt) },
                { "LC Changed By", s(r.ChangedBy) }, { "LC Changed", Stamp(r.ChangedAt) }, { "LC Note", s(r.Note) },
                { "LC Last Checked", Day(r.LastCheckedAt) }
            };
        }
    }
}
