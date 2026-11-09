using System;
using Nexus.Editor.Tests;

namespace NexusBench
{
    public static class ConsumerPersistenceSuite
    {
        public static int Run()
        {
            int failures = 0;
            var memory = new InMemoryPersistenceTests();
            Check(nameof(memory.ConfirmedSave_CanBeUsedByARewardCallback), memory.ConfirmedSave_CanBeUsedByARewardCallback);
            Check(nameof(memory.Reopen_DropsUncommittedWritesAndDeletes_WithoutSharingLiveState), memory.Reopen_DropsUncommittedWritesAndDeletes_WithoutSharingLiveState);
            Check(nameof(memory.FailedSave_DoesNotCommit_AndSaveOrThrowRetainsChangesForRetry), memory.FailedSave_DoesNotCommit_AndSaveOrThrowRetainsChangesForRetry);
            Check(nameof(memory.UnreadableKeys_SurviveReopen_AndRestoreWithoutCommittingPendingWrites), memory.UnreadableKeys_SurviveReopen_AndRestoreWithoutCommittingPendingWrites);
            Check(nameof(memory.WritingOrDeletingUnreadableKeys_ClearsOnlyTheLiveFaultUntilSave), memory.WritingOrDeletingUnreadableKeys_ClearsOnlyTheLiveFaultUntilSave);
            Check(nameof(memory.UnreadableWallet_BlocksEconomy_ThenRetriesOnTheSameService), memory.UnreadableWallet_BlocksEconomy_ThenRetriesOnTheSameService);
            var receipts = new EconomyReceiptTests();
            Check(nameof(receipts.ReceiptEarn_AfterSpendAndAnotherReceipt_RestartNeverPaysEitherTwice), receipts.ReceiptEarn_AfterSpendAndAnotherReceipt_RestartNeverPaysEitherTwice);
            Check(nameof(receipts.FailedConfirmation_RetryOrAbruptReopen_GrantsExactlyOnce), receipts.FailedConfirmation_RetryOrAbruptReopen_GrantsExactlyOnce);
            Check(nameof(receipts.ReceiptEarn_ConfirmsTheEconomyOverride_AndBypassesItsThrottler), receipts.ReceiptEarn_ConfirmsTheEconomyOverride_AndBypassesItsThrottler);
            Check(nameof(receipts.PromotionAndNormalMutations_PreserveReceiptsInTheCanonicalWallet), receipts.PromotionAndNormalMutations_PreserveReceiptsInTheCanonicalWallet);
            Check(nameof(receipts.ReentrantRewardAndSpend_CommitEveryReceiptBeforeReturning), receipts.ReentrantRewardAndSpend_CommitEveryReceiptBeforeReturning);
            Check(nameof(receipts.UnreadableOrMalformedWallet_NeverFallsBackToStaleLegacyBalances), receipts.UnreadableOrMalformedWallet_NeverFallsBackToStaleLegacyBalances);
            Check(nameof(receipts.EncryptedMigrationWriteFailure_ReopenKeepsLegacyBalanceAndRetriesMissingReceipt), receipts.EncryptedMigrationWriteFailure_ReopenKeepsLegacyBalanceAndRetriesMissingReceipt);
            Check(nameof(receipts.EncryptedCommitBeforeConfirmationFailure_ReopenDeduplicatesAfterSpending), receipts.EncryptedCommitBeforeConfirmationFailure_ReopenDeduplicatesAfterSpending);
            Check(nameof(receipts.EncryptedCorruption_ImportRecoveryKeepsReceiptIdentity), receipts.EncryptedCorruption_ImportRecoveryKeepsReceiptIdentity);
            Check(nameof(receipts.ConcurrentDuplicateReceipts_ApplyOnceAndConfirmOneEncryptedWallet), receipts.ConcurrentDuplicateReceipts_ApplyOnceAndConfirmOneEncryptedWallet);
            Check(nameof(receipts.ReceiptValidation_RefusesInvalidOrDisposedCalls_AndScopesIdsPerCurrency), receipts.ReceiptValidation_RefusesInvalidOrDisposedCalls_AndScopesIdsPerCurrency);
            Check(nameof(receipts.StoreSetterReentry_RejectsMutationBeforeItCanLoseAConfirmedNestedGrant), receipts.StoreSetterReentry_RejectsMutationBeforeItCanLoseAConfirmedNestedGrant);
            Check(nameof(receipts.InvalidUtf16Receipt_IsRejectedBeforeAnyBalanceOrLedgerMutation), receipts.InvalidUtf16Receipt_IsRejectedBeforeAnyBalanceOrLedgerMutation);
            Check(nameof(receipts.StoreSetterReentry_AlsoRejectsNormalMutationsAndDisposal), receipts.StoreSetterReentry_AlsoRejectsNormalMutationsAndDisposal);
            Check(nameof(receipts.ThrowingStoreSetter_ReleasesTheGuard_AndDuplicateRetryConfirms), receipts.ThrowingStoreSetter_ReleasesTheGuard_AndDuplicateRetryConfirms);
            Check(nameof(receipts.LongRefundCompletedDuringWalletStaging_IsDeferredAndPersisted),
                () => receipts.LongRefundCompletedDuringWalletStaging_IsDeferredAndPersisted().GetAwaiter().GetResult());
            Check(nameof(receipts.BigRefundCompletedDuringWalletStaging_IsDeferredAndPersisted),
                () => receipts.BigRefundCompletedDuringWalletStaging_IsDeferredAndPersisted().GetAwaiter().GetResult());
            var queries = new EconomyReadQueryTests();
            Check(nameof(queries.UnreadableQuery_DoesNotUseExceptionControlFlow_AndCanRetry), queries.UnreadableQuery_DoesNotUseExceptionControlFlow_AndCanRetry);
            Check(nameof(queries.UnavailableBalance_ViewBindsOnce_ThenTracksRecoveryWithoutSceneReload), queries.UnavailableBalance_ViewBindsOnce_ThenTracksRecoveryWithoutSceneReload);
            Check(nameof(queries.ReadQueries_DistinguishAbsentInvalidDisposedAndMalformedCanonicalValues), queries.ReadQueries_DistinguishAbsentInvalidDisposedAndMalformedCanonicalValues);
            Check(nameof(queries.LoadedReadQueries_DoNotAllocate_AndRespectCanonicalBigBalance), queries.LoadedReadQueries_DoNotAllocate_AndRespectCanonicalBigBalance);
            var encrypted = new EconomyUnreadableBalanceTests();
            Check(nameof(encrypted.ReadQueries_EncryptedFaultAndImport_KeepViewsRecoverableWithoutMutation), encrypted.ReadQueries_EncryptedFaultAndImport_KeepViewsRecoverableWithoutMutation);
            var delivery = new Nexus.Ads.Bridge.Tests.DurableRewardDeliveryTests();
            Check(nameof(delivery.NexusEconomyReceipt_CallbackAndStartupFailures_NeverAcknowledgeAnUncommittedGrant),
                delivery.NexusEconomyReceipt_CallbackAndStartupFailures_NeverAcknowledgeAnUncommittedGrant);
            return failures;

            void Check(string name, Action test)
            {
                bool ok = true;
                string detail = "consumer persistence assertions passed";
                try { test(); }
                catch (Exception ex) { ok = false; detail = ex.ToString(); failures++; }
                Console.WriteLine($"[ConsumerPersistence] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
                ResultSink.Capture("ConsumerPersistence", name, ok, detail);
            }
        }
    }
}
