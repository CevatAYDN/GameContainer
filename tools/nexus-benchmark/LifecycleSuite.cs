using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;

namespace NexusBench
{
    public static class LifecycleSuite
    {
        private static int _failures;

        private sealed class StartupValidationLifecycle : IContextLifecycle
        {
            public ContextBuilder Builder;

            public void OnConfigure(IContextBuilder builder)
            {
                Builder = (ContextBuilder)builder;
                builder.EnableStrictInjection();
                builder.Bind<BrokenHost>();
                builder.Bind<LazyHost>();
                builder.Bind<OptionalHost>();
                builder.Bind<CtorHost>();
            }

            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class BrokenHost
        {
#pragma warning disable 0649
            [Inject] public MissingDep Dep;
#pragma warning restore 0649
        }

        private sealed class MissingDep { }

        private sealed class LazyHost
        {
#pragma warning disable 0649
            [Inject] public LazyInjection<MissingDep> Dep;
#pragma warning restore 0649
        }

        private sealed class OptionalHost
        {
#pragma warning disable 0649
            [OptionalInject] public MissingDep Dep;
#pragma warning restore 0649
        }

        private sealed class CtorHost
        {
            public CtorHost(MissingDep dep) { }
        }

        private sealed class StartProbeLifecycle : IContextLifecycle
        {
            public readonly List<string> Log = new();
            public bool ThrowOnStart;

            public void OnConfigure(IContextBuilder builder)
            {
                Log.Add("configure");
            }

            public ValueTask OnInitializeAsync(CancellationToken ct)
            {
                Log.Add("init");
                return default;
            }

            public ValueTask OnStartAsync(CancellationToken ct)
            {
                Log.Add("start");
                if (ThrowOnStart) throw new InvalidOperationException("start-boom");
                return default;
            }

            public void OnDispose()
            {
                Log.Add("dispose");
            }
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Lifecycle] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Lifecycle", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Lifecycle] STARTUP VALIDATION + LIFECYCLE PROOF");
            Console.WriteLine("===============================================================================");

            Test_StartupValidation_DefaultOn_And_OptOutSafe();
            Test_ContextLifecycle_Phases_And_Dispose();
            Test_Lifecycle_OnStart_Throw_FailsBootFast();
            Test_Root_AutoAdds_SupportComponents();
            Test_ParentShutdown_PreservesChildSave(false);
            Test_ParentShutdown_PreservesChildSave(true);
            Test_ParentRootShutdown_PreservesDisabledDescendantSave();
            Test_ChildShutdown_LeavesParentLive_AndRejectsLateChildren();
            Test_AsyncParentShutdown_JoinsAlreadyDisposingChild();
            Test_SyncParentShutdown_DoesNotBlockOnAsyncOnlyChild();
            Test_ParentShutdown_ReentrantCallbacksDoNotRepeatTeardown();
            Test_SurvivingChildRoot_StartAfterParentDestroyDoesNotTouchDisposedToken();
            Test_ChildCancellationAndThrowingLogger_DoNotSkipHierarchyCleanup(false);
            Test_ChildCancellationAndThrowingLogger_DoNotSkipHierarchyCleanup(true);
            Test_ThrowingOverlapWarning_DoesNotSkipParentOrSiblings(false);
            Test_ThrowingOverlapWarning_DoesNotSkipParentOrSiblings(true);
            Test_EndedRoot_DoesNotBlockLiveRootStartupOrPostContext(20);
            Test_EndedRoot_DoesNotBlockLiveRootStartupOrPostContext(0);
            Test_FactoryRegistrationObserverShutdown_DoesNotLeakOwnedData();
            Test_RejectedParent_LeavesCallerOwnedContextDataAlive();
            Test_HarnessChildCreation_RejectedShutdownDoesNotLeak(false);
            Test_HarnessChildCreation_RejectedShutdownDoesNotLeak(true);
            Test_PostContext_SkipsChildWhoseAsyncTeardownIsStillRunning();
            Test_PostContext_DisposingFirstCallbackStopsRemainingCallbacks();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Lifecycle] ALL LIFECYCLE TESTS PASSED ✓"
                : $"[Lifecycle] {_failures} LIFECYCLE TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Test_StartupValidation_DefaultOn_And_OptOutSafe()
        {
            bool saved = ContextBuilder.ValidateOnStartup;
            bool ok = false;
            string detail;
            try
            {
                ContextBuilder.ValidateOnStartup = true;
                var ctx = ContextFactory.Create();
                try
                {
                    var lifecycle = new StartupValidationLifecycle();
                    ctx.Configure(new[] { lifecycle });
                    var issues = lifecycle.Builder.Validate();

                    bool missingField = issues.Exists(i => i.SourceType == typeof(BrokenHost) && i.IssueType == DiValidationIssueType.MissingFieldDependency);
                    bool ctorFlagged = issues.Exists(i => i.SourceType == typeof(CtorHost) && i.IssueType == DiValidationIssueType.MissingConstructorDependency);
                    bool lazyNotFlagged = !issues.Exists(i => i.SourceType == typeof(LazyHost));
                    bool optionalNotFlagged = !issues.Exists(i => i.SourceType == typeof(OptionalHost));

                    ContextBuilder.ValidateOnStartup = false;
                    var ctx2 = ContextFactory.Create();
                    try
                    {
                        ctx2.Configure(new[] { new StartupValidationLifecycle() });
                    }
                    finally
                    {
                        ctx2.Dispose();
                    }

                    ok = missingField && ctorFlagged && lazyNotFlagged && optionalNotFlagged;
                    detail = $"missingField={missingField} ctorFlagged={ctorFlagged} lazyNotFlagged={lazyNotFlagged} optionalNotFlagged={optionalNotFlagged}";
                }
                finally
                {
                    ctx.Dispose();
                }
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                ContextBuilder.ValidateOnStartup = saved;
            }

            Report("L1. StartupValidation_DefaultOn_And_OptOutSafe", ok, detail);
        }

        private static void Test_ContextLifecycle_Phases_And_Dispose()
        {
            var ctx = ContextFactory.Create();
            var lifecycle = new StartProbeLifecycle();
            bool ok = false;
            string detail;
            try
            {
                ctx.Configure(new[] { lifecycle });
                var initMethod = typeof(Context).GetMethod("InitializeLifecycleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var vt = (ValueTask)initMethod.Invoke(ctx, new object[] { new IContextLifecycle[] { lifecycle }, ctx.LifetimeToken });
                vt.GetAwaiter().GetResult();
                ctx.Dispose();

                bool phases = string.Join(",", lifecycle.Log) == "configure,init,start,dispose";
                ok = phases;
                detail = $"phases=[{string.Join(",", lifecycle.Log)}]";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (ctx != null)
                {
                    try { ctx.Dispose(); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Nexus Lifecycle] Dispose during teardown failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            Report("L2. ContextLifecycle_Phases_And_Dispose", ok, detail);
        }

        private static void Test_Root_AutoAdds_SupportComponents()
        {
            // L4: a Root created programmatically (the hierarchy menu and the Context Wizard,
            // AddComponent<Root>(), wizard scenes) must auto-add QueueDrainer +
            // MetricsSampler on Awake. Without QueueDrainer the HybridQueue never drains
            // (queued signals silently never run); without MetricsSampler the game never
            // records FPS/memory/GC, so the Performance Dashboard reads a flat 0.0.
            var go = new UnityEngine.GameObject("ProgRootSupport");
            var root = go.AddComponent<Root>();
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.name = "SupportProbeData";
            data.ScopeTag = "SupportProbe";
            root.SetUp(data);

            bool ok = false;
            string detail;
            try
            {
                var awake = typeof(Root).GetMethod("Awake",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                awake?.Invoke(root, null);

                bool hasDrainer = go.GetComponent<QueueDrainer>() != null;
                bool hasSampler = go.GetComponent<MetricsSampler>() != null;
                ok = hasDrainer && hasSampler;
                detail = $"queueDrainer={hasDrainer} metricsSampler={hasSampler}";

                // Idempotent: a second Awake (double boot) must not double-add.
                awake?.Invoke(root, null);
                int drainerCount = go.GetComponents<QueueDrainer>().Length;
                int samplerCount = go.GetComponents<MetricsSampler>().Length;
                bool noDouble = drainerCount == 1 && samplerCount == 1;
                ok = ok && noDouble;
                detail += $" afterSecondAwake drainerCount={drainerCount} samplerCount={samplerCount}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { UnityEngine.Object.Destroy(go); } catch { }
                try { NexusRuntime.Reset(); } catch (Exception ex)
                {
                    Console.WriteLine($"[Nexus Lifecycle] Reset during teardown failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Report("L4. Root_AutoAdds_QueueDrainer_And_MetricsSampler", ok, detail);
        }

        private static void Test_Lifecycle_OnStart_Throw_FailsBootFast()
        {
            // L3: a boot lifecycle whose OnStartAsync throws must fail the boot lifecycle
            // FAST (Context.InitializeLifecycleAsync propagates the exception; Root.Start
            // catches it, disposes the context and clears IsInitialized). This is the
            // documented fail-fast contract for boot lifecycles; IStartable singletons
            // follow it too since issue #190.
            var lifecycle = new StartProbeLifecycle { ThrowOnStart = true };
            var ctx = ContextFactory.Create();
            bool threw = false;
            string phases = "";
            try
            {
                ctx.Configure(new[] { lifecycle });
                var initMethod = typeof(Context).GetMethod("InitializeLifecycleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var vt = (ValueTask)initMethod.Invoke(ctx, new object[] { new IContextLifecycle[] { lifecycle }, ctx.LifetimeToken });
                vt.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                threw = true;
            }
            finally
            {
                if (ctx != null)
                {
                    try { ctx.Dispose(); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Nexus Lifecycle] Dispose during teardown failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                // Capture AFTER dispose: the teardown phase must still run even though
                // start threw (fail-fast aborts init, but cleanup is unconditional).
                phases = string.Join(",", lifecycle.Log);
            }

            // configure + init ran, start ran and threw (init aborted), dispose still ran.
            bool ok = threw && phases == "configure,init,start,dispose";
            Report("L3. Lifecycle_OnStart_Throw_FailsBootFast", ok,
                $"threw={threw} phases=[{phases}] (expected configure,init,start + throw, then dispose)");
        }

        private sealed class PendingSaveLifecycle : IContextLifecycle
        {
            private readonly Func<EncryptedStorageService> _storage;
            private readonly string _key;
            private readonly int _value;
            public int DisposeCalls;

            public PendingSaveLifecycle(Func<EncryptedStorageService> storage, string key, int value)
            {
                _storage = storage;
                _key = key;
                _value = value;
            }

            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose()
            {
                DisposeCalls++;
                _storage().SetInt(_key, _value);
                _storage().Save();
            }
        }

        private static void Test_ParentShutdown_PreservesChildSave(bool asynchronous)
        {
            string salt = "Hierarchy337_" + Guid.NewGuid().ToString("N");
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            using var parent = new Context(contextData: data);
            using var child = new Context(parent, data);
            using var grandchild = new Context(child, data);
            var storage = new EncryptedStorageService(salt, StorageKeyBinding.App);
            parent.Container.BindInstance(storage);
            var childSave = new PendingSaveLifecycle(() => child.Resolve<EncryptedStorageService>(), "map", 337);
            var grandchildSave = new PendingSaveLifecycle(() => grandchild.Resolve<EncryptedStorageService>(), "zone", 733);
            child.Configure(new[] { childSave });
            grandchild.Configure(new[] { grandchildSave });
            bool ok = false;
            string detail;
            try
            {
                if (asynchronous) parent.DisposeAsync().GetAwaiter().GetResult();
                else parent.Dispose();
                bool descendantsDisposed = child.IsDisposed && grandchild.IsDisposed;
                using var reopened = new EncryptedStorageService(salt, StorageKeyBinding.App);
                int map = reopened.GetInt("map", -1);
                int zone = reopened.GetInt("zone", -1);
                child.Dispose();
                grandchild.Dispose();
                parent.Dispose();
                ok = map == 337 && zone == 733 && descendantsDisposed
                    && childSave.DisposeCalls == 1 && grandchildSave.DisposeCalls == 1;
                detail = $"savedMap={map} savedZone={zone} childDisposed={child.IsDisposed} grandchildDisposed={grandchild.IsDisposed}"
                    + $" childFlushes={childSave.DisposeCalls} grandchildFlushes={grandchildSave.DisposeCalls}";
            }
            catch (Exception ex) { detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}"; }
            finally
            {
                storage.Dispose();
                UnityEngine.Object.Destroy(data);
            }
            Report($"337. Parent_{(asynchronous ? "DisposeAsync" : "Dispose")}_FlushesDescendantsBeforeStorageShutdown", ok, detail);
        }

        private static void Test_ParentRootShutdown_PreservesDisabledDescendantSave()
        {
            string salt = "RootHierarchy337_" + Guid.NewGuid().ToString("N");
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            Root NewRoot(string name, Root owner = null)
            {
                var go = new UnityEngine.GameObject(name);
                var root = go.AddComponent<Root>();
                root.AutoBindGlobalParent = false;
                root.SetUp(data, owner);
                return root;
            }
            void Message(Root root, string method) => typeof(Root).GetMethod(method,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(root, null);
            var parent = NewRoot("Parent337");
            var child = NewRoot("Child337", parent);
            var grandchild = NewRoot("Grandchild337", child);
            var storage = new EncryptedStorageService(salt, StorageKeyBinding.App);
            PendingSaveLifecycle save = null;
            bool ok = false;
            string detail;
            try
            {
                Message(parent, "Awake");
                parent.Context.Container.BindInstance(storage);
                save = new PendingSaveLifecycle(() => grandchild.Context.Resolve<EncryptedStorageService>(), "factory", 337);
                grandchild.RegisterLifecycle(save);
                Message(child, "Awake");
                Message(grandchild, "Awake");
                var childContext = child.Context;
                var grandchildContext = grandchild.Context;
                Message(child, "OnEnable");
                Message(child, "OnDisable");
                child.enabled = false;
                Message(parent, "OnDestroy");
                using var reopened = new EncryptedStorageService(salt, StorageKeyBinding.App);
                int saved = reopened.GetInt("factory", -1);
                bool descendantsDisposed = childContext.IsDisposed && grandchildContext.IsDisposed;
                Message(child, "OnDestroy");
                Message(grandchild, "OnDestroy");
                ok = saved == 337 && descendantsDisposed && save.DisposeCalls == 1 && !child.IsInitialized;
                detail = $"savedFactory={saved} disabledChildDisposed={childContext.IsDisposed}"
                    + $" grandchildDisposed={grandchildContext.IsDisposed} flushes={save.DisposeCalls}";
            }
            catch (Exception ex) { detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}"; }
            finally
            {
                Message(grandchild, "OnDestroy");
                Message(child, "OnDestroy");
                Message(parent, "OnDestroy");
                UnityEngine.Object.Destroy(grandchild.gameObject);
                UnityEngine.Object.Destroy(child.gameObject);
                UnityEngine.Object.Destroy(parent.gameObject);
                storage.Dispose();
                UnityEngine.Object.Destroy(data);
            }
            Report("337. ParentRootDestroy_FlushesDisabledChildAndGrandchild", ok, detail);
        }

        private sealed class CallbackLifecycle : IContextLifecycle
        {
            public Action OnEnd;
            public int Ends;
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { Ends++; OnEnd?.Invoke(); }
        }

        private sealed class AsyncSaveGate : IAsyncDisposable
        {
            public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Started;
            public int Ends;
            public Action Save;
            public async ValueTask DisposeAsync()
            {
                Started = true;
                await Release.Task;
                Save?.Invoke();
                Ends++;
            }
        }

        private static void Test_ChildShutdown_LeavesParentLive_AndRejectsLateChildren()
        {
            using var parent = new Context();
            var service = new object();
            parent.Container.BindInstance(service);
            var child = new Context(parent);
            child.Dispose();
            child.DisposeAsync().GetAwaiter().GetResult();
            bool parentLive = !parent.IsDisposed && ReferenceEquals(parent.Resolve<object>(), service);
            parent.Dispose();
            int contextsBefore = NexusRuntime.ActiveContexts.Count;
            bool rejected = false;
            try { using var lateChild = new Context(parent); }
            catch (ObjectDisposedException) { rejected = true; }
            Report("337. ChildDispose_LeavesParentLive_LateChildRejectedWithoutRegistryLeak",
                parentLive && rejected && contextsBefore == NexusRuntime.ActiveContexts.Count,
                $"parentLive={parentLive} rejected={rejected} registryUnchanged={contextsBefore == NexusRuntime.ActiveContexts.Count}");
        }

        private static void Test_AsyncParentShutdown_JoinsAlreadyDisposingChild()
        {
            string salt = "Overlap337_" + Guid.NewGuid().ToString("N");
            using var parent = new Context();
            using var child = new Context(parent);
            using var storage = new EncryptedStorageService(salt, StorageKeyBinding.App);
            parent.Container.BindInstance(storage);
            var gate = new AsyncSaveGate { Save = () => { child.Resolve<EncryptedStorageService>().SetInt("factory", 337); storage.Save(); } };
            child.Container.BindInstance(gate);
            var childEnd = child.DisposeAsync().AsTask();
            var parentEnd = parent.DisposeAsync().AsTask();
            bool waits = gate.Started && !childEnd.IsCompleted && !parentEnd.IsCompleted;
            // Idempotent re-entry must not launch a second teardown or wait on itself.
            parent.Dispose();
            parent.DisposeAsync().GetAwaiter().GetResult();
            gate.Release.TrySetResult(true);
            parentEnd.GetAwaiter().GetResult();
            childEnd.GetAwaiter().GetResult();
            using var reopened = new EncryptedStorageService(salt, StorageKeyBinding.App);
            int saved = reopened.GetInt("factory", -1);
            Report("337. DisposeAsync_JoinsAlreadyDisposingChildBeforeClosingStorage",
                waits && saved == 337 && gate.Ends == 1,
                $"waited={waits} savedFactory={saved} asyncEnds={gate.Ends}");
        }

        private static void Test_SyncParentShutdown_DoesNotBlockOnAsyncOnlyChild()
        {
            using var parent = new Context();
            using var child = new Context(parent);
            var gate = new AsyncSaveGate();
            child.Container.BindInstance(gate);
            parent.Dispose();
            bool returned = parent.IsDisposed && child.IsDisposed && gate.Started && gate.Ends == 0;
            gate.Release.TrySetResult(true);
            bool completed = SpinWait.SpinUntil(() => Volatile.Read(ref gate.Ends) == 1, TimeSpan.FromSeconds(5));
            Report("337. SyncDispose_RemainsNonblockingForAsyncOnlyChildSingleton",
                returned && completed, $"returnedBeforeAsyncEnd={returned} completed={completed}");
        }

        private static void Test_ParentShutdown_ReentrantCallbacksDoNotRepeatTeardown()
        {
            using var parent = new Context();
            using var child = new Context(parent);
            bool rejected = false;
            var lifecycle = new CallbackLifecycle
            {
                OnEnd = () =>
                {
                    parent.Dispose();
                    child.Dispose();
                    parent.DisposeAsync().GetAwaiter().GetResult();
                    try { using var lateChild = new Context(parent); }
                    catch (ObjectDisposedException) { rejected = true; }
                }
            };
            child.Container.BindInstance<IContextLifecycle>(lifecycle, disposeWithContainer: false);
            parent.Dispose();
            Report("337. ParentLedReentrantDispose_IsOnceOnly_AndRejectsChildRegistrationDuringShutdown",
                lifecycle.Ends == 1 && rejected && child.IsDisposed,
                $"childEnds={lifecycle.Ends} lateChildRejected={rejected} childDisposed={child.IsDisposed}");
        }

        private static void Test_SurvivingChildRoot_StartAfterParentDestroyDoesNotTouchDisposedToken()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var parent = new UnityEngine.GameObject("ParentPending337").AddComponent<Root>();
            var child = new UnityEngine.GameObject("ChildPending337").AddComponent<Root>();
            parent.AutoBindGlobalParent = false;
            child.AutoBindGlobalParent = false;
            parent.SetUp(data);
            child.SetUp(data, parent);
            void Message(Root root, string method) => typeof(Root).GetMethod(method,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(root, null);
            bool ok = false;
            string detail;
            try
            {
                Message(parent, "Awake");
                Message(child, "Awake");
                Message(parent, "OnDestroy");
                var start = (Task)typeof(Root).GetMethod("StartInternal",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(child, null);
                start.GetAwaiter().GetResult();
                ok = child.Context == null && !child.IsInitialized;
                detail = $"childContextCleared={child.Context == null} initialized={child.IsInitialized}";
            }
            catch (Exception ex) { detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}"; }
            finally
            {
                Message(child, "OnDestroy");
                Message(parent, "OnDestroy");
                UnityEngine.Object.Destroy(child.gameObject);
                UnityEngine.Object.Destroy(parent.gameObject);
                UnityEngine.Object.Destroy(data);
            }
            Report("337. SurvivingChildRoot_StartAfterParentDestroy_DoesNotReadDisposedToken", ok, detail);
        }

        private sealed class EndProbe : IDisposable
        {
            public int Ends;
            public void Dispose() => Ends++;
        }

        private sealed class ThrowingExceptionLogger : ILoggerService
        {
            public int ExceptionCalls;
            public bool ThrowOnIsEnabled, ThrowOnWarning;
            public int WarningAttempts;
            public bool IsEnabled
            {
                get
                {
                    if (ThrowOnIsEnabled)
                    {
                        WarningAttempts++;
                        throw new InvalidOperationException("logger-enabled-failure-337");
                    }
                    return true;
                }
                set { }
            }
            public void Log(string message) { }
            public void LogWarning(string message)
            {
                if (!ThrowOnWarning) return;
                WarningAttempts++;
                throw new InvalidOperationException("logger-warning-failure-337");
            }
            public void LogError(string message) { }
            public void LogException(Exception exception)
            {
                ExceptionCalls++;
                throw new InvalidOperationException("logger-failure-337");
            }
        }

        private static void Test_ChildCancellationAndThrowingLogger_DoNotSkipHierarchyCleanup(bool asynchronous)
        {
            using var parent = new Context();
            using var sibling = new Context(parent);
            using var failingChild = new Context(parent);
            var logger = new ThrowingExceptionLogger();
            parent.Container.BindInstance<ILoggerService>(logger);
            var parentEnd = new EndProbe();
            var siblingEnd = new EndProbe();
            var failingEnd = new EndProbe();
            parent.Container.BindInstance(parentEnd);
            sibling.Container.BindInstance(siblingEnd);
            failingChild.Container.BindInstance(failingEnd);
            int callbacks = 0;
            using var registration = failingChild.LifetimeToken.Register(() =>
            {
                callbacks++;
                throw new InvalidOperationException("child-cancel-337");
            });
            Exception escaped = null;
            try
            {
                if (asynchronous) parent.DisposeAsync().GetAwaiter().GetResult();
                else parent.Dispose();
            }
            catch (Exception ex) { escaped = ex; }
            bool ContainerEnded(Context context)
            {
                try { context.Resolve<EndProbe>(); return false; }
                catch (ObjectDisposedException) { return true; }
            }
            bool ended = parentEnd.Ends == 1 && siblingEnd.Ends == 1 && failingEnd.Ends == 1
                && ContainerEnded(parent) && ContainerEnded(sibling) && ContainerEnded(failingChild);
            Report($"337.C1. {(asynchronous ? "Async" : "Sync")}_ChildCancelAndLoggerFailure_EndEveryOwner",
                ended && callbacks == 1 && logger.ExceptionCalls == 1 && escaped == null,
                $"parentEnds={parentEnd.Ends} siblingEnds={siblingEnd.Ends} failingChildEnds={failingEnd.Ends}"
                + $" containersEnded={ended} callbacks={callbacks} loggerCalls={logger.ExceptionCalls}"
                + $" escaped={escaped?.GetType().Name ?? "none"}");
        }

        private static void Test_ThrowingOverlapWarning_DoesNotSkipParentOrSiblings(bool throwsInGetter)
        {
            using var parent = new Context();
            using var sibling = new Context(parent);
            using var child = new Context(parent);
            var logger = new ThrowingExceptionLogger { ThrowOnIsEnabled = throwsInGetter, ThrowOnWarning = !throwsInGetter };
            parent.Container.BindInstance<ILoggerService>(logger);
            var parentEnd = new EndProbe();
            var siblingEnd = new EndProbe();
            parent.Container.BindInstance(parentEnd);
            sibling.Container.BindInstance(siblingEnd);
            var gate = new AsyncSaveGate();
            child.Container.BindInstance(gate);
            var childEnd = child.DisposeAsync().AsTask();
            Exception escaped = null;
            try { parent.Dispose(); }
            catch (Exception ex) { escaped = ex; }
            bool endedBeforeRelease = parentEnd.Ends == 1 && siblingEnd.Ends == 1 && !childEnd.IsCompleted;
            gate.Release.TrySetResult(true);
            childEnd.GetAwaiter().GetResult();
            Report($"337.C1. OverlapWarning_{(throwsInGetter ? "IsEnabled" : "LogWarning")}_Failure_DoesNotSkipOwners",
                endedBeforeRelease && logger.WarningAttempts == 1 && escaped == null,
                $"parentEnds={parentEnd.Ends} siblingEnds={siblingEnd.Ends} nonblocking={endedBeforeRelease}"
                + $" warningAttempts={logger.WarningAttempts} escaped={escaped?.GetType().Name ?? "none"}");
        }

        private sealed class PostContextProbe : IContextLifecycle, IPostContextLifecycle
        {
            public int Starts, Posts, Ends;
            public Task InitializeGate;
            public Action OnPost;
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => InitializeGate == null ? default : new ValueTask(InitializeGate);
            public ValueTask OnStartAsync(CancellationToken ct) { Starts++; return default; }
            public void OnPostContext(IContextBuilder builder) { Posts++; OnPost?.Invoke(); }
            public void OnDispose() => Ends++;
        }

        private sealed class RootFramePump : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback callback, object state)> _pending = new();
            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_pending) _pending.Enqueue((callback, state));
            }
            public void Step()
            {
                int count;
                lock (_pending) count = _pending.Count;
                for (int i = 0; i < count; i++)
                {
                    (SendOrPostCallback callback, object state) item;
                    lock (_pending) item = _pending.Dequeue();
                    item.callback(item.state);
                }
            }

            public void Complete(Task task)
            {
                if (!SpinWait.SpinUntil(() => { Step(); return task.IsCompleted; }, TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Root task did not complete after its gate was released.");
                task.GetAwaiter().GetResult();
            }
        }

        private static object RootMessage(Root root, string method) => typeof(Root).GetMethod(method,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(root, null);

        private static void Test_EndedRoot_DoesNotBlockLiveRootStartupOrPostContext(int priority)
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            Root NewRoot(string name, int order)
            {
                var root = new UnityEngine.GameObject(name).AddComponent<Root>();
                root.AutoBindGlobalParent = false;
                root.SetUp(data, priority: order);
                return root;
            }
            var ended = NewRoot("Ended337", 10);
            var live = NewRoot("Live337", priority);
            var post = new PostContextProbe();
            live.RegisterLifecycle(post);
            var previous = SynchronizationContext.Current;
            var pump = new RootFramePump();
            SynchronizationContext.SetSynchronizationContext(pump);
            Task start = null;
            bool ok = false;
            string detail;
            try
            {
                RootMessage(ended, "Awake");
                RootMessage(ended, "OnEnable");
                ((Task)RootMessage(ended, "StartInternal")).GetAwaiter().GetResult();
                bool wasReady = ended.IsInitialized;
                // Leave the enabled Root registered, as an ancestor's Context cascade does.
                ended.Context.Dispose();
                RootMessage(live, "Awake");
                RootMessage(live, "OnEnable");
                start = (Task)RootMessage(live, "StartInternal");
                for (int frame = 0; frame < 3; frame++) pump.Step();
                ok = wasReady && !ended.IsInitialized && start.IsCompletedSuccessfully
                    && live.IsInitialized && post.Starts == 1 && post.Posts == 1;
                detail = $"wasReady={wasReady} endedReady={ended.IsInitialized} startupCompleted={start.IsCompleted}"
                    + $" liveReady={live.IsInitialized} starts={post.Starts} posts={post.Posts} frames=3";
            }
            catch (Exception ex) { detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}"; }
            finally
            {
                RootMessage(live, "OnDestroy");
                RootMessage(ended, "OnDestroy");
                for (int frame = 0; frame < 3; frame++) pump.Step();
                if (start != null) start.GetAwaiter().GetResult();
                SynchronizationContext.SetSynchronizationContext(previous);
                UnityEngine.Object.Destroy(live.gameObject);
                UnityEngine.Object.Destroy(ended.gameObject);
                UnityEngine.Object.Destroy(data);
            }
            Report($"337.C1. EndedRoot_DoesNotBlock{(priority > 10 ? "PostContext" : "SiblingStartup")}", ok, detail);
        }

        private static void Test_FactoryRegistrationObserverShutdown_DoesNotLeakOwnedData()
        {
            int before = NexusRuntime.ActiveContexts.Count;
            using var parent = new Context();
            Context observed = null;
            ContextData generated = null;
            bool configured = false;
            void OnRegistered(IContext context)
            {
                if (!ReferenceEquals(context.Parent, parent)) return;
                observed = (Context)context;
                generated = observed.ContextData;
                parent.Dispose();
            }
            NexusRuntime.OnContextRegistered += OnRegistered;
            Exception error = null;
            try
            {
                ContextFactory.StartAsync("RegistrationShutdown337", _ => configured = true, parent).GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
            finally { NexusRuntime.OnContextRegistered -= OnRegistered; }
            bool dataEnded = !ReferenceEquals(generated, null) && !generated;
            bool registryRestored = NexusRuntime.ActiveContexts.Count == before;
            bool contextError = error is ObjectDisposedException disposed && disposed.ObjectName == nameof(Context);
            Report("337.C1. FactoryRegistrationShutdown_DestroysOwnedData_AndRejectsStartupBeforeConfiguration",
                observed?.IsDisposed == true && dataEnded && registryRestored && !configured && contextError,
                $"childEnded={observed?.IsDisposed} generatedDataEnded={dataEnded} registryRestored={registryRestored}"
                + $" configured={configured} exception={error?.GetType().Name ?? "none"} objectName={(error as ObjectDisposedException)?.ObjectName}");
            // The red control must not leave the reproduced ScriptableObject leak in subsequent suites.
            if (generated) UnityEngine.Object.Destroy(generated);
        }

        private static void Test_RejectedParent_LeavesCallerOwnedContextDataAlive()
        {
            using var parent = new Context();
            parent.Dispose();
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            int before = NexusRuntime.ActiveContexts.Count;
            bool rejected = false;
            try { using var child = ContextFactory.Create(parent, data); }
            catch (ObjectDisposedException) { rejected = true; }
            bool alive = data;
            bool registryRestored = NexusRuntime.ActiveContexts.Count == before;
            Report("337.C1. RejectedParent_PreservesCallerOwnedData_WithoutRegistryLeak",
                rejected && alive && registryRestored,
                $"rejected={rejected} callerDataAlive={alive} registryRestored={registryRestored}");
            UnityEngine.Object.Destroy(data);
        }

        private static void Test_HarnessChildCreation_RejectedShutdownDoesNotLeak(bool parentAlreadyEnded)
        {
            int before = NexusRuntime.ActiveContexts.Count;
            using var parent = NexusTestHarness.CreateContext();
            string scope = "HarnessChild337_" + Guid.NewGuid().ToString("N");
            int registrations = 0;
            void OnRegistered(IContext context)
            {
                if (!ReferenceEquals(context.Parent, parent.Context)) return;
                registrations++;
                parent.Dispose();
            }
            if (parentAlreadyEnded) parent.Dispose();
            NexusRuntime.OnContextRegistered += OnRegistered;
            bool returned = false;
            Exception error = null;
            try
            {
                using var child = NexusTestHarness.CreateChildContext(parent, scope);
                returned = true;
            }
            catch (Exception ex) { error = ex; }
            finally { NexusRuntime.OnContextRegistered -= OnRegistered; }
            var leaked = new List<ContextData>();
            foreach (var data in UnityEngine.Object.FindObjectsByType<ContextData>(UnityEngine.FindObjectsInactive.Include))
                if (data.ScopeTag == scope) leaked.Add(data);
            bool registryRestored = NexusRuntime.ActiveContexts.Count == before;
            string expectedObject = parentAlreadyEnded ? "parent" : nameof(Context);
            bool correctError = error is ObjectDisposedException disposed && disposed.ObjectName == expectedObject;
            Report($"337.C2. HarnessChild_{(parentAlreadyEnded ? "AlreadyEndedParent" : "RegistrationShutdown")}_RejectsWithoutDataLeak",
                !returned && correctError && leaked.Count == 0 && registryRestored
                    && registrations == (parentAlreadyEnded ? 0 : 1),
                $"returned={returned} leakedData={leaked.Count} registryRestored={registryRestored}"
                + $" registrations={registrations} exception={error?.GetType().Name ?? "none"} objectName={(error as ObjectDisposedException)?.ObjectName}");
            foreach (var data in leaked) UnityEngine.Object.Destroy(data);
        }

        private static void Test_PostContext_SkipsChildWhoseAsyncTeardownIsStillRunning()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            Root NewRoot(string name, Root owner = null, int priority = 0)
            {
                var root = new UnityEngine.GameObject(name).AddComponent<Root>();
                root.AutoBindGlobalParent = false;
                root.SetUp(data, owner, priority);
                return root;
            }
            var parent = NewRoot("PostParent337", priority: 10);
            var child = NewRoot("PostChild337", parent);
            var unrelated = NewRoot("PostUnrelated337");
            var startupRelease = new TaskCompletionSource<bool>();
            var ending = new PostContextProbe();
            var healthy = new PostContextProbe { InitializeGate = startupRelease.Task };
            child.RegisterLifecycle(ending);
            unrelated.RegisterLifecycle(healthy);
            var endGate = new AsyncSaveGate();
            var previous = SynchronizationContext.Current;
            var pump = new RootFramePump();
            SynchronizationContext.SetSynchronizationContext(pump);
            Task startup = null, shutdown = null;
            bool ok = false;
            string detail;
            try
            {
                foreach (var root in new[] { parent, child, unrelated })
                {
                    RootMessage(root, "Awake");
                    RootMessage(root, "OnEnable");
                }
                child.Context.Container.BindInstance(endGate);
                pump.Complete((Task)RootMessage(parent, "StartInternal"));
                pump.Complete((Task)RootMessage(child, "StartInternal"));
                startup = (Task)RootMessage(unrelated, "StartInternal");
                bool startupHeld = !startup.IsCompleted && ending.Posts == 0 && healthy.Posts == 0;
                shutdown = parent.Context.DisposeAsync().AsTask();
                bool teardownHeld = !shutdown.IsCompleted && endGate.Started && ending.Ends == 1;
                startupRelease.TrySetResult(true);
                pump.Complete(startup);
                ok = startupHeld && teardownHeld && ending.Posts == 0 && healthy.Posts == 1 && unrelated.IsInitialized;
                detail = $"startupHeld={startupHeld} teardownHeld={teardownHeld} childEnds={ending.Ends}"
                    + $" childPostsAfterEnd={ending.Posts} healthyPosts={healthy.Posts} healthyReady={unrelated.IsInitialized}";
            }
            catch (Exception ex) { detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}"; }
            finally
            {
                startupRelease.TrySetResult(true);
                endGate.Release.TrySetResult(true);
                if (shutdown != null) pump.Complete(shutdown);
                if (startup != null) pump.Complete(startup);
                foreach (var root in new[] { unrelated, child, parent })
                {
                    RootMessage(root, "OnDestroy");
                    UnityEngine.Object.Destroy(root.gameObject);
                }
                SynchronizationContext.SetSynchronizationContext(previous);
                UnityEngine.Object.Destroy(data);
            }
            Report("337.C2. PostContext_SkipsEndingChild_WhileHealthyRootCompletesStartup", ok, detail);
        }

        private static void Test_PostContext_DisposingFirstCallbackStopsRemainingCallbacks()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            using var target = new Context(contextData: data);
            using var healthy = new Context(contextData: data);
            var first = new PostContextProbe { OnPost = target.Dispose };
            var second = new PostContextProbe();
            var unrelated = new PostContextProbe();
            void Initialize(Context context, params IContextLifecycle[] lifecycles)
            {
                context.Configure(lifecycles);
                var method = typeof(Context).GetMethod("InitializeLifecycleAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ((ValueTask)method.Invoke(context, new object[] { lifecycles, CancellationToken.None })).GetAwaiter().GetResult();
            }
            try
            {
                Initialize(target, first, second);
                Initialize(healthy, unrelated);
                NexusRuntime.FinalizeInitializationAsync().GetAwaiter().GetResult();
                Report("337.C2. PostContext_DisposeInFirstCallback_SkipsLaterCallbackButNotHealthyContext",
                    first.Posts == 1 && first.Ends == 1 && second.Ends == 1 && second.Posts == 0 && unrelated.Posts == 1,
                    $"firstPosts={first.Posts} firstEnds={first.Ends} secondEnds={second.Ends}"
                    + $" secondPostsAfterEnd={second.Posts} healthyPosts={unrelated.Posts}");
            }
            finally { UnityEngine.Object.Destroy(data); }
        }
    }
}
