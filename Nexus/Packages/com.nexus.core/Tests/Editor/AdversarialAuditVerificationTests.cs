using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
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
    }
}
