using System;

namespace LiveClash.Testing
{
    /// <summary>
    /// Every Revit-free assertion, in one list.
    ///
    /// THE ONE LIST, and that is the whole job. Both tiers run these — the headless CI
    /// gate on a machine with no Revit, and the in-Revit checks — and each used to name
    /// the blocks itself. They drifted: the in-Revit run never had AuditDropFormat, so a
    /// check could pass in CI and be flatly absent in Revit with nothing in either total
    /// to say so. Same failure as the filter keys having two readers, one level up.
    ///
    /// So: add a block here, and both tiers get it. Neither caller may name blocks again.
    /// </summary>
    public static class SharedChecks
    {
        public static void RunAll(Action<string, bool?> check)
        {
            FilterLogicChecks.RunAll(check);
            FilterMatchChecks.RunAll(check);
            StatusReplayChecks.RunAll(check);
            ResolvedReplayChecks.RunAll(check);
            LinkWatchChecks.RunAll(check);
            ClashSearchChecks.RunAll(check);
            ClashChangeChecks.RunAll(check);
            ModelIdentityChecks.RunAll(check);
            CsvStampChecks.RunAll(check);
            ResultFormatChecks.RunAll(check);
            LogChecks.RunAll(check);
            FuzzChecks.RunAll(check);
            EdgeCaseChecks.RunAll(check);
            ReviewChecksLivePathShared.RunAll(check);
            FullCheckReviewLogicChecks.RunAll(check);   // review 2026-10-31, F2 (M2, M10, LOW-8)
            LedgerReviewChecks.RunAll(check);
            LedgerReviewR2Checks.RunAll(check);
            LedgerReviewR3Checks.RunAll(check);
            LedgerReviewR4Checks.RunAll(check);
            LedgerReviewR8FilesChecks.RunAll(check);   // round 8, files builder: D3-4, D3-2 b/c, SP7, DL-f
            UiOutcomeChecks.RunAll(check);
            SessionHandlerLogicChecks.RunAll(check);   // second pass 2026-10-31, SP1
            MarkerIdentityChecks.RunAll(check);        // clash schedule slice 1: clash id, issue numbers
            MarkerRecordChecks.RunAll(check);          // clash schedule slice 1: the record, its formats
            MarkerPlanChecks.RunAll(check);            // clash schedule slice 2: the Refresh plan
            MarkerPendingMapChecks.RunAll(check);      // clash schedule slice 3g: ledger key to triple, closing pending
            MarkerAssemblerChecks.RunAll(check);       // clash schedule: the plan assembler and the lines after Apply
            MarkerReviewAChecks.RunAll(check);         // clash schedule review fix A: merged pending entry, summary notes
            MarkerDeltaL4Checks.RunAll(check);         // clash schedule delta review D-L4: a Seq for each pending field
            MarkerMutationPlanChecks.RunAll(check);    // clash schedule mutation batch 1: the plan's ordering and tie-break
            MarkerMutationPlan2Checks.RunAll(check);   // clash schedule mutation batch 2: one marker's fields, reasons and counts
            MarkerMutationPlan3Checks.RunAll(check);   // clash schedule mutation batch 5: which of two rows for one pair the plan uses
            MarkerMutationLedgerChecks.RunAll(check);   // clash schedule mutation batch 6: the ledger reader (widths, latest of several, split, order)
            MarkerMutationPlan4Checks.RunAll(check);   // clash schedule mutation batch 7: the planner details (defaults, repairs, pending, merge, counts)
            MarkerMutationIdChecks.RunAll(check);      // clash schedule mutation batch 8: issue-number prefixes, renumbering and pair identity text
            MarkerMutationModelChecks.RunAll(check);   // clash schedule mutation batch 9: dialog models, record reader and writer, reconcile and the small files
            MarkerMutationFlowChecks.RunAll(check);    // clash schedule mutation batch 10: the controller at its edges
            MarkerMutationFinishChecks.RunAll(check);  // clash schedule mutation batch 11: the survivors of the third Stryker run that a true check can kill
            MarkerMutationOrderChecks.RunAll(check);   // clash schedule mutation batch 11d (A): the order texts tell different field lists apart
            MarkerMutationReviewChecks.RunAll(check);  // clash schedule mutation batch 11d (B, C, D): the cases the survivors reader found unchecked
            MarkerDr3Checks.RunAll(check);             // delta review 3: unmatched pending keys, duplicate new numbers, the last number, a filtered self-test run
            MarkerMutationRowChecks.RunAll(check);  // clash schedule mutation batch 11e: the nine survivors the injective order text left
            MarkerMutationSmallChecks.RunAll(check);   // clash schedule mutation batch 3: menu, undo names, fingerprint, pending join, ledger text and order
            MarkerMutationAssemblerChecks.RunAll(check);   // clash schedule mutation batch 4: the plan assembler's guards, orders and defaults
            MarkerRowChecks.RunAll(check);             // clash schedule slice 3d: the row builder
            MarkerDedupeChecks.RunAll(check);          // clash schedule slice 3b/3c: also caught by, marker identity
            MarkerLedgerChecks.RunAll(check);          // clash schedule slice 3a: new ledger events, reconcile
            MarkerCoverChecks.RunAll(check);           // clash schedule slice 2: branch top-up for slice 1
            MarkerTextChecks.RunAll(check);            // clash schedule slice 1: group label, summary text
            ScheduleMenuChecks.RunAll(check);          // clash schedule UI B1: the split button and its menu
            ScheduleRowChecks.RunAll(check);           // clash schedule UI B1: row columns, status suffix, search text
            SchedulePlanChecks.RunAll(check);          // clash schedule UI B2: the Refresh plan dialog
            ScheduleConfirmChecks.RunAll(check);       // clash schedule UI B2: the unticked confirmations
            ScheduleAssignChecks.RunAll(check);        // clash schedule UI B2: the Assign dialog
            ScheduleFlowChecks.RunAll(check);          // clash schedule UI B3: the controller's flows
            SyncContextChecks.RunAll(check);           // those flows must not wait on the thread that runs them (Revit self-test, 2026-11-01)
            LevelTableChecks.RunAll(check);            // clash schedule at scale: the levels read once give the old per-clash answer
            SchedulePlanFactoryChecks.RunAll(check);   // clash schedule UI B5: the plan to dialog view adapter
            ScheduleReviewChecks.RunAll(check);        // independent review M2: the counts copy and what the summary says
            R8LiveChecks.RunAll(check);   // round 8, builder "live" (tests\evidence\liveclash\r8-live)
            GroupLedgerChecks.RunAll(check);   // the overnight ModificationForbiddenException: groups a check leaves open
            MarkerRevitLogicChecks.RunAll(check);     // clash schedule slice 4a: parameter map, refusals, ownership, write filter
            FilterFreezeLogicChecks.RunAll(check);   // U1: the work pump, the sliced pass, the signed cache, what the Filter window shows while it fills
        }
    }
}
