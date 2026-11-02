using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.Notifications.Unity.Mocks
{
    /// <summary>A notification the mock was asked to schedule.</summary>
    public readonly struct ScheduledNotification
    {
        public ScheduledNotification(int id, string title, string text, DateTime fireTime, string data)
        {
            Id = id;
            Title = title;
            Text = text;
            FireTime = fireTime;
            Data = data;
        }

        public int Id { get; }
        public string Title { get; }
        public string Text { get; }
        public DateTime FireTime { get; }
        public string Data { get; }
    }

    /// <summary>
    /// The editor's notification service (#160): nothing is posted, a log line per scheduled notification, and the state a test
    /// or the game's editor flow sets: the <see cref="Permission"/> (allowed by default, and what a request answers), what is
    /// <see cref="Scheduled"/>, and the data <see cref="OpenedWith"/> the next <see cref="TakeOpenedWith"/> returns. It keeps the
    /// argument rules of the Android service, so a schedule before <see cref="SetChannel"/> throws here too.
    /// </summary>
    [Preserve]
    public sealed class MockNotificationService : NexusService<INotificationService>, INotificationService
    {
        private readonly List<ScheduledNotification> _scheduled = new List<ScheduledNotification>();
        private bool _channel;
        private int _nextId = 1;

        public NotificationPermission Permission { get; set; } = NotificationPermission.Allowed;
        public IReadOnlyList<ScheduledNotification> Scheduled => _scheduled;
        public string OpenedWith { get; set; }
        public int PermissionRequests { get; private set; }
        public int SettingsOpened { get; private set; }

        public ValueTask<NotificationPermission> RequestPermissionAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PermissionRequests++;
            return new ValueTask<NotificationPermission>(Permission);
        }

        public void OpenSettings() => SettingsOpened++;

        public void SetChannel(string id, string name, string description, string smallIcon = null)
        {
            NotificationArguments.CheckChannel(id, name);
            _channel = true;
        }

        public int Schedule(string title, string text, DateTime fireTime, string data = null)
        {
            NotificationArguments.CheckSchedule(_channel, title, text);
            var notification = new ScheduledNotification(_nextId++, title, text, fireTime, data);
            _scheduled.Add(notification);
            NexusRuntime.Logger?.Log($"[Notifications] '{title}' scheduled for {fireTime:o}: editor mock, nothing is posted.");
            return notification.Id;
        }

        public void Cancel(int id) => _scheduled.RemoveAll(n => n.Id == id);

        public void CancelAll() => _scheduled.Clear();

        public string TakeOpenedWith()
        {
            var data = OpenedWith;
            OpenedWith = null;
            return data;
        }
    }
}
