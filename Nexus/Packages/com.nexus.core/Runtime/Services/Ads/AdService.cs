using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    public interface IAdService
    {
        void ShowInterstitial(string placement, Action onComplete = null);
        void ShowRewarded(string placement, Action<bool> onComplete);
        bool IsRewardedAvailable(string placement);
        bool IsInterstitialAvailable(string placement);
    }

    public class AdService : IAdService, INexusService
    {
        public ValueTask InitializeAsync(CancellationToken ct) => default;
        public void OnDispose() { }

        public bool IsRewardedAvailable(string placement) => true;
        public bool IsInterstitialAvailable(string placement) => true;

        public void ShowInterstitial(string placement, Action onComplete = null)
        {
            UnityEngine.Debug.Log($"[NexusAdService] Showing Interstitial for: {placement}");
            onComplete?.Invoke();
        }

        public void ShowRewarded(string placement, Action<bool> onComplete)
        {
            UnityEngine.Debug.Log($"[NexusAdService] Showing Rewarded for: {placement}");
            onComplete?.Invoke(true); // default mock success
        }
    }
}
