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
            Guard("R1-08", ViewThatUnregistersInItsUnbind_DoesNotBreakViewBinderDispose);
            Guard("R1-03", SyncDispose_NamesTheAsyncStopperStillRunningWhenItsSingletonsAreDisposed);
            Guard("R1-06", RootWithAnInvalidParent_IsMarkedFailedAndNotConfiguredAgain);
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

        // ── #312 R1-06 ───────────────────────────────────────────────────────

        private static void SetField(Root root, string field, object value) =>
            typeof(Root).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(root, value);

        private static bool StartupFailed(Root root) =>
            (bool)typeof(Root).GetField("_startupFailed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(root);

        private static string InitTwice(Root root)
        {
            string first = "none", second = "none";
            try { root.InitializeContext(); } catch (InvalidOperationException ex) { first = ex.Message; }
            try { root.InitializeContext(); } catch (InvalidOperationException ex) { second = ex.Message; }
            return $"first='{first}' second='{second}' failed={StartupFailed(root)}";
        }

        private static void RootWithAnInvalidParent_IsMarkedFailedAndNotConfiguredAgain()
        {
            var a = new UnityEngine.GameObject("R1CycleA").AddComponent<Root>();
            var b = new UnityEngine.GameObject("R1CycleB").AddComponent<Root>();
            SetField(a, "parentRoot", b);
            SetField(b, "parentRoot", a);
            var global = new UnityEngine.GameObject("R1GlobalWithParent").AddComponent<Root>();
            SetField(global, "isGlobalContext", true);
            SetField(global, "parentRoot", b);

            string cycle = InitTwice(a), globalParent = InitTwice(global);
            bool ok = StartupFailed(a) && StartupFailed(global)
                && cycle.Contains("second='none'") && globalParent.Contains("second='none'")
                && a.Context == null && global.Context == null;
            Report("R1-06 A_Root_With_An_Invalid_Parent_Is_Marked_Failed_And_Not_Configured_Again", ok,
                $"cycle: {cycle}; global: {globalParent}");
        }

        // ── #293 (R1-08) ─────────────────────────────────────────────────────

        public class R1CountingMediator : IMediator
        {
            public int Unbinds;
            public void Bind(object view, ISignalBus signalBus) { }
            public void Unbind() => Unbinds++;
        }
        public sealed class R1OtherMediator : R1CountingMediator { }

        // As a View whose OnUnbind disables its GameObject: OnDisable calls Context.UnregisterView, inside Dispose's loop.
        [Mediator(typeof(R1CountingMediator))]
        public sealed class R1SelfUnregisteringView : IView
        {
            public ViewBinder Binder;
            private bool _bound = true;
            public void Bind(IContext context) { }
            public void Unbind()
            {
                if (!_bound) return;
                _bound = false;
                Binder.UnregisterView(this);
            }
        }

        [Mediator(typeof(R1OtherMediator))]
        public sealed class R1PlainView : IView
        {
            public void Bind(IContext context) { }
            public void Unbind() { }
        }

        private static void ViewThatUnregistersInItsUnbind_DoesNotBreakViewBinderDispose()
        {
            var selfMediator = new R1CountingMediator();
            var otherMediator = new R1OtherMediator();
            using var container = new NexusDI();
            container.BindInstance(selfMediator);
            container.BindInstance(otherMediator);
            var binder = new ViewBinder(new MockContext(), container);
            binder.RegisterView(new R1SelfUnregisteringView { Binder = binder });
            binder.RegisterView(new R1PlainView());

            string error = "none";
            try { binder.Dispose(); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            Report("R1-08 A_View_That_Unregisters_In_Its_Unbind_Does_Not_Break_ViewBinder_Dispose",
                error == "none" && selfMediator.Unbinds == 1 && otherMediator.Unbinds == 1 && binder.ActiveMediatorCount == 0,
                $"error={error} unbinds={selfMediator.Unbinds},{otherMediator.Unbinds} (want 1,1) active={binder.ActiveMediatorCount}");
        }
    }
}
