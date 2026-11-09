using System;
using Nexus.Editor.Tests;

namespace NexusBench
{
    public static class FrameworkClosureSuite
    {
        public static int Run()
        {
            int failures = 0;
            var netcode = new NetcodeReplayRegressionTests();
            Check(nameof(netcode.Rollback_RecomputesNestedPayload_AfterLateInput),
                netcode.Rollback_RecomputesNestedPayload_AfterLateInput);
            Check(nameof(netcode.Rollback_RebuildsChangedBranch_AndDropsOldDescendants),
                netcode.Rollback_RebuildsChangedBranch_AndDropsOldDescendants);
            Check(nameof(netcode.Rollback_NewBranch_CanGenerateAdditionalChildren),
                netcode.Rollback_NewBranch_CanGenerateAdditionalChildren);
            Check(nameof(netcode.Rollback_RegeneratesFuturePayload_InItsOriginalOrder),
                netcode.Rollback_RegeneratesFuturePayload_InItsOriginalOrder);
            Check(nameof(netcode.PartialRollback_KeepsPendingSchedule_FromAnEarlierPrunedParent),
                netcode.PartialRollback_KeepsPendingSchedule_FromAnEarlierPrunedParent);
            Check(nameof(netcode.Rollback_ChangedFutureTick_DoesNotExecuteTheOldSchedule),
                netcode.Rollback_ChangedFutureTick_DoesNotExecuteTheOldSchedule);
            Check(nameof(netcode.CorrectedNestedRollback_AfterWarmup_DoesNotAllocate),
                netcode.CorrectedNestedRollback_AfterWarmup_DoesNotAllocate);
            var pools = new ObjectPoolReentrancyTests();
            Check(nameof(pools.ClearPool_ReentrantSpawn_PreservesReplacementOwnership),
                pools.ClearPool_ReentrantSpawn_PreservesReplacementOwnership);
            Check(nameof(pools.ClearAll_ReentrantSpawn_PreservesReplacementOwnership),
                pools.ClearAll_ReentrantSpawn_PreservesReplacementOwnership);
            Check(nameof(pools.ClearAll_ReentrantDespawnAndClear_ExitsEachLeaseOnce),
                pools.ClearAll_ReentrantDespawnAndClear_ExitsEachLeaseOnce);
            Check(nameof(pools.Despawn_ReentrantClearOrDispose_DoesNotReturnToDestroyedRoot),
                pools.Despawn_ReentrantClearOrDispose_DoesNotReturnToDestroyedRoot);
            Check(nameof(pools.Dispose_RejectsCallbackAcquisition_AndCompletesCleanup),
                pools.Dispose_RejectsCallbackAcquisition_AndCompletesCleanup);
            Check(nameof(pools.ClearAll_CallbackCreatesAnotherPrefabPool_LeavesItOwned),
                pools.ClearAll_CallbackCreatesAnotherPrefabPool_LeavesItOwned);
            Check(nameof(pools.ClearAll_CrossPoolCallbacks_PreserveBothNewLeasesRegardlessOfIterationOrder),
                pools.ClearAll_CrossPoolCallbacks_PreserveBothNewLeasesRegardlessOfIterationOrder);
            Check(nameof(pools.Spawn_DespawnedDuringOnSpawned_DoesNotReturnAnInactiveLease),
                pools.Spawn_DespawnedDuringOnSpawned_DoesNotReturnAnInactiveLease);
            Check(nameof(netcode.History_NestedReplayAfterClearOrPrune_KeepsOuterSnapshot),
                netcode.History_NestedReplayAfterClearOrPrune_KeepsOuterSnapshot);
            Check(nameof(netcode.History_ThreeReplayDepths_KeepIndependentGrowingBuffers),
                netcode.History_ThreeReplayDepths_KeepIndependentGrowingBuffers);
            Check(nameof(netcode.History_FailedNestedReplay_ReleasesItsBuffer_AndKeepsOuterSnapshot),
                netcode.History_FailedNestedReplay_ReleasesItsBuffer_AndKeepsOuterSnapshot);
            Check(nameof(netcode.History_NestedReplay_AfterWarmup_DoesNotAllocate),
                netcode.History_NestedReplay_AfterWarmup_DoesNotAllocate);
            var derived = new DerivedPropertyConcurrencyTests();
            Check(nameof(derived.InitialCompute_CannotOverwriteANewerSourceResult_InEveryOperator),
                derived.InitialCompute_CannotOverwriteANewerSourceResult_InEveryOperator);
            Check(nameof(derived.CombineLatest_OlderComputeFinishingLast_DoesNotOverwriteNewerResult),
                derived.CombineLatest_OlderComputeFinishingLast_DoesNotOverwriteNewerResult);
            Check(nameof(derived.Dispose_DuringSelector_DropsItsResultAndDetaches),
                derived.Dispose_DuringSelector_DropsItsResultAndDetaches);
            Check(nameof(derived.Notification_CanWaitForAnotherSourceWriter_WithoutHoldingTheCommitLock),
                derived.Notification_CanWaitForAnotherSourceWriter_WithoutHoldingTheCommitLock);
            var fsm = new FSMFailureCleanupTests();
            Check(nameof(fsm.SupersededLateFailure_ExitsOnce_WithoutEnteringFallback),
                () => fsm.SupersededLateFailure_ExitsOnce_WithoutEnteringFallback().GetAwaiter().GetResult());
            Check(nameof(fsm.DisposeDuringLateFailure_ExitsOnce_AndLeavesNoCurrentState),
                () => fsm.DisposeDuringLateFailure_ExitsOnce_AndLeavesNoCurrentState().GetAwaiter().GetResult());
            Check(nameof(fsm.LateFailureExit_IsAwaited_BeforeTheNewestStateEnters),
                () => fsm.LateFailureExit_IsAwaited_BeforeTheNewestStateEnters().GetAwaiter().GetResult());
            var saves = new SaveThrottlerRetentionTests();
            Check(nameof(saves.TickThenDispose_ReleasesCapturedOwner_WithoutAnotherTick),
                saves.TickThenDispose_ReleasesCapturedOwner_WithoutAnotherTick);
            Check(nameof(saves.ThrowingSaveThenDispose_AlsoReleasesCapturedOwner),
                saves.ThrowingSaveThenDispose_AlsoReleasesCapturedOwner);
            Check(nameof(saves.NestedTick_PreservesOuterWork_AndReleasesBothBuffers),
                saves.NestedTick_PreservesOuterWork_AndReleasesBothBuffers);
            Check(nameof(netcode.NewFutureChild_UsesCorrectedProductionTime_NotItsNewIdentity),
                netcode.NewFutureChild_UsesCorrectedProductionTime_NotItsNewIdentity);
            Check(nameof(netcode.RetimedParent_OrdersFutureChildrenByTheirNewProductionTime),
                netcode.RetimedParent_OrdersFutureChildrenByTheirNewProductionTime);
            Check(nameof(netcode.PartialRollback_InvalidatesSnapshotsOfTheAbandonedFuture),
                netcode.PartialRollback_InvalidatesSnapshotsOfTheAbandonedFuture);
            Check(nameof(netcode.RegeneratedTypedHistory_PreservesLogicalRecordingOrder),
                netcode.RegeneratedTypedHistory_PreservesLogicalRecordingOrder);
            Check(nameof(netcode.TwoDerivedChildren_JournalAndTypedReplay_AfterWarmup_DoNotAllocate),
                netcode.TwoDerivedChildren_JournalAndTypedReplay_AfterWarmup_DoNotAllocate);
            Check(nameof(netcode.CorrectedScheduling_MatchesAnIndependentChronologicalQueue),
                netcode.CorrectedScheduling_MatchesAnIndependentChronologicalQueue);
            return failures;

            void Check(string name, Action test)
            {
                bool ok = true;
                string detail = "public API regression assertions passed";
                try { test(); }
                catch (Exception ex) { ok = false; detail = ex.ToString(); failures++; }
                Console.WriteLine($"[FrameworkClosure] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
                ResultSink.Capture("FrameworkClosure", name, ok, detail);
            }
        }
    }
}
