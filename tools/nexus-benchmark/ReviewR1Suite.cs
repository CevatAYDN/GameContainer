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
    }
}
