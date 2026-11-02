#if NEXUS_PLAY_APPUPDATE
using Google.Play.AppUpdate;
using NUnit.Framework;
using Nexus.Core.Services;
using Nexus.GooglePlay.AppUpdate;

namespace Nexus.GooglePlay.Tests
{
    /// <summary>#273: how Google's answers reach a game. Google's manager reaches Java, so the editor tests the mapping only.</summary>
    public sealed class PlayAppUpdateServiceTests
    {
        // The issue's acceptance: a sideloaded build answers that no store update exists, not that the check failed.
        [Test]
        public void ASideloadedBuild_CheckIsStoreUnavailable_AndAStartIsUnavailable()
        {
            Assert.AreEqual(AppUpdateAvailability.StoreUnavailable, PlayAppUpdateService.AvailabilityAfter(AppUpdateErrorCode.ErrorAppNotOwned));
            Assert.AreEqual(AppUpdateAvailability.StoreUnavailable, PlayAppUpdateService.AvailabilityAfter(AppUpdateErrorCode.ErrorPlayStoreNotFound));
            Assert.AreEqual(AppUpdateAvailability.Unknown, PlayAppUpdateService.AvailabilityAfter(AppUpdateErrorCode.ErrorInternalError));
            Assert.AreEqual(AppUpdateOutcome.Unavailable, PlayAppUpdateService.ToOutcome(AppUpdateErrorCode.ErrorAppNotOwned));
        }

        [Test]
        public void ADeclinedUpdate_IsCancelled_AndAStoreErrorIsFailed()
        {
            Assert.AreEqual(AppUpdateOutcome.Cancelled, PlayAppUpdateService.ToOutcome(AppUpdateErrorCode.ErrorUserCanceled));
            Assert.AreEqual(AppUpdateOutcome.Failed, PlayAppUpdateService.ToOutcome(AppUpdateErrorCode.ErrorUpdateFailed));
            Assert.AreEqual(AppUpdateOutcome.Downloaded, PlayAppUpdateService.ToOutcome(AppUpdateErrorCode.NoError));
        }

        // An immediate update the game started before a restart must read as resumable, not as unknown.
        [Test]
        public void AnImmediateUpdateStartedEarlier_IsInProgress()
        {
            Assert.AreEqual(AppUpdateAvailability.UpdateInProgress, PlayAppUpdateService.ToAvailability(UpdateAvailability.DeveloperTriggeredUpdateInProgress));
            Assert.AreEqual(AppUpdateAvailability.NoUpdate, PlayAppUpdateService.ToAvailability(UpdateAvailability.UpdateNotAvailable));
        }
    }
}
#endif
