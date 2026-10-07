using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Editor;

namespace Nexus.Core.Tests
{
    public class IntegrationReadinessTests
    {
        public interface IDependency { }
        public class Dependency : IDependency { }
        public class NeedsDependency
        {
            public static int Constructors;
            public NeedsDependency(IDependency dependency) { Constructors++; }
        }
        public class NamedConsumer
        {
            public NamedConsumer([Inject(Name = "game")] IDependency dependency) { }
        }
        public class OptionalConsumer
        {
            public OptionalConsumer(IDependency dependency = null) { }
        }
        public class InvalidConstructor
        {
            [Inject] public InvalidConstructor(IDependency dependency) { }
            [Construct] public InvalidConstructor(Dependency dependency) { }
        }
        public class CycleA { public CycleA(CycleB dependency) { } }
        public class CycleB { public CycleB(CycleA dependency) { } }
        public class NamedChain
        {
            public NamedChain([Inject(Name = "leaf")] NamedChain next = null) { }
        }
        public class LazyConsumer { [Inject] public LazyInjection<LazyConsumer> Next; }
        public class PrivateConstructorOnly { private PrivateConstructorOnly(IDependency dependency) { } }
        public class ThrowingConstructor { public ThrowingConstructor() { throw new ArgumentException("constructor failure"); } }
        public class GraphLeaf { }
        public class GraphMiddle { public readonly GraphLeaf Leaf; public GraphMiddle(GraphLeaf leaf) { Leaf = leaf; } }
        public class GraphRoot { public readonly GraphMiddle Middle; public GraphRoot(GraphMiddle middle) { Middle = middle; } }
        public class GenericConsumer<T> { public readonly T Value; public GenericConsumer(T value) { Value = value; } }
        private sealed class Adapter : IDependencyAdapter
        {
            public object Resolve(Type type) => new Dependency();
            public void Inject(object instance) { }
            public bool IsRegistered(Type type) => type == typeof(IDependency);
        }
        public readonly struct WarmSignal { }
        public class WarmCommand : ICommand<WarmSignal>
        {
            public static int Executions;
            public void Execute(WarmSignal signal) { Executions++; }
        }
        public class FactoryCommand : ICommand<WarmSignal>
        {
            public FactoryCommand(string option) { Option = option; }
            public readonly string Option;
            public void Execute(WarmSignal signal) { }
        }

        [SetUp]
        public void SetUp() { NeedsDependency.Constructors = 0; WarmCommand.Executions = 0; }

        [Test]
        public void Validation_DoesNotConstructOrInvokeFactories()
        {
            using var di = new NexusDI();
            di.Bind<NeedsDependency>();
            int calls = 0;
            di.BindFactory<IDependency>(() => { calls++; return new Dependency(); });
            Assert.IsEmpty(di.ValidateBindings());
            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, NeedsDependency.Constructors);
        }

        [Test]
        public void Validation_ConstructorOverridesSatisfyDependencies()
        {
            using var di = new NexusDI();
            di.BindFluent<NeedsDependency>().WithParameter<IDependency>(new Dependency());
            Assert.IsEmpty(di.ValidateBindings());
            Assert.IsNotNull(di.Resolve<NeedsDependency>());
        }

        [Test]
        public void Validation_IncompatibleOverrideIsReported()
        {
            using var di = new NexusDI();
            di.BindFluent<NeedsDependency>().WithParameter(typeof(IDependency), "wrong type");
            Assert.IsTrue(di.ValidateBindings().Any(x => x.IssueType == DiValidationIssueType.InvalidBinding));
        }

        [Test]
        public void Validation_OpaqueSuppliedInstanceDoesNotRequireItsOriginalConstructor()
        {
            using var di = new NexusDI();
            di.BindInstance(new NeedsDependency(new Dependency()));
            Assert.IsEmpty(di.ValidateBindings());
        }

        [Test]
        public void Validation_NamedOnlyConsumerIsInspected()
        {
            using var di = new NexusDI();
            di.Bind<NamedConsumer>("consumer");
            var issue = di.ValidateBindings().Single();
            Assert.AreEqual(DiValidationIssueType.MissingConstructorDependency, issue.IssueType);
            StringAssert.Contains("game", issue.Message);
            di.Bind<IDependency, Dependency>("game");
            Assert.IsEmpty(di.ValidateBindings());
        }

        [Test]
        public void Validation_ExternalAdapterSatisfiesDefaultButCannotSatisfyNamedBinding()
        {
            using var di = new NexusDI { ExternalAdapter = new Adapter() };
            di.Bind<NeedsDependency>();
            Assert.IsEmpty(di.ValidateBindings());
            Assert.IsTrue(di.IsRegistered(typeof(IDependency)));
            Assert.IsFalse(di.IsRegistered(typeof(IDependency), "game"));
            Assert.IsNull(di.TryResolve<IDependency>("game"));
            di.Bind<NamedConsumer>();
            Assert.AreEqual(1, di.ValidateBindings().Count);
        }

        [Test]
        public void Validation_ParentIsAnalyzedInParentScope()
        {
            using var parent = new NexusDI();
            parent.Bind<NeedsDependency>();
            using var child = new NexusDI(parent);
            child.Bind<IDependency, Dependency>();
            Assert.AreEqual(1, child.ValidateBindings().Count);
            parent.Bind<IDependency, Dependency>();
            Assert.IsEmpty(child.ValidateBindings());
        }

        [Test]
        public void Validation_EagerCyclesReportAUsefulChainBeforeResolve()
        {
            using var di = new NexusDI();
            di.Bind<CycleA>(); di.Bind<CycleB>();
            var cycle = di.ValidateBindings().Single(x => x.IssueType == DiValidationIssueType.CircularDependency);
            StringAssert.Contains(nameof(CycleA), cycle.Message);
            StringAssert.Contains(nameof(CycleB), cycle.Message);
        }

        [Test]
        public void Validation_DifferentNamedBindingsOfSameTypeAreNotACycle()
        {
            using var di = new NexusDI();
            di.Bind<NamedChain>("root");
            di.BindFactory<NamedChain>("leaf", () => new NamedChain());
            Assert.IsEmpty(di.ValidateBindings());
        }

        [Test]
        public void Validation_OptionalDefaultsAndLazyEdgesAreNotMissingOrEagerCycles()
        {
            using var di = new NexusDI();
            di.Bind<OptionalConsumer>(); di.Bind<LazyConsumer>();
            Assert.IsEmpty(di.ValidateBindings());
        }

        [Test]
        public void Validation_InvalidInjectionMetadataIsAnIssue()
        {
            using var di = new NexusDI();
            di.Bind<InvalidConstructor>();
            Assert.AreEqual(DiValidationIssueType.InvalidBinding, di.ValidateBindings().Single().IssueType);
        }

        [Test]
        public void Validation_PrivateParameterizedOnlyConstructorRequiresFactory()
        {
            using var di = new NexusDI();
            di.Bind<PrivateConstructorOnly>();
            Assert.AreEqual(DiValidationIssueType.InvalidBinding, di.ValidateBindings().Single().IssueType);
        }

        [Test]
        public void Validation_ReflectionBindingsCheckEveryAliasAndAllowDefaultValueTypes()
        {
            using var di = new NexusDI();
            di.Bind(typeof(int));
            Assert.IsEmpty(di.ValidateBindings());
            Assert.AreEqual(0, di.Resolve(typeof(int)));
            di.BindMultiple(new[] { typeof(Dependency), typeof(IDisposable) }, typeof(Dependency));
            Assert.AreEqual(DiValidationIssueType.InvalidBinding, di.ValidateBindings().Single().IssueType);
        }

        [Test]
        public void Validation_NamedCaptiveDependencyUsesNamedLifetime()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, Dependency>();
            di.Bind<IDependency, Dependency>("game", Lifetime.Transient);
            di.Bind<NamedConsumer>();
            Assert.AreEqual(DiValidationIssueType.CaptiveDependency, di.ValidateBindings().Single().IssueType);
        }

        [Test]
        public void Validation_ImplicitContainerIsSatisfiable()
        {
            using var di = new NexusDI();
            Assert.IsTrue(di.IsRegistered(typeof(NexusDI)));
            Assert.AreSame(di, di.TryResolve<NexusDI>());
        }

        [Test]
        public void ConstructorPipeline_PreservesTheOriginalException()
        {
            using var di = new NexusDI();
            di.Bind<ThrowingConstructor>(Lifetime.Transient);
            StringAssert.Contains("constructor failure", Assert.Throws<ArgumentException>(() => di.Resolve<ThrowingConstructor>()).Message);
        }

        [Test]
        public void ConstructorCompilation_DoesNotEmitAnOpenGenericMethodContext()
        {
            var open = NexusDI.GetOrCreateInjectMetadata(typeof(GenericConsumer<>));
            Assert.IsNull(open.CompiledConstructor);
            using var di = new NexusDI();
            di.Bind<IDependency, Dependency>();
            di.Bind<GenericConsumer<IDependency>>();
            Assert.IsNotNull(di.Resolve<GenericConsumer<IDependency>>().Value);
        }

        [Test]
        public void TransientGraph_CreatesOnlyItsThreeObjectsOnMonoAfterWarmup()
        {
#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            Assert.Ignore("Runtime constructor compilation is unavailable on AOT; generated factories require their own player measurement.");
#else
            using var di = new NexusDI();
            di.Bind<GraphLeaf>(Lifetime.Transient);
            di.Bind<GraphMiddle>(Lifetime.Transient);
            di.Bind<GraphRoot>(Lifetime.Transient);
            for (int i = 0; i < 100; i++) di.Resolve<GraphRoot>();
            using var probe = new GcAllocationProbe();
            for (int i = 0; i < 1000; i++) di.Resolve<GraphRoot>();
            long samples = probe.Stop();
#if UNITY_5_3_OR_NEWER
            Assert.AreEqual(3000, samples, "Only three constructed objects per graph; no argument arrays or constructor boxing.");
#else
            Assert.AreEqual(1, samples, "Transient objects intentionally allocate.");
#endif
#endif
        }

        [Test]
        public void Builder_PreservesTransientCommandFactory()
        {
            using var di = new NexusDI();
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            di.BindFactory(() => new FactoryCommand("configured"));
            var builder = new ContextBuilder(di, bus);
            builder.BindCommand<WarmSignal, FactoryCommand>();
            Assert.IsEmpty(builder.Validate());
            Assert.AreEqual("configured", di.Resolve<FactoryCommand>().Option);
        }

        [Test]
        public void Prewarm_IsIdempotentAndNeverExecutesCommandsOrOneShots()
        {
            using var di = new NexusDI();
            var pools = new CommandPoolManager(di, initialSize: 0, maxSize: 8);
            using var bus = new SignalBus(di, pools, new MockContext());
            bus.RegisterCommand(typeof(WarmSignal), typeof(WarmCommand), ExecutionMode.Sequential, 0, false, oneShot: true);
            int subscribed = 0;
            using var sub = bus.Subscribe<WarmSignal>(_ => subscribed++);
            bus.Prewarm<WarmSignal>(8); bus.Prewarm<WarmSignal>(8);
            var stats = pools.GetPoolStatsSnapshot().Single();
            Assert.AreEqual(8, stats.Available);
            Assert.AreEqual(8, stats.TotalPrewarmed);
            Assert.AreEqual(0, stats.TotalGets);
            Assert.AreEqual(0, WarmCommand.Executions);
            Assert.AreEqual(0, subscribed);
            Assert.IsTrue(bus.HasCommandHandler<WarmSignal>());
            bus.Fire(new WarmSignal());
            Assert.AreEqual(1, WarmCommand.Executions);
            Assert.AreEqual(1, subscribed);
            Assert.IsFalse(bus.HasCommandHandler<WarmSignal>());
            Assert.AreEqual(0, pools.GetPoolStatsSnapshot().Single().TotalCreated);
            pools.Clear();
        }

        [Test]
        public void Prewarm_RejectsInvalidCapacityAndRepeatedFactoryInstance()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandPool(typeof(WarmCommand), () => new WarmCommand(), 9, 8));
            var instance = new WarmCommand();
            var pool = new CommandPool(typeof(WarmCommand), () => instance, 0, 8);
            Assert.Throws<InvalidOperationException>(() => pool.Prewarm(2));
            Assert.AreEqual(1, pool.GetStats().Available);
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Prewarm(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Prewarm(9));
        }

        [Test]
        public void Prewarm_RetriesAfterFactoryFailureWithoutCorruptingPool()
        {
            bool fail = true;
            var pool = new CommandPool(typeof(WarmCommand), () => fail ? throw new InvalidOperationException("failed") : new WarmCommand(), 0, 8);
            Assert.Throws<InvalidOperationException>(() => pool.Prewarm(2));
            fail = false;
            Assert.AreEqual(2, pool.Prewarm(2));
            Assert.AreNotSame(pool.Get(), pool.Get());
        }

        [Test]
        public void Prewarm_RejectsFactoryReturningAnActiveLease()
        {
            var instance = new WarmCommand();
            var pool = new CommandPool(typeof(WarmCommand), () => instance, 1, 8);
            var active = pool.Get();
            pool.Clear(); // Clear removes idle objects; an active lease remains owned.
            Assert.Throws<InvalidOperationException>(() => pool.Prewarm(1));
            Assert.Throws<InvalidOperationException>(() => pool.Get());
            pool.Return(active);
            Assert.AreEqual(1, pool.GetStats().Available);
            Assert.AreSame(active, pool.Get());
        }

        [Test]
        public void Localization_LoadsUpmAssetAndWizardInTurkish()
        {
            string previous = NexusLang.CurrentLocale;
            var wizard = new NexusSetupWizard();
            try
            {
                Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.nexus.core/Editor/Locales/tr.json"));
                NexusLang.LoadLocale("tr");
                Assert.AreEqual("Kontrol Paneli", NexusLang.Get("dashboard"));
                Assert.AreEqual("Kurulum Sihirbazı", wizard.DisplayName);
                var view = wizard.CreateView();
                Assert.AreEqual("Entegrasyon yolunuzu seçin", view.Q<Button>("nexus-integration-guide").text);
                wizard.OnDisable();
                Assert.IsNotNull(wizard.CreateView());
            }
            finally { wizard.OnDisable(); NexusLang.LoadLocale(previous); }
        }
    }
}
