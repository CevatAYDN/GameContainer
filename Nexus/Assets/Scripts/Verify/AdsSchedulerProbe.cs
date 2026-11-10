#if UNITY_ANDROID && NEXUS_DEVICE_PROBE && !UNITY_EDITOR
using System;
using System.Collections;
using Nexus.Ads.Unity;
using Unity.Profiling;
using UnityEngine;

namespace NexusVerify.Device
{
    internal sealed class AdsSchedulerProbe : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            if (intent.Call<string>("getStringExtra", "nexusProbe") == "scheduler418")
                new GameObject("[AdsSchedulerProbe]").AddComponent<AdsSchedulerProbe>();
        }

        private IEnumerator Start()
        {
            var scheduler = gameObject.AddComponent<UnityAdScheduler>();
            int calls = 0;
            Action callback = () => calls++;
            scheduler.Schedule(0, callback);
            yield return null;
            yield return null;
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1000,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            GC.KeepAlive(new byte[4096]);
            recorder.Stop();
            bool control = recorder.Valid && recorder.Count > 0;
            recorder.Reset();
            recorder.Start();
            var old = scheduler.Schedule(0, callback);
            recorder.Stop();
            long allocations = recorder.Count;
            yield return null;
            yield return null;
            int before = calls;
            scheduler.Schedule(0, callback);
            old.Dispose();
            var cancelled = scheduler.Schedule(0, callback);
            cancelled.Dispose();
            yield return null;
            yield return null;
            bool passed = control && allocations == 1 && calls == before + 1;
            Debug.Log($"[AdsSchedulerProbe] {(passed ? "PASS" : "FAIL")} scheduleAllocations={allocations} positiveControl={control} callbacks={calls} staleTokenSafe={calls == before + 1}");
        }
    }
}
#endif
