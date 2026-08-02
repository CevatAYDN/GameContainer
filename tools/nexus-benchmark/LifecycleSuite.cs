using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    public static class LifecycleSuite
    {
        private static int _failures;

        private sealed class StartupValidationLifecycle : IContextLifecycle
        {
            public ContextBuilder Builder;

            public void OnConfigure(IContextBuilder builder)
            {
                Builder = (ContextBuilder)builder;
                builder.EnableStrictInjection();
                builder.Bind<BrokenHost>();
                builder.Bind<LazyHost>();
                builder.Bind<OptionalHost>();
                builder.Bind<CtorHost>();
            }

            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class BrokenHost
        {
#pragma warning disable 0649
            [Inject] public MissingDep Dep;
#pragma warning restore 0649
        }

        private sealed class MissingDep { }

        private sealed class LazyHost
        {
#pragma warning disable 0649
            [Inject] public LazyInjection<MissingDep> Dep;
#pragma warning restore 0649
        }

        private sealed class OptionalHost
        {
#pragma warning disable 0649
            [OptionalInject] public MissingDep Dep;
#pragma warning restore 0649
        }

        private sealed class CtorHost
        {
            public CtorHost(MissingDep dep) { }
        }

        private sealed class StartProbeLifecycle : IContextLifecycle
        {
            public readonly List<string> Log = new();
            public bool ThrowOnStart;

            public void OnConfigure(IContextBuilder builder)
            {
                Log.Add("configure");
            }

            public ValueTask OnInitializeAsync(CancellationToken ct)
            {
                Log.Add("init");
                return default;
            }

            public ValueTask OnStartAsync(CancellationToken ct)
            {
                Log.Add("start");
                if (ThrowOnStart) throw new InvalidOperationException("start-boom");
                return default;
            }

            public void OnDispose()
            {
                Log.Add("dispose");
            }
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Lifecycle] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Lifecycle", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Lifecycle] STARTUP VALIDATION + LIFECYCLE PROOF");
            Console.WriteLine("===============================================================================");

            Test_StartupValidation_DefaultOn_And_OptOutSafe();
            Test_ContextLifecycle_Phases_And_Dispose();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Lifecycle] ALL LIFECYCLE TESTS PASSED ✓"
                : $"[Lifecycle] {_failures} LIFECYCLE TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Test_StartupValidation_DefaultOn_And_OptOutSafe()
        {
            bool saved = ContextBuilder.ValidateOnStartup;
            bool ok = false;
            string detail;
            try
            {
                ContextBuilder.ValidateOnStartup = true;
                var ctx = ContextFactory.Create();
                try
                {
                    var lifecycle = new StartupValidationLifecycle();
                    ctx.Configure(new[] { lifecycle });
                    var issues = lifecycle.Builder.Validate();

                    bool missingField = issues.Exists(i => i.SourceType == typeof(BrokenHost) && i.IssueType == DiValidationIssueType.MissingFieldDependency);
                    bool ctorFlagged = issues.Exists(i => i.SourceType == typeof(CtorHost) && i.IssueType == DiValidationIssueType.MissingConstructorDependency);
                    bool lazyNotFlagged = !issues.Exists(i => i.SourceType == typeof(LazyHost));
                    bool optionalNotFlagged = !issues.Exists(i => i.SourceType == typeof(OptionalHost));

                    ContextBuilder.ValidateOnStartup = false;
                    var ctx2 = ContextFactory.Create();
                    try
                    {
                        ctx2.Configure(new[] { new StartupValidationLifecycle() });
                    }
                    finally
                    {
                        ctx2.Dispose();
                    }

                    ok = missingField && ctorFlagged && lazyNotFlagged && optionalNotFlagged;
                    detail = $"missingField={missingField} ctorFlagged={ctorFlagged} lazyNotFlagged={lazyNotFlagged} optionalNotFlagged={optionalNotFlagged}";
                }
                finally
                {
                    ctx.Dispose();
                }
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                ContextBuilder.ValidateOnStartup = saved;
            }

            Report("L1. StartupValidation_DefaultOn_And_OptOutSafe", ok, detail);
        }

        private static void Test_ContextLifecycle_Phases_And_Dispose()
        {
            var ctx = ContextFactory.Create();
            var lifecycle = new StartProbeLifecycle();
            bool ok = false;
            string detail;
            try
            {
                ctx.Configure(new[] { lifecycle });
                var initMethod = typeof(Context).GetMethod("InitializeLifecycleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var vt = (ValueTask)initMethod.Invoke(ctx, new object[] { new IContextLifecycle[] { lifecycle }, ctx.LifetimeToken });
                vt.GetAwaiter().GetResult();
                ctx.Dispose();

                bool phases = string.Join(",", lifecycle.Log) == "configure,init,start,dispose";
                ok = phases;
                detail = $"phases=[{string.Join(",", lifecycle.Log)}]";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (ctx != null)
                {
                    try { ctx.Dispose(); } catch { }
                }
            }

            Report("L2. ContextLifecycle_Phases_And_Dispose", ok, detail);
        }
    }
}
