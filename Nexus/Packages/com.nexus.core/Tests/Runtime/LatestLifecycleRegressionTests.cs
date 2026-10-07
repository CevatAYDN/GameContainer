using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Nexus.Core.Components;

namespace Nexus.Core.Tests
{
    public sealed class LifecycleDependency { }
    public struct LifecycleSignal { }
    public sealed class LateContextBehaviour : NexusBehaviour
    {
        [Inject] public LifecycleDependency Dependency { get; set; }
        public int AwakeCount, StartCount, DestroyCount, Signals;
        protected override void OnNexusAwake()
        {
            Assert.IsNotNull(Dependency);
            AwakeCount++;
            Subscribe<LifecycleSignal>(_ => Signals++);
        }
        protected override void OnNexusStart() => StartCount++;
        protected override void OnNexusDestroy() => DestroyCount++;
    }
    public sealed class BindingReceiver : MonoBehaviour
    {
        [Inject] public LifecycleDependency Dependency { get; set; }
    }

    public sealed class LatestLifecycleRegressionTests
    {
        [SetUp] public void SetUp() => NexusRuntime.Reset();
        [TearDown] public void TearDown() => NexusRuntime.Reset();

        [UnityTest]
        public IEnumerator BehaviourAwakeBeforeCodeFirstStartup_RetriesAndStartsExactlyOnce()
        {
            var go = new GameObject("Early consumer");
            Context context = null;
            try
            {
                var consumer = go.AddComponent<LateContextBehaviour>();
                yield return null;
                Assert.AreEqual(0, consumer.AwakeCount);
                Assert.AreEqual(0, consumer.StartCount);
                var dependency = new LifecycleDependency();
                var boot = ContextFactory.StartAsync("Late", builder => builder.BindInstance(dependency));
                while (!boot.IsCompleted) yield return null;
                context = boot.GetAwaiter().GetResult();
                Assert.AreSame(dependency, consumer.Dependency);
                Assert.AreEqual(1, consumer.AwakeCount);
                Assert.AreEqual(1, consumer.StartCount);
                consumer.InitializeLifecycle();
                context.SignalBus.Fire(new LifecycleSignal());
                Assert.AreEqual(1, consumer.Signals);
                consumer.DestroyLifecycle();
                consumer.DestroyLifecycle();
                Assert.AreEqual(1, consumer.DestroyCount);
                context.SignalBus.Fire(new LifecycleSignal());
                Assert.AreEqual(1, consumer.Signals);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); context?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator ParentScopeIsAuthoritative_RegistrationOfUnrelatedContextDoesNotInject()
        {
            var parent = new GameObject("Pending parent");
            parent.SetActive(false);
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var root = parent.AddComponent<Root>();
            root.SetUp(data);
            var child = new GameObject("Consumer");
            child.transform.SetParent(parent.transform);
            var receiver = child.AddComponent<BindingReceiver>();
            var binding = child.AddComponent<NexusBinding>();
            Context unrelated = null;
            try
            {
                binding.InjectNow();
                var boot = ContextFactory.StartAsync("Unrelated", b => b.BindInstance(new LifecycleDependency()));
                while (!boot.IsCompleted) yield return null;
                unrelated = boot.GetAwaiter().GetResult();
                Assert.IsNull(receiver.Dependency);
                var own = new LifecycleDependency();
                root.RegisterLifecycle(new ConfigureDependency(own));
                parent.SetActive(true);
                yield return null;
                Assert.AreSame(own, receiver.Dependency);
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); unrelated?.Dispose(); UnityEngine.Object.DestroyImmediate(data); }
        }

        [UnityTest]
        public IEnumerator DisposedContext_ClearsReferencesAndSubscriptions_ReplacementReinjects()
        {
            var go = new GameObject("Persistent consumer");
            Context first = null, second = null;
            try
            {
                var consumer = go.AddComponent<LateContextBehaviour>();
                var boot = ContextFactory.StartAsync("First", b => b.BindInstance(new LifecycleDependency()));
                while (!boot.IsCompleted) yield return null;
                first = boot.GetAwaiter().GetResult();
                Assert.IsNotNull(consumer.Dependency);
                first.Dispose();
                Assert.IsNull(consumer.Context);
                Assert.IsNull(consumer.Dependency);
                var replacement = new LifecycleDependency();
                boot = ContextFactory.StartAsync("Replacement", b => b.BindInstance(replacement));
                while (!boot.IsCompleted) yield return null;
                second = boot.GetAwaiter().GetResult();
                Assert.AreSame(replacement, consumer.Dependency);
                second.SignalBus.Fire(new LifecycleSignal());
                Assert.AreEqual(1, consumer.Signals);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); first?.Dispose(); second?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator SerializedGlobalFlag_IsDiscoveredBeforeGlobalAwake()
        {
            var group = new GameObject("Load group");
            group.SetActive(false);
            var sceneGo = new GameObject("First scene root");
            var globalGo = new GameObject("Later global root");
            sceneGo.transform.SetParent(group.transform);
            globalGo.transform.SetParent(group.transform);
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var scene = sceneGo.AddComponent<Root>();
            var global = globalGo.AddComponent<Root>();
            scene.SetUp(data);
            global.SetUp(data);
            typeof(Root).GetField("isGlobalContext", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(global, true);
            try
            {
                group.SetActive(true);
                yield return null;
                Assert.AreSame(global, scene.ParentRoot);
                Assert.AreSame(global.Context, scene.Context.Parent);
                Assert.IsNull(global.transform.parent);
                Assert.IsTrue(global.Context.IsInitialized);
            }
            finally { UnityEngine.Object.DestroyImmediate(group); UnityEngine.Object.DestroyImmediate(globalGo); UnityEngine.Object.DestroyImmediate(data); }
        }

        [Test]
        public void DuplicateGlobalRegistration_IsRejectedWithoutChangingOwner()
        {
            var firstGo = new GameObject("First"); firstGo.SetActive(false);
            var secondGo = new GameObject("Second"); secondGo.SetActive(false);
            try
            {
                var first = firstGo.AddComponent<Root>(); first.IsGlobalContext = true;
                var second = secondGo.AddComponent<Root>();
                Assert.Throws<InvalidOperationException>(() => second.IsGlobalContext = true);
                Assert.AreSame(first, NexusRuntime.GlobalRoot);
                Assert.IsFalse(second.IsGlobalContext);
            }
            finally { UnityEngine.Object.DestroyImmediate(firstGo); UnityEngine.Object.DestroyImmediate(secondGo); }
        }

        [UnityTest]
        public IEnumerator FailedGlobalStartup_ReleasesGlobalAndSiblingOwnershipForReplacement()
        {
            var failedGo = new GameObject("A failed global"); failedGo.SetActive(false);
            var replacementGo = new GameObject("Z replacement global"); replacementGo.SetActive(false);
            var data = ScriptableObject.CreateInstance<ContextData>(); data.EnableAutoDiscovery = false;
            try
            {
                var failed = failedGo.AddComponent<Root>();
                failed.SetUp(data);
                failed.RegisterLifecycle(new ThrowingStartup());
                failed.IsGlobalContext = true;
                failedGo.SetActive(true);
                yield return null;
                Assert.IsNull(failed.Context);
                Assert.IsNull(NexusRuntime.GlobalRoot);
                var replacement = replacementGo.AddComponent<Root>();
                replacement.SetUp(data);
                replacement.IsGlobalContext = true;
                replacementGo.SetActive(true);
                yield return null;
                Assert.AreSame(replacement, NexusRuntime.GlobalRoot);
                Assert.IsTrue(replacement.IsInitialized, "Failed roots must not remain in the sibling startup wait registry.");
            }
            finally { UnityEngine.Object.DestroyImmediate(replacementGo); UnityEngine.Object.DestroyImmediate(failedGo); UnityEngine.Object.DestroyImmediate(data); }
        }

        private sealed class ThrowingStartup : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => throw new InvalidOperationException("Expected startup failure");
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class ConfigureDependency : IContextLifecycle
        {
            private readonly LifecycleDependency _dependency;
            public ConfigureDependency(LifecycleDependency dependency) => _dependency = dependency;
            public void OnConfigure(IContextBuilder builder) => builder.BindInstance(_dependency);
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }
    }
}
