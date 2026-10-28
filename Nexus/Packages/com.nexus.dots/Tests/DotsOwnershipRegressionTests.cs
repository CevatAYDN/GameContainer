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
