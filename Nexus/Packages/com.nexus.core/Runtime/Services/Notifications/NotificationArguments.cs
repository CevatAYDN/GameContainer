using System;

namespace Nexus.Core.Services
{
    /// <summary>The argument rules every <see cref="INotificationService"/> keeps, so a mistake shows in the editor too.</summary>
    internal static class NotificationArguments
    {
        internal static void CheckChannel(string id, string name)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A notification channel needs an id.", nameof(id));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A notification channel needs the name the phone's settings list.", nameof(name));
        }

        internal static void CheckSchedule(bool channelSet, string title, string text)
        {
            if (string.IsNullOrEmpty(title)) throw new ArgumentException("A notification needs a title.", nameof(title));
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("A notification needs a text.", nameof(text));
            if (!channelSet) throw new InvalidOperationException("Call SetChannel before Schedule: Android posts a notification only on a registered channel.");
        }
    }
}
