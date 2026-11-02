#if NEXUS_UNITY_NOTIFICATIONS
using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Unity.Notifications.Android;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Notifications.Unity
{
    /// <summary>
    /// Unity Mobile Notifications on Android behind <see cref="INotificationService"/> (#160): one channel of default importance,
    /// notifications a tap removes. Bound in an Android player only: in the editor the package's
    /// <see cref="AndroidNotificationCenter"/> adds a scene object at its first call and reports no permission.
    /// </summary>
    [Preserve]
    public sealed class UnityNotificationService : NexusService<INotificationService>, INotificationService
    {
        private string _channelId;
        private string _smallIcon;
        private int _lastOpenedId = -1;

        public NotificationPermission Permission => ToPermission(AndroidNotificationCenter.UserPermissionToPost);

        public async ValueTask<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
            // The request shows Android's dialog only when it needs one and may still show it; its status changes later.
            var request = new PermissionRequest();
            while (request.Status == PermissionStatus.RequestPending)
                await Awaitable.NextFrameAsync(ct);
            return ToPermission(request.Status);
        }

        public void OpenSettings() => AndroidNotificationCenter.OpenNotificationSettings();

        public void SetChannel(string id, string name, string description, string smallIcon = null)
        {
            NotificationArguments.CheckChannel(id, name);
            AndroidNotificationCenter.RegisterNotificationChannel(new AndroidNotificationChannel(id, name, description, Importance.Default));
            _channelId = id;
            _smallIcon = smallIcon;
        }

        public int Schedule(string title, string text, DateTime fireTime, string data = null)
        {
            NotificationArguments.CheckSchedule(_channelId != null, title, text);
            var notification = new AndroidNotification(title, text, fireTime)
            {
                SmallIcon = _smallIcon,
                IntentData = data,
                ShouldAutoCancel = true,
            };
            int id = AndroidNotificationCenter.SendNotification(notification, _channelId);
            if (id < 0) NexusLog.WarnAlways($"[NotificationService] Android did not schedule '{title}'.");
            return id;
        }

        public void Cancel(int id) => AndroidNotificationCenter.CancelNotification(id);

        public void CancelAll() => AndroidNotificationCenter.CancelAllNotifications();

        public string TakeOpenedWith()
        {
            // The activity keeps the intent it was opened with; its id tells a new tap from one already taken.
            var intent = AndroidNotificationCenter.GetLastNotificationIntent();
            if (intent == null || intent.Id == _lastOpenedId) return null;
            _lastOpenedId = intent.Id;
            return intent.Notification.IntentData;
        }

        internal static NotificationPermission ToPermission(PermissionStatus status) => status switch
        {
            PermissionStatus.Allowed => NotificationPermission.Allowed,
            PermissionStatus.NotificationsBlockedForApp => NotificationPermission.Blocked,
            _ => NotificationPermission.NeedsPermission,
        };
    }
}
#endif
