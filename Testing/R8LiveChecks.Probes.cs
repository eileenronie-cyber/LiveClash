using System;
using System.Collections.Generic;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>Round 8, builder "live", item 9: the Revit-free probes. A probe asserts the CORRECT behaviour; a FAIL means the second-pass suspicion is reproduced.</summary>
    public static partial class R8LiveChecks
    {
        private static ClashResult Row(string key, string model)
        {
            return new ClashResult { Key = key, ModelName = model, Severity = ClashSeverity.Hard, HostElementName = "Pipe " + key, OtherElementName = "Wall " + key };
        }

        /// <summary>
        /// P9-2. Second pass: "ClashChangeTracker mislabels a deleted link". Two links change before one full check: the Architecture link INSTANCE
        /// is DELETED (the full check says "dropped: their link was removed") and the Structure link is reloaded. The summary then reports the
        /// deleted link's rows as "not checked (Architecture not loaded)", which is not what happened to it: it is not unloaded, it is gone. The probe
        /// asserts the headline does not call a deleted link "not loaded".
        /// </summary>
        private static void TrackerNamesADeletedLinkAsRemoved(Action<string, bool?> check)
        {
            var saved = LinkWatch.Snapshot();
            var savedSource = ClashChangeTracker.SnapshotSource;
            try
            {
                ClashChangeTracker.Restore(null);
                var arch = Row("p92-a", "Architecture");
                var stru = Row("p92-s", "Structure");
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult> { arch, stru };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.InstanceDeleted, "Architecture");
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure");

                // The full check afterwards: the deleted link's pair was dropped (its instance is gone), Structure's is still listed.
                ClashChangeTracker.Complete(new List<ClashResult> { Row("p92-s", "Structure") }, true);
                var s = ClashChangeTracker.Current;
                string head = s == null ? "(no summary)" : s.Headline;
                check(P + "P9-2 [probe] a mixed summary does not call a DELETED link instance \"not loaded\" (headline: " + head + ")",
                    s != null && !head.Contains("Architecture not loaded") && !head.Contains("Architecture) not loaded"));
                check(P + "P9-2b [green] ...it says what the full check says, with the link named: 1 pair(s) dropped: their link was removed from the model (Architecture) (headline: " + head + ")",
                    s != null && head.Contains("1 pair(s) dropped: their link was removed from the model (Architecture)") && s.NotChecked.Count == 1 && s.Set.Cleared.Count == 0);

                // The control: an UNLOAD in the same mix is still "not loaded". Its row stays listed as Unverified after the full check.
                ClashChangeTracker.Restore(null);
                ClashChangeTracker.SnapshotSource = () => new List<ClashResult> { arch, stru };
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.Unloaded, "Architecture");
                ClashChangeTracker.NoteLinkChange(LinkWatch.Kind.LoadedOrReloaded, "Structure");
                var archUnverified = Row("p92-a", "Architecture");
                archUnverified.Severity = ClashSeverity.Unverified;
                ClashChangeTracker.Complete(new List<ClashResult> { archUnverified, Row("p92-s", "Structure") }, true);
                string head2 = ClashChangeTracker.Current == null ? "(no summary)" : ClashChangeTracker.Current.Headline;
                check(P + "P9-2c [green] control: an unloaded link in the same mix still reads \"not loaded\", not \"removed\" (headline: " + head2 + ")",
                    head2.Contains("1 not checked (Architecture not loaded)") && !head2.Contains("removed"));
            }
            finally
            {
                ClashChangeTracker.SnapshotSource = savedSource;
                ClashChangeTracker.Restore(null);
                LinkWatch.Restore(saved);
            }
        }
    }
}
