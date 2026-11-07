using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiveClash.Core
{
    /// <summary>
    /// The shared, stable id of a clash marker: "LC-" plus 8 hex characters of the UniqueId triple
    /// (host element, link instance, other element), so it survives an ElementId renumbering at
    /// Synchronize and is the same on every seat for the same pair (spec 5.2). Pure, no Revit.
    /// </summary>
    public static class ClashIdOf
    {
        public const string Prefix = "LC-";

        private static readonly Regex Suffixed = new Regex("^(LC-[0-9a-fA-F]{8})-[0-9]+\\z", RegexOptions.CultureInvariant);

        /// <summary>The id for a pair. "" when the triple is incomplete (no host or no other UniqueId).</summary>
        public static string Of(string hostUid, string linkUid, string otherUid)
        {
            return Of(hostUid, linkUid, otherUid, ShortHash.Of);
        }

        /// <summary>The same with the hash supplied, so a test can force a collision.</summary>
        public static string Of(string hostUid, string linkUid, string otherUid, Func<string, string> hash)
        {
            var t = Triple(hostUid, linkUid, otherUid);
            return t.Length == 0 ? "" : Prefix + hash(t);
        }

        /// <summary>
        /// The canonical text the id is hashed from. "" when incomplete. A host pair (no link
        /// instance) is order-normalised, so A,B and B,A are one clash; a link pair is not, because
        /// host, link and other are different roles.
        /// </summary>
        public static string Triple(string hostUid, string linkUid, string otherUid)
        {
            if (string.IsNullOrEmpty(hostUid) || string.IsNullOrEmpty(otherUid)) return "";
            if (string.IsNullOrEmpty(linkUid))
            {
                bool hostFirst = string.CompareOrdinal(hostUid, otherUid) <= 0;
                return (hostFirst ? hostUid : otherUid) + "||" + (hostFirst ? otherUid : hostUid);
            }
            return hostUid + "|" + linkUid + "|" + otherUid;
        }

        /// <summary>The id without a collision suffix ("LC-1a2b3c4d-2" gives "LC-1a2b3c4d").</summary>
        public static string BaseOf(string clashId)
        {
            if (clashId == null) return "";
            var m = Suffixed.Match(clashId);
            return m.Success ? m.Groups[1].Value : clashId;
        }

        public sealed class Entry
        {
            public string MarkerUid { get; set; }
            public string ClashId { get; set; }
            public string Triple { get; set; }
        }

        /// <summary>
        /// Markers that share one base id but stand for different triples (an 8-hex collision). The
        /// triple whose lowest marker UniqueId (ordinal) is lowest keeps the plain id; the next gets
        /// "-2", then "-3". No clock decides. Returns marker UniqueId to the id it must carry, only
        /// for markers whose id differs.
        /// </summary>
        public static Dictionary<string, string> ResolveCollisions(IEnumerable<Entry> entries)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entries == null) return result;

            var valid = entries.Where(e => e != null && !string.IsNullOrEmpty(e.MarkerUid) && !string.IsNullOrEmpty(e.ClashId) && !string.IsNullOrEmpty(e.Triple)).ToList();
            foreach (var byBase in valid.GroupBy(e => BaseOf(e.ClashId), StringComparer.Ordinal))
            {
                var triples = byBase
                    .GroupBy(e => e.Triple, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var byUid = g.Select(e => e.MarkerUid).OrderBy(u => u, StringComparer.Ordinal);
                        // Stryker disable once Linq : a GroupBy group is never empty
                        var lowest = byUid.First();
                        return new { Triple = g.Key, Lowest = lowest, Members = g.ToList() };
                    })
                    .OrderBy(t => t.Lowest, StringComparer.Ordinal)
                    .ToList();
                if (triples.Count < 2) continue;

                for (int i = 0; i < triples.Count; i++)
                {
                    string wanted = i == 0 ? byBase.Key : byBase.Key + "-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    foreach (var e in triples[i].Members)
                        if (!string.Equals(e.ClashId, wanted, StringComparison.Ordinal)) result[e.MarkerUid] = wanted;
                }
            }
            return result;
        }
    }
}
