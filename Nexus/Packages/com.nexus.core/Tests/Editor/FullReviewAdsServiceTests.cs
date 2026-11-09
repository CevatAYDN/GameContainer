using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsServiceTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposedServiceIgnoresPendingInitialization()
        {
            using var service = new AdService();
            var adapter = new DeferredAdapter();
            service.SetNetworkAdapter(adapter);
            int readinessChanges = 0;
            service.ReadyChanged += () => readinessChanges++;

            service.Dispose();
            adapter.CompleteInitialization();

            Require(!service.IsInitialized,
                "A pending adapter initialization must not initialize a disposed service.");
            Require(readinessChanges == 0,
                "A pending adapter initialization must not notify a disposed service's subscribers.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposeReleasesCurrentAdapterOnceAndClearsReadiness()
        {
            using var service = new AdService();
            var adapter = new DeferredAdapter();
            service.SetNetworkAdapter(adapter);
            adapter.CompleteInitialization();
            Require(service.IsInitialized && service.IsRewardedAvailable("reward"),
                "The adapter must be active before teardown.");
            int readinessChanges = 0;
            service.ReadyChanged += () => readinessChanges++;

            service.Dispose();
            service.Dispose();
            adapter.NotifyReady();
            adapter.CompleteInitialization();

            Require(adapter.BannerHides == 1 && adapter.Disposals == 1,
                $"Teardown must hide and dispose its adapter once; hides={adapter.BannerHides}, disposals={adapter.Disposals}.");
            Require(!service.IsInitialized && !service.IsRewardedAvailable("reward"),
                "A disposed service must not expose the released adapter as ready.");
            Require(readinessChanges == 0, "Released adapter notifications must stay detached.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_TeardownFailureStillReleasesOwnershipAndDoesNotHideTheError()
        {
            foreach (bool failHide in new[] { false, true })
            {
                using var service = new AdService();
                var adapter = new DeferredAdapter { FailHide = failHide, FailDispose = !failHide };
                service.SetNetworkAdapter(adapter);
                bool threw = false;
                try { service.Dispose(); }
                catch (InvalidOperationException) { threw = true; }
                adapter.CompleteInitialization();
                service.Dispose();

                Require(threw, "The adapter teardown error must remain observable.");
                Require(adapter.Disposals == 1, "A failed banner hide must not skip disposal, and teardown must not retry a released adapter.");
                Require(!service.IsInitialized && !service.IsRewardedAvailable("reward"),
                    "A failed teardown must not retain ownership or accept a stale initialization.");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposedServiceRejectsNewAdapterWithoutTakingOwnership()
        {
            using var service = new AdService();
            using var adapter = new DeferredAdapter();
            service.Dispose();
            int readinessChanges = 0;
            service.ReadyChanged += () => readinessChanges++;

            bool rejected = false;
            try { service.SetNetworkAdapter(adapter); }
            catch (ObjectDisposedException) { rejected = true; }
            adapter.CompleteInitialization();
            adapter.NotifyReady();

            Require(rejected, "Installing an adapter after disposal must explicitly reject the ownership transfer.");
            Require(adapter.Initializations == 0 && adapter.Disposals == 0 && adapter.ReadySubscribers == 0,
                "A rejected adapter must remain uninitialized, unsubscribed and caller-owned.");
            Require(!service.IsInitialized && !service.IsRewardedAvailable("reward") && readinessChanges == 0,
                "A disposed service must not become ready through a newly supplied adapter.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposalRejectsAdapterInstalledReentrantlyByBannerHide()
        {
            using var service = new AdService();
            using var replacement = new DeferredAdapter();
            bool rejected = false;
            var current = new DeferredAdapter
            {
                OnHide = () =>
                {
                    try { service.SetNetworkAdapter(replacement); }
                    catch (ObjectDisposedException) { rejected = true; }
                },
            };
            service.SetNetworkAdapter(current);

            service.Dispose();
            replacement.CompleteInitialization();

            Require(rejected, "Banner teardown must not install a new adapter into the disposing owner.");
            Require(current.Disposals == 1 && replacement.Initializations == 0 &&
                replacement.Disposals == 0 && replacement.ReadySubscribers == 0,
                "The old adapter must be released once and the rejected replacement must remain caller-owned.");
            Require(!service.IsInitialized && !service.IsRewardedAvailable("reward"),
                "Reentrant teardown must not revive the service.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposedServiceCannotRegisterWithTickService()
        {
            var ticks = new RecordingTicks();
            using var service = new AdService { TickService = ticks };
            service.InitializeAsync(default).GetAwaiter().GetResult();
            Require(ticks.Active == 1, "The live service must register before teardown.");
            service.Dispose();
            Require(ticks.Active == 0, "Teardown must remove the live registration.");

            bool rejected = false;
            try { service.InitializeAsync(default).GetAwaiter().GetResult(); }
            catch (ObjectDisposedException) { rejected = true; }

            Require(rejected && ticks.Active == 0,
                "Initializing a disposed service must reject before registering a tickable.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposalDuringTickRegistrationRemovesLateRegistration()
        {
            var ticks = new RecordingTicks();
            using var service = new AdService { TickService = ticks };
            ticks.OnRegister = service.Dispose;

            bool rejected = false;
            try { service.InitializeAsync(default).GetAwaiter().GetResult(); }
            catch (ObjectDisposedException) { rejected = true; }

            Require(rejected && ticks.Active == 0,
                "A registration that completes after reentrant disposal must be undone.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A07_DisposalDuringReplacementDoesNotInitializeReleasedAdapter()
        {
            using var service = new AdService();
            var previous = new DeferredAdapter { OnHide = service.Dispose };
            var replacement = new DeferredAdapter();
            service.SetNetworkAdapter(previous);

            service.SetNetworkAdapter(replacement);

            Require(previous.Disposals == 1 && replacement.Disposals == 1,
                "Disposal during replacement must release both accepted adapters once.");
            Require(replacement.Initializations == 0 && replacement.ReadySubscribers == 0,
                "The replacement must not initialize or attach handlers after teardown already released it.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class DeferredAdapter : IAdNetworkAdapter, IAdReadinessAdapter, IDisposable
        {
            private Action _initialized;
            public event Action ReadyChanged;
            public int ReadySubscribers => ReadyChanged?.GetInvocationList().Length ?? 0;
            public int Initializations;
            public int Disposals;
            public int BannerHides;
            public bool FailHide;
            public bool FailDispose;
            public Action OnHide;

            public void Initialize(Action onInitialized) { Initializations++; _initialized = onInitialized; }
            public void CompleteInitialization() => _initialized?.Invoke();
            public bool IsInterstitialReady(string placement) => true;
            public bool IsRewardedReady(string placement) => true;
            public void ShowInterstitial(string placement, Action onClosed) => onClosed?.Invoke();
            public void ShowRewarded(string placement, Action<bool> onCompleted) => onCompleted?.Invoke(true);
            public void ShowBanner(string placement, string position) { }
            public void HideBanner()
            {
                BannerHides++;
                OnHide?.Invoke();
                if (FailHide) throw new InvalidOperationException("banner hide failed");
            }
            public void NotifyReady() => ReadyChanged?.Invoke();
            public void Dispose()
            {
                Disposals++;
                if (FailDispose) throw new InvalidOperationException("adapter disposal failed");
            }
        }

        private sealed class RecordingTicks : ITickService
        {
            private readonly HashSet<ITickable> _active = new HashSet<ITickable>();
            public int Active => _active.Count;
            public Action OnRegister;
            public float TimeScale { get; set; }
            public bool IsPaused { get; set; }
            public IDisposable HoldPause() => throw new NotSupportedException();
            public void RegisterTickable(ITickable tickable) { OnRegister?.Invoke(); _active.Add(tickable); }
            public void UnregisterTickable(ITickable tickable) => _active.Remove(tickable);
            public void RegisterFixedTickable(IFixedTickable tickable) { }
            public void UnregisterFixedTickable(IFixedTickable tickable) { }
            public void RegisterLateTickable(ILateTickable tickable) { }
            public void UnregisterLateTickable(ILateTickable tickable) { }
        }
    }
}
