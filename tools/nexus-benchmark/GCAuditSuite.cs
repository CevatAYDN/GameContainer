using System;
using System.Runtime.CompilerServices;
using Nexus.Core;
using Nexus.Core.Services;

namespace NexusBench
{
    public static class GCAuditSuite
    {
        public struct AuditSignal
        {
            public int Value;
            public AuditSignal(int val) => Value = val;
        }

        public class AuditCommand : ICommand<AuditSignal>
        {
            [Inject] public TestCounter Counter;
            public void Execute(AuditSignal signal)
            {
                if (Counter != null) Counter.Value += signal.Value;
            }
        }

        public static int Run()
        {
            int failures = 0;
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine("[GCAudit] ZERO-GC ALLOCATION & PERFORMANCE AUDIT SUITE");
            Console.WriteLine("===============================================================================");

            failures += AssertPass("GCA1. SignalBus_Fire_ZeroGC", TestSignalBusFireZeroGC);
            failures += AssertPass("GCA2. ObservableProperty_ValueMutation_ZeroGC", TestObservablePropertyValueMutationZeroGC);
            failures += AssertPass("GCA3. TickService_OnTick_ZeroGC", TestTickServiceOnTickZeroGC);
            failures += AssertPass("GCA4. CommandPoolManager_RentReturn_ZeroGC", TestCommandPoolManagerRentReturnZeroGC);

            if (failures == 0)
                Console.WriteLine("\n[GCAudit] ALL ZERO-GC AUDIT TESTS PASSED ✓");
            else
                Console.WriteLine($"\n[GCAudit] {failures} ZERO-GC AUDIT TEST(S) FAILED ✗");

            return failures;
        }

        private static int AssertPass(string testName, Func<bool> testFunc)
        {
            try
            {
                bool passed = testFunc();
                if (passed)
                {
                    Console.WriteLine($"[GCAudit] PASS  {testName}");
                    return 0;
                }
                else
                {
                    Console.WriteLine($"[GCAudit] FAIL  {testName}");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GCAudit] FAIL  {testName}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestSignalBusFireZeroGC()
        {
            var container = new NexusDI();
            var pool = new CommandPoolManager(container);
            var bus = new SignalBus(container, pool, new MockContext());
            var counter = new TestCounter();
            container.BindInstance(counter);
            container.Bind<AuditCommand>(isSingleton: false);

            bus.RegisterCommand(typeof(AuditSignal), typeof(AuditCommand), ExecutionMode.Sequential, 0, false);

            // Warmup
            bus.Fire(new AuditSignal(1));

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                bus.Fire(new AuditSignal(1));
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            long allocated = allocAfter - allocBefore;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes during 5000 SignalBus.Fire calls");
                return false;
            }
            return counter.Value == 5001;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestObservablePropertyValueMutationZeroGC()
        {
            var prop = new ObservableProperty<int>(10);
            int notifications = 0;
            prop.OnChanged((oldV, newV) => notifications++);

            // Warmup
            prop.Value = 11;

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                prop.Value = i;
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            long allocated = allocAfter - allocBefore;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes during 5000 ObservableProperty mutations");
                return false;
            }
            return notifications == 5001;
        }

        private class DummyTickable : ITickable
        {
            public int Ticks;
            public void Tick(float deltaTime) => Ticks++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestTickServiceOnTickZeroGC()
        {
            var tickService = new TickService();
            var tickable = new DummyTickable();
            tickService.RegisterTickable(tickable);

            // Warmup
            tickService.OnTick(0.016f);

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                tickService.OnTick(0.016f);
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            long allocated = allocAfter - allocBefore;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes during 5000 TickService.OnTick calls");
                return false;
            }
            return tickable.Ticks == 5001;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestCommandPoolManagerRentReturnZeroGC()
        {
            var container = new NexusDI();
            container.Bind<AuditCommand>(isSingleton: false);
            var pool = new CommandPoolManager(container);

            // Warmup
            var cmd = pool.GetCommand(typeof(AuditCommand));
            pool.ReturnCommand(typeof(AuditCommand), cmd);

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                var c = pool.GetCommand(typeof(AuditCommand));
                pool.ReturnCommand(typeof(AuditCommand), c);
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            long allocated = allocAfter - allocBefore;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes during 5000 GetCommand/ReturnCommand calls");
                return false;
            }
            return true;
        }
    }
}
