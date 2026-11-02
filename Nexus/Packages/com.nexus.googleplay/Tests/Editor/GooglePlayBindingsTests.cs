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

namespace Nexus.GooglePlay.Tests
{
    /// <summary>#272: which review service each build gets; a player never binds the mock (the rule of #171).</summary>
    public sealed class GooglePlayBindingsTests
    {
        [Test]
        public void TheEditorGetsTheMock()
        {
            using var test = NexusTestHarness.CreateContext(GooglePlayBindings.Bind, autoInitialize: true);
            Assert.IsInstanceOf<MockReviewService>(test.Context.Resolve<IReviewService>());
        }

        [Test]
        public void APlayerWithoutThePlugin_BindsTheUnavailableService_WithAnErrorNamingThePackage()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[GooglePlay\] com\.google\.play\.review is not installed as a package, so IReviewService shows no rating sheet in this player"));
            using var test = NexusTestHarness.CreateContext(builder => GooglePlayBindings.Bind(builder, player: true, withPlugins: false), autoInitialize: true);

            var review = test.Context.Resolve<IReviewService>();
            Assert.IsInstanceOf<UnavailableReviewService>(review);
            LogAssert.Expect(LogType.Warning, new Regex(@"^\[ReviewService\] No store review provider is bound"));
            Assert.AreEqual(ReviewOutcome.Unavailable, review.RequestReviewAsync(default).GetAwaiter().GetResult());
        }

#if NEXUS_PLAY_REVIEW
        [Test]
        public void APlayerWithThePlugin_BindsGooglePlaysReview()
        {
            using var test = NexusTestHarness.CreateContext(builder => GooglePlayBindings.Bind(builder, player: true, withPlugins: true), autoInitialize: true);
            Assert.IsInstanceOf<PlayReviewService>(test.Context.Resolve<IReviewService>());
        }
#endif
    }
}
