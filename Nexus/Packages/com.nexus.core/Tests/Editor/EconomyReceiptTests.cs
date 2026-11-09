using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class EconomyReceiptTests
    {
        private const string WalletKey = "NT_EcoWallet_coin";
#if UNITY_EDITOR
        [Test]
#endif
        public void ReceiptEarn_AfterSpendAndAnotherReceipt_RestartNeverPaysEitherTwice()
        {
            var store = new InMemoryPlayerPrefsService();
            using var service = new EconomyService { PlayerPrefsService = store };
            IEconomyService economy = service;
            economy.SetBalance("coin", 100);
            Require(economy.EarnOnce("coin", 40, "first", "triple"), "the first receipt must apply");
            Require(economy.Spend("coin", 30), "a normal spend must be allowed after the reward");
            Require(economy.EarnOnce("coin", 20, "second", "mission"), "a distinct receipt must apply");
            using var reopened = new EconomyService { PlayerPrefsService = store.Reopen() };
            IEconomyService afterKill = reopened;
            Require(afterKill.GetBalance("coin") == 130, "both rewards and the spend must survive restart");
            Require(!afterKill.EarnOnce("coin", 40, "first") && !afterKill.EarnOnce("coin", 20, "second")
                && afterKill.GetBalance("coin") == 130,
                "replay must deduplicate historical receipt ids, independent of the current balance");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedConfirmation_RetryOrAbruptReopen_GrantsExactlyOnce()
        {
            foreach (bool restart in new[] { false, true })
            {
                var store = new InMemoryPlayerPrefsService();
                store.SetLong("NT_Eco_coin", 100);
                store.SaveOrThrow();
                using var economy = new EconomyService { PlayerPrefsService = store };
                store.FailNextSaves(1);
                ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
                Require(economy.GetBalance("coin") == 140, "failed confirmation must retain the live operation for retry");
                var killed = store.Reopen();
                using var durable = new EconomyService { PlayerPrefsService = killed };
                Require(durable.GetBalance("coin") == 100, "an uncommitted grant must not survive restart");
                var retry = restart ? durable : economy;
                Require(retry.EarnOnce("coin", 40, "receipt") == restart,
                    "same-session retry confirms the existing grant; restart applies the missing grant");
                var finalStore = (restart ? killed : store).Reopen();
                using var final = new EconomyService { PlayerPrefsService = finalStore };
                Require(final.GetBalance("coin") == 140 && !final.EarnOnce("coin", 40, "receipt"),
                    "either retry path must leave exactly one durable grant");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReceiptEarn_ConfirmsTheEconomyOverride_AndBypassesItsThrottler()
        {
            var fallback = new InMemoryPlayerPrefsService();
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("NT_Eco_coin", 100);
            store.SaveOrThrow();
            var throttler = new SaveThrottler { TimeProvider = new Clock() };
            throttler.ForceSave("economy", () => { });
            using var economy = new EconomyService
            {
                PlayerPrefsService = fallback,
                SettingsStores = new SettingsStores { Economy = store },
                SaveThrottler = throttler
            };
            economy.Earn("coin", 5);
            store.FailNextSaves(1);
            ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
            Require(!economy.EarnOnce("coin", 40, "receipt"), "duplicate retries must still confirm the failed store");
            using (var reopened = new EconomyService { PlayerPrefsService = store.Reopen() })
                Require(reopened.GetBalance("coin") == 145, "receipt commit must not wait for a throttle window");
            Require(fallback.Keys.Count == 0, "the default preferences must not receive the override's wallet");
            Require(economy.Spend("coin", 30), "a normal spend remains valid");
            throttler.Flush();
            using var final = new EconomyService { PlayerPrefsService = store.Reopen() };
            Require(final.GetBalance("coin") == 115 && !final.EarnOnce("coin", 40, "receipt"),
                "later throttled writes must preserve the ledger");
            throttler.OnDispose();
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void PromotionAndNormalMutations_PreserveReceiptsInTheCanonicalWallet()
        {
            var store = new InMemoryPlayerPrefsService();
            using var economy = new EconomyService { PlayerPrefsService = store };
            economy.SetBalance("coin", 100);
            var longView = economy.GetObservableBalance("coin");
            Require(economy.EarnOnce("coin", 40, "long"), "long receipt applies");
            Require(economy.EarnBigOnce("coin", new BigDouble(2.5), "big"), "big receipt promotes the live wallet");
            var promoted = economy.GetBigBalance("coin");
            Require(longView.Value == 142 && Math.Abs((double)promoted - 142.5) < 1e-10,
                $"promotion must preserve the observable and big amount: long={longView.Value}, mantissa={promoted.Mantissa:R}, exponent={promoted.Exponent}");
            using (var afterPromotion = new EconomyService { PlayerPrefsService = store.Reopen() })
                Require(afterPromotion.GetBigBalance("coin") == promoted, "persistence must be bit-exact even when arithmetic rounds");
            Require(economy.SpendBig("coin", new BigDouble(12.5)), "promoted wallet must spend normally");
            economy.SetBalance("coin", 170);
            economy.EarnBig("coin", new BigDouble(3.5));
            var beforeRestart = economy.GetBigBalance("coin");
            using var restarted = new EconomyService { PlayerPrefsService = store.Reopen() };
            Require(Math.Abs((double)beforeRestart - 173.5) < 1e-10 && restarted.GetBigBalance("coin") == beforeRestart
                && !restarted.EarnOnce("coin", 40, "long") && !restarted.EarnBigOnce("coin", new BigDouble(2.5), "big"),
                "set, spend, earn, promotion and restart must not discard either receipt");
            Require(restarted.GetBalance("coin") == 173, "long API remains a projection of the canonical big wallet");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReentrantRewardAndSpend_CommitEveryReceiptBeforeReturning()
        {
            var store = new InMemoryPlayerPrefsService();
            using var economy = new EconomyService { PlayerPrefsService = store };
            economy.SetBalance("coin", 100);
            bool entered = false, duplicate = true, nested = false;
            long insideCallback = -1;
            economy.GetObservableBalance("coin").OnChanged((_, __) =>
            {
                if (entered) return;
                entered = true;
                duplicate = economy.EarnOnce("coin", 40, "outer");
                Require(economy.Spend("coin", 10), "the callback's spend must apply");
                nested = economy.EarnOnce("coin", 20, "inner");
                using var reader = new EconomyService { PlayerPrefsService = store.Reopen() };
                insideCallback = reader.GetBalance("coin");
            });
            Require(economy.EarnOnce("coin", 40, "outer") && entered && !duplicate && nested
                && insideCallback == 150 && economy.GetBalance("coin") == 150,
                "nested delivery must persist the actual cumulative balance, not just both ids");
            using var reopened = new EconomyService { PlayerPrefsService = store.Reopen() };
            Require(!reopened.EarnOnce("coin", 40, "outer") && !reopened.EarnOnce("coin", 20, "inner")
                && reopened.GetBalance("coin") == 150, "both ids must stay applied across restart");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UnreadableOrMalformedWallet_NeverFallsBackToStaleLegacyBalances()
        {
            foreach (bool unreadable in new[] { false, true })
            {
                var store = new InMemoryPlayerPrefsService();
                store.SetLong("NT_Eco_coin", 100);
                using (var writer = new EconomyService { PlayerPrefsService = store })
                    writer.EarnOnce("coin", 40, "receipt");
                store.SetLong("NT_Eco_coin", 3);
                ((IPlayerPrefsService)store).SetBigDouble("NT_EcoBig_coin", new BigDouble(999));
                if (unreadable) store.MarkUnreadable(WalletKey);
                else store.SetString(WalletKey, "not-a-wallet");
                using var economy = new EconomyService { PlayerPrefsService = store };
                ExpectIO(() => economy.GetBalance("coin"));
                ExpectIO(() => economy.GetBigBalance("coin"));
                ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
                ExpectIO(() => economy.SetBalance("coin", 0));
                economy.Dispose();
                Require(store.GetLong("NT_Eco_coin") == 3 && ((IPlayerPrefsService)store).GetBigDouble("NT_EcoBig_coin") == new BigDouble(999),
                    "unavailable canonical wallet must preserve both legacy recovery keys");
                if (unreadable) Require(store.TryGetString(WalletKey, out _) == StorageReadStatus.Unreadable, "fault must remain");
                else Require(store.GetString(WalletKey) == "not-a-wallet", "malformed evidence must remain");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void EncryptedMigrationWriteFailure_ReopenKeepsLegacyBalanceAndRetriesMissingReceipt()
        {
            using var files = new Files();
            var seed = files.Open();
            seed.SetLong("NT_Eco_coin", 100);
            seed.SaveOrThrow();
            seed.Dispose();
            var store = files.Open();
            var economy = files.Economy(store);
            string staging = Files.PathFor(store, WalletKey) + ".tmp";
            Directory.CreateDirectory(staging);
            try
            {
                ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
                var reopened = files.Economy(files.Open());
                Require(reopened.GetBalance("coin") == 100, "a failed first wallet commit must keep the legacy balance");
            }
            finally { Directory.Delete(staging); }
            var restart = files.Economy(files.Open());
            Require(restart.EarnOnce("coin", 40, "receipt") && restart.GetBalance("coin") == 140,
                "the missing receipt must pay after restarting from the unmodified legacy file");
            var confirmed = files.Economy(files.Open());
            Require(confirmed.GetBalance("coin") == 140 && !confirmed.EarnOnce("coin", 40, "receipt"),
                "the committed migrated wallet must deduplicate");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void EncryptedCommitBeforeConfirmationFailure_ReopenDeduplicatesAfterSpending()
        {
            using var files = new Files();
            var store = files.OpenAfterReplace();
            var economy = files.Economy(store);
            economy.SetBalance("coin", 100);
            economy.EarnOnce("coin", 1, "seed");
            store.ThrowAfterReplace = true;
            ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
            var restarted = files.Economy(files.Open());
            Require(restarted.GetBalance("coin") == 141, "the complete wallet must survive replacement before acknowledgement");
            Require(restarted.Spend("coin", 30), "spending changes the balance that the old workaround compared");
            Require(!restarted.EarnOnce("coin", 40, "receipt") && restarted.GetBalance("coin") == 111,
                "a durable receipt remains applied even after an unrelated spend");
            var next = files.Economy(files.Open());
            Require(next.GetBalance("coin") == 111 && !next.EarnOnce("coin", 40, "receipt"),
                "no later restart may pay this receipt twice");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void EncryptedCorruption_ImportRecoveryKeepsReceiptIdentity()
        {
            using var files = new Files();
            var seed = files.Open();
            var writer = files.Economy(seed);
            writer.SetBalance("coin", 100);
            writer.EarnOnce("coin", 40, "receipt");
            string payload = seed.ExportEncryptedSaveData(WalletKey);
            writer.Dispose();
            seed.Dispose();
            string path = Files.PathFor(seed, WalletKey);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            File.Delete(path + ".bak");
            var store = files.Open();
            var economy = files.Economy(store);
            ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
            Require(File.ReadAllBytes(path).Length == 3, "failed load must not overwrite corrupt evidence");
            Require(store.ImportEncryptedSaveData(WalletKey, payload), "valid same-seed payload must restore the wallet");
            Require(!economy.EarnOnce("coin", 40, "receipt") && economy.GetBalance("coin") == 140,
                "recovery must preserve the receipt, not manufacture a new grant");
            Require(economy.EarnOnce("coin", 10, "new") && files.Economy(files.Open()).GetBalance("coin") == 150,
                "the recovered wallet must accept a new durable receipt");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ConcurrentDuplicateReceipts_ApplyOnceAndConfirmOneEncryptedWallet()
        {
            using var files = new Files();
            var store = files.Open();
            var economy = files.Economy(store);
            economy.SetBalance("coin", 100);
            int applied = 0;
            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Run(() =>
                {
                    if (economy.EarnOnce("coin", 40, "same")) Interlocked.Increment(ref applied);
                });
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            Require(applied == 1 && economy.GetBalance("coin") == 140, "concurrent duplicate delivery must apply once");
            var restarted = files.Economy(files.Open());
            Require(restarted.GetBalance("coin") == 140 && !restarted.EarnOnce("coin", 40, "same"),
                "the once-only result must be durable, not merely a locked RAM value");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReceiptValidation_RefusesInvalidOrDisposedCalls_AndScopesIdsPerCurrency()
        {
            var store = new InMemoryPlayerPrefsService();
            using var economy = new EconomyService { PlayerPrefsService = store };
            Expect<ArgumentException>(() => economy.EarnOnce("", 1, "r"));
            Expect<ArgumentException>(() => economy.EarnOnce("coin", 1, " "));
            Expect<ArgumentOutOfRangeException>(() => economy.EarnOnce("coin", 0, "r"));
            Expect<ArgumentOutOfRangeException>(() => economy.EarnBigOnce("coin", new BigDouble { Mantissa = double.NaN }, "r"));
            Require(store.Keys.Count == 0, "invalid arguments must not create data");
            const string id = "shared-\u00e7\n\"\\";
            Require(economy.EarnOnce("coin", 3, id) && economy.EarnOnce("gem", 7, id), "receipt ids are scoped per currency");
            using var restarted = new EconomyService { PlayerPrefsService = store.Reopen() };
            Require(!restarted.EarnOnce("coin", 3, id) && !restarted.EarnOnce("gem", 7, id)
                && restarted.GetBalance("coin") == 3 && restarted.GetBalance("gem") == 7,
                "binary receipt encoding must preserve exact id text and currency scope");
            economy.Dispose();
            Expect<ObjectDisposedException>(() => economy.EarnOnce("coin", 1, "late"));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void StoreSetterReentry_RejectsMutationBeforeItCanLoseAConfirmedNestedGrant()
        {
            var memory = new InMemoryPlayerPrefsService();
            var store = new ReentrantStore(memory);
            using var economy = new EconomyService { PlayerPrefsService = store };
            economy.SetBalance("coin", 100);
            bool rejected = false;
            store.BeforeSet = () =>
            {
                try { economy.EarnOnce("coin", 20, "inner"); }
                catch (InvalidOperationException) { rejected = true; }
            };
            Require(economy.EarnOnce("coin", 40, "outer"), "the outer delivery must commit");
            using var reopened = new EconomyService { PlayerPrefsService = memory.Reopen() };
            Require(rejected && economy.GetBalance("coin") == 140 && reopened.GetBalance("coin") == 140,
                $"store callbacks cannot mutate the economy during payload staging: rejected={rejected}, live={economy.GetBalance("coin")}, durable={reopened.GetBalance("coin")}");
            Require(economy.EarnOnce("coin", 20, "inner"), "a rejected nested receipt must remain unapplied and retryable");
            using var final = new EconomyService { PlayerPrefsService = memory.Reopen() };
            Require(final.GetBalance("coin") == 160 && !final.EarnOnce("coin", 20, "inner"),
                "retry outside the storage callback must commit exactly one nested grant");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void InvalidUtf16Receipt_IsRejectedBeforeAnyBalanceOrLedgerMutation()
        {
            foreach (string invalid in new[] { "receipt-\uD800", "receipt-\uDC00", "\uD800x" })
            {
                var store = new InMemoryPlayerPrefsService();
                store.SetLong("NT_Eco_coin", 100);
                store.SaveOrThrow();
                using var economy = new EconomyService { PlayerPrefsService = store };
                Expect<ArgumentException>(() => economy.EarnOnce("coin", 40, invalid));
                Expect<ArgumentException>(() => economy.EarnBigOnce("coin", new BigDouble(40), invalid));
                Require(economy.GetBalance("coin") == 100 && !store.HasKey(WalletKey),
                    "invalid UTF-16 must not be repaired into another receipt id or mutate a wallet");
                Require(economy.EarnOnce("coin", 40, "valid"), "validation failure must not block later valid delivery");
                using var reopened = new EconomyService { PlayerPrefsService = store.Reopen() };
                Require(reopened.GetBalance("coin") == 140 && !reopened.EarnOnce("coin", 40, "valid"),
                    "valid receipt identity must still round-trip after rejection");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void StoreSetterReentry_AlsoRejectsNormalMutationsAndDisposal()
        {
            var mutations = new (string Name, Action<EconomyService> Run)[]
            {
                ("Earn", e => e.Earn("coin", 5)),
                ("Spend", e => e.Spend("coin", 5)),
                ("Set", e => e.SetBalance("coin", 1)),
                ("EarnBig", e => e.EarnBig("coin", new BigDouble(5))),
                ("SpendBig", e => e.SpendBig("coin", new BigDouble(5))),
                ("SetBig", e => e.SetBigBalance("coin", new BigDouble(1))),
                ("EarnBigOnce", e => e.EarnBigOnce("coin", new BigDouble(5), "inner")),
                ("Dispose", e => e.Dispose())
            };
            foreach (var mutation in mutations)
            {
                var memory = new InMemoryPlayerPrefsService();
                var store = new ReentrantStore(memory);
                using var economy = new EconomyService { PlayerPrefsService = store };
                economy.SetBalance("coin", 100);
                bool rejected = false;
                store.BeforeSet = () =>
                {
                    try { mutation.Run(economy); }
                    catch (InvalidOperationException) { rejected = true; }
                };
                economy.EarnOnce("coin", 40, "outer");
                using var reopened = new EconomyService { PlayerPrefsService = memory.Reopen() };
                Require(rejected && economy.GetBalance("coin") == 140 && reopened.GetBalance("coin") == 140,
                    mutation.Name + " must not modify staged wallet state from a storage callback");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ThrowingStoreSetter_ReleasesTheGuard_AndDuplicateRetryConfirms()
        {
            var memory = new InMemoryPlayerPrefsService();
            var store = new ReentrantStore(memory);
            using var economy = new EconomyService { PlayerPrefsService = store };
            economy.SetBalance("coin", 100);
            store.BeforeSet = () => throw new IOException("injected setter failure");
            ExpectIO(() => economy.EarnOnce("coin", 40, "receipt"));
            using (var killed = new EconomyService { PlayerPrefsService = memory.Reopen() })
                Require(killed.GetBalance("coin") == 100, "setter failure must not be mistaken for a commit");
            Require(!economy.EarnOnce("coin", 40, "receipt"), "retry must confirm the pending operation without adding again");
            using var reopened = new EconomyService { PlayerPrefsService = memory.Reopen() };
            Require(reopened.GetBalance("coin") == 140 && !reopened.EarnOnce("coin", 40, "receipt"),
                "a throwing store must not leave the staging guard latched or lose the receipt");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public Task LongRefundCompletedDuringWalletStaging_IsDeferredAndPersisted() => RefundDuringStaging(false);

#if UNITY_EDITOR
        [Test]
#endif
        public Task BigRefundCompletedDuringWalletStaging_IsDeferredAndPersisted() => RefundDuringStaging(true);

        private static async Task RefundDuringStaging(bool big)
        {
            using var files = new Files();
            var store = new ReentrantStore(files.Open());
            var validator = new PendingValidator();
            var bus = new RecordingSignalBus();
            using var context = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindInstance<IPlayerPrefsService>(store);
                builder.BindInstance<INetworkEconomyValidator>(validator);
                builder.BindInstance<ISignalBus>(bus);
            });
            using var economy = new EconomyService();
            context.Context.Container.Inject(economy);
            var refunded = new TaskCompletionSource<SpendRefundedSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = bus.Subscribe<SpendRefundedSignal>(signal => refunded.TrySetResult(signal));
            if (big) economy.SetBigBalance("coin", new BigDouble(100));
            else economy.SetBalance("coin", 100);
            Require(big ? economy.SpendBig("coin", new BigDouble(25), "pending") : economy.Spend("coin", 25, "pending"),
                "start a spend whose server decision is still pending");
            store.BeforeSet = () => validator.Decision.SetResult(false); // inline continuation inside SetString
            try { economy.EarnOnce("coin", 50, "reward"); }
            catch (IOException) { } // a racing refund invalidates confirmation; retry after its completion below
            var completed = await Task.WhenAny(refunded.Task, Task.Delay(3000));
            Require(ReferenceEquals(completed, refunded.Task),
                $"a rejection during wallet staging must not be discarded: big={big}, balance={economy.GetBalance("coin")}");
            var signal = await refunded.Task;
            Require(!economy.EarnOnce("coin", 50, "reward"), "retry confirms the already-applied receipt after reconciliation");
            var reopened = files.Economy(files.Open());
            Require(signal.Amount == 25 && signal.Reason == "pending" && bus.Count<SpendRefundedSignal>() == 1
                && economy.GetBalance("coin") == 150 && reopened.GetBalance("coin") == 150
                && !reopened.EarnOnce("coin", 50, "reward"),
                "exactly one refund and the receipt must remain durable after the delayed reconciliation");
        }

        private static void ExpectIO(Action action) => Expect<IOException>(action);

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Clock : ITimeProvider { public float Now => 0; }

        private sealed class PendingValidator : INetworkBigEconomyValidator
        {
            public readonly TaskCompletionSource<bool> Decision = new TaskCompletionSource<bool>();
            public Task<bool> ValidateSpendAsync(string currencyId, long amount, string reason) => Decision.Task;
            public Task ValidateEarnAsync(string currencyId, long amount, string reason) => Task.CompletedTask;
            public Task<bool> ValidateSpendBigAsync(string currencyId, BigDouble amount, string reason) => Decision.Task;
            public Task ValidateEarnBigAsync(string currencyId, BigDouble amount, string reason) => Task.CompletedTask;
        }

        private sealed class ReentrantStore : IPlayerPrefsService
        {
            private readonly IPlayerPrefsService _inner;
            public Action BeforeSet;
            public ReentrantStore(IPlayerPrefsService inner) => _inner = inner;
            public int GetInt(string key, int defaultValue = 0) => _inner.GetInt(key, defaultValue);
            public void SetInt(string key, int value) => _inner.SetInt(key, value);
            public bool GetBool(string key, bool defaultValue = false) => _inner.GetBool(key, defaultValue);
            public void SetBool(string key, bool value) => _inner.SetBool(key, value);
            public string GetString(string key, string defaultValue = "") => _inner.GetString(key, defaultValue);
            public void SetString(string key, string value)
            {
                var callback = BeforeSet;
                BeforeSet = null;
                callback?.Invoke();
                _inner.SetString(key, value);
            }
            public float GetFloat(string key, float defaultValue = 0) => _inner.GetFloat(key, defaultValue);
            public void SetFloat(string key, float value) => _inner.SetFloat(key, value);
            public long GetLong(string key, long defaultValue = 0) => _inner.GetLong(key, defaultValue);
            public void SetLong(string key, long value) => _inner.SetLong(key, value);
            public bool HasKey(string key) => _inner.HasKey(key);
            public void DeleteKey(string key) => _inner.DeleteKey(key);
            public StorageReadStatus TryGetString(string key, out string value) => _inner.TryGetString(key, out value);
            public void Save() => _inner.Save();
            public bool TrySave() => _inner.TrySave();
        }

        private sealed class AfterReplaceStore : EncryptedStorageService
        {
            public bool ThrowAfterReplace;
            public AfterReplaceStore(string salt) : base(salt) { }
            protected override void ReplaceCurrentFile(string stagedPath, string currentPath, string backupPath)
            {
                base.ReplaceCurrentFile(stagedPath, currentPath, backupPath);
                if (!ThrowAfterReplace) return;
                ThrowAfterReplace = false;
                throw new IOException("injected interruption after the complete file replacement");
            }
        }

        private sealed class Files : IDisposable
        {
            private readonly string _salt = "350_wallet_" + Guid.NewGuid().ToString("N");
            private readonly List<EncryptedStorageService> _stores = new List<EncryptedStorageService>();
            private readonly List<EconomyService> _economies = new List<EconomyService>();
            public EncryptedStorageService Open() => Track(new EncryptedStorageService(_salt));
            public AfterReplaceStore OpenAfterReplace() => Track(new AfterReplaceStore(_salt));
            private T Track<T>(T store) where T : EncryptedStorageService { _stores.Add(store); return store; }
            public EconomyService Economy(IPlayerPrefsService store)
            {
                var economy = new EconomyService { PlayerPrefsService = store };
                _economies.Add(economy);
                return economy;
            }
            public static string PathFor(EncryptedStorageService store, string key)
                => (string)typeof(EncryptedStorageService).GetMethod("GetFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(store, new object[] { key }); // fault injection only; verify balances through the public economy
            public void Dispose()
            {
                foreach (var economy in _economies) economy.Dispose();
                foreach (var store in _stores) store.Dispose();
                string root = Path.GetFullPath(UnityEngine.Application.persistentDataPath) + Path.DirectorySeparatorChar;
                string folder = Path.GetFullPath(Path.Combine(root, "SecureData_" + _salt));
                Require(folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(folder) == "SecureData_" + _salt,
                    "cleanup must stay inside the unique fixture");
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                UnityEngine.PlayerPrefs.DeleteKey("NT_StorageSeed_" + _salt);
                UnityEngine.PlayerPrefs.Save();
            }
        }
    }
}
