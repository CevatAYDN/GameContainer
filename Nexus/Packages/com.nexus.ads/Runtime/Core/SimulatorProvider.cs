using System;
using System.Collections.Generic;

namespace Nexus.Ads
{
    /// <summary>What the simulator pretends the network does.</summary>
    public enum SimulatorScenario
    {
        /// <summary>Every load fills at once; a show pays (rewarded) or closes (interstitial).</summary>
        Normal,
        /// <summary>Every load answers "no fill".</summary>
        NoFill,
        /// <summary>Every load answers with an SDK error.</summary>
        LoadFails,
        /// <summary>Loads fill; every show fails.</summary>
        ShowFails,
        /// <summary>Loads fill; the player closes before the reward.</summary>
        ClosedEarly,
        /// <summary>Loads fill after <see cref="SimulatorProvider.SlowLoadSeconds"/>.</summary>
        SlowLoad,
        /// <summary>The consent flow answers "cannot request ads".</summary>
        ConsentRefused,
    }

    /// <summary>
    /// The provider that runs in the editor and on every device that may not talk to a real SDK. It is
    /// also a consent flow, so the consent scenario can be driven without a real form. With
    /// <see cref="Interactive"/> set, a show waits for <see cref="CompleteShow"/> (the Unity overlay's
    /// buttons); otherwise it answers at once. It never takes the <see cref="MediationLock"/> name of a
    /// real provider.
    /// </summary>
    public sealed class SimulatorProvider : IAdProvider, IConsentFlow
    {
        public const string ProviderName = "simulator";
        public const double SlowLoadSeconds = 5;

        private readonly IAdScheduler _scheduler;
        private readonly HashSet<AdFormat> _ready = new HashSet<AdFormat>();
        private readonly List<IDisposable> _pending = new List<IDisposable>();
        private Action<AdResult> _showResult;
        private AdFormat _showing;
        private string _showingPlacement;
        private ProviderConfig _config;

        public SimulatorScenario Scenario { get; set; }
        /// <summary>A show stays open until <see cref="CompleteShow"/>; the Unity overlay sets this.</summary>
        public bool Interactive { get; set; }
        public bool BannerVisible { get; private set; }
        public int LoadCount { get; private set; }
        public int ShowCount { get; private set; }
        public int TestSuiteOpened { get; private set; }
        public ProviderConfig ReceivedConfig => _config;

        public SimulatorProvider(IAdScheduler scheduler, SimulatorScenario scenario = SimulatorScenario.Normal)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Scenario = scenario;
        }

        // ----- IAdProvider

        public string Name => ProviderName;

        public event Action<AdFormat> Loaded;
        public event Action<AdFormat, string> LoadFailed;
        public event Action<AdFormat> Opened;
        public event Action<AdRevenue> Revenue;

        public void Initialize(ProviderConfig config, Action<bool> onReady)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            onReady?.Invoke(true);
        }

        public void Load(AdFormat format)
        {
            LoadCount++;
            switch (Scenario)
            {
                case SimulatorScenario.NoFill:
                    LoadFailed?.Invoke(format, "no fill");
                    return;
                case SimulatorScenario.LoadFails:
                    LoadFailed?.Invoke(format, "simulated SDK error");
                    return;
                case SimulatorScenario.SlowLoad:
                    _pending.Add(_scheduler.Schedule(SlowLoadSeconds, () => Fill(format)));
                    return;
                default:
                    Fill(format);
                    return;
            }
        }

        public bool IsReady(AdFormat format) => _ready.Contains(format);

        public void Show(AdFormat format, string placement, Action<AdResult> onResult)
        {
            if (onResult == null) throw new ArgumentNullException(nameof(onResult));
            if (!_ready.Remove(format)) { onResult(AdResult.NotReady); return; }
            ShowCount++;
            if (Scenario == SimulatorScenario.ShowFails) { onResult(AdResult.Failed); return; }
            _showing = format;
            _showingPlacement = placement;
            _showResult = onResult;
            Opened?.Invoke(format);
            if (Interactive) return;
            CompleteShow(Scenario == SimulatorScenario.ClosedEarly ? AdResult.Closed
                : format == AdFormat.Rewarded ? AdResult.Rewarded : AdResult.Closed);
        }

        /// <summary>Ends the open show with <paramref name="result"/>; a second call does nothing.</summary>
        public void CompleteShow(AdResult result)
        {
            var callback = _showResult;
            if (callback == null) return;
            _showResult = null;
            if (result == AdResult.Rewarded && _showing != AdFormat.Rewarded) result = AdResult.Closed; // an interstitial never pays
            if (result == AdResult.Rewarded || result == AdResult.Closed)
                Revenue?.Invoke(new AdRevenue(ProviderName, _showingPlacement, _showing, 0, "USD", "simulated"));
            callback(result);
        }

        public bool IsShowing => _showResult != null;

        public void ShowBanner(string placement, BannerPosition position) => BannerVisible = true;

        public void HideBanner() => BannerVisible = false;

        public void OpenTestSuite() => TestSuiteOpened++;

        private void Fill(AdFormat format)
        {
            _ready.Add(format);
            Loaded?.Invoke(format);
        }

        // ----- IConsentFlow (only the refused scenario needs a flow; everything else answers "no consent needed")

        public bool Completed { get; private set; }
        public bool CanRequestAds => Completed && Scenario != SimulatorScenario.ConsentRefused;
        public bool PrivacyOptionsRequired => Scenario == SimulatorScenario.ConsentRefused;
        public int PrivacyOptionsShown { get; private set; }
        public event Action Changed;

        public void Request(Action onCompleted)
        {
            Completed = true;
            Changed?.Invoke();
            onCompleted?.Invoke();
        }

        public void ShowPrivacyOptions(Action onClosed)
        {
            PrivacyOptionsShown++;
            onClosed?.Invoke();
        }

        /// <summary>Drops every pending slow load; the Unity layer calls it when the simulator is torn down.</summary>
        public void Cancel()
        {
            foreach (var handle in _pending) handle?.Dispose();
            _pending.Clear();
        }
    }
}
