using NUnit.Framework;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
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

            public void Bind(object view, ISignalBus signalBus)
            {
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
    }
}
