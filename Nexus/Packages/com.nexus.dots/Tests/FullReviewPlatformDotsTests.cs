#if UNITY_COLLECTIONS && UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Nexus.Core;
using Nexus.DOTS;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    public struct FullReviewPlatformJobSignal
    {
        public int Value;
    }

    public sealed class FullReviewPlatformJobBridge : DOTSSignalBridge<FullReviewPlatformJobSignal> { }

    public sealed class FullReviewPlatformDotsTests
    {
        [Test]
        public void P02_ProducerScheduledDuringDispatch_IsCompletedBeforeFurtherQueueAccess()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Ignore("P02 requires native Collections safety checks; do not run the unsafe baseline without them.");
#endif
            using var context = new Context();
            var go = new GameObject("FullReviewPlatform P02");
            JobHandle producer = default;
            try
            {
                var bridge = go.AddComponent<FullReviewPlatformJobBridge>();
                bridge.Initialize(context.SignalBus, Allocator.Persistent);
                Assert.That(bridge.IsInitialized, Is.True, "Run this fixture in PlayMode so Awake allocates the queue.");
                var received = new List<int>();
                using var subscription = context.SignalBus.Subscribe<FullReviewPlatformJobSignal>(signal =>
                {
                    received.Add(signal.Value);
                    if (signal.Value != 0) return;
                    producer = new ProduceFollowUp { Writer = bridge.AsParallelWriter() }.Schedule();
                    bridge.AddProducerDependency(producer);
                });

                bridge.Enqueue(new FullReviewPlatformJobSignal { Value = 0 });
                bridge.Drain();
                bridge.Drain();

                CollectionAssert.AreEqual(new[] { 0, 1 }, received,
                    "A registered follow-up producer must be delivered once, without unsafe dequeue access.");
                Assert.That(producer.IsCompleted, Is.True);
            }
            finally
            {
                producer.Complete();
                Object.DestroyImmediate(go);
            }
        }

        private struct ProduceFollowUp : IJob
        {
            public NativeQueue<FullReviewPlatformJobSignal>.ParallelWriter Writer;
            public void Execute() => Writer.Enqueue(new FullReviewPlatformJobSignal { Value = 1 });
        }
    }
}
#endif
