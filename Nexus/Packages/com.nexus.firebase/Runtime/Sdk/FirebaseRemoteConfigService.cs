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
        private bool _notReadyReported;
        private bool _disposed;

        // Lazy, instance-local SDK and persistence calls; no DefaultInstance access during construction.
        internal Func<Task<FirebaseDependencyStatus>> CheckDependenciesAsync = () => FirebaseAppSdk.CheckAndFixDependenciesAsync();
        internal Func<TimeSpan, Task> FetchSdkAsync = expiration => FirebaseConfigSdk.DefaultInstance.FetchAsync(expiration);
        internal Func<Task<bool>> ActivateSdkAsync = () => FirebaseConfigSdk.DefaultInstance.ActivateAsync();
        internal Func<IEnumerable<string>> ReadKeys = () => FirebaseConfigSdk.DefaultInstance.Keys;
        internal Func<string, string> ReadRawValue = key =>
        {
            FirebaseConfigValue value = FirebaseConfigSdk.DefaultInstance.GetValue(key);
            return value.Source == FirebaseValueSource.StaticValue ? null : value.StringValue;
        };
        internal Func<DateTime?> ReadLastFetchUtc = () => LoadLastFetch();
        internal Action<DateTime> WriteLastFetchUtc = utcNow => SaveLastFetch(utcNow);

        public event Action<string> Changed;

        public override async ValueTask InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return;
            ct.ThrowIfCancellationRequested();
            FirebaseDependencyStatus status = await CheckDependenciesAsync();
            if (_disposed) return;
            ct.ThrowIfCancellationRequested();
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
            if (_disposed) return false;
            if (!_ready || ct.IsCancellationRequested)
            {
                // once, in every build: the game runs on its fallbacks (issue #171)
                if (!_ready && !_notReadyReported)
                {
                    _notReadyReported = true;
                    NexusLog.WarnAlways("[Firebase] Remote Config cannot fetch: Firebase has not started, so the game's fallbacks stay.");
                }
                return false;
            }

            if (!RemoteConfigFetchPolicy.IsFetchDue(ReadLastFetchUtc(), DateTime.UtcNow))
            {
                return true;
            }

            try
            {
                await FetchSdkAsync(TimeSpan.Zero);
                if (_disposed || ct.IsCancellationRequested) return false;
                await ActivateSdkAsync();
            }
            catch (Exception error)
            {
                if (_disposed || ct.IsCancellationRequested) return false;
                NexusLog.WarnAlways("[Firebase] Remote Config fetch failed: " + error.Message);
                return false;
            }

            if (_disposed || ct.IsCancellationRequested) return false;
            WriteLastFetchUtc(DateTime.UtcNow);
            RaiseChanges(notify: true);
            return true;
        }

        public bool GetBool(string key, bool fallback) => RemoteConfigParser.ToBool(Raw(key), fallback);

        public int GetInt(string key, int fallback) => RemoteConfigParser.ToInt(Raw(key), fallback);

        public double GetDouble(string key, double fallback) => RemoteConfigParser.ToDouble(Raw(key), fallback);

        public string GetString(string key, string fallback) => RemoteConfigParser.ToText(Raw(key), fallback);

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _snapshot.Clear();
        }

        private string Raw(string key)
        {
            if (!_ready)
            {
                return null;
            }

            return ReadRawValue(key);
        }

        private void RaiseChanges(bool notify)
        {
            if (_disposed) return;
            var removed = new HashSet<string>(_snapshot.Keys);
            foreach (string key in ReadKeys())
            {
                if (_disposed) return;
                removed.Remove(key);
                string current = Raw(key);
                bool known = _snapshot.TryGetValue(key, out string previous);
                _snapshot[key] = current;
                if (notify && (!known || previous != current))
                {
                    Changed?.Invoke(key);
                }
            }
            foreach (string key in removed)
            {
                if (_disposed) return;
                _snapshot.Remove(key);
                if (notify) Changed?.Invoke(key);
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
