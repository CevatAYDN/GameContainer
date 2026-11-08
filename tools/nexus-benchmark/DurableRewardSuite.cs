using System;
using Nexus.Ads.Bridge.Tests;
using Nexus.Editor.Tests;

namespace NexusBench
{
    public static class DurableRewardSuite
    {
        public static int Run()
        {
            var tests = new DurableRewardDeliveryTests();
            int failures = 0;
            Check(nameof(tests.BoolCallback_FailedSave_ReopensWithPendingReward), tests.BoolCallback_FailedSave_ReopensWithPendingReward);
            Check(nameof(tests.OutcomeCallback_FailedSave_ReopensWithPendingReward), tests.OutcomeCallback_FailedSave_ReopensWithPendingReward);
            Check(nameof(tests.StartupReplay_FailedSave_RetainsReceiptAcrossAnotherReopen), tests.StartupReplay_FailedSave_RetainsReceiptAcrossAnotherReopen);
            Check(nameof(tests.CommitBeforeAckFailure_ReplaysWithoutDuplicate), tests.CommitBeforeAckFailure_ReplaysWithoutDuplicate);
            Check(nameof(tests.BoolCommitBeforeAckFailure_ReplaysWithoutDuplicate), tests.BoolCommitBeforeAckFailure_ReplaysWithoutDuplicate);
            Check(nameof(tests.SuccessfulCallbacks_CommitDistinctRewards_AndClearEachReceiptOnce), tests.SuccessfulCallbacks_CommitDistinctRewards_AndClearEachReceiptOnce);
            Check(nameof(tests.LegacyVoidSave_RemainsUnconfirmed_AndNormalCallbackStillAcknowledges), tests.LegacyVoidSave_RemainsUnconfirmed_AndNormalCallbackStillAcknowledges);
            Check(nameof(tests.LegacyThrowingCallbacks_RetainReceipt), tests.LegacyThrowingCallbacks_RetainReceipt);
            Check(nameof(tests.NonRewardOutcomes_DoNotGrantOrAcknowledge), tests.NonRewardOutcomes_DoNotGrantOrAcknowledge);
            Check(nameof(tests.ReentrantCallback_DoesNotAcknowledgeANewerReceipt), tests.ReentrantCallback_DoesNotAcknowledgeANewerReceipt);
            Check(nameof(tests.BoolReadyChangedReentry_PreservesTheNewerFailedReceipt), tests.BoolReadyChangedReentry_PreservesTheNewerFailedReceipt);
            Check(nameof(tests.OutcomeReadyChangedReentry_PreservesTheNewerFailedReceipt), tests.OutcomeReadyChangedReentry_PreservesTheNewerFailedReceipt);
            Check(nameof(tests.BoolReadyChangedReentry_GrantsTheOriginalDeliveryId), tests.BoolReadyChangedReentry_GrantsTheOriginalDeliveryId);
            Check(nameof(tests.OutcomeReadyChangedReentry_GrantsTheOriginalDeliveryId), tests.OutcomeReadyChangedReentry_GrantsTheOriginalDeliveryId);
            Check(nameof(tests.TelemetryAndAdClosedReentry_KeepTheOriginalGrantAndTheNewerPendingReceipt), tests.TelemetryAndAdClosedReentry_KeepTheOriginalGrantAndTheNewerPendingReceipt);
            Check(nameof(tests.DeliverySnapshot_RestoresAfterNestedAndNonRewardCallbacks_AndClearsAfterThrow), tests.DeliverySnapshot_RestoresAfterNestedAndNonRewardCallbacks_AndClearsAfterThrow);
            Check(nameof(tests.ReceiptWriteFailure_DoesNotDeliverAnOlderPendingId), tests.ReceiptWriteFailure_DoesNotDeliverAnOlderPendingId);
            var commits = new EncryptedStorageCommitTests();
            Check(nameof(commits.MixedSuccessfulAndFailedKeys_NeverConfirmTheBatch), commits.MixedSuccessfulAndFailedKeys_NeverConfirmTheBatch);
            Check(nameof(commits.AutoSaveFailure_RemainsUnconfirmed_AndReopensThePreviousValue), commits.AutoSaveFailure_RemainsUnconfirmed_AndReopensThePreviousValue);
            Check(nameof(commits.FailedRecoveryRewrite_RemainsPendingUntilConfirmed), commits.FailedRecoveryRewrite_RemainsPendingUntilConfirmed);
            Check(nameof(commits.TombstoneAndDeleteKey_ConfirmOnlyAfterDurableDeletion), commits.TombstoneAndDeleteKey_ConfirmOnlyAfterDurableDeletion);
            Check(nameof(commits.FailedDelete_NeverConfirms_AndKeepsTheOldDurableValue), commits.FailedDelete_NeverConfirms_AndKeepsTheOldDurableValue);
            Check(nameof(commits.DeleteKey_UnqueryablePaths_RemainPendingUntilConfirmedRetry), commits.DeleteKey_UnqueryablePaths_RemainPendingUntilConfirmedRetry);
            Check(nameof(commits.Tombstone_UnqueryablePaths_RemainPendingUntilConfirmedRetry), commits.Tombstone_UnqueryablePaths_RemainPendingUntilConfirmedRetry);
            Check(nameof(commits.ConcurrentMutation_DuringCommit_ReturnsFalse_ThenRetryPersistsTheNewVersion), commits.ConcurrentMutation_DuringCommit_ReturnsFalse_ThenRetryPersistsTheNewVersion);
            Check(nameof(commits.SupersededButAlreadyAutoSavedMutation_StillDoesNotConfirmTheOlderCall), commits.SupersededButAlreadyAutoSavedMutation_StillDoesNotConfirmTheOlderCall);
            Check(nameof(commits.DisposeDuringCommit_AndDisposedStore_DoNotConfirm), commits.DisposeDuringCommit_AndDisposedStore_DoNotConfirm);
            Check(nameof(commits.DeleteWaitingOnAnotherWrite_CannotBeMistakenForACommittedDelete), commits.DeleteWaitingOnAnotherWrite_CannotBeMistakenForACommittedDelete);
            Check(nameof(commits.LegacyCustomStore_DefaultIsFalse_AndSaveOrThrowDoesNotInferFromVoidSave), commits.LegacyCustomStore_DefaultIsFalse_AndSaveOrThrowDoesNotInferFromVoidSave);
            Check(nameof(commits.EmptySupportedStore_ConfirmsWithoutPendingWork), commits.EmptySupportedStore_ConfirmsWithoutPendingWork);
            return failures;

            void Check(string name, Action test)
            {
                bool ok = true;
                string detail = "durable reopen assertions passed";
                try { test(); }
                catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; failures++; }
                Console.WriteLine($"[DurableReward] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
                ResultSink.Capture("DurableReward", name, ok, detail);
            }
        }
    }
}
