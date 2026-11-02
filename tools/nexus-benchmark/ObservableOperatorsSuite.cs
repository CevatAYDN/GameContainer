using System;
using Nexus.Core;

namespace NexusBench
{
    /// <summary>
    /// Properties derived with Select and CombineLatest (#269): the value follows every source, a handler hears only a
    /// changed result, Dispose detaches from every source, and a selector that throws changes nothing it should not.
    /// The allocation check is GCA11 in <see cref="GCAuditSuite"/>.
    /// </summary>
    public static class ObservableOperatorsSuite
    {
        public static int Run()
        {
            int failures = 0;
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine("[Operators] DERIVED PROPERTY SUITE (#269)");
            Console.WriteLine("===============================================================================");

            failures += AssertPass("OP1. Select_StartsFromTheSourcesValue_AndFollowsIt", SelectFollows);
            failures += AssertPass("OP2. CombineLatest_FollowsEachOfItsSources_ASecureObservableIncluded", CombineFollows);
            failures += AssertPass("OP3. AHandler_HearsOnlyAChangedResult", NotifiesOnChangeOnly);
            failures += AssertPass("OP4. Dispose_DetachesFromEverySource_AndKeepsTheLastValue", DisposeDetaches);
            failures += AssertPass("OP5. ASelectorThatThrows_LeavesTheValue_AndOnCreationLeavesNoHandler", ThrowingSelector);
            failures += AssertPass("OP6. AMediatorsUnbind_DisposesADerivedPropertyItTracked", MediatorDisposesDerived);

            if (failures == 0)
                Console.WriteLine("\n[Operators] ALL DERIVED PROPERTY TESTS PASSED ✓");
            else
                Console.WriteLine($"\n[Operators] {failures} DERIVED PROPERTY TEST(S) FAILED ✗");
            return failures;
        }

        private static int AssertPass(string testName, Func<bool> testFunc)
        {
            try
            {
                bool passed = testFunc();
                Console.WriteLine($"[Operators] {(passed ? "PASS" : "FAIL")}  {testName}");
                return passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Operators] FAIL  {testName}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static bool Check(bool ok, string what)
        {
            if (!ok) Console.WriteLine("      " + what);
            return ok;
        }

        private static bool SelectFollows()
        {
            var score = new ObservableProperty<int>(4);
            using var doubled = score.Select(s => s * 2);
            bool ok = Check(doubled.Value == 8, $"initial value {doubled.Value}, expected 8");
            score.Value = 5;
            return ok & Check(doubled.Value == 10, $"after the change {doubled.Value}, expected 10");
        }

        private static bool CombineFollows()
        {
            var coins = new SecureObservableInt(50);
            var price = new ObservableProperty<int>(80);
            var discount = new ObservableProperty<int>(0);
            using var canBuy = coins.CombineLatest(price, (c, p) => c >= p);
            using var due = coins.CombineLatest(price, discount, (c, p, d) => p - d - c);
            bool ok = Check(!canBuy.Value && due.Value == 30, $"initial canBuy={canBuy.Value} due={due.Value}, expected false and 30");
            coins.Value = 90;
            ok &= Check(canBuy.Value && due.Value == -10, $"after coins canBuy={canBuy.Value} due={due.Value}, expected true and -10");
            price.Value = 100;
            ok &= Check(!canBuy.Value && due.Value == 10, $"after price canBuy={canBuy.Value} due={due.Value}, expected false and 10");
            discount.Value = 15;
            return ok & Check(due.Value == -5, $"after discount due={due.Value}, expected -5");
        }

        private static bool NotifiesOnChangeOnly()
        {
            var level = new ObservableProperty<int>(1);
            using var isBoss = level.Select(l => l % 5 == 0);
            int heard = 0;
            bool lastNew = false;
            isBoss.OnChanged((_, now) => { heard++; lastNew = now; });
            level.Value = 2;
            level.Value = 3;
            level.Value = 5;
            bool ok = Check(heard == 1 && lastNew, $"heard {heard} change(s) for 1 -> 2 -> 3 -> 5, expected 1 (to true)");
            level.Value = 6;
            return ok & Check(heard == 2 && !lastNew, $"heard {heard} change(s) after 5 -> 6, expected 2 (to false)");
        }

        private static bool DisposeDetaches()
        {
            var a = new ObservableProperty<int>(1);
            var b = new ObservableProperty<int>(2);
            int computed = 0;
            var sum = a.CombineLatest(b, (x, y) => { computed++; return x + y; });
            int heard = 0;
            sum.OnChanged((_, _) => heard++);
            computed = 0;
            sum.Dispose();
            sum.Dispose();
            a.Value = 10;
            b.Value = 20;
            return Check(sum.IsDisposed && computed == 0 && heard == 0 && sum.Value == 3,
                $"after Dispose: computed {computed} time(s), heard {heard}, value {sum.Value}; expected 0, 0, 3");
        }

        private static bool ThrowingSelector()
        {
            var source = new ObservableProperty<int>(1);
            bool otherObserverRan = false;
            using var guarded = source.Select(v => v == 13 ? throw new InvalidOperationException("unlucky") : v * 10);
            source.OnChanged((_, _) => otherObserverRan = true);
            bool threw = false;
            try { source.Value = 13; }
            catch (InvalidOperationException) { threw = true; }
            bool ok = Check(threw && guarded.Value == 10 && otherObserverRan,
                $"on a throwing change: rethrown={threw}, value={guarded.Value} (expected 10), other observer ran={otherObserverRan}");

            int calls = 0;
            bool creationThrew = false;
            try { source.Select<int, int>(_ => { calls++; throw new InvalidOperationException("at creation"); }); }
            catch (InvalidOperationException) { creationThrew = true; }
            calls = 0;
            source.Value = 2;
            return ok & Check(creationThrew && calls == 0, $"a selector that threw at creation threw={creationThrew}, ran {calls} time(s) on a later change (expected 0)");
        }

        private sealed class ShopView { public bool BuyEnabled; }

        private sealed class ShopMediator : Mediator<ShopView>
        {
            public ObservableProperty<int> Coins, Price;
            public DerivedProperty<bool> CanBuy;
            protected override void OnBind()
            {
                CanBuy = Coins.CombineLatest(Price, (c, p) => c >= p);
                TrackObservable(CanBuy, (_, can) => View.BuyEnabled = can);
            }
        }

        private static bool MediatorDisposesDerived()
        {
            using var ctx = NexusTestHarness.CreateContext();
            var view = new ShopView();
            var mediator = new ShopMediator { Coins = new ObservableProperty<int>(0), Price = new ObservableProperty<int>(10) };
            mediator.Bind(view, ctx.Context.SignalBus);
            mediator.Coins.Value = 10;
            bool ok = Check(view.BuyEnabled, "the tracked handler did not enable the buy button while bound");
            mediator.Unbind();
            mediator.Coins.Value = 0;
            return ok & Check(mediator.CanBuy.IsDisposed && mediator.CanBuy.Value && view.BuyEnabled,
                $"after Unbind: disposed={mediator.CanBuy.IsDisposed}, value={mediator.CanBuy.Value}, view={view.BuyEnabled} (expected true, true, true: nothing follows the sources)");
        }
    }
}
