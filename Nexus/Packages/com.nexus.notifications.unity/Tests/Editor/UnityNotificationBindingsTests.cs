using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Notifications.Unity.Mocks;
using UnityEngine;
using UnityEngine.TestTools;
#if NEXUS_UNITY_NOTIFICATIONS
using Unity.Notifications.Android;
#endif

namespace Nexus.Notifications.Unity.Tests
{
    /// <summary>#160: which notification service each build gets; a player never binds the mock (the rule of #171).</summary>
    public sealed class UnityNotificationBindingsTests
    {
        [Test]
        public void TheEditorGetsTheMock()
        {
            using var test = NexusTestHarness.CreateContext(UnityNotificationBindings.Bind, autoInitialize: true);
            Assert.IsInstanceOf<MockNotificationService>(test.Context.Resolve<INotificationService>());
        }

        [Test]
        public void APlayerWithoutTheSdk_BindsTheUnavailableService_WithAnErrorNamingThePackage()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Notifications\] com\.unity\.mobile\.notifications 2\.5\.0 or newer is not installed, so INotificationService posts no notification in this player"));
            using var test = NexusTestHarness.CreateContext(builder => UnityNotificationBindings.Bind(builder, player: true, withSdk: false), autoInitialize: true);
            Assert.IsInstanceOf<UnavailableNotificationService>(test.Context.Resolve<INotificationService>());
        }

#if NEXUS_UNITY_NOTIFICATIONS
        // The package's AndroidNotificationCenter adds a scene object at its first call in the editor: binding the Android
        // service must not make that call.
        [Test]
        public void APlayerWithTheSdk_BindsUnityNotifications_WithoutCallingTheCenter()
        {
            using var test = NexusTestHarness.CreateContext(builder => UnityNotificationBindings.Bind(builder, player: true, withSdk: true), autoInitialize: true);
            Assert.IsInstanceOf<UnityNotificationService>(test.Context.Resolve<INotificationService>());
            Assert.IsNull(GameObject.Find("AndroidReceivedNotificationMainThreadDispatcher"));
        }

        // Android shows its dialog at most twice; a refused or never-asked app still needs permission, only the phone's settings unblock it.
        [Test]
        public void AndroidsStatuses_MapToNeedsPermissionUntilAllowed_AndBlockedWhenTurnedOff()
        {
            Assert.AreEqual(NotificationPermission.Allowed, UnityNotificationService.ToPermission(PermissionStatus.Allowed));
            Assert.AreEqual(NotificationPermission.NeedsPermission, UnityNotificationService.ToPermission(PermissionStatus.NotRequested));
            Assert.AreEqual(NotificationPermission.NeedsPermission, UnityNotificationService.ToPermission(PermissionStatus.Denied));
            Assert.AreEqual(NotificationPermission.Blocked, UnityNotificationService.ToPermission(PermissionStatus.NotificationsBlockedForApp));
        }
#endif
    }
}
