using NUnit.Framework;
using UnityEngine;
using Nexus.Core;
using System;
using System.Threading.Tasks;
using System.Threading;

namespace Nexus.Editor.Tests
{
    public readonly struct PluginTestSignal
    {
        public readonly string Message;
        public PluginTestSignal(string message) => Message = message;
    }

    public class PluginTestCommand : ICommand
    {
        public static string LastReceivedMessage;
        public static int ExecutionCount;

        // Injected field
        private PluginTestSignal _signal;

        public void Execute()
        {
            LastReceivedMessage = _signal.Message;
            ExecutionCount++;
        }
    }

    public class TestPlugin : INexusPlugin
    {
        public NexusPluginManifest Manifest { get; }
        public bool RegisteredCalled = false;
        public bool RemovedCalled = false;
        public IPluginContext LastContext = null;

        public Action<IPluginContext> OnRegisterAction;

        public TestPlugin(string name, PluginCapabilities capabilities)
        {
            Manifest = new NexusPluginManifest(name, "1.0.0", capabilities);
        }

        public void OnPluginRegistered(IPluginContext context)
        {
            RegisteredCalled = true;
            LastContext = context;
            OnRegisterAction?.Invoke(context);
        }

        public void OnPluginRemoved()
        {
            RemovedCalled = true;
        }
    }

    public class DummyInterceptor : ISignalInterceptor
    {
        public bool Intercept(ref object signal)
        {
            if (signal is PluginTestSignal s)
            {
                if (s.Message == "BlockMe")
                {
                    return false; // blocks
                }
                // Modify message
                signal = new PluginTestSignal(s.Message + " Intercepted");
            }
            return true;
        }
    }

    public class DummyDecorator : ICommandDecorator
    {
        public static int BeforeCount = 0;
        public static int AfterCount = 0;

        public void DecorateExecute(object command, Action next)
        {
            BeforeCount++;
            next();
            AfterCount++;
        }

        public async ValueTask DecorateExecuteAsync(object command, Func<ValueTask> next)
        {
            BeforeCount++;
            await next();
            AfterCount++;
        }
    }

    [TestFixture]
    public class PluginSystemTests
    {
        [SetUp]
        public void SetUp()
        {
            PluginTestCommand.LastReceivedMessage = null;
            PluginTestCommand.ExecutionCount = 0;
            DummyDecorator.BeforeCount = 0;
            DummyDecorator.AfterCount = 0;
        }

        [Test]
        public void Plugin_UnauthorizedAction_ThrowsUnauthorizedPluginAccessException()
        {
            var context = new Context();
            var plugin = new TestPlugin("UnauthorizedPlugin", PluginCapabilities.None);

            plugin.OnRegisterAction = (ctx) =>
            {
                Assert.Throws<UnauthorizedPluginAccessException>(() =>
                {
                    ctx.RegisterSignalInterceptor(new DummyInterceptor());
                });

                Assert.Throws<UnauthorizedPluginAccessException>(() =>
                {
                    ctx.RegisterCommandDecorator(new DummyDecorator());
                });
            };

            context.RegisterPlugin(plugin);
            Assert.IsTrue(plugin.RegisteredCalled);
            context.Dispose();
        }

        [Test]
        public void SignalInterceptor_CanBlockAndModifySignals()
        {
            var context = new Context();
            context.SignalBusInternal.RegisterCommand(typeof(PluginTestSignal), typeof(PluginTestCommand), ExecutionMode.Sequential, 0, false);

            var plugin = new TestPlugin("InterceptorPlugin", PluginCapabilities.SignalInterceptor);
            plugin.OnRegisterAction = (ctx) =>
            {
                ctx.RegisterSignalInterceptor(new DummyInterceptor());
            };

            context.RegisterPlugin(plugin);

            // 1. Test Blocked Signal
            context.SignalBus.Fire(new PluginTestSignal("BlockMe"));
            Assert.AreEqual(0, PluginTestCommand.ExecutionCount);

            // 2. Test Modified Signal
            context.SignalBus.Fire(new PluginTestSignal("Hello"));
            Assert.AreEqual(1, PluginTestCommand.ExecutionCount);
            Assert.AreEqual("Hello Intercepted", PluginTestCommand.LastReceivedMessage);

            context.Dispose();
        }

        [Test]
        public void CommandDecorator_WrapsExecution()
        {
            var context = new Context();
            context.SignalBusInternal.RegisterCommand(typeof(PluginTestSignal), typeof(PluginTestCommand), ExecutionMode.Sequential, 0, false);

            var plugin = new TestPlugin("DecoratorPlugin", PluginCapabilities.CommandDecorator);
            plugin.OnRegisterAction = (ctx) =>
            {
                ctx.RegisterCommandDecorator(new DummyDecorator());
            };

            context.RegisterPlugin(plugin);

            context.SignalBus.Fire(new PluginTestSignal("Run"));

            Assert.AreEqual(1, PluginTestCommand.ExecutionCount);
            Assert.AreEqual(1, DummyDecorator.BeforeCount);
            Assert.AreEqual(1, DummyDecorator.AfterCount);

            context.Dispose();
        }

        [Test]
        public void Plugin_Removal_CleansUpHooks()
        {
            var context = new Context();
            context.SignalBusInternal.RegisterCommand(typeof(PluginTestSignal), typeof(PluginTestCommand), ExecutionMode.Sequential, 0, false);

            var plugin = new TestPlugin("InterceptorPlugin", PluginCapabilities.SignalInterceptor);
            plugin.OnRegisterAction = (ctx) =>
            {
                ctx.RegisterSignalInterceptor(new DummyInterceptor());
            };

            context.RegisterPlugin(plugin);

            // Signal is intercepted
            context.SignalBus.Fire(new PluginTestSignal("Hello"));
            Assert.AreEqual("Hello Intercepted", PluginTestCommand.LastReceivedMessage);

            // Remove plugin
            context.RemovePlugin(plugin);
            Assert.IsTrue(plugin.RemovedCalled);

            // Signal should not be intercepted anymore
            context.SignalBus.Fire(new PluginTestSignal("Hello"));
            Assert.AreEqual("Hello", PluginTestCommand.LastReceivedMessage);

            context.Dispose();
        }
    }
}
