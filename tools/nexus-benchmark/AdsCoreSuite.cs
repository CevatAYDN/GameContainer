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
        public double UtcSeconds { get; set; } = 20_000 * 86_400.0;
        public void Advance(double seconds) { Seconds += seconds; UtcSeconds += seconds; }
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
        /// <summary>Every Schedule call so far, cancelled or run ones included.</summary>
        public int Scheduled { get; private set; }

        public IDisposable Schedule(double seconds, Action action)
        {
            Scheduled++;
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
        /// <summary>Record throws for this kind, as a game's analytics sink can.</summary>
        public AdEventKind? ThrowOn;
        public void Record(in AdEvent e)
        {
            if (e.Kind == ThrowOn) throw new InvalidOperationException("analytics sink failed");
            Events.Add(e);
        }
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

    public sealed class AdsTouch : IAdTouchInput
    {
        public double LastTouchAt { get; set; } = double.NaN;
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
        public int InitializeCalls;
        public Action<AdResult> ShowCallback;
        public AdFormat ShowFormat;
        public string ShowPlacement;
        public bool BannerShown;

        public event Action<AdFormat> Loaded;
        public event Action<AdFormat, string> LoadFailed;
        public event Action<AdFormat> Opened;
        public event Action<AdRevenue> Revenue;

        /// <summary>Thrown by the next <see cref="Initialize"/>, as an SDK's start can throw; cleared when thrown.</summary>
        public Exception InitThrows;
        public void Initialize(ProviderConfig config, Action<bool> onReady)
        {
            InitializeCalls++;
            if (InitThrows != null) { var ex = InitThrows; InitThrows = null; throw ex; }
            Config = config;
            InitCallback = onReady;
        }
        /// <summary>Thrown by the next <see cref="Load"/>, as an SDK's load call can throw; cleared when thrown.</summary>
        public Exception LoadThrows;
        public void Load(AdFormat format)
        {
            if (LoadThrows != null) { var ex = LoadThrows; LoadThrows = null; throw ex; }
            Loads.Add(format);
        }
        public int IsReadyCalls;
        public bool IsReady(AdFormat format) { IsReadyCalls++; return Ready.Contains(format); }
        /// <summary>Thrown by the next <see cref="Show"/>, as an SDK's show call can throw; cleared when thrown.</summary>
        public Exception ShowThrows;
        /// <summary>False: the show raises no <see cref="Opened"/>, as an SDK that drops the show does.</summary>
        public bool RaiseOpened = true;
        public void Show(AdFormat format, string placement, Action<AdResult> onResult)
        {
            if (ShowThrows != null) { var ex = ShowThrows; ShowThrows = null; throw ex; }
            Ready.Remove(format); ShowFormat = format; ShowPlacement = placement; ShowCallback = onResult;
            if (RaiseOpened) Opened?.Invoke(format);
        }
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
        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public bool NotifyOnPrivacyClose;
        public bool NotifyAfterPrivacyClose;
        public bool SkipPrivacyClose;
        public event Action Changed;
        public void Request(Action onCompleted) => Pending = onCompleted;
        public void ShowPrivacyOptions(Action onClosed)
        {
            if (NotifyOnPrivacyClose) Changed?.Invoke();
            if (!SkipPrivacyClose) onClosed();
            if (NotifyAfterPrivacyClose) Changed?.Invoke();
        }
        public void Answer(bool canRequest) { Completed = true; CanRequestAds = canRequest; Changed?.Invoke(); var p = Pending; Pending = null; p?.Invoke(); }
    }

    public sealed class AdsQueuedMainThread : IMainThread
    {
        public bool IsCurrent { get; set; } = true;
        private readonly Queue<Action> _pending = new();
        public void Post(Action action) => _pending.Enqueue(action);
        public void Drain() { IsCurrent = true; while (_pending.Count > 0) _pending.Dequeue()(); }
    }

    /// <summary>One wired controller with every port in reach of the test.</summary>
    public sealed class AdsRig : IDisposable
    {
        public readonly AdsClock Clock;
        public readonly AdsScheduler Scheduler;
        public readonly AdsVisibility Visibility = new();
        public readonly AdsStore Store;
        public readonly AdsTelemetry Telemetry = new();
        public readonly AdsFakeProvider Provider = new();
        public readonly List<PolicyViolation> Violations = new();
        public int ReadyChanges;
        public AdController Controller;

        public static readonly AdPlacement Rewarded = new("shop_refill", AdFormat.Rewarded);
        public static readonly AdPlacement LevelEnd = new("level_end", AdFormat.Interstitial, naturalBreak: true);
        public static readonly AdPlacement MidGame = new("mid_game", AdFormat.Interstitial, naturalBreak: false);

        public AdsRig(AdsConfig config = null, IConsentFlow consent = null, IAdFreeEntitlement adFree = null, IAdsRemoteConfig remote = null, IAdProvider provider = null,
            AdsStore store = null, AdsClock clock = null, IAdTouchInput touch = null)
        {
            Clock = clock ?? new AdsClock();
            Store = store ?? new AdsStore();
            Scheduler = new AdsScheduler(Clock);
            config ??= Config();
            Controller = new AdController(config, provider ?? Provider, Clock, Visibility, new AdsMainThread(), Scheduler, Store, Telemetry, consent, adFree, remote, touch);
            Controller.Violation += v => Violations.Add(v);
            Controller.ReadyChanged += () => ReadyChanges++;
        }

        /// <summary>The same device after the process restarted: the store is kept, the monotonic clock starts over, the wall
        /// clock has moved on by <paramref name="secondsLater"/> (negative: the device clock was set back).</summary>
        public AdsRig Restart(AdsConfig config, double secondsLater)
            => new AdsRig(config, store: Store, clock: new AdsClock { UtcDay = Clock.UtcDay, UtcSeconds = Clock.UtcSeconds + secondsLater });

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
                RunPacingWake();
                RunWatchdog();
                RunAnswerIsolation();
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
            // #128: the lifetime-session threshold only grows and survives every other key's tightening
            var gated = p.Tighten(AdPolicy.Keys.MinLifetimeSessionsBeforeInterstitial, 3).Tighten(AdPolicy.Keys.MinLifetimeSessionsBeforeInterstitial, 1)
                .Tighten(AdPolicy.Keys.MinInterstitialGapSeconds, 90).Tighten(AdPolicy.Keys.MaxInterstitialsPerDay, 5).Tighten(AdPolicy.Keys.SessionResumeGapMinutes, 45);
            Check("AP8. LifetimeSessions_OnlyGrows_KeptByOtherKeys", gated.MinLifetimeSessionsBeforeInterstitial == 3 && gated.MinInterstitialGapSeconds == 90 && gated.MaxInterstitialsPerDay == 5 && gated.SessionResumeGapMinutes == 45,
                $"sessions={gated.MinLifetimeSessionsBeforeInterstitial} gap={gated.MinInterstitialGapSeconds} day={gated.MaxInterstitialsPerDay} resume={gated.SessionResumeGapMinutes}");
            var fromSettings = p.Tighten(new AdPolicy(30, 60, 30, 0, 0, 30, 2));
            Check("AP9. TightenPolicy_CarriesLifetimeSessions", fromSettings.MinLifetimeSessionsBeforeInterstitial == 2, $"sessions={fromSettings.MinLifetimeSessionsBeforeInterstitial} (the settings asset goes through this overload)");
            // #126: the wait after the player's last touch only grows and survives every other key's tightening
            var touchy = p.Tighten(AdPolicy.Keys.NoInterstitialAfterTouchSeconds, 2).Tighten(AdPolicy.Keys.NoInterstitialAfterTouchSeconds, 0.5)
                .Tighten(AdPolicy.Keys.MinInterstitialGapSeconds, 90).Tighten(AdPolicy.Keys.MinLifetimeSessionsBeforeInterstitial, 3);
            Check("AP10. TouchWait_OnlyGrows_KeptByOtherKeys", touchy.NoInterstitialAfterTouchSeconds == 2 && touchy.MinInterstitialGapSeconds == 90 && touchy.MinLifetimeSessionsBeforeInterstitial == 3,
                $"touch={touchy.NoInterstitialAfterTouchSeconds} gap={touchy.MinInterstitialGapSeconds} sessions={touchy.MinLifetimeSessionsBeforeInterstitial}");
            var touchFromSettings = p.Tighten(new AdPolicy(30, 60, 30, 0, 0, 30, 0, 1.5));
            Check("AP11. TightenPolicy_CarriesTouchWait", touchFromSettings.NoInterstitialAfterTouchSeconds == 1.5, $"touch={touchFromSettings.NoInterstitialAfterTouchSeconds} (the settings asset goes through this overload)");
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
            // #195: a row from any other day read zero, so setting the device clock back a day lifted every day cap
            rig.Clock.UtcDay -= 2;
            v = guard.Check("level_end", AdFormat.Interstitial, open);
            Check("PG25. ClockSetBack_DayCapHolds", !v.Allowed && v.Reason.Contains("day cap"), v.Reason);

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

            // #153: the interstitial gap, the wait after a rewarded ad and a placement's cooldown are measured from the show
            // across a restart; before, they started over and only the first-seconds floor held an interstitial back. Gigawatt's
            // numbers (900 s gap and cooldown, 180 s after a rewarded ad) with no floor, so the floor cannot hide the rule.
            var paced = AdsRig.Config(new AdPolicy(900, 0, 180, 0, 0, 30), BuildKind.Development,
                AdsRig.LevelEnd, new AdPlacement("boost", AdFormat.Rewarded, cooldownSeconds: 900));
            using (var before = new AdsRig(paced).Running())
            {
                before.Show("level_end"); before.Provider.ShowCallback(AdResult.Closed);
                using (var after = before.Restart(paced, 100))
                {
                    v = after.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                    double left = after.Controller.Guard.SecondsUntilPacingEnds(AdsRig.LevelEnd);
                    Check("PG21. InterstitialGap_SurvivesARestart", !v.Allowed && v.Reason.Contains("the last interstitial") && Math.Abs(left - 800) < 1e-6, $"{v.Reason}; left={left}");
                }
                using (var after = before.Restart(paced, -3600))
                {
                    v = after.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                    double left = after.Controller.Guard.SecondsUntilPacingEnds(AdsRig.LevelEnd);
                    Check("PG22. AShowAheadOfTheWallClock_CountsAsJustShown", !v.Allowed && Math.Abs(left - 900) < 1e-6, $"{v.Reason}; left={left}");
                }
            }
            using (var before = new AdsRig(paced).Running())
            {
                before.Show("boost"); before.Provider.ShowCallback(AdResult.Rewarded);
                using var after = before.Restart(paced, 100);
                v = after.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG23. WaitAfterRewarded_SurvivesARestart", !v.Allowed && v.Reason.Contains("a rewarded ad closed"), v.Reason);
                v = after.Controller.Guard.Check("boost", AdFormat.Rewarded, open);
                Check("PG24. PlacementCooldown_SurvivesARestart", !v.Allowed && v.Reason.Contains("cooldown"), v.Reason);
            }

            // #128: interstitials wait until the player has had N sessions on this device; the threshold is not the first-seconds floor,
            // so each check runs past it. A cold start and a return after the resume gap each count one session.
            var oneEarlier = AdsRig.Config(new AdPolicy(30, 60, 30, 0, 0, 30, 1));
            using (var first = new AdsRig(oneEarlier).Running())
            {
                first.Scheduler.Advance(61);
                v = first.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG26. LifetimeSessions_FirstSessionBlocked", !v.Allowed && v.Rule == PolicyRules.InterstitialFrequency && v.Reason.Contains("earlier sessions"), v.Reason);
                using var second = first.Restart(oneEarlier, 3600).Running();
                second.Scheduler.Advance(61);
                v = second.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG27. LifetimeSessions_NextColdStartAllowed", v.Allowed, $"{v.Reason}; stored count={second.Store.Get("nexus.ads.sessions.lifetime")}");
            }
            using (var resumed = new AdsRig(oneEarlier).Running())
            {
                resumed.Visibility.Visible = false;
                resumed.Scheduler.Advance(31 * 60);
                resumed.Visibility.Visible = true;
                resumed.Scheduler.Advance(61);
                v = resumed.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG28. LifetimeSessions_AResumeAfterTheGapCounts", v.Allowed, v.Reason ?? "allowed in the second session");
            }
            var raise = new AdsRemote();
            raise.Values[AdPolicy.Keys.MinLifetimeSessionsBeforeInterstitial] = 1;
            using (var remoteRig = new AdsRig(remote: raise).Running())
            {
                raise.Push();
                remoteRig.Scheduler.Advance(61);
                v = remoteRig.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG29. LifetimeSessions_RemoteRaisesIt", !v.Allowed && v.Reason.Contains("earlier sessions"), v.Reason);
            }

            // #126: interstitials wait after the player's last touch, so one that opens under a finger does not take the next tap.
            // No first-seconds floor, so the floor cannot hide the rule.
            var touchWait = AdsRig.Config(new AdPolicy(30, 0, 30, 0, 0, 30, 0, 2), BuildKind.Development, AdsRig.LevelEnd);
            using (var noInput = new AdsRig(touchWait).Running())
            {
                using var untouched = new AdsRig(touchWait, touch: new AdsTouch()).Running();
                var a = noInput.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                var b = untouched.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG30. TouchWait_NoTouchYetOrNoInput_Allowed", a.Allowed && b.Allowed, $"no input port: {a.Reason ?? "allowed"}; no touch yet: {b.Reason ?? "allowed"}");
            }
            var touched = new AdsTouch();
            using (var touchRig = new AdsRig(touchWait, touch: touched).Running())
            {
                touchRig.Scheduler.Advance(10);
                touched.LastTouchAt = touchRig.Clock.Seconds - 0.5;
                v = touchRig.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                double left = touchRig.Controller.Guard.SecondsUntilPacingEnds(AdsRig.LevelEnd);
                Check("PG31. TouchWait_BlocksWithItsRule_AndCountsDown", !v.Allowed && v.Rule == PolicyRules.InterstitialAfterTouch && Math.Abs(left - 1.5) < 1e-9, $"{v.Rule}: {v.Reason}; left={left} (expected 1.5)");
                touchRig.Scheduler.Advance(1.5);
                v = touchRig.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG32. TouchWait_AllowedOnceItEnds", v.Allowed && touchRig.Controller.Guard.SecondsUntilPacingEnds(AdsRig.LevelEnd) == 0, v.Reason ?? "allowed 2 s after the touch");
            }
            var raiseTouch = new AdsRemote();
            var remoteTouch = new AdsTouch();
            using (var remoteRig = new AdsRig(AdsRig.Config(new AdPolicy(30, 0, 30, 0, 0, 30), BuildKind.Development, AdsRig.LevelEnd), remote: raiseTouch, touch: remoteTouch).Running())
            {
                remoteRig.Scheduler.Advance(10);
                remoteTouch.LastTouchAt = remoteRig.Clock.Seconds - 1;
                bool before = remoteRig.Controller.Guard.Check("level_end", AdFormat.Interstitial, open).Allowed;
                raiseTouch.Values[AdPolicy.Keys.NoInterstitialAfterTouchSeconds] = 2;
                raiseTouch.Push();
                v = remoteRig.Controller.Guard.Check("level_end", AdFormat.Interstitial, open);
                Check("PG33. TouchWait_RemoteRaisesIt", before && !v.Allowed && v.Rule == PolicyRules.InterstitialAfterTouch, $"before the push allowed={before}; after: {v.Rule}: {v.Reason}");
            }
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

            // issue #328: a withdrawal in the privacy options left a running controller Running, ready, and showing ads
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                var cfg = AdsRig.Config(null, BuildKind.Development, AdsRig.Rewarded, AdsRig.LevelEnd, new AdPlacement("hud", AdFormat.Banner));
                using var rig = new AdsRig(cfg, consent: consent).Running();
                rig.Scheduler.Advance(61);
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom);
                AdResult? open = null;
                rig.Controller.Show("shop_refill", r => open = r); // a rewarded show is open when the player withdraws
                consent.CanRequestAds = false;
                bool closed = false;
                rig.Controller.ShowPrivacyOptions(() => closed = true);
                var state = rig.Controller.State;
                bool bannerHidden = !rig.Provider.BannerShown;
                int loads = rig.Provider.Loads.Count;
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Scheduler.Advance(3600); // past every interstitial rule that follows a rewarded show
                bool ready = rig.Controller.IsReady("level_end");
                var interstitial = rig.Show("level_end");
                rig.Visibility.Visible = false; rig.Visibility.Visible = true;
                Check("AC62. WithdrawingConsentInThePrivacyOptions_StopsARunningController",
                    closed && state == AdController.Phase.ConsentRefused && bannerHidden && open == AdResult.Rewarded && !ready && interstitial == AdResult.NotReady
                        && rig.Provider.ShowFormat == AdFormat.Rewarded && rig.Provider.Loads.Count == loads,
                    $"state={state} banner hidden={bannerHidden}; the open show answered {open?.ToString() ?? "nothing"}; IsReady(level_end)={ready} Show={interstitial} "
                    + $"reached the provider={rig.Provider.ShowFormat == AdFormat.Interstitial}; loads after the withdrawal={rig.Provider.Loads.Count - loads} "
                    + "(expected ConsentRefused True; Rewarded; False NotReady False; 0; pre-fix Running, ready, the interstitial shown)");
            }

            // issue #328: a withdrawal while the SDK start was out, or while a failed start waited for its retry, went on to load ads
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                using var starting = new AdsRig(consent: consent);
                starting.Controller.Start();
                consent.CanRequestAds = false;
                starting.Controller.ShowPrivacyOptions(null);
                starting.Provider.InitCallback(true);
                var afterStart = starting.Controller.State;
                int startingLoads = starting.Provider.Loads.Count;
                starting.Dispose();

                var consent2 = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                using var retrying = new AdsRig(consent: consent2);
                retrying.Controller.Start();
                retrying.Provider.InitCallback(false);
                consent2.CanRequestAds = false;
                retrying.Controller.ShowPrivacyOptions(null);
                retrying.Provider.InitCallback = null;
                retrying.Scheduler.Advance(60);
                var afterRetry = retrying.Controller.State;
                bool retried = retrying.Provider.InitCallback != null;
                retrying.Dispose();

                var consent3 = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                using var failing = new AdsRig(consent: consent3);
                failing.Controller.Start();
                consent3.CanRequestAds = false;
                failing.Controller.ShowPrivacyOptions(null);
                failing.Provider.InitCallback(false);
                failing.Provider.InitCallback = null;
                failing.Scheduler.Advance(60);
                Check("AC63. WithdrawingConsentWhileTheSdkStarts_LoadsNoAd",
                    afterStart == AdController.Phase.ConsentRefused && startingLoads == 0
                        && afterRetry == AdController.Phase.ConsentRefused && !retried
                        && failing.Controller.State == AdController.Phase.ConsentRefused && failing.Provider.InitCallback == null,
                    $"start answered after the withdrawal: state={afterStart} loads={startingLoads}; retry pending at the withdrawal: state={afterRetry} start retried={retried}; "
                    + $"start failed after the withdrawal: state={failing.Controller.State} start retried={failing.Provider.InitCallback != null} "
                    + "(expected ConsentRefused 0; ConsentRefused False; ConsentRefused False)");
            }

            // issue #328: the player grants again in the privacy options after a withdrawal: ads run again
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                using var rig = new AdsRig(consent: consent).Running();
                consent.CanRequestAds = false;
                rig.Controller.ShowPrivacyOptions(null);
                consent.CanRequestAds = true;
                rig.Controller.ShowPrivacyOptions(null);
                rig.Provider.InitCallback(true); // a started SDK answers a second Initialize at once
                bool ready = rig.Controller.IsReady("shop_refill");
                var r = rig.Show("shop_refill");
                Check("AC64. GrantingAgainAfterAWithdrawal_StartsAdsAgain",
                    rig.Controller.State == AdController.Phase.Running && ready && r == null && rig.Provider.ShowPlacement == "shop_refill",
                    $"state={rig.Controller.State} IsReady={ready} show={r?.ToString() ?? "open"} reached the provider={rig.Provider.ShowPlacement == "shop_refill"} (expected Running True open True)");
            }

            // issue #328: a load still out at the withdrawal, which the SDK may never answer, left its slot Loading, so the next grant loaded nothing
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                using var rig = new AdsRig(consent: consent);
                rig.Controller.Start();
                rig.Provider.InitCallback(true); // both formats' loads are out and never answer
                int before = rig.Provider.Loads.Count;
                consent.CanRequestAds = false;
                rig.Controller.ShowPrivacyOptions(null);
                consent.CanRequestAds = true;
                rig.Controller.ShowPrivacyOptions(null);
                rig.Provider.InitCallback(true);
                int asked = rig.Provider.Loads.Count - before;
                Check("AC65. ALoadOutAtAWithdrawal_IsAskedAgainAtTheNextGrant", before == 2 && asked == 2,
                    $"loads at the start={before}, after the grant={asked} (expected 2 2; pre-fix 2 0)");
            }

            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                var cfg = AdsRig.Config(null, BuildKind.Development, AdsRig.Rewarded, AdsRig.LevelEnd, new AdPlacement("hud", AdFormat.Banner));
                using var rig = new AdsRig(cfg, consent: consent).Running();
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom);
                consent.Answer(false);
                bool refused = rig.Controller.State == AdController.Phase.ConsentRefused && !rig.Provider.BannerShown
                    && !rig.Controller.IsReady("shop_refill") && rig.Show("hud") == AdResult.NotReady;
                consent.Answer(true);
                bool starting = rig.Controller.State == AdController.Phase.Initializing;
                rig.Provider.InitCallback(true);
                Check("AC66. ExternalConsentWithdrawalStopsAds_AndALateGrantRestartsThem",
                    refused && starting && rig.Controller.IsReady("shop_refill"),
                    $"withdrawal stopped ads={refused}, grant started SDK={starting}, state={rig.Controller.State}");
            }
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true };
                using var rig = new AdsRig(consent: consent);
                rig.Controller.Start();
                rig.Provider.InitCallback(false);
                consent.Answer(false);
                rig.Provider.InitCallback = null;
                rig.Scheduler.Advance(60);
                Check("AC67. ExternalConsentWithdrawalStopsAFailedStartsRetry",
                    rig.Controller.State == AdController.Phase.ConsentRefused && rig.Provider.InitCallback == null,
                    $"state={rig.Controller.State}, retried={rig.Provider.InitCallback != null}");
            }
            {
                var consent = new AdsFakeConsent { NotifyOnPrivacyClose = true };
                using var rig = new AdsRig(consent: consent);
                rig.Controller.Start();
                consent.Answer(false);
                bool once = rig.Telemetry.Count(AdEventKind.ConsentRefused) == 1;
                consent.CanRequestAds = true;
                rig.Controller.ShowPrivacyOptions(null);
                rig.Provider.InitCallback(true);
                consent.CanRequestAds = false;
                rig.Controller.ShowPrivacyOptions(null);
                bool beforeClose = rig.Telemetry.Count(AdEventKind.ConsentRefused) == 2 && rig.Provider.InitializeCalls == 1;
                consent.NotifyOnPrivacyClose = false;
                consent.NotifyAfterPrivacyClose = true;
                consent.CanRequestAds = true;
                rig.Controller.ShowPrivacyOptions(null);
                rig.Provider.InitCallback(true);
                consent.CanRequestAds = false;
                rig.Controller.ShowPrivacyOptions(null);
                Check("AC68. ConsentNotificationsBeforeOrAfterPrivacyClose_RefuseAndInitializeOncePerChange",
                    once && beforeClose && rig.Telemetry.Count(AdEventKind.ConsentRefused) == 3 && rig.Provider.InitializeCalls == 2,
                    $"request refused once={once}, before-close effects once={beforeClose}, refusals={rig.Telemetry.Count(AdEventKind.ConsentRefused)}, initializations={rig.Provider.InitializeCalls}");
            }
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = true, SkipPrivacyClose = true };
                using var rig = new AdsRig(consent: consent).Running();
                rig.Controller.ShowPrivacyOptions(null);
                rig.Controller.ShowPrivacyOptions(null);
                consent.Answer(false);
                bool refused = rig.Controller.State == AdController.Phase.ConsentRefused;
                consent.Answer(true);
                Check("AC70. PrivacyOptionsThatNeverClose_DoNotBlockExternalConsentChanges",
                    refused && rig.Controller.State == AdController.Phase.Initializing && rig.Provider.InitializeCalls == 2,
                    $"withdrawal refused={refused}, state={rig.Controller.State}, initializations={rig.Provider.InitializeCalls}");
            }
            {
                var consent = new AdsFakeConsent { Completed = true, CanRequestAds = false };
                var main = new AdsQueuedMainThread();
                var clock = new AdsClock();
                var provider = new AdsFakeProvider();
                using var controller = new AdController(AdsRig.Config(), provider, clock, new AdsVisibility(), main,
                    new AdsScheduler(clock), new AdsStore(), new AdsTelemetry(), consent);
                controller.Start();
                main.IsCurrent = false;
                consent.Answer(true);
                bool deferred = controller.State == AdController.Phase.ConsentRefused && provider.InitCallback == null;
                main.Drain();
                bool started = controller.State == AdController.Phase.Initializing;
                main.IsCurrent = false;
                consent.Answer(false);
                controller.Dispose();
                bool detached = consent.Subscribers == 0;
                main.Drain();
                consent.Answer(true);
                Check("AC69. ExternalConsentChanges_RunOnMainThread_AndDetachOnDispose",
                    deferred && started && detached && controller.State == AdController.Phase.Stopped,
                    $"deferred={deferred}, started={started}, detached={detached}, state={controller.State}");
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

            // durable reward receipt: written before the callback, survives a new controller on the same store, cleared on acknowledge
            {
                var store = new AdsStore();
                RewardReceipt? seenInCallback = null;
                var clock = new AdsClock();
                var scheduler = new AdsScheduler(clock);
                var provider = new AdsFakeProvider();
                var first = new AdController(AdsRig.Config(), provider, clock, new AdsVisibility(), new AdsMainThread(), scheduler, store, new AdsTelemetry());
                first.Start(); provider.InitCallback(true); provider.Fill(AdFormat.Rewarded);
                first.Show("shop_refill", _ => seenInCallback = first.PendingReward);
                provider.ShowCallback(AdResult.Rewarded);
                Check("AC34. RewardReceipt_WrittenBeforeCallback", seenInCallback.HasValue && seenInCallback.Value.Placement == "shop_refill", seenInCallback?.Id ?? "none");
                first.Dispose();
                var second = new AdController(AdsRig.Config(), new AdsFakeProvider(), clock, new AdsVisibility(), new AdsMainThread(), scheduler, store, new AdsTelemetry());
                var pending = second.PendingReward;
                Check("AC35. RewardReceipt_SurvivesRestart", pending.HasValue && pending.Value.Id == seenInCallback.Value.Id, "same receipt on a new controller over the same store");
                second.AcknowledgeReward("wrong-id");
                bool kept = second.PendingReward.HasValue;
                second.AcknowledgeReward(pending.Value.Id);
                Check("AC36. RewardReceipt_ClearedOnlyByItsId", kept && !second.PendingReward.HasValue, "wrong id ignored, right id cleared");
                second.Dispose();
            }

            // #413 supersedes the old #178 loss warning: appending must preserve both receipts.
            using (var rig = new AdsRig().Running())
            {
                rig.Show("shop_refill"); rig.Provider.ShowCallback(AdResult.Rewarded);
                var first = rig.Controller.PendingReward;
                rig.Provider.Fill(AdFormat.Rewarded);
                rig.Show("shop_refill"); rig.Provider.ShowCallback(AdResult.Rewarded);
                bool keptFirst = rig.Controller.PendingReward?.Id == first?.Id;
                rig.Controller.AcknowledgeReward(first.Value.Id);
                var second = rig.Controller.PendingReward;
                rig.Controller.AcknowledgeReward(second.Value.Id);
                Check("AC54. ARewardBehindAnUnacknowledgedReceipt_PreservesBoth",
                    keptFirst && first.HasValue && second.HasValue && first.Value.Id != second.Value.Id
                    && !rig.Controller.PendingReward.HasValue && rig.Telemetry.Count(AdEventKind.RewardReplaced) == 0,
                    "both identities survive, acknowledgement drains each once, no replacement/loss event");
            }

            // late attachment: the bridge wires the store's entitlement and remote config after the controller started
            {
                using var rig = new AdsRig().Running();
                rig.Scheduler.Advance(61);
                var adFree = new AdsAdFree { AdFree = true };
                rig.Controller.SetAdFreeEntitlement(adFree);
                bool blocked = !rig.Controller.IsReady("level_end") && rig.Controller.IsReady("shop_refill");
                rig.Controller.SetAdFreeEntitlement(null);
                Check("AC37. AdFree_AttachedLate_AppliesAndDetaches", blocked && rig.Controller.IsReady("level_end"), "interstitial off while attached, back when detached");
                var remote = new AdsRemote { KillSwitch = true };
                rig.Controller.SetRemoteConfig(remote);
                bool killed = rig.Controller.KillSwitch && !rig.Controller.IsReady("shop_refill");
                rig.Controller.SetRemoteConfig(null);
                Check("AC38. Remote_AttachedLate_AppliesAndDetaches", killed && !rig.Controller.KillSwitch, "kill switch applied on attach, cleared on detach");
            }
            // Removing the remote configuration cleared the kill switch with no event and no load: the release log had no line
            // and nothing loaded until a show was asked for (#234)
            {
                using var rig = new AdsRig();
                rig.Controller.SetRemoteConfig(new AdsRemote { KillSwitch = true });
                rig.Controller.Start();
                rig.Provider.InitCallback(true);
                int loadsWhileKilled = rig.Provider.Loads.Count;
                rig.Controller.SetRemoteConfig(null);
                bool loaded = rig.Provider.Loads.Contains(AdFormat.Rewarded) && rig.Provider.Loads.Contains(AdFormat.Interstitial);
                rig.Provider.Fill(AdFormat.Rewarded);
                var switches = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.KillSwitch);
                Check("AC59. RemovingTheRemoteConfig_RecordsTheKillSwitchOff_AndLoadsAgain",
                    loadsWhileKilled == 0 && loaded && rig.Controller.IsReady("shop_refill") && switches.Count == 2 && switches[1].Detail == "off",
                    $"loads while killed={loadsWhileKilled} both loaded after={loaded} ready={rig.Controller.IsReady("shop_refill")} KillSwitch events=[{string.Join(" | ", switches)}] (pre-fix: no off event, no load)");
            }

            // a provider's Opened and Revenue carry the format only (LevelPlay: no placement); the events still name the placement the game asked for
            {
                using var rig = new AdsRig().Running();
                AdRevenue? seen = null;
                rig.Controller.Revenue += r => seen = r;
                rig.Show("shop_refill");
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Provider.Pay(new AdRevenue("ironsourceads", "", AdFormat.Rewarded, 0, "USD", "exact"));
                rig.Provider.Pay(new AdRevenue("ironsourceads", "", AdFormat.Interstitial, 0, "USD", "exact"));
                var shown = rig.Telemetry.Events.Find(e => e.Kind == AdEventKind.Shown);
                var revenue = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.Revenue);
                Check("AC39. ShownAndRevenue_CarryThePlacementTheGameAsked", shown.Placement == "shop_refill" && revenue.Count == 2 && revenue[0].Placement == "shop_refill" && revenue[1].Placement == "" && seen.HasValue && seen.Value.Format == AdFormat.Interstitial && seen.Value.Placement == "",
                    $"shown '{shown.Placement}', rewarded revenue '{revenue[0].Placement}' after Closed, interstitial never shown stays '{revenue[1].Placement}'");
            }

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

            // #195: the ad-free entitlement left banners on screen and showable, and inside a pacing window the guard answered an
            // ad-free interstitial or a kill-switched show with a violation instead of NotReady
            {
                var adFree = new AdsAdFree();
                var remote = new AdsRemote();
                var cfg = AdsRig.Config(null, BuildKind.Development, AdsRig.Rewarded, AdsRig.LevelEnd, new AdPlacement("hud", AdFormat.Banner));
                using var rig = new AdsRig(cfg, adFree: adFree, remote: remote).Running();
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom);
                bool shownBefore = rig.Provider.BannerShown;
                adFree.AdFree = true;
                bool hidden = !rig.Provider.BannerShown;
                AdResult? banner = null;
                rig.Controller.ShowBanner("hud", BannerPosition.Top, r => banner = r);
                var viaShow = rig.Show("hud");
                Check("AC55. AdFree_HidesAndRefusesBanners_KeepsRewarded",
                    shownBefore && hidden && !rig.Controller.IsReady("hud") && banner == AdResult.NotReady && viaShow == AdResult.NotReady && !rig.Provider.BannerShown && rig.Controller.IsReady("shop_refill"),
                    $"shown before={shownBefore}, hidden on ad-free={hidden}, IsReady(hud)={rig.Controller.IsReady("hud")}, ShowBanner={banner}, Show(hud)={viaShow}, rewarded ready={rig.Controller.IsReady("shop_refill")}");

                int violations = rig.Violations.Count;
                var paced = rig.Show("level_end"); // the clock is at 0: inside the 60 s first-show floor
                adFree.AdFree = false;
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom);
                remote.KillSwitch = true;
                bool killHidden = !rig.Provider.BannerShown;
                var killed = rig.Show("level_end");
                Check("AC56. AdFreeAndKillSwitch_AnswerNotReady_WithoutAViolation_InsideAPacingWindow",
                    paced == AdResult.NotReady && killed == AdResult.NotReady && rig.Violations.Count == violations && killHidden,
                    $"ad-free interstitial={paced}, kill-switched={killed}, violations={rig.Violations.Count - violations} (expected 0), banner hidden by the kill switch={killHidden}");
            }

            // #282: a banner's IsReady answered true while ShowBanner was blocked, no ReadyChanged followed the end of its
            // first-show delay, and a banner show was never recorded, so the placement's cooldown and caps never engaged
            {
                var cfg = AdsRig.Config(null, BuildKind.Development, new AdPlacement("hud", AdFormat.Banner, cooldownSeconds: 60, firstShowAfterSeconds: 30));
                using var rig = new AdsRig(cfg);
                rig.Controller.Start();
                rig.Provider.InitCallback(true);
                bool readyEarly = rig.Controller.IsReady("hud");
                AdResult? early = null, first = null, second = null;
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom, r => early = r);
                int changes = rig.ReadyChanges;
                rig.Scheduler.Advance(31);
                bool raisedAtDelayEnd = rig.ReadyChanges > changes;
                bool readyAfterDelay = rig.Controller.IsReady("hud");
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom, r => first = r);
                rig.Scheduler.Advance(1);
                rig.Controller.ShowBanner("hud", BannerPosition.Bottom, r => second = r);
                bool readyInCooldown = rig.Controller.IsReady("hud");
                changes = rig.ReadyChanges;
                rig.Scheduler.Advance(60);
                bool raisedAtCooldownEnd = rig.ReadyChanges > changes && rig.Controller.IsReady("hud");
                Check("AC60. Banner_IsReadyGivesTheGuardsAnswer_AndAShowStartsItsCooldown",
                    !readyEarly && early == AdResult.Blocked && raisedAtDelayEnd && readyAfterDelay && first == AdResult.Closed
                        && second == AdResult.Blocked && !readyInCooldown && raisedAtCooldownEnd,
                    $"at 0 s IsReady={readyEarly} ShowBanner={early}; at 31 s ReadyChanged={raisedAtDelayEnd} IsReady={readyAfterDelay} ShowBanner={first}; "
                    + $"1 s later ShowBanner={second} IsReady={readyInCooldown}; cooldown end ReadyChanged and ready={raisedAtCooldownEnd} "
                    + "(expected False Blocked; True True Closed; Blocked False; True)");
            }

            // issue #327: the generic Show of a banner reached the provider without recording the show or arming the wake, so the
            // placement's cooldown and caps held for ShowBanner only
            {
                var cfg = AdsRig.Config(null, BuildKind.Development, new AdPlacement("hud", AdFormat.Banner, cooldownSeconds: 30, sessionCap: 2));
                using var rig = new AdsRig(cfg);
                rig.Controller.Start();
                rig.Provider.InitCallback(true);
                var first = rig.Show("hud");
                rig.Scheduler.Advance(1);
                var inCooldown = rig.Show("hud");
                int changes = rig.ReadyChanges;
                rig.Scheduler.Advance(30);
                bool raisedAtCooldownEnd = rig.ReadyChanges > changes && rig.Controller.IsReady("hud");
                var second = rig.Show("hud");
                rig.Scheduler.Advance(31);
                AdResult? viaShowBanner = null;
                rig.Controller.ShowBanner("hud", BannerPosition.Top, r => viaShowBanner = r);
                Check("AC61. GenericShowOfABanner_CountsTowardItsCooldownAndCaps",
                    first == AdResult.Closed && inCooldown == AdResult.Blocked && raisedAtCooldownEnd && second == AdResult.Closed && viaShowBanner == AdResult.Blocked,
                    $"Show={first}; 1 s later Show={inCooldown}; cooldown end ReadyChanged and ready={raisedAtCooldownEnd}; Show={second}; past the cooldown ShowBanner={viaShowBanner} "
                    + "(expected Closed; Blocked; True; Closed; Blocked by the session cap of 2; pre-fix the generic Show recorded nothing)");
            }

            // readiness polled every frame reaches the provider once per window; Show still asks live (issue #32)
            using (var rig = new AdsRig().Running())
            {
                rig.Provider.IsReadyCalls = 0;
                bool allReady = true;
                for (int i = 0; i < 100; i++) allReady &= rig.Controller.IsReady("shop_refill");
                int polled = rig.Provider.IsReadyCalls;
                rig.Clock.Advance(1);
                rig.Controller.IsReady("shop_refill");
                int afterWindow = rig.Provider.IsReadyCalls;
                rig.Provider.Ready.Remove(AdFormat.Rewarded); // the loaded ad expires inside the next window
                bool cachedAnswer = rig.Controller.IsReady("shop_refill");
                int loads = rig.Provider.Loads.Count;
                var r = rig.Show("shop_refill");
                bool reloaded = rig.Provider.Loads.Count == loads + 1 && rig.Telemetry.Count(AdEventKind.Expired) == 1; // #175
                Check("AC40. IsReady_PolledEveryFrame_AsksProviderOncePerWindow",
                    allReady && polled <= 1 && afterWindow == polled + 1 && cachedAnswer && r == AdResult.NotReady && rig.Provider.ShowPlacement == null && reloaded,
                    $"100 polls -> {polled} provider call(s) (at most 1; pre-fix 100), one more after the window ({afterWindow}); an ad that expired inside the window: IsReady={cachedAnswer}, Show={r} without reaching the provider, reload asked={reloaded} (#175; before, none until a restart)");
            }

            // a provider whose Show threw left the full-screen flag set and the slot Showing: every later full-screen show was
            // blocked by NoStackedAds, the format never reloaded and onResult never ran (issue #151)
            using (var rig = new AdsRig().Running())
            {
                var results = new List<AdResult>();
                Exception thrown = null;
                rig.Provider.ShowThrows = new InvalidOperationException("JNI show failed");
                try { rig.Controller.Show("shop_refill", r => results.Add(r)); }
                catch (InvalidOperationException ex) { thrown = ex; }
                var failed = rig.Telemetry.Events.Find(e => e.Kind == AdEventKind.ShowFailed);
                bool named = failed.Kind == AdEventKind.ShowFailed && failed.Detail != null && failed.Detail.Contains("JNI show failed");
                bool ready = rig.Controller.IsReady("shop_refill"); // the slot asked the provider again, which still holds its ad
                var next = rig.Show("shop_refill");
                Check("AC41. ProviderShowThrows_AnswersFailedOnce_FreesTheScreen_Rethrows",
                    thrown != null && results.Count == 1 && results[0] == AdResult.Failed && named
                    && ready && next == null && rig.Provider.ShowPlacement == "shop_refill" && rig.Violations.Count == 0,
                    $"thrown={thrown?.Message ?? "none"} results=[{string.Join(",", results)}] ShowFailed names it={named}; IsReady={ready}, next show={next?.ToString() ?? "open"}, violations={rig.Violations.Count} (pre-fix: no result, next show Blocked by NoStackedAds)");
            }

            // A host that runs no ads (issue #165): the bridge's Initialize starts a Stopped controller, and the idle host's simulator paid every reward
            using (var rig = new AdsRig(provider: new NoAdsProvider()))
            {
                string holderBefore = MediationLock.Holder;
                rig.Controller.Start();
                rig.Controller.Start();
                var result = rig.Show("shop_refill");
                bool ready = rig.Controller.IsReady("shop_refill");
                Check("AC42. NoAdsProvider_ControllerNeverStarts_TakesNoLock_EveryShowNotReady",
                    rig.Controller.State == AdController.Phase.Stopped && MediationLock.Holder == holderBefore && result == AdResult.NotReady && !ready && rig.Violations.Count == 0,
                    $"state={rig.Controller.State}, lock holder={MediationLock.Holder ?? "-"} (before {holderBefore ?? "-"}), show={result?.ToString() ?? "open"}, IsReady={ready}, violations={rig.Violations.Count} (without the guard: the lock is taken as \"none\" and the failed init retries)");
            }

            // Issue #164: a failed start and a refused consent changed the state and recorded nothing, so no log and no analytics sink saw them
            using (var rig = new AdsRig())
            {
                rig.Controller.Start();
                rig.Provider.InitCallback(false);
                var failed = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.InitFailed);
                rig.Provider.InitCallback = null;
                rig.Scheduler.Advance(2);
                rig.Provider.InitCallback(true);
                var started = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.Initialized);
                var consent = new AdsFakeConsent();
                using var refused = new AdsRig(consent: consent);
                refused.Controller.Start();
                consent.Answer(false);
                var refusedAlready = new AdsFakeConsent { Completed = true, CanRequestAds = false };
                using var refusedAtStart = new AdsRig(consent: refusedAlready);
                refusedAtStart.Controller.Start();
                Check("AC43. FailedStartRecoveryAndRefusedConsent_AreRecorded",
                    failed.Count == 1 && failed[0].Detail == "attempt 1, retry in 2 s"
                    && started.Count == 1 && started[0].Detail == "after 1 failed start(s)"
                    && refused.Telemetry.Count(AdEventKind.ConsentRefused) == 1 && refusedAtStart.Telemetry.Count(AdEventKind.ConsentRefused) == 1,
                    $"InitFailed=[{string.Join(" | ", failed)}] Initialized=[{string.Join(" | ", started)}] ConsentRefused after the flow={refused.Telemetry.Count(AdEventKind.ConsentRefused)}, at start={refusedAtStart.Telemetry.Count(AdEventKind.ConsentRefused)}");
            }

            // Issue #164: a release player's log had no line for any ad failure; the log writes each cause once, every failed show, and no success
            {
                var lines = new List<string>();
                var log = new AdFailureLog(lines.Add, violations: true);
                for (int i = 0; i < 3; i++) log.Record(new AdEvent(AdEventKind.NoFill, AdFormat.Rewarded, "", "509 Mediation No fill"));
                log.Record(new AdEvent(AdEventKind.NoFill, AdFormat.Interstitial, "", "509 Mediation No fill"));
                log.Record(new AdEvent(AdEventKind.LoadFailed, AdFormat.Rewarded, "", "simulated SDK error"));
                log.Record(new AdEvent(AdEventKind.ShowFailed, AdFormat.Rewarded, "shop_refill"));
                log.Record(new AdEvent(AdEventKind.ShowFailed, AdFormat.Rewarded, "shop_refill"));
                for (int i = 0; i < 3; i++) log.Record(new AdEvent(AdEventKind.InitFailed, AdFormat.Interstitial, "", "attempt " + (i + 1) + ", retry in 2 s"));
                log.Record(new AdEvent(AdEventKind.Initialized, AdFormat.Interstitial, "", "after 3 failed start(s)"));
                log.Record(new AdEvent(AdEventKind.Initialized, AdFormat.Interstitial, ""));
                log.Record(new AdEvent(AdEventKind.ConsentRefused, AdFormat.Interstitial, "", "no consent"));
                log.Record(new AdEvent(AdEventKind.ConsentRefused, AdFormat.Interstitial, "", "no consent"));
                log.Record(new AdEvent(AdEventKind.Violation, AdFormat.Interstitial, "level_end", "InterstitialFrequency: 16.1 s since the last interstitial"));
                log.Record(new AdEvent(AdEventKind.Violation, AdFormat.Interstitial, "level_end", "InterstitialFrequency: 30.4 s since the last interstitial"));
                foreach (var kind in new[] { AdEventKind.Loaded, AdEventKind.Shown, AdEventKind.Rewarded, AdEventKind.Closed, AdEventKind.Revenue, AdEventKind.NotReady })
                    log.Record(new AdEvent(kind, AdFormat.Rewarded, "shop_refill", "x"));
                // a configuration that changes what runs is written in every build, each turn (#234)
                log.Record(new AdEvent(AdEventKind.KillSwitch, AdFormat.Interstitial, "", "on"));
                log.Record(new AdEvent(AdEventKind.KillSwitch, AdFormat.Interstitial, "", "off"));
                var expected = new[]
                {
                    "[Nexus Ads] NoFill Rewarded '': 509 Mediation No fill (first this session; repeats are not logged)",
                    "[Nexus Ads] NoFill Interstitial '': 509 Mediation No fill (first this session; repeats are not logged)",
                    "[Nexus Ads] LoadFailed Rewarded '': simulated SDK error (first this session; repeats are not logged)",
                    "[Nexus Ads] ShowFailed Rewarded 'shop_refill'",
                    "[Nexus Ads] ShowFailed Rewarded 'shop_refill'",
                    "[Nexus Ads] InitFailed: attempt 1, retry in 2 s (later failed starts are not logged until the SDK starts)",
                    "[Nexus Ads] Initialized: after 3 failed start(s)",
                    "[Nexus Ads] ConsentRefused: no consent (first this session; repeats are not logged)",
                    "[Nexus Ads] PolicyViolation InterstitialFrequency (Interstitial 'level_end'): 16.1 s since the last interstitial (first for this rule and placement this session; repeats are not logged)",
                    "[Nexus Ads] KillSwitch on: the remote configuration turned every ad off",
                    "[Nexus Ads] KillSwitch off: ads load and show again",
                };
                var development = new List<string>();
                var devLog = new AdFailureLog(development.Add, violations: false);
                devLog.Record(new AdEvent(AdEventKind.Violation, AdFormat.Interstitial, "level_end", "InterstitialFrequency: 16.1 s"));
                var capped = new List<string>();
                var capLog = new AdFailureLog(capped.Add, violations: false);
                for (int i = 0; i < AdFailureLog.MaxCauses + 10; i++) capLog.Record(new AdEvent(AdEventKind.LoadFailed, AdFormat.Rewarded, "", "error id " + i));
                Check("AC44. FailureLog_WritesEachCauseOnce_EveryFailedShow_TheRecovery_NoSuccess",
                    string.Join("|", lines) == string.Join("|", expected) && development.Count == 0 && capped.Count == AdFailureLog.MaxCauses + 1 && capped[^1].Contains("new ones are not logged"),
                    $"lines=[{string.Join(" | ", lines)}] violation outside release={development.Count} capped={capped.Count}");
            }
        }

        // ----- ReadyChanged at the end of a pacing window (issue #95)

        private static void RunPacingWake()
        {
            // A UI that follows ReadyChanged (AdRewardedButton) stayed hidden after a cooldown: nothing raised the event when it ran out.
            // The wake is armed 0.01 s past a window's end, and 29.99 + 0.02 rounds below that in double arithmetic: each
            // case steps to 0.01 s before the end (no event yet), then 0.05 s on, past the margin (exactly one event).
            var policyNoFirst = new AdPolicy(30, 0, 30, 0, 0, 30);

            using (var rig = new AdsRig(AdsRig.Config(placements: new AdPlacement("reward_cd", AdFormat.Rewarded, cooldownSeconds: 30))).Running())
            {
                rig.Show("reward_cd");
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Provider.Fill(AdFormat.Rewarded);
                int before = rig.ReadyChanges, callsBefore = rig.Provider.IsReadyCalls, callsAtEvent = -1;
                bool readyInHandler = false;
                rig.Controller.ReadyChanged += () => { callsAtEvent = rig.Provider.IsReadyCalls; readyInHandler = rig.Controller.IsReady("reward_cd"); };
                rig.Scheduler.Advance(29.99);
                int inside = rig.ReadyChanges - before;
                rig.Scheduler.Advance(0.05);
                Check("PW1. RewardedCooldownEnd_RaisesReadyChanged_Once_WithoutAskingTheProvider",
                    inside == 0 && rig.ReadyChanges - before == 1 && readyInHandler && callsAtEvent == callsBefore && rig.Scheduler.Pending == 0,
                    $"events inside the cooldown={inside}, at its end={rig.ReadyChanges - before} (expected 1), IsReady in the handler={readyInHandler}, provider calls before the handler={callsAtEvent - callsBefore} (expected 0), wakes left={rig.Scheduler.Pending}");
            }

            using (var rig = new AdsRig(AdsRig.Config(policyNoFirst, placements: AdsRig.LevelEnd)).Running())
            {
                rig.Show("level_end");
                rig.Provider.ShowCallback(AdResult.Closed);
                rig.Provider.Fill(AdFormat.Interstitial);
                int before = rig.ReadyChanges;
                bool readyInHandler = false;
                rig.Controller.ReadyChanged += () => readyInHandler = rig.Controller.IsReady("level_end");
                rig.Scheduler.Advance(29.99);
                int inside = rig.ReadyChanges - before;
                rig.Scheduler.Advance(0.05);
                Check("PW2. InterstitialGapEnd_RaisesReadyChanged_Once", inside == 0 && rig.ReadyChanges - before == 1 && readyInHandler,
                    $"events inside the 30 s gap={inside}, at its end={rig.ReadyChanges - before} (expected 1), IsReady in the handler={readyInHandler}");
            }

            using (var rig = new AdsRig(AdsRig.Config(placements: new AdPlacement("reward_late", AdFormat.Rewarded, firstShowAfterSeconds: 45))).Running())
            {
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(44.99);
                int inside = rig.ReadyChanges - before;
                rig.Scheduler.Advance(0.05);
                Check("PW3. PlacementFirstShowDelayEnd_RaisesReadyChanged_Once", inside == 0 && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("reward_late"),
                    $"events inside the 45 s delay={inside}, at its end={rig.ReadyChanges - before} (expected 1)");
            }

            using (var rig = new AdsRig(AdsRig.Config(placements: AdsRig.LevelEnd)).Running())
            {
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(59.99);
                int inside = rig.ReadyChanges - before;
                rig.Scheduler.Advance(0.05);
                Check("PW4. FirstInterstitialDelayEnd_RaisesReadyChanged_Once", inside == 0 && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("level_end"),
                    $"default policy, events inside the first 60 s={inside}, at their end={rig.ReadyChanges - before} (expected 1)");
            }

            // Two windows on one interstitial: the gap ends at 30 s, the after-rewarded wait at 40 s; it flips once, at 40 s.
            using (var rig = new AdsRig(AdsRig.Config(policyNoFirst, placements: new[] { AdsRig.Rewarded, AdsRig.LevelEnd })).Running())
            {
                rig.Show("level_end");
                rig.Provider.ShowCallback(AdResult.Closed);
                rig.Provider.Fill(AdFormat.Interstitial);
                rig.Scheduler.Advance(10);
                rig.Show("shop_refill");
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Provider.Fill(AdFormat.Rewarded);
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(25);
                int pastGap = rig.ReadyChanges - before;
                rig.Scheduler.Advance(5.05);
                Check("PW5. OverlappingWindows_RaiseOncePerFlip_NotPerWindowEnd",
                    pastGap == 0 && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("level_end") && rig.Scheduler.Pending == 0,
                    $"events when the gap ended at 30 s={pastGap} (expected 0, still waiting after the rewarded ad), at 40 s={rig.ReadyChanges - before} (expected 1), wakes left={rig.Scheduler.Pending}");
            }

            using (var rig = new AdsRig(AdsRig.Config(placements: new AdPlacement("reward_cd", AdFormat.Rewarded, cooldownSeconds: 30))).Running())
            {
                rig.Show("reward_cd");
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Provider.Fill(AdFormat.Rewarded);
                int armed = rig.Scheduler.Pending;
                rig.Controller.Dispose();
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(31);
                Check("PW6. Dispose_CancelsThePacingWake", armed == 1 && rig.Scheduler.Pending == 0 && rig.ReadyChanges == before,
                    $"wakes armed={armed}, after Dispose={rig.Scheduler.Pending}, events after Dispose={rig.ReadyChanges - before}");
            }

            using (var rig = new AdsRig(AdsRig.Config(placements: new AdPlacement("reward_day", AdFormat.Rewarded, dayCap: 1))).Running())
            {
                rig.Show("reward_day");
                rig.Provider.ShowCallback(AdResult.Rewarded);
                rig.Provider.Fill(AdFormat.Rewarded);
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(600);
                int sameDay = rig.ReadyChanges - before;
                rig.Clock.UtcDay++;
                rig.Scheduler.Advance(60);
                Check("PW7. DayCap_RaisesReadyChanged_OnceTheUtcDayChanges",
                    sameDay == 0 && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("reward_day") && rig.Scheduler.Pending == 0,
                    $"events in 10 min of the same day={sameDay}, after the day changed={rig.ReadyChanges - before} (expected 1), wakes left={rig.Scheduler.Pending}");
            }

            // #128: the lifetime-session threshold lasts the session like a cap, so the end of the first-seconds floor is no flip
            using (var rig = new AdsRig(AdsRig.Config(new AdPolicy(30, 60, 30, 0, 0, 30, 1), placements: AdsRig.LevelEnd)).Running())
            {
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(120);
                Check("PW8. LifetimeSessions_ArmNoPacingWake", rig.ReadyChanges == before && rig.Scheduler.Pending == 0 && !rig.Controller.IsReady("level_end"),
                    $"events in the first 2 min={rig.ReadyChanges - before} (expected 0), wakes left={rig.Scheduler.Pending}");
            }

            // #126: a touch just before the first-seconds floor ends holds the interstitial past it; the wake re-armed when the
            // floor ends covers the touch wait, so a UI that follows ReadyChanged learns when the interstitial is ready again.
            var touch = new AdsTouch();
            using (var rig = new AdsRig(AdsRig.Config(new AdPolicy(30, 60, 30, 0, 0, 30, 0, 2), placements: AdsRig.LevelEnd), touch: touch).Running())
            {
                rig.Scheduler.Advance(59.5);
                touch.LastTouchAt = rig.Clock.Seconds;
                rig.Scheduler.Advance(1.9);
                bool heldPastTheFloor = !rig.Controller.IsReady("level_end");
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(0.2);
                Check("PW9. TouchWait_RaisesReadyChanged_WhenItEnds",
                    heldPastTheFloor && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("level_end") && rig.Scheduler.Pending == 0,
                    $"held 1.9 s after the touch={heldPastTheFloor}, events when the 2 s wait ended={rig.ReadyChanges - before} (expected 1), wakes left={rig.Scheduler.Pending}");
            }

            // #126: a touch starts its wait at no event of the controller's, so with nothing else pending no wake is armed; the
            // Show it blocks arms one, and a game that keeps its break open hears ReadyChanged when the wait ends and asks again.
            var noFloorTouchWait = AdsRig.Config(new AdPolicy(30, 0, 30, 0, 0, 30, 0, 2), placements: AdsRig.LevelEnd);
            var showTouch = new AdsTouch();
            using (var rig = new AdsRig(noFloorTouchWait, touch: showTouch).Running())
            {
                rig.Scheduler.Advance(5);
                int idle = rig.Scheduler.Pending;
                showTouch.LastTouchAt = rig.Clock.Seconds;
                var r = rig.Show("level_end");
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(1.9);
                int early = rig.ReadyChanges - before;
                rig.Scheduler.Advance(0.2);
                Check("PW10. TouchWait_BlockedShow_RaisesReadyChanged_WhenItEnds",
                    idle == 0 && r == AdResult.Blocked && early == 0 && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("level_end") && rig.Scheduler.Pending == 0,
                    $"wakes before the touch={idle} (expected 0), show={r}, events before the wait ended={early}, when it ended={rig.ReadyChanges - before} (expected 1), wakes left={rig.Scheduler.Pending}");
            }
            var askTouch = new AdsTouch();
            using (var rig = new AdsRig(noFloorTouchWait, touch: askTouch).Running())
            {
                rig.Scheduler.Advance(5);
                askTouch.LastTouchAt = rig.Clock.Seconds;
                int scheduled = rig.Scheduler.Scheduled;
                bool anyReady = false;
                for (int i = 0; i < 20; i++) { anyReady |= rig.Controller.IsReady("level_end"); rig.Scheduler.Advance(0.05); }
                int armed = rig.Scheduler.Scheduled - scheduled;
                int before = rig.ReadyChanges;
                rig.Scheduler.Advance(1.05);
                Check("PW11. TouchWait_IsReadyMeetingIt_ArmsOneWake",
                    !anyReady && armed == 1 && rig.ReadyChanges - before == 1 && rig.Controller.IsReady("level_end") && rig.Scheduler.Pending == 0,
                    $"ready during the wait={anyReady}, wakes armed by 20 asks={armed} (expected 1), events when it ended={rig.ReadyChanges - before} (expected 1), wakes left={rig.Scheduler.Pending}");
            }
        }

        // ----- ProviderGate

        // ----- shows, consent and starts that never answer (issue #180)

        private static void RunWatchdog()
        {
            double wait = AdController.ShowAnswerSeconds;

            // A show the provider never opened nor answered held the full-screen flag for good: every later interstitial and
            // rewarded show was refused (NoStackedAds) until a restart, and nothing was logged
            using (var rig = new AdsRig().Running())
            {
                var results = new List<AdResult>();
                rig.Provider.RaiseOpened = false;
                rig.Controller.Show("shop_refill", r => results.Add(r));
                rig.Scheduler.Advance(wait - 0.1);
                int beforeDeadline = results.Count;
                rig.Scheduler.Advance(0.2);
                var failed = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.ShowFailed);
                rig.Provider.RaiseOpened = true;
                rig.Provider.Fill(AdFormat.Rewarded);
                var next = rig.Show("shop_refill");
                Check("AC45. ShowNeverOpened_EndsAsFailedOnce_AfterTheWindow_ScreenAndSlotFreed",
                    beforeDeadline == 0 && results.Count == 1 && results[0] == AdResult.Failed
                    && failed.Count == 1 && failed[0].Placement == "shop_refill" && failed[0].Detail != null && failed[0].Detail.Contains("no answer")
                    && next == null && rig.Violations.Count == 0,
                    $"results before the window={beforeDeadline} after=[{string.Join(",", results)}] ShowFailed=[{string.Join(" | ", failed)}] next show={next?.ToString() ?? "open"} violations={rig.Violations.Count} (pre-fix: no result, the next show Blocked)");
            }

            // An ad that took the focus (Android's ad activity) and whose close never arrived: the window counts from the app's return,
            // not from the request, so an ad watched for a minute is not cut short
            using (var rig = new AdsRig().Running())
            {
                var results = new List<AdResult>();
                rig.Controller.Show("shop_refill", r => results.Add(r));
                rig.Visibility.Visible = false;
                rig.Scheduler.Advance(60);
                int whileAway = results.Count;
                rig.Visibility.Visible = true;
                rig.Scheduler.Advance(wait - 0.1);
                int beforeDeadline = results.Count;
                rig.Scheduler.Advance(0.2);
                int afterDeadline = results.Count;
                // the provider answers after all: a reward is kept as the pending receipt the game pays at its next start
                rig.Provider.ShowCallback(AdResult.Rewarded);
                var pending = rig.Controller.PendingReward;
                Check("AC46. AdThatLeftTheFocus_EndsAsFailed_AfterTheWindowFromTheReturn_ALateRewardIsKept",
                    whileAway == 0 && beforeDeadline == 0 && afterDeadline == 1 && results.Count == 1 && results[0] == AdResult.Failed
                    && pending.HasValue && pending.Value.Placement == "shop_refill",
                    $"results away={whileAway} before the window={beforeDeadline} after={afterDeadline} all=[{string.Join(",", results)}] pending={(pending.HasValue ? pending.Value.Placement : "none")}");
            }

            // An ad shown in the game's own window (the editor's simulator, which waits for a button) keeps the focus: no window, it waits
            using (var rig = new AdsRig().Running())
            {
                var results = new List<AdResult>();
                rig.Controller.Show("shop_refill", r => results.Add(r));
                rig.Scheduler.Advance(10 * wait);
                int waited = results.Count;
                rig.Provider.ShowCallback(AdResult.Rewarded);
                Check("AC47. AdOpenInTheGamesWindow_WaitsForTheProvider",
                    waited == 0 && results.Count == 1 && results[0] == AdResult.Rewarded && rig.Telemetry.Count(AdEventKind.ShowFailed) == 0,
                    $"results after {10 * wait} s={waited} then=[{string.Join(",", results)}] ShowFailed={rig.Telemetry.Count(AdEventKind.ShowFailed)}");
            }

            // A consent flow or an SDK start that never answered left the controller waiting, silently in a release player
            {
                var consent = new AdsFakeConsent();
                using var rig = new AdsRig(consent: consent);
                rig.Controller.Start();
                rig.Scheduler.Advance(AdController.AnswerWaitSeconds * 3);
                var consentRows = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.Unanswered);
                var stateWhileConsent = rig.Controller.State;
                consent.Answer(true);
                rig.Scheduler.Advance(AdController.AnswerWaitSeconds * 3);
                var rows = rig.Telemetry.Events.FindAll(e => e.Kind == AdEventKind.Unanswered);
                var stateWhileStart = rig.Controller.State;
                rig.Provider.InitCallback(true);
                var lines = new List<string>();
                var log = new AdFailureLog(lines.Add, violations: false);
                foreach (var row in rows) { log.Record(row); log.Record(row); }
                Check("AC48. ConsentAndStartThatNeverAnswer_AreRecordedOnceEach_TheWaitGoesOn",
                    consentRows.Count == 1 && consentRows[0].Detail.Contains("consent") && stateWhileConsent == AdController.Phase.AwaitingConsent
                    && rows.Count == 2 && rows[1].Detail.Contains("SDK") && stateWhileStart == AdController.Phase.Initializing
                    && rig.Controller.State == AdController.Phase.Running && lines.Count == 2,
                    $"rows=[{string.Join(" | ", rows)}] states={stateWhileConsent}/{stateWhileStart}/{rig.Controller.State} log=[{string.Join(" | ", lines)}]");
            }
        }

        // ----- a dropped ad reloads (#175); a throw while a show is answered does not cost the game its result (#176)

        private static void RunAnswerIsolation()
        {
            // AdMob drops a loaded ad after an hour, or when consent is withdrawn, without a load failure: the slot stayed Ready, IsReady
            // false, the rewarded button hidden and nothing loaded until a restart
            using (var rig = new AdsRig().Running())
            {
                rig.Clock.Advance(1);
                bool before = rig.Controller.IsReady("shop_refill");
                int loads = rig.Provider.Loads.Count;
                rig.Provider.Ready.Remove(AdFormat.Rewarded);
                rig.Clock.Advance(1); // past the probe's reuse window
                bool dropped = rig.Controller.IsReady("shop_refill");
                bool asked = rig.Provider.Loads.Count == loads + 1 && rig.Provider.Loads[^1] == AdFormat.Rewarded;
                int changes = rig.ReadyChanges;
                rig.Provider.Fill(AdFormat.Rewarded);
                Check("AC49. ADroppedAd_SeenByIsReady_Reloads",
                    before && !dropped && asked && rig.Telemetry.Count(AdEventKind.Expired) == 1 && rig.Controller.IsReady("shop_refill") && rig.ReadyChanges > changes,
                    $"ready before={before}, after the drop={dropped}, reload asked={asked}, Expired rows={rig.Telemetry.Count(AdEventKind.Expired)}, ready after the fill={rig.Controller.IsReady("shop_refill")}");
            }

            // A destroyed view's handler, a failing analytics sink or a throwing SDK load stopped the answer: the reward callback never
            // ran although its receipt was written, RequestRewardAsync never completed and AdRewardedButton stayed hidden
            static (int results, AdResult? last, string threw) Answer(AdsRig rig)
            {
                int results = 0;
                AdResult? last = null;
                string threw = null;
                rig.Controller.Show("shop_refill", r => { results++; last = r; });
                try { rig.Provider.ShowCallback(AdResult.Rewarded); }
                catch (Exception ex) { threw = ex.GetType().Name + ": " + ex.Message; }
                return (results, last, threw);
            }

            using (var rig = new AdsRig().Running())
            {
                rig.Controller.AdClosed += (format, result) => throw new InvalidOperationException("dead view");
                var a = Answer(rig);
                var row = rig.Telemetry.Events.Find(e => e.Kind == AdEventKind.HandlerFailed);
                var lines = new List<string>();
                new AdFailureLog(lines.Add, violations: false).Record(row);
                Check("AC50. AThrowingAdClosedSubscriber_StillDeliversTheResultOnce_AndIsRecordedAndLogged",
                    a.results == 1 && a.last == AdResult.Rewarded && a.threw == null && rig.Telemetry.Count(AdEventKind.HandlerFailed) == 1
                    && row.Detail != null && row.Detail.Contains("dead view") && lines.Count == 1 && rig.Controller.PendingReward.HasValue && rig.Provider.Loads.Count == 3,
                    $"results={a.results} last={a.last} threw={a.threw ?? "-"} row=[{row}] log=[{string.Join(" | ", lines)}] loads={rig.Provider.Loads.Count} (the reload ran)");
            }
            using (var rig = new AdsRig().Running())
            {
                rig.Controller.ReadyChanged += () => throw new InvalidOperationException("destroyed button");
                var a = Answer(rig);
                Check("AC51. AThrowingReadyChangedSubscriber_StillDeliversTheResultOnce",
                    a.results == 1 && a.last == AdResult.Rewarded && a.threw == null && rig.Telemetry.Count(AdEventKind.HandlerFailed) == 1,
                    $"results={a.results} last={a.last} threw={a.threw ?? "-"} HandlerFailed={rig.Telemetry.Count(AdEventKind.HandlerFailed)}");
            }
            // The subscribers ran as one multicast: one that threw stopped those after it, so a destroyed view's handler kept the
            // rewarded button, subscribed later, hidden (#236)
            using (var rig = new AdsRig().Running())
            {
                int before = rig.ReadyChanges, later = 0;
                rig.Controller.ReadyChanged += () => throw new InvalidOperationException("destroyed view");
                rig.Controller.ReadyChanged += () => later++;
                var a = Answer(rig);
                int raised = rig.ReadyChanges - before;
                Check("AC58. AThrowingReadyChangedSubscriber_LeavesTheLaterOnesTold",
                    a.results == 1 && raised > 0 && later == raised && rig.Telemetry.Count(AdEventKind.HandlerFailed) == raised,
                    $"raised={raised} later subscriber heard={later} HandlerFailed={rig.Telemetry.Count(AdEventKind.HandlerFailed)} (pre-fix: heard 0)");
            }
            using (var rig = new AdsRig().Running())
            {
                rig.Telemetry.ThrowOn = AdEventKind.Rewarded;
                var a = Answer(rig);
                Check("AC52. AThrowingTelemetrySink_StillDeliversTheResultOnce",
                    a.results == 1 && a.last == AdResult.Rewarded && a.threw == null && rig.Telemetry.Count(AdEventKind.HandlerFailed) == 1 && rig.Provider.Loads.Count == 3,
                    $"results={a.results} last={a.last} threw={a.threw ?? "-"} HandlerFailed={rig.Telemetry.Count(AdEventKind.HandlerFailed)} loads={rig.Provider.Loads.Count}");
            }
            using (var rig = new AdsRig().Running())
            {
                rig.Provider.LoadThrows = new InvalidOperationException("JNI load failed");
                var a = Answer(rig);
                var failed = rig.Telemetry.Events.Find(e => e.Kind == AdEventKind.LoadFailed);
                int loads = rig.Provider.Loads.Count;
                rig.Scheduler.Advance(RetryPolicy.DelaySeconds(1));
                bool retried = rig.Provider.Loads.Count == loads + 1;
                rig.Provider.Fill(AdFormat.Rewarded);
                Check("AC53. AThrowingReload_StillDeliversTheResultOnce_AndRetriesAsAFailedLoad",
                    a.results == 1 && a.last == AdResult.Rewarded && a.threw == null && failed.Detail != null && failed.Detail.Contains("JNI load failed")
                    && retried && rig.Controller.IsReady("shop_refill"),
                    $"results={a.results} last={a.last} threw={a.threw ?? "-"} LoadFailed=[{failed}] retried={retried} ready after the fill={rig.Controller.IsReady("shop_refill")}");
            }

            // #195: an SDK start that threw left the controller Initializing for good: no retry, no start line, no Started event
            using (var rig = new AdsRig())
            {
                rig.Provider.InitThrows = new InvalidOperationException("JNI init failed");
                string threw = null;
                try { rig.Controller.Start(); }
                catch (Exception ex) { threw = ex.Message; }
                var failed = rig.Telemetry.Events.Find(e => e.Kind == AdEventKind.InitFailed);
                bool failedState = rig.Controller.State == AdController.Phase.InitFailed;
                rig.Scheduler.Advance(RetryPolicy.DelaySeconds(1));
                bool retried = rig.Provider.InitCallback != null;
                rig.Provider.InitCallback?.Invoke(true);
                Check("AC57. AThrowingSdkStart_IsAFailedStart_RetriedWithTheBackOff",
                    threw == null && failedState && failed.Detail != null && failed.Detail.Contains("JNI init failed") && retried && rig.Controller.State == AdController.Phase.Running,
                    $"threw={threw ?? "-"} state after the throw={(failedState ? "InitFailed" : "not InitFailed")} InitFailed=[{failed}] retried={retried} state={rig.Controller.State}");
            }
        }

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

            // issue #19: the bootstrap ran the simulator, overlay and free rewards included, for these in a store build
            var blocked = ProviderGate.Evaluate(BuildKind.Release, test, "x");
            var provider = ProviderGate.Evaluate(BuildKind.Release, live, "x");
            Check("GT10. Release_Blocked_RunsNothing", ProviderGate.Host(BuildKind.Release, blocked, true, true) == HostMode.Idle, "no simulated ad in a store build");
            Check("GT11. Release_ProviderMissing_RunsNothing", ProviderGate.Host(BuildKind.Release, provider, true, false) == HostMode.Idle
                && ProviderGate.Host(BuildKind.Release, provider, false, false) == HostMode.Idle, "package missing, assembly stripped, or no mediation");
            var devBlocked = ProviderGate.Evaluate(BuildKind.Development, live, "x");
            var devListed = ProviderGate.Evaluate(BuildKind.Development, test, "abc-123");
            Check("GT12. Development_BlockedOrProviderMissing_RunsNothing", ProviderGate.Host(BuildKind.Development, devBlocked, true, true) == HostMode.Idle
                && ProviderGate.Host(BuildKind.Development, devListed, true, false) == HostMode.Idle, "a tester never sees a simulated ad standing in for a real one");
            Check("GT13. Simulator_OnlyWhereTheGateChoseIt_OrNoMediation", ProviderGate.Host(BuildKind.Development, ProviderGate.Evaluate(BuildKind.Development, test, "other"), true, true) == HostMode.Simulator
                && ProviderGate.Host(BuildKind.Editor, ProviderGate.Evaluate(BuildKind.Editor, live, "x"), true, true) == HostMode.Simulator
                && ProviderGate.Host(BuildKind.Development, devListed, false, false) == HostMode.Simulator
                && ProviderGate.Host(BuildKind.Development, devListed, true, true) == HostMode.Provider, "unlisted device, editor, no mediation yet; a listed device gets the SDK");

            // issue #42: an all-zero id (ad personalization off, id deleted) or a missing one is unavailable,
            // never a test device even when listed, and the reason says so instead of "add it on the Test tab"
            const string zeroId = "00000000-0000-0000-0000-000000000000";
            var zeroListed = AdsRig.Provider_(testMode: true, devices: new[] { zeroId });
            var zero = ProviderGate.Evaluate(BuildKind.Development, zeroListed, zeroId);
            var missing = ProviderGate.Evaluate(BuildKind.Development, test, null);
            Check("GT14. UnavailableAdvertisingId_NeverATestDevice_AndSaysWhy",
                zero.Decision == GateDecision.Simulator && zero.Rule == PolicyRules.UnregisteredDevice && zero.Reason.Contains("unavailable")
                && missing.Decision == GateDecision.Simulator && missing.Reason.Contains("unavailable") && !missing.Reason.Contains("add it on the Test tab"),
                $"listed all-zero id: {zero.Decision} ({zero.Reason}); no id: {missing.Decision} ({missing.Reason}) (pre-fix: the listed zero id got the SDK)");

            // issue #133: the bootstrap read the advertising id before every start, also where the decision never uses it.
            // Where NeedsAdvertisingId is false, a listed id, an unlisted one and none at all give the same decision.
            int checkedCases = 0, wrong = 0;
            foreach (BuildKind build in new[] { BuildKind.Editor, BuildKind.Development, BuildKind.Release })
                foreach (var config in new[] { live, test, sample, AdsRig.Provider_(testMode: true, devices: new[] { "abc-123" }, published: true) })
                {
                    var listed = ProviderGate.Evaluate(build, config, "abc-123");
                    var unlisted = ProviderGate.Evaluate(build, config, "other");
                    var none = ProviderGate.Evaluate(build, config, null);
                    bool idMatters = listed.Decision != unlisted.Decision || listed.Decision != none.Decision || listed.Reason != unlisted.Reason || listed.Reason != none.Reason;
                    checkedCases++;
                    if (idMatters != ProviderGate.NeedsAdvertisingId(build, config)) wrong++;
                }
            Check("GT15. AdvertisingId_NeededExactlyWhereTheDecisionUsesIt", wrong == 0 && ProviderGate.NeedsAdvertisingId(BuildKind.Development, test),
                $"{checkedCases} build and configuration cases, {wrong} where NeedsAdvertisingId disagrees with Evaluate");
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

            // #132: a game's own screen keeps a simulated rewarded open for MinShowSeconds; the controller's answer window does
            // not end a show that opened in the game's window, so only the game's CompleteShow does
            {
                var simTime = new AdsScheduler(new AdsClock());
                var sim = new SimulatorProvider(simTime) { Interactive = true, MinShowSeconds = 15 };
                using var rig = new AdsRig(provider: sim, consent: sim);
                rig.Controller.Start();
                AdResult? shown = null;
                rig.Controller.Show("shop_refill", x => shown = x);
                rig.Scheduler.Advance(14.9); simTime.Advance(14.9);
                bool canCloseEarly = sim.CanClose, closedEarly = sim.CompleteShow(AdResult.Closed);
                bool held = !canCloseEarly && !closedEarly && sim.IsShowing && shown == null;
                rig.Scheduler.Advance(0.2); simTime.Advance(0.2);
                bool closed = sim.CanClose && sim.CompleteShow(AdResult.Closed) && shown == AdResult.Closed && rig.Telemetry.Count(AdEventKind.Rewarded) == 0;
                Check("SM11. MinShowSeconds_RefusesCloseUntilItElapses", held && closed,
                    $"at 14.9 s: can close={canCloseEarly}, close accepted={closedEarly}, still open={held}; at 15.1 s closed={closed}, result={shown}");
            }

            // #132: each show waits for its own minimum: one that failed early leaves no timer that frees the next one too soon
            {
                var simTime = new AdsScheduler(new AdsClock());
                var sim = new SimulatorProvider(simTime) { Interactive = true, MinShowSeconds = 15 };
                AdResult? first = null, second = null;
                sim.Load(AdFormat.Rewarded);
                sim.Show(AdFormat.Rewarded, "shop_refill", x => first = x);
                simTime.Advance(1);
                bool failedAtOnce = sim.CompleteShow(AdResult.Failed) && first == AdResult.Failed;
                simTime.Advance(4);
                sim.Load(AdFormat.Rewarded);
                sim.Show(AdFormat.Rewarded, "shop_refill", x => second = x);
                simTime.Advance(10.5); // 15.5 s: the failed show's minimum would have ended here
                bool heldByItsOwn = !sim.CanClose && !sim.CompleteShow(AdResult.Closed) && second == null;
                simTime.Advance(5); // 20.5 s: 15.5 s after the second show opened
                bool closed = sim.CompleteShow(AdResult.Closed) && second == AdResult.Closed;
                Check("SM12. MinShowSeconds_EachShowWaitsForItsOwn_AFailureEndsAtOnce", failedAtOnce && heldByItsOwn && closed,
                    $"failure at 1 s ended the show={failedAtOnce}, second show held at 10.5 s of its 15={heldByItsOwn}, closed at 15.5 s={closed}");
            }

            // #132: a reward ends a rewarded before the minimum; an interstitial, which never pays, is not closed early by one
            {
                var simTime = new AdsScheduler(new AdsClock());
                var sim = new SimulatorProvider(simTime) { Interactive = true, MinShowSeconds = 15 };
                AdResult? rewarded = null, interstitial = null;
                sim.Load(AdFormat.Rewarded);
                sim.Show(AdFormat.Rewarded, "shop_refill", x => rewarded = x);
                simTime.Advance(1);
                bool paid = sim.CompleteShow(AdResult.Rewarded) && rewarded == AdResult.Rewarded;
                sim.Load(AdFormat.Interstitial);
                sim.Show(AdFormat.Interstitial, "level_end", x => interstitial = x);
                simTime.Advance(1);
                bool held = !sim.CompleteShow(AdResult.Rewarded) && sim.IsShowing && interstitial == null;
                simTime.Advance(14.5);
                bool closed = sim.CompleteShow(AdResult.Rewarded) && interstitial == AdResult.Closed;
                Check("SM13. MinShowSeconds_RewardEndsARewarded_ButCannotCloseAnInterstitialEarly", paid && held && closed,
                    $"reward at 1 s={paid} ({rewarded}), interstitial held at 1 s={held}, closed at 15.5 s={closed} ({interstitial})");
            }

            // #132: a show that answers by itself (not interactive) ignores the minimum and arms no timer
            {
                var simTime = new AdsScheduler(new AdsClock());
                var sim = new SimulatorProvider(simTime, SimulatorScenario.ClosedEarly) { MinShowSeconds = 15 };
                AdResult? answered = null;
                sim.Load(AdFormat.Rewarded);
                sim.Show(AdFormat.Rewarded, "shop_refill", x => answered = x);
                Check("SM14. MinShowSeconds_NotInteractive_AnswersAtOnce", answered == AdResult.Closed && !sim.IsShowing && simTime.Pending == 0,
                    $"result={answered?.ToString() ?? "none"} (expected Closed at once), timers left={simTime.Pending}");
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

            // a package that compiles against uGUI brings it: a project made from the bare template has no com.unity.ugui (issue #21)
            string packages = Path.GetFullPath(Path.Combine(dir, "../../.."));
            var missingUgui = new List<string>();
            int scanned = 0;
            foreach (var package in Directory.Exists(packages) ? Directory.GetDirectories(packages, "com.nexus.*") : Array.Empty<string>())
            {
                scanned++;
                bool usesUi = false;
                foreach (var asm in Directory.GetFiles(package, "*.asmdef", SearchOption.AllDirectories))
                {
                    string rel = asm.Substring(package.Length).Replace('\\', '/');
                    if (rel.Contains("/Tests/") || rel.Contains("/Samples~/")) continue;
                    if (File.ReadAllText(asm).Contains("\"UnityEngine.UI\"")) usesUi = true;
                }
                string manifest = Path.Combine(package, "package.json");
                if (usesUi && !(File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"com.unity.ugui\""))) missingUgui.Add(Path.GetFileName(package));
            }
            Check("EF3. PackageThatReferencesUnityUi_DeclaresUgui", scanned >= 5 && missingUgui.Count == 0, $"{scanned} packages scanned" + (missingUgui.Count == 0 ? "" : ", missing com.unity.ugui: " + string.Join(",", missingUgui)));
        }
    }
}
