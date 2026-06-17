using NUnit.Framework;
using Nexus.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class NexusTestHarnessTests
    {
        public readonly struct TestSignal
        {
            public readonly int Value;
            public TestSignal(int value) => Value = value;
        }

        public readonly struct UnregisteredSignal
        {
        }

        public class TestModel
        {
            public int Value { get; set; }
        }

        [SignalHandler(typeof(TestSignal))]
        public class TestCommand : ICommand
        {
            [Inject] public TestModel Model;
            [Inject] public TestSignal Signal;

            public void Execute()
            {
                Model.Value += Signal.Value;
            }
        }

        [Test]
        public void CreateContext_CreatesValidContext()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                Assert.IsNotNull(testContext);
                Assert.IsNotNull(testContext.Context);
            }
        }

        [Test]
        public void RegisterCommand_And_Dispatch_ExecutesCommand_And_UpdatesModel()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                // Register model and command
                testContext.Register<TestModel>();
                testContext.Register<TestCommand>();

                var model = testContext.GetModel<TestModel>();
                Assert.AreEqual(0, model.Value);

                // Dispatch signal
                testContext.Dispatch(new TestSignal(42));

                Assert.AreEqual(42, model.Value);
            }
        }

        [Test]
        public void RegisterSignal_TracksDispatchedSignals()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestSignal>();

                Assert.IsFalse(testContext.SignalWasDispatched<TestSignal>());

                testContext.Dispatch(new TestSignal(10));
                testContext.Dispatch(new TestSignal(20));

                Assert.IsTrue(testContext.SignalWasDispatched<TestSignal>());

                var signals = testContext.GetDispatchedSignals<TestSignal>();
                Assert.AreEqual(2, signals.Count);
                Assert.AreEqual(10, signals[0].Value);
                Assert.AreEqual(20, signals[1].Value);

                var lastSignal = testContext.GetLastDispatchedSignal<TestSignal>();
                Assert.AreEqual(20, lastSignal.Value);
            }
        }

        [Test]
        public void GetLastDispatchedSignal_ThrowsIfNoneFired()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestSignal>();
                Assert.Throws<InvalidOperationException>(() => testContext.GetLastDispatchedSignal<TestSignal>());
            }
        }

        [Test]
        public void UnregisteredSignal_IsNotTracked()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Dispatch(new UnregisteredSignal());
                Assert.IsFalse(testContext.SignalWasDispatched<UnregisteredSignal>());
            }
        }
    }
}
