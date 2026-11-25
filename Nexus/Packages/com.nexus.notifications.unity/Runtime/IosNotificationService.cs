#if NEXUS_UNITY_NOTIFICATIONS && UNITY_IOS
using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Unity.Notifications.iOS;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Notifications.Unity
{
    /// <summary>
    /// Unity Mobile Notifications on iOS behind <see cref="INotificationService"/> (#459). Nothing asks for the permission at
    /// launch: keep "Request Authorization on App Launch" off in the notifications package's project settings, and call
    /// <see cref="RequestPermissionAsync"/> when the player sees why a reminder helps.
    /// </summary>
    [Preserve]
    public sealed class IosNotificationService : NexusService<INotificationService>, INotificationService
    {
        private bool _channel;
        // Pending notifications outlive the process and share identifiers, so a counter from 1 would replace an earlier launch's.
        private int _nextId = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x3FFFFFFF);
        private string _lastOpenedIdentifier;

        public NotificationPermission Permission =>
            IosNotificationRules.ToPermission((IosAuthorization)(int)iOSNotificationCenter.GetNotificationSettings().AuthorizationStatus);

        public async ValueTask<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
            // iOS shows its dialog only while the status is NotDetermined; otherwise the request finishes at once with the standing answer.
            using (var request = new AuthorizationRequest(AuthorizationOption.Alert | AuthorizationOption.Badge | AuthorizationOption.Sound, false))
            {
                while (!request.IsFinished)
                    await Awaitable.NextFrameAsync(ct);
            }
            return Permission;
        }

        public void OpenSettings() => Application.OpenURL("app-settings:");

        // iOS has no channels: the id, name and description are only validated, and the small icon is the app icon.
        public void SetChannel(string id, string name, string description, string smallIcon = null)
        {
            NotificationArguments.CheckChannel(id, name);
            _channel = true;
        }

        public int Schedule(string title, string text, DateTime fireTime, string data = null)
        {
            NotificationArguments.CheckSchedule(_channel, title, text);
            int id = _nextId++;
            var notification = new iOSNotification(IosNotificationRules.Identifier(id))
            {
                Title = title,
                Body = text,
                Data = data,
                ShowInForeground = false,
                Trigger = new iOSNotificationTimeIntervalTrigger
                {
                    TimeInterval = TimeSpan.FromSeconds(IosNotificationRules.SecondsUntil(fireTime, DateTime.Now)),
                    Repeats = false,
                },
            };
            try
            {
                iOSNotificationCenter.ScheduleNotification(notification);
            }
            catch (Exception e)
            {
                NexusLog.WarnAlways($"[NotificationService] iOS did not schedule '{title}': {e.Message}");
                return -1;
            }
            return id;
        }

        public void Cancel(int id) => iOSNotificationCenter.RemoveScheduledNotification(IosNotificationRules.Identifier(id));

        public void CancelAll()
        {
            iOSNotificationCenter.RemoveAllScheduledNotifications();
            iOSNotificationCenter.RemoveAllDeliveredNotifications();
        }

        public string TakeOpenedWith()
        {
            // The query the package prefers is asynchronous; this contract is synchronous, so the obsolete getter it replaces stays.
#pragma warning disable CS0618
            var notification = iOSNotificationCenter.GetLastRespondedNotification();
#pragma warning restore CS0618
            if (notification == null || notification.Identifier == _lastOpenedIdentifier) return null;
            _lastOpenedIdentifier = notification.Identifier;
            return notification.Data;
        }
    }
}
#endif
