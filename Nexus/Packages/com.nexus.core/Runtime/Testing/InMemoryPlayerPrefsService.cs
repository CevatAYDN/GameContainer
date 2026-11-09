using System;
using System.Collections.Generic;
using Nexus.Core.Services;

namespace Nexus.Core
{
    /// <summary>
    /// <see cref="IPlayerPrefsService"/> in memory, stored as <see cref="UnityPlayerPrefsService"/> stores it (a bool as an
    /// int, a long as an invariant string), so a value read back under another type answers as on a device.
    /// Save/TrySave copy live values to a durable snapshot; Reopen drops uncommitted changes (#351).
    /// This single-threaded test store models a failed whole save, not partial commits across encrypted files.
    /// </summary>
    public sealed class InMemoryPlayerPrefsService : IPlayerPrefsService
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _durable = new Dictionary<string, object>();
        private readonly HashSet<string> _unreadable = new HashSet<string>();
        private readonly HashSet<string> _durableUnreadable = new HashSet<string>();
        private int _failedSavesRemaining;

        /// <summary>How many times <see cref="Save"/> was called.</summary>
        public int Saves { get; private set; }

        public IReadOnlyCollection<string> Keys => _values.Keys;

        private T Get<T>(string key, T defaultValue) =>
            !_unreadable.Contains(key) && _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        private void Set(string key, object value)
        {
            if (value == null) _values.Remove(key);
            else _values[key] = value;
            _unreadable.Remove(key);
        }

        public int GetInt(string key, int defaultValue = 0) => Get(key, defaultValue);
        public void SetInt(string key, int value) => Set(key, value);
        public bool GetBool(string key, bool defaultValue = false) => GetInt(key, defaultValue ? 1 : 0) == 1;
        public void SetBool(string key, bool value) => SetInt(key, value ? 1 : 0);
        public string GetString(string key, string defaultValue = "") => Get(key, defaultValue);
        public void SetString(string key, string value) => Set(key, value);
        public float GetFloat(string key, float defaultValue = 0f) => Get(key, defaultValue);
        public void SetFloat(string key, float value) => Set(key, value);
        public long GetLong(string key, long defaultValue = 0L) =>
            IntegerStorageCodec.TryDecode(GetString(key, null), out long value) ? value : defaultValue;
        public void SetLong(string key, long value) => SetString(key, IntegerStorageCodec.Encode(value));
        public StorageReadStatus TryGetString(string key, out string value)
        {
            value = null;
            if (_unreadable.Contains(key)) return StorageReadStatus.Unreadable;
            value = GetString(key, null);
            return value != null ? StorageReadStatus.Present : StorageReadStatus.Absent;
        }

        public bool HasKey(string key) => _values.ContainsKey(key) || _unreadable.Contains(key);
        public void DeleteKey(string key) => Set(key, null);

        public void Save()
        {
            Saves++;
            TrySave();
        }

        public bool TrySave()
        {
            if (_failedSavesRemaining > 0)
            {
                _failedSavesRemaining--;
                return false;
            }
            _durable.Clear();
            foreach (var pair in _values) _durable.Add(pair.Key, pair.Value);
            _durableUnreadable.Clear();
            _durableUnreadable.UnionWith(_unreadable);
            return true;
        }

        /// <summary>The next count Save/TrySave attempts commit nothing; later attempts can succeed.</summary>
        public void FailNextSaves(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            _failedSavesRemaining = count;
        }

        /// <summary>Simulates inaccessible/corrupt data, including after Reopen, without discarding its value.</summary>
        public void MarkUnreadable(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A storage key is required.", nameof(key));
            _unreadable.Add(key);
            _durableUnreadable.Add(key);
        }

        /// <summary>Restores access to the existing live and durable values without saving pending changes.</summary>
        public void Restore(string key)
        {
            _unreadable.Remove(key);
            _durableUnreadable.Remove(key);
        }

        /// <summary>A separate store containing only successfully saved state, with no pending failure injections.</summary>
        public InMemoryPlayerPrefsService Reopen()
        {
            var reopened = new InMemoryPlayerPrefsService();
            foreach (var pair in _durable)
            {
                reopened._values.Add(pair.Key, pair.Value);
                reopened._durable.Add(pair.Key, pair.Value);
            }
            reopened._unreadable.UnionWith(_durableUnreadable);
            reopened._durableUnreadable.UnionWith(_durableUnreadable);
            return reopened;
        }
    }
}
