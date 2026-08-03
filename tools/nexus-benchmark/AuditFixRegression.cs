// Runtime regression suite for the adversarial-audit fixes:
//   C-1 composite-trigger ThreadStatic buffer re-entrancy (nested completion)
//   C-2 NexusDI.Dispose sync-over-async deadlock (IAsyncDisposable singleton)
//   T-1 factory circular dependency → catchable InvalidOperationException
//   T-4 NetworkSignalBus.FireAtTick with async handlers (no mismatch throw)
//   T-5 ErrorCollection throwing subscribers isolated (no propagation)
//   T-6 rejected fallback still dispatches CommandFailedSignal (no silent drop)
//   T-7 cross-context broadcast to a bus with async handlers (no mismatch throw)
//   M-1 SaveThrottler failure backoff + retry cap (no tight retry loop)
//   M-4 ObservableList reentrant mutation notifications are queued, not dropped
//   M-6 GameSaveManager concurrent SaveAsync serialized (no torn temp writes)
//   M-7 PerformanceMonitor / NetworkMonitor throwing subscribers isolated
// Each test FAILS if the pre-fix behavior returns.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Extensions;
using Nexus.Core.Services;
using Nexus.Netcode;
using UnityEngine;

namespace NexusBench
{
    // ── C-1 types ─────────────────────────────────────────────────────────────
    public struct C1SigA { }
    public struct C1SigB { }
    public struct C1SigC { }
    public struct C1SigX { }

    /// <summary>Composite (A+B). On completion fires C — which completes the (C) trigger
    /// NESTED on the same thread, exercising the shared ThreadStatic buffer re-entrancy.</summary>
    public class C1CmdAB : ICompositeCommand
    {
        public static SignalBus Bus;
        public static int Fired;
        public void Execute(CompositeContext context)
        {
            Fired++;
            Bus?.Fire(new C1SigC());
        }
    }

    /// <summary>Composite (X+B). Completes in the SAME ProcessCompositeTriggers pass as
    /// C1CmdAB — the pre-fix nested Fire(C) cleared the shared buffer, so this command
    /// was silently lost (never executed).</summary>
    public class C1CmdXB : ICompositeCommand
    {
        public static int Fired;
        public void Execute(CompositeContext context) => Fired++;
    }

    /// <summary>Composite (C) — single-signal trigger completed by C1CmdAB's nested fire.</summary>
    public class C1CmdC : ICompositeCommand
    {
        public static int Fired;
        public void Execute(CompositeContext context) => Fired++;
    }

    // ── C-2 type ──────────────────────────────────────────────────────────────
    /// <summary>IAsyncDisposable whose DisposeAsync suspends once — on a non-pumping
    /// SynchronizationContext, the pre-fix sync-over-async GetResult() deadlocked forever.</summary>
    public class C2BlockingDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new ValueTask(Task.Delay(5));
    }

    // ── T-1 type ──────────────────────────────────────────────────────────────
    public class T1FactoryDep { }

    // ── T-4 types ─────────────────────────────────────────────────────────────
    public struct T4Sig : INetworkSignal { public int V; }
    public class T4AsyncCmd : IAsyncCommand<T4Sig>
    {
        public ValueTask ExecuteAsync(T4Sig signal, CancellationToken ct) => default;
    }

    // ── T-5 / M-7 helper ──────────────────────────────────────────────────────
    internal sealed class ThrowingSubscriber<T> : IDisposable
    {
        private readonly Action<T> _handler;
        public ThrowingSubscriber(Action<T> handler) => _handler = handler;
        public void Dispose() { }
        public void Invoke(T arg) => _handler(arg);
    }

    // ── T-6 types ─────────────────────────────────────────────────────────────
    public struct T6Sig { public int V; }
    public class T6ThrowingCmd : ICommand<T6Sig>
    {
        public void Execute(T6Sig signal) => throw new InvalidOperationException("t6-boom");
    }

    /// <summary>Async-only — rejected by the sync recovery context (cannot be awaited).</summary>
    public class T6InvalidFallback : IAsyncCommand<T6Sig>
    {
        public ValueTask ExecuteAsync(T6Sig signal, CancellationToken ct) => default;
    }

    // ── T-7 types ─────────────────────────────────────────────────────────────
    [CrossContext]
    public struct T7Sig { public int V; }
    public class T7AsyncCmd : IAsyncCommand<T7Sig>
    {
        public ValueTask ExecuteAsync(T7Sig signal, CancellationToken ct) => default;
    }

    // ── M-1 helper ────────────────────────────────────────────────────────────
    internal sealed class AuditFakeTimeProvider : ITimeProvider
    {
        public float Now { get; set; }
    }

    // ── M-6 helper ────────────────────────────────────────────────────────────
    internal sealed class AuditSaveModel : ISaveDataProvider
    {
        public string Data = "audit-v1";
        public byte[] CaptureSaveData() => System.Text.Encoding.UTF8.GetBytes(Data);
        public void RestoreSaveData(byte[] data) => Data = System.Text.Encoding.UTF8.GetString(data);
    }

    // ── M-3 pool-bound probe ──────────────────────────────────────────────────
    internal sealed class AuditPoolable : Component, IPoolable
    {
        public void OnSpawned() { }
        public void OnDespawned() { }
    }

    public static class AuditFixRegression
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === Audit-fix regression (C-1/C-2/T-1/T-4/T-5/T-6/T-7/M-1/M-3/M-4/M-6/M-7) ===");
            _failures = 0;

            Test_Composite_NestedCompletion_NoLostTriggers();
            Test_Dispose_AsyncSingleton_NoSyncOverAsyncDeadlock();
            Test_Factory_CircularDependency_ThrowsNotStackOverflow();
            Test_NetworkFireAtTick_AsyncHandlers_NoMismatch().GetAwaiter().GetResult();
            Test_ErrorCollection_ThrowingSubscriber_Isolated();
            Test_Recovery_RejectedFallback_FiresFailedSignal().GetAwaiter().GetResult();
            Test_CrossContext_AsyncTarget_NoMismatch();
            Test_SaveThrottler_FailureBackoff_And_RetryCap();
            Test_ObservableList_Reentrant_Notifications_Queued();
            Test_GameSaveManager_ConcurrentSaves_Serialized();
            Test_PerfMonitor_ThrowingSubscriber_Isolated();
            Test_NetworkMonitor_ThrowingSubscriber_Isolated();
            Test_ObjectPoolService_BoundRetention();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Nexus Benchmark] AUDIT-FIX REGRESSION PASSED ✓"
                : $"[Nexus Benchmark] {_failures} AUDIT-FIX REGRESSION(S) FAILED ✗");
            return _failures;
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("AuditFixRegression", name, ok, detail);
            if (!ok) _failures++;
        }

        // ── C-1 ────────────────────────────────────────────────────────────────
        private static void Test_Composite_NestedCompletion_NoLostTriggers()
        {
            var di = new NexusDI();
            di.Bind<C1CmdAB>(isSingleton: false);
            di.Bind<C1CmdXB>(isSingleton: false);
            di.Bind<C1CmdC>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());

            C1CmdAB.Bus = bus;
            C1CmdAB.Fired = 0;
            C1CmdXB.Fired = 0;
            C1CmdC.Fired = 0;

            bus.RegisterCompositeCommand(new[] { typeof(C1SigA), typeof(C1SigB) }, typeof(C1CmdAB), oneShot: false, priority: 0, isAsync: false);
            bus.RegisterCompositeCommand(new[] { typeof(C1SigX), typeof(C1SigB) }, typeof(C1CmdXB), oneShot: false, priority: 0, isAsync: false);
            bus.RegisterCompositeCommand(new[] { typeof(C1SigC) }, typeof(C1CmdC), oneShot: false, priority: 0, isAsync: false);

            bus.Fire(new C1SigA());
            bus.Fire(new C1SigX());
            bus.Fire(new C1SigB());

            // Pre-fix: Fire(B) completed AB + XB into the shared buffer; C1CmdAB's nested
            // Fire(C) then CLEARED the buffer and re-filled it, so the outer loop's i=1
            // iteration read the nested (C) entries — C1CmdXB never ran.
            bool ok = C1CmdAB.Fired == 1 && C1CmdXB.Fired == 1 && C1CmdC.Fired == 1;
            Check("C1. Composite_NestedCompletion_NoLostTriggers", ok,
                $"AB={C1CmdAB.Fired} (expected 1), XB={C1CmdXB.Fired} (expected 1 — lost pre-fix), C={C1CmdC.Fired} (expected 1)");

            C1CmdAB.Bus = null;
            bus.Dispose();
            pool.Clear();
            di.Dispose();
        }

        // ── C-2 ────────────────────────────────────────────────────────────────
        private static void Test_Dispose_AsyncSingleton_NoSyncOverAsyncDeadlock()
        {
            // A non-pumping SynchronizationContext: the pre-fix path
            // (DisposeAsync().AsTask().GetAwaiter().GetResult()) captured this context and
            // blocked the calling thread forever once DisposeAsync suspended. The fixed
            // path schedules disposal on a background task and returns immediately.
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSyncContext());

            var di = new NexusDI();
            di.BindInstance<C2BlockingDisposable>(new C2BlockingDisposable());

            bool finished = false;
            var thread = new Thread(() =>
            {
                di.Dispose();
                finished = true;
            });
            thread.Start();

            bool completed = thread.Join(3000); // 3 s watchdog — pre-fix hangs past this

            if (!completed)
            {
                // The stuck thread can no longer be joined; abort via thread abort is unsafe,
                // so mark the failure and let the process continue (worker thread leaks only
                // in the FAIL case, which never happens post-fix).
            }
            SynchronizationContext.SetSynchronizationContext(previous);

            Check("C2. Dispose_AsyncSingleton_NoSyncOverAsyncDeadlock", completed && finished,
                $"Dispose completed within 3 s watchdog = {completed && finished} (pre-fix: deadlocked forever)");
        }

        private sealed class NonPumpingSyncContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state) { /* never runs continuations */ }
            public override void Send(SendOrPostCallback d, object state) { /* never runs continuations */ }
        }

        // ── T-1 ────────────────────────────────────────────────────────────────
        private static void Test_Factory_CircularDependency_ThrowsNotStackOverflow()
        {
            var di = new NexusDI();
            // Factory that resolves its own key — pre-fix this recursed until
            // StackOverflowException (an uncatchable process crash). Now the resolution
            // stack guard throws a catchable InvalidOperationException.
            di.BindFactory<T1FactoryDep>(() => di.Resolve<T1FactoryDep>());

            bool threwExpected = false;
            try
            {
                di.Resolve<T1FactoryDep>();
            }
            catch (InvalidOperationException ex)
            {
                threwExpected = ex.Message.Contains("Circular dependency", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase);
            }
            catch (StackOverflowException)
            {
                threwExpected = false; // stack overflow is NOT an acceptable outcome
            }

            Check("T1. Factory_CircularDependency_ThrowsNotStackOverflow", threwExpected,
                $"threw catchable InvalidOperationException = {threwExpected} (pre-fix: StackOverflow crash)");
        }

        // ── T-4 ────────────────────────────────────────────────────────────────
        private static async Task Test_NetworkFireAtTick_AsyncHandlers_NoMismatch()
        {
            var di = new NexusDI();
            di.Bind<T4AsyncCmd>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            bus.RegisterCommand(typeof(T4Sig), typeof(T4AsyncCmd), ExecutionMode.Sequential, 0, isAsync: true);

            var netBus = new NetworkSignalBus(bus);
            netBus.SetTick(5);

            bool threw = false;
            try
            {
                // Pre-fix: _localSignalBus.Fire() threw NexusSyncAsyncMismatchException
                // because the local bus has an async handler for T4Sig.
                netBus.FireAtTick(new T4Sig { V = 1 }, 5);
                await Task.Delay(50); // let the fire-and-forget async dispatch settle
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine($"[Nexus Benchmark]   T4 unexpected: {ex.GetType().Name}: {ex.Message}");
            }

            Check("T4. NetworkFireAtTick_AsyncHandlers_NoMismatch", !threw,
                $"FireAtTick with async handler threw = {threw} (pre-fix: NexusSyncAsyncMismatchException)");
        }

        // ── T-5 ────────────────────────────────────────────────────────────────
        private static void Test_ErrorCollection_ThrowingSubscriber_Isolated()
        {
            ErrorCollection.Clear();

            Action<ErrorCollection.ErrorEntry> throwing = _ => throw new InvalidOperationException("t5-subscriber-boom");
            bool secondCalled = false;
            Action<ErrorCollection.ErrorEntry> second = _ => secondCalled = true;

            ErrorCollection.OnErrorAdded += throwing;
            ErrorCollection.OnErrorAdded += second;

            bool threw = false;
            try
            {
                // Pre-fix: the throwing subscriber propagated out of Collect() and broke the
                // error-collection path; the second subscriber never ran.
                ErrorCollection.CollectException(new InvalidOperationException("t5-original"));
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine($"[Nexus Benchmark]   T5 unexpected: {ex.GetType().Name}: {ex.Message}");
            }

            ErrorCollection.OnErrorAdded -= throwing;
            ErrorCollection.OnErrorAdded -= second;

            Check("T5. ErrorCollection_ThrowingSubscriber_Isolated", !threw && secondCalled,
                $"propagated={threw}, second subscriber ran={secondCalled} (pre-fix: propagated + skipped others)");
        }

        // ── T-6 ────────────────────────────────────────────────────────────────
        private static async Task Test_Recovery_RejectedFallback_FiresFailedSignal()
        {
            var strategy = new TestRecoveryStrategy
            {
                // Async-only fallback from a SYNC context → rejected by IsSyncCapableFallbackType.
                DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(T6InvalidFallback), 0)
            };

            var counter = new FailCounter();
            var di = new NexusDI();
            di.BindInstance(counter);
            di.Bind<T6ThrowingCmd>(isSingleton: false);
            di.Bind<T6InvalidFallback>(isSingleton: false);
            di.BindInstance<IRecoveryStrategy>(strategy);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            bus.RegisterCommand(typeof(T6Sig), typeof(T6ThrowingCmd), ExecutionMode.Sequential, 0, false);

            bool failedSignalSeen = false;
            var sub = bus.Subscribe<CommandFailedSignal>(_ => failedSignalSeen = true);

            bool threw = false;
            try
            {
                bus.Fire(new T6Sig { V = 1 });
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine($"[Nexus Benchmark]   T6 unexpected: {ex.GetType().Name}: {ex.Message}");
            }

            // The failed-signal dispatch is fire-and-forget; poll briefly for it.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!failedSignalSeen && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            sub.Dispose();
            bus.Dispose();
            pool.Clear();
            di.Dispose();

            // Pre-fix: the rejected fallback produced a Fallback plan with a NULL type, and
            // ExecuteSyncPlan returned RecoveryAction.Fallback WITHOUT firing the failed
            // signal — the error was logged then silently dropped.
            Check("T6. Recovery_RejectedFallback_FiresFailedSignal", !threw && failedSignalSeen,
                $"threw={threw}, CommandFailedSignal dispatched={failedSignalSeen} (pre-fix: silently dropped)");
        }

        // ── T-7 ────────────────────────────────────────────────────────────────
        private static void Test_CrossContext_AsyncTarget_NoMismatch()
        {
            var rootDi = new NexusDI();
            var childDi = new NexusDI();
            var rootCtx = new MockContext();
            var childCtx = new MockContext();

            var contexts = new System.Collections.Generic.List<IContext> { rootCtx, childCtx };
            var resolver = new ListContextResolver(contexts);

            var rootPool = new CommandPoolManager(rootDi);
            var childPool = new CommandPoolManager(childDi);
            var rootBus = new SignalBus(rootDi, rootPool, rootCtx, resolver);
            var childBus = new SignalBus(childDi, childPool, childCtx, resolver);
            rootCtx.SignalBus = rootBus;
            childCtx.SignalBus = childBus;

            // The TARGET (child) bus has an async handler for T7Sig.
            childDi.Bind<T7AsyncCmd>(isSingleton: false);
            childBus.RegisterCommand(typeof(T7Sig), typeof(T7AsyncCmd), ExecutionMode.Sequential, 0, isAsync: true);

            bool threw = false;
            try
            {
                // Pre-fix: BroadcastCrossContext called the child's sync FireCrossContext,
                // which threw NexusSyncAsyncMismatchException because the child has async
                // handlers — breaking the ROOT's dispatch.
                rootBus.Fire(new T7Sig { V = 1 });
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine($"[Nexus Benchmark]   T7 unexpected: {ex.GetType().Name}: {ex.Message}");
            }

            Check("T7. CrossContext_AsyncTarget_NoMismatch", !threw,
                $"broadcast to async-handler target threw = {threw} (pre-fix: NexusSyncAsyncMismatchException)");

            rootBus.Dispose();
            childBus.Dispose();
            rootPool.Clear();
            childPool.Clear();
            rootDi.Dispose();
            childDi.Dispose();
        }

        // ── M-1 ────────────────────────────────────────────────────────────────
        private static void Test_SaveThrottler_FailureBackoff_And_RetryCap()
        {
            var time = new AuditFakeTimeProvider { Now = 0f };
            var throttler = new SaveThrottler(null, TimeSpan.FromSeconds(1)) { TimeProvider = time };

            int attempts = 0;
            throttler.TryRequestSave(() =>
            {
                attempts++;
                throw new System.IO.IOException("disk full");
            });

            // First request: seconds-since-last-save = 999 >= 1 → flush → attempt 1 fails.
            bool firstAttempted = attempts == 1;

            // Immediately re-requesting must NOT retry (pre-fix: _lastSaveTime was not
            // updated on failure, so the throttle window was never armed → every request
            // flushed again in a tight retry loop).
            throttler.TryRequestSave(() =>
            {
                attempts++;
                throw new System.IO.IOException("disk full");
            });
            bool noTightRetry = attempts == 1;

            // Advance time past the throttle window and tick → second attempt.
            time.Now = 2f;
            throttler.Tick(0.016f);
            bool secondAttempted = attempts == 2;

            // Exhaust the retry cap: 4 more failures → pending flag cleared, no more retries.
            for (int i = 3; i <= 5; i++)
            {
                time.Now = i;
                throttler.Tick(0.016f);
            }
            bool capped = attempts == 5;

            // After the cap, further ticks must NOT retry.
            time.Now = 99f;
            throttler.Tick(0.016f);
            bool noRetryAfterCap = attempts == 5;

            Check("M1. SaveThrottler_FailureBackoff_And_RetryCap",
                firstAttempted && noTightRetry && secondAttempted && capped && noRetryAfterCap,
                $"attempts={attempts} (expected 5: 1 initial + 4 throttled retries), first={firstAttempted}, noTightRetry={noTightRetry}, second={secondAttempted}, capped={capped}, noRetryAfterCap={noRetryAfterCap}");
        }

        // ── M-4 ────────────────────────────────────────────────────────────────
        private static void Test_ObservableList_Reentrant_Notifications_Queued()
        {
            var list = new ObservableList<int>();
            int addedEvents = 0;

            // Handler adds ANOTHER item while the first notification is being dispatched —
            // a reentrant mutation. Pre-fix: the nested Add's notification was silently
            // dropped (addedEvents stayed 1 even though the list had 2 items).
            list.OnAdded((index, value) =>
            {
                addedEvents++;
                if (value == 1) list.Add(2);
            });

            list.Add(1);

            bool bothNotified = addedEvents == 2;
            bool listHasBoth = list.Contains(1) && list.Contains(2);

            Check("M4. ObservableList_Reentrant_Notifications_Queued", bothNotified && listHasBoth,
                $"added events={addedEvents} (expected 2), list contains 1&2={listHasBoth} (pre-fix: nested Add notified nobody)");
        }

        // ── M-6 ────────────────────────────────────────────────────────────────
        private static void Test_GameSaveManager_ConcurrentSaves_Serialized()
        {
            var model = new AuditSaveModel();
            var gsm = new GameSaveManager();
            gsm.RegisterModel(model);

            try
            {
                // 16 concurrent saves of the same slot. Pre-fix: all raced on the shared
                // "slot.sav.tmp" path — interleaved WriteAllText/Replace pairs produced a
                // torn file that failed to deserialize on load.
                var tasks = new Task[16];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = gsm.SaveAsync("concurrent-slot");
                }
                Task.WaitAll(tasks);

                model.Data = "audit-v2"; // if load restores v1, the file was valid
                bool loaded = gsm.LoadAsync("concurrent-slot").GetAwaiter().GetResult() && model.Data == "audit-v1";
                gsm.DeleteSave("concurrent-slot");
                bool deleted = !gsm.SaveExists("concurrent-slot");

                Check("M6. GameSaveManager_ConcurrentSaves_Serialized", loaded && deleted,
                    $"loaded intact save={loaded}, deleted={deleted} (pre-fix: torn temp file → load failed)");
            }
            finally
            {
                try
                {
                    gsm.DeleteSave("concurrent-slot");
                    System.IO.Directory.Delete(System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "saves"), true);
                }
                catch { /* cleanup best-effort */ }
            }
        }

        // ── M-7 (PerformanceMonitor) ───────────────────────────────────────────
        private static void Test_PerfMonitor_ThrowingSubscriber_Isolated()
        {
            bool firstCalled = false;
            Action<PerformanceMonitor.MetricSample> throwing = _ => throw new InvalidOperationException("m7-perf-boom");
            Action<PerformanceMonitor.MetricSample> first = _ => firstCalled = true;

            PerformanceMonitor.OnMetricRecorded += throwing;
            PerformanceMonitor.OnMetricRecorded += first;

            PerformanceMonitor.StartRecording();
            bool threw = false;
            try
            {
                // Pre-fix: the throwing subscriber propagated out of RecordMetric (the
                // frame/GC metrics hot path) and the recording state was left inconsistent.
                PerformanceMonitor.RecordMetric("m7", 1);
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine($"[Nexus Benchmark]   M7 perf unexpected: {ex.GetType().Name}: {ex.Message}");
            }
            PerformanceMonitor.StopRecording();

            PerformanceMonitor.OnMetricRecorded -= throwing;
            PerformanceMonitor.OnMetricRecorded -= first;
            PerformanceMonitor.ClearMetric("m7");

            Check("M7. PerfMonitor_ThrowingSubscriber_Isolated", !threw && firstCalled,
                $"propagated={threw}, other subscriber ran={firstCalled} (pre-fix: propagated)");
        }

        // ── M-7 (NetworkMonitor) ───────────────────────────────────────────────
        private static void Test_NetworkMonitor_ThrowingSubscriber_Isolated()
        {
            bool secondCalled = false;
            Action<NetworkMonitor.NetworkEvent> throwing = _ => throw new InvalidOperationException("m7-net-boom");
            Action<NetworkMonitor.NetworkEvent> second = _ => secondCalled = true;

            NetworkMonitor.OnNetworkEvent += throwing;
            NetworkMonitor.OnNetworkEvent += second;

            bool threw = false;
            try
            {
                NetworkMonitor.RecordSignalSent("m7-signal");
            }
            catch (Exception ex)
            {
                threw = true;
                Console.WriteLine($"[Nexus Benchmark]   M7 net unexpected: {ex.GetType().Name}: {ex.Message}");
            }

            NetworkMonitor.OnNetworkEvent -= throwing;
            NetworkMonitor.OnNetworkEvent -= second;
            NetworkMonitor.ClearHistory();

            Check("M7. NetworkMonitor_ThrowingSubscriber_Isolated", !threw && secondCalled,
                $"propagated={threw}, other subscriber ran={secondCalled} (pre-fix: propagated)");
        }

        // ── M-3 (ObjectPoolService bounded retention) ──────────────────────────
        private static void Test_ObjectPoolService_BoundRetention()
        {
            var pool = new ObjectPoolService();
            var prefab = new GameObject("audit-prefab");
            prefab.AddComponent<AuditPoolable>();

            // Despawn far more instances than the pool cap — inactive retention must be
            // bounded (excess instances destroyed), so pooled-object count cannot grow
            // without limit.
            const int spawnCount = 512; // cap is 128
            var spawned = new System.Collections.Generic.List<GameObject>(spawnCount);
            for (int i = 0; i < spawnCount; i++)
            {
                spawned.Add(pool.Spawn(prefab));
            }
            foreach (var go in spawned)
            {
                pool.Despawn(go);
            }

            // Probe the inactive stack size via reflection (the pool keeps it private).
            int inactiveCount = 0;
            var poolsField = typeof(ObjectPoolService).GetField("_poolsByPrefabId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (poolsField?.GetValue(pool) is System.Collections.IDictionary dict && dict.Count > 0)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var inactiveProp = entry.Value?.GetType().GetProperty("Inactive");
                    if (inactiveProp?.GetValue(entry.Value) is System.Collections.ICollection coll)
                        inactiveCount += coll.Count;
                }
            }

            pool.Dispose();
            UnityEngine.Object.Destroy(prefab);

            Check("M3. ObjectPoolService_BoundRetention", inactiveCount > 0 && inactiveCount <= 128,
                $"retained inactive={inactiveCount} after {spawnCount} despawns (cap 128, pre-fix: unbounded = {spawnCount})");
        }
    }
}
