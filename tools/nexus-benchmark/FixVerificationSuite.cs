using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    /// <summary>
    /// Regression cover for the defects fixed in this pass. Each case fails on the
    /// pre-fix code, so a future refactor cannot silently reintroduce them.
    /// </summary>
    public static class FixVerificationSuite
    {
        private static int _failures;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[FixVerify] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("FixVerify", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[FixVerify] REGRESSION COVER FOR FIXED DEFECTS");
            Console.WriteLine("===============================================================================");

            Test_BigDouble_Zero_Ordering();
            Test_BigDouble_Suffix_And_Fractions();
            Test_Inject_PrivateBaseClassMembers();
            Test_PostConstruct_On_DerivedOverride();
            Test_NamedBindings_NoFalseCycle();
            Test_BindMultiple_TwoInterfaces_BindsConcrete();
            Test_FireAsyncAndForget_UsesOnError();
            Test_RegisterCommand_OneShot_Honored();
            Test_MixedMode_Rejected_On_Incoming();
            Test_ObservableList_Indexer_Notifies();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[FixVerify] ALL FIX-VERIFICATION TESTS PASSED ✓"
                : $"[FixVerify] {_failures} FIX-VERIFICATION TEST(S) FAILED ✗");
            return _failures;
        }

        // ── BigDouble ────────────────────────────────────────────────────────

        private static void Test_BigDouble_Zero_Ordering()
        {
            var zero = BigDouble.Zero;
            var ten = new BigDouble(10);
            var negTen = new BigDouble(-10);

            bool zeroLessThanTen = zero.CompareTo(ten) < 0;
            bool tenGreaterThanZero = ten.CompareTo(zero) > 0;
            bool zeroGreaterThanNeg = zero.CompareTo(negTen) > 0;
            bool negLessThanZero = negTen.CompareTo(zero) < 0;
            bool operatorsAgree = zero < ten && ten > zero && zero > negTen && negTen < zero;
            // Antisymmetry: sign(a.CompareTo(b)) == -sign(b.CompareTo(a)).
            bool antisymmetric = Math.Sign(zero.CompareTo(ten)) == -Math.Sign(ten.CompareTo(zero));
            // Clamp(0, 10, 20) must be 10, not 20.
            bool clamped = BigDouble.Clamp(zero, ten, new BigDouble(20)).Equals(ten);

            bool ok = zeroLessThanTen && tenGreaterThanZero && zeroGreaterThanNeg
                && negLessThanZero && operatorsAgree && antisymmetric && clamped;
            Report("FV1. BigDouble_Zero_Ordering", ok,
                $"0<10={zeroLessThanTen} 10>0={tenGreaterThanZero} 0>-10={zeroGreaterThanNeg} " +
                $"-10<0={negLessThanZero} operators={operatorsAgree} antisymmetric={antisymmetric} clamp={clamped}");
        }

        private static void Test_BigDouble_Suffix_And_Fractions()
        {
            // 1e15 is the first dynamic-table entry ("aa"); 1e93 must NOT collide with it.
            string atTable = new BigDouble(1.0, 15).ToFormattedString();
            string beyondTable = new BigDouble(1.0, 93).ToFormattedString();
            bool distinct = atTable != beyondTable;
            bool expectedBeyond = beyondTable.EndsWith("ba", StringComparison.Ordinal);

            // Fractions must not print as "0".
            string quarter = new BigDouble(0.25).ToFormattedString();
            bool fractionShown = quarter != "0" && quarter.Contains("25");

            bool ok = distinct && expectedBeyond && fractionShown;
            Report("FV2. BigDouble_Suffix_And_Fractions", ok,
                $"1e15='{atTable}' 1e93='{beyondTable}' distinct={distinct} 0.25='{quarter}'");
        }

        // ── DI metadata across the inheritance chain ──────────────────────────

        private sealed class Dep { public int Value = 7; }

        private class PrivateBase
        {
#pragma warning disable 0649
            [Inject] private Dep _baseDep;
#pragma warning restore 0649
            public Dep ReadBaseDep() => _baseDep;
        }

        private sealed class DerivedHost : PrivateBase
        {
            [Inject] public Dep DerivedDep { get; set; }
        }

        private static void Test_Inject_PrivateBaseClassMembers()
        {
            var container = new NexusDI();
            container.BindInstance(new Dep());
            var host = new DerivedHost();
            container.Inject(host);

            bool baseInjected = host.ReadBaseDep() != null;
            bool derivedInjected = host.DerivedDep != null;

            // Clearing must reach the private base field too (pooled-instance reuse).
            NexusDI.ClearInjectedReferences(host);
            bool baseCleared = host.ReadBaseDep() == null;

            container.Dispose();
            bool ok = baseInjected && derivedInjected && baseCleared;
            Report("FV3. Inject_PrivateBaseClassMembers", ok,
                $"baseInjected={baseInjected} derivedInjected={derivedInjected} baseCleared={baseCleared}");
        }

        private class PostConstructBase
        {
            public int Calls;
            protected virtual void AfterInject() { Calls++; }
        }

        private sealed class PostConstructDerived : PostConstructBase
        {
            // The attribute lives ONLY on the override: a hierarchy walk that dedupes
            // before reading attributes would drop it entirely.
            [PostConstruct]
            protected override void AfterInject() => base.AfterInject();
        }

        private static void Test_PostConstruct_On_DerivedOverride()
        {
            var container = new NexusDI();
            var host = new PostConstructDerived();
            container.Inject(host);
            container.Dispose();

            bool ok = host.Calls == 1;
            Report("FV4. PostConstruct_On_DerivedOverride", ok, $"calls={host.Calls} (expected exactly 1)");
        }

        // ── Named bindings and polymorphic bindings ──────────────────────────

        private interface IThing { string Tag { get; } }
        private sealed class ThingA : IThing { public string Tag => "a"; }

        private sealed class ThingConsumer
        {
            [Inject(Name = "primary")] public IThing Primary { get; set; }
            [Inject(Name = "secondary")] public IThing Secondary { get; set; }
        }

        private static void Test_NamedBindings_NoFalseCycle()
        {
            var container = new NexusDI();
            container.BindInstance<IThing>("primary", new ThingA());
            container.BindInstance<IThing>("secondary", new ThingA());
            var consumer = new ThingConsumer();

            string error = null;
            try { container.Inject(consumer); }
            catch (Exception ex) { error = ex.Message; }

            bool ok = error == null && consumer.Primary != null && consumer.Secondary != null;
            container.Dispose();
            Report("FV5. NamedBindings_NoFalseCycle", ok,
                $"error={(error ?? "none")} primary={consumer.Primary != null} secondary={consumer.Secondary != null}");
        }

        private interface IFaceA { }
        private interface IFaceB { }
        private sealed class TwoFaced : IFaceA, IFaceB { }

        private static void Test_BindMultiple_TwoInterfaces_BindsConcrete()
        {
            var container = new NexusDI();
            container.BindMultiple<IFaceA, IFaceB, TwoFaced>();

            var viaA = container.TryResolve<IFaceA>();
            var viaB = container.TryResolve<IFaceB>();
            // The two-interface overload used to omit the concrete key that the
            // three-interface overload registered.
            var viaConcrete = container.TryResolve<TwoFaced>();
            bool shared = ReferenceEquals(viaA, viaB) && ReferenceEquals(viaA, viaConcrete);

            container.Dispose();
            bool ok = viaA != null && viaB != null && viaConcrete != null && shared;
            Report("FV6. BindMultiple_TwoInterfaces_BindsConcrete", ok,
                $"A={viaA != null} B={viaB != null} concrete={viaConcrete != null} shared={shared}");
        }

        // ── Signal bus contracts ─────────────────────────────────────────────

        private struct FvThrowSignal { public int Value; }

        private static void Test_FireAsyncAndForget_UsesOnError()
        {
            var container = new NexusDI();
            var bus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            // A throwing SUBSCRIBER, not a throwing command: command failures are handled
            // by the RecoveryEngine and never escape dispatch, so they would not exercise
            // the fire-and-forget error path at all.
            bus.Subscribe<FvThrowSignal>(_ => throw new InvalidOperationException("boom"));

            Exception captured = null;
            bool globalFired = false;
            Action<Exception, string> globalHandler = (ex, ctx) => globalFired = true;
            SignalBus.OnUnhandledException += globalHandler;
            try
            {
                bus.FireAsyncAndForget(new FvThrowSignal { Value = 1 }, ex => captured = ex);
                // The dispatch completes synchronously up to the throw; give any
                // continuation a brief window regardless.
                for (int i = 0; i < 50 && captured == null; i++) Thread.Sleep(2);
            }
            finally
            {
                SignalBus.OnUnhandledException -= globalHandler;
            }

            // onError must receive the failure, and the global handler must NOT also fire
            // (the caller opted into handling it).
            bool ok = captured != null && !globalFired;
            bus.Dispose();
            container.Dispose();
            Report("FV7. FireAsyncAndForget_UsesOnError", ok,
                $"captured={captured?.GetType().Name ?? "null"} globalAlsoFired={globalFired}");
        }

        private struct FvOnceSignal { public int Value; }

        [RegisterCommand(typeof(FvOnceSignal), OneShot = true)]
        private sealed class FvOnceCommand : ICommand<FvOnceSignal>
        {
            public static int Executions;
            public void Execute(FvOnceSignal signal) => Executions++;
        }

        private static void Test_RegisterCommand_OneShot_Honored()
        {
            FvOnceCommand.Executions = 0;
            var container = new NexusDI();
            var bus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            container.Bind<FvOnceCommand>(isSingleton: false);

            // Route through the same attribute path the assembly scan uses, so the
            // OneShot flag has to survive attribute → registration.
            var attrs = (RegisterCommandAttribute[])typeof(FvOnceCommand)
                .GetCustomAttributes(typeof(RegisterCommandAttribute), false);
            var handlers = new System.Collections.Generic.List<SignalHandlerAttribute>();
            foreach (var a in attrs)
            {
                handlers.Add(new SignalHandlerAttribute(a.SignalType)
                {
                    Mode = a.Mode,
                    Priority = a.Priority,
                    OneShot = a.OneShot,
                    IsAsync = a.IsAsync ? true : (bool?)null
                });
            }
            bus.GetType()
                .GetMethod("RegisterCommandType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(bus, new object[] { typeof(FvOnceCommand), handlers, null, null });

            bus.Fire(new FvOnceSignal { Value = 1 });
            bus.Fire(new FvOnceSignal { Value = 2 });
            bus.Fire(new FvOnceSignal { Value = 3 });

            bool ok = FvOnceCommand.Executions == 1;
            bus.Dispose();
            container.Dispose();
            Report("FV8. RegisterCommand_OneShot_Honored", ok,
                $"executions={FvOnceCommand.Executions} (expected exactly 1)");
        }

        private struct FvModeSignal { public int Value; }
        private sealed class FvModeCommand : ICommand<FvModeSignal> { public void Execute(FvModeSignal signal) { } }

        private static void Test_MixedMode_Rejected_On_Incoming()
        {
            var container = new NexusDI();
            var registry = new CommandRegistry(container);
            registry.RegisterCommand(typeof(FvModeSignal), typeof(FvModeCommand), ExecutionMode.Sequential, 0, false);

            bool rejected = false;
            try
            {
                registry.RegisterCommand(typeof(FvModeSignal), typeof(FvModeCommand), ExecutionMode.Concurrent, 1, false);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            registry.Dispose();
            container.Dispose();
            Report("FV9. MixedMode_Rejected_On_Incoming", rejected,
                $"rejected={rejected} (Sequential then Concurrent must throw)");
        }

        // ── Observable collection ────────────────────────────────────────────

        private static void Test_ObservableList_Indexer_Notifies()
        {
            var list = new ObservableList<string>();
            list.Add("a");

            int replaceCount = 0;
            string sawOld = null, sawNew = null;
            list.OnReplaced((index, oldItem, newItem) =>
            {
                replaceCount++;
                sawOld = oldItem;
                sawNew = newItem;
            });

            list[0] = "b";

            bool ok = replaceCount == 1 && sawOld == "a" && sawNew == "b" && list[0] == "b";
            Report("FV10. ObservableList_Indexer_Notifies", ok,
                $"replaceCount={replaceCount} old={sawOld} new={sawNew}");
        }
    }
}
