using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nexus.Core.Tests
{
    public sealed class ContextHierarchyShutdownTests
    {
        private ContextData _data;
        private string _salt;

        [SetUp]
        public void SetUp()
        {
            _data = ScriptableObject.CreateInstance<ContextData>();
            _data.EnableAutoDiscovery = false;
            _salt = "Hierarchy337_" + Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown()
        {
            try { Object.DestroyImmediate(_data); }
            finally
            {
                const string prefix = "Hierarchy337_";
                if (_salt != null)
                {
                    if (!_salt.StartsWith(prefix, StringComparison.Ordinal)
                        || !Guid.TryParseExact(_salt.Substring(prefix.Length), "N", out _))
                        throw new InvalidOperationException("Refusing to clean a storage salt not owned by this fixture.");
                    var root = Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var folder = Path.GetFullPath(Path.Combine(root, "SecureData_" + _salt));
                    if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Refusing to clean storage outside this fixture's unique directory.");
                    PlayerPrefs.DeleteKey("NT_StorageSeed_" + _salt);
                    PlayerPrefs.DeleteKey("NT_StorageSeed_" + _salt + "_App");
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
            }
        }

        private sealed class PendingSave : IContextLifecycle
        {
            private readonly Func<EncryptedStorageService> _storage;
            private readonly string _key;
            private readonly int _value;
            public int DisposeCalls;

            public PendingSave(Func<EncryptedStorageService> storage, string key, int value)
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
                var storage = _storage();
                storage.SetInt(_key, _value);
                storage.Save();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ParentFirstShutdown_FlushesChildAndGrandchildToStillLiveStorage_Once(bool asynchronous)
        {
            using var parent = new Context(contextData: _data);
            using var child = new Context(parent, _data);
            using var grandchild = new Context(child, _data);
            using var storage = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            parent.Container.BindInstance(storage);
            var childSave = new PendingSave(() => child.Resolve<EncryptedStorageService>(), "map", 337);
            var grandchildSave = new PendingSave(() => grandchild.Resolve<EncryptedStorageService>(), "zone", 733);
            child.Configure(new[] { childSave });
            grandchild.Configure(new[] { grandchildSave });

            if (asynchronous) parent.DisposeAsync().GetAwaiter().GetResult();
            else parent.Dispose();

            using var reopened = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            Assert.AreEqual(337, reopened.GetInt("map", -1));
            Assert.AreEqual(733, reopened.GetInt("zone", -1));
            Assert.IsTrue(child.IsDisposed);
            Assert.IsTrue(grandchild.IsDisposed);
            child.Dispose();
            grandchild.Dispose();
            parent.Dispose();
            Assert.AreEqual(1, childSave.DisposeCalls);
            Assert.AreEqual(1, grandchildSave.DisposeCalls);
        }

        private Root NewRoot(string name, Root parent = null, int priority = 0)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            root.AutoBindGlobalParent = false;
            root.SetUp(_data, parent, priority);
            return root;
        }

        [UnityTest]
        public IEnumerator ParentDestroyImmediateFirst_FlushesDisabledChildRootsGrandchild_Once()
        {
            var parent = NewRoot("Parent337");
            var child = NewRoot("Child337", parent);
            var grandchild = NewRoot("Grandchild337", child);
            using var storage = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            var save = new PendingSave(() => grandchild.Context.Resolve<EncryptedStorageService>(), "factory", 337);
            try
            {
                grandchild.RegisterLifecycle(save);
                parent.gameObject.SetActive(true);
                parent.Context.Container.BindInstance(storage);
                child.gameObject.SetActive(true);
                grandchild.gameObject.SetActive(true);
                yield return null;
                Assert.IsTrue(grandchild.IsInitialized);
                var childContext = child.Context;
                var grandchildContext = grandchild.Context;
                child.enabled = false;

                Object.DestroyImmediate(parent.gameObject);
                using (var reopened = new EncryptedStorageService(_salt, StorageKeyBinding.App))
                    Assert.AreEqual(337, reopened.GetInt("factory", -1));
                Assert.IsTrue(childContext.IsDisposed);
                Assert.IsTrue(grandchildContext.IsDisposed);
                Assert.IsFalse(child.IsInitialized, "a surviving disabled Root must not advertise dead readiness");
                Assert.IsFalse(grandchild.IsInitialized);
                Object.Destroy(child.gameObject);
                Object.Destroy(grandchild.gameObject);
                yield return null;
                Assert.AreEqual(1, save.DisposeCalls);
            }
            finally
            {
                if (grandchild != null) Object.DestroyImmediate(grandchild.gameObject);
                if (child != null) Object.DestroyImmediate(child.gameObject);
                if (parent != null) Object.DestroyImmediate(parent.gameObject);
            }
        }

        public interface IPendingFactorySave
        {
            void Request(int value);
        }

        public sealed class PendingFactorySaveService : NexusService<IPendingFactorySave>, IPendingFactorySave
        {
            [Inject] public IPlayerPrefsService Storage { get; set; }
            private bool _pending;
            private int _value;
            public int DisposeCalls;

            public void Request(int value)
            {
                _value = value;
                _pending = true;
            }

            public override void OnDispose()
            {
                DisposeCalls++;
                if (_pending)
                {
                    Storage.SetInt("factory", _value);
                    Storage.Save();
                    _pending = false;
                }
                base.OnDispose();
            }
        }

        private sealed class FactorySaveBindings : IContextLifecycle
        {
            private readonly PendingFactorySaveService _save;
            public FactorySaveBindings(PendingFactorySaveService save) => _save = save;
            public void OnConfigure(IContextBuilder builder) => builder.BindServiceInstance<IPendingFactorySave>(_save);
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        [UnityTest]
        public IEnumerator ParentAndChildDestroyQueuedInSameFrame_FlushesPendingServiceSave_Once()
        {
            var parent = NewRoot("QueuedParent337");
            var child = NewRoot("QueuedChild337", parent);
            using var storage = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            var save = new PendingFactorySaveService();
            try
            {
                child.RegisterLifecycle(new FactorySaveBindings(save));
                parent.gameObject.SetActive(true);
                parent.Context.Container.BindInstance<IPlayerPrefsService>(storage);
                child.gameObject.SetActive(true);
                double deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!child.IsInitialized && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.IsTrue(child.IsInitialized);
                Assert.AreSame(storage, save.Storage, "service startup must inject the parent's storage");
                var pending = child.Context.Resolve<IPendingFactorySave>();
                pending.Request(337);
                Assert.AreEqual(-1, storage.GetInt("factory", -1), "the save must still be pending at destruction");
                var parentContext = parent.Context;
                var childContext = child.Context;

                Object.Destroy(parent.gameObject);
                Object.Destroy(child.gameObject);
                yield return null;

                Assert.IsTrue(parent == null);
                Assert.IsTrue(child == null);
                Assert.IsTrue(parentContext.IsDisposed);
                Assert.IsTrue(childContext.IsDisposed);
                using var reopened = new EncryptedStorageService(_salt, StorageKeyBinding.App);
                Assert.AreEqual(337, reopened.GetInt("factory", -1));
                Assert.AreEqual(1, save.DisposeCalls);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (child != null) Object.DestroyImmediate(child.gameObject);
                if (parent != null) Object.DestroyImmediate(parent.gameObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ChildDisposedAlone_LeavesParentStorageLive_AndDetaches(bool asynchronous)
        {
            using var parent = new Context(contextData: _data);
            using var child = new Context(parent, _data);
            using var storage = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            parent.Container.BindInstance(storage);
            var save = new PendingSave(() => child.Resolve<EncryptedStorageService>(), "map", 337);
            child.Configure(new[] { save });
            if (asynchronous) child.DisposeAsync().GetAwaiter().GetResult();
            else child.Dispose();

            Assert.IsFalse(parent.IsDisposed);
            Assert.AreSame(storage, parent.Resolve<EncryptedStorageService>());
            storage.SetInt("stillLive", 733);
            parent.Dispose();
            using var reopened = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            Assert.AreEqual(337, reopened.GetInt("map", -1));
            Assert.AreEqual(733, reopened.GetInt("stillLive", -1));
            Assert.AreEqual(1, save.DisposeCalls);
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

        [Test]
        public void ParentLedReentrantDispose_EndsChildOnce_RejectsNewChildrenWithoutRegistryLeak()
        {
            using var parent = new Context(contextData: _data);
            using var child = new Context(parent, _data);
            bool rejectedDuringShutdown = false;
            var lifecycle = new CallbackLifecycle
            {
                OnEnd = () =>
                {
                    parent.Dispose();
                    child.Dispose();
                    parent.DisposeAsync().GetAwaiter().GetResult();
                    try { using var late = ContextFactory.Create(parent, _data); }
                    catch (ObjectDisposedException) { rejectedDuringShutdown = true; }
                }
            };
            child.Configure(new[] { lifecycle });
            parent.Dispose();
            Assert.AreEqual(1, lifecycle.Ends);
            Assert.IsTrue(rejectedDuringShutdown);
            int before = NexusRuntime.ActiveContexts.Count;
            Assert.Throws<ObjectDisposedException>(() => ContextFactory.Create(parent, _data));
            Assert.AreEqual(before, NexusRuntime.ActiveContexts.Count);
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

        private static IEnumerator UntilCompleted(Task task)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "teardown did not finish after the gate was released");
            task.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator AsyncParentShutdown_JoinsAlreadyDisposingChild_BeforeClosingStorage()
        {
            using var parent = new Context(contextData: _data);
            using var child = new Context(parent, _data);
            using var storage = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            parent.Container.BindInstance(storage);
            var gate = new AsyncSaveGate
            {
                Save = () =>
                {
                    child.Resolve<EncryptedStorageService>().SetInt("factory", 337);
                    storage.Save();
                }
            };
            child.Container.BindInstance(gate);
            var childEnd = child.DisposeAsync().AsTask();
            var parentEnd = parent.DisposeAsync().AsTask();
            try
            {
                Assert.IsTrue(gate.Started);
                Assert.IsFalse(childEnd.IsCompleted);
                Assert.IsFalse(parentEnd.IsCompleted, "the parent skipped a child already shutting down");
                parent.Dispose();
                parent.DisposeAsync().GetAwaiter().GetResult();
            }
            finally { gate.Release.TrySetResult(true); }
            yield return UntilCompleted(parentEnd);
            yield return UntilCompleted(childEnd);
            using var reopened = new EncryptedStorageService(_salt, StorageKeyBinding.App);
            Assert.AreEqual(337, reopened.GetInt("factory", -1));
            Assert.AreEqual(1, gate.Ends);
        }

        [UnityTest]
        public IEnumerator SyncParentShutdown_ReturnsBeforeAsyncOnlyChildSingletonCompletes()
        {
            using var parent = new Context(contextData: _data);
            using var child = new Context(parent, _data);
            var gate = new AsyncSaveGate();
            child.Container.BindInstance(gate);
            try
            {
                parent.Dispose();
                Assert.IsTrue(parent.IsDisposed);
                Assert.IsTrue(child.IsDisposed);
                Assert.IsTrue(gate.Started);
                Assert.AreEqual(0, gate.Ends, "synchronous shutdown waited on an async-only singleton");
            }
            finally { gate.Release.TrySetResult(true); }
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (gate.Ends == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(1, gate.Ends);
        }

        [UnityTest]
        public IEnumerator SurvivingChildRoot_StartAfterParentDestroyed_DoesNotReadDisposedLifetimeToken()
        {
            var parent = NewRoot("ParentBeforeStart337");
            var child = NewRoot("ChildBeforeStart337", parent);
            try
            {
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                var childContext = child.Context;
                Object.DestroyImmediate(parent.gameObject);
                Assert.IsTrue(childContext.IsDisposed);
                yield return null;
                Assert.IsFalse(child.IsInitialized);
                Assert.IsNull(child.Context);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (child != null) Object.DestroyImmediate(child.gameObject);
                if (parent != null) Object.DestroyImmediate(parent.gameObject);
            }
        }

        private sealed class PendingStartup : IContextLifecycle
        {
            public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Started;
            public int Ends;
            public void OnConfigure(IContextBuilder builder) { }
            public async ValueTask OnInitializeAsync(CancellationToken ct)
            {
                Started = true;
                await Release.Task;
            }
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() => Ends++;
        }

        [UnityTest]
        public IEnumerator AncestorDestroyedDuringChildStartup_StopsSurvivingChildWithoutRestartingIt()
        {
            var parent = NewRoot("ParentDuringStart337");
            var child = NewRoot("ChildDuringStart337", parent);
            var pending = new PendingStartup();
            try
            {
                child.RegisterLifecycle(pending);
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                yield return null;
                Assert.IsTrue(pending.Started);
                Object.DestroyImmediate(parent.gameObject);
                Assert.AreEqual(1, pending.Ends);
                Assert.IsFalse(child.IsInitialized);
                pending.Release.TrySetResult(true);
                double deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (child.Context != null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.IsNull(child.Context);
                Assert.IsFalse(child.IsInitialized);
                Assert.AreEqual(1, pending.Ends);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                pending.Release.TrySetResult(true);
                if (child != null) Object.DestroyImmediate(child.gameObject);
                if (parent != null) Object.DestroyImmediate(parent.gameObject);
            }
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

        [UnityTest]
        public IEnumerator AncestorCascadeWithSurvivingEnabledChild_DoesNotBlockNewRootStartupOrPostContext()
        {
            foreach (int priority in new[] { 20, 0 })
            {
                var parent = NewRoot("EndedParent337");
                var child = NewRoot("SurvivingChild337", parent, 10);
                var live = NewRoot("NewLiveRoot337", priority: priority);
                var post = new PostContextProbe();
                try
                {
                    parent.gameObject.SetActive(true);
                    child.gameObject.SetActive(true);
                    double deadline = Time.realtimeSinceStartupAsDouble + 5;
                    while (!child.IsInitialized && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                    Assert.IsTrue(child.IsInitialized);
                    Object.DestroyImmediate(parent.gameObject);
                    Assert.IsTrue(child.enabled && child.gameObject.activeInHierarchy);
                    Assert.IsFalse(child.IsInitialized);

                    live.RegisterLifecycle(post);
                    live.gameObject.SetActive(true);
                    deadline = Time.realtimeSinceStartupAsDouble + 5;
                    while (!live.IsInitialized && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                    Assert.IsTrue(live.IsInitialized, $"priority {priority}: waited for an ended sibling");
                    Assert.AreEqual(1, post.Starts);
                    Assert.AreEqual(1, post.Posts, $"priority {priority}: an ended Root blocked the post-context phase");
                    LogAssert.NoUnexpectedReceived();
                }
                finally
                {
                    if (live != null) Object.DestroyImmediate(live.gameObject);
                    if (child != null) Object.DestroyImmediate(child.gameObject);
                    if (parent != null) Object.DestroyImmediate(parent.gameObject);
                }
            }
        }

        [UnityTest]
        public IEnumerator FactoryRegistrationObserverDisposesParent_DoesNotLeakGeneratedDataAfterFailedStartup()
        {
            int before = NexusRuntime.ActiveContexts.Count;
            using var parent = new Context(contextData: _data);
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
            try
            {
                var startup = ContextFactory.StartAsync("RegistrationShutdown337", _ => configured = true, parent);
                var error = Assert.Throws<ObjectDisposedException>(() => startup.GetAwaiter().GetResult());
                yield return null; // Runtime-created ScriptableObjects use Unity's deferred destruction.
                Assert.AreEqual(nameof(Context), error.ObjectName, "startup must reject the context, not read a disposed CTS");
                Assert.IsFalse(configured, "user configuration must not run on an already-ended context");
                Assert.IsNotNull(observed);
                Assert.IsTrue(observed.IsDisposed);
                Assert.IsTrue(generated == null, "the generated ContextData survived failed startup");
                Assert.AreEqual(before, NexusRuntime.ActiveContexts.Count);
                Assert.IsTrue(_data != null, "the parent's caller-owned ContextData must survive");
            }
            finally
            {
                NexusRuntime.OnContextRegistered -= OnRegistered;
                if (generated != null) Object.DestroyImmediate(generated);
            }
        }

        [Test]
        public void ConstructorRejectsEndedParent_WithoutDestroyingCallerOwnedDataOrRegisteringChild()
        {
            using var parent = new Context(contextData: _data);
            parent.Dispose();
            int before = NexusRuntime.ActiveContexts.Count;
            Assert.Throws<ObjectDisposedException>(() => ContextFactory.Create(parent, _data));
            Assert.IsTrue(_data != null);
            Assert.AreEqual(before, NexusRuntime.ActiveContexts.Count);
        }

        [UnityTest]
        public IEnumerator HarnessChildRegistrationObserverDisposesParent_RejectsWrapperAndDestroysGeneratedData()
            => CheckHarnessChildRejectedWithoutLeak(false);

        [UnityTest]
        public IEnumerator HarnessChildWithAlreadyEndedParent_RejectsWrapperAndDestroysGeneratedData()
            => CheckHarnessChildRejectedWithoutLeak(true);

        private IEnumerator CheckHarnessChildRejectedWithoutLeak(bool parentAlreadyEnded)
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
            try
            {
                var error = Assert.Throws<ObjectDisposedException>(() => NexusTestHarness.CreateChildContext(parent, scope));
                Assert.AreEqual(parentAlreadyEnded ? "parent" : nameof(Context), error.ObjectName);
                yield return null;
                foreach (var data in Resources.FindObjectsOfTypeAll<ContextData>())
                    Assert.AreNotEqual(scope, data.ScopeTag, "child creation leaked its generated ContextData");
                Assert.AreEqual(before, NexusRuntime.ActiveContexts.Count);
                Assert.AreEqual(parentAlreadyEnded ? 0 : 1, registrations);
            }
            finally
            {
                NexusRuntime.OnContextRegistered -= OnRegistered;
                foreach (var data in Resources.FindObjectsOfTypeAll<ContextData>())
                    if (data.ScopeTag == scope) Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator HealthyRootFinishingStartup_DoesNotRunPostContextAfterChildOnDispose()
        {
            var parent = NewRoot("PostParent337", priority: 10);
            var child = NewRoot("PostChild337", parent);
            var unrelated = NewRoot("PostUnrelated337");
            var startupRelease = new TaskCompletionSource<bool>();
            var ending = new PostContextProbe();
            var healthy = new PostContextProbe { InitializeGate = startupRelease.Task };
            var endGate = new AsyncSaveGate();
            Task shutdown = null;
            try
            {
                child.RegisterLifecycle(ending);
                unrelated.RegisterLifecycle(healthy);
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                unrelated.gameObject.SetActive(true);
                child.Context.Container.BindInstance(endGate);
                double deadline = Time.realtimeSinceStartupAsDouble + 5;
                while ((!parent.IsInitialized || !child.IsInitialized) && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                Assert.IsTrue(parent.IsInitialized && child.IsInitialized);
                Assert.IsFalse(unrelated.IsInitialized);
                Assert.AreEqual(0, ending.Posts);
                Assert.AreEqual(0, healthy.Posts);

                shutdown = parent.Context.DisposeAsync().AsTask();
                Assert.IsFalse(shutdown.IsCompleted);
                Assert.IsTrue(endGate.Started);
                Assert.AreEqual(1, ending.Ends);
                startupRelease.TrySetResult(true);
                deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!unrelated.IsInitialized && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.IsTrue(unrelated.IsInitialized);
                Assert.AreEqual(1, healthy.Posts);
                Assert.AreEqual(0, ending.Posts, "the child's OnPostContext ran after its OnDispose");
                Assert.AreEqual(1, ending.Ends);
                endGate.Release.TrySetResult(true);
                yield return UntilCompleted(shutdown);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                startupRelease.TrySetResult(true);
                endGate.Release.TrySetResult(true);
                if (unrelated != null) Object.DestroyImmediate(unrelated.gameObject);
                if (child != null) Object.DestroyImmediate(child.gameObject);
                if (parent != null) Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public async Task FirstPostContextCallbackDisposesTarget_SkipsLaterCallbacksButKeepsHealthyContext()
        {
            using var target = new Context(contextData: _data);
            using var healthy = new Context(contextData: _data);
            var first = new PostContextProbe { OnPost = target.Dispose };
            var second = new PostContextProbe();
            var unrelated = new PostContextProbe();
            var callbacks = new IContextLifecycle[] { first, second };
            target.Configure(callbacks);
            healthy.Configure(new[] { unrelated });
            await target.InitializeLifecycleAsync(callbacks, CancellationToken.None);
            await healthy.InitializeLifecycleAsync(new[] { unrelated }, CancellationToken.None);

            await NexusRuntime.FinalizeInitializationAsync();

            Assert.AreEqual(1, first.Posts);
            Assert.AreEqual(1, first.Ends);
            Assert.AreEqual(1, second.Ends);
            Assert.AreEqual(0, second.Posts, "target disposal must stop the current callback loop");
            Assert.AreEqual(1, unrelated.Posts, "healthy contexts must still receive their post-context phase");
        }
    }
}
