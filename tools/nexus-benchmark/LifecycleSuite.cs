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
            Test_Lifecycle_OnStart_Throw_FailsBootFast();
            Test_Root_AutoAdds_SupportComponents();

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
                    try { ctx.Dispose(); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Nexus Lifecycle] Dispose during teardown failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            Report("L2. ContextLifecycle_Phases_And_Dispose", ok, detail);
        }

        private static void Test_Root_AutoAdds_SupportComponents()
        {
            // L4: a Root created programmatically (Dashboard "Create Root",
            // AddComponent<Root>(), wizard scenes) must auto-add QueueDrainer +
            // MetricsSampler on Awake. Without QueueDrainer the HybridQueue never drains
            // (queued signals silently never run); without MetricsSampler the game never
            // records FPS/memory/GC, so the Performance Dashboard reads a flat 0.0.
            var go = new UnityEngine.GameObject("ProgRootSupport");
            var root = go.AddComponent<Root>();
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.name = "SupportProbeData";
            data.ScopeTag = "SupportProbe";
            root.SetUp(data);

            bool ok = false;
            string detail;
            try
            {
                var awake = typeof(Root).GetMethod("Awake",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                awake?.Invoke(root, null);

                bool hasDrainer = go.GetComponent<QueueDrainer>() != null;
                bool hasSampler = go.GetComponent<MetricsSampler>() != null;
                ok = hasDrainer && hasSampler;
                detail = $"queueDrainer={hasDrainer} metricsSampler={hasSampler}";

                // Idempotent: a second Awake (double boot) must not double-add.
                awake?.Invoke(root, null);
                int drainerCount = go.GetComponents<QueueDrainer>().Length;
                int samplerCount = go.GetComponents<MetricsSampler>().Length;
                bool noDouble = drainerCount == 1 && samplerCount == 1;
                ok = ok && noDouble;
                detail += $" afterSecondAwake drainerCount={drainerCount} samplerCount={samplerCount}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { UnityEngine.Object.Destroy(go); } catch { }
                try { NexusRuntime.Reset(); } catch (Exception ex)
                {
                    Console.WriteLine($"[Nexus Lifecycle] Reset during teardown failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Report("L4. Root_AutoAdds_QueueDrainer_And_MetricsSampler", ok, detail);
        }

        private static void Test_Lifecycle_OnStart_Throw_FailsBootFast()
        {
            // L3: a boot lifecycle whose OnStartAsync throws must fail the boot lifecycle
            // FAST (Context.InitializeLifecycleAsync propagates the exception; Root.Start
            // catches it, disposes the context and clears IsInitialized). This is the
            // documented fail-fast contract for boot lifecycles — deliberately distinct
            // from ContextLifecycleOrchestrator, which isolates IStartable singletons.
            var lifecycle = new StartProbeLifecycle { ThrowOnStart = true };
            var ctx = ContextFactory.Create();
            bool threw = false;
            string phases = "";
            try
            {
                ctx.Configure(new[] { lifecycle });
                var initMethod = typeof(Context).GetMethod("InitializeLifecycleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var vt = (ValueTask)initMethod.Invoke(ctx, new object[] { new IContextLifecycle[] { lifecycle }, ctx.LifetimeToken });
                vt.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                threw = true;
            }
            finally
            {
                if (ctx != null)
                {
                    try { ctx.Dispose(); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Nexus Lifecycle] Dispose during teardown failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                // Capture AFTER dispose: the teardown phase must still run even though
                // start threw (fail-fast aborts init, but cleanup is unconditional).
                phases = string.Join(",", lifecycle.Log);
            }

            // configure + init ran, start ran and threw (init aborted), dispose still ran.
            bool ok = threw && phases == "configure,init,start,dispose";
            Report("L3. Lifecycle_OnStart_Throw_FailsBootFast", ok,
                $"threw={threw} phases=[{phases}] (expected configure,init,start + throw, then dispose)");
        }
    }
}
