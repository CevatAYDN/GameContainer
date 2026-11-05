using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    public static class TeardownLeakSuite
    {
        public class LeakTargetModel : IReactiveModel
        {
            public readonly ObservableProperty<int> Score = new(0);
            public ValueTask OnBind(CancellationToken ct) => default;
        }

        public class LeakTargetService : INexusService, IDisposable
        {
            public bool Disposed;
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() => Disposed = true;
            public void Dispose() => OnDispose();
        }

        public class UnbindProbeMediator : IMediator
        {
            public bool Unbound;
            public void Bind(object view, ISignalBus signalBus) { }
            public void Unbind() => Unbound = true;
        }

        public class ReadOnlyTrackingMediator : Mediator<object>
        {
            public IReadOnlyObservableProperty<int> Source;
            public int Changes;
            protected override void OnBind() => TrackObservable(Source, (oldValue, newValue) => Changes++);
        }

        [Mediator(typeof(UnbindProbeMediator))]
        public class ThrowingUnbindView : IView
        {
            public void Bind(IContext context) { }
            public void Unbind() => throw new InvalidOperationException("view unbind failed");
        }

        public interface IPoolKeyMediator : IMediator { }

        public class PoolKeyDeclaredMediator : IPoolKeyMediator
        {
            public void Bind(object view, ISignalBus signalBus) { }
            public void Unbind() { }
        }

        public class PoolKeyBoundMediator : IPoolKeyMediator
        {
            public void Bind(object view, ISignalBus signalBus) { }
            public void Unbind() { }
        }

        // the attribute names one concrete, DI binds the abstraction to another
        [Mediator(typeof(PoolKeyDeclaredMediator), typeof(IPoolKeyMediator))]
        public class AbstractionMediatedView : IView
        {
            public void Bind(IContext context) { }
            public void Unbind() { }
        }

        public static int Run()
        {
            int failures = 0;
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine("[TeardownLeak] TEARDOWN & MEMORY LEAK HUNTER AUDIT SUITE");
            Console.WriteLine("===============================================================================");

            failures += AssertPass("TL1. PureContext_Teardown_Frees_ContextAndServices", TestPureContextTeardownFreesAll);
            failures += AssertPass("TL2. DisposedContext_Removed_From_NexusRuntime_ActiveContexts", TestDisposedContextRemovedFromRegistry);
            failures += AssertPass("TL3. ViewBinderDispose_Unbinds_Mediator_When_View_Unbind_Throws", TestViewBinderDisposeUnbindsMediatorWhenViewThrows);
            failures += AssertPass("TL4. ReadOnlyObservable_TrackedByAMediator_IsReleasedOnUnbind", TestReadOnlyObservableReleasedOnUnbind);
            failures += AssertPass("TL5. AbstractionMediator_ReturnedToThePool_IsReusedByTheNextView", TestAbstractionMediatorIsReusedFromThePool);

            if (failures == 0)
                Console.WriteLine("\n[TeardownLeak] ALL TEARDOWN LEAK AUDIT TESTS PASSED ✓");
            else
                Console.WriteLine($"\n[TeardownLeak] {failures} TEARDOWN LEAK AUDIT TEST(S) FAILED ✗");

            return failures;
        }

        private static int AssertPass(string testName, Func<bool> testFunc)
        {
            try
            {
                bool passed = testFunc();
                if (passed)
                {
                    Console.WriteLine($"[TeardownLeak] PASS  {testName}");
                    return 0;
                }
                else
                {
                    Console.WriteLine($"[TeardownLeak] FAIL  {testName}");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TeardownLeak] FAIL  {testName}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestPureContextTeardownFreesAll()
        {
            WeakReference ctxRef;
            WeakReference serviceRef;
            WeakReference modelRef;

            // Scope context creation inside helper method to isolate references
            CreateAndDisposeContext(out ctxRef, out serviceRef, out modelRef);

            // Force GC collect & finalizer wait
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (serviceRef.IsAlive)
            {
                Console.WriteLine("      Leak detected: LeakTargetService was not garbage-collected after Context Dispose");
                return false;
            }
            if (modelRef.IsAlive)
            {
                Console.WriteLine("      Leak detected: LeakTargetModel was not garbage-collected after Context Dispose");
                return false;
            }
            if (ctxRef.IsAlive)
            {
                Console.WriteLine("      Leak detected: Context instance was not garbage-collected after Dispose");
                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateAndDisposeContext(out WeakReference ctxRef, out WeakReference serviceRef, out WeakReference modelRef)
        {
            var ctx = NexusRuntime.CreatePureContextAsync("TeardownLeakTestContext").GetAwaiter().GetResult();
            var service = new LeakTargetService();
            var model = new LeakTargetModel();

            ctx.Container.BindInstance<LeakTargetService>(service);
            ctx.Container.BindInstance<LeakTargetModel>(model);

            ctxRef = new WeakReference(ctx);
            serviceRef = new WeakReference(service);
            modelRef = new WeakReference(model);

            ctx.Dispose();
        }

        private static bool TestViewBinderDisposeUnbindsMediatorWhenViewThrows()
        {
            var mediator = new UnbindProbeMediator();
            using var container = new NexusDI();
            container.BindInstance(mediator);
            var binder = new ViewBinder(new MockContext(), container);
            binder.RegisterView(new ThrowingUnbindView());

            if (binder.ActiveMediatorCount != 1)
            {
                Console.WriteLine("      Setup failed: the view was not registered with its mediator");
                return false;
            }

            binder.Dispose();

            if (!mediator.Unbound)
            {
                Console.WriteLine("      Leak detected: the mediator kept its subscriptions because the view's Unbind threw");
                return false;
            }

            return true;
        }

        // A mediator resolved through an abstraction bound to another concrete went back to a pool its view never reads:
        // every registration constructed a new one and the pool kept up to 64 unused instances per type.
        private static bool TestAbstractionMediatorIsReusedFromThePool()
        {
            using var container = new NexusDI();
            container.Bind<IPoolKeyMediator, PoolKeyBoundMediator>(isSingleton: false);
            var binder = new ViewBinder(new MockContext(), container);
            var view = new AbstractionMediatedView();

            binder.RegisterView(view);
            binder.UnregisterView(view);
            binder.RegisterView(view);

            if (binder.PoolReturnCount != 1 || binder.PoolPopCount != 1)
            {
                Console.WriteLine($"      returned={binder.PoolReturnCount} popped={binder.PoolPopCount}; expected 1 and 1 (the second registration reuses the first mediator)");
                return false;
            }
            return true;
        }

        // ADR-0003: a mediator watches a service's read-only state; the subscription must go with Unbind like any other
        private static bool TestReadOnlyObservableReleasedOnUnbind()
        {
            var state = new ObservableProperty<int>(0);
            var mediator = new ReadOnlyTrackingMediator { Source = state };
            using var di = new NexusDI();
            mediator.Bind(new object(), new SignalBus(di, new CommandPoolManager(di), new MockContext()));
            state.Value = 1;
            mediator.Unbind();
            state.Value = 2;

            if (mediator.Changes != 1)
            {
                Console.WriteLine($"      {mediator.Changes} change(s) seen; expected 1 (one while bound, none after Unbind)");
                return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestDisposedContextRemovedFromRegistry()
        {
            var ctx = NexusRuntime.CreatePureContextAsync("RegistryLeakTestContext").GetAwaiter().GetResult();

            bool isPresentBefore = ((System.Collections.IList)NexusRuntime.ActiveContexts).Contains(ctx);

            ctx.Dispose();

            bool isPresentAfter = ((System.Collections.IList)NexusRuntime.ActiveContexts).Contains(ctx);

            if (!isPresentBefore)
            {
                Console.WriteLine("      Context was not registered in NexusRuntime.ActiveContexts on creation");
                return false;
            }

            if (isPresentAfter)
            {
                Console.WriteLine("      Context was not removed from NexusRuntime.ActiveContexts on Dispose");
                return false;
            }

            return true;
        }
    }
}
