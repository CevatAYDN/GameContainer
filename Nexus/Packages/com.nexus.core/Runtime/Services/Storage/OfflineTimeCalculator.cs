using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Utility helper for Idle & Incremental games to calculate offline progress duration safely.
    /// Detects device time manipulation (anti-cheat) and caps maximum offline progress.
    /// </summary>
    [Preserve]
    public static class OfflineTimeCalculator
    {
        private const string DefaultTimestampKey = "NT_LastQuitTimestamp";

        /// <summary>
        /// Saves the current UTC timestamp into storage. Call in OnApplicationPause / OnApplicationQuit.
        /// </summary>
        public static void RecordQuitTimestamp(IPlayerPrefsService storage, string key = DefaultTimestampKey)
        {
            if (storage == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            storage.SetLong(key, now);
            storage.Save();
        }

        /// <summary>
        /// Calculates offline duration in seconds since last recorded quit timestamp.
        /// Returns 0 if time manipulation is detected (current time is earlier than quit time).
        /// </summary>
        public static long CalculateOfflineSeconds(IPlayerPrefsService storage, long maxOfflineSeconds = 28800L, string key = DefaultTimestampKey)
        {
            if (storage == null || !storage.HasKey(key)) return 0L;

            long lastQuit = storage.GetLong(key, 0L);
            if (lastQuit <= 0) return 0L;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long diff = now - lastQuit;

            // Anti-cheat: device time set backwards
            if (diff < 0)
            {
                NexusRuntime.Logger?.LogWarning("[OfflineTimeCalculator] Time manipulation detected! Offline progress set to 0.");
                return 0L;
            }

            return Math.Min(diff, maxOfflineSeconds);
        }
    }
}
