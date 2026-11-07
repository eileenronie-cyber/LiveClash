using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace LiveClash.Core
{
    /// <summary>The model-level settings record (spec 5.3): what a Refresh needs to find again without searching by name.</summary>
    public sealed class MarkerSettingsRecord
    {
        public string ScheduleUid { get; set; } = "";
        public string TypeUid { get; set; } = "";
        /// <summary>The LiveClash workset's id, or -1 in a model that is not workshared.</summary>
        public int WorksetId { get; set; } = -1;
        public string ParamVersion { get; set; } = "";
    }

    /// <summary>
    /// The two kinds of model-level record (spec 5.3), each one <see cref="DataStorage"/> element in Extensible Storage on the LiveClash
    /// workset, vendor LCLS, write access Vendor: ONE settings record (written by Create and Remove all), and ONE counter record per Revit
    /// user (the prefix and the next issue number), which only that user ever writes, so it is never contended between users (6.10).
    /// </summary>
    [RevitBound]
    public static class MarkerSettings
    {
        /// <summary>Which set of shared parameters the model has. A later build that adds one raises it.</summary>
        public const string ParamVersion = "1";

        private static readonly Guid SettingsSchemaGuid = new Guid("15598a51-6b79-4c66-a3f3-f676e80a6258");
        private static readonly Guid CounterSchemaGuid = new Guid("2545e044-7a60-4e62-897a-a7f3acea72a9");

        /// <summary>This seat's Revit user name.</summary>
        public static string UserName(Document doc)
        {
            try { return doc.Application.Username ?? ""; } catch { return ""; }
        }

        private static Schema Build(Guid guid, string name, string[] fields)
        {
            var existing = Schema.Lookup(guid);
            if (existing != null) return existing;
            var b = new SchemaBuilder(guid);
            b.SetSchemaName(name);
            b.SetVendorId(MarkerFormats.Vendor);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            foreach (var f in fields) b.AddSimpleField(f, typeof(string));
            return b.Finish();
        }

        private static readonly string[] SettingsFields = { "ScheduleUid", "TypeUid", "WorksetId", "ParamVersion" };
        private static readonly string[] CounterFields = { "UserName", "Prefix", "Next" };

        private static Schema SettingsSchema() { return Build(SettingsSchemaGuid, "LiveClashSettings1", SettingsFields); }
        private static Schema CounterSchema() { return Build(CounterSchemaGuid, "LiveClashCounter1", CounterFields); }

        private static List<DataStorage> Elements(Document doc, Guid guid)
        {
            var schema = Schema.Lookup(guid);
            var list = new List<DataStorage>();
            if (schema == null) return list;
            foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
            {
                var ds = e as DataStorage;
                if (ds == null) continue;
                try
                {
                    var ent = ds.GetEntity(schema);
                    if (ent != null && ent.IsValid()) list.Add(ds);
                }
                catch { }
            }
            return list.OrderBy(x => x.UniqueId, StringComparer.Ordinal).ToList();
        }

        private static void PutOnWorkset(Document doc, Element e)
        {
            int ws = doc.IsWorkshared ? MarkerWorkset.Find(doc) : -1;
            if (ws < 0) return;
            var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (p != null && !p.IsReadOnly) p.Set(ws);
        }

        // ------------------------------------------------------------------ settings

        public static int CountSettingsElements(Document doc)
        {
            return Elements(doc, SettingsSchemaGuid).Count;
        }

        /// <summary>The settings record, or null when the model has none yet. With more than one (a copy made before a sync), the lowest unique id.</summary>
        public static MarkerSettingsRecord ReadSettings(Document doc)
        {
            var schema = Schema.Lookup(SettingsSchemaGuid);
            var holder = Elements(doc, SettingsSchemaGuid).FirstOrDefault();
            if (schema == null || holder == null) return null;
            var ent = holder.GetEntity(schema);
            int ws;
            return new MarkerSettingsRecord
            {
                ScheduleUid = ent.Get<string>("ScheduleUid") ?? "",
                TypeUid = ent.Get<string>("TypeUid") ?? "",
                WorksetId = int.TryParse(ent.Get<string>("WorksetId"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ws) ? ws : -1,
                ParamVersion = ent.Get<string>("ParamVersion") ?? ""
            };
        }

        /// <summary>Writes the settings record, making its element (on the LiveClash workset) when there is none. Needs an open transaction.</summary>
        public static void WriteSettings(Document doc, MarkerSettingsRecord record)
        {
            var schema = SettingsSchema();
            var holder = Elements(doc, SettingsSchemaGuid).FirstOrDefault();
            if (holder == null)
            {
                holder = DataStorage.Create(doc);
                PutOnWorkset(doc, holder);
            }
            var ent = new Entity(schema);
            ent.Set<string>("ScheduleUid", record.ScheduleUid ?? "");
            ent.Set<string>("TypeUid", record.TypeUid ?? "");
            ent.Set<string>("WorksetId", record.WorksetId.ToString(CultureInfo.InvariantCulture));
            ent.Set<string>("ParamVersion", record.ParamVersion ?? "");
            holder.SetEntity(ent);
        }

        /// <summary>The user holding the settings element right now (another user), or empty. Only a workshared model with a central has holders.</summary>
        public static string HeldByOther(Document doc)
        {
            if (!MarkerOwnership.HasCentral(doc)) return "";
            var holder = Elements(doc, SettingsSchemaGuid).FirstOrDefault();
            if (holder == null) return "";
            try
            {
                string owner;
                var status = WorksharingUtils.GetCheckoutStatus(doc, holder.Id, out owner);
                return status == CheckoutStatus.OwnedByOtherUser ? (string.IsNullOrWhiteSpace(owner) ? "another user" : owner) : "";
            }
            catch { return ""; }
        }

        /// <summary>The unique ids of every model-level record element: the settings record and all the counter records (Remove all deletes them).</summary>
        public static List<string> RecordUids(Document doc)
        {
            return Elements(doc, SettingsSchemaGuid).Concat(Elements(doc, CounterSchemaGuid)).Select(e => e.UniqueId).ToList();
        }

        // ------------------------------------------------------------------ counters

        private static IssueNumbers.Counter ReadCounter(DataStorage holder, Schema schema)
        {
            var ent = holder.GetEntity(schema);
            int next;
            return new IssueNumbers.Counter
            {
                Uid = holder.UniqueId,
                UserName = ent.Get<string>("UserName") ?? "",
                Prefix = ent.Get<string>("Prefix") ?? "",
                Next = int.TryParse(ent.Get<string>("Next"), NumberStyles.None, CultureInfo.InvariantCulture, out next) && next > 0 ? next : 1
            };
        }

        /// <summary>Every user's counter record in the model.</summary>
        public static List<IssueNumbers.Counter> ReadCounters(Document doc)
        {
            var schema = Schema.Lookup(CounterSchemaGuid);
            if (schema == null) return new List<IssueNumbers.Counter>();
            return Elements(doc, CounterSchemaGuid).Select(h => ReadCounter(h, schema)).ToList();
        }

        /// <summary>This user's counter record, or null when they have made no markers in this model yet.</summary>
        public static IssueNumbers.Counter MyCounter(Document doc)
        {
            var me = UserName(doc);
            return ReadCounters(doc).FirstOrDefault(c => string.Equals(c.UserName, me, StringComparison.Ordinal));
        }

        /// <summary>
        /// Writes this user's counter record (making it when there is none). Only the user's own record is ever written: a counter for
        /// anyone else throws and changes nothing. Needs an open transaction.
        /// </summary>
        public static void WriteMyCounter(Document doc, IssueNumbers.Counter counter)
        {
            if (counter == null) throw new InvalidOperationException("There is no counter to write.");
            var me = UserName(doc);
            if (!string.Equals(counter.UserName, me, StringComparison.Ordinal))
                throw new InvalidOperationException("The issue-number counter belongs to another user; only its own user writes it.");
            var schema = CounterSchema();
            var holder = Elements(doc, CounterSchemaGuid)
                .FirstOrDefault(h => string.Equals(h.GetEntity(schema).Get<string>("UserName"), me, StringComparison.Ordinal));
            if (holder == null)
            {
                holder = DataStorage.Create(doc);
                PutOnWorkset(doc, holder);
            }
            var ent = new Entity(schema);
            ent.Set<string>("UserName", me);
            ent.Set<string>("Prefix", counter.Prefix ?? "");
            ent.Set<string>("Next", counter.Next.ToString(CultureInfo.InvariantCulture));
            holder.SetEntity(ent);
        }
    }
}
