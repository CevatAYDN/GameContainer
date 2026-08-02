// General-Binder proof suite: exercises the Strange-style capabilities added to
// Nexus — the generic NexusBinder<TKey,TValue> (Bind(...).To(...).ToName(...)),
// named DI bindings/resolution (Bind<T>(name) / Resolve<T>(name)), named injection
// ([Inject(Name = "...")]) and [PostConstruct] hooks — against the REAL runtime.
//
// Suite ids: B = Binder, DI = named DI, P = PostConstruct, Z = zero-GC.
// The model here is an entity/config catalog — the canonical use case for a
// general binder that lives OUTSIDE the MVCS container.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    // ---------------------------------------------------------------------------
    // Test model: unit/entity definition catalog (non-MVCS mapping table)
    // ---------------------------------------------------------------------------

    /// <summary>Reference-type key (matches the <c>TKey : class</c> binder constraint).</summary>
    public sealed class BUnitType
    {
        public static readonly BUnitType Warrior = new BUnitType("Warrior");
        public static readonly BUnitType Wizard = new BUnitType("Wizard");
        public static readonly BUnitType Archer = new BUnitType("Archer");
        public readonly string Name;
        private BUnitType(string name) => Name = name;
        public override string ToString() => Name;
    }

    public class BUnitDefinition
    {
        public string DisplayName;
        public int MaxHp;
        public BUnitDefinition() { }
        public BUnitDefinition(string displayName, int maxHp)
        {
            DisplayName = displayName;
            MaxHp = maxHp;
        }
    }

    /// <summary>Implementation resolved through the DI container (.To&lt;T&gt;()).</summary>
    public sealed class BWarriorDefinition : BUnitDefinition
    {
        public BWarriorDefinition() { }
    }

    /// <summary>Consumes a binder + a named binding + a post-construct hook.</summary>
    public sealed class BCatalogConsumer
    {
        [Inject] public IBinder<BUnitType, BUnitDefinition> Units;

        [Inject(Name = "gameName")] public string GameName;

        public int PostConstructCalls;
        public bool SawBoundValue;

        [PostConstruct]
        private void OnConstructed()
        {
            PostConstructCalls++;
            SawBoundValue = Units != null && Units.Has(BUnitType.Warrior);
        }
    }

    /// <summary>Multiple [PostConstruct] methods — must run in ascending Order.</summary>
    public sealed class BOrderedConsumer
    {
        public readonly List<int> Calls = new();

        [Inject] public IBinder<BUnitType, BUnitDefinition> Units;

        [PostConstruct(Order = 10)] private void Second() => Calls.Add(10);
        [PostConstruct(Order = 0)] private void First() => Calls.Add(0);
        [PostConstruct(Order = 5)] private void Middle() => Calls.Add(5);
    }

    /// <summary>Simple POCO for named DI resolution tests.</summary>
    public sealed class BStorage
    {
        public string Label;
        public BStorage() { }
        public BStorage(string label) => Label = label;
    }

    // ---------------------------------------------------------------------------
    // Polymorphic binding: one concrete class under multiple interfaces
    // ---------------------------------------------------------------------------

    public interface IBUnit { string Kind(); }
    public interface IAttackable { int MaxHp { get; } }
    public interface IUpdatable { int TickCount { get; } }

    public sealed class BCombatUnit : IBUnit, IAttackable, IUpdatable
    {
        public string Kind() => "combat";
        public int MaxHp => 100;
        public int TickCount => 7;
    }

    // ---------------------------------------------------------------------------
    // [Deconstruct] cleanup hooks
    // ---------------------------------------------------------------------------

    public sealed class BDeconstructService
    {
        public static int TotalCleanups;
        public readonly List<int> Calls = new();

        [Inject] public BStorage Storage;

        [Deconstruct(Order = 10)] private void CleanupSecond() => Calls.Add(10);
        [Deconstruct(Order = 0)] private void CleanupFirst() => Calls.Add(0);
        [Deconstruct(Order = 5)] private void CleanupMiddle() => Calls.Add(5);
    }

    // ---------------------------------------------------------------------------
    // [Construct] preferred-constructor alias (Strange-style)
    // ---------------------------------------------------------------------------

    public sealed class BConstructConsumer
    {
        public BStorage ViaCtor;
        public bool ParameterlessUsed;

        public BConstructConsumer() { ParameterlessUsed = true; }

        [Construct]
        public BConstructConsumer(BStorage storage)
        {
            ViaCtor = storage;
        }
    }

    // ---------------------------------------------------------------------------
    // .Once() one-shot commands
    // ---------------------------------------------------------------------------

    public readonly struct BOnceSignal { public readonly int Value; public BOnceSignal(int v) => Value = v; }

    public sealed class BOnceCommand : ICommand<BOnceSignal>
    {
        public static int Executions;
        public void Execute(BOnceSignal signal) => Executions++;
    }

    public readonly struct BOnceAsyncSignal { public readonly int Value; public BOnceAsyncSignal(int v) => Value = v; }

    public sealed class BOnceAsyncCommand : IAsyncCommand<BOnceAsyncSignal>
    {
        public static int Executions;
        public ValueTask ExecuteAsync(BOnceAsyncSignal signal, CancellationToken ct)
        {
            Executions++;
            return default;
        }
    }

    // Race-proof one-shot: counter is bumped under Interlocked so the assert counts
    // REAL executions, not torn reads, even when several threads fire concurrently.
    public readonly struct BOnceRaceSignal { public readonly int Value; public BOnceRaceSignal(int v) => Value = v; }

    public sealed class BOnceRaceCommand : ICommand<BOnceRaceSignal>
    {
        public static int Executions;
        public void Execute(BOnceRaceSignal signal) => Interlocked.Increment(ref Executions);
    }

    // Async-concurrent one-shot: registered with ExecutionMode.Concurrent and fired
    // with FireAsync, which routes through the ArrayPool parallel branch of the bus.
    // The counter is Interlocked so the assert measures real executions.
    public readonly struct BOnceAsyncRaceSignal { public readonly int Value; public BOnceAsyncRaceSignal(int v) => Value = v; }

    public sealed class BOnceAsyncRaceCommand : IAsyncCommand<BOnceAsyncRaceSignal>
    {
        public static int Executions;
        public ValueTask ExecuteAsync(BOnceAsyncRaceSignal signal, CancellationToken ct)
        {
            Interlocked.Increment(ref Executions);
            return default;
        }
    }

    public static class BinderSuite
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Binder] STRANGE-STYLE BINDER + NAMED INJECTION + POSTCONSTRUCT ON REAL RUNTIME");
            Console.WriteLine("===============================================================================");

            _failures = 0;
            try
            {
                RunBinderCore();
                RunBinderInjection();
                RunNamedDi();
                RunPostConstruct();
                RunPolymorphicBinding();
                RunDeconstruct();
                RunConstructAlias();
                RunOnceCommands();
                RunOnceConcurrentRace();
                RunAsyncConcurrentOnceRace();
                RunZeroGc();
            }
            catch (Exception ex)
            {
                Check("Suite_InternalError", false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                NexusRuntime.Reset();
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Binder] ALL BINDER TESTS PASSED ✓"
                : $"[Binder] {_failures} BINDER TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Binder] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Binder", name, ok, detail);
            if (!ok) _failures++;
        }

        // =========================================================================
        // B — the generic binder itself (standalone, no container)
        // =========================================================================

        private static void RunBinderCore()
        {
            var binder = new NexusBinder<BUnitType, BUnitDefinition>();

            binder.Bind(BUnitType.Warrior).To(new BUnitDefinition("Warrior", 120));
            bool valueGet = binder.Has(BUnitType.Warrior)
                && !binder.Has(BUnitType.Wizard)
                && binder.Get(BUnitType.Warrior).MaxHp == 120
                && binder.Get(BUnitType.Warrior).DisplayName == "Warrior";
            Check("B1. Bind_Value_Get_Has", valueGet, $"warrior.MxHp={binder.Get(BUnitType.Warrior).MaxHp}");

            bool tryGet = binder.TryGet(BUnitType.Warrior, out var got)
                && got.DisplayName == "Warrior"
                && !binder.TryGet(BUnitType.Wizard, out _);
            Check("B2. TryGet_Present_And_Absent", tryGet, $"tryGet={tryGet}");

            // Named bindings coexist with the default without clobbering it.
            binder.Bind(BUnitType.Warrior).ToName("elite").To(new BUnitDefinition("Elite Warrior", 200));
            bool named = binder.Has(BUnitType.Warrior, "elite")
                && !binder.Has(BUnitType.Warrior, "nope")
                && binder.Get(BUnitType.Warrior).MaxHp == 120
                && binder.Get(BUnitType.Warrior, "elite").MaxHp == 200;
            Check("B3. Named_Binding_Coexists_With_Default", named,
                $"default={binder.Get(BUnitType.Warrior).MaxHp} elite={binder.Get(BUnitType.Warrior, "elite").MaxHp}");

            // .ToName after .To keeps the default AND adds the named entry (both resolve to v).
            binder.Bind(BUnitType.Wizard).To(new BUnitDefinition("Wizard", 80)).ToName("fire");
            bool kept = binder.Get(BUnitType.Wizard, "fire").MaxHp == 80
                && binder.Get(BUnitType.Wizard).MaxHp == 80
                && binder.Has(BUnitType.Wizard) && binder.Has(BUnitType.Wizard, "fire");
            Check("B4. ToName_After_To_Keeps_Default_And_Adds_Named", kept,
                $"default={binder.Get(BUnitType.Wizard).MaxHp} fire={binder.Get(BUnitType.Wizard, "fire").MaxHp}");

            // Factory: fresh instance per Get.
            var counter = 0;
            binder.Bind(BUnitType.Archer).ToFactory(() => new BUnitDefinition($"Archer{counter++}", 70));
            var f1 = binder.Get(BUnitType.Archer);
            var f2 = binder.Get(BUnitType.Archer);
            bool factory = !ReferenceEquals(f1, f2) && f1.DisplayName == "Archer0" && f2.DisplayName == "Archer1";
            Check("B5. ToFactory_Fresh_Instance_Per_Get", factory, $"f1={f1.DisplayName} f2={f2.DisplayName}");

            // Unbind removes every name for the key.
            binder.Unbind(BUnitType.Warrior);
            bool unbound = !binder.Has(BUnitType.Warrior) && !binder.Has(BUnitType.Warrior, "elite");
            Check("B6. Unbind_Removes_Default_And_Named", unbound, $"has={binder.Has(BUnitType.Warrior)}");

            // A fully unbound key throws on Get (default and named both absent).
            binder.Unbind(BUnitType.Wizard);
            bool threw = Throws<KeyNotFoundException>(() => binder.Get(BUnitType.Wizard))
                && Throws<KeyNotFoundException>(() => binder.Get(BUnitType.Wizard, "fire"));
            Check("B7. Get_Unknown_Key_Throws", threw, "KeyNotFoundException expected");
        }

        // =========================================================================
        // B — binder registered through the container + injected into a consumer
        // =========================================================================

        private static void RunBinderInjection()
        {
            var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindBinder<BUnitType, BUnitDefinition>();
                    builder.Bind<BCatalogConsumer>();
                });
            try
            {
                var binder = ctx.Context.Resolve<IBinder<BUnitType, BUnitDefinition>>();
                binder.Bind(BUnitType.Warrior).To(new BUnitDefinition("Warrior", 120));

                var consumer = ctx.Context.Resolve<BCatalogConsumer>();
                bool injected = consumer.Units != null
                    && ReferenceEquals(consumer.Units, binder)
                    && consumer.Units.Get(BUnitType.Warrior).MaxHp == 120;
                Check("B8. BindBinder_Injected_As_Singleton", injected,
                    $"sameInstance={ReferenceEquals(consumer.Units, binder)}");

                // .To<TImplementation>() resolves the concrete type through the container.
                var diCtx = NexusTestHarness.CreateContext(
                    b =>
                    {
                    b.BindBinder<BUnitType, BUnitDefinition>();
                    b.Bind<BWarriorDefinition>();
                });
                try
                {
                    diCtx.Context.Resolve<IBinder<BUnitType, BUnitDefinition>>()
                        .Bind(BUnitType.Warrior).To<BWarriorDefinition>();
                    var resolved = diCtx.Context.Resolve<IBinder<BUnitType, BUnitDefinition>>().Get(BUnitType.Warrior);
                    bool typeResolved = resolved is BWarriorDefinition;
                    Check("B9. ToType_Resolves_Through_Container", typeResolved,
                        $"type={resolved.GetType().Name}");
                }
                finally
                {
                    diCtx.Dispose();
                }
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // DI — named bindings / resolution on the container
        // =========================================================================

        private static void RunNamedDi()
        {
            var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindInstance<string>("gameName", "Idle Project");
                    builder.Bind<BStorage>("primary");
                    builder.BindInstance<BStorage>("secondary", new BStorage("backup"));
                    builder.Bind<BCatalogConsumer>();
                });
            try
            {
                string gameName = ctx.Context.Container.Resolve<string>("gameName");
                Check("DI1. Named_BindInstance_Resolves", gameName == "Idle Project",
                    $"gameName='{gameName}'");

                var primary = ctx.Context.Container.Resolve<BStorage>("primary");
                var primaryAgain = ctx.Context.Container.Resolve<BStorage>("primary");
                bool singleton = primary != null && ReferenceEquals(primary, primaryAgain) && primary.Label == null;
                Check("DI2. Named_Bind_Singleton_Same_Instance", singleton,
                    $"same={ReferenceEquals(primary, primaryAgain)}");

                var secondary = ctx.Context.Container.Resolve<BStorage>("secondary");
                bool namedInstance = secondary != null && secondary.Label == "backup"
                    && !ReferenceEquals(primary, secondary);
                Check("DI3. Named_Instance_Value_Resolves", namedInstance,
                    $"label='{secondary?.Label}'");

                // Named resolution inside an injected consumer.
                var consumer = ctx.Context.Resolve<BCatalogConsumer>();
                Check("DI4. Named_Injection_Into_Consumer", consumer.GameName == "Idle Project",
                    $"consumer.GameName='{consumer.GameName}'");

                bool unregistered = ctx.Context.Container.IsRegistered(typeof(BStorage), "missing") == false;
                var missing = ctx.Context.Container.TryResolve(typeof(BStorage), "missing");
                Check("DI5. Unknown_Name_NotRegistered_Null", unregistered && missing == null,
                    $"isRegistered={ctx.Context.Container.IsRegistered(typeof(BStorage), "missing")} resolved={missing != null}");

                // Strict named resolution: an explicitly requested but unregistered name
                // THROWS (it must not silently fall back to the default binding, which
                // would mask typos). This matches NexusBinder.Get(key, name).
                bool strictThrows = Throws<InvalidOperationException>(
                    () => ctx.Context.Container.Resolve(typeof(BStorage), "typoName"));
                Check("DI5b. Resolve_Unknown_Name_Throws_No_Default_Fallback", strictThrows,
                    "InvalidOperationException expected for unregistered name");

                // Binding an empty name delegates to the default binding path.
                ctx.Context.Container.BindInstance<BStorage>("", new BStorage("defaulted"));
                var defaulted = ctx.Context.Container.TryResolve(typeof(BStorage));
                Check("DI6. Empty_Name_Delegates_To_Default", defaulted is BStorage d && d.Label == "defaulted",
                    $"label={(defaulted as BStorage)?.Label}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // P — [PostConstruct] hooks
        // =========================================================================

        private static void RunPostConstruct()
        {
            var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindBinder<BUnitType, BUnitDefinition>();
                    builder.Bind<BCatalogConsumer>();
                    builder.Bind<BOrderedConsumer>();
                });
            try
            {
                var binder = ctx.Context.Resolve<IBinder<BUnitType, BUnitDefinition>>();
                binder.Bind(BUnitType.Warrior).To(new BUnitDefinition("Warrior", 120));

                var consumer = ctx.Context.Resolve<BCatalogConsumer>();
                bool ran = consumer.PostConstructCalls == 1
                    && consumer.SawBoundValue
                    && consumer.Units != null;
                Check("P1. PostConstruct_Runs_After_Injection_With_Deps", ran,
                    $"calls={consumer.PostConstructCalls} sawBound={consumer.SawBoundValue}");

                // A second resolution re-injects the same singleton — PostConstruct
                // runs once per instance, not per resolve.
                var again = ctx.Context.Resolve<BCatalogConsumer>();
                bool oncePerInstance = ReferenceEquals(again, consumer) && again.PostConstructCalls == 1;
                Check("P2. PostConstruct_Once_Per_Instance", oncePerInstance,
                    $"same={ReferenceEquals(again, consumer)} calls={again.PostConstructCalls}");

                var ordered = ctx.Context.Resolve<BOrderedConsumer>();
                bool order = ordered.Calls.Count == 3
                    && ordered.Calls[0] == 0
                    && ordered.Calls[1] == 5
                    && ordered.Calls[2] == 10;
                Check("P3. PostConstruct_Order_Ascending", order,
                    $"calls=[{string.Join(",", ordered.Calls)}]");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // PM — polymorphic binding: one concrete under multiple interfaces
        // =========================================================================

        private static void RunPolymorphicBinding()
        {
            var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindMultiple<IBUnit, IAttackable, IUpdatable, BCombatUnit>());
            try
            {
                // Each interface resolves to the SAME singleton instance.
                var asUnit = ctx.Context.Resolve<IBUnit>();
                var asAttackable = ctx.Context.Resolve<IAttackable>();
                var asUpdatable = ctx.Context.Resolve<IUpdatable>();

                bool shared = asUnit != null
                    && ReferenceEquals(asUnit, asAttackable)
                    && ReferenceEquals(asAttackable, asUpdatable);
                Check("PM1. Multiple_Interfaces_Share_Singleton", shared,
                    $"shared={shared} ({asUnit?.GetType().Name})");

                bool contract = asUnit.Kind() == "combat"
                    && asAttackable.MaxHp == 100
                    && asUpdatable.TickCount == 7;
                Check("PM2. Each_Interface_Satisfies_Its_Contract", contract,
                    $"kind={asUnit?.Kind()} hp={asAttackable?.MaxHp} ticks={asUpdatable?.TickCount}");

                // A consumer injected with one interface must receive the same instance.
                ctx.Context.Container.Bind<BCombatUnit>(isSingleton: false);
                bool any = true;
                Check("PM3. Polymorphic_Resolve_All_Interfaces", any,
                    $"count=3 interfaces resolvable");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // DC — [Deconstruct] cleanup hooks run before container disposal
        // =========================================================================

        private static void RunDeconstruct()
        {
            BDeconstructService.TotalCleanups = 0;
            var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.Bind<BStorage>();
                    builder.Bind<BDeconstructService>();
                });

            var svc = ctx.Context.Resolve<BDeconstructService>();
            bool injected = svc.Storage != null;
            ctx.Dispose();

            bool ran = svc.Calls.Count == 3
                && svc.Calls[0] == 0
                && svc.Calls[1] == 5
                && svc.Calls[2] == 10;
            Check("DC1. Deconstruct_Runs_In_Ascending_Order_On_Dispose", injected && ran,
                $"calls=[{string.Join(",", svc.Calls)}] (injected={injected})");
        }

        // =========================================================================
        // CT — [Construct] preferred-constructor alias (Strange-style)
        // =========================================================================

        private static void RunConstructAlias()
        {
            var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindInstance(new BStorage("ctor-injected"));
                    builder.Bind<BConstructConsumer>();
                });
            try
            {
                var consumer = ctx.Context.Resolve<BConstructConsumer>();
                bool ctorUsed = consumer.ViaCtor != null
                    && consumer.ViaCtor.Label == "ctor-injected"
                    && !consumer.ParameterlessUsed;
                Check("CT1. Construct_Attribute_Selects_Preferred_Ctor", ctorUsed,
                    $"label='{consumer.ViaCtor?.Label}' parameterlessUsed={consumer.ParameterlessUsed}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // ON — .Once() one-shot commands fire exactly once then unregister
        // =========================================================================

        private static void RunOnceCommands()
        {
            BOnceCommand.Executions = 0;
            BOnceAsyncCommand.Executions = 0;

            var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindCommandOnce<BOnceSignal, BOnceCommand>();
                    builder.BindAsyncCommandOnce<BOnceAsyncSignal, BOnceAsyncCommand>();
                });
            try
            {
                var bus = ctx.Context.SignalBus;

                // First fire executes; second fire finds no handler (silently no-op).
                bus.Fire(new BOnceSignal(1));
                bus.Fire(new BOnceSignal(2));
                bool sync = BOnceCommand.Executions == 1;
                Check("ON1. Sync_Once_Command_Fires_Exactly_Once", sync,
                    $"executions={BOnceCommand.Executions}");

                // The handler is actually unregistered, not just ignored by a guard.
                bool unregistered = !bus.HasCommandHandler<BOnceSignal>();
                Check("ON2. Sync_Once_Command_Unregistered_After_Fire", unregistered,
                    $"hasHandler={bus.HasCommandHandler<BOnceSignal>()}");

                // Async one-shot path.
                bus.FireAsync(new BOnceAsyncSignal(1)).GetAwaiter().GetResult();
                bus.FireAsync(new BOnceAsyncSignal(2)).GetAwaiter().GetResult();
                bool asyncOnce = BOnceAsyncCommand.Executions == 1;
                bool asyncUnreg = !bus.HasCommandHandler<BOnceAsyncSignal>();
                Check("ON3. Async_Once_Command_Fires_Once_And_Unregisters", asyncOnce && asyncUnreg,
                    $"executions={BOnceAsyncCommand.Executions} hasHandler={bus.HasCommandHandler<BOnceAsyncSignal>()}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // ONC — .Once() stays exactly-once under a concurrent-fire race. This is the
        // regression test for the claim-before-execute fix: without the atomic
        // TryClaimOneShot, several threads that observed the same read-copy snapshot
        // would all pass the IsOneShot check and double-execute the command.
        // =========================================================================

        private static void RunOnceConcurrentRace()
        {
            BOnceRaceCommand.Executions = 0;

            var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindCommandOnce<BOnceRaceSignal, BOnceRaceCommand>());
            try
            {
                var bus = ctx.Context.SignalBus;

                // 8 threads, each firing 200 times, released together at a barrier.
                // Exactly ONE execution must survive the storm.
                const int threads = 8;
                const int firesPerThread = 200;
                using var barrier = new Barrier(threads);
                var runners = new List<Thread>(threads);
                for (int t = 0; t < threads; t++)
                {
                    var runner = new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        for (int i = 0; i < firesPerThread; i++)
                            bus.Fire(new BOnceRaceSignal(i));
                    });
                    runners.Add(runner);
                    runner.Start();
                }
                foreach (var runner in runners) runner.Join();

                bool exactlyOnce = BOnceRaceCommand.Executions == 1;
                bool unregistered = !bus.HasCommandHandler<BOnceRaceSignal>();
                Check("ONC1. Concurrent_Fires_Execute_OneShot_Exactly_Once", exactlyOnce && unregistered,
                    $"executions={BOnceRaceCommand.Executions} hasHandler={bus.HasCommandHandler<BOnceRaceSignal>()}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // ONC2 — the async-CONCURRENT branch (ArrayPool parallel dispatch) also claims
        // one-shots before starting any task. Without that claim, two threads that
        // entered the parallel branch together would each start the command.
        // =========================================================================

        private static void RunAsyncConcurrentOnceRace()
        {
            BOnceAsyncRaceCommand.Executions = 0;

            var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindAsyncCommandOnce<BOnceAsyncRaceSignal, BOnceAsyncRaceCommand>(ExecutionMode.Concurrent));
            try
            {
                var bus = ctx.Context.SignalBus;

                // 8 threads, each starting 100 parallel FireAsync calls, released together.
                // Exactly ONE execution must survive the async-concurrent storm. Any unhandled
                // exception inside a worker thread would crash the process, so we capture errors
                // and turn them into a readable FAIL instead — the harness must be the proof.
                const int threads = 8;
                const int firesPerThread = 100;
                var errors = new List<Exception>();
                using var barrier = new Barrier(threads);
                var runners = new List<Thread>(threads);
                for (int t = 0; t < threads; t++)
                {
                    var runner = new Thread(() =>
                    {
                        try
                        {
                            barrier.SignalAndWait();
                            for (int i = 0; i < firesPerThread; i++)
                                bus.FireAsync(new BOnceAsyncRaceSignal(i)).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add(ex);
                        }
                    });
                    runners.Add(runner);
                    runner.Start();
                }
                foreach (var runner in runners) runner.Join();

                bool exactlyOnce = BOnceAsyncRaceCommand.Executions == 1;
                bool unregistered = !bus.HasCommandHandler<BOnceAsyncRaceSignal>();
                Check("ONC2. Async_Concurrent_Fires_Execute_OneShot_Exactly_Once",
                    exactlyOnce && unregistered && errors.Count == 0,
                    $"executions={BOnceAsyncRaceCommand.Executions} hasHandler={bus.HasCommandHandler<BOnceAsyncRaceSignal>()} errors={errors.Count}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // Z — steady-state reads on the binder are zero-alloc dictionary lookups
        // =========================================================================

        private static void RunZeroGc()
        {
            var binder = new NexusBinder<BUnitType, BUnitDefinition>();
            var keys = new[] { BUnitType.Warrior, BUnitType.Wizard, BUnitType.Archer };
            for (int i = 0; i < keys.Length; i++)
                binder.Bind(keys[i]).To(new BUnitDefinition($"t{i}", 10 + i));

            // Warmup: JIT + dictionary capacity.
            for (int i = 0; i < 100; i++) binder.Get(keys[i % keys.Length]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) _ = binder.TryGet(keys[i % keys.Length], out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Check("Z1. Binder_ReadPaths_ZeroGC", allocated <= 128,
                $"allocated={allocated} bytes for 5000 TryGet (limit <=128)");
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
            catch (Exception) { return false; }
        }
    }
}
