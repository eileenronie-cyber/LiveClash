using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>Where a pair sits in the coordination record.</summary>
    public enum ClashRecordState
    {
        /// <summary>Reviewed, still to fix.</summary>
        Pending = 0,
        /// <summary>Deliberately accepted — a real intersection that stays.</summary>
        Coordinated = 1,
        /// <summary>A false positive the suppressor missed.</summary>
        Ignored = 2,
        /// <summary>Re-tested and found gone — the engine's own verdict, never a manual one.</summary>
        Resolved = 3,
    }

    /// <summary>One line of the unified record: a decision, or a resolution.</summary>
    public class ClashRecordRow
    {
        public ClashRecordState State;
        public string Key;
        public DateTime WhenUtc;
        public string By = "";
        public string Note = "";

        public long HostId, OtherId;
        public string HostText = "";
        public string OtherText = "";
        public string ModelName = "";

        /// <summary>The pair is in the live results right now.</summary>
        public bool StillClashing;

        public string WhenText { get { return WhenUtc == DateTime.MinValue ? "" : WhenUtc.ToLocalTime().ToString("dd MMM HH:mm"); } }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case ClashRecordState.Pending:     return "Pending";
                    case ClashRecordState.Coordinated: return "Coordinated";
                    case ClashRecordState.Ignored:     return "Ignored";
                    default:                           return StillClashing ? "Clashing again" : "Resolved";
                }
            }
        }
    }

    /// <summary>
    /// The unified coordination record — every pair someone decided about (Pending /
    /// Coordinated / Ignored, from <see cref="ClashStatusStore"/>) plus every pair the
    /// engine re-tested and found gone (Resolved, from <see cref="ResolvedLog"/>).
    ///
    /// The recording already happens per-model and append-only; this just joins the two
    /// stores into one list for the record window and the full-record CSV. Element names
    /// come from the detail captured at decision time, falling back to the live result,
    /// then to the bare id — a decision made before detail capture, on a pair no longer
    /// live, still shows something useful.
    /// </summary>
    public static class ClashRecord
    {
        public static List<ClashRecordRow> Build()
        {
            var rows = new List<ClashRecordRow>();

            var live = new Dictionary<string, ClashResult>();
            foreach (var r in ClashResultStore.Instance.Snapshot())
                if (!string.IsNullOrEmpty(r.Key) && !live.ContainsKey(r.Key)) live[r.Key] = r;

            // ---- decisions
            foreach (var kv in ClashStatusStore.Instance.All())
            {
                var e = kv.Value;
                if (e == null || e.Kind == ClashStatusKind.New) continue;

                ClashRecordState state;
                switch (e.Kind)
                {
                    case ClashStatusKind.Pending:     state = ClashRecordState.Pending; break;
                    case ClashStatusKind.Coordinated: state = ClashRecordState.Coordinated; break;
                    case ClashStatusKind.Ignored:     state = ClashRecordState.Ignored; break;
                    default: continue;
                }

                ClashResult lr;
                live.TryGetValue(kv.Key, out lr);

                rows.Add(new ClashRecordRow
                {
                    State = state,
                    Key = kv.Key,
                    WhenUtc = e.WhenUtc,
                    By = e.By ?? "",
                    Note = e.Note ?? "",
                    HostId = IdOr(e.HostId, lr, true),
                    OtherId = IdOr(e.OtherId, lr, false),
                    HostText = Text(e.HostCategory, e.HostName, e.HostId, lr, true),
                    OtherText = Text(e.OtherCategory, e.OtherName, e.OtherId, lr, false),
                    ModelName = !string.IsNullOrEmpty(e.ModelName) ? e.ModelName
                              : lr != null ? (lr.ModelName ?? "") : "",
                    StillClashing = lr != null,
                });
            }

            // ---- resolutions
            foreach (var r in ResolvedLog.Snapshot())
            {
                rows.Add(new ClashRecordRow
                {
                    State = ClashRecordState.Resolved,
                    Key = r.Key,
                    WhenUtc = r.WhenUtc,
                    By = r.By ?? "",
                    Note = "",
                    HostId = r.HostId,
                    OtherId = r.OtherId,
                    HostText = Join(r.HostCategory, r.HostName),
                    OtherText = Join(r.OtherCategory, r.OtherName),
                    ModelName = r.ModelName ?? "",
                    StillClashing = r.ClashingAgain,
                });
            }

            rows.Sort(delegate (ClashRecordRow a, ClashRecordRow b)
            {
                int c = a.State.CompareTo(b.State);
                return c != 0 ? c : b.WhenUtc.CompareTo(a.WhenUtc);
            });
            return rows;
        }

        /// <summary>Counts per state, for the panel breakdown line.</summary>
        public static Dictionary<ClashRecordState, int> Tally(List<ClashRecordRow> rows)
        {
            var t = new Dictionary<ClashRecordState, int>();
            foreach (ClashRecordState s in Enum.GetValues(typeof(ClashRecordState))) t[s] = 0;
            if (rows != null) foreach (var r in rows) t[r.State]++;
            return t;
        }

        private static long IdOr(long stored, ClashResult lr, bool host)
        {
            if (stored != 0) return stored;
            if (lr == null) return 0;
            var id = host ? lr.HostElementId : lr.OtherElementId;
            return id == null ? 0 : IdUtil.Value(id);
        }

        private static string Text(string cat, string name, long id, ClashResult lr, bool host)
        {
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(cat)) return Join(cat, name);
            if (lr != null)
                return host ? Join(lr.HostCategory, lr.HostElementName)
                            : Join(lr.OtherCategory, lr.OtherElementName);
            return id != 0 ? "(element " + id + ")" : "";
        }

        private static string Join(string cat, string name)
        {
            if (string.IsNullOrEmpty(cat)) return name ?? "";
            if (string.IsNullOrEmpty(name)) return cat;
            return cat + ": " + name;
        }
    }
}
