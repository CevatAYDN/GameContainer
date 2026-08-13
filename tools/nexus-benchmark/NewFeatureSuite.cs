using System;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    /// <summary>
    /// Proves the three new adoption-surface features end to end:
    ///   FLUENT — BindFluent&lt;T&gt;().To/AsSingleton/AsScoped/AsTransient/AsSingle/AsCached/
    ///            AsImplementedInterfaces/AndSelf/WithParameter (VContainer/Zenject-style chain).
    ///   FILTER — ISignalFilter&lt;T&gt; ref-based pipeline on SignalBus: cancel, mutate, ordering,
    ///            generic registration and the async path. Never boxes the signal struct.
    ///   CTOR   — NexusDI.RegisterConstructorFactory&lt;T&gt; zero-reflection instantiation:
    ///            precedence over reflection, readonly state, parent-chain resolution from a
    ///            child scope, and cleanup by NexusRuntime.Reset (mirroring the codegen path).
    /// Runs after LifetimeScopeSuite. Uses only plain containers / MockContext buses, and
    /// finishes with NexusRuntime.Reset() so the leak-audit suites that follow stay clean.
    /// </summary>
    public static class NewFeatureSuite
    {
        private static int _failures;

        // ─── Fluent binding fixtures ───
        public interface IFluentDep { string Name { get; } }
        public sealed class FluentDep : IFluentDep
        {
            public string Name => "FluentDep";
        }

        public interface IFluentA { }
        public interface IFluentB { }
        public sealed class FluentMulti : IFluentA, IFluentB { }

        public sealed class WithParamDep
        {
            public readonly int Value;
            public WithParamDep(int value) { Value = value; }
        }

        public sealed class CtorDep
        {
            public readonly int Value;
            public readonly FluentDep Dep;
            public CtorDep(int value, FluentDep dep) { Value = value; Dep = dep; }
        }

        public sealed class TrackedDisposable : IDisposable
        {
            public bool Disposed;
            public void Dispose() { Disposed = true; }
        }

        // ─── Adapter-priority fixtures (CF5) ───
        public sealed class ExternalAdapterDep { }
        public sealed class ExternalBridgeValue { public static readonly ExternalBridgeValue Instance = new ExternalBridgeValue(); }

        private sealed class ClaimingAdapter : IDependencyAdapter
        {
            public readonly object BridgeValue = new ExternalBridgeValue();
            public int IsRegisteredCalls;
            public int ResolveCalls;

            public bool IsRegistered(Type type)
            {
                IsRegisteredCalls++;
                return type == typeof(ExternalAdapterDep) || type == typeof(ExternalBridgeValue);
            }

            public object Resolve(Type type)
            {
                ResolveCalls++;
                return BridgeValue;
            }

            public void Inject(object instance) { }
        }

        // ─── Filter fixtures ───
        public struct FilterSignal { public int Value; }
        public struct CancelSignal { public int Value; }

        public sealed class DoubleFilter : ISignalFilter<FilterSignal>
        {
            public bool OnFilter(ref FilterSignal signal) { signal.Value *= 2; return true; }
        }

        public sealed class AddOneFilter : ISignalFilter<FilterSignal>
        {
            public bool OnFilter(ref FilterSignal signal) { signal.Value += 1; return true; }
        }

        public sealed class AlwaysCancelFilter : ISignalFilter<CancelSignal>
        {
            public bool OnFilter(ref CancelSignal signal) { return false; }
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[NewFeature] FLUENT BINDING + ISignalFilter<T> PIPELINE + CTOR FACTORY");
            Console.WriteLine("===============================================================================");

            Test_Fluent_To_AsScoped_SharesInstance();
            Test_Fluent_AsTransient_FreshPerResolve_NotOwned();
            Test_Fluent_AsSingleton_OnChildScope_LivesAtRoot();
            Test_Fluent_AsImplementedInterfaces_OneSharedBinding();
            Test_Fluent_WithParameter_OverridesCtorArgument();
            Test_Fluent_AsSingle_And_AsCached_MapToScoped();
            Test_Fluent_Alone_RegistersNothing();

            Test_Filter_CancelsSignal_SubscriberNeverRuns();
            Test_Filter_MutatesSignal_SubscriberSeesMutatedValue();
            Test_Filter_GenericOverload_ActivatesOrResolves();
            Test_Filter_RunInRegistrationOrder();
            Test_Filter_AppliesOnAsyncPath();

            Test_CtorFactory_TakesPrecedenceOverReflection();
            Test_CtorFactory_ReadonlyState_ResolvesParentDepFromChildScope();
            Test_CtorFactory_ClearedByNexusRuntime_Reset();
            Test_CtorFactory_WithParameter_OverrideWins();
            Test_ExternalAdapter_DoesNotShadowLocalBinding();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[NewFeature] ALL NEW-FEATURE TESTS PASSED ✓"
                : $"[NewFeature] {_failures} NEW-FEATURE TEST(S) FAILED ✗");
            return _failures;
        }

        // ─── FLUENT ───

        private static void Test_Fluent_To_AsScoped_SharesInstance()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<IFluentDep>().To<FluentDep>().AsScoped();
                var a = root.Resolve<IFluentDep>();
                var b = root.Resolve<IFluentDep>();
                bool shared = ReferenceEquals(a, b);
                bool impl = a.Name == "FluentDep";
                // Key type is bound; the concrete type itself is NOT (interface-keyed binding).
                bool concreteNotBound = Throws<InvalidOperationException>(() => root.Resolve<FluentDep>());
                ok = shared && impl && concreteNotBound;
                detail = $"shared={shared} impl={impl} concreteNotBound={concreteNotBound}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL1. Fluent_To_AsScoped_SharesInstance", ok, detail);
        }

        private static void Test_Fluent_AsTransient_FreshPerResolve_NotOwned()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<TrackedDisposable>().AsTransient();
                var a = root.Resolve<TrackedDisposable>();
                var b = root.Resolve<TrackedDisposable>();
                bool fresh = !ReferenceEquals(a, b);
                root.Dispose();
                bool notOwned = !a.Disposed && !b.Disposed;
                ok = fresh && notOwned;
                detail = $"fresh={fresh} notOwned={notOwned}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL2. Fluent_AsTransient_FreshPerResolve_NotOwned", ok, detail);
        }

        private static void Test_Fluent_AsSingleton_OnChildScope_LivesAtRoot()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                var child = root.CreateChildScope();
                child.BindFluent<TrackedDisposable>().AsSingleton();
                var viaRoot = root.Resolve<TrackedDisposable>();
                var viaChild = child.Resolve<TrackedDisposable>();
                bool shared = ReferenceEquals(viaRoot, viaChild);
                child.Dispose();
                bool aliveAfterChildDispose = !viaRoot.Disposed;
                root.Dispose();
                bool disposedWithRoot = viaRoot.Disposed;
                ok = shared && aliveAfterChildDispose && disposedWithRoot;
                detail = $"shared={shared} aliveAfterChildDispose={aliveAfterChildDispose} disposedWithRoot={disposedWithRoot}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL3. Fluent_AsSingleton_OnChildScope_LivesAtRoot", ok, detail);
        }

        private static void Test_Fluent_AsImplementedInterfaces_OneSharedBinding()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<FluentMulti>().AsImplementedInterfaces();
                var self = root.Resolve<FluentMulti>();
                var a = root.Resolve<IFluentA>();
                var b = root.Resolve<IFluentB>();
                bool shared = ReferenceEquals(self, a) && ReferenceEquals(a, b);
                ok = shared;
                detail = $"shared={shared}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL4. Fluent_AsImplementedInterfaces_OneSharedBinding", ok, detail);
        }

        private static void Test_Fluent_WithParameter_OverridesCtorArgument()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<WithParamDep>().WithParameter<int>(42);
                var dep = root.Resolve<WithParamDep>();
                bool overridden = dep.Value == 42;
                ok = overridden;
                detail = $"value={dep.Value}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL5. Fluent_WithParameter_OverridesCtorArgument", ok, detail);
        }

        private static void Test_Fluent_AsSingle_And_AsCached_MapToScoped()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<TrackedDisposable>().AsSingle();
                var a1 = root.Resolve<TrackedDisposable>();
                var a2 = root.Resolve<TrackedDisposable>();
                bool singleShares = ReferenceEquals(a1, a2);
                var root2 = new NexusDI();
                root2.BindFluent<TrackedDisposable>().AsCached();
                var c1 = root2.Resolve<TrackedDisposable>();
                var c2 = root2.Resolve<TrackedDisposable>();
                bool cachedShares = ReferenceEquals(c1, c2);
                root2.Dispose();
                bool cachedDisposedWithContainer = c1.Disposed;
                ok = singleShares && cachedShares && cachedDisposedWithContainer;
                detail = $"singleShares={singleShares} cachedShares={cachedShares} cachedDisposedWithContainer={cachedDisposedWithContainer}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL6. Fluent_AsSingle_And_AsCached_MapToScoped", ok, detail);
        }

        private static void Test_Fluent_Alone_RegistersNothing()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<FluentDep>();
                bool notRegistered = !root.IsRegistered(typeof(FluentDep));
                ok = notRegistered;
                detail = $"notRegistered={notRegistered}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("FL7. Fluent_Alone_RegistersNothing", ok, detail);
        }

        // ─── FILTER ───

        private static SignalBus NewBus(NexusDI container)
            => new SignalBus(container, new CommandPoolManager(container), new MockContext());

        private static void Test_Filter_CancelsSignal_SubscriberNeverRuns()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bool ok = false;
            string detail;
            try
            {
                int seen = 0;
                bus.Subscribe<CancelSignal>(s => seen++);
                bus.AddSignalFilter(new AlwaysCancelFilter());
                bus.Fire(new CancelSignal { Value = 7 });
                bool cancelled = seen == 0;
                ok = cancelled;
                detail = $"subscriberRuns={seen}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { bus.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] bus.Dispose teardown: {ex.Message}"); }
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("SF1. Filter_CancelsSignal_SubscriberNeverRuns", ok, detail);
        }

        private static void Test_Filter_MutatesSignal_SubscriberSeesMutatedValue()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bool ok = false;
            string detail;
            try
            {
                int seen = 0;
                bus.Subscribe<FilterSignal>(s => seen = s.Value);
                bus.AddSignalFilter(new DoubleFilter());
                bus.Fire(new FilterSignal { Value = 21 });
                bool mutated = seen == 42;
                ok = mutated;
                detail = $"seen={seen}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { bus.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] bus.Dispose teardown: {ex.Message}"); }
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("SF2. Filter_MutatesSignal_SubscriberSeesMutatedValue", ok, detail);
        }

        private static void Test_Filter_GenericOverload_ActivatesOrResolves()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bool ok = false;
            string detail;
            try
            {
                int seen = 0;
                bus.Subscribe<FilterSignal>(s => seen = s.Value);
                bus.AddSignalFilter<FilterSignal, DoubleFilter>();
                bus.Fire(new FilterSignal { Value = 3 });
                bool applied = seen == 6;
                ok = applied;
                detail = $"seen={seen}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { bus.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] bus.Dispose teardown: {ex.Message}"); }
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("SF3. Filter_GenericOverload_ActivatesOrResolves", ok, detail);
        }

        private static void Test_Filter_RunInRegistrationOrder()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bool ok = false;
            string detail;
            try
            {
                int seen = 0;
                bus.Subscribe<FilterSignal>(s => seen = s.Value);
                bus.AddSignalFilter(new DoubleFilter());   // 5 * 2 = 10
                bus.AddSignalFilter(new AddOneFilter());   // 10 + 1 = 11
                bus.Fire(new FilterSignal { Value = 5 });
                bool ordered = seen == 11;
                ok = ordered;
                detail = $"seen={seen}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { bus.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] bus.Dispose teardown: {ex.Message}"); }
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("SF4. Filter_RunInRegistrationOrder", ok, detail);
        }

        private static void Test_Filter_AppliesOnAsyncPath()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bool ok = false;
            string detail;
            try
            {
                int seen = 0;
                bus.SubscribeAsync<FilterSignal>(async (s, ct) => { seen = s.Value; await Task.CompletedTask; });
                bus.AddSignalFilter(new DoubleFilter());
                bus.FireAsync(new FilterSignal { Value = 5 }).GetAwaiter().GetResult();
                bool mutated = seen == 10;
                ok = mutated;
                detail = $"seen={seen}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { bus.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] bus.Dispose teardown: {ex.Message}"); }
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("SF5. Filter_AppliesOnAsyncPath", ok, detail);
        }

        // ─── CTOR FACTORY ───

        private static void Test_CtorFactory_TakesPrecedenceOverReflection()
        {
            // CtorDep(int, FluentDep): the reflection path cannot resolve the int parameter
            // (value type, non-strict -> null -> Value 0). The factory produces Value 999 and
            // the real FluentDep — only the factory path can yield that combination.
            NexusDI.RegisterConstructorFactory<CtorDep>(di => new CtorDep(999, di.Resolve<FluentDep>()));
            var container = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                container.BindFluent<FluentDep>().AsScoped();
                // The factory REPLACES instantiation of a bound type; it does not auto-register.
                container.Bind<CtorDep>(Lifetime.Transient);
                var dep = container.Resolve<CtorDep>();
                var shared = container.Resolve<FluentDep>();
                bool factoryValue = dep.Value == 999;
                bool depResolved = dep.Dep != null && ReferenceEquals(dep.Dep, shared);
                ok = factoryValue && depResolved;
                detail = $"value={dep.Value} depShared={depResolved}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("CF1. CtorFactory_TakesPrecedenceOverReflection", ok, detail);
        }

        private static void Test_CtorFactory_ReadonlyState_ResolvesParentDepFromChildScope()
        {
            // Factory runs against the CHILD container; the dependency it resolves must come
            // through the parent chain. Readonly fields are set inside the generated lambda
            // (exactly what the AOT binder emits), proving immutable state works.
            NexusDI.RegisterConstructorFactory<CtorDep>(di => new CtorDep(999, di.Resolve<FluentDep>()));
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.BindFluent<FluentDep>().AsScoped();
                root.Bind<CtorDep>(Lifetime.Transient);
                var child = root.CreateChildScope();
                var viaChild = child.Resolve<CtorDep>();
                var viaRoot = root.Resolve<FluentDep>();
                bool readonlyValue = viaChild.Value == 999;
                bool parentDep = viaChild.Dep != null && ReferenceEquals(viaChild.Dep, viaRoot);
                ok = readonlyValue && parentDep;
                detail = $"value={viaChild.Value} parentDep={parentDep}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] root.Dispose teardown: {ex.Message}"); }
            }
            Report("CF2. CtorFactory_ReadonlyState_ResolvesParentDepFromChildScope", ok, detail);
        }

        private static void Test_CtorFactory_ClearedByNexusRuntime_Reset()
        {
            // Register a factory, then Reset (as Unity does on domain reload with
            // Domain Reload disabled). The factory dictionary must be cleared so the type
            // falls back to the reflection path instead of a stale lambda holding a dead
            // assembly reference.
            NexusDI.RegisterConstructorFactory<CtorDep>(di => new CtorDep(999, null));
            NexusRuntime.Reset();
            var container = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                container.BindFluent<FluentDep>().AsScoped();
                container.Bind<CtorDep>(Lifetime.Transient);
                var dep = container.Resolve<CtorDep>();
                // Reflection path: int param unresolvable (non-strict) -> 0, dep resolved.
                bool fellBack = dep.Value == 0 && dep.Dep != null;
                ok = fellBack;
                detail = $"value={dep.Value} depNull={dep.Dep == null}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
                try { NexusRuntime.Reset(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] NexusRuntime.Reset teardown: {ex.Message}"); }
            }
            Report("CF3. CtorFactory_ClearedByNexusRuntime_Reset", ok, detail);
        }

        private static void Test_CtorFactory_WithParameter_OverrideWins()
        {
            // A factory is registered for the type (exactly what the AOT binder emits), but the
            // fluent WithParameter value must still win: explicit constructor arguments take
            // precedence over the generated lambda's container resolution. Regression for the
            // silent-drop bug where the factory path bypassed ParameterOverrides entirely.
            NexusDI.RegisterConstructorFactory<CtorDep>(di => new CtorDep(999, di.Resolve<FluentDep>()));
            var container = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                container.BindFluent<FluentDep>().AsScoped();
                var manual = new FluentDep();
                container.BindFluent<CtorDep>().To<CtorDep>().AsTransient().WithParameter<FluentDep>(manual);
                var dep = container.Resolve<CtorDep>();
                bool overrideWon = ReferenceEquals(dep.Dep, manual);
                bool factoryDropped = dep.Dep != null; // override value, not container resolution
                ok = overrideWon && factoryDropped;
                detail = $"overrideShared={overrideWon} depSet={factoryDropped} (factory would resolve the container's FluentDep instead)";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("CF4. CtorFactory_WithParameter_OverrideWins", ok, detail);
        }

        private static void Test_ExternalAdapter_DoesNotShadowLocalBinding()
        {
            // Regression: the external-adapter fast path used to be consulted BEFORE local
            // bindings, so an explicit Bind on a type the adapter claims was silently
            // shadowed — the adapter's value won and the local binding was unreachable.
            var adapter = new ClaimingAdapter();
            var container = new NexusDI { ExternalAdapter = adapter };
            bool ok = false;
            string detail;
            try
            {
                container.Bind<ExternalAdapterDep>(Lifetime.Scoped);
                int adapterCallsBefore = adapter.IsRegisteredCalls;
                var a = container.Resolve<ExternalAdapterDep>();
                var b = container.Resolve<ExternalAdapterDep>();
                bool localWon = ReferenceEquals(a, b) && adapter.ResolveCalls == 0;
                // A locally bound type is answered WITHOUT consulting the adapter at all.
                bool adapterSkippedForLocal = adapter.IsRegisteredCalls == adapterCallsBefore;
                // The bridge still works for unbound types.
                var bridged = container.Resolve<ExternalBridgeValue>();
                bool bridgeStillWorks = ReferenceEquals(bridged, adapter.BridgeValue) && adapter.ResolveCalls == 1;
                bool registeredConsistent = container.IsRegistered(typeof(ExternalAdapterDep));
                ok = localWon && adapterSkippedForLocal && bridgeStillWorks && registeredConsistent;
                detail = $"localWon={localWon} adapterSkippedForLocal={adapterSkippedForLocal} bridgeStillWorks={bridgeStillWorks} registered={registeredConsistent} adapterResolveCalls={adapter.ResolveCalls}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { container.Dispose(); } catch (Exception ex) { Console.WriteLine($"[NewFeature] container.Dispose teardown: {ex.Message}"); }
            }
            Report("CF5. ExternalAdapter_DoesNotShadowLocalBinding", ok, detail);
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("NewFeature", name, ok, detail);
            if (!ok) _failures++;
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
            catch { return false; }
        }
    }
}
