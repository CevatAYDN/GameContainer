using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Components;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    public sealed class ReadyReentrantBehaviour : NexusBehaviour
    {
        public int PostConstructCount, AwakeCount;
        public Task<Context> AuxiliaryBoot;
        private bool _auxiliaryRequested;

        [PostConstruct]
        public void BootAuxiliaryContext()
        {
            PostConstructCount++;
            // Set the guard BEFORE StartAsync emits its synchronous configured event.
            if (_auxiliaryRequested) return;
            _auxiliaryRequested = true;
            AuxiliaryBoot = ContextFactory.StartAsync("BehaviourAuxReadyRegression", _ => { });
        }

        protected override void OnNexusAwake() => AwakeCount++;
    }

    public sealed class ReadyReentrantBindingTarget : MonoBehaviour
    {
        public int PostConstructCount;
        public Task<Context> AuxiliaryBoot;
        public NexusBinding Binding;
        public bool ReenterManually;
        private bool _auxiliaryRequested;

        [PostConstruct]
        public void BootAuxiliaryContext()
        {
            PostConstructCount++;
            if (_auxiliaryRequested) return;
            _auxiliaryRequested = true;
            if (ReenterManually) Binding.InjectNow();
            AuxiliaryBoot = ContextFactory.StartAsync("BindingAuxReadyRegression", _ => { });
        }
    }

    public sealed class ReadyAttachmentChangingBehaviour : NexusBehaviour
    {
        public Action AwakeAction;
        public int AwakeCount, StartCount, DestroyCount;
        public IContext StartedContext;
        public void RequestUnityStart() => base.Start();
        protected override void OnNexusAwake() { AwakeCount++; AwakeAction?.Invoke(); }
        protected override void OnNexusStart() { StartCount++; StartedContext = Context; }
        protected override void OnNexusDestroy() => DestroyCount++;
    }

    public class ReadyReentrancyRegressionTests
    {
        private static Context BootImmediateContext(string name)
        {
            var boot = ContextFactory.StartAsync(name, _ => { });
            Assert.That(boot.IsCompleted, Is.True, "An empty startup must finish synchronously; never block the Editor thread.");
            return boot.GetAwaiter().GetResult();
        }

        private static Context ReadyUninitializedContext(ContextData data)
        {
            var context = ContextFactory.Create(contextData: data);
            context.Configure();
            Assert.That(context.IsInjectionReady, Is.True);
            Assert.That(context.IsInitialized, Is.False);
            return context;
        }

        private static void DisposeCompletedAuxiliary(Task<Context> boot)
        {
            if (boot != null && boot.Status == TaskStatus.RanToCompletion) boot.GetAwaiter().GetResult().Dispose();
        }

        [Test]
        public void InitializedObserverDisposal_FailsStartupAndStopsRemainingReadyListeners()
        {
            IContext observed = null;
            int disposingCalls = 0, laterCalls = 0;
            Action<IContext> first = context =>
            {
                if (context.ScopeTag != "InitializedDisposalReadyRegression") return;
                observed = context;
                disposingCalls++;
                ((Context)context).Dispose();
            };
            Action<IContext> later = context => { if (ReferenceEquals(context, observed)) laterCalls++; };
            NexusRuntime.OnContextInitialized += first;
            NexusRuntime.OnContextInitialized += later;
            Task<Context> boot = null;
            try
            {
                boot = ContextFactory.StartAsync("InitializedDisposalReadyRegression", _ => { });
                Assert.That(boot.IsCompleted, Is.True);
                Assert.That(disposingCalls, Is.EqualTo(1), "The actual initialized callback must have run.");
                Assert.That(((Context)observed).IsDisposed, Is.True);
                Assert.That(boot.IsCanceled || boot.IsFaulted, Is.True, "Startup must never successfully return its disposed owner.");
                Assert.Catch<OperationCanceledException>(() => boot.GetAwaiter().GetResult());
                Assert.That(laterCalls, Is.Zero, "No later readiness subscriber may receive the disposed context.");
            }
            finally
            {
                NexusRuntime.OnContextInitialized -= first;
                NexusRuntime.OnContextInitialized -= later;
                DisposeCompletedAuxiliary(boot);
                (observed as Context)?.Dispose();
            }
        }

        [Test]
        public void ConfiguredObserverDisposal_StopsRemainingReadyListeners()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var owner = ContextFactory.Create(contextData: data);
            int disposingCalls = 0, laterCalls = 0;
            Action<IContext> first = context =>
            {
                if (!ReferenceEquals(context, owner)) return;
                disposingCalls++;
                owner.Dispose();
            };
            Action<IContext> later = context => { if (ReferenceEquals(context, owner)) laterCalls++; };
            NexusRuntime.OnContextConfigured += first;
            NexusRuntime.OnContextConfigured += later;
            try
            {
                owner.Configure();
                Assert.That(disposingCalls, Is.EqualTo(1));
                Assert.That(owner.IsDisposed, Is.True);
                Assert.That(laterCalls, Is.Zero);
            }
            finally
            {
                NexusRuntime.OnContextConfigured -= first;
                NexusRuntime.OnContextConfigured -= later;
                owner.Dispose();
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void BehaviourPostConstructBootingAuxiliaryContext_DoesNotRepeatInjectionOrAwake()
        {
            var owner = BootImmediateContext("BehaviourOwnerReadyRegression");
            var go = new GameObject("Reentrant behaviour");
            go.SetActive(false);
            var consumer = go.AddComponent<ReadyReentrantBehaviour>();
            try
            {
                consumer.Context = owner;
                consumer.InitializeLifecycle();
                Assert.That(consumer.AuxiliaryBoot, Is.Not.Null, "PostConstruct must really trigger the nested context startup.");
                Assert.That(consumer.AuxiliaryBoot.Status, Is.EqualTo(TaskStatus.RanToCompletion));
                Assert.That(consumer.AuxiliaryBoot.GetAwaiter().GetResult().IsInitialized, Is.True);
                Assert.That(consumer.PostConstructCount, Is.EqualTo(1));
                Assert.That(consumer.AwakeCount, Is.EqualTo(1));
                consumer.InitializeLifecycle();
                Assert.That(consumer.AwakeCount, Is.EqualTo(1));
            }
            finally
            {
                consumer.DestroyLifecycle();
                DisposeCompletedAuxiliary(consumer.AuxiliaryBoot);
                UnityEngine.Object.DestroyImmediate(go);
                owner.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BindingPostConstructBootingAuxiliaryContext_InjectsExactlyOnce(bool manualReentry)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var owner = ContextFactory.Create(contextData: data);
            var go = new GameObject("Reentrant binding");
            go.SetActive(false);
            var binding = go.AddComponent<NexusBinding>();
            var target = go.AddComponent<ReadyReentrantBindingTarget>();
            target.Binding = binding;
            target.ReenterManually = manualReentry;
            try
            {
                binding.InjectNow(owner);
                Assert.That(target.PostConstructCount, Is.Zero, "Injection must wait for configuration.");
                owner.Configure();
                Assert.That(target.AuxiliaryBoot, Is.Not.Null);
                Assert.That(target.AuxiliaryBoot.Status, Is.EqualTo(TaskStatus.RanToCompletion));
                Assert.That(target.PostConstructCount, Is.EqualTo(1));
                binding.InjectNow(owner);
                Assert.That(target.PostConstructCount, Is.EqualTo(1));
            }
            finally
            {
                DisposeCompletedAuxiliary(target.AuxiliaryBoot);
                UnityEngine.Object.DestroyImmediate(go);
                owner.Dispose();
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void ClearingExplicitContextAfterDisposal_AllowsAutomaticAttachmentToNewSoleContext()
        {
            Assert.That(NexusRuntime.ActiveContexts, Is.Empty, "Automatic selection requires an isolated runtime registry.");
            var original = BootImmediateContext("ExplicitOwnerReadyRegression");
            var go = new GameObject("Clear explicit ownership");
            go.SetActive(false);
            var consumer = go.AddComponent<ReadyAttachmentChangingBehaviour>();
            consumer.AutoInject = false;
            Context replacement = null;
            try
            {
                consumer.Context = original;
                consumer.InitializeLifecycle();
                consumer.RequestUnityStart();
                Assert.That(consumer.Context, Is.SameAs(original));
                Assert.That(consumer.AwakeCount, Is.EqualTo(1));
                Assert.That(consumer.StartCount, Is.EqualTo(1));

                original.Dispose();
                Assert.That(consumer.Context, Is.Null, "Disposal must have released the cached explicit owner.");
                Assert.That(NexusRuntime.ActiveContexts, Is.Empty);
                consumer.Context = null;

                replacement = BootImmediateContext("AutomaticReplacementReadyRegression");
                CollectionAssert.AreEqual(new IContext[] { replacement }, NexusRuntime.ActiveContexts);
                Assert.That(consumer.Context, Is.SameAs(replacement));
                Assert.That(consumer.AwakeCount, Is.EqualTo(2), "The replacement must really trigger a new attachment.");
                Assert.That(consumer.StartCount, Is.EqualTo(2));
                Assert.That(consumer.StartedContext, Is.SameAs(replacement));
                consumer.InitializeLifecycle();
                Assert.That(consumer.AwakeCount, Is.EqualTo(2));
                Assert.That(consumer.StartCount, Is.EqualTo(2));
            }
            finally
            {
                consumer.DestroyLifecycle();
                UnityEngine.Object.DestroyImmediate(go);
                replacement?.Dispose();
                original.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AwakeChangingAttachmentOrDestroyingLifecycle_DoesNotRunStaleStart(bool destroy)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var pending = ContextFactory.Create(contextData: data);
            var original = BootImmediateContext("OriginalAttachmentReadyRegression");
            var replacement = ReadyUninitializedContext(data);
            var go = new GameObject("Changing attachment");
            go.SetActive(false);
            var consumer = go.AddComponent<ReadyAttachmentChangingBehaviour>();
            consumer.AutoInject = false;
            try
            {
                consumer.Context = pending;
                consumer.InitializeLifecycle();
                consumer.RequestUnityStart();
                Assert.That(consumer.AwakeCount, Is.Zero);
                consumer.AwakeAction = () =>
                {
                    if (!ReferenceEquals(consumer.Context, original)) return;
                    if (destroy) consumer.DestroyLifecycle();
                    else consumer.Context = replacement;
                };
                consumer.Context = original;
                Assert.That(consumer.AwakeCount, Is.GreaterThanOrEqualTo(1), "The ownership-changing Awake callback must run.");
                Assert.That(consumer.StartCount, Is.Zero, "The old initialized owner must not unlock Start for a changed/destroyed attachment.");
                if (destroy)
                {
                    Assert.That(consumer.DestroyCount, Is.EqualTo(1));
                    consumer.InitializeLifecycle();
                    consumer.RequestUnityStart();
                    Assert.That(consumer.StartCount, Is.Zero);
                }
                else
                {
                    Assert.That(consumer.Context, Is.SameAs(replacement));
                    var startup = replacement.InitializeLifecycleAsync(replacement.ConfiguredLifecycles, replacement.LifetimeToken);
                    Assert.That(startup.IsCompleted, Is.True);
                    startup.GetAwaiter().GetResult();
                    NexusRuntime.NotifyContextInitialized(replacement);
                    Assert.That(consumer.AwakeCount, Is.EqualTo(2));
                    Assert.That(consumer.StartCount, Is.EqualTo(1));
                    Assert.That(consumer.StartedContext, Is.SameAs(replacement));
                }
            }
            finally
            {
                consumer.DestroyLifecycle();
                UnityEngine.Object.DestroyImmediate(go);
                replacement.Dispose();
                original.Dispose();
                pending.Dispose();
                UnityEngine.Object.DestroyImmediate(data);
            }
        }
    }
}
