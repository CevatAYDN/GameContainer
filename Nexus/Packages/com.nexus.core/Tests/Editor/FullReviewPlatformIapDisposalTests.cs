using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewPlatformIapDisposalTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void P04_DirectDispose_ReleasesAdapterAndDetachesCallbacksExactlyOnce()
        {
            var adapter = new OwnedStore();
            var service = new IapService();
            int changes = 0;
            int grants = 0;
            service.StoreChanged += () => changes++;
            service.UnrequestedPurchase += (_, _) => grants++;
            try
            {
                service.SetStoreAdapter(adapter);
                adapter.Notify();
                Require(changes == 1 && grants == 1, "The live adapter must forward both callbacks.");

                service.Dispose();

                Require(adapter.Disposals == 1,
                    $"Direct Dispose must release the owned adapter once; observed {adapter.Disposals}.");
                adapter.Notify();
                Require(changes == 1 && grants == 1, "A disposed service must detach both adapter callbacks.");
                Require(!service.IsStoreAvailable, "A disposed service must no longer expose an available store.");

                ((IDisposable)service).Dispose();
                ((INexusService)service).OnDispose();
                Require(adapter.Disposals == 1, "Direct and framework disposal must share idempotent cleanup.");
            }
            finally
            {
                service.OnDispose();
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class OwnedStore : IIapStoreAdapter, IIapStoreInfoAdapter,
            IIapUnrequestedPurchaseAdapter, IDisposable
        {
            public int Disposals { get; private set; }
            public bool IsAvailable => true;
            public bool OwnershipKnown => true;
            public event Action Changed;
            public event UnrequestedPurchaseHandler UnrequestedPurchase;

            public void Initialize(List<ProductDefinition> products, Action<bool> onInitialized) =>
                onInitialized?.Invoke(true);

            public void Purchase(string productId, Action<bool, string> onComplete) =>
                throw new InvalidOperationException("This disposal fixture must not purchase.");

            public void Restore(Action<bool> onComplete) =>
                throw new InvalidOperationException("This disposal fixture must not restore.");

            public bool IsOwned(string productId) => false;
            public string GetLocalizedPrice(string productId) => null;
            public void Dispose() => Disposals++;

            public void Notify()
            {
                Changed?.Invoke();
                UnrequestedPurchase?.Invoke("fixture_coins", "fixture_transaction");
            }
        }
    }
}
