#if NEXUS_ADS_FIREBASE
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Ads.Unity;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;

[assembly: InternalsVisibleTo("com.nexus.ads.bridge.nexus.editor.tests")]

namespace Nexus.Ads.Bridge
{
    /// <summary>
    /// The analytics consent <c>com.nexus.firebase</c> reads, over the game-drawn consent screen (issue #430): bind it with
    /// <c>FirebaseBindings.Bind&lt;GameConsentAnalyticsConsent&gt;(builder)</c> after <c>GameConsent.Register</c>. It reads the
    /// same stored answer as the ad flow, so Accept grants analytics and Reject denies them, and a question asked again
    /// (a new version) holds analytics until it is answered. A child-directed or under-age build, which shows no screen and
    /// applies a refusal, reports that refusal (Obtained and Denied), so nothing is sent and nothing waits. Without <c>GameConsent.Register</c> the state stays Unknown and
    /// nothing is sent. Compiled only with <c>com.nexus.firebase</c> installed.
    /// </summary>
    public sealed class GameConsentAnalyticsConsent : NexusService<IAnalyticsConsent>, IAnalyticsConsent
    {
        private readonly Func<ConsentAnswerStore> _answers;
        private readonly Func<bool> _refusedWithoutScreen;
        private bool _subscribed;

        public GameConsentAnalyticsConsent() : this(() => GameConsent.Answers, () => GameConsent.RefusedWithoutScreen) { }

        /// <summary>Tests: the store to read and whether the build refuses without a screen.</summary>
        internal GameConsentAnalyticsConsent(Func<ConsentAnswerStore> answers, Func<bool> refusedWithoutScreen = null)
        {
            _answers = answers;
            _refusedWithoutScreen = refusedWithoutScreen ?? (() => false);
        }

        public ConsentState State => Read(out _);

        public AnalyticsChoice Choice
        {
            get
            {
                Read(out bool accepted);
                return State == ConsentState.Obtained ? (accepted ? AnalyticsChoice.Granted : AnalyticsChoice.Denied) : AnalyticsChoice.Unknown;
            }
        }

        public event Action Changed;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            GameConsent.AnswerChanged += OnAnswer;
            _subscribed = true;
            NexusLog.WarnAlways("[Nexus Ads] analytics consent: " + State + ", analytics " + AnalyticsCollectionPolicy.Decide(State, Choice));
            return default;
        }

        public override void OnDispose()
        {
            if (_subscribed) GameConsent.AnswerChanged -= OnAnswer;
            _subscribed = false;
            base.OnDispose();
        }

        private void OnAnswer()
        {
            // the decision the analytics gate takes, as UmpAnalyticsConsent writes it
            NexusLog.WarnAlways("[Nexus Ads] analytics consent: " + State + ", analytics " + AnalyticsCollectionPolicy.Decide(State, Choice));
            Changed?.Invoke();
        }

        // The question is asked and unanswered: Required (analytics wait). No registered flow: Unknown (analytics wait too).
        private ConsentState Read(out bool accepted)
        {
            accepted = false;
            var store = _answers();
            if (store == null) return ConsentState.Unknown;
            if (_refusedWithoutScreen()) return ConsentState.Obtained; // the refusal the ad flow applied; accepted stays false
            return store.TryRead(out accepted) ? ConsentState.Obtained : ConsentState.Required;
        }
    }
}
#endif
