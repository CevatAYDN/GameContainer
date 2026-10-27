using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Platform haptic feedback. Every effect (the six <see cref="HapticType"/> presets and the game's own
    /// one-shots and waveforms) is defined once; on Android 8+ its <c>VibrationEffect</c> is built once and played
    /// through a cached JNI method, so a trigger allocates nothing.
    /// </summary>
    [Preserve]
    public class HapticService : NexusService<IHapticService>, IHapticService
    {
        private const string KeyDisabled = "NT_HapticsDisabled";

        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        // Optional: the game's store for the on/off setting (SettingsStores.Haptics); null uses PlayerPrefsService.
        [OptionalInject] public SettingsStores SettingsStores { get; set; }
        private IPlayerPrefsService Store => SettingsStores?.Haptics ?? PlayerPrefsService;

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                var store = Store;
                if (store == null) return;
                store.SetBool(KeyDisabled, !value);
                store.Save();
            }
        }

        // Index = effect id - 1; the first six are the HapticType presets in enum order.
        private readonly List<long[]> _timings = new();
        private readonly List<int[]> _amplitudes = new();

        public HapticService()
        {
            Define(10, 30);   // Light
            Define(30, 60);   // Medium
            Define(60, 120);  // Heavy
            Define(100, 180); // Warning
            Define(150, 100); // Success
            Define(10, 25);   // Selection
        }

        private int Define(long milliseconds, int amplitude) => Define(new[] { 0L, milliseconds }, new[] { 0, amplitude });

        private int Define(long[] timings, int[] amplitudes)
        {
            _timings.Add(timings);
            _amplitudes.Add(amplitudes);
            int index = _timings.Count - 1;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_vibrateMethod != IntPtr.Zero) BuildEffect(index);
#endif
            return index + 1;
        }

        public HapticEffect CreateEffect(long milliseconds, int amplitude = -1)
        {
            if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "A haptic effect lasts at least 1 ms.");
            if (amplitude != -1 && (amplitude < 1 || amplitude > 255)) throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "Amplitude is 1-255, or -1 for the device default.");
            return new HapticEffect(Define(milliseconds, amplitude));
        }

        public HapticEffect CreateWaveform(long[] timings, int[] amplitudes)
        {
            if (timings == null) throw new ArgumentNullException(nameof(timings));
            if (amplitudes == null) throw new ArgumentNullException(nameof(amplitudes));
            if (timings.Length == 0 || timings.Length != amplitudes.Length) throw new ArgumentException("A waveform needs one amplitude per timing and at least one timing.", nameof(amplitudes));
            long total = 0;
            for (int i = 0; i < timings.Length; i++)
            {
                if (timings[i] < 0) throw new ArgumentOutOfRangeException(nameof(timings), timings[i], "Timings are not negative.");
                if (amplitudes[i] < -1 || amplitudes[i] > 255) throw new ArgumentOutOfRangeException(nameof(amplitudes), amplitudes[i], "Amplitudes are 0-255, or -1 for the device default.");
                total += timings[i];
            }
            if (total <= 0) throw new ArgumentException("A waveform lasts at least 1 ms.", nameof(timings));
            return new HapticEffect(Define((long[])timings.Clone(), (int[])amplitudes.Clone()));
        }

        public void Vibrate(HapticType type)
        {
            if (!_isEnabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            // UnityEngine.iOS.Device.PlaySystemSound plays system SOUND ids, not the haptic engine; per-type
            // CoreHaptics needs a native plugin, so Handheld.Vibrate() is the built-in path that vibrates.
            Handheld.Vibrate();
#elif UNITY_ANDROID && !UNITY_EDITOR
            PlayIndex((int)type);
#else
            // Editor / Desktop: log haptic requests so developers see they're happening.
            NexusRuntime.Logger?.Log($"[HapticService] Vibrate({type}) — editor/desktop preview (no-op).");
#endif
        }

        public void Play(HapticEffect effect)
        {
            if (!_isEnabled || effect.Id < 1 || effect.Id > _timings.Count) return;
#if UNITY_IOS && !UNITY_EDITOR
            Handheld.Vibrate();
#elif UNITY_ANDROID && !UNITY_EDITOR
            PlayIndex(effect.Id - 1);
#endif
        }

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            var store = Store;
            if (store != null) _isEnabled = !store.GetBool(KeyDisabled, false);
#if UNITY_ANDROID && !UNITY_EDITOR
            InitAndroidVibrator();
#endif
            return default;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _vibrator;
        private AndroidJavaClass _vibrationEffectClass;
        private int _sdkVersion;
        private bool _hasVibrator = true;
        private bool _amplitudeControl;
        private bool _nativeFailureReported;
        // vibrate(VibrationEffect) and one prebuilt argument array per effect (a global ref to the effect).
        private IntPtr _vibrateMethod;
        private readonly List<jvalue[]> _effectArgs = new();

        private void InitAndroidVibrator()
        {
            try
            {
                using (var versionClass = new AndroidJavaClass("android.os.Build$VERSION"))
                    _sdkVersion = versionClass.GetStatic<int>("SDK_INT");

                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");

                if (_vibrator == null) { _hasVibrator = false; return; }
                _hasVibrator = _vibrator.Call<bool>("hasVibrator");
                if (_sdkVersion < 26) return;

                _amplitudeControl = _vibrator.Call<bool>("hasAmplitudeControl");
                _vibrationEffectClass = new AndroidJavaClass("android.os.VibrationEffect");
                IntPtr vibratorClass = AndroidJNI.GetObjectClass(_vibrator.GetRawObject());
                _vibrateMethod = AndroidJNI.GetMethodID(vibratorClass, "vibrate", "(Landroid/os/VibrationEffect;)V");
                AndroidJNI.DeleteLocalRef(vibratorClass);
                for (int i = 0; i < _timings.Count; i++) BuildEffect(i);
            }
            catch (Exception ex)
            {
                _vibrateMethod = IntPtr.Zero;
                NexusRuntime.Logger?.LogWarning($"[HapticService] Failed to initialize Android Vibrator: {ex.Message}");
            }
        }

        private void BuildEffect(int index)
        {
            while (_effectArgs.Count <= index) _effectArgs.Add(null);
            var timings = _timings[index];
            var amplitudes = _amplitudes[index];
            int[] used = new int[amplitudes.Length];
            for (int i = 0; i < used.Length; i++)
                used[i] = _amplitudeControl || amplitudes[i] == 0 ? amplitudes[i] : -1; // DEFAULT_AMPLITUDE
            try
            {
                using (var effect = timings.Length == 2 && timings[0] == 0
                    ? _vibrationEffectClass.CallStatic<AndroidJavaObject>("createOneShot", timings[1], used[1])
                    : _vibrationEffectClass.CallStatic<AndroidJavaObject>("createWaveform", timings, used, -1))
                {
                    _effectArgs[index] = new[] { new jvalue { l = AndroidJNI.NewGlobalRef(effect.GetRawObject()) } };
                }
            }
            catch (Exception ex)
            {
                ReportNativeFailure(ex.Message);
            }
        }

        private void PlayIndex(int index)
        {
            if (_vibrator == null || !_hasVibrator) { Handheld.Vibrate(); return; }
            var args = index < _effectArgs.Count ? _effectArgs[index] : null;
            if (args == null) { PlayWithoutEffect(index); return; }
            AndroidJNI.CallVoidMethod(_vibrator.GetRawObject(), _vibrateMethod, args);
            IntPtr error = AndroidJNI.ExceptionOccurred();
            if (error != IntPtr.Zero) VibrateThrew(error);
        }

        private void VibrateThrew(IntPtr error)
        {
            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(error);
            ReportNativeFailure("vibrate(VibrationEffect) threw");
            Handheld.Vibrate();
        }

        // Android 7 and older (or an effect that could not be built): vibrate(long), which allocates.
        private void PlayWithoutEffect(int index)
        {
            try
            {
                long total = 0;
                foreach (var t in _timings[index]) total += t;
                _vibrator.Call("vibrate", total);
            }
            catch (Exception ex)
            {
                ReportNativeFailure(ex.Message);
                Handheld.Vibrate();
            }
        }

        private void ReportNativeFailure(string message)
        {
            if (_nativeFailureReported) return;
            _nativeFailureReported = true;
            NexusRuntime.Logger?.LogWarning($"[HapticService] Android vibrator call failed ({message}); falling back to Handheld.Vibrate().");
        }
#endif

        public override void OnDispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            foreach (var args in _effectArgs)
                if (args != null) AndroidJNI.DeleteGlobalRef(args[0].l);
            _effectArgs.Clear();
            _vibrateMethod = IntPtr.Zero;
            _vibrator?.Dispose();
            _vibrator = null;
            _vibrationEffectClass?.Dispose();
            _vibrationEffectClass = null;
#endif
        }
    }
}
