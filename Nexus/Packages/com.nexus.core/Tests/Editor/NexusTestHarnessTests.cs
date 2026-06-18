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

        [SignalHandler(typeof(TestSignal), Priority = 1)]
        public class TestAsyncCommand : IAsyncCommand
        {
            [Inject] public TestModel Model;
            [Inject] public TestSignal Signal;

            public async ValueTask ExecuteAsync(CancellationToken ct)
            {
                await Task.Yield();
                Model.Value += Signal.Value * 2;
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

        [Test]
        public void CreateContext_WithScopeTag_CreatesScopeTag()
        {
            using (var testContext = NexusTestHarness.CreateContext("CustomScope"))
            {
                Assert.AreEqual("CustomScope", testContext.Context.ScopeTag);
            }
        }

        [Test]
        public void CreateChildContext_CreatesValidChildContext()
        {
            using (var parent = NexusTestHarness.CreateContext("ParentScope"))
            {
                using (var child = NexusTestHarness.CreateChildContext(parent, "ChildScope"))
                {
                    Assert.AreEqual("ChildScope", child.Context.ScopeTag);
                    Assert.AreSame(parent.Context, child.Context.Parent);
                }
            }
        }

        [Test]
        public void CreateContext_WithBuilderAction_BindsProperly()
        {
            using (var testContext = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindModel<TestModel>();
            }))
            {
                var model = testContext.GetModel<TestModel>();
                Assert.IsNotNull(model);
            }
        }

        [Test]
        public void RegisterCommand_CustomHelper_Works()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestModel>();
                testContext.RegisterCommand<TestCommand>();

                var model = testContext.GetModel<TestModel>();
                testContext.Dispatch(new TestSignal(10));
                Assert.AreEqual(10, model.Value);
            }
        }

        [Test]
        public async Task RegisterAsyncCommand_CustomHelper_Works()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestModel>();
                testContext.RegisterAsyncCommand<TestAsyncCommand>();

                var model = testContext.GetModel<TestModel>();
                await testContext.DispatchAsync(new TestSignal(5));
                Assert.AreEqual(10, model.Value);
            }
        }

        [Test]
        public void GetDispatchedSignalCount_TracksCount()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestSignal>();
                Assert.AreEqual(0, testContext.GetDispatchedSignalCount<TestSignal>());

                testContext.Dispatch(new TestSignal(1));
                testContext.Dispatch(new TestSignal(2));

                Assert.AreEqual(2, testContext.GetDispatchedSignalCount<TestSignal>());
            }
        }

        [Test]
        public void ClearDispatchedSignals_ClearsSignals()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestSignal>();
                testContext.Dispatch(new TestSignal(1));
                Assert.AreEqual(1, testContext.GetDispatchedSignalCount<TestSignal>());

                testContext.ClearDispatchedSignals();
                Assert.AreEqual(0, testContext.GetDispatchedSignalCount<TestSignal>());
            }
        }

        [Test]
        public void AssertSignalDispatched_ThrowsIfNotDispatched()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestSignal>();
                Assert.Throws<UnityEngine.Assertions.AssertionException>(() => testContext.AssertSignalDispatched<TestSignal>());

                testContext.Dispatch(new TestSignal(1));
                Assert.DoesNotThrow(() => testContext.AssertSignalDispatched<TestSignal>());
            }
        }

        [Test]
        public void AssertSignalNotDispatched_ThrowsIfDispatched()
        {
            using (var testContext = NexusTestHarness.CreateContext())
            {
                testContext.Register<TestSignal>();
                Assert.DoesNotThrow(() => testContext.AssertSignalNotDispatched<TestSignal>());

                testContext.Dispatch(new TestSignal(1));
                Assert.Throws<UnityEngine.Assertions.AssertionException>(() => testContext.AssertSignalNotDispatched<TestSignal>());
            }
        }
    }
}
