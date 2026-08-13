// Repro for the Unity PlayMode full-suite hang: PerformanceTests runs first, then the
// 13 RecoveryTests in alphabetical order; the runner hangs inside
// Recovery_Skip_SuppressesExceptionAndPublishesCommandFailedSignal (the LAST test of the
// fixture). The same RecoveryTests fixture passes in isolation, so the trigger is the
// PerformanceTests state left behind. This suite replays the EXACT sequence on a
// watchdog thread; a hang becomes a bounded FAIL instead of a frozen process.
//
// NOTE: compiled WITHOUT NEXUS_DEBUG (matching the benchmark build), so the causal
// tracer is compiled out — if the hang only reproduces with the tracer active, this
// suite passes and the difference tells us the hang lives in the NEXUS_DEBUG path.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    // ─── RecoveryTests command/signal surface (mirrors Tests/Runtime/RecoveryTests.cs) ───
    public struct RFSignal
    {
        public readonly string Message;
        public RFSignal(string message) => Message = message;
    }

    public class RFResults
    {
        public int ThrowCount;
        public int FallbackCount;
        public string FallbackMessage;
        public int AsyncFallbackCount;
        public string AsyncFallbackMessage;
    }

    public class RFThrowCommand : ICommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public void Execute(RFSignal signal)
        {
            Results.ThrowCount++;
            throw new InvalidOperationException("Command failed intendedly: " + signal.Message);
        }
    }

    public class RFFallbackCommand : ICommand, ICommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public RFSignal Signal;
        public void Execute() => Execute(Signal);
        public void Execute(RFSignal signal)
        {
            Results.FallbackCount++;
            Results.FallbackMessage = signal.Message;
        }
    }

    public class RFAsyncFallbackCommand : IAsyncCommand, IAsyncCommand<RFSignal>, ICommand, ICommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public RFSignal Signal;
        public void Execute() => Execute(Signal);
        public void Execute(RFSignal signal)
        {
            Results.AsyncFallbackCount++;
            Results.AsyncFallbackMessage = signal.Message;
        }
        public ValueTask ExecuteAsync(CancellationToken ct) => ExecuteAsync(Signal, ct);
        public ValueTask ExecuteAsync(RFSignal signal, CancellationToken ct)
        {
            Execute(signal);
            return default;
        }
    }

    public class RFGenericOnlyFallbackCommand : ICommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public void Execute(RFSignal signal)
        {
            Results.FallbackCount++;
            Results.FallbackMessage = signal.Message;
        }
    }

    public class RFGenericOnlyAsyncFallbackCommand : IAsyncCommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public ValueTask ExecuteAsync(RFSignal signal, CancellationToken ct)
        {
            Results.AsyncFallbackCount++;
            Results.AsyncFallbackMessage = signal.Message;
            return default;
        }
    }

    public class RFAsyncThrowCommand : IAsyncCommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public ValueTask ExecuteAsync(RFSignal signal, CancellationToken ct)
        {
            Results.ThrowCount++;
            throw new InvalidOperationException("Async command failed intendedly: " + signal.Message);
        }
    }

    [CommandTimeout(50)]
    public class RFHangingAsyncCommand : IAsyncCommand<RFSignal>
    {
        [Inject] public RFResults Results;
        public async ValueTask ExecuteAsync(RFSignal signal, CancellationToken ct)
        {
            Results.ThrowCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    public class RFCustomRecoveryStrategy : IRecoveryStrategy
    {
        public Func<CommandFailureContext, RecoveryDecision> DecisionFactory;
        public RecoveryDecision OnCommandFailed(CommandFailureContext failure)
            => DecisionFactory != null ? DecisionFactory(failure) : RecoveryDecision.Skip();
    }

    public static class PlayModeHangRepro
    {
        private static int s_failures;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("PlayModeHangRepro", name, ok, detail);
            if (!ok) s_failures++;
        }

        // ─── Phase 1: PerformanceTests sequence (fresh bus per test, exact op counts) ───
        private static void PerfPhase()
        {
            // Dispatch1000Signals_CompletesUnderTime
            using (var b = NewPerfBus(out var counter))
            {
                for (int i = 0; i < 1000; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_Dispatch1000", counter.Value == 1000, $"counter={counter.Value}");
            }

            // Subscribe1000AndFire_AllReceived
            using (var b = NewPerfBus(out var counter2))
            {
                int received = 0;
                b.Bus.Subscribe<PerfSignal>(sig => received++);
                for (int i = 0; i < 1000; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_Subscribe1000", received == 1000, $"received={received}");
            }

            // CommandPool_ReusesInstances
            using (var b = NewPerfBus(out var counter3))
            {
                b.Bus.Fire(new PerfSignal(1));
                b.Bus.Fire(new PerfSignal(2));
                for (int i = 0; i < 100; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_PoolReuse", counter3.Value == 102, $"counter={counter3.Value}");
            }

            // CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate (GC pressure)
            using (var b = NewPerfBus(out _))
            {
                var mgr = new CommandPoolManager(b.Container);
                var cmdType = typeof(PerfCommand);
                for (int i = 0; i < 100; i++)
                {
                    var cmd = mgr.GetCommand(cmdType);
                    mgr.ReturnCommand(cmdType, cmd);
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = mgr.GetCommand(cmdType);
                    mgr.ReturnCommand(cmdType, cmd);
                }
                Report("P1_PoolGetReturn", true, "5000 get/return + GC done");
            }

            // SteadyState_HasZeroGCAllocations (GC.Collect x3 on the hot path)
            using (var b = NewPerfBus(out _))
            {
                for (int i = 0; i < 100; i++) b.Bus.Fire(new PerfSignal(i));
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                for (int i = 0; i < 5000; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_SteadyState", true, "GC x3 + 5000 fires done");
            }

            // HighFrequency_Performance_StressTest (50k)
            using (var b = NewPerfBus(out _))
            {
                for (int i = 0; i < 100; i++) b.Bus.Fire(new PerfSignal(i));
                for (int i = 0; i < 50000; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_HighFrequency", true, "50,000 fires done");
            }

            // Benchmark_SignalFire_HotPathNs (20k)
            using (var b = NewPerfBus(out _))
            {
                for (int i = 0; i < 2000; i++) b.Bus.Fire(new PerfSignal(i));
                for (int i = 0; i < 20000; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_HotPath", true, "20k fires done");
            }

            // Benchmark_SignalFire_WithSubscriberNs (20k)
            using (var b = NewPerfBus(out _))
            {
                b.Bus.Subscribe<PerfSignal>(_ => { });
                for (int i = 0; i < 2000; i++) b.Bus.Fire(new PerfSignal(i));
                for (int i = 0; i < 20000; i++) b.Bus.Fire(new PerfSignal(i));
                Report("P1_HotPathSubscriber", true, "20k fires w/ subscriber done");
            }
        }

        private sealed class PerfBus : IDisposable
        {
            public NexusDI Container;
            public CommandPoolManager Pool;
            public SignalBus Bus;
            public void Dispose()
            {
                Bus.Dispose();
                Pool.Clear();
                Container.Dispose();
            }
        }

        private static PerfBus NewPerfBus(out TestCounter counter)
        {
            counter = new TestCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<PerfCommand>(isSingleton: false);
            var pool = new CommandPoolManager(container);
            var bus = new SignalBus(container, pool, new MockContext());
            bus.RegisterCommand(typeof(PerfSignal), typeof(PerfCommand), ExecutionMode.Sequential, 0, false);
            return new PerfBus { Container = container, Pool = pool, Bus = bus };
        }

        // ─── Phase 2: RecoveryTests fixture (13 tests, alphabetical, fresh bus per test) ───
        private static void RecoveryPhase()
        {
            Recovery_CommandTimeout();
            Recovery_ContextDataDefaults();
            Recovery_CreatePureContext_Disposes().GetAwaiter().GetResult();
            Recovery_CreatePureContext_Registers().GetAwaiter().GetResult();
            Recovery_TestContextDispose();
            Recovery_Abort();
            Recovery_Fallback_AsyncOnlyRejected();
            Recovery_Fallback_Executes();
            Recovery_Fallback_GenericOnly();
            Recovery_FallbackAsync_Executes().GetAwaiter().GetResult();
            Recovery_FallbackAsync_GenericOnly().GetAwaiter().GetResult();
            Recovery_Retry();
            Recovery_Skip(); // ← the Unity hang point
        }

        private static (NexusDI Container, CommandPoolManager Pool, SignalBus Bus, RFCustomRecoveryStrategy Strategy, RFResults Results) NewRecoveryBus()
        {
            var results = new RFResults();
            var container = new NexusDI();
            var pool = new CommandPoolManager(container);
            var bus = new SignalBus(container, pool, new MockContext());
            var strategy = new RFCustomRecoveryStrategy();
            container.BindInstance<IRecoveryStrategy>(strategy);
            container.BindInstance(results);
            container.Bind<RFFallbackCommand>(isSingleton: false);
            container.Bind<RFAsyncFallbackCommand>(isSingleton: false);
            container.Bind<RFGenericOnlyFallbackCommand>(isSingleton: false);
            container.Bind<RFGenericOnlyAsyncFallbackCommand>(isSingleton: false);
            container.Bind<RFAsyncThrowCommand>(isSingleton: false);
            container.Bind<RFHangingAsyncCommand>(isSingleton: false);
            return (container, pool, bus, strategy, results);
        }

        private static void Recovery_CommandTimeout()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFHangingAsyncCommand), ExecutionMode.Sequential, 0, true);
                strategy.DecisionFactory = ctx => RecoveryDecision.Retry(10);
                var fireTask = bus.FireAsync(new RFSignal("Timeout")).AsTask();
                var completed = Task.WhenAny(fireTask, Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
                bool ok = ReferenceEquals(fireTask, completed) && results.ThrowCount == 1;
                if (ok)
                {
                    try { fireTask.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { ok = true; }
                    catch (Exception) { ok = false; }
                }
                Report("R1_CommandTimeout", ok, $"completedPromptly={ReferenceEquals(fireTask, completed)} throwCount={results.ThrowCount}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static void Recovery_ContextDataDefaults()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            bool ok = data.EnableAutoDiscovery && data.CommandPoolInitialSize == 4 && data.CommandPoolMaxSize == 64;
            UnityEngine.Object.DestroyImmediate(data);
            Report("R2_ContextDataDefaults", ok, $"init={data.CommandPoolInitialSize} max={data.CommandPoolMaxSize}");
        }

        private static async Task Recovery_CreatePureContext_Disposes()
        {
            bool ok = false;
            try
            {
                var context = await NexusRuntime.CreatePureContextAsync("RegistryRefreshScope", new[] { "Assembly-CSharp" });
                context.Dispose();
                NexusRuntime.Reset();
                ok = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Nexus Benchmark] NOTE R3_CreatePureContext_Disposes threw {ex.GetType().Name}: {ex.Message} (sequence continues)");
            }
            Report("R3_CreatePureContext_Disposes", ok, "pure context create+dispose+reset");
        }

        private static async Task Recovery_CreatePureContext_Registers()
        {
            bool ok = false;
            try
            {
                var context = await NexusRuntime.CreatePureContextAsync("ReusableScope", new[] { "Assembly-CSharp" });
                ok = context != null && NexusRuntime.GetContext("ReusableScope") == context;
                context?.Dispose();
                NexusRuntime.Reset();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Nexus Benchmark] NOTE R4_CreatePureContext_Registers threw {ex.GetType().Name}: {ex.Message} (sequence continues)");
            }
            Report("R4_CreatePureContext_Registers", ok, "pure context register+dispose+reset");
        }

        private static void Recovery_TestContextDispose()
        {
            bool ok = false;
            try
            {
                var testContext = NexusTestHarness.CreateContext("HarnessScope");
                ok = testContext != null && testContext.Context != null && testContext.Context.ScopeTag == "HarnessScope";
                testContext.Dispose();
                NexusRuntime.Reset();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Nexus Benchmark] NOTE R5_TestContextDispose threw {ex.GetType().Name}: {ex.Message} (sequence continues)");
            }
            Report("R5_TestContextDispose", ok, "harness context dispose+reset");
        }

        private static void Recovery_Abort()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => RecoveryDecision.Abort();
                bool threw = false;
                try { bus.Fire(new RFSignal("AbortTest")); }
                catch { threw = true; }
                Report("R6_Abort", threw && results.ThrowCount == 1, $"threw={threw} throwCount={results.ThrowCount}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static void Recovery_Fallback_AsyncOnlyRejected()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(RFGenericOnlyAsyncFallbackCommand), 0);
                bool threw = false;
                try { bus.Fire(new RFSignal("SyncContextAsyncFallback")); }
                catch { threw = true; }
                Report("R7_Fallback_AsyncOnlyRejected", !threw && results.ThrowCount == 1 && results.AsyncFallbackCount == 0,
                    $"threw={threw} throwCount={results.ThrowCount} asyncFallback={results.AsyncFallbackCount}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static void Recovery_Fallback_Executes()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => RecoveryDecision.Fallback<RFFallbackCommand>();
                bus.Fire(new RFSignal("FallbackTest"));
                Report("R8_Fallback_Executes", results.ThrowCount == 1 && results.FallbackCount == 1 && results.FallbackMessage == "FallbackTest",
                    $"throw={results.ThrowCount} fb={results.FallbackCount} msg={results.FallbackMessage}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static void Recovery_Fallback_GenericOnly()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(RFGenericOnlyFallbackCommand), 0);
                bus.Fire(new RFSignal("GenericFallbackTest"));
                Report("R9_Fallback_GenericOnly", results.ThrowCount == 1 && results.FallbackCount == 1 && results.FallbackMessage == "GenericFallbackTest",
                    $"throw={results.ThrowCount} fb={results.FallbackCount} msg={results.FallbackMessage}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static async Task Recovery_FallbackAsync_Executes()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => RecoveryDecision.FallbackAsync<RFAsyncFallbackCommand>();
                await bus.FireAsync(new RFSignal("FallbackAsyncTest"));
                Report("R10_FallbackAsync_Executes", results.ThrowCount == 1 && results.AsyncFallbackCount == 1 && results.AsyncFallbackMessage == "FallbackAsyncTest",
                    $"throw={results.ThrowCount} afb={results.AsyncFallbackCount} msg={results.AsyncFallbackMessage}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static async Task Recovery_FallbackAsync_GenericOnly()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFAsyncThrowCommand), ExecutionMode.Sequential, 0, true);
                strategy.DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(RFGenericOnlyAsyncFallbackCommand), 0);
                await bus.FireAsync(new RFSignal("GenericAsyncFallbackTest"));
                Report("R11_FallbackAsync_GenericOnly", results.ThrowCount == 1 && results.AsyncFallbackCount == 1 && results.AsyncFallbackMessage == "GenericAsyncFallbackTest",
                    $"throw={results.ThrowCount} afb={results.AsyncFallbackCount} msg={results.AsyncFallbackMessage}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static void Recovery_Retry()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => RecoveryDecision.Retry(3);
                bool threw = false;
                try { bus.Fire(new RFSignal("RetryTest")); }
                catch { threw = true; }
                Report("R12_Retry", threw && results.ThrowCount == 4, $"threw={threw} throwCount={results.ThrowCount} (expect 4)");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        private static void Recovery_Skip()
        {
            var (c, p, bus, strategy, results) = NewRecoveryBus();
            try
            {
                bus.RegisterCommand(typeof(RFSignal), typeof(RFThrowCommand), ExecutionMode.Sequential, 0, false);
                strategy.DecisionFactory = ctx => RecoveryDecision.Skip();

                CommandFailedSignal? caught = null;
                bus.Subscribe<CommandFailedSignal>(sig => caught = sig);

                bool threw = false;
                try { bus.Fire(new RFSignal("SkipTest")); }
                catch { threw = true; }

                bool ok = !threw && results.ThrowCount == 1 && caught.HasValue
                    && caught.Value.SourceCommand == typeof(RFThrowCommand)
                    && caught.Value.SourceSignal is RFSignal rs && rs.Message == "SkipTest";
                Report("R13_Skip", ok,
                    $"threw={threw} throwCount={results.ThrowCount} caught={caught.HasValue} sourceCmd={caught.GetValueOrDefault().SourceCommand?.Name} message={(caught.GetValueOrDefault().SourceSignal is RFSignal m ? m.Message : "?")}");
            }
            finally
            {
                bus.Dispose(); p.Clear(); c.Dispose();
            }
        }

        // ─── Watchdog driver ───
        public static int Run()
        {
            s_failures = 0;
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === PlayModeHangRepro (full-suite sequence replay) ===");

            bool completed = false;
            var worker = new Thread(() =>
            {
                try
                {
                    PerfPhase();
                    Console.WriteLine("[Nexus Benchmark] Phase 1 (PerformanceTests) replay done.");
                    RecoveryPhase();
                    Console.WriteLine("[Nexus Benchmark] Phase 2 (RecoveryTests) replay done.");
                    completed = true;
                }
                catch (Exception ex)
                {
                    Report("Sequence", false, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                    completed = true;
                }
            })
            { IsBackground = true };

            worker.Start();

            const int timeoutMs = 60_000;
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!completed && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
            }

            if (!completed)
            {
                Report("FullSequence", false,
                    $"HANG REPRODUCED: the PerformanceTests → RecoveryTests sequence did not complete within {timeoutMs / 1000}s — the worker is still running (matches the Unity PlayMode freeze at Recovery_Skip).");
            }
            else
            {
                Report("FullSequence", s_failures == 0,
                    $"sequence completed ({(s_failures == 0 ? "all checks passed" : $"{s_failures} check(s) failed, but no hang")}).");
            }

            return s_failures;
        }
    }
}
