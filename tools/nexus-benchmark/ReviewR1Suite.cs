using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;

namespace NexusBench
{
    /// <summary>
    /// Regression cover for the DI and lifecycle defects of the 2026-11-06 core review, round R1 (#290, #291, #292,
    /// #293, #312). Each case fails on the code before its fix.
    /// </summary>
    public static class ReviewR1Suite
    {
        private static int _failures;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[ReviewR1] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("ReviewR1", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[ReviewR1] DI AND LIFECYCLE DEFECTS OF THE 2026-11-06 REVIEW");
            Console.WriteLine("===============================================================================");
            Guard("R1-01", StaleConstructorFactory_GivesWayToTheConstructorItNoLongerMatches);
            Guard("R1-04", ToNameAfterTo_AddsTheNamedEntryAndKeepsTheKeysDefault);
            Guard("R1-03", SyncDispose_NamesTheAsyncStopperStillRunningWhenItsSingletonsAreDisposed);
            Console.WriteLine(_failures == 0 ? "[ReviewR1] ALL PASSED" : $"[ReviewR1] {_failures} FAILED");
            return _failures;
        }

        private static void Guard(string name, Action test)
        {
            try { test(); }
            catch (Exception ex) { Report(name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        }

        // ── #290 (R1-01) ─────────────────────────────────────────────────────

        public interface IR1Store { }
        public sealed class R1FreeStore : IR1Store { }
        public sealed class R1PremiumStore : IR1Store { }
        // The binder was generated for `R1Shop(IR1Store store)`; the parameter has since been named.
        public sealed class R1Shop
        {
            public readonly IR1Store Store;
            public R1Shop([Inject("premium")] IR1Store store) { Store = store; }
        }
        public sealed class R1CurrentShop
        {
            public readonly IR1Store Store;
            public R1CurrentShop(IR1Store store) { Store = store; }
        }

        private static void StaleConstructorFactory_GivesWayToTheConstructorItNoLongerMatches()
        {
            var ctx = NexusTestHarness.CreateContext();
            var logger = new SvcLoggerRecorder();
            ctx.Context.Container.BindInstance<ILoggerService>(logger);
            int currentFactoryRuns = 0;
            try
            {
                // as the editor generator writes them: the parameters each factory was generated for
                NexusDI.RegisterConstructorFactory<R1Shop>(
                    di => new R1Shop(di.ResolveConstructorParameter<IR1Store>(0, "R1Shop", "IR1Store")), "IR1Store");
                NexusDI.RegisterConstructorFactory<R1CurrentShop>(di =>
                {
                    currentFactoryRuns++;
                    return new R1CurrentShop(di.ResolveConstructorParameter<IR1Store>(0, "R1CurrentShop", "IR1Store"));
                }, "IR1Store");

                var di = new NexusDI();
                di.Bind<IR1Store, R1FreeStore>();
                di.Bind<IR1Store, R1PremiumStore>("premium");
                di.Bind<R1Shop>(); // a singleton: CreateInstance meets the unchecked factory
                di.Bind<R1CurrentShop>(Lifetime.Transient); // the cached transient path checks it in RefreshBindingCache

                var first = di.Resolve<R1Shop>();
                var second = new NexusDI();
                second.Bind<IR1Store, R1PremiumStore>("premium");
                second.Bind<R1Shop>(Lifetime.Transient);
                var again = second.Resolve<R1Shop>();
                di.Resolve<R1CurrentShop>();
                di.Resolve<R1CurrentShop>();
                int staleWarnings = logger.Messages.FindAll(m => m.Contains("R1Shop") && m.Contains("constructor")).Count;

                bool ok = first.Store is R1PremiumStore && again.Store is R1PremiumStore
                    && staleWarnings == 1 && currentFactoryRuns == 2
                    && !logger.Messages.Exists(m => m.Contains("R1CurrentShop"));
                Report("R1-01 A_Stale_Generated_Constructor_Factory_Gives_Way_To_The_Constructor_It_No_Longer_Matches", ok,
                    $"stores={first.Store.GetType().Name},{again.Store.GetType().Name} staleWarnings={staleWarnings} currentFactoryRuns={currentFactoryRuns}");
            }
            finally
            {
                ctx.Dispose();
                NexusDI.ClearGeneratedRegistrations();
            }
        }

        // ── #292 (R1-04) ─────────────────────────────────────────────────────

        private static void ToNameAfterTo_AddsTheNamedEntryAndKeepsTheKeysDefault()
        {
            var binder = new NexusBinder<BUnitType, BUnitDefinition>();
            binder.Bind(BUnitType.Warrior).To(new BUnitDefinition("Warrior", 120));
            binder.Bind(BUnitType.Warrior).To(new BUnitDefinition("Elite Warrior", 200)).ToName("elite");
            // a later .To on the same chain changes the named entry only
            binder.Bind(BUnitType.Archer).To(new BUnitDefinition("Archer", 70));
            binder.Bind(BUnitType.Archer).To(new BUnitDefinition("Scout", 50)).ToName("scout").To(new BUnitDefinition("Ranger", 90));

            int warrior = binder.Get(BUnitType.Warrior).MaxHp, elite = binder.Get(BUnitType.Warrior, "elite").MaxHp;
            int archer = binder.Get(BUnitType.Archer).MaxHp, scout = binder.Get(BUnitType.Archer, "scout").MaxHp;
            Report("R1-04 ToName_After_To_Adds_The_Named_Entry_And_Keeps_The_Keys_Default",
                warrior == 120 && elite == 200 && archer == 70 && scout == 90,
                $"warrior={warrior} elite={elite} archer={archer} scout={scout} (want 120 200 70 90)");
        }

        // ── #291 (R1-03) ─────────────────────────────────────────────────────

        public sealed class R1SlowStopper : IAsyncStoppable
        {
            public readonly TaskCompletionSource<bool> Release = new();
            public async ValueTask StopAsync(CancellationToken ct) => await Release.Task;
        }
        public sealed class R1FastStopper : IAsyncStoppable
        {
            public ValueTask StopAsync(CancellationToken ct) => default;
        }

        private static void SyncDispose_NamesTheAsyncStopperStillRunningWhenItsSingletonsAreDisposed()
        {
            var ctx = NexusTestHarness.CreateContext();
            var logger = new SvcLoggerRecorder();
            ctx.Context.Container.BindInstance<ILoggerService>(logger);
            ctx.Context.Container.Bind<R1SlowStopper>();
            ctx.Context.Container.Bind<R1FastStopper>();
            var slow = ctx.Context.Resolve<R1SlowStopper>();
            ctx.Context.Resolve<R1FastStopper>();
            try
            {
                ctx.Context.Dispose(); // the sync path: Root.OnDestroy, NexusRuntime.Reset
                bool slowNamed = logger.Messages.Exists(m => m.Contains(nameof(R1SlowStopper)) && m.Contains("still running"));
                bool fastNamed = logger.Messages.Exists(m => m.Contains(nameof(R1FastStopper)));
                Report("R1-03 Sync_Dispose_Names_The_Async_Stopper_Still_Running_When_Its_Singletons_Are_Disposed",
                    slowNamed && !fastNamed, $"slowNamed={slowNamed} fastNamed={fastNamed} log=[{string.Join(" | ", logger.Messages)}]");
            }
            finally
            {
                slow.Release.TrySetResult(true);
                ctx.Dispose();
            }
        }
    }
}
