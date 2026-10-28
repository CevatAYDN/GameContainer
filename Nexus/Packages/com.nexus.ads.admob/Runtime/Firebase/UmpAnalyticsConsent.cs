using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GoogleMobileAds.Ump.Api;
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
        private bool _read;

        /// <summary>The ad family's bridge raises it after the consent answer, when ads start or are refused.</summary>
        [Inject] public IAdService Ads { get; set; }

        public UmpAnalyticsConsent() : this(() => ConsentInformation.ConsentStatus) { }

        internal UmpAnalyticsConsent(Func<ConsentStatus> status) => _status = status;

        public ConsentState State { get; private set; }

        public AnalyticsChoice Choice => AnalyticsChoice.Denied;

        public event Action Changed;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            Refresh();
            // shortcut: re-reads on IAdService.ReadyChanged, not on the consent flow's own event, which the bootstrap keeps;
            // an answer followed by no ad event is read at the next one. Subscribe to the flow once the bootstrap exposes it.
            Ads.ReadyChanged += Refresh;
            return default;
        }

        public override void OnDispose()
        {
            if (Ads != null) Ads.ReadyChanged -= Refresh;
            base.OnDispose();
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

        internal static ConsentState StateFor(ConsentStatus status) => status switch
        {
            ConsentStatus.NotRequired => ConsentState.NotRequired,
            ConsentStatus.Required => ConsentState.Required,
            ConsentStatus.Obtained => ConsentState.Obtained,
            _ => ConsentState.Unknown,
        };
    }
}
