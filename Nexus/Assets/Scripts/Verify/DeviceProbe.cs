#if UNITY_ANDROID && DEVELOPMENT_BUILD && !UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using Nexus.Core.Services;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NexusVerify.Device
{
    /// <summary>
    /// Phone checks for issues #77 and #84, compiled only into development Android players of this project
    /// (NexusVerifyBuild.BuildAndroidIL2CPPDevelopment). Logs "[Probe]" lines for logcat: the GC.Alloc samples of every
    /// haptic Play (the first and later plays of two built effects), and on each resume the wall-clock, Stopwatch and
    /// elapsedRealtime time since the pause beside OfflineTimeCalculator's answer.
    /// </summary>
    internal sealed class DeviceProbe : MonoBehaviour
    {
        private HapticService _haptics;
        private long _pauseWallMs, _pauseStopwatchMs, _pauseRealtimeMs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject("[NexusVerify.DeviceProbe]");
            DontDestroyOnLoad(go);
            go.AddComponent<DeviceProbe>();
        }

        private IEnumerator Start()
        {
            _haptics = new HapticService();
            _haptics.InitializeAsync(default);
            var a = _haptics.CreateEffect(45, 170);
            var b = _haptics.CreateEffect(30, 120);
            yield return new WaitForSeconds(1f);
            Measure("A first", a);
            yield return new WaitForSeconds(0.5f);
            Measure("B first", b);
            for (int i = 0; i < 5; i++)
            {
                yield return new WaitForSeconds(0.3f);
                Measure("A again", a);
                yield return new WaitForSeconds(0.3f);
                Measure("B again", b);
            }
            Debug.Log("[Probe] haptics done");
        }

        private void Measure(string label, HapticEffect effect)
        {
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1000, ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            if (!recorder.Valid) { Debug.Log("[Probe] GC.Alloc recorder unavailable"); return; }
            var control = new byte[4096];
            GC.KeepAlive(control);
            recorder.Stop();
            long controlCount = recorder.Count;
            recorder.Reset();
            recorder.Start();
            _haptics.Play(effect);
            recorder.Stop();
            Debug.Log($"[Probe] haptic {label}: gcAllocSamples={recorder.Count} (positive control {controlCount})");
        }

        private void OnApplicationPause(bool paused)
        {
            var prefs = new UnityPlayerPrefsService();
            long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long stopwatch = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            long realtime;
            using (var clock = new AndroidJavaClass("android.os.SystemClock")) realtime = clock.CallStatic<long>("elapsedRealtime");
            if (paused)
            {
                (_pauseWallMs, _pauseStopwatchMs, _pauseRealtimeMs) = (wall, stopwatch, realtime);
                OfflineTimeCalculator.RecordQuitTimestamp(prefs);
                Debug.Log("[Probe] paused");
                return;
            }
            if (_pauseWallMs == 0) return;
            long offline = OfflineTimeCalculator.CalculateOfflineSeconds(prefs, long.MaxValue);
            Debug.Log($"[Probe] resumed wallMs={wall - _pauseWallMs} stopwatchMs={stopwatch - _pauseStopwatchMs} " +
                $"elapsedRealtimeMs={realtime - _pauseRealtimeMs} offlineSeconds={offline}");
        }
    }
}
#endif
