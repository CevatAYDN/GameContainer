using System;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Play-Mode Chaos & Fuzzing Stress Agent.
    /// Fires rapid signals, toggles states, and monitors GC allocations and error collections.
    /// </summary>
    public static class NexusFuzzAgent
    {
        [MenuItem("Nexus/Run Play-Mode Chaos Fuzzing Test", false, 2)]
        public static void RunFuzzingTest()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Nexus Chaos Fuzzer", "Chaos Fuzzing Stress Test can only be run during Play-Mode.\n\nPlease enter Play-Mode and run this command again.", "OK");
                return;
            }

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[Nexus Fuzzer] No active Contexts found in scene to fuzz.");
                return;
            }

            int signalFires = 5000;
            int initialErrorCount = ErrorCollection.TotalErrorCount;
            long memoryBefore = GC.GetTotalMemory(false);

            var sw = Stopwatch.StartNew();
            UnityEngine.Debug.Log($"[Nexus Fuzzer] Starting Chaos Fuzzing Test across {contexts.Count} active Context(s)... ({signalFires} rapid iterations)");

            for (int i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i];
                if (ctx?.SignalBus == null) continue;

                for (int j = 0; j < signalFires; j++)
                {
                    // Fire diagnostic tick signals to stress test the bus
                    ctx.SignalBus.Fire(new FuzzDiagnosticSignal(j));
                }
            }

            sw.Stop();
            long memoryAfter = GC.GetTotalMemory(false);
            int newErrors = ErrorCollection.TotalErrorCount - initialErrorCount;
            long memoryDelta = memoryAfter - memoryBefore;

            UnityEngine.Debug.Log("===============================================================================");
            UnityEngine.Debug.Log($"[Nexus Fuzzer] CHAOS FUZZING COMPLETED IN {sw.ElapsedMilliseconds}ms");
            UnityEngine.Debug.Log($"      Total Dispatches: {signalFires * contexts.Count}");
            UnityEngine.Debug.Log($"      New Errors/Exceptions Captured: {newErrors}");
            UnityEngine.Debug.Log($"      GC Memory Delta: {memoryDelta} bytes");
            UnityEngine.Debug.Log("===============================================================================");

            if (newErrors == 0)
            {
                EditorUtility.DisplayDialog("Nexus Chaos Fuzzer", $"Chaos Stress Test Passed! ✓\n\nFired Dispatches: {signalFires * contexts.Count}\nExceptions Captured: 0\nExecution Time: {sw.ElapsedMilliseconds}ms", "OK");
            }
            else
            {
                UnityEngine.Debug.LogError($"[Nexus Fuzzer] FAIL  Chaos test encountered {newErrors} exception(s)! Check Error Dashboard / Console.");
                EditorUtility.DisplayDialog("Nexus Chaos Fuzzer", $"Chaos Stress Test Encountered Errors! ✗\n\nNew Errors Captured: {newErrors}\nCheck Unity Console / Error Dashboard.", "OK");
            }
        }

        public struct FuzzDiagnosticSignal
        {
            public int Iteration;
            public FuzzDiagnosticSignal(int iter) => Iteration = iter;
        }
    }
}
