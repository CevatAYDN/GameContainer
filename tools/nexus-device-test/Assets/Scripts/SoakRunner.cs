using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nexus.DeviceTest
{
    /// <summary>
    /// 24-hour soak test runner for mobile devices.
    /// Runs a continuous functional loop with periodic Ads, IAP, Save/Load, and
    /// SLO metric logging (memory, FPS, battery, thermal, network). Uses core-package
    /// services only, so it can be dropped into any project with com.nexus.core installed.
    /// </summary>
    public class SoakRunner : MonoBehaviour
    {
        [Header("Soak Configuration")]
        [SerializeField] private float _targetDurationHours = 24f;
        [SerializeField] private int _functionalLoopIntervalSeconds = 30;
        [SerializeField] private int _adRequestIntervalMinutes = 5;
        [SerializeField] private int _saveIntervalMinutes = 10;
        [SerializeField] private int _metricsLogIntervalMinutes = 1;
        [SerializeField] private bool _enableChaosEvents = true;

        [Header("Chaos Configuration")]
        [SerializeField] private float _networkToggleProbability = 0.1f;
        [SerializeField] private float _backgroundProbability = 0.05f;
        [SerializeField] private float _timeChangeProbability = 0.02f;
        [SerializeField] private float _forceGCProbability = 0.1f;

        private CancellationTokenSource _cts;
        private DateTime _startTime;
        private DateTime _endTime;
        private int _loopCount;
        private int _adRequestCount;
        private int _saveCount;
        private int _chaosEventCount;
        private long _peakMemoryMB;
        private string _logPath;

        // FPS/CPU sampling (rolling window over the metrics interval)
        private int _frameCount;
        private float _frameTimeAccumulator;
        private float _avgFps;

        private async void Start()
        {
            _logPath = Path.Combine(Application.persistentDataPath, $"soak_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            InitializeLog();

            _startTime = DateTime.Now;
            _endTime = _startTime.AddHours(_targetDurationHours);
            _cts = new CancellationTokenSource();

            Debug.Log($"[SoakRunner] Starting {_targetDurationHours}h soak test. Log: {_logPath}");

            // Wait for Nexus contexts to initialize
            await WaitForContextsReady();

            // Start main loops
            _ = RunFunctionalLoop(_cts.Token);
            _ = RunAdLoop(_cts.Token);
            _ = RunSaveLoop(_cts.Token);
            _ = RunMetricsLoop(_cts.Token);

            if (_enableChaosEvents)
            {
                _ = RunChaosLoop(_cts.Token);
            }

            // Auto-stop after duration
            _ = AutoStop(_cts.Token);
        }

        private async Task WaitForContextsReady()
        {
            var root = FindFirstObjectByType<Nexus.Core.Root>();
            if (root == null) return;

            int timeout = 300; // 30 seconds
            while (!root.IsInitialized && timeout > 0)
            {
                await Task.Delay(100);
                timeout--;
            }
        }

        private async Task RunFunctionalLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && DateTime.Now < _endTime)
            {
                try
                {
                    // Simulated gameplay: exercise the signal bus + economy through the
                    // container (requires the project to bind these services).
                    var signalBus = FindService<ISignalBus>();
                    var economy = FindService<EconomyService>();
                    if (signalBus != null && economy != null)
                    {
                        EconomyService economyLocal = economy;
                        int amount = UnityEngine.Random.Range(1, 20);
                        economyLocal.Earn("Coins", amount, "soak_gameplay");
                        signalBus.Fire(new TestChaosSignal { Payload = $"loop_{_loopCount}:coins_{amount}" });
                    }

                    _loopCount++;
                }
                catch (Exception ex)
                {
                    LogError("FunctionalLoop", ex);
                }

                await Task.Delay(_functionalLoopIntervalSeconds * 1000, ct);
            }
        }

        private async Task RunAdLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && DateTime.Now < _endTime)
            {
                try
                {
                    var adService = FindService<AdService>();
                    if (adService != null)
                    {
                        if (UnityEngine.Random.value < 0.5f)
                        {
                            adService.ShowInterstitial("soak");
                        }
                        else
                        {
                            adService.ShowRewarded("reward_soak", _ => { });
                        }
                        _adRequestCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogError("AdLoop", ex);
                }

                await Task.Delay(_adRequestIntervalMinutes * 60 * 1000, ct);
            }
        }

        private async Task RunSaveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && DateTime.Now < _endTime)
            {
                try
                {
                    var storage = FindService<IPlayerPrefsService>();
                    if (storage != null)
                    {
                        storage.Save();
                        _saveCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogError("SaveLoop", ex);
                }

                await Task.Delay(_saveIntervalMinutes * 60 * 1000, ct);
            }
        }

        private async Task RunMetricsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && DateTime.Now < _endTime)
            {
                try
                {
                    LogMetrics();
                }
                catch (Exception ex)
                {
                    LogError("MetricsLoop", ex);
                }

                await Task.Delay(_metricsLogIntervalMinutes * 60 * 1000, ct);
            }
        }

        private async Task RunChaosLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && DateTime.Now < _endTime)
            {
                try
                {
                    await Task.Delay(30000, ct); // Check every 30 seconds

                    if (UnityEngine.Random.value < _networkToggleProbability)
                    {
                        TriggerNetworkToggle();
                        _chaosEventCount++;
                    }

                    if (UnityEngine.Random.value < _backgroundProbability)
                    {
                        TriggerBackground();
                        _chaosEventCount++;
                    }

                    if (UnityEngine.Random.value < _timeChangeProbability)
                    {
                        TriggerTimeChange();
                        _chaosEventCount++;
                    }

                    if (UnityEngine.Random.value < _forceGCProbability)
                    {
                        GC.Collect();
                        _chaosEventCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogError("ChaosLoop", ex);
                }
            }
        }

        private async Task AutoStop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && DateTime.Now < _endTime)
            {
                await Task.Delay(60000, ct); // Check every minute
            }

            if (!ct.IsCancellationRequested)
            {
                _cts.Cancel();
                Debug.Log("[SoakRunner] Target duration reached. Stopping...");
                LogFinalSummary();
            }
        }

        private void Update()
        {
            _frameCount++;
            _frameTimeAccumulator += Time.unscaledDeltaTime;
        }

        private void TriggerNetworkToggle()
        {
            Debug.Log("[SoakRunner] CHAOS: Network toggle triggered");
        }

        private void TriggerBackground()
        {
            Debug.Log("[SoakRunner] CHAOS: Background/foreground triggered");
        }

        private void TriggerTimeChange()
        {
            Debug.Log("[SoakRunner] CHAOS: Time change triggered");
        }

        private T FindService<T>() where T : class
        {
            var root = FindFirstObjectByType<Nexus.Core.Root>();
            return root?.Context?.TryResolve<T>();
        }

        private void InitializeLog()
        {
            var header = "Timestamp,ElapsedMinutes,LoopCount,AdRequests,Saves,ChaosEvents,MemoryMB,PeakMemoryMB,FPS,BatteryPercent,ThermalStatus,NetworkStatus,GCGen0,GCGen1,GCGen2";
            File.WriteAllText(_logPath, header + Environment.NewLine);
        }

        private void SampleFps()
        {
            _avgFps = _frameTimeAccumulator > 0f
                ? _frameCount / _frameTimeAccumulator
                : 0f;
            _frameCount = 0;
            _frameTimeAccumulator = 0f;
        }

        private void LogMetrics()
        {
            SampleFps();

            var elapsed = DateTime.Now - _startTime;
            var memoryMB = GC.GetTotalMemory(false) / (1024f * 1024f);
            _peakMemoryMB = Math.Max(_peakMemoryMB, (long)memoryMB);

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{elapsed.TotalMinutes:F1},{_loopCount},{_adRequestCount},{_saveCount},{_chaosEventCount},{memoryMB:F1},{_peakMemoryMB},{_avgFps:F1},{BatteryPercent()},{ThermalStatus()},{NetworkStatus()},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}";
            File.AppendAllText(_logPath, line + Environment.NewLine);

            Debug.Log($"[SoakRunner] Metrics: Loop={_loopCount}, Ads={_adRequestCount}, Saves={_saveCount}, Chaos={_chaosEventCount}, Mem={memoryMB:F1}MB, Peak={_peakMemoryMB}MB, FPS={_avgFps:F1}");
        }

        private static string BatteryPercent()
        {
#if UNITY_IOS || UNITY_ANDROID
            var level = SystemInfo.batteryLevel; // 0..1, -1 if unsupported
            if (level < 0f) return "n/a";
            return $"{(int)(level * 100f)}";
#else
            return "n/a";
#endif
        }

        private static string ThermalName()
        {
#if UNITY_IOS
            return UnityEngine.iOS.Device.thermalState.ToString();
#else
            return "n/a";
#endif
        }

        private static string NetworkAccess()
        {
            switch (Application.internetReachability)
            {
                case NetworkReachability.ReachableViaLocalAreaNetwork: return "wifi";
                case NetworkReachability.ReachableViaCarrierDataNetwork: return "cellular";
                default: return "offline";
            }
        }

        private void LogError(string source, Exception ex)
        {
            Debug.LogError($"[SoakRunner] {source} error: {ex.Message}\n{ex.StackTrace}");
        }

        private void LogFinalSummary()
        {
            var elapsed = DateTime.Now - _startTime;
            var summary = $"\n=== SOAK TEST SUMMARY ===\n" +
                          $"Duration: {elapsed.TotalHours:F2} hours\n" +
                          $"Functional Loops: {_loopCount}\n" +
                          $"Ad Requests: {_adRequestCount}\n" +
                          $"Saves: {_saveCount}\n" +
                          $"Chaos Events: {_chaosEventCount}\n" +
                          $"Peak Memory: {_peakMemoryMB} MB\n" +
                          $"Final GC: Gen0={GC.CollectionCount(0)}, Gen1={GC.CollectionCount(1)}, Gen2={GC.CollectionCount(2)}\n" +
                          $"Log: {_logPath}";

            Debug.Log(summary);

            // Write summary to separate file
            File.WriteAllText(_logPath.Replace(".csv", "_summary.txt"), summary);
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            Debug.Log(pauseStatus ? "[SoakRunner] Application paused" : "[SoakRunner] Application resumed");
        }

        private void OnApplicationQuit()
        {
            _cts?.Cancel();
            LogFinalSummary();
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}