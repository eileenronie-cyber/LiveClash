using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The Revit half of the row builder: a ClashResult and the document become the plain values of
    /// <see cref="RowBuilder.Source"/> (UniqueIds, ids, point, display texts, the host level at the clash point, the rule the
    /// seat's settings imply). Thin on purpose; everything that decides anything is in the pure <see cref="RowBuilder"/>.
    /// </summary>
    internal static class RowAdapter
    {
        /// <summary>Stopwatch ticks spent in <see cref="SourceOf"/>'s level lookup, rule lookup and element reads; <see cref="MarkerCollector.Collect"/> resets and reads them (one Revit thread).</summary>
        internal static long LevelTicks, RuleTicks, ReadTicks;

        [RevitBound]
        internal static RowBuilder.Source SourceOf(
            Document doc, ClashResult r, Dictionary<long, LinkContext> links, ClashSettings s, string groupKey, LevelTable levels = null)
        {
            if (doc == null || !doc.IsValidObject || r == null) return null;

            var src = new RowBuilder.Source
            {
                Key = r.Key,
                HostId = IdUtil.Value(r.HostElementId), OtherId = IdUtil.Value(r.OtherElementId),
                LinkId = r.LinkInstanceId == null ? 0 : IdUtil.Value(r.LinkInstanceId),
                Severity = r.Severity.ToString(), SetName = r.SetName, AlsoSets = r.AlsoSets, GroupKey = groupKey,
                HostName = r.HostElementName, HostCategory = r.HostCategory, OtherName = r.OtherElementName, OtherCategory = r.OtherCategory, ModelName = r.ModelName,
                HostUid = "", LinkUid = "", OtherUid = ""
            };

            long tRead0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Element host = null;
            try { host = doc.GetElement(r.HostElementId); } catch { }
            if (host != null && host.IsValidObject) src.HostUid = host.UniqueId;

            try
            {
                if (r.LinkInstanceId == null)
                {
                    var other = doc.GetElement(r.OtherElementId);
                    if (other != null && other.IsValidObject) src.OtherUid = other.UniqueId;
                }
                else
                {
                    var instance = doc.GetElement(r.LinkInstanceId);
                    if (instance != null && instance.IsValidObject) src.LinkUid = instance.UniqueId;
                    LinkContext link = null;
                    if (links != null) links.TryGetValue(IdUtil.Value(r.LinkInstanceId), out link);
                    if (link != null && link.Document != null && link.Document.IsValidObject)
                    {
                        var other = link.Document.GetElement(r.OtherElementId);
                        if (other != null && other.IsValidObject) src.OtherUid = other.UniqueId;
                    }
                }
            }
            catch { }

            XYZ at = r.Location;
            if (at == null && host != null)
            {
                try
                {
                    var box = host.get_BoundingBox(null);
                    if (box != null) at = (box.Min + box.Max) * 0.5;
                }
                catch { }
            }
            ReadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tRead0;
            src.HasPoint = at != null;
            if (at != null)
            {
                long tLevel0 = System.Diagnostics.Stopwatch.GetTimestamp();
                src.X = at.X; src.Y = at.Y; src.Z = at.Z; src.Level = levels != null ? levels.At(at.Z) : LevelAt(doc, at.Z);   // Collect passes the table it read once
                LevelTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tLevel0;
            }

            double mm; bool insulated;
            long tRule0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool hasRule = RuleOf(doc, r, s, links, out mm, out insulated);
            RuleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tRule0;
            if (hasRule) { src.RuleClearanceMm = mm; src.RuleInsulated = insulated; }
            return src;
        }

        [RevitBound]
        private static bool RuleOf(Document doc, ClashResult r, ClashSettings s, Dictionary<long, LinkContext> links, out double mm, out bool insulated)
        {
            return PairVerifier.RuleFor(doc, r, s, links, out mm, out insulated);
        }

        /// <summary>The name of the highest level whose elevation is at or below <paramref name="zFeet"/>; "" when there is none.</summary>
        [RevitBound]
        internal static string LevelAt(Document doc, double zFeet)
        {
            if (doc == null || !doc.IsValidObject) return "";
            return LevelsOf(doc).At(zFeet);
        }

        /// <summary>The model's levels as a table, read ONCE (a model with no readable levels gives an empty table: every point is on no level).</summary>
        [RevitBound]
        internal static LevelTable LevelsOf(Document doc)
        {
            try
            {
                return new LevelTable(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => new KeyValuePair<double, string>(l.Elevation, l.Name)).ToList());
            }
            catch { return new LevelTable(null); }
        }
    }
}
