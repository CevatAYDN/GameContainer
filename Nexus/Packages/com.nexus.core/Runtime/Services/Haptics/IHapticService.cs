namespace Nexus.Core.Services
{
    public enum HapticType
    {
        Light,      // UI Impact (Light)
        Medium,     // UI Impact (Medium)
        Heavy,      // UI Impact (Heavy)
        Warning,    // UI Notification (Warning)
        Success,    // UI Notification (Success)
        Selection,  // UI Selection
    }

    public interface IHapticService
    {
        void Vibrate(HapticType type);
        bool IsEnabled { get; set; }
    }
}
