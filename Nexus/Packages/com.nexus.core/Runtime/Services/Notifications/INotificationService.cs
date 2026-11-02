using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>Whether the app may post notifications (#160).</summary>
    public enum NotificationPermission
    {
        /// <summary>The phone lets the app post notifications.</summary>
        Allowed,
        /// <summary>Android 13+: the player has not been asked yet, or refused. After two refusals Android shows its dialog no
        /// more, and only <see cref="INotificationService.OpenSettings"/> still helps.</summary>
        NeedsPermission,
        /// <summary>Notifications are turned off for the app in the phone's settings; no dialog turns them on.</summary>
        Blocked,
        /// <summary>No notification provider in this build (the editor, another platform).</summary>
        Unavailable,
    }

    /// <summary>
    /// Local notifications the app schedules on the device (#160): one channel, the permission to post, and the notification
    /// a tap opened the app with. Android: <c>com.nexus.notifications.unity</c>'s <c>UnityNotificationBindings</c>.
    /// </summary>
    public interface INotificationService
    {
        NotificationPermission Permission { get; }

        /// <summary>
        /// Shows Android's permission dialog when it needs one and completes with the answer; an app that is allowed, blocked
        /// or may not ask any more gets its state at once, without a dialog. A cancelled <paramref name="ct"/> stops the wait,
        /// not the dialog.
        /// </summary>
        ValueTask<NotificationPermission> RequestPermissionAsync(CancellationToken ct);

        /// <summary>Opens the app's notification page in the phone's settings.</summary>
        void OpenSettings();

        /// <summary>
        /// Registers the channel the notifications post to: <paramref name="name"/> and <paramref name="description"/> are what
        /// the phone's settings list, so call it again after a language change (Android updates a channel of the same id).
        /// <paramref name="smallIcon"/> is the status-bar icon of its notifications, an identifier registered in Project
        /// Settings &gt; Mobile Notifications; null shows the app icon.
        /// </summary>
        void SetChannel(string id, string name, string description, string smallIcon = null);

        /// <summary>
        /// Schedules a notification on the channel for <paramref name="fireTime"/> (local time; Android may deliver it a few
        /// minutes late). A tap opens the app and removes the notification; <paramref name="data"/> is what
        /// <see cref="TakeOpenedWith"/> then returns. Returns the notification's id, -1 when nothing was scheduled. Throws
        /// <see cref="InvalidOperationException"/> before <see cref="SetChannel"/>.
        /// </summary>
        int Schedule(string title, string text, DateTime fireTime, string data = null);

        /// <summary>Removes the notification with this id, scheduled or shown.</summary>
        void Cancel(int id);

        /// <summary>Removes every notification of the app, scheduled and shown (a player who is back needs no reminder).</summary>
        void CancelAll();

        /// <summary>The data of the notification the player tapped to open the app, once per tap; null otherwise.</summary>
        string TakeOpenedWith();
    }
}
