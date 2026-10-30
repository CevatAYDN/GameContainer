using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GoogleMobileAds.Ump.Api;
using Nexus.Ads.Unity;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;

[assembly: InternalsVisibleTo("Nexus.Ads.AdMob.Firebase.Tests")]

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// The analytics consent <c>com.nexus.firebase</c> reads, over Google's consent form (issue #87): bind it with
    /// <c>FirebaseBindings.Bind&lt;UmpAnalyticsConsent&gt;(builder)</c>. <see cref="State"/> is the User Messaging Platform's
    /// consent status. <see cref="Choice"/> is always <see cref="AnalyticsChoice.Denied"/>: Google maps no TCF purpose to
    /// <c>analytics_storage</c>, so the form answers nothing about analytics, and where consent is required nothing is sent
    /// (the analytics decision and, since #78, Crashlytics). Where it is not required, collection runs. A game that asks its
    /// own analytics question binds its own reader instead. Compiled only with <c>com.nexus.firebase</c> installed.
    /// </summary>
    public sealed class UmpAnalyticsConsent : NexusService<IAnalyticsConsent>, IAnalyticsConsent
    {
        private readonly Func<ConsentStatus> _status;
        private readonly Func<ConsentRequestParameters> _request;
        private readonly Action<ConsentRequestParameters, Action<FormError>> _update;
        private UnityMainThread _main;
        private bool _read, _disposed;

        /// <summary>The ad family's bridge raises it after the consent answer, when ads start or are refused.</summary>
        [Inject] public IAdService Ads { get; set; }

        public UmpAnalyticsConsent() : this(() => ConsentInformation.ConsentStatus, LaunchRequest, ConsentInformation.Update) { }

        /// <summary>Tests: without <paramref name="update"/> no status update is requested.</summary>
        internal UmpAnalyticsConsent(Func<ConsentStatus> status, Func<ConsentRequestParameters> request = null,
            Action<ConsentRequestParameters, Action<FormError>> update = null)
        {
            _status = status;
            _request = request;
            _update = update;
        }

        public ConsentState State { get; private set; }

        public AnalyticsChoice Choice => AnalyticsChoice.Denied;

        public event Action Changed;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            _main = new UnityMainThread();
            Refresh();
            // shortcut: the form's own answer is read on IAdService.ReadyChanged, not on the consent flow's event, which the
            // bootstrap keeps; subscribe to the flow once the bootstrap exposes it.
            Ads.ReadyChanged += Refresh;
            RequestStatusUpdate();
            return default;
        }

        public override void OnDispose()
        {
            _disposed = true;
            if (Ads != null) Ads.ReadyChanged -= Refresh;
            base.OnDispose();
        }

        // UMP has no status in a first session (or after the app's data is cleared) until an update answers. Without this
        // request the status stays Unknown until the ads start, and analytics and crash reports wait for them (issue #121).
        private void RequestStatusUpdate()
        {
            ConsentRequestParameters request = _update == null ? null : _request?.Invoke();
            if (request == null) return;
            _update(request, error => OnMain(() =>
            {
                if (_disposed) return;
                if (error != null) NexusLog.WarnAlways("[Nexus Ads] analytics consent: the status update failed: " + error.Message);
                Refresh();
            }));
        }

        /// <summary>
        /// The request the bootstrap's consent flow will send (<see cref="UmpConsentFlow.RequestParameters"/>), from the
        /// bootstrap's settings or the settings asset the bootstrap loads by default; null without settings.
        /// </summary>
        private static ConsentRequestParameters LaunchRequest()
        {
            AdsSettings settings = AdsBootstrap.Instance != null ? AdsBootstrap.Instance.Settings : AdsSettings.Load();
            if (settings == null)
            {
                NexusLog.WarnAlways("[Nexus Ads] analytics consent: no AdsSettings, so no consent status update at launch; the status is read again when the ads start");
                return null;
            }
            return UmpConsentFlow.RequestParameters(settings.ToProviderConfig(AdsBootstrap.CurrentBuild, AdsBootstrap.CurrentPlatform, settings.SelectedMediation));
        }

        private void Refresh()
        {
            ConsentState state = StateFor(_status());
            if (_read && state == State) return;
            _read = true;
            State = state;
            NexusLog.WarnAlways("[Nexus Ads] analytics consent: " + state + ", analytics " + Choice);
            Changed?.Invoke();
        }

        // The UMP plugin calls back on Android's UI thread while MobileAds.RaiseAdEventsOnUnityMainThread is off, its default.
        private void OnMain(Action action)
        {
            if (_main == null || _main.IsCurrent) action();
            else _main.Post(action);
        }

        internal static ConsentState StateFor(ConsentStatus status) => status switch
        {
            ConsentStatus.NotRequired => ConsentState.NotRequired,
            ConsentStatus.Required => ConsentState.Required,
            ConsentStatus.Obtained => ConsentState.Obtained,
            _ => ConsentState.Unknown,
        };
    }
}
