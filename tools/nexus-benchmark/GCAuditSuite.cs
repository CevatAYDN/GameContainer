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
            failures += AssertPass("GCA5. EconomyService_EarnSpendRead_WithThrottler_ZeroGC", TestEconomyWithThrottlerZeroGC);
            failures += AssertPass("GCA6. SubscribedSignal_FirstFireAfterPrewarm_ZeroGC", TestSubscribedFirstFireAfterPrewarmZeroGC);
            failures += AssertPass("GCA7. EconomyService_SubscribedBalance_EarnSpend_ZeroGC", TestEconomySubscribedBalanceZeroGC);
            failures += AssertPass("GCA8. SaveThrottler_Tick_IdleAndDue_ZeroGC", TestSaveThrottlerTickZeroGC);

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

        public struct FirstFireSignal { public int Value; }

        // Issue #77: Wickling fired an empty signal to warm a subscriber path; Prewarm<T> must leave nothing for the first Fire.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestSubscribedFirstFireAfterPrewarmZeroGC()
        {
            var container = new NexusDI();
            var pool = new CommandPoolManager(container);
            var bus = new SignalBus(container, pool, new MockContext());
            int heard = 0;
            bus.Subscribe<FirstFireSignal>(s => heard += s.Value);
            bus.Prewarm<FirstFireSignal>();
            bool metrics = NexusRuntime.Metrics.MetricsEnabled;
            NexusRuntime.Metrics.MetricsEnabled = true; // the metered path, as in a player

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            bus.Fire(new FirstFireSignal { Value = 1 });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            NexusRuntime.Metrics.MetricsEnabled = metrics;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes on the first Fire after Prewarm");
                return false;
            }
            return heard == 1;
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

        private sealed class FixedClock : ITimeProvider
        {
            public float Now => 0f;
        }

        private sealed class StepClock : ITimeProvider
        {
            public float Seconds;
            public float Now => Seconds;
        }

        private sealed class NullPrefs : IPlayerPrefsService
        {
            public int GetInt(string key, int defaultValue = 0) => defaultValue;
            public void SetInt(string key, int value) { }
            public bool GetBool(string key, bool defaultValue = false) => defaultValue;
            public void SetBool(string key, bool value) { }
            public string GetString(string key, string defaultValue = "") => defaultValue;
            public void SetString(string key, string value) { }
            public float GetFloat(string key, float defaultValue = 0f) => defaultValue;
            public void SetFloat(string key, float value) { }
            public long GetLong(string key, long defaultValue = 0L) => defaultValue;
            public void SetLong(string key, long value) { }
            public bool HasKey(string key) => false;
            public void DeleteKey(string key) { }
            public void Save() { }
        }

        // A game earns on the frame loop (cash pickup, per-frame purchase spend). With a SaveThrottler bound the
        // write is deferred, so after a currency's first touch Earn/Spend/GetBalance must not allocate.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestEconomyWithThrottlerZeroGC()
        {
            var economy = new EconomyService
            {
                PlayerPrefsService = new NullPrefs(),
                SaveThrottler = new SaveThrottler { TimeProvider = new FixedClock() } // clock frozen: the window never elapses
            };

            // Warmup: creates the balance and flushes the fresh throttler slot once.
            economy.Earn("gold", 1);
            economy.Spend("gold", 1);

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                economy.Earn("gold", 2);
                economy.Spend("gold", 1);
                economy.GetBalance("gold");
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            long allocated = allocAfter - allocBefore;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes during 5000 Earn/Spend/GetBalance rounds");
                return false;
            }
            return economy.GetBalance("gold") == 5000;
        }

        // A HUD follows the balance (ChipCrisis HudMediator): every Earn/Spend notifies the subscriber
        // through the secure observable, a path GCA5 does not reach because nothing is subscribed there.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestEconomySubscribedBalanceZeroGC()
        {
            var economy = new EconomyService
            {
                PlayerPrefsService = new NullPrefs(),
                SaveThrottler = new SaveThrottler { TimeProvider = new FixedClock() }
            };
            long shown = 0;
            int notifications = 0;
            economy.GetObservableBalance("gold").OnChanged((_, now) => { shown = now; notifications++; });

            // Warmup: creates the balance, flushes the fresh throttler slot and builds the handler snapshot.
            economy.Earn("gold", 1);
            economy.Spend("gold", 1);
            notifications = 0;

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                economy.Earn("gold", 2);
                economy.Spend("gold", 1);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            if (allocated > 0)
            {
                Console.WriteLine($"      Allocated {allocated} bytes during 5000 Earn/Spend rounds with a subscribed balance");
                return false;
            }
            return notifications == 10000 && shown == 5000;
        }

        // SaveThrottler is an ITickable: it runs every frame in a game that binds it, with or without a save due.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TestSaveThrottlerTickZeroGC()
        {
            var clock = new StepClock();
            var throttler = new SaveThrottler { TimeProvider = clock };
            int saves = 0;
            Action save = () => saves++;

            // Warmup: two owners' slots and the due-slot buffer.
            throttler.TryRequestSave("economy", save);
            throttler.TryRequestSave("progression", save);
            for (int i = 0; i < 50; i++)
            {
                clock.Seconds += 0.5f;
                throttler.TryRequestSave("economy", save);
                throttler.Tick(0.5f);
            }
            int savesBefore = saves;

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) throttler.Tick(0.016f);
            long idle = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                clock.Seconds += 0.5f;
                throttler.TryRequestSave("economy", save);
                throttler.Tick(0.5f);
            }
            long due = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            if (idle > 0 || due > 0)
            {
                Console.WriteLine($"      Allocated {idle} bytes in 5000 idle ticks, {due} bytes in 5000 ticks with saves due");
                return false;
            }
            return saves > savesBefore;
        }
    }
}
