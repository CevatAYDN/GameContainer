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
//   M-4b ObservableList concurrent multi-thread reentrant drains stay isolated (per-thread buffer)
//   M-6 GameSaveManager concurrent SaveAsync serialized (no torn temp writes)
//   M-7 PerformanceMonitor / NetworkMonitor throwing subscribers isolated
// External audit of 2026-10-25 (issue #23; ids are the report's):
//   A23-O2 int/long saves survive a locale change (NegativeSign U+2212)
//   A23-O3 a transient read error is not cached as "no value"
//   A23-O4 a second Despawn does not destroy the pooled instance
//   A23-M1 a singleton built while the container is disposed is disposed
//   A23-M5 a failed one-shot composite surfaces and can fire again
//   A23-M8 two constructor overrides of one type fill their parameters in order
// Follow-up issues:
//   I24 integer saves written before #23 with another culture's minus sign read back
//   I26 a convention-found lifecycle that is IDisposable gets one teardown (OnDispose)
//   I28 a failed async one-shot composite surfaces (FireAsync) or is released (Fire), and fires again
//   I29 ReInject runs [Inject] methods outside its lock (another thread's ReInject does not deadlock)
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

    // ── #23 types ─────────────────────────────────────────────────────────────
    public struct A23SigA { }
    public struct A23SigB { }

    /// <summary>One-shot composite whose first run throws (default recovery: Skip).</summary>
    public sealed class A23OnceCompositeCmd : ICompositeCommand
    {
        public static int Attempts;
        public void Execute(CompositeContext context)
        {
            if (Attempts++ == 0) throw new InvalidOperationException("a23-m5-composite-failure");
        }
    }

    /// <summary>Disposes its container from its constructor: the publish step then finds the container disposed.</summary>
    public sealed class A23OrphanSingleton : IDisposable
    {
        public static NexusDI Container;
        public static bool Disposed;
        public A23OrphanSingleton() { Container?.Dispose(); }
        public void Dispose() => Disposed = true;
    }

    public sealed class A23TwoStrings
    {
        public readonly string First;
        public readonly string Second;
        public A23TwoStrings(string first, string second) { First = first; Second = second; }
    }

    public struct I28SigA { }
    public struct I28SigB { }
    public struct I28SigC { }
    public struct I28SigD { }

    /// <summary>Async one-shot composite whose first run fails (default recovery: Skip); fired with FireAsync (issue #28).</summary>
    public sealed class I28AwaitedCompositeCmd : IAsyncCompositeCommand
    {
        public static int Attempts;
        public async ValueTask ExecuteAsync(CompositeContext signals, CancellationToken ct)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref Attempts) == 1) throw new InvalidOperationException("i28-awaited-failure");
        }
    }

    /// <summary>The same, fired with Fire: the command runs detached (issue #28).</summary>
    public sealed class I28DetachedCompositeCmd : IAsyncCompositeCommand
    {
        public static int Attempts;
        public async ValueTask ExecuteAsync(CompositeContext signals, CancellationToken ct)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref Attempts) == 1) throw new InvalidOperationException("i28-detached-failure");
        }
    }

    public interface I29Missing { }
    public sealed class I29MissingImpl : I29Missing { }
    public sealed class I29Target { [Inject] public I29Missing Dep; }

    /// <summary>Its [Inject] method waits for another thread's ReInject (issue #29): a deadlock while ReInject held its lock.</summary>
    public sealed class I29Caller
    {
        public static NexusDI Di;
        public static I29Target Other;
        public static bool InnerFinished;
        [Inject]
        public void Init(I29Missing dep)
        {
            var inner = Task.Run(() => Di.ReInject(Other));
            InnerFinished = inner.Wait(2000);
        }
    }

    /// <summary>Found by name convention for scope tag "I26Convention"; counts both teardown paths (issue #26).</summary>
    public sealed class I26ConventionLifecycle : IContextLifecycle, IDisposable
    {
        public static int OnDisposeCalls;
        public static int DisposeCalls;
        public void OnConfigure(IContextBuilder builder) { }
        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() => OnDisposeCalls++;
        public void Dispose() => DisposeCalls++;
    }

    public static class AuditFixRegression
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === Audit-fix regression (C-1/C-2/T-1/T-4/T-5/T-6/T-7/M-1/M-3/M-4/M-4b/M-6/M-7, #23 O2/O3/O4/M1/M5/M8) ===");
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
            Test_ObservableList_ConcurrentReentrant_DrainIsolation();
            Test_GameSaveManager_ConcurrentSaves_Serialized();
            Test_PerfMonitor_ThrowingSubscriber_Isolated();
            Test_NetworkMonitor_ThrowingSubscriber_Isolated();
            Test_ObjectPoolService_BoundRetention();
            Test_A23_IntegerSaves_SurviveLocaleChange();
            Test_A23_TransientReadError_NotCachedAsMissing();
            Test_A23_DoubleDespawn_KeepsPooledInstance();
            Test_A23_SingletonBuiltDuringDispose_IsDisposed();
            Test_A23_FailedOneShotComposite_SurfacesAndFiresAgain();
            Test_A23_SameTypeOverrides_FillInOrder();
            Test_I24_LegacyMinusSigns_ReadBack();
            Test_I26_ConventionLifecycle_TornDownOnce();
            Test_I28_FailedAsyncOneShotComposite_FiresAgain();
            Test_I29_ReInject_RunsUserCodeOutsideItsLock();

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

        // ── M-4b ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Multi-threaded reentrant-drain stress. ObservableList supports concurrent
        /// mutation from multiple threads; the claim protocol must guarantee exactly
        /// ONE thread owns the dispatch unit (direct mutation + every drain pass) — the
        /// _isNotifying flag is claimed under _eventLock and released only when the
        /// pending queue is empty, in the same lock scope. A regression that lets one
        /// dispatcher's teardown clear ANOTHER dispatcher's in-flight claim lets
        /// reentrant mutations claim directly, start nested drains, and silently lose or
        /// double-deliver notifications (observed 2026-08-06: blocks of values notified
        /// 0 and 2 times). Phase 1 hammers concurrent Add + handler-reentrant Add;
        /// phase 2 hammers concurrent indexer-set Replace + handler-reentrant Replace
        /// (the indexer was the last path still clearing the flag in its own finally).
        /// Every value must be notified EXACTLY once — the per-index count check is the
        /// wrong-pair/dropped detector (a value dispatched in place of another, or
        /// missed, shows up as a count mismatch). NOTE: this is a stress test with HIGH
        /// (not deterministic) detection probability — it failed 5/5 against the
        /// pre-fix shared-buffer variant and ~3/4 against the pre-fix flag-overwrite
        /// protocol; a pass does not prove a future regression variant cannot exist.
        /// </summary>
        private static void Test_ObservableList_ConcurrentReentrant_DrainIsolation()
        {
            const int threadCount = 8;
            const int addsPerThread = 4000;
            int domain = threadCount * addsPerThread; // primary values [0, domain)
            int[] counts = new int[domain * 2 + 16]; // primary + reentrant-sentinel domain

            var list = new ObservableList<int>();
            list.OnAdded((index, value) =>
            {
                System.Threading.Interlocked.Increment(ref counts[value]);
                // Reentrant mutation while this (or another thread's) dispatch is in
                // flight → queued under _eventLock, drained by the outer loop later.
                // Only primary values re-add, so sentinels never chain out of bounds.
                if (value < domain && value % 97 == 0)
                {
                    list.Add(domain + value);
                }
            });

            var threads = new Thread[threadCount];
            var startGate = new ManualResetEvent(false);
            var failures = new bool[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int baseId = t * addsPerThread;
                int idx = t;
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        startGate.WaitOne();
                        for (int i = 0; i < addsPerThread; i++)
                        {
                            list.Add(baseId + i);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures[idx] = true;
                        Console.WriteLine($"[Nexus Benchmark]   M4b worker {idx} failed: {ex.GetType().Name}: {ex.Message}");
                    }
                });
                threads[t].Start();
            }

            startGate.Set();
            bool joined = true;
            foreach (var th in threads)
            {
                if (!th.Join(30000)) joined = false; // watchdog — must never trip post-fix
            }

            // Every primary value notified exactly once (the wrong-pair/dropped detector).
            bool primariesExact = true;
            for (int v = 0; v < domain; v++)
            {
                if (counts[v] != 1) primariesExact = false;
            }

            // Every reentrant sentinel (added when v % 97 == 0) notified exactly once.
            int sentinelCount = 0;
            bool sentinelsExact = true;
            for (int v = 0; v < domain; v++)
            {
                if (v % 97 == 0)
                {
                    sentinelCount++;
                    if (counts[domain + v] != 1) sentinelsExact = false;
                }
            }

            bool sizeCorrect = list.Count == domain + sentinelCount;

            // ── Phase 2: indexer-set (Replace) stress ─────────────────────────────
            // The indexer setter was the last mutation path still using the old
            // flag-clearing pattern (it cleared _isNotifying in its own finally, before
            // the drain) — the same flag-overwrite hazard the claim protocol eliminates.
            // Same stress as phase 1 but through list[0] = v (index 0 is always valid
            // once the list is non-empty; phase 1 left 32330 items).
            int[] replaceCounts = new int[domain * 2 + 16];
            list.OnReplaced((index, oldItem, newItem) =>
            {
                System.Threading.Interlocked.Increment(ref replaceCounts[newItem]);
                // Reentrant Replace while a dispatch is in flight → queued, drained later.
                // Only primary values re-replace, so sentinels never chain out of bounds.
                if (newItem < domain && newItem % 97 == 0)
                {
                    list[list.Count - 1] = domain + newItem;
                }
            });

            startGate.Reset();
            for (int t = 0; t < threadCount; t++)
            {
                int baseId = t * addsPerThread;
                int idx = t;
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        startGate.WaitOne();
                        for (int i = 0; i < addsPerThread; i++)
                        {
                            list[0] = baseId + i;
                        }
                    }
                    catch (Exception ex)
                    {
                        failures[idx] = true;
                        Console.WriteLine($"[Nexus Benchmark]   M4b worker {idx} failed (phase 2): {ex.GetType().Name}: {ex.Message}");
                    }
                });
                threads[t].Start();
            }
            startGate.Set();
            foreach (var th in threads)
            {
                if (!th.Join(30000)) joined = false; // watchdog — must never trip post-fix
            }

            // Every primary replace notified exactly once.
            bool replacesExact = true;
            for (int v = 0; v < domain; v++)
            {
                if (replaceCounts[v] != 1) replacesExact = false;
            }
            // Every reentrant replace sentinel (added when v % 97 == 0) notified exactly once.
            bool replaceSentinelsExact = true;
            for (int v = 0; v < domain; v++)
            {
                if (v % 97 == 0 && replaceCounts[domain + v] != 1) replaceSentinelsExact = false;
            }

            bool noWorkerFailures = true;
            for (int t = 0; t < threadCount; t++) noWorkerFailures &= !failures[t];

            Check("M4b. ObservableList_ConcurrentReentrant_DrainIsolation",
                joined && primariesExact && sentinelsExact && sizeCorrect && replacesExact && replaceSentinelsExact && noWorkerFailures,
                $"threads joined={joined}, adds notified exactly once={primariesExact} (x{domain}) + sentinels={sentinelsExact} (x{sentinelCount}), " +
                $"replaces notified exactly once={replacesExact} (x{domain}) + sentinels={replaceSentinelsExact}, " +
                $"list size={list.Count} (expected {domain + sentinelCount}), worker failures={!noWorkerFailures} " +
                $"(flag-overwrite/nested-drain would drop or double-deliver)");
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

        // ── #23 ───────────────────────────────────────────────────────────────
        private static void DeleteSecureData(string salt)
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "SecureData_" + salt);
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        }

        private static void Test_A23_IntegerSaves_SurviveLocaleChange()
        {
            const string salt = "HarnessSalt_A23_O2";
            var unicodeMinus = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("en-US").Clone();
            unicodeMinus.NumberFormat.NegativeSign = "−"; // as sv-SE, nb-NO, fi-FI and others write it
            UnityEngine.PlayerPrefs.ClearAll();
            DeleteSecureData(salt);
            // Own thread: setting CurrentCulture on the harness thread would pin it and hide later suites'
            // DefaultThreadCurrentCulture (ServicesSuite sets the invariant culture that way).
            var worker = new Thread(() => RunIntegerSavesAcrossLocales(salt, unicodeMinus));
            worker.Start();
            worker.Join();
        }

        private static void RunIntegerSavesAcrossLocales(string salt, System.Globalization.CultureInfo unicodeMinus)
        {
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = unicodeMinus;
                var writer = new EncryptedStorageService(salt) { AutoSave = true };
                writer.SetLong("coins", -5);
                writer.SetInt("level", -3);
                writer.Dispose();
                var prefs = new UnityPlayerPrefsService();
                prefs.SetLong("plain", -7);

                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                var reader = new EncryptedStorageService(salt);
                long coins = reader.GetLong("coins");
                int level = reader.GetInt("level");
                long plain = prefs.GetLong("plain");
                reader.Dispose();

                Check("A23-O2. IntegerSaves_SurviveLocaleChange", coins == -5 && level == -3 && plain == -7,
                    $"coins={coins} level={level} plain={plain} (expected -5/-3/-7; pre-fix 0/0/0: \"−5\" written under one locale did not parse under another)");
            }
            catch (Exception ex)
            {
                Check("A23-O2. IntegerSaves_SurviveLocaleChange", false, "threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                DeleteSecureData(salt);
            }
        }

        private static void Test_A23_TransientReadError_NotCachedAsMissing()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("[Nexus Benchmark] SKIP  A23-O3. TransientReadError_NotCachedAsMissing: FileShare.None locks a file only on Windows");
                return;
            }
            const string salt = "HarnessSalt_A23_O3";
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "SecureData_" + salt);
            UnityEngine.PlayerPrefs.ClearAll();
            DeleteSecureData(salt);
            try
            {
                var writer = new EncryptedStorageService(salt) { AutoSave = true };
                writer.SetLong("gems", 42);
                writer.Dispose();

                var reader = new EncryptedStorageService(salt);
                long whileLocked;
                var locks = new System.Collections.Generic.List<System.IO.FileStream>();
                try
                {
                    foreach (var file in System.IO.Directory.GetFiles(dir))
                        locks.Add(new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None));
                    whileLocked = reader.GetLong("gems", -1);
                }
                finally
                {
                    foreach (var s in locks) s.Dispose();
                }
                long afterRelease = reader.GetLong("gems", -1);
                reader.Dispose();

                Check("A23-O3. TransientReadError_NotCachedAsMissing", locks.Count > 0 && whileLocked == -1 && afterRelease == 42,
                    $"locked files={locks.Count}, read while locked={whileLocked} (expected -1), after release={afterRelease} (expected 42; pre-fix -1 for the session)");
            }
            finally
            {
                DeleteSecureData(salt);
            }
        }

        private static void Test_A23_DoubleDespawn_KeepsPooledInstance()
        {
            var pool = new ObjectPoolService();
            var prefab = new GameObject("a23-prefab");
            var first = pool.Spawn(prefab);
            pool.Despawn(first);
            pool.Despawn(first); // the second call must not destroy the instance waiting in the pool
            var next = pool.Spawn(prefab);
            bool alive = next;
            bool reused = ReferenceEquals(next, first);
            pool.Dispose();
            UnityEngine.Object.Destroy(prefab);

            Check("A23-O4. DoubleDespawn_KeepsPooledInstance", alive && reused,
                $"next spawn alive={alive}, reused the pooled instance={reused} (pre-fix: the pooled instance was destroyed and handed out)");
        }

        private static void Test_A23_SingletonBuiltDuringDispose_IsDisposed()
        {
            var di = new NexusDI();
            A23OrphanSingleton.Container = di;
            A23OrphanSingleton.Disposed = false;
            di.Bind<A23OrphanSingleton>(isSingleton: true);
            string thrown = "nothing";
            try { di.Resolve<A23OrphanSingleton>(); }
            catch (Exception ex) { thrown = ex.GetType().Name; }
            A23OrphanSingleton.Container = null;

            Check("A23-M1. SingletonBuiltDuringDispose_IsDisposed", thrown == nameof(ObjectDisposedException) && A23OrphanSingleton.Disposed,
                $"resolve threw {thrown} (expected ObjectDisposedException), instance disposed={A23OrphanSingleton.Disposed} (pre-fix: false, nothing owned it)");
        }

        private static void Test_A23_FailedOneShotComposite_SurfacesAndFiresAgain()
        {
            var di = new NexusDI();
            di.Bind<A23OnceCompositeCmd>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            A23OnceCompositeCmd.Attempts = 0;
            bus.RegisterCompositeCommand(new[] { typeof(A23SigA), typeof(A23SigB) }, typeof(A23OnceCompositeCmd), oneShot: true, priority: 0, isAsync: false);

            bus.Fire(new A23SigA());
            bool threw = false;
            try { bus.Fire(new A23SigB()); }
            catch (InvalidOperationException) { threw = true; }
            bus.Fire(new A23SigA());
            bus.Fire(new A23SigB());
            int afterRetry = A23OnceCompositeCmd.Attempts;
            bus.Fire(new A23SigA());
            bus.Fire(new A23SigB());
            int afterSuccess = A23OnceCompositeCmd.Attempts;

            Check("A23-M5. FailedOneShotComposite_SurfacesAndFiresAgain", threw && afterRetry == 2 && afterSuccess == 2,
                $"failure reached Fire={threw}, runs after a second A+B={afterRetry} (expected 2), after a third={afterSuccess} (expected 2: consumed once it succeeded; pre-fix 1 run, failure swallowed)");
        }

        private static void Test_A23_SameTypeOverrides_FillInOrder()
        {
            var di = new NexusDI();
            di.BindTypedFactory<string, string, A23TwoStrings>();
            var made = di.Resolve<IFactory<string, string, A23TwoStrings>>().Create("x", "y");

            Check("A23-M8. SameTypeOverrides_FillInOrder", made.First == "x" && made.Second == "y",
                $"first={made.First} second={made.Second} (expected x/y; pre-fix x/x)");
        }

        // ── follow-up issues ──────────────────────────────────────────────────
        private static void Test_I24_LegacyMinusSigns_ReadBack()
        {
            const string salt = "HarnessSalt_I24";
            UnityEngine.PlayerPrefs.ClearAll();
            DeleteSecureData(salt);
            try
            {
                // what sv-SE (U+2212) and ar (U+061C ALM before the hyphen) wrote before the invariant format
                var storage = new EncryptedStorageService(salt);
                storage.SetString("swedish", "−5");
                storage.SetString("arabic", "؜-7");
                long swedish = storage.GetLong("swedish");
                int arabic = storage.GetInt("arabic");
                storage.Dispose();
                UnityEngine.PlayerPrefs.SetString("persian", "‎−9");
                long persian = new UnityPlayerPrefsService().GetLong("persian");

                Check("I24. LegacyMinusSigns_ReadBack", swedish == -5 && arabic == -7 && persian == -9,
                    $"U+2212={swedish} U+061C-={arabic} U+200E U+2212={persian} (expected -5/-7/-9; pre-fix 0/0/0)");
            }
            finally
            {
                DeleteSecureData(salt);
            }
        }

        private static void Test_I26_ConventionLifecycle_TornDownOnce()
        {
            I26ConventionLifecycle.OnDisposeCalls = 0;
            I26ConventionLifecycle.DisposeCalls = 0;
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = "I26Convention";
            data.EnableAutoDiscovery = true;
            var context = new Context(parent: null, contextData: data);
            context.ConfigureWithBuilder(new ContextBuilder(context.Container, context.SignalBusInternal));
            bool found = context.TryResolve<IContextLifecycle>() is I26ConventionLifecycle;
            context.Dispose();

            Check("I26. ConventionLifecycle_TornDownOnce", found && I26ConventionLifecycle.OnDisposeCalls == 1 && I26ConventionLifecycle.DisposeCalls == 0,
                $"found by convention={found}, OnDispose={I26ConventionLifecycle.OnDisposeCalls} Dispose={I26ConventionLifecycle.DisposeCalls} (expected 1/0, as an explicitly passed lifecycle; pre-fix 1/1)");
        }

        private static void Test_I28_FailedAsyncOneShotComposite_FiresAgain()
        {
            var di = new NexusDI();
            di.Bind<I28AwaitedCompositeCmd>(isSingleton: false);
            di.Bind<I28DetachedCompositeCmd>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            I28AwaitedCompositeCmd.Attempts = 0;
            I28DetachedCompositeCmd.Attempts = 0;
            bus.RegisterCompositeCommand(new[] { typeof(I28SigA), typeof(I28SigB) }, typeof(I28AwaitedCompositeCmd), oneShot: true, priority: 0, isAsync: true);
            bus.RegisterCompositeCommand(new[] { typeof(I28SigC), typeof(I28SigD) }, typeof(I28DetachedCompositeCmd), oneShot: true, priority: 0, isAsync: true);

            // awaited (FireAsync): the failure reaches the caller and the trigger fires again
            bus.FireAsync(new I28SigA()).AsTask().GetAwaiter().GetResult();
            bool awaitedThrew = false;
            try { bus.FireAsync(new I28SigB()).AsTask().GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { awaitedThrew = true; }
            bus.FireAsync(new I28SigA()).AsTask().GetAwaiter().GetResult();
            bus.FireAsync(new I28SigB()).AsTask().GetAwaiter().GetResult();
            int awaitedRuns = I28AwaitedCompositeCmd.Attempts;
            bus.FireAsync(new I28SigA()).AsTask().GetAwaiter().GetResult();
            bus.FireAsync(new I28SigB()).AsTask().GetAwaiter().GetResult();
            int awaitedAfter = I28AwaitedCompositeCmd.Attempts;

            // detached (Fire): nobody can catch the failure, so the trigger is released and fires again
            bus.Fire(new I28SigC());
            bus.Fire(new I28SigD());
            bool firstRan = SpinUntil(() => Volatile.Read(ref I28DetachedCompositeCmd.Attempts) >= 1);
            Thread.Sleep(100); // let the detached failure's recovery finish before the re-fire
            bus.Fire(new I28SigC());
            bus.Fire(new I28SigD());
            SpinUntil(() => Volatile.Read(ref I28DetachedCompositeCmd.Attempts) >= 2);
            int detachedRuns = Volatile.Read(ref I28DetachedCompositeCmd.Attempts);

            Check("I28. FailedAsyncOneShotComposite_FiresAgain",
                awaitedThrew && awaitedRuns == 2 && awaitedAfter == 2 && firstRan && detachedRuns == 2,
                $"FireAsync: failure reached caller={awaitedThrew}, runs after retry={awaitedRuns} (expected 2), after a third pair={awaitedAfter} (expected 2); " +
                $"Fire: runs after retry={detachedRuns} (expected 2; pre-fix 1 and 1, failure swallowed, trigger consumed)");
        }

        private static void Test_I29_ReInject_RunsUserCodeOutsideItsLock()
        {
            var di = new NexusDI();
            var caller = new I29Caller();
            var target = new I29Target();
            di.Inject(caller); // Init(I29Missing) waits: the dependency is not bound yet
            di.Inject(target); // Dep waits too
            di.Bind<I29Missing, I29MissingImpl>();
            I29Caller.Di = di;
            I29Caller.Other = target;
            I29Caller.InnerFinished = false;

            bool callerDone = di.ReInject(caller); // runs Init, which waits up to 2 s for ReInject(target) on another thread
            bool targetDone = target.Dep != null;
            I29Caller.Di = null;
            I29Caller.Other = null;

            Check("I29. ReInject_RunsUserCodeOutsideItsLock", I29Caller.InnerFinished && callerDone && targetDone,
                $"inner ReInject finished={I29Caller.InnerFinished} (pre-fix: blocked on the lock the outer ReInject held, 2 s timeout), caller done={callerDone}, target injected={targetDone}");
        }

        private static bool SpinUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(5);
            }
            return true;
        }
    }
}
