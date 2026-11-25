using System;
using NUnit.Framework;
using Nexus.Core.Services;

namespace Nexus.Notifications.Unity.Tests
{
    /// <summary>#459: the iOS provider's decisions, which run in the editor though the service itself compiles only for iOS.</summary>
    public sealed class IosNotificationRulesTests
    {
        // iOS shows its dialog once; after a refusal only the Settings app helps, which is Blocked, never NeedsPermission.
        [Test]
        public void AuthorizationStatuses_MapToNeedsPermissionUntilAsked_AllowedWhenGranted_AndBlockedWhenDenied()
        {
            Assert.AreEqual(NotificationPermission.NeedsPermission, IosNotificationRules.ToPermission(IosAuthorization.NotDetermined));
            Assert.AreEqual(NotificationPermission.Blocked, IosNotificationRules.ToPermission(IosAuthorization.Denied));
            Assert.AreEqual(NotificationPermission.Allowed, IosNotificationRules.ToPermission(IosAuthorization.Authorized));
            Assert.AreEqual(NotificationPermission.Allowed, IosNotificationRules.ToPermission(IosAuthorization.Provisional));
            Assert.AreEqual(NotificationPermission.Allowed, IosNotificationRules.ToPermission(IosAuthorization.Ephemeral));
        }

        [Test]
        public void AFireTimeInThePast_OrWithinASecond_SchedulesAfterOneSecond()
        {
            var now = new DateTime(2026, 11, 26, 12, 0, 0);
            Assert.AreEqual(1, IosNotificationRules.SecondsUntil(now.AddHours(-3), now));
            Assert.AreEqual(1, IosNotificationRules.SecondsUntil(now.AddMilliseconds(200), now));
        }

        // The trigger counts whole seconds; rounding down would fire the reminder before its time.
        [Test]
        public void AFireTimeInTheFuture_RoundsUpToWholeSeconds()
        {
            var now = new DateTime(2026, 11, 26, 12, 0, 0);
            Assert.AreEqual(7200, IosNotificationRules.SecondsUntil(now.AddHours(2), now));
            Assert.AreEqual(11, IosNotificationRules.SecondsUntil(now.AddSeconds(10.2), now));
        }

        // Cancel(int) rebuilds the identifier it scheduled with; a mismatch would cancel nothing.
        [Test]
        public void TheIdentifierOfAnId_IsStableAndDistinctPerId()
        {
            Assert.AreEqual(IosNotificationRules.Identifier(42), IosNotificationRules.Identifier(42));
            Assert.AreNotEqual(IosNotificationRules.Identifier(42), IosNotificationRules.Identifier(43));
        }
    }
}
