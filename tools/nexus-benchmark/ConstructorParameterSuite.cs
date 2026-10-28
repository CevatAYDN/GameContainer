using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;

namespace NexusBench
{
    /// <summary>
    /// Semantic lock for the parameter path of generated constructor factories (#111). Every scenario runs twice
    /// with factories in the generated shape: once through <see cref="NexusDI.ResolveConstructorParameter{T}"/> and
    /// once through <see cref="Reference{T}"/>, a verbatim copy of that method's body before #111. The two traces
    /// (object graph and identity, exception type and message, warnings, lazy-service callbacks, resolve counter)
    /// must be equal.
    /// </summary>
    public static class ConstructorParameterSuite
    {
        private static int _failures;
        private static bool s_reference;

        // ResolveConstructorParameter<T> at 1495a18, with `this` as `di`.
        private static T Reference<T>(NexusDI di, int parameterIndex, string ownerTypeName, string parameterTypeName, string name = null) where T : class
        {
            T value = string.IsNullOrEmpty(name) ? di.TryResolve<T>() : di.TryResolve<T>(name);
            if (value == null && di.StrictInjection)
            {
                throw new InvalidOperationException(
                    $"Strict injection failed: constructor parameter {parameterIndex} of type '{parameterTypeName}' on '{ownerTypeName}' is not registered.");
            }
            if (value == null && !di.StrictInjection)
            {
                NexusRuntime.Logger?.LogWarning(
                    $"[Nexus] Constructor parameter {parameterIndex} ('{parameterTypeName}') on '{ownerTypeName}' is not registered; passing null. " +
                    "Enable StrictInjection or mark the dependency [OptionalInject] to make this explicit.");
            }
            return value;
        }

        private static T Param<T>(NexusDI di, int index, string owner, string type, string name = null) where T : class
            => s_reference ? Reference<T>(di, index, owner, type, name) : di.ResolveConstructorParameter<T>(index, owner, type, name);

        public abstract class Holder
        {
            public readonly object Value;
            protected Holder(object value) { Value = value; }
        }

        public class Leaf { }
        public sealed class AltLeaf : Leaf { }
        public sealed class Mid : Holder { public Mid(Leaf leaf) : base(leaf) { } }
        public sealed class Owner : Holder { public Owner(Mid mid) : base(mid) { } }
        public interface IMissing { }
        public sealed class NeedsMissing : Holder { public NeedsMissing(IMissing value) : base(value) { } }
        public sealed class NeedsNamed : Holder { public NeedsNamed(Leaf value) : base(value) { } }
        public sealed class LazyLeaf : INexusService
        {
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }
        public sealed class NeedsLazy : Holder { public NeedsLazy(LazyLeaf value) : base(value) { } }
        public sealed class Disposer
        {
            public static Action OnConstruct;
            public Disposer() { OnConstruct?.Invoke(); }
        }
        public sealed class NeedsDisposer : Holder { public NeedsDisposer(Disposer value) : base(value) { } }
        public sealed class CycRoot : Holder { public CycRoot(CycA value) : base(value) { } }
        public sealed class CycA : Holder { public CycA(CycB value) : base(value) { } }
        public sealed class CycB : Holder { public CycB(CycA value) : base(value) { } }
        public sealed class Tagged
        {
            public readonly string Tag;
            public Tagged(string tag) { Tag = tag; }
        }
        public sealed class NeedsTagged : Holder { public NeedsTagged(Tagged value) : base(value) { } }
        public interface IAdapted { }
        public sealed class Adapted : IAdapted { }
        public sealed class AdapterAdapted : IAdapted { }
        public sealed class NeedsAdapted : Holder { public NeedsAdapted(IAdapted value) : base(value) { } }
        public interface IVariant { }
        public sealed class VariantOne : IVariant { }
        public sealed class VariantTwo : IVariant { }
        public sealed class NeedsVariant : Holder { public NeedsVariant(IVariant value) : base(value) { } }
        public sealed class LateLeaf
        {
            public readonly string Via;
            public LateLeaf(string via) { Via = via; }
        }
        public sealed class NeedsLate : Holder { public NeedsLate(LateLeaf value) : base(value) { } }
        public sealed class NeedsContainer : Holder { public NeedsContainer(NexusDI value) : base(value) { } }

        private sealed class Adapter : IDependencyAdapter
        {
            public object Resolve(Type type) => type == typeof(IAdapted) ? new AdapterAdapted() : null;
            public void Inject(object instance) { }
            public bool IsRegistered(Type type) => type == typeof(IAdapted);
        }

        private static void RegisterFactories()
        {
            NexusDI.RegisterConstructorFactory<Mid>(di => new Mid(Param<Leaf>(di, 0, "Mid", "Leaf")));
            NexusDI.RegisterConstructorFactory<Owner>(di => new Owner(Param<Mid>(di, 0, "Owner", "Mid")));
            NexusDI.RegisterConstructorFactory<NeedsMissing>(di => new NeedsMissing(Param<IMissing>(di, 0, "NeedsMissing", "IMissing")));
            NexusDI.RegisterConstructorFactory<NeedsNamed>(di => new NeedsNamed(Param<Leaf>(di, 0, "NeedsNamed", "Leaf", "alt")));
            NexusDI.RegisterConstructorFactory<NeedsLazy>(di => new NeedsLazy(Param<LazyLeaf>(di, 0, "NeedsLazy", "LazyLeaf")));
            NexusDI.RegisterConstructorFactory<NeedsDisposer>(di => new NeedsDisposer(Param<Disposer>(di, 0, "NeedsDisposer", "Disposer")));
            NexusDI.RegisterConstructorFactory<CycRoot>(di => new CycRoot(Param<CycA>(di, 0, "CycRoot", "CycA")));
            NexusDI.RegisterConstructorFactory<CycA>(di => new CycA(Param<CycB>(di, 0, "CycA", "CycB")));
            NexusDI.RegisterConstructorFactory<CycB>(di => new CycB(null));
            NexusDI.RegisterConstructorFactory<NeedsTagged>(di => new NeedsTagged(Param<Tagged>(di, 0, "NeedsTagged", "Tagged")));
            NexusDI.RegisterConstructorFactory<NeedsAdapted>(di => new NeedsAdapted(Param<IAdapted>(di, 0, "NeedsAdapted", "IAdapted")));
            NexusDI.RegisterConstructorFactory<NeedsVariant>(di => new NeedsVariant(Param<IVariant>(di, 0, "NeedsVariant", "IVariant")));
            NexusDI.RegisterConstructorFactory<LateLeaf>(di => new LateLeaf("binder 1"));
            NexusDI.RegisterConstructorFactory<NeedsLate>(di => new NeedsLate(Param<LateLeaf>(di, 0, "NeedsLate", "LateLeaf")));
            NexusDI.RegisterConstructorFactory<NeedsContainer>(di => new NeedsContainer(Param<NexusDI>(di, 0, "NeedsContainer", "NexusDI")));
        }

        /// <summary>What one run of a scenario did, line by line; instances are labelled in order of first appearance.</summary>
        private sealed class Trace
        {
            public readonly List<string> Lines = new();
            private readonly SvcLoggerRecorder _logger;
            private readonly Dictionary<object, string> _labels = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<string, int> _counts = new();
            private int _flushed;

            public Trace(SvcLoggerRecorder logger) { _logger = logger; }

            public string Label(object instance)
            {
                if (instance == null) return "null";
                if (_labels.TryGetValue(instance, out var label)) return label;
                string type = instance.GetType().Name;
                _counts.TryGetValue(type, out int n);
                _counts[type] = ++n;
                return _labels[instance] = $"{type}#{n}";
            }

            private string Describe(object instance) => instance switch
            {
                Holder holder => $"{Label(holder)}({Describe(holder.Value)})",
                Tagged tagged => $"{Label(tagged)}[{tagged.Tag}]",
                LateLeaf late => $"{Label(late)}[{late.Via}]",
                _ => Label(instance),
            };

            public void Add(string line)
            {
                FlushWarnings();
                Lines.Add(line);
            }

            public void Step(string what, Func<object> action)
            {
                FlushWarnings();
                int resolvesBefore = NexusProfiler.ResolvesPerformed.Value;
                string result;
                try { result = Describe(action()); }
                catch (Exception ex) { result = $"{ex.GetType().Name}: {ex.Message}"; }
                Lines.Add($"{what} -> {result} | resolves +{NexusProfiler.ResolvesPerformed.Value - resolvesBefore}");
                FlushWarnings();
            }

            private void FlushWarnings()
            {
                for (; _flushed < _logger.Messages.Count; _flushed++)
                    Lines.Add("  warn: " + _logger.Messages[_flushed]);
            }
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[CtorParam] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("CtorParam", name, ok, detail);
            if (!ok) _failures++;
        }

        /// <summary>Runs <paramref name="body"/> through both parameter paths and reports whether the traces agree.</summary>
        private static void Scenario(string name, Action<Trace> body)
        {
            List<string> Record(bool reference)
            {
                s_reference = reference;
                var ctx = NexusTestHarness.CreateContext();
                var logger = new SvcLoggerRecorder();
                ctx.Context.Container.BindInstance<ILoggerService>(logger);
                var trace = new Trace(logger);
                try
                {
                    RegisterFactories();
                    body(trace);
                }
                catch (Exception ex)
                {
                    trace.Add($"scenario threw {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Disposer.OnConstruct = null;
                    ctx.Dispose();
                }
                return trace.Lines;
            }

            var current = Record(reference: false);
            var reference = Record(reference: true);
            int first = 0;
            while (first < current.Count && first < reference.Count && current[first] == reference[first]) first++;
            bool same = first == current.Count && first == reference.Count;
            Report(name, same, same
                ? $"{current.Count} lines agree; last: {current[current.Count - 1]}"
                : $"line {first + 1}: current '{(first < current.Count ? current[first] : "<end>")}' vs reference '{(first < reference.Count ? reference[first] : "<end>")}'");
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[CtorParam] GENERATED FACTORY PARAMETERS: CURRENT PATH vs THE PATH BEFORE #111");
            Console.WriteLine("===============================================================================");
            try
            {
                Scenario("CP1. Transient_Three_Node_Graph", t =>
                {
                    var di = new NexusDI();
                    di.Bind<Leaf>(Lifetime.Transient);
                    di.Bind<Mid>(Lifetime.Transient);
                    di.Bind<Owner>(Lifetime.Transient);
                    for (int i = 0; i < 3; i++) t.Step("resolve Owner", () => di.Resolve<Owner>());
                });

                Scenario("CP2. Strict_Missing_Parameter_Throws", t =>
                {
                    var di = new NexusDI { StrictInjection = true };
                    di.Bind<NeedsMissing>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsMissing", () => di.Resolve<NeedsMissing>());
                });

                Scenario("CP3. NonStrict_Missing_Parameter_Warns_And_Passes_Null", t =>
                {
                    var di = new NexusDI();
                    di.Bind<NeedsMissing>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsMissing", () => di.Resolve<NeedsMissing>());
                });

                Scenario("CP4. Named_Parameter_Present_And_Missing_Never_Takes_The_Default", t =>
                {
                    var di = new NexusDI();
                    di.Bind<Leaf>(Lifetime.Transient);
                    di.Bind<Leaf, AltLeaf>("alt", Lifetime.Transient);
                    di.Bind<NeedsNamed>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsNamed", () => di.Resolve<NeedsNamed>());
                    var unnamed = new NexusDI();
                    unnamed.Bind<Leaf>(Lifetime.Transient);
                    unnamed.Bind<NeedsNamed>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsNamed without 'alt'", () => unnamed.Resolve<NeedsNamed>());
                });

                Scenario("CP5. Parameter_Bound_Only_In_The_Parent", t =>
                {
                    var parent = new NexusDI();
                    parent.Bind<Leaf>(Lifetime.Transient);
                    parent.Bind<IVariant, VariantOne>(Lifetime.Scoped);
                    var child = parent.CreateChildScope();
                    child.Bind<Mid>(Lifetime.Transient);
                    child.Bind<NeedsVariant>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("child resolve Mid (transient Leaf in parent)", () => child.Resolve<Mid>());
                    for (int i = 0; i < 2; i++) t.Step("child resolve NeedsVariant (scoped in parent)", () => child.Resolve<NeedsVariant>());
                });

                Scenario("CP6. Lazy_Marked_Transient_Parameter_Notifies_Each_Instance", t =>
                {
                    var di = new NexusDI();
                    di.Bind<LazyLeaf>(Lifetime.Transient);
                    di.MarkLazyService(typeof(LazyLeaf));
                    di.Bind<NeedsLazy>(Lifetime.Transient);
                    di.LazyServiceResolvedCallback = service => t.Add("  lazy: " + t.Label(service));
                    for (int i = 0; i < 3; i++) t.Step("resolve NeedsLazy", () => di.Resolve<NeedsLazy>());
                });

                Scenario("CP7. Disposal_During_And_After_Construction", t =>
                {
                    var di = new NexusDI();
                    di.Bind<Disposer>(Lifetime.Transient);
                    di.Bind<NeedsDisposer>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsDisposer", () => di.Resolve<NeedsDisposer>());
                    Disposer.OnConstruct = di.Dispose;
                    t.Step("resolve NeedsDisposer, the parameter disposes its container", () => di.Resolve<NeedsDisposer>());
                    Disposer.OnConstruct = null;

                    var direct = new NexusDI();
                    direct.Bind<Leaf>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("parameter Leaf", () => Param<Leaf>(direct, 0, "Direct", "Leaf"));
                    direct.Dispose();
                    t.Step("parameter Leaf after Dispose", () => Param<Leaf>(direct, 0, "Direct", "Leaf"));

                    var parent = new NexusDI();
                    parent.Bind<Leaf>(Lifetime.Transient);
                    var child = parent.CreateChildScope();
                    child.Bind<Mid>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("child resolve Mid", () => child.Resolve<Mid>());
                    parent.Dispose();
                    t.Step("child resolve Mid, parent disposed", () => child.Resolve<Mid>());
                });

                Scenario("CP8. Cycle_Through_Warm_Parameters_After_A_Binder_Change", t =>
                {
                    var di = new NexusDI();
                    di.Bind<CycRoot>(Lifetime.Transient);
                    di.Bind<CycA>(Lifetime.Transient);
                    di.Bind<CycB>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve CycRoot (CycB does not ask for CycA)", () => di.Resolve<CycRoot>());
                    NexusDI.RegisterConstructorFactory<CycB>(c => new CycB(Param<CycA>(c, 0, "CycB", "CycA")));
                    t.Step("resolve CycRoot (CycB asks for CycA)", () => di.Resolve<CycRoot>());
                });

                Scenario("CP9. WithParameter_On_The_Parameter_Binding", t =>
                {
                    var di = new NexusDI();
                    di.BindFluent<Tagged>().AsTransient().WithParameter(typeof(string), "from WithParameter");
                    di.Bind<NeedsTagged>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsTagged", () => di.Resolve<NeedsTagged>());
                });

                Scenario("CP10. External_Adapter_Alone_And_Behind_A_Local_Binding", t =>
                {
                    var adapterOnly = new NexusDI { ExternalAdapter = new Adapter() };
                    adapterOnly.Bind<NeedsAdapted>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsAdapted (adapter only)", () => adapterOnly.Resolve<NeedsAdapted>());
                    var local = new NexusDI { ExternalAdapter = new Adapter() };
                    local.Bind<IAdapted, Adapted>(Lifetime.Transient);
                    local.Bind<NeedsAdapted>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsAdapted (local wins)", () => local.Resolve<NeedsAdapted>());
                });

                Scenario("CP11. Rebinding_The_Parameter_Between_Resolves", t =>
                {
                    var di = new NexusDI();
                    di.Bind<IVariant, VariantOne>(Lifetime.Transient);
                    di.Bind<NeedsVariant>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsVariant", () => di.Resolve<NeedsVariant>());
                    di.Bind<IVariant, VariantTwo>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsVariant after rebind", () => di.Resolve<NeedsVariant>());
                });

                Scenario("CP12. Binder_Registered_After_The_First_Resolve", t =>
                {
                    var di = new NexusDI();
                    di.Bind<LateLeaf>(Lifetime.Transient);
                    di.Bind<NeedsLate>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsLate", () => di.Resolve<NeedsLate>());
                    NexusDI.RegisterConstructorFactory<LateLeaf>(c => new LateLeaf("binder 2"));
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsLate after a new binder", () => di.Resolve<NeedsLate>());
                });

                Scenario("CP13. NexusDI_As_A_Parameter", t =>
                {
                    var di = new NexusDI();
                    t.Add("container " + t.Label(di));
                    di.Bind<NeedsContainer>(Lifetime.Transient);
                    for (int i = 0; i < 2; i++) t.Step("resolve NeedsContainer", () => di.Resolve<NeedsContainer>());
                });
            }
            finally
            {
                s_reference = false;
                try { NexusRuntime.Reset(); NexusDI.ClearGeneratedRegistrations(); } catch (Exception ex) { Console.WriteLine($"[CtorParam] teardown: {ex.Message}"); }
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[CtorParam] ALL CONSTRUCTOR PARAMETER TESTS PASSED ✓"
                : $"[CtorParam] {_failures} CONSTRUCTOR PARAMETER TEST(S) FAILED ✗");
            return _failures;
        }
    }
}
