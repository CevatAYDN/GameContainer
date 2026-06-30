using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine.Scripting;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class SignalBusTests
    {
        /// <summary>
        /// Instance-based result collector used by test commands to report execution state.
        /// Replaces static fields to prevent test pollution under parallel execution.
        /// </summary>
        public class TestResults
        {
            public int ExecutedCount;
            public int LastExecutedValue;
            public int PriorityRunOrder;
            public int FirstExecutedPriority;
            public int SecondExecutedPriority;
            public int GenericExecutedCount;
            public int GenericLastExecutedValue;
            public int AsyncExecutionCount;
        }

        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private MockContext _context;
        private SignalBus _signalBus;
        private TestResults _results;

        public struct SimpleSignal
        {
            public int Value;
            public SimpleSignal(int value) => Value = value;
        }

        public class MockContext : IContext
        {
            public ISignalBus SignalBus => null;
            public CancellationToken LifetimeToken => CancellationToken.None;
            public string ScopeTag => null;
            public IContext Parent => null;
            public void RegisterView(IView view) { }
            public void UnregisterView(IView view) { }
            public T Resolve<T>() where T : class => null;
            public void RegisterPlugin(INexusPlugin plugin) { }
            public void RemovePlugin(INexusPlugin plugin) { }
            public void Dispose() { }
        }

        public class TestCommand : ICommand
        {
            public SimpleSignal Signal;
            [Inject] private TestResults _results;
            
            public void Execute()
            {
                _results.ExecutedCount++;
                _results.LastExecutedValue = Signal.Value;
            }
        }

        public class HighPriorityCommand : ICommand
        {
            [Inject] private TestResults _results;

            public void Execute()
            {
                _results.PriorityRunOrder++;
                _results.FirstExecutedPriority = _results.PriorityRunOrder;
            }
        }

        public class LowPriorityCommand : ICommand
        {
            [Inject] private TestResults _results;

            public void Execute()
            {
                _results.PriorityRunOrder++;
                _results.SecondExecutedPriority = _results.PriorityRunOrder;
            }
        }

        public class ReentrantCommand : ICommand
        {
#pragma warning disable 0649
            [Inject] private ISignalBus _signalBus;
#pragma warning restore 0649

            public void Execute()
            {
                _signalBus.Fire(new SimpleSignal(10));
            }
        }

        public class ConcurrentSyncCommand : ICommand
        {
            public SimpleSignal Signal;
            [Inject] private TestResults _results;

            public void Execute()
            {
                _results.ExecutedCount++;
                _results.LastExecutedValue = Signal.Value;
            }
        }

        [SetUp]
        public void Setup()
        {
            _results = new TestResults();

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);

            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
            _container.BindInstance(_results);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void Fire_ExecutesRegisteredCommandAndInjectsSignal()
        {
            _container.Bind<TestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 0, isAsync: false);

            _signalBus.Fire(new SimpleSignal(42));

            Assert.AreEqual(1, _results.ExecutedCount);
            Assert.AreEqual(42, _results.LastExecutedValue);
        }

        [Test]
        public void SequentialMode_ExecutesInPriorityOrder()
        {
            _container.Bind<HighPriorityCommand>(isSingleton: false);
            _container.Bind<LowPriorityCommand>(isSingleton: false);

            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(LowPriorityCommand), ExecutionMode.Sequential, 10, isAsync: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(HighPriorityCommand), ExecutionMode.Sequential, 100, isAsync: false);

            _signalBus.Fire(new SimpleSignal(5));

            Assert.AreEqual(1, _results.FirstExecutedPriority);
            Assert.AreEqual(2, _results.SecondExecutedPriority);
        }

        [Test]
        public void RegisterCommand_MixedModes_ThrowsException()
        {
            _container.Bind<TestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 0, isAsync: false);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Concurrent, 0, isAsync: false);
            });
        }

        [Test]
        public void RegisterCommand_DuplicatePriority_ThrowsException()
        {
            _container.Bind<TestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 10, isAsync: false);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 10, isAsync: false);
            });
        }

        [Test]
        public void ReentrancyProtection_StackOverflow_ThrowsNexusReentrancyException()
        {
            _container.Bind<ReentrantCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(ReentrantCommand), ExecutionMode.Sequential, 0, isAsync: false);

            Assert.Throws<NexusReentrancyException>(() =>
            {
                _signalBus.Fire(new SimpleSignal(1));
            });
        }

        [Test]
        public void Subscribe_InvokesHandlerOnFire()
        {
            int subValue = 0;
            _signalBus.Subscribe<SimpleSignal>(sig => subValue = sig.Value);

            _signalBus.Fire(new SimpleSignal(99));

            Assert.AreEqual(99, subValue);
        }

        [Test]
        public async Task FireAsync_ConcurrentSyncCommand_ExecutesCommand()
        {
            _container.Bind<ConcurrentSyncCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(ConcurrentSyncCommand), ExecutionMode.Concurrent, 0, isAsync: false);

            await _signalBus.FireAsync(new SimpleSignal(77));

            Assert.AreEqual(1, _results.ExecutedCount);
            Assert.AreEqual(77, _results.LastExecutedValue);
        }

        public class GenericTestCommand : ICommand<SimpleSignal>
        {
            [Inject] private TestResults _results;

            public void Execute(SimpleSignal signal)
            {
                _results.GenericExecutedCount++;
                _results.GenericLastExecutedValue = signal.Value;
            }
        }

        public class MockDependencyAdapter : IDependencyAdapter
        {
            public bool IsRegisteredCalled = false;
            public bool ResolveCalled = false;
            public bool InjectCalled = false;

            public bool IsRegistered(Type type)
            {
                IsRegisteredCalled = true;
                return type == typeof(string);
            }

            public object Resolve(Type type)
            {
                ResolveCalled = true;
                if (type == typeof(string))
                    return "InjectedExternalString";
                return null;
            }

            public void Inject(object instance)
            {
                InjectCalled = true;
            }
        }

        [Test]
        public void GenericCommand_ExecutesAndInjectsSignalWithoutReflection()
        {
            _container.Bind<GenericTestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(GenericTestCommand), ExecutionMode.Sequential, 0, isAsync: false);

            _signalBus.Fire(new SimpleSignal(88));

            Assert.AreEqual(1, _results.GenericExecutedCount);
            Assert.AreEqual(88, _results.GenericLastExecutedValue);
        }

        [Test]
        public void FluentBindingAPI_RegistersCommandCorrectly()
        {
            var builder = new ContextBuilder(_container, _signalBus);
            builder.BindSignal<SimpleSignal>().To<GenericTestCommand>();

            _signalBus.Fire(new SimpleSignal(12));

            Assert.AreEqual(1, _results.GenericExecutedCount);
            Assert.AreEqual(12, _results.GenericLastExecutedValue);
        }

        [Test]
        public void IDependencyAdapter_DelegatesResolvesAndInjections()
        {
            var adapter = new MockDependencyAdapter();
            _container.ExternalAdapter = adapter;

            var resolvedString = _container.Resolve<string>();

            Assert.IsTrue(adapter.IsRegisteredCalled);
            Assert.IsTrue(adapter.ResolveCalled);
            Assert.AreEqual("InjectedExternalString", resolvedString);

            var cmd = new GenericTestCommand();
            _container.Inject(cmd);
            Assert.IsTrue(adapter.InjectCalled);
        }

        public class AsyncTestCommand : IAsyncCommand<SimpleSignal>
        {
            [Inject] private TestResults _results;
            public async ValueTask ExecuteAsync(SimpleSignal signal, CancellationToken ct)
            {
                _results.AsyncExecutionCount++;
                await default(ValueTask);
            }
        }

        [Test]
        public void RegisterCommand_AsyncCommandAsSync_ThrowsException()
        {
            _container.Bind<AsyncTestCommand>(isSingleton: false);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(AsyncTestCommand), ExecutionMode.Sequential, 0, isAsync: false);
            });
        }

        [Test]
        public void Fire_WithAsyncHandlers_ThrowsInDevelopmentBuild()
        {
            _container.Bind<AsyncTestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(AsyncTestCommand), ExecutionMode.Sequential, 0, isAsync: true);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.Fire(new SimpleSignal(1));
            });
        }

        [Test]
        public async Task FireAsync_AsyncCommand_ExecutesSuccessfully()
        {
            _container.Bind<AsyncTestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(AsyncTestCommand), ExecutionMode.Sequential, 0, isAsync: true);

            await _signalBus.FireAsync(new SimpleSignal(1));

            Assert.AreEqual(1, _results.AsyncExecutionCount);
        }
    }
}
