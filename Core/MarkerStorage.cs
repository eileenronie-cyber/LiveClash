using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace LiveClash.Core
{
    /// <summary>
    /// A marker's machine record in Extensible Storage (spec 5.1, 5.2). The truth about a marker; the shared parameters are only
    /// a projection of it. One schema per record format (rule M4): this file writes the newest format, format 1, and reads it
    /// through <see cref="MarkerReader"/>; a record in a schema this build does not know is NewerFormat, read-only.
    /// Vendor LCLS (the manifest's VendorId), write access Vendor (only LiveClash writes), read access Public.
    /// </summary>
    [RevitBound]
    public static class MarkerStorage
    {
        private static readonly MarkerReader Reader = MarkerReader.Default;

        /// <summary>The format-1 schema, made on first use (a schema cannot change once created, so the field list is frozen).</summary>
        public static Schema EnsureSchema()
        {
            var existing = Schema.Lookup(MarkerFormats.Format1);
            if (existing != null) return existing;
            var b = new SchemaBuilder(MarkerFormats.Format1);
            b.SetSchemaName("LiveClashMarker1");
            b.SetDocumentation("A LiveClash clash marker's record (format 1). Written only by LiveClash.");
            b.SetVendorId(MarkerFormats.Vendor);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            foreach (var f in MarkerFormats.Fields1) b.AddSimpleField(f, typeof(string));
            return b.Finish();
        }

        private static Entity Format1Entity(Element e)
        {
            if (e == null) return null;
            var schema = Schema.Lookup(MarkerFormats.Format1);
            if (schema == null) return null;
            try
            {
                var ent = e.GetEntity(schema);
                return ent != null && ent.IsValid() ? ent : null;
            }
            catch { return null; }
        }

        /// <summary>True when the element carries a valid format-1 record.</summary>
        public static bool HasRecord(Element e)
        {
            return Format1Entity(e) != null;
        }

        /// <summary>The raw field text of the element's format-1 record, or null when there is none.</summary>
        public static Dictionary<string, string> ReadFields(Element e)
        {
            var ent = Format1Entity(e);
            if (ent == null) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in MarkerFormats.Fields1)
            {
                string v = null;
                try { v = ent.Get<string>(f); } catch { }
                fields[f] = v ?? "";
            }
            return fields;
        }

        /// <summary>Writes the record in format 1. Needs an open transaction. Unpins the element for the write and pins it again.</summary>
        public static void Write(Element e, MarkerRecord record)
        {
            if (e == null) throw new InvalidOperationException("There is no element to write.");
            var values = MarkerFormats.Write(record);
            var ent = new Entity(EnsureSchema());
            foreach (var f in MarkerFormats.Fields1) ent.Set<string>(f, values[f]);
            MarkerElements.WhilePinnedOff(e, () => e.SetEntity(ent));
        }

        /// <summary>
        /// Reads the element's record. Never throws. A format-1 record is read through the one reader; a record in any other
        /// LiveClash schema is NewerFormat; no record at all is Junk ("No record").
        /// </summary>
        public static MarkerReadResult Read(Element e)
        {
            try
            {
                var fields = ReadFields(e);
                if (fields != null) return Reader.Read(MarkerFormats.Format1, fields);
                if (e != null)
                    foreach (var g in e.GetEntitySchemaGuids())
                        if (g != MarkerFormats.Format1) return Reader.Read(g, null);
                return new MarkerReadResult { Status = MarkerReadStatus.Junk, Message = "No record." };
            }
            catch (Exception ex)
            {
                return new MarkerReadResult { Status = MarkerReadStatus.Junk, Message = "The record could not be read: " + ex.GetType().Name };
            }
        }

        /// <summary>
        /// A hash of everything stored on the element in LiveClash's schemas, so a check can prove a record did not change
        /// (a move, a pass-over, a no-op Refresh). "(none)" when there is no record.
        /// </summary>
        public static string Hash(Element e)
        {
            var sb = new StringBuilder();
            try
            {
                var fields = ReadFields(e);
                if (fields != null)
                    foreach (var f in MarkerFormats.Fields1) sb.Append(f).Append('=').Append(fields[f]).Append('\n');
                if (e != null)
                    foreach (var g in e.GetEntitySchemaGuids().OrderBy(x => x))
                    {
                        if (g == MarkerFormats.Format1) continue;
                        sb.Append("schema ").Append(g.ToString("D", CultureInfo.InvariantCulture)).Append('\n');
                        var schema = Schema.Lookup(g);
                        if (schema == null) continue;
                        var ent = e.GetEntity(schema);
                        if (ent == null || !ent.IsValid()) continue;
                        foreach (var f in schema.ListFields().Where(x => x.ValueType == typeof(string)).OrderBy(x => x.FieldName, StringComparer.Ordinal))
                            sb.Append(f.FieldName).Append('=').Append(ent.Get<string>(f.FieldName)).Append('\n');
                    }
            }
            catch (Exception ex) { sb.Append("(unreadable ").Append(ex.GetType().Name).Append(')'); }
            if (sb.Length == 0) return "(none)";
            return Sha256Hex.Of(Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }
}
