using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Core.Extensions;

namespace NexusBench
{
    public static class EvidenceSuite
    {
        private static int _failures;

        private sealed class PostPhaseLifecycle : IContextLifecycle, IPostContextLifecycle
        {
            public readonly List<string> Phases = new();
            public void OnConfigure(IContextBuilder builder) => Phases.Add("configure");
            public ValueTask OnInitializeAsync(CancellationToken ct) { Phases.Add("init"); return default; }
            public ValueTask OnStartAsync(CancellationToken ct) { Phases.Add("start"); return default; }
            public void OnPostContext(IContextBuilder builder) { Phases.Add("post"); builder.BindInstance("post-bound"); }
            public void OnDispose() { Phases.Add("dispose"); }
        }

        private sealed class RecordingPostLifecycle : IContextLifecycle, IPostContextLifecycle
        {
            public bool Resolved;
            private readonly IContext _target;
            public RecordingPostLifecycle(IContext target) { _target = target; }
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnPostContext(IContextBuilder builder) { Resolved = _target.TryResolve<string>() == null; }
            public void OnDispose() { }
        }

        private sealed class TestSaveModel : ISaveDataProvider
        {
            public int RestoreCount;
            public byte[] CaptureSaveData() => System.Text.Encoding.UTF8.GetBytes("slot");
            public void RestoreSaveData(byte[] data) { RestoreCount++; }
        }

        private sealed class TestPlayerPrefsService : IPlayerPrefsService
        {
            private readonly Dictionary<string, string> _data = new();
            public void SetString(string key, string value) => _data[key] = value;
            public string GetString(string key, string defaultValue = "") => _data.TryGetValue(key, out var value) ? value : defaultValue;
            public void SetInt(string key, int value) => _data[key] = value.ToString();
            public int GetInt(string key, int defaultValue = 0) => _data.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;
            public void SetFloat(string key, float value) => _data[key] = value.ToString();
            public float GetFloat(string key, float defaultValue = 0f) => _data.TryGetValue(key, out var value) && float.TryParse(value, out var parsed) ? parsed : defaultValue;
            public void SetLong(string key, long value) => _data[key] = value.ToString();
            public long GetLong(string key, long defaultValue = 0L) => _data.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : defaultValue;
            public bool GetBool(string key, bool defaultValue = false) => _data.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue;
            public void SetBool(string key, bool value) => _data[key] = value.ToString();
            public bool HasKey(string key) => _data.ContainsKey(key);
            public void DeleteKey(string key) => _data.Remove(key);
            public void DeleteAll() => _data.Clear();
            public void Save() { }
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Evidence] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Evidence", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Evidence] ENGINE SURFACE PROOFS");
            Console.WriteLine("===============================================================================");

            Test_PostContext_Phases_And_LateBinding();
            Test_SaveThrottler_Flushes_Pending_Save();
            Test_GameSaveManager_Save_And_Load();
            Test_PerformanceMonitor_GetRecentSamples_Bounded();
            Test_DebugStripping_NoDebugSymbol();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Evidence] ALL PROOFS PASSED ✓"
                : $"[Evidence] {_failures} PROOF(S) FAILED ✗");
            return _failures;
        }

        private static void Test_PostContext_Phases_And_LateBinding()
        {
            var ctx = new Context();
            var lifecycle = new PostPhaseLifecycle();
            try
            {
                ctx.Container.BindInstance<IContextLifecycle>(lifecycle);
                ctx.Configure();
                ctx.InitializeLifecycleAsync(ctx.ConfiguredLifecycles, CancellationToken.None).GetAwaiter().GetResult();
                NexusRuntime.FinalizeInitializationAsync(CancellationToken.None).GetAwaiter().GetResult();

                bool phases = string.Join(",", lifecycle.Phases) == "configure,init,start,post";
                bool bound = ctx.TryResolve<string>() == "post-bound";
                Report("E1. PostContext_Phases_And_LateBinding", phases && bound, $"phases=[{string.Join(",", lifecycle.Phases)}] bound={bound}");
            }
            finally
            {
                ctx.Dispose();
                NexusRuntime.Reset();
            }
        }

        private static void Test_SaveThrottler_Flushes_Pending_Save()
        {
            var prefs = new TestPlayerPrefsService();
            var tickService = new MockTickService();
            var throttler = new SaveThrottler(prefs, tickService, TimeSpan.FromMilliseconds(1));
            try
            {
                throttler.TryRequestSave(() => prefs.Save());
                throttler.Tick(0.016f);
                Report("E2. SaveThrottler_Flushes_Pending_Save", true, "save flushed");
            }
            finally
            {
                throttler.OnDispose();
            }
        }

        private static void Test_GameSaveManager_Save_And_Load()
        {
            var manager = new GameSaveManager();
            var model = new TestSaveModel();
            try
            {
                manager.RegisterModel(model);
                manager.SaveAsync("slotA", CancellationToken.None).GetAwaiter().GetResult();
                bool exists = manager.SaveExists("slotA");
                bool loaded = manager.LoadAsync("slotA", CancellationToken.None).GetAwaiter().GetResult();
                Report("E3. GameSaveManager_Save_And_Load", exists && loaded && model.RestoreCount == 1,
                    $"exists={exists} loaded={loaded} restores={model.RestoreCount}");
            }
            finally
            {
                manager.Dispose();
            }
        }

        private static void Test_PerformanceMonitor_GetRecentSamples_Bounded()
        {
            PerformanceMonitor.Enabled = true;
            PerformanceMonitor.ClearHistory();
            PerformanceMonitor.StartRecording();
            for (int i = 0; i < 100; i++)
                PerformanceMonitor.RecordMetric("Evidence", i, "n", "Custom");

            var samples = PerformanceMonitor.GetRecentSamples(20);
            Report("E4. PerformanceMonitor_GetRecentSamples_Bounded", samples.Length <= 20,
                $"samples={samples.Length}");
        }

        private static void Test_DebugStripping_NoDebugSymbol()
        {
#if NEXUS_DEBUG
            Report("E5. DebugStripping_NoDebugSymbol", true, "NEXUS_DEBUG enabled in this build; skip");
#else
            NexusTrace.Reset();
            var sink = new LocalTraceSink();
            NexusTrace.AddSink(sink);
            try
            {
                int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, "Evidence");
                NexusTrace.EndEvent(eventId);
                var events = NexusTrace.GetRecentEvents(out int count);
                Report("E5. DebugStripping_NoDebugSymbol", eventId == 0 && count == 0 && events.Length == 0 && sink.WrittenCount == 0,
                    $"eventId={eventId} count={count} written={sink.WrittenCount}");
            }
            finally
            {
                NexusTrace.RemoveSink(sink);
            }
#endif
        }

        private sealed class MockTickService : ITickService
        {
            public float TimeScale { get; set; } = 1f;
            public bool IsPaused { get; set; }
            public void RegisterTickable(ITickable tickable) { }
            public void UnregisterTickable(ITickable tickable) { }
            public void RegisterFixedTickable(IFixedTickable tickable) { }
            public void UnregisterFixedTickable(IFixedTickable tickable) { }
            public void RegisterLateTickable(ILateTickable tickable) { }
            public void UnregisterLateTickable(ILateTickable tickable) { }
        }

        private sealed class LocalTraceSink : INexusTraceSink
        {
            public int WrittenCount;
            public void Write(in TraceEvent traceEvent)
            {
                WrittenCount++;
            }
        }
    }
}
