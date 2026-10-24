#if NEXUS_ADS_FIREBASE
using System;
using System.Globalization;
using Nexus.Firebase.RemoteConfig;

namespace Nexus.Ads.Bridge
{
    /// <summary>
    /// The kill switch and the pacing values from <c>com.nexus.firebase</c>'s remote configuration.
    /// Keys: <c>ads.kill_switch</c> (bool) and the <see cref="AdPolicy.Keys"/> names; a value that is
    /// absent is simply not reported, and every pacing value passes through the controller's
    /// tightening, so remote can only make the floors stricter. Compiled only while com.nexus.firebase
    /// is installed (<c>NEXUS_ADS_FIREBASE</c>).
    /// </summary>
    public sealed class FirebaseAdsRemoteConfig : IAdsRemoteConfig
    {
        public const string KillSwitchKey = "ads.kill_switch";

        private readonly IRemoteConfigService _remote;

        public event Action Changed;

        public FirebaseAdsRemoteConfig(IRemoteConfigService remote)
        {
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
            _remote.Changed += _ => Changed?.Invoke();
        }

        public bool KillSwitch => _remote.GetBool(KillSwitchKey, false);

        public bool TryGetDouble(string key, out double value)
        {
            string raw = _remote.GetString(key, null);
            if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            value = 0;
            return false;
        }
    }
}
#endif
