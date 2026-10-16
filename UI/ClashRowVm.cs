using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;
using LiveClash.Core;

// Autodesk.Revit.DB also defines Color; the panel means the WPF one.
using Color = System.Windows.Media.Color;

namespace LiveClash.UI
{
    /// <summary>
    /// One row in the results list — either a grouped issue, or one individual clash
    /// pair inside it.
    ///
    /// Deliberately a FLAT list of both kinds rather than a TreeView: a TreeView only
    /// supports single selection, and selecting several issues at once is how status
    /// gets marked in practice. A flat list with an expander column keeps
    /// multi-select and still lets a group be opened to reach its individual pairs.
    /// </summary>
    public class ClashRowVm
    {
        public ClashGroup Group { get; private set; }

        /// <summary>The individual pair, for a child row. Null on a group row.</summary>
        public ClashResult Member { get; private set; }

        public bool IsPair { get { return Member != null; } }

        /// <summary>
        /// True for a row that has just stopped clashing. Shown struck through for a
        /// few seconds so you see the fix land, then dropped — a row that simply
        /// vanishes leaves you wondering whether you fixed it or lost it.
        /// </summary>
        public bool IsResolved { get; private set; }

        /// <summary>Group rows only. Drives the expander glyph and the child rows.</summary>
        public bool IsExpanded { get; set; }

        public ClashRowVm(ClashGroup g) : this(g, false) { }

        public ClashRowVm(ClashGroup g, bool resolved)
        {
            Group = g;
            IsResolved = resolved;
        }

        /// <summary>A child row for one pair inside <paramref name="g"/>.</summary>
        public ClashRowVm(ClashGroup g, ClashResult member, bool resolved)
        {
            Group = g;
            Member = member;
            IsResolved = resolved;
        }

        // ------------------------------------------------------------- expanding

        public bool CanExpand { get { return !IsPair && Group.Count > 1; } }

        public string ExpanderText
        {
            get { return CanExpand ? (IsExpanded ? "▾" : "▸") : ""; }
        }

        /// <summary>
        /// Identifies this row across a refresh, so selection and scroll can be put
        /// back after the list is rebuilt. Group rows key on their first member,
        /// which is stable for as long as the group holds together.
        /// </summary>
        public string RowKey
        {
            get
            {
                if (IsPair) return (IsResolved ? "RP:" : "P:") + Member.Key;
                var first = Group.First;
                return (IsResolved ? "RG:" : "G:") + (first == null ? "" : first.Key);
            }
        }

        /// <summary>Child rows are indented so the hierarchy reads at a glance.</summary>
        public Thickness Indent { get { return new Thickness(IsPair ? 18 : 0, 0, 0, 0); } }

        // ------------------------------------------------------------- status

        public ClashStatusKind Status
        {
            get
            {
                return IsPair
                    ? ClashStatusStore.Instance.Get(Member.Key)
                    : Group.Status;
            }
        }

        /// <summary>The keys a status decision applies to for this row.</summary>
        public List<string> Keys()
        {
            if (IsPair)
            {
                var one = new List<string>();
                if (!string.IsNullOrEmpty(Member.Key)) one.Add(Member.Key);
                return one;
            }
            return Group.Keys();
        }

        /// <summary>A decision that was fixed and has come back. Never hidden.</summary>
        public bool IsReopened
        {
            get
            {
                if (IsResolved) return false;
                if (IsPair) return ClashStatusStore.Instance.IsReopened(Member.Key);
                foreach (var k in Group.Keys()) if (ClashStatusStore.Instance.IsReopened(k)) return true;
                return false;
            }
        }

        public string StatusText
        {
            get
            {
                if (IsResolved) return "Resolved";
                if (IsReopened) return "Reopened";
                return Status.ToString();
            }
        }

        /// <summary>Why the decision was made. Shown in its own column.</summary>
        public string NoteText
        {
            get
            {
                if (IsPair) return ClashStatusStore.Instance.NoteOf(Member.Key) ?? "";
                foreach (var k in Group.Keys())
                {
                    var n = ClashStatusStore.Instance.NoteOf(k);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
                return "";
            }
        }

        public Brush StatusBrush
        {
            get
            {
                if (IsResolved) return ResolvedBrush;
                if (IsReopened) return ReopenedBrush;
                switch (Status)
                {
                    case ClashStatusKind.Pending: return PendingBrush;
                    case ClashStatusKind.Coordinated: return CoordinatedBrush;
                    case ClashStatusKind.Ignored: return IgnoredBrush;
                    default: return ThemeManager.IsDark ? NewBrushDark : NewBrush;
                }
            }
        }

        public TextDecorationCollection Decorations
        {
            get { return IsResolved ? TextDecorations.Strikethrough : null; }
        }

        // ------------------------------------------------------------- text

        /// <summary>Blank on a single clash and on child rows; "×3" on a real group.</summary>
        public string CountText
        {
            get { return (!IsPair && Group.Count > 1) ? "×" + Group.Count : ""; }
        }

        public string HostText
        {
            get
            {
                if (IsPair) return Member.HostElementName;
                return Group.First == null ? "" : Group.HostLabel;
            }
        }

        public string OtherText
        {
            get
            {
                if (IsPair) return Member.OtherElementName;
                return Group.First == null ? "" : Group.OtherLabel;
            }
        }

        public string ModelName
        {
            get { return IsPair ? Member.ModelName : Group.ModelName; }
        }

        /// <summary>
        /// Which clash set caught this row, for the Set column. A group can in principle
        /// span sets, so its distinct member set names are joined — but grouping is by
        /// proximity within one scope, so that is nearly always a single name.
        /// </summary>
        public string SetText
        {
            get
            {
                if (IsPair) return Member.SetName ?? "";
                if (Group == null) return "";
                var names = new List<string>();
                foreach (var m in Group.Members)
                    if (!string.IsNullOrEmpty(m.SetName) && !names.Contains(m.SetName)) names.Add(m.SetName);
                return string.Join(", ", names.ToArray());
            }
        }

        public string TimeText
        {
            get
            {
                var r = IsPair ? Member : Group.First;
                return r == null ? "" : r.DetectedUtc.ToLocalTime().ToString("HH:mm:ss");
            }
        }

        public string SeverityText
        {
            get
            {
                if (IsPair) return Text(Member.Severity);
                if (Group.Count == 1) return Text(Group.Members[0].Severity);
                var bits = new List<string>();
                if (Group.HardCount > 0) bits.Add(Group.HardCount + " hard");
                if (Group.UnverifiedCount > 0) bits.Add(Group.UnverifiedCount + " unverified");
                if (Group.ClearanceCount > 0) bits.Add(Group.ClearanceCount + " clearance");
                return string.Join(", ", bits.ToArray());
            }
        }

        private static string Text(ClashSeverity s)
        {
            switch (s)
            {
                case ClashSeverity.Hard: return "Hard clash";
                case ClashSeverity.Unverified: return "Unverified - check by hand";
                default: return "Clearance";
            }
        }

        // Frozen and shared: WPF re-reads these on every render, so allocating a
        // brush per read churned the heap for no reason.
        private static readonly Brush ResolvedBrush = Frozen(0x2A, 0x9D, 0x5C);
        private static readonly Brush ReopenedBrush = Frozen(0xD1, 0x3B, 0x3B);
        private static readonly Brush PendingBrush = Frozen(0xC8, 0x7A, 0x00);
        private static readonly Brush CoordinatedBrush = Frozen(0x2A, 0x7A, 0xC8);
        private static readonly Brush IgnoredBrush = Frozen(0x99, 0x99, 0x99);
        private static readonly Brush NewBrush = Frozen(0x44, 0x44, 0x44);
        private static readonly Brush NewBrushDark = Frozen(0xC9, 0xCE, 0xD8);   // #444 on a dark row cannot be read
        private static readonly Brush HardBrush = Frozen(0xD1, 0x3B, 0x3B);       // red
        private static readonly Brush ClearanceBrush = Frozen(0xE8, 0xA0, 0x21);  // amber
        private static readonly Brush UnverifiedBrush = Frozen(0x8A, 0x4F, 0xC8); // purple

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// How the pair meets, in the words a coordinator would use. Blank when the
        /// test does not apply — pipe against pipe has no "through" or "ends in".
        /// </summary>
        public string KindText
        {
            get
            {
                var k = Member != null ? Member.GeometryKind : DominantKind();
                switch (k)
                {
                    case ClashGeometryKind.Termination: return "ends in";
                    case ClashGeometryKind.Through:     return "through";
                    case ClashGeometryKind.Enclosed:    return "inside";
                    case ClashGeometryKind.EndsInside:  return "ends in*";
                    default:                            return "";
                }
            }
        }

        public string KindTip
        {
            get
            {
                switch (Member != null ? Member.GeometryKind : DominantKind())
                {
                    case ClashGeometryKind.Termination:
                        return "The service ENDS inside the other element — usually a connection "
                             + "modelled without a connector, not a routing fault.";
                    case ClashGeometryKind.Through:
                        return "The service crosses clean through and comes out the other side — "
                             + "a designed penetration, or a genuine reroute.";
                    case ClashGeometryKind.Enclosed:
                        return "The service lies entirely inside the other element.";
                    case ClashGeometryKind.EndsInside:
                        return "The service ends inside something it could never connect to "
                             + "(a duct inside electrical gear, say). Geometrically the same as "
                             + "a termination, but there is no connection to be missing — it is a clash.";
                    default:
                        return "Neither side is a routed service, so this test does not apply.";
                }
            }
        }

        private ClashGeometryKind DominantKind()
        {
            if (Group == null || Group.Members.Count == 0) return ClashGeometryKind.Unknown;
            var tally = new Dictionary<ClashGeometryKind, int>();
            foreach (var m in Group.Members)
            {
                int c;
                tally[m.GeometryKind] = tally.TryGetValue(m.GeometryKind, out c) ? c + 1 : 1;
            }
            var best = ClashGeometryKind.Unknown;
            int bestN = -1;
            foreach (var kv in tally)
            {
                if (kv.Key == ClashGeometryKind.Unknown && tally.Count > 1) continue;
                if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
            }
            return best;
        }

        /// <summary>Either element's type is flagged by the audit as a stand-in.</summary>
        public bool IsProvisional
        {
            get
            {
                if (Member != null) return Member.Provisional;
                if (Group == null) return false;
                foreach (var m in Group.Members) if (m.Provisional) return true;
                return false;
            }
        }

        /// <summary>
        /// Is this row one of the clashes a link change made (in the current "what changed" summary)? A group is new when
        /// any pair in it is. A fixed-and-fading row never is.
        /// </summary>
        public bool IsNewSinceChange
        {
            get
            {
                if (IsResolved) return false;
                if (IsPair) return ClashChangeTracker.IsNew(Member.Key);
                if (Group == null) return false;
                foreach (var m in Group.Members) if (ClashChangeTracker.IsNew(m.Key)) return true;
                return false;
            }
        }

        public string NewText { get { return IsNewSinceChange ? "NEW" : ""; } }

        public string NewTip
        {
            get
            {
                var s = ClashChangeTracker.Current;
                return IsNewSinceChange ? "New since " + (s == null ? "the link" : s.LinkText) + " changed" : "";
            }
        }

        public string ProvisionalText { get { return IsProvisional ? "△" : ""; } }

        public string ProvisionalTip
        {
            get
            {
                return IsProvisional
                    ? "One side is a PLACEHOLDER family — the clash is real, but its footprint "
                    + "will change when the actual equipment arrives, so the route around it is untested."
                    : "";
            }
        }

        /// <summary>The bare pipe/duct does not reach the other element - insulation does (its
        /// own, or that of one it meets). See ClashResult.Insulated.</summary>
        public bool IsInsulated
        {
            get
            {
                if (Member != null) return Member.Insulated;
                if (Group == null) return false;
                foreach (var m in Group.Members) if (m.Insulated) return true;
                return false;
            }
        }

        public string InsulatedText { get { return IsInsulated ? "◌" : ""; } }

        public string InsulatedTip
        {
            get
            {
                return IsInsulated
                    ? "The bare pipe or duct clears this — INSULATION is what actually clashes: the insulation of the pipe or duct itself, or of one it meets - in this model or in a link."
                    : "";
            }
        }

        public Brush SeverityBrush
        {
            get
            {
                var sev = IsPair ? Member.Severity
                        : Group.AnyHard ? ClashSeverity.Hard
                        : Group.UnverifiedCount > 0 ? ClashSeverity.Unverified
                        : ClashSeverity.Clearance;
                switch (sev)
                {
                    case ClashSeverity.Hard: return HardBrush;
                    case ClashSeverity.Unverified: return UnverifiedBrush;
                    default: return ClearanceBrush;
                }
            }
        }

        // ------------------------------------------------------------- selection

        /// <summary>
        /// The host-document elements this row stands for. A child row selects just
        /// its own two elements; a group row selects the whole issue, so one
        /// Selection Box frames all of it.
        /// </summary>
        public List<ElementId> SelectionIds()
        {
            if (!IsPair)
            {
                var ids = Group.HostElementIds();
                ids.AddRange(Group.OtherHostDocIds());
                return ids;
            }

            var pair = new List<ElementId>();
            if (Member.HostElementId != null) pair.Add(Member.HostElementId);
            // A linked element cannot be selected from the host document.
            if (Member.LinkInstanceId == null && Member.OtherElementId != null)
                pair.Add(Member.OtherElementId);
            return pair;
        }

        /// <summary>
        /// The clash(es) this row stands for, for framing in 3D: one pair for a child row,
        /// the whole issue for a group row (so a section box wraps the entire cluster).
        /// Unlike <see cref="SelectionIds"/> these carry the link side too — the focus view
        /// frames it geometrically, without needing to select it.
        /// </summary>
        public List<ClashResult> FocusTargets()
        {
            if (IsPair) return Member != null ? new List<ClashResult> { Member } : new List<ClashResult>();
            return Group != null ? new List<ClashResult>(Group.Members) : new List<ClashResult>();
        }

        /// <summary>True when the other side is in a link and cannot be selected.</summary>
        public bool OtherIsLinked
        {
            get
            {
                if (IsPair) return Member.LinkInstanceId != null;
                return Group.First != null && Group.First.LinkInstanceId != null;
            }
        }
    }
}
