#if NEXUS_FIREBASE_REMOTE_CONFIG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.RemoteConfig;
using UnityEngine;
using FirebaseAppSdk = global::Firebase.FirebaseApp;
using FirebaseConfigSdk = global::Firebase.RemoteConfig.FirebaseRemoteConfig;
using FirebaseConfigValue = global::Firebase.RemoteConfig.ConfigValue;
using FirebaseDependencyStatus = global::Firebase.DependencyStatus;
using FirebaseValueSource = global::Firebase.RemoteConfig.ValueSource;

namespace Nexus.Firebase.Sdk
{
    /// <summary>
    /// Firebase Remote Config behind <see cref="IRemoteConfigService"/>. A fetch is made only when <see cref="RemoteConfigFetchPolicy"/> says one
    /// is due; a value the console does not set (Firebase answers with its static default) returns the game's fallback.
    /// </summary>
    public sealed class FirebaseRemoteConfigService : NexusService<IRemoteConfigService>, IRemoteConfigService
    {
        private const string LastFetchKey = "nexus.firebase.remote_config.last_fetch_utc_ticks";

        private readonly Dictionary<string, string> _snapshot = new Dictionary<string, string>();
        private bool _ready;

        public event Action<string> Changed;

        public override async ValueTask InitializeAsync(CancellationToken ct)
        {
            FirebaseDependencyStatus status = await FirebaseAppSdk.CheckAndFixDependenciesAsync();
            if (status != FirebaseDependencyStatus.Available)
            {
                NexusLog.WarnAlways("[Firebase] Remote Config is off: the SDK's dependencies are " + status);
                return;
            }

            _ready = true;
            RaiseChanges(notify: false);
        }

        public async ValueTask<bool> FetchAsync(CancellationToken ct)
        {
            if (!_ready || ct.IsCancellationRequested)
            {
                return false;
            }

            if (!RemoteConfigFetchPolicy.IsFetchDue(LoadLastFetch(), DateTime.UtcNow))
            {
                return true;
            }

            try
            {
                await FirebaseConfigSdk.DefaultInstance.FetchAsync(TimeSpan.Zero);
                await FirebaseConfigSdk.DefaultInstance.ActivateAsync();
            }
            catch (Exception error)
            {
                NexusLog.WarnAlways("[Firebase] Remote Config fetch failed: " + error.Message);
                return false;
            }

            SaveLastFetch(DateTime.UtcNow);
            RaiseChanges(notify: true);
            return true;
        }

        public bool GetBool(string key, bool fallback) => RemoteConfigParser.ToBool(Raw(key), fallback);

        public int GetInt(string key, int fallback) => RemoteConfigParser.ToInt(Raw(key), fallback);

        public double GetDouble(string key, double fallback) => RemoteConfigParser.ToDouble(Raw(key), fallback);

        public string GetString(string key, string fallback) => RemoteConfigParser.ToText(Raw(key), fallback);

        private string Raw(string key)
        {
            if (!_ready)
            {
                return null;
            }

            FirebaseConfigValue value = FirebaseConfigSdk.DefaultInstance.GetValue(key);
            return value.Source == FirebaseValueSource.StaticValue ? null : value.StringValue;
        }

        private void RaiseChanges(bool notify)
        {
            foreach (string key in FirebaseConfigSdk.DefaultInstance.Keys)
            {
                string current = Raw(key);
                bool known = _snapshot.TryGetValue(key, out string previous);
                _snapshot[key] = current;
                if (notify && (!known || previous != current))
                {
                    Changed?.Invoke(key);
                }
            }
        }

        private static DateTime? LoadLastFetch()
        {
            string stored = PlayerPrefs.GetString(LastFetchKey, string.Empty);
            return long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                ? new DateTime(ticks, DateTimeKind.Utc)
                : (DateTime?)null;
        }

        private static void SaveLastFetch(DateTime utcNow)
        {
            PlayerPrefs.SetString(LastFetchKey, utcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
        }
    }
}
#endif