using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    /// <summary>
    /// Proves the Lifetime enum (Singleton/Scoped/Transient), CreateChildScope hierarchical
    /// scoping and the REVERSE-CREATION-ORDER disposal guarantee. Runs after LifecycleSuite;
    /// like that suite it creates real Contexts (LT9) and calls NexusRuntime.Reset() in
    /// teardown so the leak-audit suites that follow stay clean.
    /// </summary>
    public static class LifetimeScopeSuite
    {
        private static int _failures;

        private sealed class TrackedDep : IDisposable
        {
            public static int DisposeSequence; // monotonic; reset per test
            public string Name;
            public bool Disposed;
            public int DisposeIndex;
            public TrackedDep(string name) { Name = name; }
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                DisposeIndex = ++DisposeSequence;
            }
        }

        private sealed class DepA : IDisposable
        {
            public bool Disposed;
            public int DisposeIndex;
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                DisposeIndex = ++TrackedDep.DisposeSequence;
            }
        }

        private sealed class DepB : IDisposable
        {
            public bool Disposed;
            public int DisposeIndex;
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                DisposeIndex = ++TrackedDep.DisposeSequence;
            }
        }

        private sealed class TransientDep : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class ParentOwned : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class ExternalOwned : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class SharedDep : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class LifetimeProbeLifecycle : IContextLifecycle
        {
            public ContextBuilder Builder;
            public void OnConfigure(IContextBuilder builder)
            {
                Builder = (ContextBuilder)builder;
                builder.Bind<DepA>(Lifetime.Scoped);
                builder.Bind<TransientDep>(Lifetime.Transient);
                builder.Bind<SharedDep>(Lifetime.Singleton);
            }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[LifetimeScope] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("LifetimeScope", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[LifetimeScope] LIFETIME (Singleton/Scoped/Transient) + CHILD SCOPE + DISPOSE ORDER");
            Console.WriteLine("===============================================================================");

            Test_ChildScope_Scoped_OwnsAndDisposes_ItsOwnInstance();
            Test_ChildScope_Dispose_DoesNotTouch_ParentInstances();
            Test_ParentScoped_ResolvedThroughChild_IsShared_AndDisposedWithParent();
            Test_Singleton_RegisteredOnChild_LivesAtRoot_Shared_DisposedWithRoot();
            Test_Transient_FreshPerResolve_NotOwnedByContainer();
            Test_LegacyBoolOverloads_MapToScopedAndTransient();
            Test_Dispose_RunsInReverseCreationOrder();
            Test_BindInstance_DisposeWithContainer_False_LeavesInstanceAlive();
            Test_ContextBuilder_LifetimeOverloads_ConfigureAndDispose();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[LifetimeScope] ALL LIFETIME/SCOPE TESTS PASSED ✓"
                : $"[LifetimeScope] {_failures} LIFETIME/SCOPE TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Test_ChildScope_Scoped_OwnsAndDisposes_ItsOwnInstance()
        {
            var root = new NexusDI();
            var child = root.CreateChildScope(scope => scope.Bind<DepA>(Lifetime.Scoped));
            bool ok = false;
            string detail;
            try
            {
                var a1 = child.Resolve<DepA>();
                var a2 = child.Resolve<DepA>();
                bool same = ReferenceEquals(a1, a2);
                child.Dispose();
                bool disposed = a1.Disposed;
                // The parent container does not know the child's scoped binding.
                bool parentThrows = Throws<InvalidOperationException>(() => root.Resolve<DepA>());
                ok = same && disposed && parentThrows;
                detail = $"sameInstance={same} disposedWithChild={disposed} parentResolveThrows={parentThrows}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT1. ChildScope_Scoped_OwnsAndDisposes_ItsOwnInstance", ok, detail);
        }

        private static void Test_ChildScope_Dispose_DoesNotTouch_ParentInstances()
        {
            var root = new NexusDI();
            var parentOwned = new ParentOwned();
            root.BindInstance(parentOwned); // disposeWithContainer: true (default)
            var child = root.CreateChildScope();
            bool ok = false;
            string detail;
            try
            {
                var viaChild = child.Resolve<ParentOwned>(); // parent-scope resolution
                bool shared = ReferenceEquals(viaChild, parentOwned);
                child.Dispose();
                bool parentStillAlive = !parentOwned.Disposed;
                root.Dispose();
                bool parentDisposedWithRoot = parentOwned.Disposed;
                ok = shared && parentStillAlive && parentDisposedWithRoot;
                detail = $"shared={shared} aliveAfterChildDispose={parentStillAlive} disposedWithRoot={parentDisposedWithRoot}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT2. ChildScope_Dispose_DoesNotTouch_ParentInstances", ok, detail);
        }

        private static void Test_ParentScoped_ResolvedThroughChild_IsShared_AndDisposedWithParent()
        {
            var root = new NexusDI();
            root.Bind<DepA>(Lifetime.Scoped);
            var child = root.CreateChildScope(); // no own binding for DepA
            bool ok = false;
            string detail;
            try
            {
                var viaChild = child.Resolve<DepA>();
                var viaRoot = root.Resolve<DepA>();
                bool shared = ReferenceEquals(viaChild, viaRoot);
                child.Dispose(); // must NOT dispose the parent's scoped instance
                bool stillAlive = !viaChild.Disposed;
                root.Dispose();
                bool disposedWithRoot = viaChild.Disposed;
                ok = shared && stillAlive && disposedWithRoot;
                detail = $"shared={shared} aliveAfterChildDispose={stillAlive} disposedWithRoot={disposedWithRoot}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT3. ParentScoped_ResolvedThroughChild_IsShared_AndDisposedWithParent", ok, detail);
        }

        private static void Test_Singleton_RegisteredOnChild_LivesAtRoot_Shared_DisposedWithRoot()
        {
            var root = new NexusDI();
            var child = root.CreateChildScope(scope => scope.Bind<SharedDep>(Lifetime.Singleton));
            bool ok = false;
            string detail;
            try
            {
                var viaChild = child.Resolve<SharedDep>();
                var viaRoot = root.Resolve<SharedDep>();
                bool shared = ReferenceEquals(viaChild, viaRoot);
                child.Dispose(); // singleton lives at root — child teardown must not dispose it
                bool stillAlive = !viaChild.Disposed;
                root.Dispose();
                bool disposedWithRoot = viaChild.Disposed;
                ok = shared && stillAlive && disposedWithRoot;
                detail = $"shared={shared} aliveAfterChildDispose={stillAlive} disposedWithRoot={disposedWithRoot}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT4. Singleton_RegisteredOnChild_LivesAtRoot_Shared_DisposedWithRoot", ok, detail);
        }

        private static void Test_Transient_FreshPerResolve_NotOwnedByContainer()
        {
            var root = new NexusDI();
            root.Bind<TransientDep>(Lifetime.Transient);
            bool ok = false;
            string detail;
            try
            {
                var t1 = root.Resolve<TransientDep>();
                var t2 = root.Resolve<TransientDep>();
                bool fresh = !ReferenceEquals(t1, t2);
                root.Dispose();
                bool notDisposed = !t1.Disposed && !t2.Disposed;
                ok = fresh && notDisposed;
                detail = $"freshPerResolve={fresh} containerOwned={!notDisposed}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT5. Transient_FreshPerResolve_NotOwnedByContainer", ok, detail);
        }

        private static void Test_LegacyBoolOverloads_MapToScopedAndTransient()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.Bind<DepA>(isSingleton: true);     // → Scoped
                root.Bind<TransientDep>(isSingleton: false); // → Transient
                var s1 = root.Resolve<DepA>();
                var s2 = root.Resolve<DepA>();
                bool scopedShared = ReferenceEquals(s1, s2);
                var t1 = root.Resolve<TransientDep>();
                var t2 = root.Resolve<TransientDep>();
                bool transientFresh = !ReferenceEquals(t1, t2);
                root.Dispose();
                bool scopedDisposed = s1.Disposed && s2.Disposed;
                bool transientNotDisposed = !t1.Disposed && !t2.Disposed;
                ok = scopedShared && transientFresh && scopedDisposed && transientNotDisposed;
                detail = $"isSingleton:true→shared+disposed={scopedShared && scopedDisposed} isSingleton:false→fresh+notOwned={transientFresh && transientNotDisposed}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT6. LegacyBoolOverloads_MapToScopedAndTransient", ok, detail);
        }

        private static void Test_Dispose_RunsInReverseCreationOrder()
        {
            var root = new NexusDI();
            bool ok = false;
            string detail;
            try
            {
                root.Bind<DepA>(Lifetime.Scoped);
                root.Bind<DepB>(Lifetime.Scoped);
                var a = root.Resolve<DepA>(); // created FIRST
                var b = root.Resolve<DepB>(); // created SECOND
                root.Dispose();
                // Reverse creation order: B (later) disposed before A (earlier).
                ok = a.Disposed && b.Disposed && a.DisposeIndex > b.DisposeIndex;
                detail = $"aDisposed={a.Disposed} bDisposed={b.Disposed} aIdx={a.DisposeIndex} bIdx={b.DisposeIndex} (expect bIdx < aIdx)";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT7. Dispose_RunsInReverseCreationOrder", ok, detail);
        }

        private static void Test_BindInstance_DisposeWithContainer_False_LeavesInstanceAlive()
        {
            var root = new NexusDI();
            var external = new ExternalOwned();
            bool ok = false;
            string detail;
            try
            {
                root.BindInstance(external, disposeWithContainer: false);
                root.Dispose();
                ok = !external.Disposed;
                detail = $"externalInstanceDisposed={external.Disposed} (expected false)";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { root.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] root.Dispose teardown: {ex.Message}"); }
            }
            Report("LT8. BindInstance_DisposeWithContainer_False_LeavesInstanceAlive", ok, detail);
        }

        private static void Test_ContextBuilder_LifetimeOverloads_ConfigureAndDispose()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            string detail;
            try
            {
                var lifecycle = new LifetimeProbeLifecycle();
                ctx.Configure(new[] { lifecycle });
                var container = ctx.Container;

                var s1 = container.Resolve<DepA>();      // builder.Bind(Lifetime.Scoped)
                var s2 = container.Resolve<DepA>();
                bool scopedShared = ReferenceEquals(s1, s2);

                var t1 = container.Resolve<TransientDep>(); // builder.Bind(Lifetime.Transient)
                var t2 = container.Resolve<TransientDep>();
                bool transientFresh = !ReferenceEquals(t1, t2);

                var shared = container.Resolve<SharedDep>(); // builder.Bind(Lifetime.Singleton)

                ctx.Dispose();
                bool scopedDisposed = s1.Disposed;
                bool transientNotDisposed = !t1.Disposed;
                bool singletonDisposed = shared.Disposed;
                ok = scopedShared && transientFresh && scopedDisposed && transientNotDisposed && singletonDisposed;
                detail = $"scopedShared={scopedShared} transientFresh={transientFresh} scopedDisposed={scopedDisposed} transientNotOwned={transientNotDisposed} singletonDisposed={singletonDisposed}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { ctx.Dispose(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] ctx.Dispose teardown: {ex.Message}"); }
                try { NexusRuntime.Reset(); } catch (Exception ex) { Console.WriteLine($"[LifetimeScope] NexusRuntime.Reset teardown: {ex.Message}"); }
            }
            Report("LT9. ContextBuilder_LifetimeOverloads_ConfigureAndDispose", ok, detail);
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
            catch { return false; }
        }
    }
}
