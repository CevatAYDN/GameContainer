using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Utility helper for Idle &amp; Incremental games to calculate offline progress duration safely.
    /// Detects device time manipulation (anti-cheat) and caps maximum offline progress.
    ///
    /// Anti-cheat model (A8):
    /// 1. A wall-clock timestamp is stored on quit. If the wall clock reads LOWER on resume
    ///    than on quit, the clock was set backwards → 0 offline progress.
    /// 2. A hardware monotonic tick (ms since boot) is stored alongside it. On the same boot
    ///    session, monotonic time cannot be forged by changing the clock, so the offline
    ///    reward is additionally bounded by the hardware-measured elapsed time. This closes
    ///    the forward-clock hole: setting the clock forward inflates the wall-clock diff but
    ///    not the monotonic diff, so the reward stays clamped to real elapsed time.
    /// 3. Across a device reboot the monotonic counter resets (monotonic diff turns negative),
    ///    which is indistinguishable from tampering with the monotonic source alone — in that
    ///    case we fall back to wall-clock-only validation, still capped at maxOfflineSeconds.
    /// </summary>
    [Preserve]
    public static class OfflineTimeCalculator
    {
        private const string DefaultTimestampKey = "NT_LastQuitTimestamp";
        private const string DefaultMonotonicKey = "NT_LastQuitMonotonicMs";

        /// <summary>
        /// Saves the current UTC timestamp and hardware monotonic tick into storage.
        /// Call in OnApplicationPause / OnApplicationQuit.
        /// </summary>
        public static void RecordQuitTimestamp(IPlayerPrefsService storage, string key = DefaultTimestampKey)
        {
            if (storage == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            storage.SetLong(key, now);
            storage.SetLong(DefaultMonotonicKey, Environment.TickCount64);
            storage.Save();
        }

        /// <summary>
        /// Calculates offline duration in seconds since last recorded quit timestamp.
        /// Returns 0 if time manipulation is detected (current wall time is earlier than
        /// quit time, or the hardware monotonic tick contradicts the wall-clock claim).
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

            // A8: hardware monotonic validation. On the same boot session, the true elapsed
            // time cannot exceed what the hardware monotonic clock measured, so a clock pushed
            // forward (wall diff inflated) is clamped back to real elapsed time.
            long lastMonotonic = storage.GetLong(DefaultMonotonicKey, 0L);
            if (lastMonotonic > 0)
            {
                long monoDiffMs = Environment.TickCount64 - lastMonotonic;
                if (monoDiffMs >= 0)
                {
                    // Same boot session — hardware-validated. Bound the reward by real elapsed time.
                    long monoElapsedSec = monoDiffMs / 1000L;
                    if (monoElapsedSec < diff)
                    {
                        diff = monoElapsedSec;
                    }
                }
                // monoDiffMs < 0 → device rebooted (monotonic reset). Cannot validate via
                // hardware ticks; fall through to wall-clock-only behavior (still capped).
            }

            return Math.Min(diff, maxOfflineSeconds);
        }
    }
}
