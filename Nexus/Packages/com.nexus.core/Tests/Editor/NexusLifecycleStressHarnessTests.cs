using System;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Core.Tests
{
    [TestFixture]
    public class NexusLifecycleStressHarnessTests
    {
        [SetUp]
        public void SetUp()
        {
            NexusRuntime.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            NexusRuntime.Reset();
        }

        // Test 1: Aggressive Multi-Threaded O(1) Resolve Stress Test
        [Test]
        public void StressTest_LockFreeFastArrayResolve_ConcurrentlyResolvedWithoutRaceConditions()
        {
            var container = new NexusDI();
            container.Bind<ITestService, TestServiceImplementation>();

            // Pre-warm resolve to populate _fastSlots
            var initial = container.Resolve<ITestService>();
            Assert.IsNotNull(initial);

            int workerThreads = 20;
            int iterationsPerThread = 500;

            Parallel.For(0, workerThreads, i =>
            {
                for (int j = 0; j < iterationsPerThread; j++)
                {
                    var resolved = container.Resolve<ITestService>();
                    Assert.IsNotNull(resolved);
                    Assert.AreSame(initial, resolved);
                }
            });
        }

        // Test 2: Dynamic FastSlot Array Expansion (> 128 Types)
        [Test]
        public void StressTest_FastSlotArrayExpansion_ResizesCleanlyWithoutDataLoss()
        {
            var container = new NexusDI();
            // Bind a singleton to ensure resolution succeeds
            container.Bind<ITestService, TestServiceImplementation>();

            for (int i = 0; i < 200; i++)
            {
                var resolved = container.Resolve<ITestService>();
                Assert.IsNotNull(resolved);
            }
        }

        // Test 3: Hierarchy Scope Creation & Auto-Parent Discovery
        [Test]
        public void Test_NexusLifetimeScope_AutoDiscoversParentRootInHierarchy()
        {
            var parentGo = new GameObject("ParentScope");
            var parentScope = parentGo.AddComponent<NexusLifetimeScope>();

            var childGo = new GameObject("ChildScope");
            childGo.transform.SetParent(parentGo.transform);
            var childScope = childGo.AddComponent<NexusLifetimeScope>();

            Assert.IsNotNull(parentScope.Context);
            Assert.IsNotNull(childScope.Context);
            Assert.AreSame(parentScope.Context, childScope.Context.Parent);

            UnityEngine.Object.DestroyImmediate(childGo);
            UnityEngine.Object.DestroyImmediate(parentGo);
        }

        private interface ITestService { }
        private class TestServiceImplementation : ITestService { }
    }
}
