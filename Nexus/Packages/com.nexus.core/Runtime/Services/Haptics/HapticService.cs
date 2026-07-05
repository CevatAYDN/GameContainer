using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Platform-spesifik haptic feedback (titreşim) yönetimi.
    /// IPlayerPrefsService yardımıyla bağımsız olarak çalışabilir.
    /// </summary>
    public class HapticService : IHapticService, INexusService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        public bool IsEnabled { get; set; } = true;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            if (PlayerPrefsService != null)
            {
                // SettingsModel bağımlılığını kopararak doğrudan depolamadan okuyoruz.
                IsEnabled = !PlayerPrefsService.GetBool("NT_HapticsDisabled", false);
            }
            return default;
        }

        public void OnDispose() { }

        public void Vibrate(HapticType type)
        {
            if (!IsEnabled) return;
#if UNITY_IOS
            switch (type)
            {
                case HapticType.Light:     Handheld.Vibrate(); break;
                case HapticType.Medium:    Handheld.Vibrate(); break;
                case HapticType.Heavy:     Handheld.Vibrate(); break;
                case HapticType.Warning:   Handheld.Vibrate(); break;
                case HapticType.Success:   Handheld.Vibrate(); break;
                case HapticType.Selection: Handheld.Vibrate(); break;
            }
#elif UNITY_ANDROID
            long ms;
            int amplitude;
            switch (type)
            {
                case HapticType.Light:     ms = 10;  amplitude = 30; break;
                case HapticType.Medium:    ms = 30;  amplitude = 50; break;
                case HapticType.Heavy:     ms = 60;  amplitude = 80; break;
                case HapticType.Warning:   ms = 100; amplitude = 100; break;
                case HapticType.Success:   ms = 200; amplitude = 70; break;
                case HapticType.Selection: ms = 10;  amplitude = 20; break;
                default:                   ms = 20;  amplitude = 50; break;
            }
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator"))
                {
                    if (vibrator != null)
                    {
                        vibrator.Call("vibrate", ms);
                    }
                }
            }
            catch
            {
                Handheld.Vibrate();
            }
#else
            Debug.Log($"[HapticService] {type}");
#endif
        }
    }
}
