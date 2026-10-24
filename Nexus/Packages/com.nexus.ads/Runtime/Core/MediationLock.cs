using System;

namespace Nexus.Ads
{
    /// <summary>
    /// One mediation per process (<see cref="PolicyRules.SingleMediation"/>). The first controller to
    /// start takes the lock under its provider's name; a second provider is refused until the holder
    /// is disposed. The simulator never takes it, so a simulator can run beside a stopped provider.
    /// </summary>
    public static class MediationLock
    {
        private static readonly object Sync = new object();
        private static string _holder;

        public static string Holder { get { lock (Sync) return _holder; } }

        public static bool TryAcquire(string providerName)
        {
            if (string.IsNullOrEmpty(providerName)) throw new ArgumentException("A provider name is required.", nameof(providerName));
            if (providerName == SimulatorProvider.ProviderName) return true; // no SDK behind it: never a second mediation
            lock (Sync)
            {
                if (_holder != null && !string.Equals(_holder, providerName, StringComparison.Ordinal)) return false;
                _holder = providerName;
                return true;
            }
        }

        public static void Release(string providerName)
        {
            lock (Sync)
            {
                if (string.Equals(_holder, providerName, StringComparison.Ordinal)) _holder = null;
            }
        }
    }
}
