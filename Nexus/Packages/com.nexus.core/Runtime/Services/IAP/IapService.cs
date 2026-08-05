using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public enum ProductType
    {
        Consumable,
        NonConsumable,
        Subscription
    }

    public class ProductDefinition
    {
        public string Id { get; set; }
        public ProductType Type { get; set; }
        public string PriceString { get; set; } = "$0.99";
    }

    public interface IIapStoreAdapter
    {
        void Initialize(List<ProductDefinition> products, Action<bool> onInitialized);
        void Purchase(string productId, Action<bool, string> onComplete);
        void Restore(Action<bool> onComplete);
        bool IsOwned(string productId);
    }

    public interface IIapService
    {
        void SetStoreAdapter(IIapStoreAdapter adapter);
        void RegisterProducts(params ProductDefinition[] products);
        void PurchaseProduct(string productId, Action<bool, string> onComplete);
        void RestorePurchases(Action<bool> onComplete);
        bool IsProductOwned(string productId);
        ProductDefinition GetProduct(string productId);
    }

    [Preserve]
    [StubService("Replace with Unity IAP / RevenueCat adapter before release")]
    public class IapService : IIapService, INexusService
    {
        // Guards adapter swap and read-use hand-off. The catalog has its own lock;
        // the adapter is a separate mutable reference that SetStoreAdapter/Purchase/
        // Restore/Ownership/OnDispose touch from potentially different threads, so it
        // needs its own discipline (mirrors AdService's _lock pattern).
        private readonly object _adapterLock = new();
        private IIapStoreAdapter _adapter;
        private readonly Dictionary<string, ProductDefinition> _catalog = new();
        private readonly HashSet<string> _mockOwnedProducts = new();
        // Integrity checksum over _mockOwnedProducts (editor/dev mock ONLY). This is
        // deterrence, not a security boundary: the checksum lives in the same process RAM a
        // determined memory editor could patch, and the hash algorithm is discoverable in the
        // binary. The real ownership gate is the release #else path, which never trusts this
        // set.
        //
        // Hardening over the plain hash: the checksum is salted per instance and XOR-masked
        // with a mask that ROTATES on every successful verify. Consequences:
        //  - value-scans fail across instances (salt differs per IapService),
        //  - a snapshot-replay of an observed (checksum, mask) pair is detected on the next
        //    read because both the set content hash and the mask have moved,
        //  - fabricating a valid checksum requires computing the salted hash of the tampered
        //    set under the CURRENT mask — i.e. reverse-engineering the binary, which is the
        //    deterrence ceiling for an in-process mock.
        // Within its scope it still works as before: a naive RAM scan can append a fake
        // product ID to the HashSet but cannot produce a matching checksum — every read AND
        // every purchase verifies, detects the mismatch, wipes the tampered set and denies
        // the forged ownership (fail-closed; note the whole set — including legitimately-
        // owned products — is revoked on a tamper, by design). Guarded by lock (_catalog),
        // the same lock that guards the catalog dictionary.
        private int _mockOwnedSalt;
        private int _mockOwnedMask;
        private int _mockOwnedChecksum;

        public IapService()
        {
            unchecked
            {
                // Per-instance salt: the stored checksum value differs for every service
                // instance, so an observed value cannot be replayed from another instance
                // or another run. GetHashCode() is identity-based (unique per object).
                _mockOwnedSalt = (int)((long)System.DateTime.UtcNow.Ticks ^ GetHashCode() ^ 0x5A17C0DE);
                _mockOwnedMask = _mockOwnedSalt * 31 + 17;
            }
            // Seed the checksum to the salted empty-set hash under the initial mask so the
            // first read does not false-positive on a 0 vs. empty-set mismatch.
            RecomputeMockOwnedChecksum();
        }

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public void SetStoreAdapter(IIapStoreAdapter adapter)
        {
            IIapStoreAdapter toInitialize;
            List<ProductDefinition> productList;
            lock (_catalog)
            {
                productList = new List<ProductDefinition>(_catalog.Values);
            }
            lock (_adapterLock)
            {
                _adapter = adapter;
                toInitialize = adapter;
            }
            // Initialize OUTSIDE the lock: store SDKs may block or re-enter this service
            // (e.g. invoke the callback synchronously), and holding the lock across that
            // would deadlock any other caller.
            toInitialize?.Initialize(productList, (success) =>
            {
                NexusRuntime.Logger?.Log($"[IapService] Store adapter initialized: {success}");
            });
        }

        public void RegisterProducts(params ProductDefinition[] products)
        {
            lock (_catalog)
            {
                foreach (var p in products)
                {
                    if (p != null && !string.IsNullOrEmpty(p.Id))
                    {
                        _catalog[p.Id] = p;
                    }
                }
            }
        }

        // Deterministic in-session hash over the salt + mock-owned set. Iterates chars
        // directly (not string.GetHashCode, which is randomized per process on some runtimes)
        // so the checksum is stable across reads within the session and cheap to recompute.
        private int ComputeMockOwnedHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + _mockOwnedSalt;
                hash = hash * 31 + _mockOwnedProducts.Count;
                foreach (var id in _mockOwnedProducts)
                {
                    int h = 23;
                    for (int i = 0; i < id.Length; i++)
                        h = h * 31 + id[i];
                    hash = hash * 31 + h;
                }
                return hash;
            }
        }

        private void RecomputeMockOwnedChecksum()
        {
            _mockOwnedChecksum = ComputeMockOwnedHash() ^ _mockOwnedMask;
        }

        // Pure comparison — does NOT mutate on mismatch. On success the mask is rotated and
        // the checksum re-seeded under the new mask, so the stored value moves on every read:
        // a memory editor that replayed an observed consistent (checksum, mask) snapshot
        // fails on the very next verify. On tamper the stored fields are left untouched and
        // the caller (WipeIfTampered) wipes the set and re-seeds.
        private bool VerifyMockOwnedChecksum()
        {
            if (_mockOwnedChecksum != (ComputeMockOwnedHash() ^ _mockOwnedMask)) return false;
            unchecked { _mockOwnedMask = _mockOwnedMask * 31 + 17; }
            RecomputeMockOwnedChecksum();
            return true;
        }

        // Verifies the checksum and, on tamper, wipes the set and re-seeds the checksum.
        // Returns true when tampering was detected (the caller should deny the read or
        // re-run its mutation on a clean set). Shared by every mock-ownership entry point
        // so the wipe path is defined exactly once.
        private bool WipeIfTampered(string context)
        {
            if (VerifyMockOwnedChecksum()) return false;
            NexusRuntime.Logger?.LogWarning(
                $"[IapService] Mock ownership integrity check failed ({context}) — memory tampering detected. " +
                "Clearing mock ownership and denying the forged product.");
            _mockOwnedProducts.Clear();
            RecomputeMockOwnedChecksum();
            return true;
        }

        public void PurchaseProduct(string productId, Action<bool, string> onComplete)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter != null)
            {
                adapter.Purchase(productId, onComplete);
                return;
            }

            // No adapter bound — fall back gracefully instead of throwing.
            // Previously this threw InvalidOperationException in release builds,
            // crashing the app when the player tapped "Remove Ads" before the platform
            // adapter had finished initialising (cold start, missing network entitlement, etc.).
            var logger = NexusRuntime.Logger;
            if (logger != null)
            {
                logger.LogWarning(
                    $"[IapService] PurchaseProduct('{productId}') called without an IStoreAdapter bound. " +
                    "The Real Store Adapter is owned by the platform bootstrapper; if this is a release build, " +
                    "verify that GameplayLifecycle registers the adapter before any UI can trigger a purchase.");
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Developer/Editor: simulate a successful purchase so QA flows remain testable.
            lock (_catalog)
            {
                // Integrity gate BEFORE mutating: recomputing the checksum over a tampered
                // set would silently bless the forged product. Detect + wipe first so a
                // legitimate purchase can never legitimize a RAM-injected ownership.
                WipeIfTampered("before purchase");
                if (_mockOwnedProducts.Add(productId))
                {
                    RecomputeMockOwnedChecksum();
                }
            }
            onComplete?.Invoke(true, productId);
#else
            // (release) Never throw. Surface a localised, actionable failure
            // through the existing callback contract so the caller can display a
            // "Store temporarily unavailable" toast and queue the purchase intent for retry.
            try
            {
                onComplete?.Invoke(false, "store_unavailable");
            }
            catch (Exception ex)
            {
                logger?.LogException(ex);
            }
#endif
        }

        public void RestorePurchases(Action<bool> onComplete)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter != null)
            {
                adapter.Restore(onComplete);
                return;
            }

            // (mirror) Same graceful failure for restore flow.
            var logger = NexusRuntime.Logger;
            if (logger != null)
            {
                logger.LogWarning("[IapService] RestorePurchases called without an IStoreAdapter bound.");
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            onComplete?.Invoke(true);
#else
            try
            {
                onComplete?.Invoke(false);
            }
            catch (Exception ex)
            {
                logger?.LogException(ex);
            }
#endif
        }

        public bool IsProductOwned(string productId)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter != null) return adapter.IsOwned(productId);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            lock (_catalog)
            {
                // Integrity check before trusting the mock set: a memory tamper that added
                // a fake product ID breaks the checksum. Wipe and deny rather than honour
                // the forged ownership.
                if (WipeIfTampered("read")) return false;
                return _mockOwnedProducts.Contains(productId);
            }
#else
            return false;
#endif
        }

        public ProductDefinition GetProduct(string productId)
        {
            lock (_catalog)
            {
                _catalog.TryGetValue(productId, out var p);
                return p;
            }
        }

        public void OnDispose()
        {
            // Release the platform adapter so its native handles (billing connections) are
            // torn down deterministically instead of leaking until process exit. Swap under
            // the lock so a concurrent Purchase/Restore/Ownership either captures the
            // adapter before disposal or observes null (graceful "store unavailable" path).
            IIapStoreAdapter adapter;
            lock (_adapterLock)
            {
                adapter = _adapter;
                _adapter = null;
            }
            if (adapter is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
