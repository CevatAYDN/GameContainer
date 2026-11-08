using System;
using Nexus.Editor.Tests;

namespace NexusBench
{
    public static class EconomyUnreadableSuite
    {
        public static int Run()
        {
            var tests = new EconomyUnreadableBalanceTests();
            int failures = 0;
            Check(nameof(tests.LongReadFault_ThenDispose_PreservesTheOriginal1000AndBytes), tests.LongReadFault_ThenDispose_PreservesTheOriginal1000AndBytes);
            Check(nameof(tests.CanonicalBigReadFault_PreservesCanonicalAndReadableLegacy), tests.CanonicalBigReadFault_PreservesCanonicalAndReadableLegacy);
            Check(nameof(tests.CorruptLongRead_ThenDispose_PreservesEvidenceInsteadOfWritingZero), tests.CorruptLongRead_ThenDispose_PreservesEvidenceInsteadOfWritingZero);
            Check(nameof(tests.UnavailableBalances_RejectEveryReadAndMutation_WithoutChangingCanonicalOrLegacy), tests.UnavailableBalances_RejectEveryReadAndMutation_WithoutChangingCanonicalOrLegacy);
            Check(nameof(tests.TransientRecovery_RetriesSameEconomyInstance_AndResumesNormalMutation), tests.TransientRecovery_RetriesSameEconomyInstance_AndResumesNormalMutation);
            Check(nameof(tests.CorruptImportRecovery_RetriesSameEconomyInstance_AndResumesNormalMutation), tests.CorruptImportRecovery_RetriesSameEconomyInstance_AndResumesNormalMutation);
            Check(nameof(tests.HealthyCurrencyAndSharedThrottlerFlush_DoNotPersistFailedLoads), tests.HealthyCurrencyAndSharedThrottlerFlush_DoNotPersistFailedLoads);
            Check(nameof(tests.MalformedCanonicalNumbers_ThrowAndPreserveStoredValues), tests.MalformedCanonicalNumbers_ThrowAndPreserveStoredValues);
            Check(nameof(tests.AbsentAndHealthyBalances_RetainNormalPersistenceAndPromotion), tests.AbsentAndHealthyBalances_RetainNormalPersistenceAndPromotion);
            Check(nameof(tests.NativeTypedStores_DoNotRequireFrameworkStringEncoding), tests.NativeTypedStores_DoNotRequireFrameworkStringEncoding);
            Check(nameof(tests.LegacyLocaleAndSentinelValues_RetainExistingNumericDecoding), tests.LegacyLocaleAndSentinelValues_RetainExistingNumericDecoding);
            Check(nameof(tests.InvalidAmountsAndDisposedRefusals_RemainIndependentOfUnreadableStorage), tests.InvalidAmountsAndDisposedRefusals_RemainIndependentOfUnreadableStorage);
            Check(nameof(tests.LoadedBigBalance_RemainsAuthoritativeForItsLongViewDuringAReadFault), tests.LoadedBigBalance_RemainsAuthoritativeForItsLongViewDuringAReadFault);
            Check(nameof(tests.EconomyStoreOverride_UnreadableBalancePreservesBothStores_AndRetriesAfterImport), tests.EconomyStoreOverride_UnreadableBalancePreservesBothStores_AndRetriesAfterImport);
            Check(nameof(tests.SlowFirstTouch_DoesNotHoldTheBalanceLock), tests.SlowFirstTouch_DoesNotHoldTheBalanceLock);
            Check(nameof(tests.RecoveredConcurrentMutations_AndReentrantSubscribers_PreserveBalances), tests.RecoveredConcurrentMutations_AndReentrantSubscribers_PreserveBalances);
            return failures;

            void Check(string name, Action test)
            {
                bool ok = true;
                string detail = "public economy and durable-file assertions passed";
                try { test(); }
                catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; failures++; }
                Console.WriteLine($"[EconomyUnreadable] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
                ResultSink.Capture("EconomyUnreadable", name, ok, detail);
            }
        }
    }
}
