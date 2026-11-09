using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewStorageOfflineTimeTests
    {
        private const string TimestampKey = "quit";
        private const string MonotonicKey = "quit_MonotonicMs";

#if UNITY_EDITOR
        [Test]
#endif
        public void ST06_UnreadableMonotonic_DoesNotGrantWallClockOnlyReward()
        {
            var store = new InMemoryPlayerPrefsService();
            OfflineTimeCalculator.RecordQuitTimestamp(store, TimestampKey);
            store.SetLong(TimestampKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
            store.SaveOrThrow();
            store.MarkUnreadable(MonotonicKey);
            long reward = OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, TimestampKey);
            bool preserved = store.TryGetString(MonotonicKey, out _) == StorageReadStatus.Unreadable
                && store.Reopen().TryGetString(MonotonicKey, out _) == StorageReadStatus.Unreadable;
            Require(reward == 0 && preserved,
                $"An unreadable saved counter cannot disable monotonic validation: reward={reward}, evidencePreserved={preserved}; expected 0.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST06_MalformedMonotonic_DoesNotGrantWallClockOnlyReward()
        {
            var failures = new List<string>();
            foreach (string malformed in new[] { "", "bad-counter", "9223372036854775808", "-1" })
            {
                var store = new InMemoryPlayerPrefsService();
                store.SetLong(TimestampKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
                store.SetString(MonotonicKey, malformed);
                store.SaveOrThrow();
                long reward = OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, TimestampKey);
                if (reward != 0 || store.Reopen().GetString(MonotonicKey) != malformed)
                    failures.Add($"'{malformed}' granted {reward}");
            }
            Require(failures.Count == 0,
                "Present invalid monotonic data must not be treated as legacy absence: " + string.Join(", ", failures));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST06_MissingLegacyCounter_RetainsTheCappedWallClockReward()
        {
            foreach (string key in new[] { "NT_LastQuitTimestamp", TimestampKey })
            {
                var store = new InMemoryPlayerPrefsService();
                store.SetLong(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
                long reward = OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, key);
                Require(reward == 3600, "A genuinely absent legacy counter must retain capped wall-clock validation.");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST06_ReadableCounters_StillValidateStringAndNativeTypedStores()
        {
            foreach (IPlayerPrefsService store in new IPlayerPrefsService[]
                { new InMemoryPlayerPrefsService(), new NativeLongStore() })
            {
                OfflineTimeCalculator.RecordQuitTimestamp(store, TimestampKey);
                store.SetLong(TimestampKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
                long reward = OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, TimestampKey);
                Require(reward >= 0 && reward < 60,
                    $"A just-recorded counter must clamp the two-hour wall-clock claim, including native typed stores: {reward}.");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST06_Recovery_RetriesTheExistingCounterWithoutResettingIt()
        {
            var store = new InMemoryPlayerPrefsService();
            OfflineTimeCalculator.RecordQuitTimestamp(store, TimestampKey);
            string original = store.GetString(MonotonicKey);
            store.SetLong(TimestampKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
            store.MarkUnreadable(MonotonicKey);
            Require(OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, TimestampKey) == 0,
                "The unreadable counter must first fail closed.");
            store.Restore(MonotonicKey);
            long reward = OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, TimestampKey);
            Require(reward >= 0 && reward < 60 && store.GetString(MonotonicKey) == original,
                "Restored access must resume monotonic validation without replacing the saved counter.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST06_RebootedCounter_RetainsTheCappedWallClockFallback()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong(TimestampKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
            store.SetLong(MonotonicKey, long.MaxValue);
            Require(OfflineTimeCalculator.CalculateOfflineSeconds(store, 3600, TimestampKey) == 3600,
                "A valid saved counter above the current boot counter must keep the documented reboot fallback.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class NativeLongStore : IPlayerPrefsService
        {
            private readonly Dictionary<string, long> _values = new();
            public long GetLong(string key, long defaultValue = 0) => _values.TryGetValue(key, out var value) ? value : defaultValue;
            public void SetLong(string key, long value) => _values[key] = value;
            public string GetString(string key, string defaultValue = "") => defaultValue;
            public bool HasKey(string key) => _values.ContainsKey(key);
            public void DeleteKey(string key) => _values.Remove(key);
            public void Save() { }
            public int GetInt(string key, int defaultValue = 0) => throw new NotSupportedException();
            public void SetInt(string key, int value) => throw new NotSupportedException();
            public bool GetBool(string key, bool defaultValue = false) => throw new NotSupportedException();
            public void SetBool(string key, bool value) => throw new NotSupportedException();
            public void SetString(string key, string value) => throw new NotSupportedException();
            public float GetFloat(string key, float defaultValue = 0) => throw new NotSupportedException();
            public void SetFloat(string key, float value) => throw new NotSupportedException();
        }
    }
}
