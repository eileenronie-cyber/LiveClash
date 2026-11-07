using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>What this seat's re-test said about one pair.</summary>
    public enum PlanVerdict
    {
        Clashing,
        NoLongerClashes,
        NotRetested
    }

    /// <summary>Whether the marker element may be written by this user, decided before the plan (no transaction).</summary>
    public enum MarkerAccess
    {
        Free,
        HeldByOther,
        ChangedInCentral,
        InsideGroup,
        NewerFormat,
        Unreadable
    }

    public enum PendingKind
    {
        Applied,
        Dropped,
        Kept
    }

    /// <summary>
    /// The Refresh plan (spec 6.2): what to create, update, resolve, merge, pass over and remove, worked out
    /// from this seat's re-test rows and the markers already in the model. Pure, no Revit, no clock (the
    /// day comes from <see cref="Options"/>). It writes only where a recorded value differs (H1), resolves
    /// only from a re-test under the marker's own rule (E3), matches by UniqueId triple, and never plans a
    /// write to a marker the user cannot borrow.
    /// </summary>
    public static partial class MarkerPlan
    {
        public const string NoSetName = "(no set)";
        public const double PointTolerance = 0.001;

        /// <summary>A decision the ledger already holds for a pair, used to start a new marker with it.</summary>
        public sealed class Seed
        {
            public MarkerStatus Status { get; set; }
            public string Note { get; set; }
            public string AssignedTo { get; set; }
            public string By { get; set; }
            public DateTimeOffset? At { get; set; }
        }

        /// <summary>One pair as this seat sees it. Fields the seat does not know stay null or empty and are never written.</summary>
        public sealed class Row
        {
            public string Key { get; set; }
            public string HostUid { get; set; }
            public string LinkUid { get; set; }
            public string OtherUid { get; set; }
            public long HostId { get; set; }
            public long LinkId { get; set; }
            public long OtherId { get; set; }
            /// <summary>False for a decided pair that is not live on this seat.</summary>
            public bool Live { get; set; } = true;
            public PlanVerdict Verdict { get; set; }
            public string NotRetestedReason { get; set; }
            /// <summary>The rule the pair was re-tested under; must equal the marker's recorded rule to count.</summary>
            public double RuleClearanceMm { get; set; }
            public bool RuleInsulated { get; set; }
            public string Severity { get; set; }
            public bool HasPoint { get; set; } = true;
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
            public string SetName { get; set; }
            /// <summary>Null means not computed.</summary>
            public List<string> AlsoSets { get; set; }
            /// <summary>Null means not computed; empty means a pair alone.</summary>
            public string GroupLabel { get; set; }
            /// <summary>Rows with one non-empty key are one proximity group and the plan labels them from the issue numbers (new markers included); empty is a pair alone; null means the group is not known and <see cref="GroupLabel"/> is used as it is.</summary>
            public string GroupKey { get; set; }
            /// <summary>A hash of the values this row would put in the marker's parameters.</summary>
            public string ShownHash { get; set; }
            /// <summary>The display values (names, categories, level, model) the Revit side worked out for this row; copied onto what the plan writes.</summary>
            public Dictionary<string, string> Shown { get; set; }
            public Seed Seed { get; set; }
            public string RememberedIssueNo { get; set; }
        }

        public sealed class Existing
        {
            public string MarkerUid { get; set; }
            public MarkerRecord Record { get; set; }
            public MarkerAccess Access { get; set; }
            public string HeldBy { get; set; }
            /// <summary>A hash of the values the marker's parameters hold now.</summary>
            public string ShownHash { get; set; }
            /// <summary>The element is not at its recorded point.</summary>
            public bool OffPlace { get; set; }
            public bool WrongWorkset { get; set; }
        }

        /// <summary>A triage the ledger holds for a marker that could not be borrowed when it was made.</summary>
        public sealed class Pending
        {
            public string Triple { get; set; }
            public long SeqAtPending { get; set; }
            /// <summary>The marker Seq each field was set at; null means <see cref="SeqAtPending"/>.</summary>
            public long? StatusSeq { get; set; }
            public long? NoteSeq { get; set; }
            public long? AssignedSeq { get; set; }
            public MarkerStatus? Status { get; set; }
            public string Note { get; set; }
            public string AssignedTo { get; set; }
            public string By { get; set; }
        }

        public sealed class Options
        {
            public DateTimeOffset Today { get; set; }
            public string UserName { get; set; }
            public bool RemoveRetention { get; set; }
            public int Cap { get; set; } = 2000;
            public int WarnAt { get; set; } = 5000;
            public int RetentionDays { get; set; } = 30;
            public int DialogCreateThreshold { get; set; } = 50;
            /// <summary>The parameters, workset or schedule have to be added to the model first.</summary>
            public bool SetupNeeded { get; set; }
            public List<string> DisabledSets { get; set; } = new List<string>();
            /// <summary>The hash behind the clash id; a test forces a collision with it.</summary>
            public Func<string, string> Hash { get; set; }
        }

        public sealed class Input
        {
            public List<Row> Rows { get; set; } = new List<Row>();
            public List<Existing> Markers { get; set; } = new List<Existing>();
            public List<Pending> Pending { get; set; } = new List<Pending>();
            public IssueNumbers.Counter Counter { get; set; }
            /// <summary>Every counter record in the model, the refreshing user's own included. When two share a prefix (two users made their first markers before either synchronized) the plan repairs the refreshing user's own side. Null or empty: no repair.</summary>
            public List<IssueNumbers.Counter> AllCounters { get; set; } = new List<IssueNumbers.Counter>();
            public Options Options { get; set; } = new Options();
        }

        public sealed class Creation
        {
            public string Triple { get; set; }
            public MarkerRecord Record { get; set; }
            public string ShownHash { get; set; }
            /// <summary>A copy of the row's display values, or null when there was no row (the display columns are then left as they are).</summary>
            public Dictionary<string, string> Shown { get; set; }
            /// <summary>1 New, 2 Pending, 3 Reopened, 4 Coordinated, 5 Ignored, 6 Resolved: the hidden "LC Status Order" parameter the schedule sorts by.</summary>
            public int StatusOrder { get; set; }
            /// <summary>The number the ledger remembered was taken, so a new one was issued.</summary>
            public bool IssueNoReplaced { get; set; }
        }

        public sealed class Change
        {
            public string MarkerUid { get; set; }
            public MarkerRecord Before { get; set; }
            public MarkerRecord After { get; set; }
            /// <summary>The reasons, ordinal order: record field names, plus Parameters, Location, Workset.</summary>
            public List<string> Fields { get; set; } = new List<string>();
            public bool Resolved { get; set; }
            public bool Reopened { get; set; }
            public bool Moved { get; set; }
            public bool PutBack { get; set; }
            public bool WorksetFix { get; set; }
            public bool Rekeyed { get; set; }
            public string ShownHash { get; set; }
            /// <summary>A copy of the row's display values, or null when there was no row (the display columns are then left as they are).</summary>
            public Dictionary<string, string> Shown { get; set; }
            /// <summary>1 New, 2 Pending, 3 Reopened, 4 Coordinated, 5 Ignored, 6 Resolved: the hidden "LC Status Order" parameter the schedule sorts by.</summary>
            public int StatusOrder { get; set; }
        }

        public sealed class Removal
        {
            public string MarkerUid { get; set; }
            /// <summary>"merged duplicate" or "retention".</summary>
            public string Reason { get; set; }
            public string KeptUid { get; set; }
        }

        public sealed class Reasoned
        {
            public string MarkerUid { get; set; }
            public string Key { get; set; }
            public string Reason { get; set; }
        }

        public sealed class PendingOutcome
        {
            public string Triple { get; set; }
            public string MarkerUid { get; set; }
            public PendingKind Kind { get; set; }
            public string Message { get; set; }
            /// <summary>An Applied triage that was only partly applied: the fields (status, note, assignee) left out because the marker had changed since they were set.</summary>
            public List<string> NotApplied { get; set; } = new List<string>();
        }

        public sealed class LostDecision
        {
            public string RemovedUid { get; set; }
            public string RemovedIssueNo { get; set; }
            public string KeptUid { get; set; }
            public string KeptIssueNo { get; set; }
            public MarkerStatus Status { get; set; }
            public string Note { get; set; }
            public string AssignedTo { get; set; }
            public string By { get; set; }
        }

        public sealed class SetCount
        {
            public string Name { get; set; }
            public int InPanel { get; set; }
            public int UpToDate { get; set; }
            public int NotLive { get; set; }
            public bool SetOff { get; set; }
            public int NotRetested { get; set; }
        }

        public sealed class Result
        {
            public List<Creation> Creations { get; set; } = new List<Creation>();
            public List<Change> Changes { get; set; } = new List<Change>();
            public List<Removal> Removals { get; set; } = new List<Removal>();
            public List<Removal> RetentionOffers { get; set; } = new List<Removal>();
            public List<Reasoned> PassedOver { get; set; } = new List<Reasoned>();
            public List<Reasoned> NotRetested { get; set; } = new List<Reasoned>();
            public List<Reasoned> NotMarked { get; set; } = new List<Reasoned>();
            public List<PendingOutcome> Pending { get; set; } = new List<PendingOutcome>();
            public List<LostDecision> LostDecisions { get; set; } = new List<LostDecision>();
            public List<SetCount> SetCounts { get; set; } = new List<SetCount>();
            public int Unchanged { get; set; }
            public int Merges { get; set; }
            public int ExistingMarkers { get; set; }
            public bool NeedsDialog { get; set; }
            public bool OverCap { get; set; }
            public bool OverWarn { get; set; }
            public IssueNumbers.Counter CounterAfter { get; set; }
            /// <summary>Set when the refreshing user's counter lost a prefix collision and moved to another prefix this Refresh.</summary>
            public IssueNumbers.PrefixMove PrefixMove { get; set; }

            /// <summary>The summary counts, worked out from the plan (the Revit side re-reads them from the model after the commit).</summary>
            public MarkerSummary.Counts ToCounts()
            {
                return Planner.CountsOf(this);
            }
        }

        /// <summary>Works out the plan. Never throws: null and empty inputs give an empty plan.</summary>
        public static Result Build(Input input)
        {
            return new Planner(input).Run();
        }
    }
}
