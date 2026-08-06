using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Platform-specific zero-alloc haptic feedback management.
    /// Caches native Android/iOS references to prevent GC spikes during rapid haptic triggers.
    /// </summary>
    [Preserve]
    public class HapticService : NexusService<IHapticService>, IHapticService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        public bool IsEnabled { get; set; } = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _vibrator;
        private AndroidJavaClass _vibrationEffectClass;
        private int _sdkVersion;
        private bool _hasVibrator = true;

        // Pre-created immutable VibrationEffect per HapticType (SDK 26+). The old hot path
        // called createOneShot on EVERY Vibrate — allocating an AndroidJavaObject wrapper
        // plus boxed long/int args per trigger, violating the service's 0-GC claim.
        // VibrationEffect is immutable, so caching one per type makes Vibrate() allocation-
        // minimal (only the unavoidable JNI params array remains).
        private readonly AndroidJavaObject[] _vibrationEffects = new AndroidJavaObject[6];
#endif

        public override ValueTask InitializeAsync(CancellationToken ct)
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

                if (_vibrator != null)
                {
                    _hasVibrator = _vibrator.Call<bool>("hasVibrator");
                }

                if (_sdkVersion >= 26)
                {
                    _vibrationEffectClass = new AndroidJavaClass("android.os.VibrationEffect");
                    // Pre-cache one effect per HapticType so the per-trigger path is allocation-free.
                    // Explicit per-member assignment (not an index loop) so a future enum
                    // member added in the middle can never silently shift the mapping.
                    _vibrationEffects[(int)HapticType.Light] = CreateCachedEffect(HapticType.Light);
                    _vibrationEffects[(int)HapticType.Medium] = CreateCachedEffect(HapticType.Medium);
                    _vibrationEffects[(int)HapticType.Heavy] = CreateCachedEffect(HapticType.Heavy);
                    _vibrationEffects[(int)HapticType.Warning] = CreateCachedEffect(HapticType.Warning);
                    _vibrationEffects[(int)HapticType.Success] = CreateCachedEffect(HapticType.Success);
                    _vibrationEffects[(int)HapticType.Selection] = CreateCachedEffect(HapticType.Selection);
                }
            }
            catch (System.Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[HapticService] Failed to initialize Android Vibrator: {ex.Message}");
            }
        }

        private AndroidJavaObject CreateCachedEffect(HapticType type)
        {
            var (ms, amplitude) = GetHapticPattern(type);
            return _vibrationEffectClass.CallStatic<AndroidJavaObject>("createOneShot", ms, amplitude);
        }
#endif

        /// <summary>Returns the vibration duration/amplitude for a haptic type (SDK 26+ uses
        /// amplitude; pre-26 uses duration only). Single source of truth for the pattern table.
        /// Moved OUTSIDE the UNITY_ANDROID guard — the iOS path also needs it, and the
        /// old placement made the iOS build fail to compile (GetHapticPattern was undefined).</summary>
        private static (long ms, int amplitude) GetHapticPattern(HapticType type)
        {
            switch (type)
            {
                case HapticType.Light:     return (10, 30);
                case HapticType.Medium:    return (30, 60);
                case HapticType.Heavy:     return (60, 120);
                case HapticType.Warning:   return (100, 180);
                case HapticType.Success:   return (150, 100);
                case HapticType.Selection: return (10, 25);
                default:                   return (20, 50);
            }
        }

        public override void OnDispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            for (int i = 0; i < _vibrationEffects.Length; i++)
            {
                _vibrationEffects[i]?.Dispose();
                _vibrationEffects[i] = null;
            }
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
            // UnityEngine.iOS.Device.PlaySystemSound(int) plays one of a small set of
            // predefined SYSTEM SOUND IDs — it does NOT drive the haptic engine, and the
            // computed values (10*30/100=3, 60*120/100=72, …) are arbitrary IDs, most out of
            // the valid range. Handheld.Vibrate() on iOS triggers the real system haptic
            // motor. (True per-type CoreHaptics differentiation requires a native plugin;
            // Handheld.Vibrate is the correct built-in fallback that actually vibrates.)
            Handheld.Vibrate();
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (_vibrator != null && _hasVibrator)
            {
                try
                {
                    if (_sdkVersion >= 26 && (int)type < _vibrationEffects.Length && _vibrationEffects[(int)type] != null)
                    {
                        // Reuse the pre-cached immutable effect — no JNI wrapper, no boxing.
                        _vibrator.Call("vibrate", _vibrationEffects[(int)type]);
                    }
                    else if (_sdkVersion >= 26 && _vibrationEffectClass != null)
                    {
                        // Defensive fallback for any future HapticType not pre-cached.
                        var (ms, amplitude) = GetHapticPattern(type);
                        using (var effect = _vibrationEffectClass.CallStatic<AndroidJavaObject>("createOneShot", ms, amplitude))
                        {
                            _vibrator.Call("vibrate", effect);
                        }
                    }
                    else
                    {
                        _vibrator.Call("vibrate", GetHapticPattern(type).ms);
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
            // Editor / Desktop: log haptic requests so developers see they're happening.
            NexusRuntime.Logger?.Log($"[HapticService] Vibrate({type}) — editor/desktop preview (no-op).");
#endif
        }
    }
}

