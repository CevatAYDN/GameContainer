// Ads core proof suite: drives Nexus.Ads.Core (com.nexus.ads/Runtime/Core) through hand-written
// ports — a settable clock, a manual scheduler, a visibility switch, an in-memory store — so every
// PolicyGuard rule, the retry back-off, the preload, the reward-once contract, the provider gate,
// the session reset and the simulator scenarios are proven in milliseconds, without Unity.
// The suite also scans the Core sources for UnityEngine/UnityEditor tokens, because the harness
// compiles Core beside the Unity stubs and cannot otherwise tell a slip from a clean build.
//
// Suite ids: AP = AdPolicy, PG = PolicyGuard, AC = AdController, GT = ProviderGate, SM = simulator, EF = engine-free.

using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Ads;

namespace NexusBench
{
    // ---------------------------------------------------------------------------
    // Ports implemented by hand
    // ---------------------------------------------------------------------------

    public sealed class AdsClock : IAdClock
    {
        public double Seconds { get; set; }
        public long UtcDay { get; set; } = 20_000;
        public void Advance(double seconds) => Seconds += seconds;
    }

    public sealed class AdsVisibility : IAppVisibility
    {
        private bool _visible = true;
        public bool Visible { get => _visible; set { if (_visible == value) return; _visible = value; Changed?.Invoke(value); } }
        public event Action<bool> Changed;
    }

    public sealed class AdsMainThread : IMainThread
    {
        public bool IsCurrent => true;
        public void Post(Action action) => action();
    }

    /// <summary>Manual scheduler: nothing runs until the test advances it.</summary>
    public sealed class AdsScheduler : IAdScheduler
    {
        private sealed class Entry : IDisposable
        {
            public double Due; public Action Action; public bool Cancelled;
            public void Dispose() => Cancelled = true;
        }

        private readonly AdsClock _clock;
        private readonly List<Entry> _entries = new();
        public AdsScheduler(AdsClock clock) => _clock = clock;
        public int Pending { get { int n = 0; foreach (var e in _entries) if (!e.Cancelled) n++; return n; } }

        public IDisposable Schedule(double seconds, Action action)
        {
            var entry = new Entry { Due = _clock.Seconds + seconds, Action = action };
            _entries.Add(entry);
            return entry;
        }

        /// <summary>Advances the clock and runs every entry that became due, in order.</summary>
        public void Advance(double seconds)
        {
            _clock.Advance(seconds);
            bool ran;
            do
            {
                ran = false;
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (e.Cancelled || e.Due > _clock.Seconds) continue;
                    _entries.RemoveAt(i);
                    e.Action();
                    ran = true;
                    break;
                }
            } while (ran);
            _entries.RemoveAll(e => e.Cancelled);
        }
    }

    public sealed class AdsStore : IPacingStore
    {
        public readonly Dictionary<string, string> Rows = new();
        public string Get(string key) => Rows.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Rows[key] = value;
    }

    public sealed class AdsTelemetry : IAdTelemetry
    {
        public readonly List<AdEvent> Events = new();
        public void Record(in AdEvent e) => Events.Add(e);
        public int Count(AdEventKind kind) { int n = 0; foreach (var e in Events) if (e.Kind == kind) n++; return n; }
    }

    public sealed class AdsRemote : IAdsRemoteConfig
    {
        public readonly Dictionary<string, double> Values = new();
        private bool _kill;
        public bool KillSwitch { get => _kill; set { _kill = value; Changed?.Invoke(); } }
        public bool TryGetDouble(string key, out double value) => Values.TryGetValue(key, out value);
        public event Action Changed;
        public void Push() => Changed?.Invoke();
    }

    public sealed class AdsAdFree : IAdFreeEntitlement
    {
        private bool _adFree;
        public bool AdFree { get => _adFree; set { _adFree = value; Changed?.Invoke(); } }
        public event Action Changed;
    }

    /// <summary>A provider that records calls and answers only when the test says so.</summary>
    public sealed class AdsFakeProvider : IAdProvider
    {
        public string Name { get; set; } = "fake";
        public readonly List<AdFormat> Loads = new();
        public readonly HashSet<AdFormat> Ready = new();
        public ProviderConfig Config;
        public Action<bool> InitCallback;
        public Action<AdResult> ShowCallback;
        public AdFormat ShowFormat;
        public string ShowPlacement;
        public bool BannerShown;

        public event Action<AdFormat> Loaded;
        public event Action<AdFormat, string> LoadFailed;
        public event Action<AdFormat> Opened;
        public event Action<AdRevenue> Revenue;

        public void Initialize(ProviderConfig config, Action<bool> onReady) { Config = config; InitCallback = onReady; }
        public void Load(AdFormat format) => Loads.Add(format);
        public bool IsReady(AdFormat format) => Ready.Contains(format);
        public void Show(AdFormat format, string placement, Action<AdResult> onResult) { Ready.Remove(format); ShowFormat = format; ShowPlacement = placement; ShowCallback = onResult; Opened?.Invoke(format); }
        public void ShowBanner(string placement, BannerPosition position) => BannerShown = true;
        public void HideBanner() => BannerShown = false;
        public void OpenTestSuite() { }

        public void Fill(AdFormat format) { Ready.Add(format); Loaded?.Invoke(format); }
        public void Fail(AdFormat format, string reason) => LoadFailed?.Invoke(format, reason);
        public void Pay(AdRevenue revenue) => Revenue?.Invoke(revenue);
    }

    public sealed class AdsFakeConsent : IConsentFlow
    {
        public bool Completed { get; set; }
        public bool CanRequestAds { get; set; }
        public bool PrivacyOptionsRequired { get; set; }
        public Action Pending;
        public event Action Changed;
        public void Request(Action onCompleted) => Pending = onCompleted;
        public void ShowPrivacyOptions(Action onClosed) => onClosed();
        public void Answer(bool canRequest) { Completed = true; CanRequestAds = canRequest; Changed?.Invoke(); var p = Pending; Pending = null; p?.Invoke(); }
    }

    /// <summary>One wired controller with every port in reach of the test.</summary>
    public sealed class AdsRig : IDisposable
    {
        public readonly AdsClock Clock = new();
        public readonly AdsScheduler Scheduler;
        public readonly AdsVisibility Visibility = new();
        public readonly AdsStore Store = new();
        public readonly AdsTelemetry Telemetry = new();
        public readonly AdsFakeProvider Provider = new();
        public readonly List<PolicyViolation> Violations = new();
        public int ReadyChanges;
        public AdController Controller;

        public static readonly AdPlacement Rewarded = new("shop_refill", AdFormat.Rewarded);
        public static readonly AdPlacement LevelEnd = new("level_end", AdFormat.Interstitial, naturalBreak: true);
        public static readonly AdPlacement MidGame = new("mid_game", AdFormat.Interstitial, naturalBreak: false);

        public AdsRig(AdsConfig config = null, IConsentFlow consent = null, IAdFreeEntitlement adFree = null, IAdsRemoteConfig remote = null, IAdProvider provider = null)
        {
            Scheduler = new AdsScheduler(Clock);
            config ??= Config();
            Controller = new AdController(config, provider ?? Provider, Clock, Visibility, new AdsMainThread(), Scheduler, Store, Telemetry, consent, adFree, remote);
            Controller.Violation += v => Violations.Add(v);
            Controller.ReadyChanged += () => ReadyChanges++;
        }

        public static ProviderConfig Provider_(bool testMode = true, string[] devices = null, bool published = false)
            => new ProviderConfig("key", new Dictionary<AdFormat, string> { { AdFormat.Rewarded, "r" }, { AdFormat.Interstitial, "i" } }, testMode, devices, published, false, false, "G");

        public static AdsConfig Config(AdPolicy policy = null, BuildKind build = BuildKind.Development, params AdPlacement[] placements)
            => new AdsConfig(placements.Length == 0 ? new[] { Rewarded, LevelEnd, MidGame } : placements, policy ?? AdPolicy.Default, Provider_(), build);

        /// <summary>Start, initialize and fill both formats: the state most tests begin from.</summary>
        public AdsRig Running()
        {
            Controller.Start();
            Provider.InitCallback(true);
            Provider.Fill(AdFormat.Rewarded);
            Provider.Fill(AdFormat.Interstitial);
            return this;
        }

        public AdResult? Show(string placement)
        {
            AdResult? result = null;
            Controller.Show(placement, r => result = r);
            return result;
        }

        public void Dispose() => Controller.Dispose();
    }

    // ---------------------------------------------------------------------------
    // Suite
    // ---------------------------------------------------------------------------

    public static class AdsCoreSuite
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[AdsCore] ADS CORE PROOF: POLICYGUARD/CONTROLLER/RETRY/GATE/SIMULATOR WITHOUT UNITY");
            Console.WriteLine("===============================================================================");
            _failures = 0;
            MediationLock.Release(MediationLock.Holder ?? "");
            try
            {
                RunPolicy();
                RunGuard();
                RunController();
                RunGate();
                RunSimulator();
                RunEngineFree();
            }
            catch (Exception ex)
            {
                Check("Suite_InternalError", false, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                MediationLock.Release(MediationLock.Holder ?? "");
            }
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "[AdsCore] ADS CORE PROOF PASSED ✓" : $"[AdsCore] ADS CORE PROOF FAILED ({_failures} failures)");
            return _failures;
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[AdsCore] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            if (!ok) _failures++;
        }

        // ----- AdPolicy

        private static void RunPolicy()
        {
            var p = AdPolicy.Default;
            var looser = p.Tighten(AdPolicy.Keys.MinInterstitialGapSeconds, 5);
            Check("AP1. Gap_CannotLoosen", ReferenceEquals(looser, p) && looser.MinInterstitialGapSeconds == 30, $"gap={looser.MinInterstitialGapSeconds} (floor 30 kept)");
            var tighter = p.Tighten(AdPolicy.Keys.MinInterstitialGapSeconds, 90);
            Check("AP2. Gap_CanTighten", tighter.MinInterstitialGapSeconds == 90, $"gap={tighter.MinInterstitialGapSeconds}");
            var capped = p.Tighten(AdPolicy.Keys.MaxInterstitialsPerSession, 4);
            var lifted = capped.Tighten(AdPolicy.Keys.MaxInterstitialsPerSession, 0).Tighten(AdPolicy.Keys.MaxInterstitialsPerSession, 9);
            Check("AP3. Cap_OnlyShrinks_ZeroNeverLifts", capped.MaxInterstitialsPerSession == 4 && lifted.MaxInterstitialsPerSession == 4, $"cap={capped.MaxInterstitialsPerSession} after 0 and 9: {lifted.MaxInterstitialsPerSession}");
            var resume = p.Tighten(AdPolicy.Keys.SessionResumeGapMinutes, 5).Tighten(AdPolicy.Keys.SessionResumeGapMinutes, 45);
            Check("AP4. ResumeGap_OnlyGrows", resume.SessionResumeGapMinutes == 45, $"resume={resume.SessionResumeGapMinutes} min");
            var merged = p.Tighten(new AdPolicy(10, 120, 10, 3, 0, 10));
            Check("AP5. TightenPolicy_TakesStricterOfEach", merged.MinInterstitialGapSeconds == 30 && merged.NoInterstitialForFirstSeconds == 120 && merged.MaxInterstitialsPerSession == 3 && merged.SessionResumeGapMinutes == 30,
                $"gap={merged.MinInterstitialGapSeconds} first={merged.NoInterstitialForFirstSeconds} cap={merged.MaxInterstitialsPerSession} resume={merged.SessionResumeGapMinutes}");
            Check("AP6. UnknownKey_Ignored", ReferenceEquals(p.Tighten("ads.nothing", 99), p), "same instance");
            Check("AP7. Retry_Backoff_2_4_8_64", RetryPolicy.DelaySeconds(1) == 2 && RetryPolicy.DelaySeconds(2) == 4 && RetryPolicy.DelaySeconds(3) == 8 && RetryPolicy.DelaySeconds(6) == 64 && RetryPolicy.DelaySeconds(20) == 64,
                $"{RetryPolicy.DelaySeconds(1)},{RetryPolicy.DelaySeconds(2)},{RetryPolicy.DelaySeconds(3)},...,{RetryPolicy.DelaySeconds(6)},{RetryPolicy.DelaySeconds(20)}");
        }

        // ----- PolicyGuard

        private static void RunGuard()
        {
            using var rig = new AdsRig().Running();
            var guard = rig.Controller.Guard;
            var open = new GuardState(false, false);

            var v = guard.Check("nope", AdFormat.Rewarded, open);
            Check("PG1. UnknownPlacement_Blocked", !v.Allowed && v.Rule == PolicyRules.UnknownPlacement, v.Reason);
            v = guard.Check("shop_refill", AdFormat.Interstitial, open);
            Check("PG2. FormatMismatch_IsUnknownPlacement", !v.Allowed && v.Rule == PolicyRules.UnknownPlacement, v.Reason);
            v = guard.Check("mid_game", AdFormat.Interstitial, open);
            Check("PG3. Interstitial_WithoutNaturalBreak_Blocked", !v.Allowed && v.Rule == PolicyRules.InterstitialNaturalBreakOnly, v.Reason);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG4. Interstitial_InFirstSeconds_Blocked", !v.Allowed && v.Rule == PolicyRules.InterstitialFrequency, v.Reason);
            v = guard.Check("shop_refill", AdFormat.Rewarded, open);
            Check("PG5. Rewarded_NotPacedByInterstitialFloors", v.Allowed, "allowed at session start");

            rig.Scheduler.Advance(61);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG6. Interstitial_AfterFirstSeconds_Allowed", v.Allowed, "61 s into the session");
            v = guard.Check("level_end", AdFormat.Interstitial, new GuardState(true, false));
            Check("PG7. Stacked_Blocked", !v.Allowed && v.Rule == PolicyRules.NoStackedAds, v.Reason);
            v = guard.Check("level_end", AdFormat.Interstitial, new GuardState(false, true));
            Check("PG8. BeforeConsent_Blocked", !v.Allowed && v.Rule == PolicyRules.NoRequestBeforeConsent, v.Reason);
            rig.Visibility.Visible = false;
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG9. Background_Blocked", !v.Allowed && v.Rule == PolicyRules.NoAdInBackground, v.Reason);
            rig.Visibility.Visible = true;

            // a rewarded ad just closed: interstitials wait the after-rewarded gap
            var shown = rig.Show("shop_refill");
            rig.Provider.ShowCallback(AdResult.Rewarded);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG10. Interstitial_RightAfterRewarded_Blocked", shown == null && !v.Allowed && v.Rule == PolicyRules.InterstitialFrequency, v.Reason);
            rig.Scheduler.Advance(30);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG11. Interstitial_AfterRewardedGap_Allowed", v.Allowed, "30 s after the rewarded ad");

            // gap between interstitials
            rig.Provider.Fill(AdFormat.Interstitial);
            rig.Show("level_end");
            rig.Provider.ShowCallback(AdResult.Closed);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG12. Interstitial_InsideGap_Blocked", !v.Allowed && v.Rule == PolicyRules.InterstitialFrequency, v.Reason);
            rig.Scheduler.Advance(30);
            Check("PG13. Interstitial_AfterGap_Allowed", guard.Check("level_end", AdFormat.Interstitial, open).Allowed, "30 s later");

            // caps: session and day, remote can only tighten
            guard.Tighten(AdPolicy.Keys.MaxInterstitialsPerSession, 1);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG14. SessionCap_Blocks", !v.Allowed && v.Reason.Contains("session cap"), v.Reason);
            guard.Tighten(AdPolicy.Keys.MaxInterstitialsPerSession, 50);
            Check("PG15. SessionCap_CannotLoosen", guard.Policy.MaxInterstitialsPerSession == 1, $"cap={guard.Policy.MaxInterstitialsPerSession}");

            // a new session after the resume gap clears session counters, the day cap survives
            guard.Tighten(AdPolicy.Keys.MaxInterstitialsPerDay, 1);
            rig.Visibility.Visible = false;
            rig.Scheduler.Advance(31 * 60);
            rig.Visibility.Visible = true;
            rig.Scheduler.Advance(61);
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG16. NewSession_ResetsSessionCap_DayCapHolds", !v.Allowed && v.Reason.Contains("day cap"), v.Reason);
            rig.Clock.UtcDay++;
            Check("PG17. NextDay_DayCapResets", guard.Check("level_end", AdFormat.Interstitial, open).Allowed, "allowed on the next UTC day");

            // per-placement cooldown and caps apply to rewarded too
            var cfg = AdsRig.Config(null, BuildKind.Development, new AdPlacement("boost", AdFormat.Rewarded, cooldownSeconds: 10, sessionCap: 2, dayCap: 3));
            using var rig2 = new AdsRig(cfg).Running();
            rig2.Show("boost"); rig2.Provider.ShowCallback(AdResult.Rewarded);
            v = rig2.Controller.Guard.Check("boost", AdFormat.Rewarded, open);
            Check("PG18. Placement_Cooldown_Blocks", !v.Allowed && v.Reason.Contains("cooldown"), v.Reason);
            rig2.Scheduler.Advance(10);
            rig2.Provider.Fill(AdFormat.Rewarded);
            rig2.Show("boost"); rig2.Provider.ShowCallback(AdResult.Rewarded);
            rig2.Scheduler.Advance(10);
            v = rig2.Controller.Guard.Check("boost", AdFormat.Rewarded, open);
            Check("PG19. Placement_SessionCap_Blocks", !v.Allowed && v.Reason.Contains("session cap"), v.Reason);
            Check("PG20. DayCount_PersistedInStore", rig2.Store.Rows.ContainsKey("nexus.ads.day.boost") && rig2.Store.Get("nexus.ads.day.boost").EndsWith(":2"), rig2.Store.Get("nexus.ads.day.boost"));
        }

        // ----- AdController

        private static void RunController()
        {
            // preload after init, only for the formats placements use
            using (var rig = new AdsRig())
            {
                rig.Controller.Start();
                Check("AC1. NoLoad_BeforeInit", rig.Provider.Loads.Count == 0 && rig.Controller.State == AdController.Phase.Initializing, $"loads={rig.Provider.Loads.Count}");
                Check("AC2. ChildSettings_ReachProvider", rig.Provider.Config != null && rig.Provider.Config.MaxAdContentRating == "G", "config forwarded with rating G");
                rig.Provider.InitCallback(true);
                Check("AC3. Preload_BothFormats_AfterInit", rig.Provider.Loads.Count == 2 && rig.Provider.Loads.Contains(AdFormat.Rewarded) && rig.Provider.Loads.Contains(AdFormat.Interstitial), $"loads={string.Join(",", rig.Provider.Loads)}");
                Check("AC4. NotReady_WhileLoading", !rig.Controller.IsReady("shop_refill") && rig.Show("shop_refill") == AdResult.NotReady, "NotReady, no violation");
                rig.Provider.Fill(AdFormat.Rewarded);
                Check("AC5. Ready_AfterLoad_RaisesReadyChanged", rig.Controller.IsReady("shop_refill") && rig.ReadyChanges > 0, $"readyChanges={rig.ReadyChanges}");
            }

            // reward once, only on the SDK callback, then reload
            using (var rig = new AdsRig().Running())
            {
                int loadsBefore = rig.Provider.Loads.Count;
                var results = new List<AdResult>();
                int opened = 0, closed = 0;
                rig.Controller.AdOpened += _ => opened++;
                rig.Controller.AdClosed += (_, __) => closed++;
                rig.Controller.Show("shop_refill", r => results.Add(r));
                Check("AC6. Show_OpensFullScreen", opened == 1 && results.Count == 0, "AdOpened once, no result yet");
                var blocked = rig.Show("level_end");
                Check("AC7. SecondShow_WhileOpen_Blocked", blocked == AdResult.Blocked && rig.Violations.Count == 1 && rig.Violations[0].Rule == PolicyRules.NoStackedAds, rig.Violations[0].ToString());
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Provider.ShowCallback(AdResult.Rewarded);
                Check("AC8. Reward_DeliveredOnce", results.Count == 1 && results[0] == AdResult.Rewarded && closed == 1, $"results={results.Count} closed={closed}");
                Check("AC9. Reload_AfterShow", rig.Provider.Loads.Count == loadsBefore + 1 && rig.Provider.Loads[^1] == AdFormat.Rewarded, $"loads={rig.Provider.Loads.Count}");
                rig.Provider.Fill(AdFormat.Rewarded);
                rig.Controller.Show("shop_refill", r => results.Add(r));
                rig.Provider.ShowCallback(AdResult.Closed);
                Check("AC10. ClosedEarly_NoReward", results[^1] == AdResult.Closed, "Closed");
                rig.Provider.Fill(AdFormat.Rewarded);
                rig.Controller.Show("shop_refill", r => results.Add(r));
                rig.Provider.ShowCallback(AdResult.Failed);
                Check("AC11. ShowFailed_MapsToFailed_NotCounted", results[^1] == AdResult.Failed && rig.Telemetry.Count(AdEventKind.ShowFailed) == 1, "Failed");
            }

            // retry back-off, paused in background, resumed on return
            using (var rig = new AdsRig())
            {
                rig.Controller.Start();
                rig.Provider.InitCallback(true);
                rig.Provider.Fail(AdFormat.Rewarded, "no fill");
                Check("AC12. NoFill_Telemetry", rig.Telemetry.Count(AdEventKind.NoFill) == 1 && rig.Scheduler.Pending == 1, "one retry scheduled");
                rig.Scheduler.Advance(1.9);
                Check("AC13. Retry_NotBefore2s", rig.Provider.Loads.Count == 2, $"loads={rig.Provider.Loads.Count}");
                rig.Scheduler.Advance(0.1);
                Check("AC14. Retry_At2s", rig.Provider.Loads.Count == 3, $"loads={rig.Provider.Loads.Count}");
                rig.Provider.Fail(AdFormat.Rewarded, "error");
                rig.Scheduler.Advance(3.9);
                bool notYet = rig.Provider.Loads.Count == 3;
                rig.Scheduler.Advance(0.1);
                Check("AC15. SecondRetry_At4s", notYet && rig.Provider.Loads.Count == 4, $"loads={rig.Provider.Loads.Count}");
                rig.Provider.Fail(AdFormat.Rewarded, "error");
                rig.Visibility.Visible = false;
                Check("AC16. Background_CancelsRetry", rig.Scheduler.Pending == 0, "no pending retry");
                rig.Scheduler.Advance(100);
                Check("AC17. Background_NoLoads", rig.Provider.Loads.Count == 4, $"loads={rig.Provider.Loads.Count}");
                rig.Visibility.Visible = true;
                Check("AC18. Foreground_ReloadsAtOnce", rig.Provider.Loads.Count == 5, $"loads={rig.Provider.Loads.Count}");
            }

            // no automatic requests: with everything loaded nothing else ever calls Load
            using (var rig = new AdsRig().Running())
            {
                int loads = rig.Provider.Loads.Count;
                rig.Scheduler.Advance(3600);
                rig.Visibility.Visible = false; rig.Visibility.Visible = true;
                Check("AC19. NoAutomaticRequests_WhenLoaded", rig.Provider.Loads.Count == loads, $"loads stayed {loads} over an hour and a resume");
            }

            // consent gate
            {
                var consent = new AdsFakeConsent();
                using var rig = new AdsRig(consent: consent);
                rig.Controller.Start();
                Check("AC20. AwaitsConsent_NoInit", rig.Controller.State == AdController.Phase.AwaitingConsent && rig.Provider.InitCallback == null, "no Initialize before consent");
                var r = rig.Show("shop_refill");
                Check("AC21. Show_BeforeConsent_Blocked", r == AdResult.Blocked && rig.Violations[^1].Rule == PolicyRules.NoRequestBeforeConsent, rig.Violations[^1].Reason);
                consent.Answer(false);
                Check("AC22. ConsentRefused_NoInit_NotReady", rig.Controller.State == AdController.Phase.ConsentRefused && rig.Provider.InitCallback == null && rig.Show("shop_refill") == AdResult.NotReady, "NotReady, no violation");
                consent.CanRequestAds = true;
                rig.Controller.ShowPrivacyOptions(null);
                Check("AC23. ConsentGranted_FromPrivacyOptions_Starts", rig.Provider.InitCallback != null, "Initialize called after the options closed");
            }

            // kill switch and ad-free: NotReady, never a violation; remote only tightens
            {
                var remote = new AdsRemote();
                var adFree = new AdsAdFree();
                using var rig = new AdsRig(adFree: adFree, remote: remote).Running();
                rig.Scheduler.Advance(61);
                int violations = rig.Violations.Count;
                adFree.AdFree = true;
                Check("AC24. AdFree_BlocksInterstitial_KeepsRewarded", !rig.Controller.IsReady("level_end") && rig.Controller.IsReady("shop_refill") && rig.Show("level_end") == AdResult.NotReady && rig.Violations.Count == violations, "interstitial NotReady, rewarded ready");
                adFree.AdFree = false;
                remote.KillSwitch = true;
                Check("AC25. KillSwitch_StopsEverything", !rig.Controller.IsReady("shop_refill") && rig.Show("shop_refill") == AdResult.NotReady && rig.Telemetry.Count(AdEventKind.KillSwitch) == 1, "NotReady");
                remote.KillSwitch = false;
                remote.Values[AdPolicy.Keys.MinInterstitialGapSeconds] = 5;
                remote.Values[AdPolicy.Keys.NoInterstitialForFirstSeconds] = 600;
                remote.Push();
                Check("AC26. Remote_OnlyTightens", rig.Controller.Guard.Policy.MinInterstitialGapSeconds == 30 && rig.Controller.Guard.Policy.NoInterstitialForFirstSeconds == 600, $"gap={rig.Controller.Guard.Policy.MinInterstitialGapSeconds} first={rig.Controller.Guard.Policy.NoInterstitialForFirstSeconds}");
            }

            // revenue forwarded; unknown placement blocked with the placement named; single mediation
            using (var rig = new AdsRig().Running())
            {
                AdRevenue? seen = null;
                rig.Controller.Revenue += r => seen = r;
                rig.Provider.Pay(new AdRevenue("unityads", "shop_refill", AdFormat.Rewarded, 0.0123, "USD", "exact"));
                Check("AC27. Revenue_Forwarded", seen.HasValue && seen.Value.Value == 0.0123 && rig.Telemetry.Count(AdEventKind.Revenue) == 1, "0.0123 USD");
                var r = rig.Show("typo_placement");
                Check("AC28. UnknownPlacement_Blocked_Named", r == AdResult.Blocked && rig.Violations[^1].Rule == PolicyRules.UnknownPlacement && rig.Violations[^1].Placement == "typo_placement", rig.Violations[^1].Reason);
                var second = new AdsFakeProvider { Name = "other" };
                using var rig2 = new AdsRig(provider: second);
                rig2.Controller.Start();
                Check("AC29. SecondMediation_Refused", rig2.Controller.State == AdController.Phase.Stopped && rig2.Violations.Count == 1 && rig2.Violations[0].Rule == PolicyRules.SingleMediation && second.InitCallback == null, rig2.Violations[0].Reason);
            }
            Check("AC30. Dispose_ReleasesLock", MediationLock.Holder == null, "lock free after dispose");

            // init failure retries with the same back-off
            using (var rig = new AdsRig())
            {
                rig.Controller.Start();
                rig.Provider.InitCallback(false);
                Check("AC31. InitFailed_SchedulesRetry", rig.Controller.State == AdController.Phase.InitFailed && rig.Scheduler.Pending == 1, "retry pending");
                rig.Provider.InitCallback = null;
                rig.Scheduler.Advance(2);
                Check("AC32. InitRetried_At2s", rig.Provider.InitCallback != null, "Initialize called again");
            }

            // banner: shown through the provider, never counted as full screen
            {
                var cfg = AdsRig.Config(null, BuildKind.Development, new AdPlacement("hud", AdFormat.Banner));
                using var rig = new AdsRig(cfg);
                rig.Controller.Start();
                rig.Provider.InitCallback(true);
                var r = rig.Show("hud");
                rig.Controller.HideBanner();
                Check("AC33. Banner_ShowHide_NoPreload", r == AdResult.Closed && rig.Provider.Loads.Count == 0 && !rig.Provider.BannerShown, "banner shown then hidden, no full-screen preload");
            }
        }

        // ----- ProviderGate

        private static void RunGate()
        {
            var live = AdsRig.Provider_(testMode: false);
            var test = AdsRig.Provider_(testMode: true, devices: new[] { " ABC-123 " });
            var sample = AdsRig.Provider_(testMode: false, published: true);

            var g = ProviderGate.Evaluate(BuildKind.Editor, live, "x");
            Check("GT1. Editor_AlwaysSimulator", g.Decision == GateDecision.Simulator && g.Rule == null, g.Reason);
            g = ProviderGate.Evaluate(BuildKind.Release, live, "x");
            Check("GT2. Release_LiveConfig_Provider", g.Decision == GateDecision.Provider, "provider");
            g = ProviderGate.Evaluate(BuildKind.Release, test, "x");
            Check("GT3. Release_TestMode_Blocked", g.Decision == GateDecision.Blocked && g.Rule == PolicyRules.TestModeRequired, g.Reason);
            g = ProviderGate.Evaluate(BuildKind.Release, sample, "x");
            Check("GT4. Release_SampleUnits_Blocked", g.Decision == GateDecision.Blocked && g.Rule == PolicyRules.TestModeRequired, g.Reason);
            g = ProviderGate.Evaluate(BuildKind.Development, live, "x");
            Check("GT5. Development_WithoutTestMode_Blocked", g.Decision == GateDecision.Blocked && g.Rule == PolicyRules.TestModeRequired, g.Reason);
            g = ProviderGate.Evaluate(BuildKind.Development, sample, "any-device");
            Check("GT6. Development_SampleUnits_AnyDevice", g.Decision == GateDecision.Provider, "Google's sample units are safe everywhere");
            g = ProviderGate.Evaluate(BuildKind.Development, test, "abc-123");
            Check("GT7. Development_ListedDevice_Provider", g.Decision == GateDecision.Provider, "matched case-insensitively and trimmed");
            g = ProviderGate.Evaluate(BuildKind.Development, test, "other");
            Check("GT8. Development_UnlistedDevice_Simulator", g.Decision == GateDecision.Simulator && g.Rule == PolicyRules.UnregisteredDevice, g.Reason);
            g = ProviderGate.Evaluate(BuildKind.Development, AdsRig.Provider_(testMode: true), "other");
            Check("GT9. Development_EmptyList_Simulator", g.Decision == GateDecision.Simulator && g.Rule == PolicyRules.UnregisteredDevice, "empty device list = simulator everywhere");
        }

        // ----- Simulator scenarios through the real controller

        private static void RunSimulator()
        {
            AdResult? Drive(SimulatorScenario scenario, out AdsRig rig, out SimulatorProvider sim, bool interactive = false)
            {
                var clock = new AdsClock();
                sim = new SimulatorProvider(new AdsScheduler(clock), scenario) { Interactive = interactive };
                rig = new AdsRig(provider: sim, consent: sim);
                rig.Controller.Start();
                return rig.Show("shop_refill");
            }

            var r = Drive(SimulatorScenario.Normal, out var rig1, out var sim1);
            Check("SM1. Normal_PaysAtOnce_ThenReloads", r == AdResult.Rewarded && sim1.IsReady(AdFormat.Rewarded) && rig1.Telemetry.Count(AdEventKind.Revenue) == 1, $"result={r}, reloaded, simulated revenue row");
            rig1.Dispose();
            r = Drive(SimulatorScenario.NoFill, out var rig2, out _);
            Check("SM2. NoFill_NotReady_RetryScheduled", r == AdResult.NotReady && rig2.Telemetry.Count(AdEventKind.NoFill) >= 1, $"result={r}");
            rig2.Dispose();
            r = Drive(SimulatorScenario.LoadFails, out var rig3, out _);
            Check("SM3. LoadFails_NotReady", r == AdResult.NotReady && rig3.Telemetry.Count(AdEventKind.LoadFailed) >= 1, $"result={r}");
            rig3.Dispose();
            r = Drive(SimulatorScenario.ShowFails, out var rig4, out _);
            Check("SM4. ShowFails_Failed", r == AdResult.Failed, $"result={r}");
            rig4.Dispose();
            r = Drive(SimulatorScenario.ClosedEarly, out var rig5, out _);
            Check("SM5. ClosedEarly_Closed_NoReward", r == AdResult.Closed, $"result={r}");
            rig5.Dispose();
            r = Drive(SimulatorScenario.ConsentRefused, out var rig6, out _);
            Check("SM6. ConsentRefused_NotReady_NoInit", r == AdResult.NotReady && rig6.Controller.State == AdController.Phase.ConsentRefused && rig6.Controller.PrivacyOptionsRequired, $"result={r} state={rig6.Controller.State}");
            rig6.Dispose();

            // slow load: not ready until the simulator's delay elapsed
            {
                var clock = new AdsClock();
                var scheduler = new AdsScheduler(clock);
                var sim = new SimulatorProvider(scheduler, SimulatorScenario.SlowLoad);
                using var rig = new AdsRig(provider: sim, consent: sim);
                rig.Controller.Start();
                bool notYet = !rig.Controller.IsReady("shop_refill");
                scheduler.Advance(SimulatorProvider.SlowLoadSeconds);
                Check("SM7. SlowLoad_ReadyAfterDelay", notYet && rig.Controller.IsReady("shop_refill"), "ready after 5 s");
            }

            // interactive: the overlay decides; an interstitial can never pay
            {
                r = Drive(SimulatorScenario.Normal, out var rig, out var sim, interactive: true);
                bool open = r == null && sim.IsShowing;
                sim.CompleteShow(AdResult.Rewarded);
                sim.CompleteShow(AdResult.Rewarded);
                Check("SM8. Interactive_WaitsForOverlay_PaysOnce", open && rig.Telemetry.Count(AdEventKind.Rewarded) == 1, "one reward");
                rig.Scheduler.Advance(61);
                AdResult? interstitial = null;
                rig.Controller.Show("level_end", x => interstitial = x);
                sim.CompleteShow(AdResult.Rewarded);
                Check("SM9. Interstitial_CannotPay", interstitial == AdResult.Closed, $"result={interstitial}");
                rig.Dispose();
            }
            {
                var clock = new AdsClock();
                var sim = new SimulatorProvider(new AdsScheduler(clock));
                using var simRig = new AdsRig(provider: sim, consent: sim);
                simRig.Controller.Start();
                var real = new AdsFakeProvider { Name = "levelplay" };
                using var realRig = new AdsRig(provider: real);
                realRig.Controller.Start();
                Check("SM10. Simulator_NeverHoldsMediationLock", simRig.Controller.State == AdController.Phase.Running && MediationLock.Holder == "levelplay" && realRig.Violations.Count == 0, $"holder={MediationLock.Holder ?? "none"} with a simulator running beside it");
            }
        }

        // ----- engine independence

        private static void RunEngineFree()
        {
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Nexus/Packages/com.nexus.ads/Runtime/Core"));
            if (!Directory.Exists(dir)) dir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Nexus/Packages/com.nexus.ads/Runtime/Core"));
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories) : Array.Empty<string>();
            var offenders = new List<string>();
            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                if (text.Contains("UnityEngine") || text.Contains("UnityEditor") || text.Contains("using Nexus.Core"))
                    offenders.Add(Path.GetFileName(file));
            }
            Check("EF1. Core_HasNoUnityOrNexusCoreReference", files.Length >= 10 && offenders.Count == 0, $"{files.Length} files scanned" + (offenders.Count == 0 ? "" : ", offenders: " + string.Join(",", offenders)));
            string asmdef = Path.Combine(dir, "Nexus.Ads.Core.asmdef");
            bool flag = File.Exists(asmdef) && File.ReadAllText(asmdef).Contains("\"noEngineReferences\": true") && File.ReadAllText(asmdef).Contains("\"references\": []");
            Check("EF2. Asmdef_NoEngineReferences_NoReferences", flag, asmdef);
        }
    }
}
