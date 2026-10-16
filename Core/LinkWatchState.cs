using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// The pure half of <see cref="LinkWatch"/>: the notice, its wording and age, the
    /// latest-wins state machine, and the instance-id → name map.
    ///
    /// SPLIT OUT so it can be asserted without Revit. Everything here is plain .NET —
    /// no Autodesk types, no Document — so it links into the headless logic project like
    /// <c>ClashStatusReplay</c> does. The Revit-touching half (<c>Observe</c>,
    /// <c>Reset</c>, name lookup) is in LinkWatch.cs and only ever calls the small,
    /// named transitions below, so what a real change MEANS to the notice is decided
    /// here and is testable, while what Revit reports stays there.
    ///
    /// Why it matters that this is tested: the feature is event-driven and stateful, and
    /// its failure mode is a warning that silently stops appearing — nothing crashes,
    /// nobody notices, and a link change that created new clashes goes unreported.
    /// </summary>
    public static partial class LinkWatch
    {
        public enum Kind { LoadedOrReloaded, Unloaded, InstanceAdded, InstanceMoved, InstanceDeleted }

        public class Notice
        {
            public Kind Kind;
            public string LinkName;
            public DateTime WhenUtc;

            /// <summary>The verb phrase for the panel sentence: "{LinkName} {VerbText} {AgeText}."</summary>
            public string VerbText
            {
                get
                {
                    switch (Kind)
                    {
                        case Kind.LoadedOrReloaded: return "link loaded or reloaded";
                        case Kind.Unloaded: return "link unloaded";
                        case Kind.InstanceAdded: return "link added";
                        case Kind.InstanceMoved: return "link moved";
                        case Kind.InstanceDeleted: return "link removed";
                        default: return "link changed";
                    }
                }
            }

            /// <summary>"3 min ago", same thresholds as RecordWindow's AgeText — kept as its
            /// own copy rather than shared, matching how every other "time ago" spot in this
            /// project already does its own (ClashRowVm.TimeText, RecordWindow.AgeText).</summary>
            public string AgeText { get { return AgeTextAt(DateTime.UtcNow); } }

            /// <summary>
            /// <see cref="AgeText"/> against a supplied clock, so the boundaries can be
            /// pinned exactly instead of by sleeping. A timestamp in the future (clock
            /// skew) reads "just now" rather than a negative age.
            /// </summary>
            internal string AgeTextAt(DateTime nowUtc)
            {
                var span = nowUtc - WhenUtc;
                if (span.TotalMinutes < 1) return "just now";
                if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
                if (span.TotalHours < 24) return (int)span.TotalHours + " hr ago";
                int days = (int)span.TotalDays;
                return days == 1 ? "1 day ago" : days + " days ago";
            }
        }

        private static Notice _current;
        private static Dictionary<long, string> _instanceNames = new Dictionary<long, string>();

        /// <summary>The active notice, or null when nothing has changed since the model was
        /// adopted, a full check ran, or the user dismissed it.</summary>
        public static Notice Current { get { return _current; } }

        /// <summary>Clears the notice without touching the name map — what Dismiss and a
        /// completed full check both want. A full check cannot race a change landing after
        /// it started: both run on Revit's single main thread, so nothing can be recorded
        /// while Execute is on the stack, and everything the notice could be reporting was
        /// necessarily recorded before the check began.</summary>
        public static void Clear()
        {
            _current = null;
            // Only the NOTICE goes. The "what changed" baseline stays: hiding a warning must not throw away the answer to what it
            // warned about, and a full check (the tracker's own end of the wait) needs it.
        }

        public static void RecordLoadedOrReloaded(string linkName) { Record(Kind.LoadedOrReloaded, linkName); }

        /// <summary>A link TYPE seen with an Unloaded status — see LinkWatch.Observe.</summary>
        internal static void RecordUnloaded(string linkName) { Record(Kind.Unloaded, linkName); }

        /// <summary>
        /// A link instance was added or moved: remember its name (so a later DELETE can
        /// still name it — the element is gone by then and cannot be asked) and record the
        /// change. Re-learning an id overwrites, so a renamed link type is picked up.
        /// </summary>
        internal static void NoteInstance(long id, string name, bool wasModified)
        {
            _instanceNames[id] = name;
            Record(wasModified ? Kind.InstanceMoved : Kind.InstanceAdded, name);
        }

        /// <summary>
        /// An element id was deleted. If it was a link instance we knew, record that under
        /// the name we learned and forget it; anything else is not a link and changes
        /// nothing. Returns whether a notice was recorded.
        /// </summary>
        internal static bool NoteDeleted(long id)
        {
            string name;
            if (!_instanceNames.TryGetValue(id, out name)) return false;   // not a link we knew about
            Record(Kind.InstanceDeleted, name);
            _instanceNames.Remove(id);
            return true;
        }

        /// <summary>
        /// Adopt a freshly built name map and clear any notice — a notice must never
        /// survive into a different model, including the burst of LinkedResourceOpened
        /// events Revit may raise for a model's own links while it opens.
        /// </summary>
        internal static void Adopt(Dictionary<long, string> names)
        {
            _current = null;
            ClashChangeTracker.Reset();             // what changed belongs to the model it changed in
            _instanceNames = names ?? new Dictionary<long, string>();
        }

        /// <summary>The name learned for an instance id, or null. For tests.</summary>
        internal static string KnownName(long id)
        {
            string name;
            return _instanceNames.TryGetValue(id, out name) ? name : null;
        }

        /// <summary>Everything mutable, so a test that runs inside a LIVE Revit session can
        /// put the real add-in's notice and name map back exactly as it found them.</summary>
        internal sealed class State
        {
            internal Notice Notice;
            internal Dictionary<long, string> Names;
            internal ClashChangeTracker.State Changes;   // recording a notice also starts a "what changed" wait
        }

        internal static State Snapshot()
        {
            return new State { Notice = _current, Names = new Dictionary<long, string>(_instanceNames), Changes = ClashChangeTracker.Snapshot() };
        }

        internal static void Restore(State s)
        {
            _current = s.Notice;
            _instanceNames = s.Names;
            ClashChangeTracker.Restore(s.Changes);
        }

        private static void Record(Kind kind, string linkName)
        {
            _current = new Notice
            {
                Kind = kind,
                LinkName = string.IsNullOrEmpty(linkName) ? "A link" : linkName,
                WhenUtc = DateTime.UtcNow
            };
            // The clash list as it stands NOW is the "before" that a later check is compared with.
            ClashChangeTracker.NoteLinkChange(kind, _current.LinkName);
        }

        /// <summary>Drop a trailing ".rvt" so "HVAC unloaded" and "HVAC loaded or reloaded"
        /// read the same way regardless of which event named the link — RevitLinkType.Name
        /// carries it (Snowdon's link TYPES are literally named "....rvt"), the loaded-event
        /// path (see LiveClashApp.OnLinkedResourceOpened) already strips it from a file path.
        /// Same trim LinkContext.TrimRvt does for the same reason, kept as its own copy —
        /// this class already keeps its own "ago" formatter rather than sharing one.</summary>
        internal static string TrimRvt(string name)
        {
            return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
        }
    }
}
