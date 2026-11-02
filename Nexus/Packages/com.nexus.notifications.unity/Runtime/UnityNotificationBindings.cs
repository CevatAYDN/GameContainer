using System.Runtime.CompilerServices;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Notifications.Unity.Mocks;
using UnityEngine;

[assembly: InternalsVisibleTo("com.nexus.notifications.unity.tests")]

namespace Nexus.Notifications.Unity
{
    /// <summary>
    /// Binds <see cref="INotificationService"/> (#160): an Android player gets Unity Mobile Notifications, and one whose
    /// notifications package is older than 2.5.0 gets the core's unavailable service with an error at start; the editor gets
    /// <see cref="MockNotificationService"/>, because the package's Android API adds a scene object there at its first call. The
    /// choice is made here because <c>NEXUS_UNITY_NOTIFICATIONS</c> exists only inside this assembly. Like the package's Android
    /// API, the assembly exists in Android players and the editor only, so a game that also builds for another platform calls
    /// it under <c>#if UNITY_ANDROID || UNITY_EDITOR</c>.
    /// </summary>
    public static class UnityNotificationBindings
    {
        public static void Bind(IContextBuilder builder) => Bind(builder, player: !Application.isEditor, withSdk: true);

        // withSdk false takes the path of a player without the notifications package, in a project that has it (tests).
        internal static void Bind(IContextBuilder builder, bool player, bool withSdk)
        {
            if (!player)
            {
                builder.BindService<INotificationService, MockNotificationService>();
                return;
            }
#if NEXUS_UNITY_NOTIFICATIONS
            if (withSdk)
            {
                builder.BindService<INotificationService, UnityNotificationService>();
                return;
            }
#endif
            NexusRuntime.Logger?.LogError("[Notifications] com.unity.mobile.notifications 2.5.0 or newer is not installed, so "
                + "INotificationService posts no notification in this player (com.nexus.notifications.unity README, Install).");
            builder.BindService<INotificationService, UnavailableNotificationService>();
        }
    }
}
