#if UNITY_COLLECTIONS
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Nexus.DOTS;

namespace Nexus.Core.Tests
{
    public struct JobSignal { public int Value; }
    public sealed class JobSignalBridge : DOTSSignalBridge<JobSignal> { }
    public struct ProduceSignals : IJobParallelFor
    {
        public NativeQueue<JobSignal>.ParallelWriter Writer;
        public void Execute(int index) => Writer.Enqueue(new JobSignal { Value = index });
    }
    public sealed class DotsOwnershipRegressionTests
    {
        [Test]
        public void Drain_CompletesRegisteredParallelProducersBeforeDispatch()
        {
            using var context = new Context();
            var go = new GameObject("Job bridge");
            try
            {
                var bridge = go.AddComponent<JobSignalBridge>();
                bridge.Initialize(context.SignalBus, Allocator.Persistent);
                int received = 0, sum = 0;
                using var subscription = context.SignalBus.Subscribe<JobSignal>(s => { received++; sum += s.Value; });
                var job = new ProduceSignals { Writer = bridge.AsParallelWriter() }.Schedule(1000, 32);
                bridge.AddProducerDependency(job);
                bridge.Drain();
                Assert.IsTrue(job.IsCompleted);
                Assert.AreEqual(1000, received);
                Assert.AreEqual(499500, sum);
                bridge.Drain();
                Assert.AreEqual(1000, received);
            }
            finally { Object.DestroyImmediate(go); }
        }
        [Test]
        public void Destroy_CompletesRegisteredProducersBeforeReleasingStorage()
        {
            using var context = new Context();
            var go = new GameObject("Job bridge teardown");
            var bridge = go.AddComponent<JobSignalBridge>();
            bridge.Initialize(context.SignalBus, Allocator.Persistent);
            var job = new ProduceSignals { Writer = bridge.AsParallelWriter() }.Schedule(10000, 32);
            bridge.AddProducerDependency(job);
            Object.DestroyImmediate(go);
            Assert.IsTrue(job.IsCompleted);
            job.Complete();
        }
        // Issue #192: Unity never calls OnDestroy on an object that was never active, so a bridge initialized on an
        // inactive object kept its Persistent queue after the object was destroyed.
        [Test]
        public void ABridgeOnAnInactiveObject_OwnsNoQueueUntilActive_SoDestroyingItNeverActiveLeaksNothing()
        {
            using var context = new Context();
            var never = new GameObject("Never active job bridge");
            never.SetActive(false);
            var idle = never.AddComponent<JobSignalBridge>();
            idle.Initialize(context.SignalBus, Allocator.Persistent);
            Assert.IsFalse(idle.IsInitialized, "no native queue before the object is active");
            Object.DestroyImmediate(never);

            var later = new GameObject("Later active job bridge");
            later.SetActive(false);
            var bridge = later.AddComponent<JobSignalBridge>();
            bridge.Initialize(context.SignalBus, Allocator.Persistent);
            later.SetActive(true);
            Assert.IsTrue(bridge.IsInitialized, "the queue is created when the object first becomes active");
            int received = 0;
            using var subscription = context.SignalBus.Subscribe<JobSignal>(_ => received++);
            bridge.Enqueue(new JobSignal());
            bridge.Drain();
            Assert.AreEqual(1, received);
            Object.DestroyImmediate(later);
            Assert.IsFalse(bridge.IsInitialized, "OnDestroy released it");
        }

        [Test]
        public void Reinitialize_CompletesProducersAndReplacesQueueWithoutDispatchingOldSignals()
        {
            using var context = new Context();
            var go = new GameObject("Job bridge reinit");
            try
            {
                var bridge = go.AddComponent<JobSignalBridge>();
                bridge.Initialize(context.SignalBus, Allocator.Persistent);
                var job = new ProduceSignals { Writer = bridge.AsParallelWriter() }.Schedule(1000, 32);
                bridge.AddProducerDependency(job);
                bridge.Initialize(context.SignalBus, Allocator.Persistent);
                Assert.IsTrue(job.IsCompleted);
                int received = 0;
                using var subscription = context.SignalBus.Subscribe<JobSignal>(_ => received++);
                bridge.Enqueue(new JobSignal());
                bridge.Drain();
                Assert.AreEqual(1, received);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
#endif
