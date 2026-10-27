using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;
using Nexus.Firebase.Crash;
using Nexus.Firebase.Mocks;
using Nexus.Firebase.RemoteConfig;
using UnityEngine;
#if NEXUS_FIREBASE_ANALYTICS || NEXUS_FIREBASE_REMOTE_CONFIG || NEXUS_FIREBASE_CRASHLYTICS
using Nexus.Firebase.Sdk;
#endif

namespace Nexus.Firebase
{
    /// <summary>
    /// Binds <see cref="IAnalyticsService"/>, <see cref="IRemoteConfigService"/> and <see cref="ICrashReporter"/> in one call
    /// (issue #78). A player gets the Firebase service of each SDK package the project has; the Editor (every batch tool, with no
    /// google-services.json needed) and a missing SDK get the package's mock. The choice is made here because the SDK defines
    /// (<c>NEXUS_FIREBASE_*</c>) exist only inside this assembly.
    /// </summary>
    public static class FirebaseBindings
    {
        /// <summary>
        /// <typeparamref name="TConsent"/>, the game's consent reader, is bound only when a Firebase service reads it, and after the
        /// Firebase services, so they start Firebase before it asks the User Messaging Platform. Otherwise a
        /// <see cref="MockAnalyticsConsent"/> answering <see cref="ConsentState.NotRequired"/> is bound, so the mocks send at once.
        /// </summary>
        public static void Bind<TConsent>(IContextBuilder builder) where TConsent : class, IAnalyticsConsent, INexusService
        {
            bool player = !Application.isEditor;
            bool readsConsent = BindAnalytics(builder, player);
            readsConsent |= BindCrashReporter(builder, player);
            BindRemoteConfig(builder, player);

            if (readsConsent)
            {
                builder.BindService<IAnalyticsConsent, TConsent>();
                return;
            }

            var consent = new MockAnalyticsConsent();
            consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            builder.BindInstance<IAnalyticsConsent>(consent);
        }

        private static bool BindAnalytics(IContextBuilder builder, bool player)
        {
#if NEXUS_FIREBASE_ANALYTICS
            if (player)
            {
                builder.BindService<IAnalyticsService, FirebaseAnalyticsService>();
                return true;
            }
#endif
            builder.BindService<IAnalyticsService, MockAnalyticsService>();
            return false;
        }

        private static bool BindCrashReporter(IContextBuilder builder, bool player)
        {
#if NEXUS_FIREBASE_CRASHLYTICS
            if (player)
            {
                builder.BindService<ICrashReporter, FirebaseCrashReporter>();
                return true;
            }
#endif
            builder.BindService<ICrashReporter, MockCrashReporter>();
            return false;
        }

        private static void BindRemoteConfig(IContextBuilder builder, bool player)
        {
#if NEXUS_FIREBASE_REMOTE_CONFIG
            if (player)
            {
                builder.BindService<IRemoteConfigService, FirebaseRemoteConfigService>();
                return;
            }
#endif
            builder.BindService<IRemoteConfigService, MockRemoteConfigService>();
        }
    }
}
