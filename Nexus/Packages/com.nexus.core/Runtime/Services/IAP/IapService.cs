using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    public interface IIapService
    {
        void PurchaseProduct(string productId, Action<bool, string> onComplete);
        void RestorePurchases(Action<bool> onComplete);
        bool IsProductOwned(string productId);
    }

    public class IapService : IIapService, INexusService
    {
        public ValueTask InitializeAsync(CancellationToken ct) => default;
        public void OnDispose() { }

        public void PurchaseProduct(string productId, Action<bool, string> onComplete)
        {
            UnityEngine.Debug.Log($"[NexusIapService] Purchasing product: {productId}");
            onComplete?.Invoke(true, productId); // default mock success
        }

        public void RestorePurchases(Action<bool> onComplete)
        {
            UnityEngine.Debug.Log("[NexusIapService] Restoring purchases");
            onComplete?.Invoke(true);
        }

        public bool IsProductOwned(string productId) => false;
    }
}
