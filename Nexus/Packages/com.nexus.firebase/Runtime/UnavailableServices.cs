using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Crash;
using Nexus.Firebase.RemoteConfig;

namespace Nexus.Firebase
{
    /// <summary>What a player binds for <c>com.google.firebase.analytics</c> when it lacks the package: nothing is sent, and
    /// <see cref="FirebaseBindings"/> logged an error at start that names the package (issue #171).</summary>
    public sealed class UnavailableAnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
        public void LogEvent(string eventName) { }

        public void LogEvent(string eventName, Dictionary<string, object> parameters) { }

        public void SetUserProperty(string key, string value) { }
    }

    /// <summary>What a player binds for <c>com.google.firebase.crashlytics</c> when it lacks the package: nothing is reported, and
    /// <see cref="FirebaseBindings"/> logged an error at start that names the package (issue #171).</summary>
    public sealed class UnavailableCrashReporter : NexusService<ICrashReporter>, ICrashReporter
    {
        public void RecordNonFatal(Exception exception) { }

        public void SetCustomKey(string key, string value) { }

        public void Log(string message) { }
    }

    /// <summary>What a player binds for <c>com.google.firebase.remote-config</c> when it lacks the package: every read returns the
    /// game's fallback and a fetch fails, and <see cref="FirebaseBindings"/> logged an error at start that names the package (issue #171).</summary>
    public sealed class UnavailableRemoteConfigService : NexusService<IRemoteConfigService>, IRemoteConfigService
    {
        public event Action<string> Changed { add { } remove { } }

        public ValueTask<bool> FetchAsync(CancellationToken ct) => new ValueTask<bool>(false);

        public bool GetBool(string key, bool fallback) => fallback;

        public int GetInt(string key, int fallback) => fallback;

        public double GetDouble(string key, double fallback) => fallback;

        public string GetString(string key, string fallback) => fallback;
    }
}
