using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Firebase.RemoteConfig;

namespace Nexus.Firebase.Mocks
{
    /// <summary>
    /// Remote values held in memory: the game's fallbacks until a test or a development menu sets a value. A fetch succeeds and changes nothing.
    /// </summary>
    public sealed class MockRemoteConfigService : IRemoteConfigService
    {
        private readonly Dictionary<string, string> _values = new();

        public event Action<string> Changed;

        public ValueTask<bool> FetchAsync(CancellationToken ct)
        {
            return new ValueTask<bool>(true);
        }

        /// <summary>Sets the raw value a key returns, and raises <see cref="Changed"/> when it differs from the one before.</summary>
        public void Set(string key, string raw)
        {
            if (_values.TryGetValue(key, out string previous) && previous == raw)
            {
                return;
            }

            _values[key] = raw;
            Changed?.Invoke(key);
        }

        public bool GetBool(string key, bool fallback) => RemoteConfigParser.ToBool(Raw(key), fallback);

        public int GetInt(string key, int fallback) => RemoteConfigParser.ToInt(Raw(key), fallback);

        public double GetDouble(string key, double fallback) => RemoteConfigParser.ToDouble(Raw(key), fallback);

        public string GetString(string key, string fallback) => RemoteConfigParser.ToText(Raw(key), fallback);

        private string Raw(string key)
        {
            return _values.TryGetValue(key, out string raw) ? raw : null;
        }
    }
}
