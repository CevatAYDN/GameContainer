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

            public virtual void Bind(IContext context)
            {
                BoundContext = context;
                IsBound = true;
            }

            public virtual void Unbind()
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

            public override void Bind(IContext context)
            {
                Events.Add("view-bind");
                base.Bind(context);
            }

            public override void Unbind()
            {
                Events.Add("view-unbind");
                base.Unbind();
            }
        }

        [Mediator(typeof(MockMediator))]
        public class OrderedDecoratedView : OrderTrackingView
        {
        }

        public class ResettableMockMediator : IMediator, IResettable
        {
            public int ResetCount;
            public object BoundView { get; private set; }
            public bool IsBound { get; private set; }

            public void Bind(object view, ISignalBus signalBus)
            {
                BoundView = view;
                IsBound = true;
            }

            public void Unbind()
            {
                BoundView = null;
                IsBound = false;
            }

            public void Reset()
            {
                ResetCount++;
                BoundView = null;
                IsBound = false;
            }
        }

        [Mediator(typeof(ResettableMockMediator))]
        public class ResettableDecoratedView : TestView
        {
        }

        // Base-class reset coverage: Mediator<TView> now implements IResettable, so even a
        // mediator that does NOT opt into IResettable itself gets Reset() on pool return
        // (ClearInjectedReferences) AND pool pop (GetMediator). OnReset() is the hook.
        public class ResetTrackingMediator : Mediator<TestView>
        {
            public int ResetCount;
            protected override void OnReset() { ResetCount++; }
        }

        [Mediator(typeof(ResetTrackingMediator))]
        public class ResetTrackingDecoratedView : TestView
        {
        }

        // Derived-state hygiene: ResetCount proves Reset() RAN, but the actual leak class is
        // private (non-injected) state cached during OnBind — e.g. a per-session event log or
        // a pooled item index. OnReset must clear it so the next binding starts from a clean
        // slate; the test proves the log does not accumulate across pool cycles.
        public class DerivedStateMediator : Mediator<TestView>
        {
            public readonly System.Collections.Generic.List<string> SessionLog = new();
            public int CachedPoolIndex = -1;

            protected override void OnBind()
            {
                SessionLog.Add("bind");
                CachedPoolIndex = 42;
            }

            protected override void OnReset()
            {
                SessionLog.Clear();
                CachedPoolIndex = -1;
            }
        }

        [Mediator(typeof(DerivedStateMediator))]
        public class DerivedStateDecoratedView : TestView
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
        public void GetMediator_ResetsPooledMediatorOnReuse()
        {
            // Regression guard: a mediator popped from the pool must be Reset() BEFORE
            // reuse so stale private state from the previous view session cannot leak
            // into the new binding. ReturnMediator already resets via
            // ClearInjectedReferences; GetMediator must do the same defensively.
            var binder = _context.Resolve<ViewBinder>();

            var go1 = new GameObject();
            var view1 = go1.AddComponent<ResettableDecoratedView>();
            binder.RegisterView(view1);

            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeMediators1 = activeMediatorsField?.GetValue(binder) as System.Collections.IDictionary;
            var mediator = activeMediators1[view1] as ResettableMockMediator;
            Assert.IsNotNull(mediator);
            Assert.AreEqual(0, mediator.ResetCount, "Fresh mediator must not have been reset yet.");

            binder.UnregisterView(view1); // ReturnMediator → ClearInjectedReferences → Reset()
            Object.DestroyImmediate(go1);
            Assert.AreEqual(1, mediator.ResetCount, "Return-to-pool must reset the mediator.");

            var go2 = new GameObject();
            var view2 = go2.AddComponent<ResettableDecoratedView>();
            binder.RegisterView(view2); // GetMediator pops the pooled instance

            var activeMediators2 = activeMediatorsField?.GetValue(binder) as System.Collections.IDictionary;
            var reused = activeMediators2[view2] as ResettableMockMediator;
            Assert.AreSame(mediator, reused, "Pool must reuse the same mediator instance.");
            Assert.AreEqual(2, mediator.ResetCount,
                "GetMediator must Reset() the pooled mediator before handing it out (1 return + 1 get).");
            Assert.IsTrue(reused.IsBound, "Mediator must be rebound after reuse.");

            binder.UnregisterView(view2);
            Object.DestroyImmediate(go2);
        }

        [Test]
        public void MediatorBase_ResetCalledOnPoolReturnAndPop()
        {
            // Regression guard for the base-class IResettable: Mediator<TView> subclasses
            // must be reset on BOTH pool directions without needing to implement
            // IResettable themselves (OnReset() hook is the derived extension point).
            var binder = _context.Resolve<ViewBinder>();
            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var go1 = new GameObject();
            var view1 = go1.AddComponent<ResetTrackingDecoratedView>();
            binder.RegisterView(view1);
            var mediator = ((System.Collections.IDictionary)activeMediatorsField.GetValue(binder))[view1] as ResetTrackingMediator;
            Assert.IsNotNull(mediator);
            Assert.AreEqual(0, mediator.ResetCount, "Fresh mediator must not be reset yet.");

            binder.UnregisterView(view1); // ReturnMediator → ClearInjectedReferences → Reset()
            Object.DestroyImmediate(go1);
            Assert.AreEqual(1, mediator.ResetCount, "Return-to-pool must reset the base mediator.");

            var go2 = new GameObject();
            var view2 = go2.AddComponent<ResetTrackingDecoratedView>();
            binder.RegisterView(view2); // GetMediator pops the pooled instance → Reset()

            var reused = ((System.Collections.IDictionary)activeMediatorsField.GetValue(binder))[view2] as ResetTrackingMediator;
            Assert.AreSame(mediator, reused, "Pool must reuse the same base-mediator instance.");
            Assert.AreEqual(2, mediator.ResetCount,
                "Pool pop must reset the base mediator too (1 return + 1 pop).");

            binder.UnregisterView(view2);
            Object.DestroyImmediate(go2);
        }

        [Test]
        public void MediatorBase_OnReset_ClearsDerivedPrivateStateOnReuse()
        {
            // Beyond counting Reset() calls: derived private state cached during OnBind must
            // be cleared by OnReset on BOTH pool directions, otherwise the second binding
            // inherits stale state (here: a growing session log + a stale pool index).
            var binder = _context.Resolve<ViewBinder>();
            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var go1 = new GameObject();
            var view1 = go1.AddComponent<DerivedStateDecoratedView>();
            binder.RegisterView(view1);
            var mediator = ((System.Collections.IDictionary)activeMediatorsField.GetValue(binder))[view1] as DerivedStateMediator;
            Assert.IsNotNull(mediator);
            Assert.AreEqual(1, mediator.SessionLog.Count, "OnBind must populate derived state.");
            Assert.AreEqual(42, mediator.CachedPoolIndex);

            binder.UnregisterView(view1); // return → ClearInjectedReferences → Reset() → OnReset()
            Object.DestroyImmediate(go1);
            Assert.AreEqual(0, mediator.SessionLog.Count,
                "Return-to-pool must clear derived private state via OnReset.");
            Assert.AreEqual(-1, mediator.CachedPoolIndex);

            var go2 = new GameObject();
            var view2 = go2.AddComponent<DerivedStateDecoratedView>();
            binder.RegisterView(view2); // pop → Reset() defensively, then Inject → Bind

            var reused = ((System.Collections.IDictionary)activeMediatorsField.GetValue(binder))[view2] as DerivedStateMediator;
            Assert.AreSame(mediator, reused, "Pool must reuse the same mediator instance.");
            Assert.AreEqual(1, reused.SessionLog.Count,
                "Rebind must NOT accumulate stale log entries from the previous session.");
            Assert.AreEqual("bind", reused.SessionLog[0], "Only the fresh OnBind entry must survive.");
            Assert.AreEqual(42, reused.CachedPoolIndex, "OnBind must re-set derived state on the reused instance.");

            binder.UnregisterView(view2);
            Object.DestroyImmediate(go2);
        }

        [Test]
        public void RegisterView_BindsViewBeforeMediator()
        {
            var go = new GameObject();
            var view = go.AddComponent<OrderedDecoratedView>();

            var binder = _context.Resolve<ViewBinder>();
            binder.RegisterView(view);

            Assert.AreEqual(1, view.Events.Count);
            Assert.AreEqual("view-bind", view.Events[0]);
            Assert.IsTrue(view.IsBound);

            var activeMediatorsField = typeof(ViewBinder).GetField("_activeMediators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeMediators = activeMediatorsField?.GetValue(binder) as System.Collections.IDictionary;
            Assert.IsNotNull(activeMediators);
            Assert.IsTrue(activeMediators.Contains(view));

            var mediator = activeMediators[view] as MockMediator;
            Assert.IsNotNull(mediator);
            Assert.AreSame(view, mediator.BoundView);
            Assert.IsTrue(mediator.IsBound);

            binder.UnregisterView(view);
            Assert.AreEqual(2, view.Events.Count);
            Assert.AreEqual("view-unbind", view.Events[1]);

            Object.DestroyImmediate(go);
        }
    }
}
