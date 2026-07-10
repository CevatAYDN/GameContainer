using NUnit.Framework;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    [Category("RequiresPlayMode")]
    public class ViewBindingTests
    {
        private Context _context;

        public class TestView : MonoBehaviour, IView
        {
            public IContext BoundContext { get; private set; }
            public bool IsBound { get; private set; }

            public void Bind(IContext context)
            {
                BoundContext = context;
                IsBound = true;
            }

            public void Unbind()
            {
                BoundContext = null;
                IsBound = false;
            }
        }

        public class MockMediator : IMediator
        {
            public object BoundView { get; private set; }
            public ISignalBus SignalBus { get; private set; }
            public bool IsBound { get; private set; }
            public string[] Events { get; set; }

            public void Bind(object view, ISignalBus signalBus)
            {
                Events?.GetType();
                Events?.Method;
                BoundView = view;
                SignalBus = signalBus;
                IsBound = true;
            }

            public void Unbind()
            {
                BoundView = null;
                SignalBus = null;
                IsBound = false;
            }
        }

        [Mediator(typeof(MockMediator))]
        public class DecoratedView : TestView
        {
        }

        public class OrderTrackingView : TestView
        {
            public readonly System.Collections.Generic.List<string> Events = new System.Collections.Generic.List<string>();

            public new void Bind(IContext context)
            {
                Events.Add("view-bind");
                base.Bind(context);
            }

            public new void Unbind()
            {
                Events.Add("view-unbind");
                base.Unbind();
            }
        }

        [Mediator(typeof(MockMediator))]
        public class OrderedDecoratedView : OrderTrackingView
        {
        }

        [SetUp]
        public void Setup()
        {
            _context = new Context();
        }

        [TearDown]
        public void TearDown()
        {
            _context.Dispose();
        }

        [Test]
        public void RegisterView_BindsView()
        {
            var go = new GameObject();
            var view = go.AddComponent<TestView>();

            _context.RegisterView(view);

            Assert.IsTrue(view.IsBound);
            Assert.AreSame(_context, view.BoundContext);

            _context.UnregisterView(view);
            Assert.IsFalse(view.IsBound);
            
            Object.DestroyImmediate(go);
        }

        [Test]
        public void RegisterView_WithoutMediatorAttribute_StillBindsContext()
        {
            var go = new GameObject();
            var view = go.AddComponent<TestView>();
            var binder = _context.Resolve<ViewBinder>();

            binder.RegisterView(view);

            Assert.IsTrue(view.IsBound);
            Assert.AreSame(_context, view.BoundContext);

            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeMediators = activeMediatorsField?.GetValue(binder) as System.Collections.IDictionary;
            Assert.IsNotNull(activeMediators);
            Assert.IsFalse(activeMediators.Contains(view));

            binder.UnregisterView(view);
            Assert.IsFalse(view.IsBound);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void RegisterView_WithMediator_InstantiatesAndBindsMediator()
        {
            var go = new GameObject();
            var view = go.AddComponent<DecoratedView>();

            // ViewBinder is registered in Context constructor
            var binder = _context.Resolve<ViewBinder>();

            _context.RegisterView(view);

            // Access internal active mediators via reflection for testing
            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeMediators = activeMediatorsField?.GetValue(binder) as System.Collections.IDictionary;

            Assert.IsNotNull(activeMediators);
            Assert.IsTrue(activeMediators.Contains(view));

            var mediator = activeMediators[view] as MockMediator;
            Assert.IsNotNull(mediator);
            Assert.IsTrue(mediator.IsBound);
            Assert.AreSame(view, mediator.BoundView);
            Assert.AreSame(_context.SignalBus, mediator.SignalBus);

            _context.UnregisterView(view);
            Assert.IsFalse(mediator.IsBound);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void RegisterView_BindsViewBeforeMediator()
        {
            var go = new GameObject();
            var view = go.AddComponent<OrderedDecoratedView>();

            var binder = _context.Resolve<ViewBinder>();
            binder.RegisterView(view);

            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeMediators = activeMediatorsField?.GetValue(binder) as System.Collections.IDictionary;
            var mediator = activeMediators?[view] as MockMediator;

            Assert.IsNotNull(mediator);
            Assert.AreEqual(1, view.Events.Count);
            Assert.AreEqual("view-bind", view.Events[0]);
            Assert.IsTrue(view.IsBound);
            Assert.IsTrue(mediator.IsBound);

            binder.UnregisterView(view);
            Assert.AreEqual(2, view.Events.Count);
            Assert.AreEqual("view-unbind", view.Events[1]);

            Object.DestroyImmediate(go);
        }
    }
}
