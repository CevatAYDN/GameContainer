using System.Collections.Generic;
using Nexus.Core.Services;

namespace Nexus.Core
{
    /// <summary>
    /// <see cref="IPlayerPrefsService"/> in memory, stored as <see cref="UnityPlayerPrefsService"/> stores it (a bool as an
    /// int, a long as an invariant string), so a value read back under another type answers as on a device. Two contexts
    /// given the same instance share one save: a quit and relaunch in a test. Issue #74.
    /// </summary>
    public sealed class InMemoryPlayerPrefsService : IPlayerPrefsService
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        /// <summary>How many times <see cref="Save"/> was called.</summary>
        public int Saves { get; private set; }

        public IReadOnlyCollection<string> Keys => _values.Keys;

        private T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public int GetInt(string key, int defaultValue = 0) => Get(key, defaultValue);
        public void SetInt(string key, int value) => _values[key] = value;
        public bool GetBool(string key, bool defaultValue = false) => GetInt(key, defaultValue ? 1 : 0) == 1;
        public void SetBool(string key, bool value) => SetInt(key, value ? 1 : 0);
        public string GetString(string key, string defaultValue = "") => Get(key, defaultValue);
        public void SetString(string key, string value) => _values[key] = value;
        public float GetFloat(string key, float defaultValue = 0f) => Get(key, defaultValue);
        public void SetFloat(string key, float value) => _values[key] = value;
        public long GetLong(string key, long defaultValue = 0L) =>
            IntegerStorageCodec.TryDecode(GetString(key, null), out long value) ? value : defaultValue;
        public void SetLong(string key, long value) => SetString(key, IntegerStorageCodec.Encode(value));
        public bool HasKey(string key) => _values.ContainsKey(key);
        public void DeleteKey(string key) => _values.Remove(key);
        public void Save() => Saves++;
    }
}
