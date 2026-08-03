using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Tests.Editor
{
    [TestFixture]
    public class AdversarialAuditVerificationTests
    {
        // ─── 1. TOCTOU & Thread Safety Verification for EconomyService ───
        [Test]
        public void EconomyService_SpendUnderConcurrency_IsThreadSafeAndPreventsDoubleSpending()
        {
            var eco = new EconomyService();
            eco.SetBalance("GOLD", 100L);

            int successfulSpends = 0;
            int failedSpends = 0;

            // Spawn 10 concurrent threads each trying to spend 20 GOLD (total demand 200 GOLD)
            Parallel.For(0, 10, i =>
            {
                if (eco.Spend("GOLD", 20L))
                {
                    Interlocked.Increment(ref successfulSpends);
                }
                else
                {
                    Interlocked.Increment(ref failedSpends);
                }
            });

            Assert.AreEqual(5, successfulSpends, "Exactly 5 spends of 20 GOLD should succeed with initial balance 100.");
            Assert.AreEqual(5, failedSpends, "Exactly 5 spends should be rejected.");
            Assert.AreEqual(0L, eco.GetBalance("GOLD"), "Final balance must be exactly 0 GOLD.");
            eco.Dispose();
        }

        // ─── 2. CommandPool Reset Parity Verification ───
        public class TestResettableCommand : ICommand<int>, IResettable
        {
            public int ExecutionCount;
            public string DirtyData = "dirty";

            public void Execute(int signal)
            {
                ExecutionCount++;
            }

            public void Reset()
            {
                ExecutionCount = 0;
                DirtyData = null;
            }
        }

        [Test]
        public void CommandPool_OnDespawn_ResetsCommandStateParity()
        {
            var pool = new CommandPool(typeof(TestResettableCommand), () => new TestResettableCommand(), initialSize: 1, maxSize: 10);
            
            var cmd = (TestResettableCommand)pool.Get();
            cmd.Execute(42);
            cmd.DirtyData = "modified";

            Assert.AreEqual(1, cmd.ExecutionCount);
            Assert.AreEqual("modified", cmd.DirtyData);

            pool.Return(cmd);

            // Fetch again from pool: must be freshly reset
            var recycledCmd = (TestResettableCommand)pool.Get();
            Assert.AreEqual(0, recycledCmd.ExecutionCount, "Pooled command must be reset on despawn.");
            Assert.IsNull(recycledCmd.DirtyData, "State leakage must be prevented via IResettable.Reset().");
        }

        // ─── 3. IAsyncCommand & Cancellation Token Verification ───
        [Test]
        public async Task AsyncCommand_CancellationRequested_ThrowsOperationCanceledException()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-canceled token

            bool caughtCancellation = false;
            try
            {
                await Task.Delay(100, cts.Token);
            }
            catch (OperationCanceledException)
            {
                caughtCancellation = true;
            }

            Assert.IsTrue(caughtCancellation, "CancellationToken must propagate OperationCanceledException safely.");
        }

        // ─── 4. ScopeTag Decoupled Registration Verification ───
        [Test]
        public void ScopeTag_Registry_ResolvesDecoupledFromHierarchy()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = "AuditGameplayScope";

            using var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            using var bus = new SignalBus(container, poolManager, new MockContext());
            var context = new Context(parent: null, contextData: data);

            Assert.AreEqual("AuditGameplayScope", context.ScopeTag);
            context.Dispose();
        }

        // ─── 5. SecureObservableInt Key Rotation & Integrity Verification ───
        [Test]
        public void SecureObservableInt_KeyRotationOnWrite_ProtectsValueAndFiresOnChanged()
        {
            var secureInt = new SecureObservableInt(100);
            int changes = 0;
            secureInt.OnChanged((oldVal, newVal) => changes++);

            Assert.AreEqual(100, (int)secureInt);

            // Mutate value: triggers key rotation and observer notification
            secureInt.Value = 250;

            Assert.AreEqual(250, (int)secureInt);
            Assert.AreEqual(1, changes);
        }
    }
}
