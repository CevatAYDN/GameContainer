using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests
{
    /// <summary>
    /// Phase 3 — PostContext lifecycle tests.
    ///
    /// Verifies that:
    /// - PC1: IPostContextLifecycle.OnPostContext fires after all standard lifecycle phases
    /// - PC2: PostContext fires in context registration order across multiple contexts
    /// - PC3: No PostContext when no lifecycle opts in (graceful non-participation)
    /// - PC4: Cross-context wiring through PostContext (StrangeIoC-style)
    /// - PC5: PostContext receives a valid ContextBuilder for late bindings
    /// </summary>
    [TestFixture]
    public class PostContextTests
    {
        // ─── Test lifecycle implementations ───

        /// <summary>Tracks every phase called, in order.</summary>
        private class PhaseTrackerLifecycle : IContextLifecycle, IPostContextLifecycle
        {
            public readonly List<string> Phases = new();

            public void OnConfigure(IContextBuilder builder) { Phases.Add(nameof(OnConfigure)); }
            public ValueTask OnInitializeAsync(CancellationToken ct) { Phases.Add(nameof(OnInitializeAsync)); return default; }
            public ValueTask OnStartAsync(CancellationToken ct) { Phases.Add(nameof(OnStartAsync)); return default; }
            public void OnPostContext(IContextBuilder builder) { Phases.Add(nameof(OnPostContext)); }
            public void OnDispose() { }
        }

        /// <summary>Appends its Id to a shared static list when OnPostContext fires.</summary>
        private class PostContextCounter : IContextLifecycle, IPostContextLifecycle
        {
            public static readonly List<int> CallOrder = new();
            public readonly int Id;

            public PostContextCounter(int id) { Id = id; }

            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnPostContext(IContextBuilder builder) { CallOrder.Add(Id); }
            public void OnDispose() { }
        }

        /// <summary>Standard lifecycle WITHOUT IPostContextLifecycle — PostContext must not fire.</summary>
        private class NoPostLifecycle : IContextLifecycle
        {
            public bool PostContextCalled;
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        public interface ICrossService { }
        private class CrossService : ICrossService { }

        /// <summary>Binds ICrossService as cross-boundary during OnConfigure.</summary>
        private class CrossBindLifecycle : IContextLifecycle, IPostContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder)
            {
                builder.BindCrossBoundary<ICrossService, CrossService>();
            }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnPostContext(IContextBuilder builder) { }
            public void OnDispose() { }
        }

        /// <summary>
        /// Resolves ICrossService from a sibling context during PostContext,
        /// proving cross-context wiring works in the PostContext phase.
        /// </summary>
        private class CrossResolveLifecycle : IContextLifecycle, IPostContextLifecycle
        {
            public bool Resolved;
            private readonly IContext _targetContext;

            public CrossResolveLifecycle(IContext targetContext) { _targetContext = targetContext; }

            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnPostContext(IContextBuilder builder)
            {
                Resolved = _targetContext.ResolveCrossBoundary<ICrossService>() != null;
            }
            public void OnDispose() { }
        }

        /// <summary>Binds a late string instance during PostContext to prove the builder is valid.</summary>
        private class LateBindLifecycle : IContextLifecycle, IPostContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnPostContext(IContextBuilder builder)
            {
                builder.BindInstance("PostContextLateBinding");
            }
            public void OnDispose() { }
        }

        // ─── Setup / Teardown ───

        [SetUp]
        public void Setup()
        {
            PostContextCounter.CallOrder.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            PostContextCounter.CallOrder.Clear();
            NexusRuntime.Reset();
        }

        // ─── PC1: PostContext fires after ALL standard lifecycle phases ───

        [Test]
        public async Task PostContext_FiresAfterStandardLifecycle()
        {
            var lifecycle = new PhaseTrackerLifecycle();
            var context = new Context();
            context.Container.BindInstance<IContextLifecycle>(lifecycle);
            context.Configure();

            // Run standard lifecycle
            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);

            // Before PostContext: exactly 3 phases (Configure, Initialize, Start)
            Assert.AreEqual(3, lifecycle.Phases.Count, "Standard lifecycle must complete 3 phases before PostContext");
            Assert.AreEqual(nameof(IContextLifecycle.OnConfigure), lifecycle.Phases[0], "Phase order: Configure first");
            Assert.AreEqual(nameof(IContextLifecycle.OnInitializeAsync), lifecycle.Phases[1], "Phase order: Initialize second");
            Assert.AreEqual(nameof(IContextLifecycle.OnStartAsync), lifecycle.Phases[2], "Phase order: Start third");

            // Fire PostContext
            await NexusRuntime.FinalizeInitializationAsync(CancellationToken.None);

            Assert.AreEqual(4, lifecycle.Phases.Count, "PostContext must add a 4th phase");
            Assert.AreEqual(nameof(IPostContextLifecycle.OnPostContext), lifecycle.Phases[3],
                "PostContext must fire after all standard phases");

            context.Dispose();
        }

        // ─── PC2: PostContext fires in context registration order ───

        [Test]
        public async Task PostContext_MultipleContexts_FiresInRegistrationOrder()
        {
            var lifecycle1 = new PostContextCounter(1);
            var lifecycle2 = new PostContextCounter(2);

            // Create and initialize context 1 first → registered first
            var ctx1 = new Context();
            ctx1.Container.BindInstance<IContextLifecycle>(lifecycle1);
            ctx1.Configure();
            await ctx1.InitializeLifecycleAsync(ctx1.ConfiguredLifecycles, CancellationToken.None);

            // Create and initialize context 2 second → registered second
            var ctx2 = new Context();
            ctx2.Container.BindInstance<IContextLifecycle>(lifecycle2);
            ctx2.Configure();
            await ctx2.InitializeLifecycleAsync(ctx2.ConfiguredLifecycles, CancellationToken.None);

            // Fire PostContext for all contexts
            await NexusRuntime.FinalizeInitializationAsync(CancellationToken.None);

            Assert.AreEqual(2, PostContextCounter.CallOrder.Count,
                "Both contexts must receive PostContext");
            Assert.AreEqual(1, PostContextCounter.CallOrder[0],
                "Context 1 (registered first) must receive PostContext before context 2");
            Assert.AreEqual(2, PostContextCounter.CallOrder[1],
                "Context 2 (registered second) must receive PostContext after context 1");

            ctx1.Dispose();
            ctx2.Dispose();
        }

        // ─── PC3: No PostContext when lifecycle does not implement IPostContextLifecycle ───

        [Test]
        public async Task PostContext_NotCalled_WhenLifecycleDoesNotImplement()
        {
            var lifecycle = new NoPostLifecycle();
            var context = new Context();
            context.Container.BindInstance<IContextLifecycle>(lifecycle);
            context.Configure();
            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);

            // Must not throw and must not call PostContext
            Assert.DoesNotThrowAsync(
                async () => await NexusRuntime.FinalizeInitializationAsync(CancellationToken.None),
                "FinalizeInitializationAsync must not throw when no lifecycle opts into PostContext");

            context.Dispose();
        }

        // ─── PC4: Cross-context wiring via PostContext ───

        [Test]
        public async Task PostContext_EnablesCrossContextWiring()
        {
            // Context A: binds ICrossService as cross-boundary visible to sibling/child contexts
            var lifecycleA = new CrossBindLifecycle();
            var ctxA = new Context();
            ctxA.Container.BindInstance<IContextLifecycle>(lifecycleA);
            ctxA.Configure();
            await ctxA.InitializeLifecycleAsync(ctxA.ConfiguredLifecycles, CancellationToken.None);

            // Context B: resolves ICrossService from context A during PostContext
            var lifecycleB = new CrossResolveLifecycle(ctxA);
            var ctxB = new Context();
            ctxB.Container.BindInstance<IContextLifecycle>(lifecycleB);
            ctxB.Configure();
            await ctxB.InitializeLifecycleAsync(ctxB.ConfiguredLifecycles, CancellationToken.None);

            // Fire PostContext — context B's lifecycle resolves cross-boundary from context A
            await NexusRuntime.FinalizeInitializationAsync(CancellationToken.None);

            Assert.IsTrue(lifecycleB.Resolved,
                "Cross-context resolution via PostContext must succeed — " +
                "context B must resolve ICrossService from context A");

            ctxA.Dispose();
            ctxB.Dispose();
        }

        // ─── PC5: PostContext receives valid ContextBuilder for late bindings ───

        [Test]
        public async Task PostContext_ReceivesValidBuilder_ForLateBindings()
        {
            var lifecycle = new LateBindLifecycle();
            var context = new Context();
            context.Container.BindInstance<IContextLifecycle>(lifecycle);
            context.Configure();
            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);

            // Before PostContext: string is NOT resolvable
            var before = context.TryResolve<string>();
            Assert.IsNull(before,
                "Before PostContext, the late binding must not exist in the container");

            // Fire PostContext — lifecycle binds a string instance
            await NexusRuntime.FinalizeInitializationAsync(CancellationToken.None);

            // After PostContext: string IS resolvable
            var after = context.TryResolve<string>();
            Assert.IsNotNull(after,
                "After PostContext, the late binding must be resolvable from the container");
            Assert.AreEqual("PostContextLateBinding", after,
                "The resolved late binding must match the value set in OnPostContext");

            context.Dispose();
        }
    }
}
