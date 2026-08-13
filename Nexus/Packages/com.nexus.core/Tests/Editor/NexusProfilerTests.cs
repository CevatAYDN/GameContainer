using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests.Editor
{
    /// <summary>
    /// Smoke tests for the Nexus profiler instrumentation (NexusProfiler counters +
    /// the editor NexusProfilerModule). Guards the hot-path wiring: a signal fire, a
    /// command execution, and a DI resolve must move their counters, and the editor
    /// module must construct against the installed Unity API.
    /// </summary>
    public class NexusProfilerTests
    {
        private struct ProfilerSmokeSignal { }

        [Test]
        public void Fire_IncrementsSignalsDispatchedCounter()
        {
            var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            try
            {
                int before = NexusProfiler.SignalsDispatched.Value;
                bus.Fire(new ProfilerSmokeSignal());
                Assert.Greater(NexusProfiler.SignalsDispatched.Value, before,
                    "Fire() must move the SignalsDispatched counter (hot-path wiring).");
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        [Test]
        public void ExecuteCommand_IncrementsCommandsExecutedCounter()
        {
            var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            try
            {
                container.Bind<ProfilerSmokeCommand>(isSingleton: false);
                bus.RegisterCommand(typeof(ProfilerSmokeSignal), typeof(ProfilerSmokeCommand),
                    ExecutionMode.Sequential, 0, isAsync: false);

                int before = NexusProfiler.CommandsExecuted.Value;
                bus.Fire(new ProfilerSmokeSignal());
                Assert.Greater(NexusProfiler.CommandsExecuted.Value, before,
                    "Command execution must move the CommandsExecuted counter.");
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        [Test]
        public void Resolve_IncrementsResolvesPerformedCounter()
        {
            var container = new NexusDI();
            try
            {
                container.Bind<ProfilerSmokeResolvable>(isSingleton: false);
                int before = NexusProfiler.ResolvesPerformed.Value;
                container.Resolve<ProfilerSmokeResolvable>();
                Assert.Greater(NexusProfiler.ResolvesPerformed.Value, before,
                    "DI Resolve must move the ResolvesPerformed counter.");
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void ProfilerModule_ConstructsAgainstInstalledUnityApi()
        {
            // Constructs against the actual Unity 6 ProfilerModule API; a mismatch throws here.
            // (ProfilerModule does not implement IDisposable in Unity 6000.5 — it is a
            // discovery-registered window module, so a constructed instance needs no teardown.)
            var module = new NexusProfilerModule();
            Assert.IsNotNull(module);
        }

        private class ProfilerSmokeCommand : ICommand<ProfilerSmokeSignal>
        {
            public void Execute(ProfilerSmokeSignal signal) { }
        }

        private class ProfilerSmokeResolvable { }
    }
}
