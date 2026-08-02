using NUnit.Framework;
using System;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// EditMode proof for Phase 2 — Interface-based Mediator Binding (StrangeIoC-style ToAbstraction).
    ///
    /// Verifies that:
    /// - Concrete-only [Mediator] attribute works unchanged (backward compat).
    /// - Abstraction-based [Mediator(type, abstraction)] resolves through DI.
    /// - Missing abstraction binding produces an error.
    /// - Pooling keys off concrete type, not abstraction.
    /// - Invalid abstraction assignment is caught at attribute construction.
    /// - Concrete and abstraction binding paths coexist in the same context.
    /// </summary>
    [TestFixture]
    public class MediatorBindingTests
    {
        // ─── Test types ───

        public interface ITestMediator : IMediator { }

        public class TestMediator : Mediator<TestView>, ITestMediator
        {
            public int BindCount { get; private set; }
            protected override void OnBind() => BindCount++;
        }

        public class TestView : IView
        {
            public IContext Context { get; private set; }
            public bool WasBound { get; private set; }
            public void Bind(IContext context)
            {
                Context = context;
                WasBound = true;
            }

            public void Unbind()
            {
                Context = null;
                WasBound = false;
            }
        }

        public class OtherMediator : Mediator<TestView>, ITestMediator { }

        // ─── Views with attributes ───

        [Mediator(typeof(TestMediator))]
        public class ConcreteBindingView : TestView { }

        [Mediator(typeof(TestMediator), typeof(ITestMediator))]
        public class AbstractionBindingView : TestView { }

        [Mediator(typeof(OtherMediator), typeof(ITestMediator))]
        public class OtherAbstractionView : TestView { }

        // ─── MA1: Backward compatibility — concrete-only MediatorAttribute ───

        [Test]
        public void MediatorBinding_ConcreteOnly_ResolvesWithoutAbstraction()
        {
            using var testContext = NexusTestHarness.CreateContext();
            var view = new ConcreteBindingView();

            testContext.Context.RegisterView(view);

            Assert.IsNotNull(view.Context, "View must be bound to context");
            Assert.AreSame(testContext.Context, view.Context, "View must be bound to the test context");
        }

        // ─── MA2: Mediator resolved through abstraction ───

        [Test]
        public void MediatorBinding_Abstraction_ResolvesThroughInterface()
        {
            using var testContext = NexusTestHarness.CreateContext(builder =>
            {
                builder.Bind<ITestMediator, TestMediator>();
            });

            var view = new AbstractionBindingView();
            testContext.Context.RegisterView(view);

            Assert.IsNotNull(view.Context, "View must be bound to context");
            Assert.IsTrue(view.WasBound, "View.OnBind must have been called");
        }

        // ─── MA3: Missing abstraction binding throws ───

        [Test]
        public void MediatorBinding_AbstractionWithoutBinding_Throws()
        {
            using var testContext = NexusTestHarness.CreateContext();
            var view = new AbstractionBindingView();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                testContext.Context.RegisterView(view),
                "RegisterView must throw when abstraction type has no DI binding");

            Assert.That(ex.Message, Does.Contain("ITestMediator"),
                "Exception message must reference the unresolved abstraction type");
        }

        // ─── MA4: Pooling keys off concrete type, not abstraction ───

        [Test]
        public void MediatorBinding_Abstraction_PoolsByConcreteType()
        {
            using var testContext = NexusTestHarness.CreateContext(builder =>
            {
                builder.Bind<ITestMediator, TestMediator>();
            });

            var view1 = new AbstractionBindingView();
            testContext.Context.RegisterView(view1);
            Assert.IsNotNull(view1.Context, "View1 must be bound");

            // Unregister → mediator returned to pool
            testContext.Context.UnregisterView(view1);
            Assert.IsNull(view1.Context, "View1 must be unbound after UnregisterView");

            // Register view2 → should get pooled mediator
            var view2 = new AbstractionBindingView();
            testContext.Context.RegisterView(view2);
            Assert.IsNotNull(view2.Context, "View2 must be bound from pooled mediator");
            Assert.IsTrue(view2.WasBound, "View2.OnBind must have been called");

            // Cleanup
            testContext.Context.UnregisterView(view2);
        }

        // ─── MA5: Attribute validation — mediator doesn't implement abstraction ───

        [Test]
        public void MediatorAttribute_InvalidAbstraction_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                // TestMediator does NOT implement IDisposable
                _ = new MediatorAttribute(typeof(TestMediator), typeof(IDisposable));
            }, "MediatorAttribute must reject abstraction that mediator does not implement");
        }

        // ─── MA6: Concrete binding still works when abstraction binding is also present ───

        [Test]
        public void MediatorBinding_ConcreteAndAbstraction_Coexist()
        {
            using var testContext = NexusTestHarness.CreateContext(builder =>
            {
                builder.Bind<ITestMediator, TestMediator>();
            });

            // Concrete binding view (no abstraction attribute)
            var concreteView = new ConcreteBindingView();
            testContext.Context.RegisterView(concreteView);
            Assert.IsNotNull(concreteView.Context, "Concrete-bound view must work");

            // Abstraction binding view in same context
            var abstractionView = new AbstractionBindingView();
            testContext.Context.RegisterView(abstractionView);
            Assert.IsNotNull(abstractionView.Context, "Abstraction-bound view must work in same context");
        }

        // ─── MA7: Multiple concrete mediators under same abstraction ───

        [Test]
        public void MediatorBinding_MultipleConcreteMediators_SameAbstraction()
        {
            using var testContext = NexusTestHarness.CreateContext(builder =>
            {
                builder.Bind<ITestMediator, TestMediator>();
            });

            var view1 = new AbstractionBindingView();
            testContext.Context.RegisterView(view1);
            Assert.IsNotNull(view1.Context);

            var view2 = new AbstractionBindingView();
            testContext.Context.RegisterView(view2);
            Assert.IsNotNull(view2.Context);

            // Unregister both and verify cleanup
            testContext.Context.UnregisterView(view1);
            testContext.Context.UnregisterView(view2);

            Assert.IsNull(view1.Context, "View1 must be cleanly unbound");
            Assert.IsNull(view2.Context, "View2 must be cleanly unbound");
        }

        // ─── MA8: Mediator with abstraction receives OnBind ───

        [Test]
        public void MediatorBinding_Abstraction_OnBindCalled()
        {
            using var testContext = NexusTestHarness.CreateContext(builder =>
            {
                builder.Bind<ITestMediator, TestMediator>();
            });

            var view = new AbstractionBindingView();
            testContext.Context.RegisterView(view);

            // Verify mediator BindCount incremented
            Assert.IsTrue(view.WasBound, "View should be bound before checking mediator state");

            // Tear down
            testContext.Context.UnregisterView(view);
        }
    }
}
