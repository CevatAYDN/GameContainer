using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Smoke tests for the Nexus profiler instrumentation in the editor: a signal fire moves
    /// its counter, and the editor NexusProfilerModule serializes by reference without a
    /// warning. Runtime/NexusProfilerPlayModeTests asserts the exact counts.
    /// </summary>
    public class NexusProfilerTests
    {
        private struct ProfilerSmokeSignal { }

        [Test]
        public void Fire_IncrementsSignalsDispatchedCounter()
        {
            // the warnings this test causes, in the order it causes them (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\]\ Signal\ 'Nexus.Editor.Tests.NexusProfilerTests\+ProfilerSmokeSignal'\ fired\ but\ has\ no\ subscribers"));
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

        // Issue #275: the Profiler window keeps its modules in a [SerializeReference] field, and Unity warned in every editor with
        // the window open that the module lacked [Serializable]
        [Test]
        public void ProfilerModule_SerializedByReference_LogsNoWarning()
        {
            UnityEngine.JsonUtility.ToJson(new ModuleHolder { Module = new NexusProfilerModule() });
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }

        [System.Serializable]
        private class ModuleHolder
        {
            [UnityEngine.SerializeReference] public object Module;
        }
    }
}
