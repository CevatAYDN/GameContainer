using System;

namespace Nexus.Ads
{
    /// <summary>
    /// The provider of a host that runs no ads (ad-free release, blocked, consent flow or provider missing). An
    /// <see cref="AdController"/> over it never starts, whoever calls <see cref="AdController.Start"/>, so it takes
    /// no <see cref="MediationLock"/>, loads nothing and every show answers <see cref="AdResult.NotReady"/> (issue #165:
    /// the idle host kept a simulator that a bridge game's <c>Initialize</c> started, and it paid every reward).
    /// </summary>
    public sealed class NoAdsProvider : IAdProvider
    {
        public string Name => "none";

        // never reached through a controller; false keeps a direct caller from treating it as a running SDK
        public void Initialize(ProviderConfig config, Action<bool> onReady) => onReady?.Invoke(false);
        public void Load(AdFormat format) { }
        public bool IsReady(AdFormat format) => false;
        public void Show(AdFormat format, string placement, Action<AdResult> onResult) => onResult?.Invoke(AdResult.NotReady);
        public void ShowBanner(string placement, BannerPosition position) { }
        public void HideBanner() { }
        public void OpenTestSuite() { }

        // nothing is ever raised
        public event Action<AdFormat> Loaded { add { } remove { } }
        public event Action<AdFormat, string> LoadFailed { add { } remove { } }
        public event Action<AdFormat> Opened { add { } remove { } }
        public event Action<AdRevenue> Revenue { add { } remove { } }
    }
}
