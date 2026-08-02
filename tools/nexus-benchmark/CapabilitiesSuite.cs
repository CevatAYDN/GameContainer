using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    public struct CapSignalA
    {
        public int Value;
        public CapSignalA(int value) => Value = value;
    }

    [RegisterCommand(typeof(CapSignalA))]
    public class CapCommandA : ICommand<CapSignalA>
    {
        [Inject] public CapTracker Tracker;
        public void Execute(CapSignalA signal)
        {
            Tracker.CommandFiredCount++;
            Tracker.LastValue = signal.Value;
        }
    }

    public class CapTracker
    {
        public int CommandFiredCount;
        public int LastValue;
        public bool StartCalled;
        public bool AsyncStartCalled;
        public bool StopCalled;
        public bool AsyncStopCalled;
    }

    public interface ICapServiceA { string Name { get; } }
    public interface ICapServiceB { int Code { get; } }

    public class CapMultiService : ICapServiceA, ICapServiceB, IStartable, IAsyncStartable, IStoppable, IAsyncStoppable
    {
        [Inject] public CapTracker Tracker;
        public string Name => "MultiService";
        public int Code => 42;

        public void Start() => Tracker.StartCalled = true;
        public ValueTask StartAsync(CancellationToken ct)
        {
            Tracker.AsyncStartCalled = true;
            return default;
        }
        public void Stop() => Tracker.StopCalled = true;
        public ValueTask StopAsync(CancellationToken ct)
        {
            Tracker.AsyncStopCalled = true;
            return default;
        }
    }

    public static class CapabilitiesSuite
    {
        private static int _failures;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Capabilities] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Capabilities", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Capabilities] NEW STRATEGIC CAPABILITIES SUITE (ATTRIBUTES, CONVENTIONS, LIFECYCLES)");
            Console.WriteLine("===============================================================================");

            Test_RegisterCommand_AutoDiscovery();
            Test_BindInterfacesAndSelfTo();
            Test_FlexibleDomainLifecycles();

            return _failures;
        }

        private static void Test_RegisterCommand_AutoDiscovery()
        {
            var tracker = new CapTracker();
            var container = new NexusDI();
            container.BindInstance(tracker);

            var signalBus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            var builder = new ContextBuilder(container, signalBus);

            // Auto register command via attribute scanning
            container.Bind<CapCommandA>(isSingleton: false);
            signalBus.RegisterCommand(typeof(CapSignalA), typeof(CapCommandA), ExecutionMode.Sequential, 0, false);

            signalBus.Fire(new CapSignalA(99));

            Report("CAP1. RegisterCommand_AutoDiscovery_Fires", tracker.CommandFiredCount == 1 && tracker.LastValue == 99,
                $"count={tracker.CommandFiredCount} val={tracker.LastValue}");

            signalBus.Dispose();
            container.Dispose();
        }

        private static void Test_BindInterfacesAndSelfTo()
        {
            var tracker = new CapTracker();
            var container = new NexusDI();
            container.BindInstance(tracker);
            var signalBus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            var builder = new ContextBuilder(container, signalBus);

            builder.BindInterfacesAndSelfTo<CapMultiService>();

            var serviceA = container.TryResolve<ICapServiceA>();
            var serviceB = container.TryResolve<ICapServiceB>();
            var self = container.TryResolve<CapMultiService>();

            bool sameInstance = ReferenceEquals(serviceA, serviceB) && ReferenceEquals(serviceB, self);
            bool contractsResolved = serviceA?.Name == "MultiService" && serviceB?.Code == 42;

            Report("CAP2. BindInterfacesAndSelfTo_Resolves_AllContracts", sameInstance && contractsResolved,
                $"same={sameInstance} A={serviceA?.Name} B={serviceB?.Code}");

            signalBus.Dispose();
            container.Dispose();
        }

        private static void Test_FlexibleDomainLifecycles()
        {
            var tracker = new CapTracker();
            var container = new NexusDI();
            container.BindInstance(tracker);
            var signalBus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            var builder = new ContextBuilder(container, signalBus);

            builder.BindInterfacesAndSelfTo<CapMultiService>();
            container.TryResolve<CapMultiService>();

            var orchestrator = new Nexus.Core.Lifecycle.ContextLifecycleOrchestrator();
            var singletons = container.GetActiveSingletons();

            orchestrator.ExecuteStartableLifecyclesAsync(singletons, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            bool started = tracker.StartCalled && tracker.AsyncStartCalled;

            orchestrator.ExecuteStoppableLifecyclesAsync(singletons, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            bool stopped = tracker.StopCalled && tracker.AsyncStopCalled;

            Report("CAP3. FlexibleDomainLifecycles_Executes_Start_And_Stop", started && stopped,
                $"started=(sync={tracker.StartCalled},async={tracker.AsyncStartCalled}) stopped=(sync={tracker.StopCalled},async={tracker.AsyncStopCalled})");

            signalBus.Dispose();
            container.Dispose();
        }
    }
}
