using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// Writes the register out as CSV.
    ///
    /// Exports ISSUES, not raw clashes — the grouped row is the unit of work, and a
    /// per-clash dump of 3,325 lines where 601 will do is a worse report, not a more
    /// detailed one. Each row still carries its member count so nothing is hidden.
    ///
    /// Excel opens this. That is the whole requirement: the coordination meeting runs
    /// on a spreadsheet, and until now the only way out of this tool was to read the
    /// panel and retype it.
    /// </summary>
    public static class CsvExport
    {
        /// <summary>
        /// Where an export is coming from, right now: the model, the time, the Revit username and the
        /// rules fingerprint. Gathered here, where the Revit-bound pieces live, so <see cref="CsvStamp"/>
        /// itself stays Revit-free and headless-tested. Never throws: a part it cannot read is left blank
        /// rather than costing the export.
        /// </summary>
        public static CsvStamp StampNow(string model)
        {
            var stamp = new CsvStamp { Exported = DateTime.Now };
            try
            {
                // ModelName and RevitUser.Current are plain accessors that are already fully
                // defensive on their own — this catch exists for RulesFingerprint.Of, which walks a
                // live ClashSettings and is not guaranteed never to throw. One catch, not three: a
                // part this cannot read is simply left blank, and it costs nothing already set,
                // since Model/By are assigned before the one line that can genuinely fail.
                stamp.Model = string.IsNullOrEmpty(model) ? ClashSettings.ModelName : model;
                stamp.By = RevitUser.Current;
                stamp.Rules = RulesFingerprint.Of(ClashSettings.Current);
            }
            catch { }
            return stamp;
        }

        /// <summary>
        /// The full coordination record — every Pending / Coordinated / Ignored decision
        /// and every Resolved pair — as one CSV with a State column. This is the whole
        /// history for the meeting, as opposed to <see cref="WriteIssues"/> which is only
        /// today's open register.
        /// </summary>
        /// <param name="path">Where to write the CSV.</param>
        /// <param name="rows">The rows to write, in order.</param>
        /// <param name="stamp">Where the export came from, appended to the header and to every row
        /// (see <see cref="CsvStamp"/>). Null writes the columns as they were before stamps existed.</param>
        public static int WriteRecord(string path, List<ClashRecordRow> rows, CsvStamp stamp = null)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            rows = rows ?? new List<ClashRecordRow>();
            string stampCells = stamp == null ? "" : stamp.RowCells();

            var sb = new StringBuilder();
            sb.Append("State,When,Still clashing,Element,Element id,Clashed with,Other id,Model,By,Note");
            if (stamp != null) sb.Append(CsvStamp.HeaderCells());
            sb.Append("\r\n");

            foreach (var r in rows)
            {
                sb.Append(Q(r.StateText)).Append(',');
                sb.Append(Q(r.WhenText)).Append(',');
                sb.Append(r.StillClashing ? "yes" : "").Append(',');
                sb.Append(Q(r.HostText)).Append(',');
                sb.Append(r.HostId != 0 ? r.HostId.ToString() : "").Append(',');
                sb.Append(Q(r.OtherText)).Append(',');
                sb.Append(r.OtherId != 0 ? r.OtherId.ToString() : "").Append(',');
                sb.Append(Q(r.ModelName)).Append(',');
                sb.Append(Q(r.By)).Append(',');
                sb.Append(Q(r.Note));
                sb.Append(stampCells);
                sb.Append("\r\n");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return rows.Count;
        }

        /// <param name="path">Where to write the CSV.</param>
        /// <param name="groups">The issues to write, one row each, in order.</param>
        /// <param name="modelName">Fallback model label for a group that carries none of its own.</param>
        /// <param name="stamp">Where the export came from, appended to the header and to every row
        /// (see <see cref="CsvStamp"/>). Null writes the columns as they were before stamps existed.</param>
        public static int WriteIssues(string path, List<ClashGroup> groups, string modelName, CsvStamp stamp = null)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            groups = groups ?? new List<ClashGroup>();
            string stampCells = stamp == null ? "" : stamp.RowCells();

            var sb = new StringBuilder();

            sb.Append("Issue,Clashes,Hard,Clearance,Unverified,Status,Statuses,Note,How they meet,Provisional,");
            sb.Append("Host category,Host elements,Host ids,");
            sb.Append("Other category,Other elements,Other ids,");
            sb.Append("Model,X (m),Y (m),Z (m),Insulated");
            if (stamp != null) sb.Append(CsvStamp.HeaderCells());
            sb.Append("\r\n");

            int n = 0;
            foreach (var g in groups)
            {
                n++;
                var first = g.First;

                sb.Append(n).Append(',');
                sb.Append(g.Count).Append(',');
                sb.Append(g.HardCount).Append(',');
                sb.Append(g.ClearanceCount).Append(',');
                sb.Append(g.UnverifiedCount).Append(',');
                sb.Append(Q(g.Status.ToString())).Append(',');
                sb.Append(Q(StatusMix(g))).Append(',');
                sb.Append(Q(NoteOf(g))).Append(',');
                sb.Append(Q(KindOf(g))).Append(',');
                sb.Append(AnyProvisional(g) ? "yes" : "").Append(',');

                sb.Append(Q(first != null ? first.HostCategory : "")).Append(',');
                sb.Append(Q(g.HostLabel)).Append(',');
                sb.Append(Q(Ids(g, true))).Append(',');

                sb.Append(Q(first != null ? first.OtherCategory : "")).Append(',');
                sb.Append(Q(g.OtherLabel)).Append(',');
                sb.Append(Q(Ids(g, false))).Append(',');

                sb.Append(Q(g.ModelName ?? modelName ?? "")).Append(',');

                var p = g.Location;
                sb.Append(M(p == null ? 0 : p.X)).Append(',');
                sb.Append(M(p == null ? 0 : p.Y)).Append(',');
                sb.Append(M(p == null ? 0 : p.Z)).Append(',');
                sb.Append(AnyInsulated(g) ? "yes" : "");
                sb.Append(stampCells);
                sb.Append("\r\n");
            }

            // UTF-8 WITH a BOM. Excel reads a BOM-less UTF-8 CSV as the system
            // codepage, which turns every ø and every non-ASCII family name to
            // mojibake — and this model is full of both.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return n;
        }

        /// <summary>
        /// The full spread of statuses in the group, as "143 New, 12 Pending".
        ///
        /// The Status column carries the WORST status, so a cluster holding one
        /// unreviewed clash reads New even when most of it is decided — and the Note
        /// column shows a note belonging to one of the decided members. Read together
        /// those two look contradictory. This says what is actually in there.
        /// </summary>
        private static string StatusMix(ClashGroup g)
        {
            var tally = new Dictionary<string, int>();
            foreach (var m in g.Members)
            {
                var k = ClashStatusStore.Instance.Get(m.Key).ToString();
                int n;
                tally[k] = tally.TryGetValue(k, out n) ? n + 1 : 1;
            }
            if (tally.Count <= 1) return "";      // nothing to add beyond Status

            var parts = new List<string>();
            foreach (var kv in tally) parts.Add(kv.Value + " " + kv.Key);
            parts.Sort();
            return string.Join(", ", parts.ToArray());
        }

        private static string KindOf(ClashGroup g)
        {
            // A group can mix, so report the dominant one rather than the first.
            var tally = new Dictionary<ClashGeometryKind, int>();
            foreach (var m in g.Members)
            {
                int c;
                tally[m.GeometryKind] = tally.TryGetValue(m.GeometryKind, out c) ? c + 1 : 1;
            }

            var best = ClashGeometryKind.Unknown;
            int bestN = -1;
            foreach (var kv in tally)
            {
                // Unknown only wins if it is all there is.
                if (kv.Key == ClashGeometryKind.Unknown && tally.Count > 1) continue;
                if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
            }

            switch (best)
            {
                case ClashGeometryKind.Termination: return "Service ends inside";
                case ClashGeometryKind.Through:     return "Passes through";
                case ClashGeometryKind.Enclosed:    return "Fully enclosed";
                case ClashGeometryKind.EndsInside:  return "Ends inside, cannot connect";
                default:                            return "";
            }
        }

        private static bool AnyProvisional(ClashGroup g)
        {
            foreach (var m in g.Members) if (m.Provisional) return true;
            return false;
        }

        private static bool AnyInsulated(ClashGroup g)
        {
            foreach (var m in g.Members) if (m.Insulated) return true;
            return false;
        }

        private static string NoteOf(ClashGroup g)
        {
            foreach (var k in g.Keys())
            {
                var note = ClashStatusStore.Instance.NoteOf(k);
                if (!string.IsNullOrEmpty(note)) return note;
            }
            return "";
        }

        private static string Ids(ClashGroup g, bool host)
        {
            var seen = new HashSet<long>();
            var sb = new StringBuilder();
            foreach (var m in g.Members)
            {
                var id = host ? m.HostElementId : m.OtherElementId;
                if (id == null) continue;
                var v = IdUtil.Value(id);
                if (!seen.Add(v)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(v);
                if (seen.Count >= 40) { sb.Append(" ..."); break; }
            }
            return sb.ToString();
        }

        private static string M(double feet)
        {
            return (feet * 0.3048).ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>RFC 4180 quoting plus Excel's formula guard - see <see cref="CsvText.Q"/>, where
        /// the rule now lives so the headless gate can prove it.</summary>
        private static string Q(string s)
        {
            return CsvText.Q(s);
        }
    }
}
