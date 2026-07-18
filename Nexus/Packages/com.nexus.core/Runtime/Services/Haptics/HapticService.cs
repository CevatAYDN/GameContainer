using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Platform-specific zero-alloc haptic feedback management.
    /// Caches native Android/iOS references to prevent GC spikes during rapid haptic triggers.
    /// </summary>
    public class HapticService : IHapticService, INexusService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        public bool IsEnabled { get; set; } = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _vibrator;
        private AndroidJavaClass _vibrationEffectClass;
        private int _sdkVersion;
#endif

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            if (PlayerPrefsService != null)
            {
                IsEnabled = !PlayerPrefsService.GetBool("NT_HapticsDisabled", false);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            InitAndroidVibrator();
#endif
            return default;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void InitAndroidVibrator()
        {
            try
            {
                using (var versionClass = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    _sdkVersion = versionClass.GetStatic<int>("SDK_INT");
                }

                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }

                if (_sdkVersion >= 26)
                {
                    _vibrationEffectClass = new AndroidJavaClass("android.os.VibrationEffect");
                }
            }
            catch (System.Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[HapticService] Failed to initialize Android Vibrator: {ex.Message}");
            }
        }
#endif

        public void OnDispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _vibrator?.Dispose();
            _vibrator = null;
            _vibrationEffectClass?.Dispose();
            _vibrationEffectClass = null;
#endif
        }

        public void Vibrate(HapticType type)
        {
            if (!IsEnabled) return;

#if UNITY_IOS && !UNITY_EDITOR
            switch (type)
            {
                case HapticType.Light:     Handheld.Vibrate(); break;
                case HapticType.Medium:    Handheld.Vibrate(); break;
                case HapticType.Heavy:     Handheld.Vibrate(); break;
                case HapticType.Warning:   Handheld.Vibrate(); break;
                case HapticType.Success:   Handheld.Vibrate(); break;
                case HapticType.Selection: Handheld.Vibrate(); break;
                default:                   Handheld.Vibrate(); break;
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            long ms;
            int amplitude;
            switch (type)
            {
                case HapticType.Light:     ms = 10;  amplitude = 30; break;
                case HapticType.Medium:    ms = 30;  amplitude = 60; break;
                case HapticType.Heavy:     ms = 60;  amplitude = 120; break;
                case HapticType.Warning:   ms = 100; amplitude = 180; break;
                case HapticType.Success:   ms = 150; amplitude = 100; break;
                case HapticType.Selection: ms = 10;  amplitude = 25; break;
                default:                   ms = 20;  amplitude = 50; break;
            }

            if (_vibrator != null)
            {
                try
                {
                    if (_sdkVersion >= 26 && _vibrationEffectClass != null)
                    {
                        using (var effect = _vibrationEffectClass.CallStatic<AndroidJavaObject>("createOneShot", ms, amplitude))
                        {
                            _vibrator.Call("vibrate", effect);
                        }
                    }
                    else
                    {
                        _vibrator.Call("vibrate", ms);
                    }
                }
                catch
                {
                    Handheld.Vibrate();
                }
            }
            else
            {
                Handheld.Vibrate();
            }
#else
            // Editor / Desktop log preview
#endif
        }
    }
}

