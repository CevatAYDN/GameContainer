using NUnit.Framework;
using Nexus.Core;
using System.Threading;

namespace Nexus.Tests
{
    [TestFixture]
    public class CrossContextTests
    {
        [CrossContext]
        public readonly struct GlobalSignal
        {
            public readonly int Value;
            public GlobalSignal(int value) => Value = value;
        }

        [CrossContext(ScopeTag = "Gameplay")]
        public readonly struct ScopedSignal
        {
            public readonly string Data;
            public ScopedSignal(string data) => Data = data;
        }

        public readonly struct LocalSignal { }

        private int _receivedValue;
        private int _scopedReceivedCount;

        [SetUp]
        public void Setup()
        {
            _receivedValue = 0;
            _scopedReceivedCount = 0;
        }

        [Test]
        public void CrossContextSignal_BroadcastsToOtherContexts()
        {
            using (var ctx1 = new Context())
            using (var ctx2 = new Context())
            {
                ctx2.SignalBus.Subscribe<GlobalSignal>(sig => _receivedValue = sig.Value);

                ctx1.SignalBus.Fire(new GlobalSignal(42));

                Assert.AreEqual(42, _receivedValue);
            }
        }

        [Test]
        public void CrossContextSignal_WithScopeTag_FiltersByTag()
        {
            using (var ctx1 = new Context())
            using (var ctx2 = new Context())
            {
                // ctx2 subscribes to ScopedSignal
                ctx2.SignalBus.Subscribe<ScopedSignal>(sig => _scopedReceivedCount++);

                // Fire on ctx1 — ctx2 has no ScopeTag set, so it won't receive scoped signals
                ctx1.SignalBus.Fire(new ScopedSignal("test"));

                Assert.AreEqual(0, _scopedReceivedCount);
            }
        }

        [Test]
        public void LocalSignal_DoesNotBroadcast()
        {
            using (var ctx1 = new Context())
            using (var ctx2 = new Context())
            {
                int received = 0;
                ctx2.SignalBus.Subscribe<LocalSignal>(sig => received++);

                ctx1.SignalBus.Fire(new LocalSignal());

                Assert.AreEqual(0, received);
            }
        }

        [Test]
        public void CrossContextSignal_SenderDoesNotReceiveOwnBroadcast()
        {
            using (var ctx = new Context())
            {
                int received = 0;
                ctx.SignalBus.Subscribe<GlobalSignal>(sig => received++);

                // Fire on the same context that has the subscription
                ctx.SignalBus.Fire(new GlobalSignal(99));

                // Context should receive its own signal via normal dispatch, not cross-context
                // The cross-context broadcast skips the sender context
                Assert.AreEqual(1, received);
            }
        }
    }
}
