#if NEXUS_STORAGE_PROBE && !UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using Nexus.Core.Services;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NexusVerify.Device
{
    internal sealed class StorageWriteProbe : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create() => new GameObject("[StorageWriteProbe]").AddComponent<StorageWriteProbe>();

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(3);
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 16);
            yield return null;
            long idle = recorder.LastValue;
            var control = new byte[1024 * 1024];
            yield return null;
            long positive = recorder.LastValue;
            GC.KeepAlive(control);
            if (!recorder.Valid || positive < 1024 * 1024)
            {
                Debug.LogError($"[StorageWriteProbe] FAIL frame allocation positive control={positive}");
                yield break;
            }
            foreach (int length in new[] { 64, 4096, 32768 })
            {
                yield return null;
                double milliseconds = Measure(length);
                yield return null;
                long bytes = recorder.LastValue;
                Debug.Log($"[StorageWriteProbe] {(milliseconds >= 0 ? "PASS" : "FAIL")} payloadChars={length} saves=40 keysPerSave=2 wholeFrameBytes={bytes} bytesPerSaveUpperBound={bytes / 40} msPerSave={milliseconds:F3} idleFrameBytes={idle} positiveControl={positive}");
            }
        }

        private static double Measure(int length)
        {
            try
            {
                string salt = "write_probe_100_" + length;
                string a = new string('a', length), b = new string('b', length);
                using var store = new EncryptedStorageService(salt);
                store.SetString("main", a);
                store.SetString("backup", a);
                if (!store.TrySave()) throw new InvalidOperationException("Warmup save failed.");
                long start = Stopwatch.GetTimestamp();
                const int saves = 40;
                for (int i = 0; i < saves; i++)
                {
                    store.SetString("main", (i & 1) == 0 ? b : a);
                    store.SetString("backup", (i & 1) == 0 ? b : a);
                    if (!store.TrySave()) throw new InvalidOperationException("Measured save failed.");
                }
                long elapsed = Stopwatch.GetTimestamp() - start;
                using var reader = new EncryptedStorageService(salt);
                if (reader.GetString("main") != a || reader.GetString("backup") != a)
                    throw new InvalidOperationException("Fresh store readback failed.");
                store.DeleteKey("main");
                store.DeleteKey("backup");
                return elapsed * 1000.0 / Stopwatch.Frequency / saves;
            }
            catch (Exception error) { Debug.LogError("[StorageWriteProbe] FAIL " + error); return -1; }
        }

    }
}
#endif
