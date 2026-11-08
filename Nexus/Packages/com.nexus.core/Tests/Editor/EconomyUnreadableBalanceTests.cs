using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
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
    // The host compiles this same fixture with the real economy/storage sources.
    // Reflection only locates a unique fixture's encrypted file for fault injection.
    public sealed class EconomyUnreadableBalanceTests
    {
        private const string LongKey = "NT_Eco_coin";
        private const string BigKey = "NT_EcoBig_coin";

#if UNITY_EDITOR
        [Test]
#endif
        public void LongReadFault_ThenDispose_PreservesTheOriginal1000AndBytes()
        {
            using var files = new Files();
            var writer = files.Open();
            writer.SetLong(LongKey, 1000);
            writer.SaveOrThrow();
            files.Close(writer);
            string path = Files.PathFor(writer, LongKey);
            string before = Bytes(path);
            var store = files.Open();
            var economy = files.Economy(store);
            long observed = -1;
            bool threw;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                threw = ThrowsIO(() => observed = economy.GetBalance("coin"));
            economy.Dispose();
            long durable = files.Open().GetLong(LongKey, -1);
            bool unchanged = Bytes(path) == before;
            Require(threw && durable == 1000 && unchanged,
                $"unavailable read must not become a persisted zero: threw={threw}, observed={observed}, durable={durable}, bytesUnchanged={unchanged}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CanonicalBigReadFault_PreservesCanonicalAndReadableLegacy()
        {
            foreach (bool longView in new[] { false, true })
            {
                using var files = new Files();
                var writer = files.Open();
                writer.SetLong(LongKey, 900);
                writer.SetBigDouble(BigKey, new BigDouble(1.25, 30));
                writer.SaveOrThrow();
                files.Close(writer);
                string path = Files.PathFor(writer, BigKey);
                string canonicalBefore = Bytes(path), legacyBefore = Bytes(Files.PathFor(writer, LongKey));
                var store = files.Open();
                var economy = files.Economy(store);
                bool threw;
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    threw = ThrowsIO(() => { if (longView) economy.GetBalance("coin"); else economy.GetBigBalance("coin"); });
                economy.Dispose();
                var reopened = files.Open();
                bool unchanged = Bytes(path) == canonicalBefore && Bytes(Files.PathFor(writer, LongKey)) == legacyBefore;
                Require(threw && unchanged && reopened.GetBigDouble(BigKey) == new BigDouble(1.25, 30)
                    && reopened.GetLong(LongKey, -1) == 900,
                    $"unreadable canonical key must not destroy recovery data: longView={longView}, threw={threw}, bytesUnchanged={unchanged}, legacyPresent={reopened.HasKey(LongKey)}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CorruptLongRead_ThenDispose_PreservesEvidenceInsteadOfWritingZero()
        {
            using var files = new Files();
            var writer = files.Open();
            writer.SetLong(LongKey, 1000);
            writer.SaveOrThrow();
            files.Close(writer);
            string path = Files.PathFor(writer, LongKey);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            string corrupt = Bytes(path);
            var economy = files.Economy(files.Open());
            bool threw = ThrowsIO(() => economy.GetBalance("coin"));
            economy.Dispose();
            bool unchanged = Bytes(path) == corrupt;
            Require(threw && unchanged, $"corrupt balance must not be rewritten as zero: threw={threw}, bytesUnchanged={unchanged}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UnavailableBalances_RejectEveryReadAndMutation_WithoutChangingCanonicalOrLegacy()
        {
            var operations = new (string Name, Action<EconomyService> Run)[]
            {
                ("GetBalance", e => e.GetBalance("coin")),
                ("GetBigBalance", e => e.GetBigBalance("coin")),
                ("GetObservableBalance", e => e.GetObservableBalance("coin")),
                ("GetObservableBigBalance", e => e.GetObservableBigBalance("coin")),
                ("CanAfford", e => e.CanAfford("coin", 1)),
                ("CanAffordBig", e => e.CanAffordBig("coin", BigDouble.One)),
                ("Earn", e => e.Earn("coin", 1)),
                ("Spend", e => e.Spend("coin", 1)),
                ("SpendWithRefusal", e => e.Spend("coin", 1, out _)),
                ("SetBalance", e => e.SetBalance("coin", 0)),
                ("EarnBig", e => e.EarnBig("coin", BigDouble.One)),
                ("SpendBig", e => e.SpendBig("coin", BigDouble.One)),
                ("SpendBigWithRefusal", e => e.SpendBig("coin", BigDouble.One, out _)),
                ("SetBigBalance", e => e.SetBigBalance("coin", BigDouble.Zero))
            };
            foreach (bool big in new[] { false, true })
                foreach (bool corrupt in new[] { false, true })
                    foreach (var operation in operations)
                    {
                        using var files = new Files();
                        var seed = Seed(files, big);
                        string path = Files.PathFor(seed, big ? BigKey : LongKey);
                        if (corrupt) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                        string canonicalBefore = Bytes(path), legacyBefore = Bytes(Files.PathFor(seed, LongKey));
                        var economy = files.Economy(files.Open());
                        bool threw;
                        if (corrupt) threw = ThrowsIO(() => operation.Run(economy));
                        else
                        {
                            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                                threw = ThrowsIO(() => operation.Run(economy));
                        }
                        economy.Dispose();
                        Require(threw && Bytes(path) == canonicalBefore && Bytes(Files.PathFor(seed, LongKey)) == legacyBefore,
                            $"{operation.Name}: unavailable {(big ? "big" : "long")} {(corrupt ? "corrupt" : "locked")} balance must throw without mutation");
                    }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void TransientRecovery_RetriesSameEconomyInstance_AndResumesNormalMutation()
        {
            foreach (bool big in new[] { false, true })
            {
                using var files = new Files();
                var seed = Seed(files, big);
                var economy = files.Economy(files.Open());
                using (File.Open(Files.PathFor(seed, big ? BigKey : LongKey), FileMode.Open, FileAccess.Read, FileShare.None))
                    Require(ThrowsIO(() => { if (big) economy.GetBigBalance("coin"); else economy.GetBalance("coin"); }), "read fault must be observable");
                ResumeAndVerify(files, economy, big);
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CorruptImportRecovery_RetriesSameEconomyInstance_AndResumesNormalMutation()
        {
            foreach (bool big in new[] { false, true })
            {
                using var files = new Files();
                var seed = Seed(files, big);
                string key = big ? BigKey : LongKey;
                var backupReader = files.Open();
                string payload = backupReader.ExportEncryptedSaveData(key);
                Require(payload != null, "fixture needs a valid supported export");
                files.Close(backupReader);
                File.WriteAllBytes(Files.PathFor(seed, key), new byte[] { 1, 2, 3 });
                var store = files.Open();
                var economy = files.Economy(store);
                Require(ThrowsIO(() => economy.GetBalance("coin")), "corruption must not become cached zero");
                Require(store.ImportEncryptedSaveData(key, payload), "supported import must recover the same store");
                ResumeAndVerify(files, economy, big);
            }
        }

        private static void ResumeAndVerify(Files files, EconomyService economy, bool big)
        {
            if (big)
            {
                Require(economy.GetBigBalance("coin") == new BigDouble(1000.5), "retry must load the original canonical big amount");
                economy.EarnBig("coin", new BigDouble(25));
                Require(economy.SpendBig("coin", new BigDouble(5)), "recovered big spend must succeed");
                Require(files.Open().GetBigDouble(BigKey) == new BigDouble(1020.5), "recovered big mutations must persist");
            }
            else
            {
                Require(economy.GetBalance("coin") == 1000, "retry must load the original long amount");
                economy.Earn("coin", 25);
                Require(economy.Spend("coin", 5), "recovered long spend must succeed");
                Require(files.Open().GetLong(LongKey) == 1020, "recovered long mutations must persist");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void HealthyCurrencyAndSharedThrottlerFlush_DoNotPersistFailedLoads()
        {
            foreach (bool big in new[] { false, true })
                foreach (bool corrupt in new[] { false, true })
                    foreach (bool deferred in new[] { false, true })
                    {
                        using var files = new Files();
                        var seed = Seed(files, big);
                        string path = Files.PathFor(seed, big ? BigKey : LongKey);
                        if (corrupt) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                        string canonicalBefore = Bytes(path), legacyBefore = Bytes(Files.PathFor(seed, LongKey));
                        var throttler = deferred ? new SaveThrottler { TimeProvider = new Clock() } : null;
                        var economy = files.Economy(files.Open(), throttler);
                        economy.Earn("healthy", 1); // prime the real throttler's economy slot
                        bool threw;
                        if (corrupt) threw = ThrowsIO(() => economy.GetBalance("coin"));
                        else
                        {
                            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                                threw = ThrowsIO(() => economy.GetBalance("coin"));
                        }
                        economy.Earn("healthy", 6);
                        int peerFlushes = 0;
                        if (throttler != null)
                        {
                            throttler.TryRequestSave("peer", () => peerFlushes++);
                            throttler.TryRequestSave("peer", () => peerFlushes++);
                            throttler.Flush();
                            Require(peerFlushes == 2, "shared throttler must also flush the independent owner");
                        }
                        economy.Dispose();
                        throttler?.OnDispose();
                        Require(threw && Bytes(path) == canonicalBefore && Bytes(Files.PathFor(seed, LongKey)) == legacyBefore,
                            "healthy currency/shared flush/disposal must not rewrite a failed load or delete its legacy recovery key");
                        Require(files.Open().GetLong("NT_Eco_healthy") == 7, "the healthy currency must still persist");
                    }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void MalformedCanonicalNumbers_ThrowAndPreserveStoredValues()
        {
            foreach (bool big in new[] { false, true })
                foreach (string malformed in big
                    ? new[] { "", "bad", "NaN;0", "Infinity;0", "1;bad", "1;9223372036854775808", "1;2;3" }
                    : new[] { "", "bad", "1000.5", "9223372036854775808" })
                {
                    using var files = new Files();
                    var seed = files.Open();
                    seed.SetLong(LongKey, 900);
                    seed.SetString(big ? BigKey : LongKey, malformed);
                    seed.SaveOrThrow();
                    files.Close(seed);
                    string path = Files.PathFor(seed, big ? BigKey : LongKey);
                    string before = Bytes(path), legacyBefore = Bytes(Files.PathFor(seed, LongKey));
                    var economy = files.Economy(files.Open());
                    Require(ThrowsIO(() => economy.GetBalance("coin")) && ThrowsIO(() => economy.GetBigBalance("coin")),
                        "present but undecodable canonical values are not an authoritative zero: " + malformed);
                    economy.Dispose();
                    Require(Bytes(path) == before && Bytes(Files.PathFor(seed, LongKey)) == legacyBefore,
                        "malformed canonical values and legacy recovery must not change");
                }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AbsentAndHealthyBalances_RetainNormalPersistenceAndPromotion()
        {
            using var files = new Files();
            var economy = files.Economy(files.Open());
            Require(economy.GetBalance("coin") == 0 && economy.GetBigBalance("idle") == BigDouble.Zero, "absent balances still start at zero");
            economy.Earn("coin", 1000);
            Require(economy.Spend("coin", 25), "healthy spend must succeed");
            economy.SetBigBalance("idle", new BigDouble(1.25, 30));
            Require(economy.GetBalance("idle") == long.MaxValue, "large canonical projection remains saturated");
            economy.Dispose();
            var reopened = files.Economy(files.Open());
            Require(reopened.GetBalance("coin") == 975 && reopened.GetBigBalance("idle") == new BigDouble(1.25, 30),
                "healthy balances must round-trip");
            Require(reopened.GetBigBalance("coin") == new BigDouble(975), "promotion must preserve the live long balance");
            reopened.EarnBig("coin", new BigDouble(25));
            Require(reopened.GetBalance("coin") == 1000 && files.Open().GetBigDouble(BigKey) == new BigDouble(1000),
                "promoted mutations must remain canonical");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NativeTypedStores_DoNotRequireFrameworkStringEncoding()
        {
            foreach (bool stringProjection in new[] { false, true })
            {
                var store = new TypedStore { StringProjection = stringProjection };
                store.SetLong(LongKey, 1000);
                store.SetBigDouble("NT_EcoBig_idle", new BigDouble(1.25, 3));
                using var economy = new EconomyService { PlayerPrefsService = store };
                Require(economy.GetBalance("coin") == 1000 && economy.GetBigBalance("idle") == new BigDouble(1.25, 3),
                    "native typed getters own decoding even when string projection is missing or opaque");
                economy.Earn("coin", 5);
                economy.EarnBig("idle", new BigDouble(2.5, 2));
                Require(store.GetLong(LongKey) == 1005 && store.GetBigDouble("NT_EcoBig_idle") == new BigDouble(1.5, 3),
                    "custom typed persistence must remain compatible");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void LegacyLocaleAndSentinelValues_RetainExistingNumericDecoding()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                culture.NumberFormat.PositiveSign = "p";
                CultureInfo.CurrentCulture = culture;
                IPlayerPrefsService store = new InMemoryPlayerPrefsService();
                store.SetString("NT_Eco_locale", "p1000");
                store.SetString("NT_Eco_legacySign", "\u22121000");
                store.SetLong("NT_Eco_minimum", long.MinValue);
                store.SetLong("NT_Eco_maximum", long.MaxValue);
                using var economy = new EconomyService { PlayerPrefsService = store };
                Require(economy.GetBalance("locale") == 1000, "current-culture integer compatibility must remain");
                Require(economy.GetBalance("legacySign") == 0 && economy.GetBalance("minimum") == 0,
                    "valid stored negatives keep the existing nonnegative clamp, including the fallback sentinel");
                Require(economy.GetBalance("maximum") == long.MaxValue, "long.MaxValue must not collide with fallback detection");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void InvalidAmountsAndDisposedRefusals_RemainIndependentOfUnreadableStorage()
        {
            using var files = new Files();
            var seed = Seed(files, false);
            File.WriteAllBytes(Files.PathFor(seed, LongKey), new byte[] { 1, 2, 3 });
            var economy = files.Economy(files.Open());
            ExpectArgument(() => economy.Earn("coin", 0));
            ExpectArgument(() => economy.Spend("coin", -1));
            ExpectArgument(() => economy.SetBalance("coin", -1));
            ExpectArgument(() => economy.EarnBig("coin", BigDouble.Zero));
            ExpectArgument(() => economy.SpendBig("coin", new BigDouble { Mantissa = double.NaN }));
            ExpectArgument(() => economy.SetBigBalance("coin", new BigDouble { Mantissa = double.PositiveInfinity }));
            Require(economy.CanAfford("coin", 0) && economy.CanAffordBig("coin", BigDouble.Zero), "zero-cost checks remain true without loading");
            Require(!economy.Spend(null, 1, out var invalid) && invalid == EconomyRefusal.InvalidCurrencyId, "invalid-currency refusal must remain");
            economy.Dispose();
            Require(!economy.Spend("coin", 1, out var disposed) && disposed == EconomyRefusal.Disposed, "disposed spend must not try loading storage");
            Require(!economy.SpendBig("coin", BigDouble.One, out disposed) && disposed == EconomyRefusal.Disposed, "disposed big spend must keep its refusal");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void LoadedBigBalance_RemainsAuthoritativeForItsLongViewDuringAReadFault()
        {
            var store = new TypedStore();
            store.SetBigDouble(BigKey, new BigDouble(1.25, 3));
            var throttler = new SaveThrottler { TimeProvider = new Clock() };
            throttler.ForceSave("economy", () => { }); // mutations stay deferred during the injected read fault
            using var economy = new EconomyService { PlayerPrefsService = store, SaveThrottler = throttler };
            Require(economy.GetBigBalance("coin") == new BigDouble(1.25, 3), "load the healthy big balance first");
            int faultedReads = 0;
            store.BeforeStringRead = key =>
            {
                faultedReads++;
                throw new IOException("loaded balance must not re-read storage: " + key);
            };
            try
            {
                Require(economy.GetBalance("coin") == 1250, "long projection must use the already-loaded big balance");
                economy.EarnBig("coin", new BigDouble(2.5, 2));
                Require(economy.GetBalance("coin") == 1500, "loaded projection must remain live without re-reading storage");
                Require(economy.GetBigBalance("coin") == new BigDouble(1.5, 3) && faultedReads == 0,
                    "creating the first long view and earning big must not touch unreadable storage");
            }
            finally { store.BeforeStringRead = null; }
            throttler.Flush();
            Require(store.GetBigDouble(BigKey) == new BigDouble(1.5, 3), "healthy cached mutations must persist after access returns");
            throttler.OnDispose();
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void EconomyStoreOverride_UnreadableBalancePreservesBothStores_AndRetriesAfterImport()
        {
            foreach (bool big in new[] { false, true })
            {
                using var files = new Files();
                var seed = Seed(files, big);
                string key = big ? BigKey : LongKey;
                var backup = files.Open();
                string payload = backup.ExportEncryptedSaveData(key);
                Require(payload != null, "override fixture needs a valid supported export");
                files.Close(backup);
                string path = Files.PathFor(seed, key);
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                string canonicalBefore = Bytes(path), legacyBefore = Bytes(Files.PathFor(seed, LongKey));
                var fallback = new TypedStore();
                fallback.SetLong(LongKey, 7000);
                fallback.SetBigDouble(BigKey, new BigDouble(1.75, 3));
                int fallbackReads = 0;
                fallback.BeforeStringRead = _ => fallbackReads++;
                var store = files.Open();
                var settings = new SettingsStores { Economy = store };
                var economy = files.Economy(fallback);
                economy.SettingsStores = settings;
                Require(ThrowsIO(() => economy.GetBalance("coin")) && ThrowsIO(() => economy.GetBigBalance("coin"))
                    && ThrowsIO(() => economy.Earn("coin", 1)) && ThrowsIO(() => economy.EarnBig("coin", BigDouble.One)),
                    "an unreadable override must reject reads and mutations, not load the healthy fallback");
                economy.Earn("healthy", 7);
                var failed = files.Economy(fallback);
                failed.SettingsStores = settings;
                Require(ThrowsIO(() => failed.GetBalance("coin")), "a second override load must also fail closed");
                failed.Dispose();
                Require(Bytes(path) == canonicalBefore && Bytes(Files.PathFor(seed, LongKey)) == legacyBefore,
                    "healthy flush and failed-load disposal must preserve override canonical and legacy evidence");
                RequireFallbackUnchanged();
                Require(store.ImportEncryptedSaveData(key, payload), "supported import must recover the override store");
                ResumeAndVerify(files, economy, big);
                economy.Dispose();
                Require(files.Open().GetLong("NT_Eco_healthy") == 7, "healthy currency must persist only in the override");
                RequireFallbackUnchanged();

                void RequireFallbackUnchanged()
                {
                    Require(fallbackReads == 0 && fallback.GetLong(LongKey) == 7000
                        && fallback.GetBigDouble(BigKey) == new BigDouble(1.75, 3)
                        && fallback.HasKey(LongKey) && !fallback.HasKey("NT_Eco_healthy"),
                        "override failures, recovery and disposal must not read, write or delete fallback currency data");
                }
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void SlowFirstTouch_DoesNotHoldTheBalanceLock()
        {
            var store = new TypedStore();
            store.SetLong("NT_Eco_ready", 100);
            store.SetLong("NT_Eco_slow", 1000);
            using var economy = new EconomyService { PlayerPrefsService = store };
            Require(economy.GetBalance("ready") == 100, "load unrelated healthy currency");
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            store.BeforeStringRead = key =>
            {
                if (key != "NT_Eco_slow") return;
                entered.Set();
                Require(resume.Wait(TimeSpan.FromSeconds(10)), "fixture reader was not released");
            };
            var reader = Task.Run(() => economy.GetBalance("slow"));
            Task mutation = null;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(10)), "first-touch reader never entered storage");
                mutation = Task.Run(() => economy.Earn("ready", 1));
                Require(mutation.Wait(TimeSpan.FromSeconds(5)), "slow first-touch storage must not block a loaded currency mutation");
            }
            finally
            {
                resume.Set();
                reader.GetAwaiter().GetResult();
                mutation?.GetAwaiter().GetResult();
            }
            Require(reader.Result == 1000 && economy.GetBalance("ready") == 101, "both operations must finish with correct balances");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void RecoveredConcurrentMutations_AndReentrantSubscribers_PreserveBalances()
        {
            using var files = new Files();
            var seed = Seed(files, false);
            var throttler = new SaveThrottler { TimeProvider = new Clock() };
            throttler.ForceSave("economy", () => { });
            var economy = files.Economy(files.Open(), throttler);
            using (File.Open(Files.PathFor(seed, LongKey), FileMode.Open, FileAccess.Read, FileShare.None))
                Require(ThrowsIO(() => economy.GetBalance("coin")), "read fault must be observable");
            var balance = economy.GetObservableBalance("coin");
            economy.GetBalance("bonus");
            int notifications = 0, reentrantMismatches = 0;
            balance.OnChanged((_, value) =>
            {
                notifications++;
                if (economy.GetBalance("coin") != value) reentrantMismatches++;
                if (value % 100 == 0) economy.Earn("bonus", 1);
            });
            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Run(() => { for (int j = 0; j < 100; j++) economy.Earn("coin", 1); });
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            Require(economy.GetBalance("coin") == 1800 && economy.GetBalance("bonus") == 8 && notifications == 800 && reentrantMismatches == 0,
                "concurrent mutations and reentrant callbacks must preserve exact totals");
            throttler.Flush();
            Require(files.Open().GetLong(LongKey) == 1800, "recovered concurrent balance must persist");
            throttler.OnDispose();
        }

        private static EncryptedStorageService Seed(Files files, bool big)
        {
            var writer = files.Open();
            writer.SetLong(LongKey, big ? 900 : 1000);
            if (big) writer.SetBigDouble(BigKey, new BigDouble(1000.5));
            writer.SaveOrThrow();
            files.Close(writer);
            return writer;
        }

        private static void ExpectArgument(Action action)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { return; }
            throw new InvalidOperationException("invalid amount must retain its argument exception");
        }

        private static bool ThrowsIO(Action action)
        {
            try { action(); return false; }
            catch (IOException) { return true; }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string Bytes(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : "<absent>";

        private sealed class Clock : ITimeProvider { public float Now => 0; }

        private sealed class TypedStore : IPlayerPrefsService
        {
            private readonly ConcurrentDictionary<string, object> _values = new ConcurrentDictionary<string, object>();
            public bool StringProjection;
            public Action<string> BeforeStringRead;
            private T Read<T>(string key, T fallback) => _values.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
            public int GetInt(string key, int defaultValue = 0) => Read(key, defaultValue);
            public void SetInt(string key, int value) => _values[key] = value;
            public bool GetBool(string key, bool defaultValue = false) => Read(key, defaultValue);
            public void SetBool(string key, bool value) => _values[key] = value;
            public string GetString(string key, string defaultValue = "")
            {
                BeforeStringRead?.Invoke(key);
                return StringProjection && HasKey(key) ? "opaque-native-value" : Read(key, defaultValue);
            }
            public void SetString(string key, string value) => _values[key] = value;
            public float GetFloat(string key, float defaultValue = 0) => Read(key, defaultValue);
            public void SetFloat(string key, float value) => _values[key] = value;
            public long GetLong(string key, long defaultValue = 0) => Read(key, defaultValue);
            public void SetLong(string key, long value) => _values[key] = value;
            public BigDouble GetBigDouble(string key, BigDouble defaultValue = default) => Read(key, defaultValue);
            public void SetBigDouble(string key, BigDouble value) => _values[key] = value;
            public bool HasKey(string key) => _values.ContainsKey(key);
            public void DeleteKey(string key) => _values.TryRemove(key, out _);
            public void Save() { }
        }

        private sealed class Files : IDisposable
        {
            private readonly string _salt = "339_economy_" + Guid.NewGuid().ToString("N");
            private readonly List<EncryptedStorageService> _stores = new List<EncryptedStorageService>();
            private readonly List<EconomyService> _economies = new List<EconomyService>();
            public EncryptedStorageService Open()
            {
                var store = new EncryptedStorageService(_salt);
                _stores.Add(store);
                return store;
            }
            public EconomyService Economy(IPlayerPrefsService store, ISaveThrottler throttler = null)
            {
                var economy = new EconomyService { PlayerPrefsService = store, SaveThrottler = throttler };
                _economies.Add(economy);
                return economy;
            }
            public void Close(EncryptedStorageService store) => store.Dispose();
            public static string PathFor(EncryptedStorageService store, string key)
                => (string)typeof(EncryptedStorageService).GetMethod("GetFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(store, new object[] { key });
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
