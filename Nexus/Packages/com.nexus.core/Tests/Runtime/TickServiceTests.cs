using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using System.Threading.Tasks;

namespace Nexus.Tests
{
    [TestFixture]
    public class TickServiceTests
    {
        private class TestTickable : ITickable
        {
            public int TickCount;
            public float LastDeltaTime;
            public void Tick(float deltaTime)
            {
                TickCount++;
                LastDeltaTime = deltaTime;
            }
        }

        private class TestFixedTickable : IFixedTickable
        {
            public int FixedTickCount;
            public float LastDeltaTime;
            public void FixedTick(float deltaTime)
            {
                FixedTickCount++;
                LastDeltaTime = deltaTime;
            }
        }

        private class TestLateTickable : ILateTickable
        {
            public int LateTickCount;
            public float LastDeltaTime;
            public void LateTick(float deltaTime)
            {
                LateTickCount++;
                LastDeltaTime = deltaTime;
            }
        }

        [Test]
        public void TickService_UpdatesRegisteredTickables()
        {
            var service = new TickService();
            var tickable = new TestTickable();

            service.RegisterTickable(tickable);
            service.OnTick(0.1f);

            Assert.AreEqual(1, tickable.TickCount);
            Assert.AreEqual(0.1f, tickable.LastDeltaTime);

            service.UnregisterTickable(tickable);
            service.OnTick(0.2f);

            Assert.AreEqual(1, tickable.TickCount);
            service.Dispose();
        }

        [Test]
        public void TickService_UpdatesRegisteredFixedAndLateTickables()
        {
            var service = new TickService();
            var fixedTickable = new TestFixedTickable();
            var lateTickable = new TestLateTickable();

            service.RegisterFixedTickable(fixedTickable);
            service.RegisterLateTickable(lateTickable);

            service.OnFixedTick(0.02f);
            service.OnLateTick(0.016f);

            Assert.AreEqual(1, fixedTickable.FixedTickCount);
            Assert.AreEqual(0.02f, fixedTickable.LastDeltaTime);
            Assert.AreEqual(1, lateTickable.LateTickCount);
            Assert.AreEqual(0.016f, lateTickable.LastDeltaTime);

            service.Dispose();
        }

        [Test]
        public void TickService_RespectsPausedState()
        {
            var service = new TickService();
            var tickable = new TestTickable();

            service.RegisterTickable(tickable);
            service.IsPaused = true;
            service.OnTick(0.1f);

            Assert.AreEqual(0, tickable.TickCount);

            service.IsPaused = false;
            service.OnTick(0.1f);

            Assert.AreEqual(1, tickable.TickCount);
            service.Dispose();
        }
    }
}
