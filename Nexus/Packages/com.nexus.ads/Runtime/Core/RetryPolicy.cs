using System;

namespace Nexus.Ads
{
    /// <summary>Exponential back-off for failed loads: 2, 4, 8, 16, 32, then 64 s for every later failure.</summary>
    public static class RetryPolicy
    {
        public const double FirstDelaySeconds = 2;
        public const double MaxDelaySeconds = 64;

        /// <param name="failures">Consecutive failures so far, at least 1.</param>
        public static double DelaySeconds(int failures)
        {
            if (failures < 1) throw new ArgumentOutOfRangeException(nameof(failures));
            double delay = FirstDelaySeconds * Math.Pow(2, failures - 1);
            return delay > MaxDelaySeconds ? MaxDelaySeconds : delay;
        }
    }
}
