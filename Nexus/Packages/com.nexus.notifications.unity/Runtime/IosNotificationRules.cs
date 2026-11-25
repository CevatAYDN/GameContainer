using System;
using Nexus.Core.Services;

namespace Nexus.Notifications.Unity
{
    /// <summary>The values of the notifications package's <c>AuthorizationStatus</c> (iOS), mirrored so the mapping compiles on every platform.</summary>
    internal enum IosAuthorization
    {
        NotDetermined = 0,
        Denied = 1,
        Authorized = 2,
        Provisional = 3,
        Ephemeral = 4,
    }

    /// <summary>The platform-neutral decisions of <c>IosNotificationService</c>, kept here so editor tests reach them.</summary>
    internal static class IosNotificationRules
    {
        // iOS shows its dialog once: after Denied only the Settings app helps, which is what Blocked means.
        internal static NotificationPermission ToPermission(IosAuthorization status) => status switch
        {
            IosAuthorization.Authorized or IosAuthorization.Provisional or IosAuthorization.Ephemeral => NotificationPermission.Allowed,
            IosAuthorization.Denied => NotificationPermission.Blocked,
            _ => NotificationPermission.NeedsPermission,
        };

        // A time-interval trigger counts whole seconds and iOS rejects less than one.
        internal static int SecondsUntil(DateTime fireTime, DateTime now) => (int)Math.Max(1, Math.Ceiling((fireTime - now).TotalSeconds));

        internal static string Identifier(int id) => "nexus-" + id;
    }
}
