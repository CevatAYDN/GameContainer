using System;

namespace Nexus.Firebase.RemoteConfig
{
    /// <summary>
    /// When a fetch is due. Values are fetched at launch and reused for a cache lifetime, so a launch inside the lifetime makes no request.
    /// </summary>
    public static class RemoteConfigFetchPolicy
    {
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

        /// <summary>
        /// True on the first launch, once the cache is older than <see cref="CacheLifetime"/>, and whenever the clock reads earlier than
        /// the last fetch: a clock set back would otherwise freeze the cache for as long as the difference lasts.
        /// </summary>
        public static bool IsFetchDue(DateTime? lastFetchUtc, DateTime nowUtc)
        {
            if (lastFetchUtc == null)
            {
                return true;
            }

            TimeSpan elapsed = nowUtc - lastFetchUtc.Value;
            return elapsed < TimeSpan.Zero || elapsed >= CacheLifetime;
        }
    }
}
