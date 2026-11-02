using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.GooglePlay.Mocks;
using UnityEngine;
using UnityEngine.TestTools;
#if NEXUS_PLAY_REVIEW
using Nexus.GooglePlay.Review;
#endif
#if NEXUS_PLAY_APPUPDATE
using Nexus.GooglePlay.AppUpdate;
#endif

namespace Nexus.GooglePlay.Tests
{
    /// <summary>#272, #273: which review and update services each build gets; a player never binds a mock (the rule of #171).</summary>
    public sealed class GooglePlayBindingsTests
    {
        [Test]
        public void TheEditorGetsTheMock()
        {
            using var test = NexusTestHarness.CreateContext(GooglePlayBindings.Bind, autoInitialize: true);
            Assert.IsInstanceOf<MockReviewService>(test.Context.Resolve<IReviewService>());
            Assert.IsInstanceOf<MockAppUpdateService>(test.Context.Resolve<IAppUpdateService>());
        }

        [Test]
        public void APlayerWithoutThePlugins_BindsTheUnavailableServices_WithAnErrorNamingEachPackage()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[GooglePlay\] com\.google\.play\.review is not installed as a package, so IReviewService shows no rating sheet in this player"));
            LogAssert.Expect(LogType.Error, new Regex(@"^\[GooglePlay\] com\.google\.play\.appupdate is not installed as a package, so IAppUpdateService finds no update in this player"));
            using var test = NexusTestHarness.CreateContext(builder => GooglePlayBindings.Bind(builder, player: true, withPlugins: false), autoInitialize: true);

            var review = test.Context.Resolve<IReviewService>();
            Assert.IsInstanceOf<UnavailableReviewService>(review);
            LogAssert.Expect(LogType.Warning, new Regex(@"^\[ReviewService\] No store review provider is bound"));
            Assert.AreEqual(ReviewOutcome.Unavailable, review.RequestReviewAsync(default).GetAwaiter().GetResult());

            var update = test.Context.Resolve<IAppUpdateService>();
            Assert.IsInstanceOf<UnavailableAppUpdateService>(update);
            LogAssert.Expect(LogType.Warning, new Regex(@"^\[AppUpdateService\] No store update provider is bound"));
            Assert.AreEqual(AppUpdateAvailability.StoreUnavailable, update.CheckAsync(default).GetAwaiter().GetResult().Availability);
        }

#if NEXUS_PLAY_REVIEW
        [Test]
        public void APlayerWithThePlugin_BindsGooglePlaysReview()
        {
            using var test = NexusTestHarness.CreateContext(builder => GooglePlayBindings.Bind(builder, player: true, withPlugins: true), autoInitialize: true);
            Assert.IsInstanceOf<PlayReviewService>(test.Context.Resolve<IReviewService>());
        }
#endif
#if NEXUS_PLAY_APPUPDATE

        // Google's AppUpdateManager reaches Java when it is made; binding and resolving the service must not make it.
        [Test]
        public void APlayerWithThePlugin_BindsGooglePlaysUpdate_WithoutReachingJava()
        {
            using var test = NexusTestHarness.CreateContext(builder => GooglePlayBindings.Bind(builder, player: true, withPlugins: true), autoInitialize: true);
            Assert.IsInstanceOf<PlayAppUpdateService>(test.Context.Resolve<IAppUpdateService>());
        }
#endif
    }
}
