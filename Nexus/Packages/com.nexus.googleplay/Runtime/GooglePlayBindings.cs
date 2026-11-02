using System.Runtime.CompilerServices;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.GooglePlay.Mocks;
using UnityEngine;
#if NEXUS_PLAY_REVIEW
using Nexus.GooglePlay.Review;
#endif

[assembly: InternalsVisibleTo("com.nexus.googleplay.tests")]

namespace Nexus.GooglePlay
{
    /// <summary>
    /// Binds the Google Play services in one call (#272). An Android player gets the Play service of each Google plugin package
    /// the project has, and for one it lacks the core's unavailable service, with an error at start that names the package; the
    /// editor gets this package's mocks. The choice is made here because the plugin defines (<c>NEXUS_PLAY_*</c>) exist only
    /// inside this assembly. Like Google's plugins, the assembly exists in Android players and the editor only, so a game that
    /// also builds for another platform calls it under <c>#if UNITY_ANDROID || UNITY_EDITOR</c>.
    /// </summary>
    public static class GooglePlayBindings
    {
        public static void Bind(IContextBuilder builder) => Bind(builder, player: !Application.isEditor, withPlugins: true);

        // withPlugins false takes the path of a player that lacks every plugin package, in a project that has them (tests).
        internal static void Bind(IContextBuilder builder, bool player, bool withPlugins)
        {
            BindReview(builder, player, withPlugins);
        }

        private static void BindReview(IContextBuilder builder, bool player, bool withPlugins)
        {
            if (!player)
            {
                builder.BindService<IReviewService, MockReviewService>();
                return;
            }
#if NEXUS_PLAY_REVIEW
            if (withPlugins)
            {
                builder.BindService<IReviewService, PlayReviewService>();
                return;
            }
#endif
            Missing("com.google.play.review", "IReviewService shows no rating sheet");
            builder.BindService<IReviewService, UnavailableReviewService>();
        }

        private static void Missing(string package, string consequence) =>
            NexusRuntime.Logger?.LogError("[GooglePlay] " + package + " is not installed as a package, so " + consequence
                + " in this player. Install it from developers.google.com/unity/archive (com.nexus.googleplay README, Install the plugins).");
    }
}
