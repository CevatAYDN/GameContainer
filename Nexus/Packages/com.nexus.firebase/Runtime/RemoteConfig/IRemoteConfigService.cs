using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Firebase.RemoteConfig
{
    /// <summary>
    /// Values that can change a game without an update. Every read returns the fallback the game passes when the key is absent or its
    /// value cannot be read as the requested type.
    /// </summary>
    public interface IRemoteConfigService
    {
        /// <summary>Raised with the key once per value that changed on a fetch or a set.</summary>
        event Action<string> Changed;

        /// <summary>Fetches the latest values. The result is true when the fetch succeeded; a failed fetch keeps the cached values.</summary>
        ValueTask<bool> FetchAsync(CancellationToken ct);

        bool GetBool(string key, bool fallback);

        int GetInt(string key, int fallback);

        double GetDouble(string key, double fallback);

        string GetString(string key, string fallback);
    }
}
