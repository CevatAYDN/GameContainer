using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The notification service of a build without a provider (#160): the permission is
    /// <see cref="NotificationPermission.Unavailable"/>, nothing is scheduled (-1), and the first call says so in every build's log.
    /// </summary>
    [Preserve]
    public sealed class UnavailableNotificationService : NexusService<INotificationService>, INotificationService
    {
        private int _warned;
        private bool _channel;

        public NotificationPermission Permission => NotificationPermission.Unavailable;

        public ValueTask<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
            WarnOnce();
            return new ValueTask<NotificationPermission>(NotificationPermission.Unavailable);
        }

        public void OpenSettings() => WarnOnce();

        public void SetChannel(string id, string name, string description, string smallIcon = null)
        {
            NotificationArguments.CheckChannel(id, name);
            _channel = true;
        }

        public int Schedule(string title, string text, DateTime fireTime, string data = null)
        {
            NotificationArguments.CheckSchedule(_channel, title, text);
            WarnOnce();
            return -1;
        }

        public void Cancel(int id) { }

        public void CancelAll() { }

        public string TakeOpenedWith() => null;

        private void WarnOnce()
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                NexusLog.WarnAlways("[NotificationService] No notification provider is bound, so this build posts no notification "
                    + "(on Android: com.nexus.notifications.unity's UnityNotificationBindings).");
        }
    }
}
