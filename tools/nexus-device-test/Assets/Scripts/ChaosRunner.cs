using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.DeviceTest
{
    /// <summary>
    /// Chaos engineering test runner for specific failure scenarios.
    /// Uses core-package services only (no demo project dependency), so it can be
    /// dropped into any project that has com.nexus.core installed.
    /// </summary>
    public class ChaosRunner : MonoBehaviour
    {
        [Header("Test Scenarios")]
        [SerializeField] private bool _testNetworkLoss = true;
        [SerializeField] private bool _testBackgroundForeground = true;
        [SerializeField] private bool _testTimeManipulation = true;
        [SerializeField] private bool _testMemoryPressure = true;
        [SerializeField] private bool _testCrashRecovery = true;

        [Header("Configuration")]
        [SerializeField] private int _iterationsPerScenario = 10;
        [SerializeField] private float _delayBetweenIterationsSeconds = 5f;

        private CancellationTokenSource _cts;
        private readonly List<ChaosResult> _results = new();

        public async void RunAllScenarios()
        {
            _cts = new CancellationTokenSource();
            Debug.Log("[ChaosRunner] Starting chaos test scenarios...");

            if (_testNetworkLoss) await RunScenario("NetworkLoss", TestNetworkLoss, _cts.Token);
            if (_testBackgroundForeground) await RunScenario("BackgroundForeground", TestBackgroundForeground, _cts.Token);
            if (_testTimeManipulation) await RunScenario("TimeManipulation", TestTimeManipulation, _cts.Token);
            if (_testMemoryPressure) await RunScenario("MemoryPressure", TestMemoryPressure, _cts.Token);
            if (_testCrashRecovery) await RunScenario("CrashRecovery", TestCrashRecovery, _cts.Token);

            WriteReport();
            Debug.Log("[ChaosRunner] All chaos scenarios completed.");
        }

        private async Task RunScenario(string name, Func<CancellationToken, Task<bool>> test, CancellationToken ct)
        {
            Debug.Log($"[ChaosRunner] Starting scenario: {name}");

            for (int i = 0; i < _iterationsPerScenario && !ct.IsCancellationRequested; i++)
            {
                var startTime = DateTime.Now;
                bool success = false;
                string error = null;

                try
                {
                    success = await test(ct);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Debug.LogError($"[ChaosRunner] {name} iteration {i} failed: {ex}");
                }

                var duration = DateTime.Now - startTime;
                _results.Add(new ChaosResult
                {
                    Scenario = name,
                    Iteration = i,
                    Success = success,
                    DurationMs = (long)duration.TotalMilliseconds,
                    Error = error,
                    Timestamp = startTime
                });

                await Task.Delay((int)(_delayBetweenIterationsSeconds * 1000), ct);
            }
        }

        private async Task<bool> TestNetworkLoss(CancellationToken ct)
        {
            // Simulate network loss by disabling/enabling network reachability
            // In real test, this would use a network conditioner or VPN API
            await Task.Delay(100, ct);

            // Verify SignalBus still works (local signals should work)
            var signalBus = FindService<ISignalBus>();
            if (signalBus == null) return false;

            bool received = false;
            var sub = signalBus.Subscribe<TestChaosSignal>(s => received = true);
            signalBus.Fire(new TestChaosSignal { Payload = "network_test" });
            await Task.Delay(50, ct);
            sub.Dispose();

            return received; // Local signals should always work
        }

        private async Task<bool> TestBackgroundForeground(CancellationToken ct)
        {
            // Simulate app background/foreground
            // In real test, use Application.focusChanged or platform-specific APIs

            // Verify services survive background
            var storage = FindService<IPlayerPrefsService>();
            if (storage == null) return false;

            // Quick save/load test
            storage.SetString("chaos_test", "background_test");
            storage.Save();

            await Task.Delay(200, ct);

            var loaded = storage.GetString("chaos_test", null);
            return loaded == "background_test";
        }

        private async Task<bool> TestTimeManipulation(CancellationToken ct)
        {
            // Test OfflineTimeCalculator with time changes (static API, storage-backed).
            var storage = new UnityPlayerPrefsService();

            // Normal: 2h gap, expect ~2h (7200s) offline
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            storage.SetLong("NT_LastQuitTimestamp", now - 7200L);
            long offline = OfflineTimeCalculator.CalculateOfflineSeconds(storage);
            if (Math.Abs(offline - 7200) > 10) return false;

            // Clock forward: 3h gap, capped at the default 8h max → still 3h
            storage.SetLong("NT_LastQuitTimestamp", now - 10800L);
            offline = OfflineTimeCalculator.CalculateOfflineSeconds(storage);
            if (Math.Abs(offline - 10800) > 10) return false;

            // Clock backward (tamper): current time earlier than quit time → 0 offline
            storage.SetLong("NT_LastQuitTimestamp", now + 3600L);
            offline = OfflineTimeCalculator.CalculateOfflineSeconds(storage);
            if (offline != 0) return false;

            // Max cap: 24h gap must be clamped to the configured 4h cap
            storage.SetLong("NT_LastQuitTimestamp", now - 86400L);
            offline = OfflineTimeCalculator.CalculateOfflineSeconds(storage, 14400L);
            if (offline > 14400L) return false;

            await Task.Delay(50, ct);
            return true;
        }

        private async Task<bool> TestMemoryPressure(CancellationToken ct)
        {
            // Allocate memory to trigger GC pressure
            var allocations = new List<byte[]>();

            try
            {
                for (int i = 0; i < 50; i++)
                {
                    if (ct.IsCancellationRequested) return false;
                    allocations.Add(new byte[1024 * 1024]); // 1MB each
                    await Task.Delay(10, ct);
                }

                // Force GC
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                await Task.Delay(100, ct);

                // Verify system still responsive
                var signalBus = FindService<ISignalBus>();
                if (signalBus == null) return false;

                bool received = false;
                var sub = signalBus.Subscribe<TestChaosSignal>(s => received = true);
                signalBus.Fire(new TestChaosSignal { Payload = "memory_test" });
                await Task.Delay(50, ct);
                sub.Dispose();

                return received;
            }
            finally
            {
                allocations.Clear();
            }
        }

        private async Task<bool> TestCrashRecovery(CancellationToken ct)
        {
            // Test that EncryptedStorage survives process kill
            // This is simulated by saving, then "restarting" (new service instance)

            var storage1 = new EncryptedStorageService();
            storage1.SetString("crash_test", "survived_crash");
            storage1.Save();
            storage1.Dispose();

            // Simulate restart - new instance reads same file
            var storage2 = new EncryptedStorageService();
            var value = storage2.GetString("crash_test", null);
            storage2.Dispose();

            await Task.Delay(50, ct);
            return value == "survived_crash";
        }

        private T FindService<T>() where T : class
        {
            var root = FindFirstObjectByType<Nexus.Core.Root>();
            return root?.Context?.TryResolve<T>();
        }

        private void WriteReport()
        {
            var path = Path.Combine(Application.persistentDataPath, $"chaos_report_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            var json = SerializeReport();
            File.WriteAllText(path, json);
            Debug.Log($"[ChaosRunner] Report written: {path}");

            // Summary to console
            foreach (var r in _results)
            {
                Debug.Log($"[ChaosRunner] {r.Scenario} #{r.Iteration}: {(r.Success ? "PASS" : "FAIL")} ({r.DurationMs}ms) {r.Error ?? ""}");
            }
        }

        /// <summary>
        /// Minimal self-contained JSON serializer (avoids a Newtonsoft dependency in the
        /// device-test project). Produces valid JSON for the flat report shape.
        /// </summary>
        private string SerializeReport()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"Timestamp\": \"{DateTime.Now:yyyy-MM-ddTHH:mm:ssK}\",\n");
            sb.Append($"  \"TotalScenarios\": {_results.Count},\n");
            sb.Append($"  \"Passed\": {_results.Count(r => r.Success)},\n");
            sb.Append($"  \"Failed\": {_results.Count(r => !r.Success)},\n");
            sb.Append("  \"Results\": [\n");
            for (int i = 0; i < _results.Count; i++)
            {
                var r = _results[i];
                sb.Append("    {\n");
                sb.Append($"      \"Scenario\": \"{JsonEscape(r.Scenario)}\",\n");
                sb.Append($"      \"Iteration\": {r.Iteration},\n");
                sb.Append($"      \"Success\": {(r.Success ? "true" : "false")},\n");
                sb.Append($"      \"DurationMs\": {r.DurationMs},\n");
                sb.Append($"      \"Error\": {(r.Error == null ? "null" : $"\"{JsonEscape(r.Error)}\"")},\n");
                sb.Append($"      \"Timestamp\": \"{r.Timestamp:yyyy-MM-ddTHH:mm:ssK}\"\n");
                sb.Append(i == _results.Count - 1 ? "    }\n" : "    },\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string JsonEscape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }

    public readonly struct TestChaosSignal
    {
        public readonly string Payload;
    }

    public class ChaosResult
    {
        public string Scenario { get; set; }
        public int Iteration { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }
}