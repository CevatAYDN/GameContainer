using System;
using System.Runtime.CompilerServices;
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

[assembly: InternalsVisibleTo("com.nexus.firebase.tests")]

namespace Nexus.Firebase
{
    /// <summary>
    /// Binds <see cref="IAnalyticsService"/>, <see cref="IRemoteConfigService"/> and <see cref="ICrashReporter"/> in one call
    /// (issue #78). A player gets the Firebase service of each SDK package the project has, and for one it lacks a service that
    /// does nothing, with an error at start naming the package: a player never binds a mock (issue #171). The Editor (every batch
    /// tool, with no google-services.json needed) gets the package's mocks. The choice is made here because the SDK defines
    /// (<c>NEXUS_FIREBASE_*</c>) exist only inside this assembly.
    /// </summary>
    public static class FirebaseBindings
    {
        /// <summary>
        /// <typeparamref name="TConsent"/>, the game's consent reader, is bound in a player, after the Firebase services, so they
        /// start Firebase before it asks the User Messaging Platform. In the Editor a <see cref="MockAnalyticsConsent"/> answering
        /// <see cref="ConsentState.NotRequired"/> is bound instead, so the mocks send at once.
        /// </summary>
        public static void Bind<TConsent>(IContextBuilder builder) where TConsent : class, IAnalyticsConsent, INexusService
            => Bind<TConsent>(builder, player: !Application.isEditor, withSdks: true);

        /// <summary>
        /// As <see cref="Bind{TConsent}(IContextBuilder)"/>, and every analytics call also goes to <paramref name="alsoSendTo"/>, in that
        /// order, after the build's analytics service (Firebase in a player, the mock in the Editor), through an <see cref="AnalyticsFanOut"/>
        /// bound as <see cref="IAnalyticsService"/> (#159). The build's service stays bound under its own type
        /// (<c>FirebaseAnalyticsService</c>, <see cref="MockAnalyticsService"/> or <c>UnavailableAnalyticsService</c>). The fan-out only
        /// forwards: bind each extra as a service under its own type (<c>builder.BindServiceInstance&lt;LocalFileSink&gt;(sink)</c>), so it
        /// gets its <c>[Inject]</c> members, start and dispose, and pass the same instance here. An extra receives every call as the game
        /// makes it, before consent and with the names Firebase refuses: limit it to the builds that may have it (a development build).
        /// With no extra this binds what the one-argument overload binds.
        /// </summary>
        public static void Bind<TConsent>(IContextBuilder builder, params IAnalyticsService[] alsoSendTo) where TConsent : class, IAnalyticsConsent, INexusService
            => Bind<TConsent>(builder, player: !Application.isEditor, withSdks: true, alsoSendTo);

        // withSdks false takes the path of a player that lacks every SDK package, in a project that has them too (tests).
        internal static void Bind<TConsent>(IContextBuilder builder, bool player, bool withSdks, IAnalyticsService[] alsoSendTo = null)
            where TConsent : class, IAnalyticsConsent, INexusService
        {
            BindAnalytics(builder, player, withSdks, alsoSendTo);
            BindCrashReporter(builder, player, withSdks);
            BindRemoteConfig(builder, player, withSdks);

            if (player)
            {
                builder.BindService<IAnalyticsConsent, TConsent>();
                return;
            }

            var consent = new MockAnalyticsConsent();
            consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            builder.BindInstance<IAnalyticsConsent>(consent);
        }

        private static void BindAnalytics(IContextBuilder builder, bool player, bool withSdks, IAnalyticsService[] alsoSendTo)
        {
#if NEXUS_FIREBASE_ANALYTICS
            if (player && withSdks)
            {
                BindAnalytics<FirebaseAnalyticsService>(builder, alsoSendTo);
                return;
            }
#endif
            if (player)
            {
                Missing("com.google.firebase.analytics", "IAnalyticsService sends no event");
                BindAnalytics<UnavailableAnalyticsService>(builder, alsoSendTo);
                return;
            }
            BindAnalytics<MockAnalyticsService>(builder, alsoSendTo);
        }

        // No extra: the service as IAnalyticsService, as before #159. Extras: the service under its own type, and the fan-out as
        // IAnalyticsService, bound after it so the service starts first.
        private static void BindAnalytics<TService>(IContextBuilder builder, IAnalyticsService[] alsoSendTo)
            where TService : class, IAnalyticsService, INexusService, new()
        {
            if (alsoSendTo == null || alsoSendTo.Length == 0)
            {
                builder.BindService<IAnalyticsService, TService>();
                return;
            }
            var service = new TService();
            var targets = new IAnalyticsService[alsoSendTo.Length + 1];
            targets[0] = service;
            Array.Copy(alsoSendTo, 0, targets, 1, alsoSendTo.Length);
            var fanOut = new AnalyticsFanOut(targets);
            builder.BindServiceInstance(service);
            builder.BindServiceInstance<IAnalyticsService>(fanOut);
        }

        private static void BindCrashReporter(IContextBuilder builder, bool player, bool withSdks)
        {
#if NEXUS_FIREBASE_CRASHLYTICS
            if (player && withSdks)
            {
                builder.BindService<ICrashReporter, FirebaseCrashReporter>();
                return;
            }
#endif
            if (player)
            {
                Missing("com.google.firebase.crashlytics", "ICrashReporter reports nothing");
                builder.BindService<ICrashReporter, UnavailableCrashReporter>();
                return;
            }
            builder.BindService<ICrashReporter, MockCrashReporter>();
        }

        private static void BindRemoteConfig(IContextBuilder builder, bool player, bool withSdks)
        {
#if NEXUS_FIREBASE_REMOTE_CONFIG
            if (player && withSdks)
            {
                builder.BindService<IRemoteConfigService, FirebaseRemoteConfigService>();
                return;
            }
#endif
            if (player)
            {
                Missing("com.google.firebase.remote-config", "IRemoteConfigService returns the game's fallbacks");
                builder.BindService<IRemoteConfigService, UnavailableRemoteConfigService>();
                return;
            }
            builder.BindService<IRemoteConfigService, MockRemoteConfigService>();
        }

        private static void Missing(string package, string consequence) =>
            NexusRuntime.Logger?.LogError("[Firebase] " + package + " is not installed as a package, so " + consequence + " in this player. Install it as a UPM package (com.nexus.firebase README, Install the SDK).");
    }
}
