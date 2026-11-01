using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core.Services;

namespace Nexus.Ads.Bridge.Tests
{
    // Hand-written ports: the contract is proven without Unity's clock, scenes or the bootstrap.
    internal sealed class TestClock : IAdClock { public double Seconds { get; set; } public long UtcDay { get; set; } = 1; public double UtcSeconds { get; set; } = 86_400; }
    internal sealed class TestVisibility : IAppVisibility { public bool Visible => true; public event Action<bool> Changed { add { } remove { } } }
    internal sealed class TestMainThread : IMainThread { public bool IsCurrent => true; public void Post(Action action) => action(); }
    internal sealed class TestScheduler : IAdScheduler
    {
        private sealed class Handle : IDisposable { public void Dispose() { } }
        public IDisposable Schedule(double seconds, Action action) => new Handle();
    }
    internal sealed class TestStore : IPacingStore
    {
        private readonly Dictionary<string, string> _rows = new Dictionary<string, string>();
        public string Get(string key) => _rows.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _rows[key] = value;
    }
    internal sealed class TestTelemetry : IAdTelemetry { public void Record(in AdEvent e) { } }
    internal sealed class TestStoreService : IIapService
    {
        public readonly HashSet<string> Owned = new HashSet<string>();
        public event Action StoreChanged;
        public int StoreChangedHandlers => StoreChanged?.GetInvocationList().Length ?? 0;
        public event UnrequestedPurchaseHandler UnrequestedPurchase { add { } remove { } }
        public void Own(string id) { Owned.Add(id); StoreChanged?.Invoke(); }
        public void SetStoreAdapter(IIapStoreAdapter adapter) { }
        public void RegisterProducts(params ProductDefinition[] products) { }
        public void PurchaseProduct(string productId, Action<bool, string> onComplete) => onComplete?.Invoke(false, "store_unavailable");
        public void Purchase(string productId, Action<PurchaseOutcome> onOutcome) => onOutcome?.Invoke(PurchaseOutcome.Unavailable);
        public void RestorePurchases(Action<bool> onComplete) => onComplete?.Invoke(false);
        public bool IsProductOwned(string productId) => Owned.Contains(productId);
        public bool OwnershipKnown = true;
        public void AnswerOwnership() { OwnershipKnown = true; StoreChanged?.Invoke(); }
        public Ownership GetOwnership(string productId) => Owned.Contains(productId) ? Ownership.Owned : OwnershipKnown ? Ownership.NotOwned : Ownership.Unknown;
        public ProductDefinition GetProduct(string productId) => null;
        public string GetLocalizedPrice(string productId) => null;
        public bool IsStoreAvailable => true;
    }

    /// <summary>
    /// The capability rows of docs/ADAPTERS.md, driven through AdService with the bridge adapter over the
    /// simulator: outcomes, readiness, privacy options, the cooldown floor, revenue and the ad-free entitlement.
    /// </summary>
    public sealed class NexusAdsBridgeContractTests
    {
        private TestClock _clock;
        private SimulatorProvider _simulator;
        private AdController _controller;
        private AdService _service;
        private AdAdapterFactory _factory;
        private TestStoreService _store;

        private static AdsConfig Config() => new AdsConfig(
            new[] { new AdPlacement("shop_refill", AdFormat.Rewarded), new AdPlacement("level_end", AdFormat.Interstitial, naturalBreak: true) },
            AdPolicy.Default,
            new ProviderConfig("key", new Dictionary<AdFormat, string>(), true, null, false, false, false, "G"),
            BuildKind.Editor);

        [SetUp]
        public void SetUp()
        {
            MediationLock.Release(MediationLock.Holder ?? "");
            _clock = new TestClock();
            _simulator = new SimulatorProvider(new TestScheduler());
            _controller = new AdController(Config(), _simulator, _clock, new TestVisibility(), new TestMainThread(), new TestScheduler(), new TestStore(), new TestTelemetry(), _simulator);
            _service = new AdService();
            _factory = new AdAdapterFactory();
            _store = new TestStoreService();
            NexusAdsBridge.Attach(_service, _factory, _controller, _simulator.Name, _store, "remove_ads");
        }

        [TearDown]
        public void TearDown() { _controller.Dispose(); _service.Dispose(); }

        // Issue #118: every Attach created an entitlement that stayed subscribed to the store
        [Test]
        public void ASecondAttach_LeavesOneEntitlementSubscribedToTheStore()
        {
            NexusAdsBridge.Attach(_service, _factory, _controller, _simulator.Name, _store, "remove_ads");
            Assert.AreEqual(1, _store.StoreChangedHandlers);
        }

        // Issue #178: the bridge never acknowledged a receipt, so a game that checks the pending reward at start-up, as the README
        // says, paid every reward again at its next start, and the next reward replaced the receipt
        [Test]
        public void ARewardedOutcome_AcknowledgesItsReceipt_AfterTheGameHeardIt()
        {
            RewardReceipt? pendingWhileTheGameHears = null;
            _service.RequestReward("shop_refill", o => pendingWhileTheGameHears = _controller.PendingReward);
            Assert.IsTrue(pendingWhileTheGameHears.HasValue, "the receipt exists while the game pays");
            Assert.IsFalse(_controller.PendingReward.HasValue, "acknowledged once the game's callback returned");
        }

        [Test]
        public void AGameCallbackThatThrows_KeepsTheReceiptForTheNextStart()
        {
            Assert.Throws<InvalidOperationException>(() => _service.RequestReward("shop_refill", o => throw new InvalidOperationException("grant failed")));
            Assert.IsTrue(_controller.PendingReward.HasValue, "a reward the game did not finish paying is paid at the next start");
        }

        [Test]
        public void TheFactoryKnowsTheBridge_UnderBothNames()
        {
            Assert.IsInstanceOf<NexusAdsAdapter>(_factory.CreateAdapter(NexusAdsAdapter.ProviderName));
            Assert.AreSame(_factory.CreateAdapter(NexusAdsAdapter.ProviderName), _factory.CreateAdapter("simulator"));
        }

        [Test]
        public void RequestReward_ReportsRewarded_ThroughTheService_AndReloads()
        {
            AdOutcome? outcome = null;
            Assert.IsTrue(_service.IsRewardedAvailable("shop_refill"));
            _service.RequestReward("shop_refill", o => outcome = o);
            Assert.AreEqual(AdOutcome.Rewarded, outcome);
            Assert.IsTrue(_service.IsRewardedAvailable("shop_refill"), "reloaded after the show");
        }

        [Test]
        public async Task RequestRewardAsync_AwaitsTheBridgesOutcome() // issue #73
        {
            Assert.AreEqual(AdOutcome.Rewarded, await _service.RequestRewardAsync("shop_refill"));
            _simulator.Scenario = SimulatorScenario.ClosedEarly;
            Assert.AreEqual(AdOutcome.Closed, await _service.RequestRewardAsync("shop_refill"));
        }

        [Test]
        public void ClosedEarly_IsClosed_NotRewarded_AndTheBoolOverloadSaysFalse()
        {
            _simulator.Scenario = SimulatorScenario.ClosedEarly;
            AdOutcome? outcome = null;
            _service.RequestReward("shop_refill", o => outcome = o);
            Assert.AreEqual(AdOutcome.Closed, outcome);
            bool? paid = null;
            _service.ShowRewarded("shop_refill", p => paid = p);
            Assert.AreEqual(false, paid);
        }

        [Test]
        public void UnknownPlacement_IsNotReady_ToTheGame_WithoutReachingTheController_AndAViolationWhenItDoes()
        {
            PolicyViolation? violation = null;
            _controller.Violation += v => violation = v;
            AdOutcome? outcome = null;
            _service.RequestReward("typo", o => outcome = o);
            Assert.AreEqual(AdOutcome.NotReady, outcome, "the service answers from IsRewardedReady and never calls the adapter");
            Assert.IsNull(violation, "the controller was not reached");
            AdResult? direct = null;
            _controller.Show("typo", r => direct = r);
            Assert.AreEqual(AdResult.Blocked, direct);
            Assert.AreEqual(PolicyRules.UnknownPlacement, violation?.Rule);
        }

        [Test]
        public void TheServiceCooldown_IsThePolicyFloor_AndTheGuardPacesInterstitials()
        {
            Assert.IsFalse(_service.IsInterstitialAvailable("level_end"), "first minute of the session");
            _clock.Seconds = 61;
            Assert.IsTrue(_service.IsInterstitialAvailable("level_end"));
            bool closed = false;
            _service.ShowInterstitial("level_end", () => closed = true);
            Assert.IsTrue(closed);
            Assert.IsFalse(_service.IsInterstitialAvailable("level_end"), "service cooldown = policy gap, both 30 s");
            _clock.Seconds = 95;
            // the service's own clock is Unity's; its cooldown stays engaged in a headless test, so only the guard is asserted here
            Assert.IsTrue(_controller.IsReady("level_end"));
        }

        [Test]
        public void ReadyChanged_IsForwarded_ToTheService()
        {
            int changes = 0;
            _service.ReadyChanged += () => changes++;
            _service.RequestReward("shop_refill", _ => { });
            Assert.Greater(changes, 0);
        }

        [Test]
        public void PrivacyOptions_ComeFromTheConsentFlow()
        {
            Assert.IsFalse(_service.PrivacyOptionsRequired, "the simulator's normal scenario needs none");
            _simulator.Scenario = SimulatorScenario.ConsentRefused;
            Assert.IsTrue(_service.PrivacyOptionsRequired);
            bool closed = false;
            _service.ShowPrivacyOptions(() => closed = true);
            Assert.IsTrue(closed);
            Assert.AreEqual(1, _simulator.PrivacyOptionsShown);
        }

        [Test]
        public void Revenue_ReachesTheServicesImpressionEvent()
        {
            var seen = new List<(string network, double revenue, string placement)>();
            _service.OnImpressionRecorded += (n, r, p) => seen.Add((n, r, p));
            _service.RequestReward("shop_refill", _ => { });
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("simulator", seen[0].network);
            Assert.AreEqual("shop_refill", seen[0].placement);
        }

        [Test]
        public void OwningRemoveAds_StopsInterstitials_KeepsRewarded()
        {
            _clock.Seconds = 61;
            Assert.IsTrue(_service.IsInterstitialAvailable("level_end"));
            _store.Own("remove_ads");
            Assert.IsFalse(_service.IsInterstitialAvailable("level_end"));
            Assert.IsTrue(_service.IsRewardedAvailable("shop_refill"));
        }

        // Issue #183: a player who bought remove_ads saw interstitials offline, or until the store answered what the account owns.
        [Test]
        public void WhileTheStoreHasNotSaidWhetherRemoveAdsIsOwned_NoInterstitialIsAvailable()
        {
            _clock.Seconds = 61;
            _store.OwnershipKnown = false;
            Assert.IsFalse(_service.IsInterstitialAvailable("level_end"), "ownership unknown: no interstitial");
            Assert.IsTrue(_service.IsRewardedAvailable("shop_refill"), "rewarded ads stay");

            _store.AnswerOwnership();
            Assert.IsTrue(_service.IsInterstitialAvailable("level_end"), "the store answered: not owned");
        }

        [Test]
        public void Map_BlockedAndNotReady_AreNotReady_FailedStaysFailed()
        {
            Assert.AreEqual(AdOutcome.NotReady, NexusAdsAdapter.Map(AdResult.Blocked));
            Assert.AreEqual(AdOutcome.NotReady, NexusAdsAdapter.Map(AdResult.NotReady));
            Assert.AreEqual(AdOutcome.Failed, NexusAdsAdapter.Map(AdResult.Failed));
            Assert.AreEqual(AdOutcome.Closed, NexusAdsAdapter.Map(AdResult.Closed));
        }
    }
}
