// Counter sample proof (#289). Compiles Samples~/Counter as shipped (minus its two uGUI files) and boots
// CounterLifecycle on the ContextData the sample README's step 5 creates: a NEW asset, so strict injection
// is ON (ContextData.EnableStrictInjection's field initializer). Before the fix the lifecycle bound
// FeedbackService, UIManager, EconomyService and ProgressionService, none of which the sample uses, and
// FeedbackService's [Inject] IAudioService / IHapticService and UIManager's [Inject] IUIAssetProvider are
// bound nowhere: strict injection threw "Strict injection failed ... is not registered" at start-up and no
// mediator ever bound. No gate ran the sample, so nothing saw it (#308).

using System;
using System.Threading;
using Nexus.Core;
using Nexus.Samples.Counter;
using UnityEngine;

namespace NexusBench
{
    public static class CounterSampleSuite
    {
        public static int Run()
        {
            int failures = 0;
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === Counter sample (starts on the ContextData its README creates) ===");

            Context context = null;
            try
            {
                var data = ScriptableObject.CreateInstance<ContextData>();   // every other field keeps its default, like the new asset
                data.ScopeTag = "Counter";
                // The harness assembly holds other suites' handlers and the convention lookup would find them;
                // CounterLifecycle is handed over explicitly, which is what auto discovery does in the scene.
                data.EnableAutoDiscovery = false;

                context = new Context(parent: null, contextData: data);
                var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
                context.ConfigureWithBuilder(builder, new IContextLifecycle[] { new CounterLifecycle() }, scanAssemblies: false);

                int issues = builder.Validate().Count;

                bool booted = false;
                string bootError = null;
                try
                {
                    context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None).GetAwaiter().GetResult();
                    booted = true;
                }
                catch (Exception ex)
                {
                    bootError = $"{ex.GetType().Name}: {ex.Message}";
                }

                int count = -1;
                if (booted)
                {
                    context.SignalBus.Fire(new CounterSignal(2));
                    count = context.TryResolve<ICounterModel>()?.Count.Value ?? -1;
                }

                bool ok = issues == 0 && booted && count == 2;
                Check(ref failures, "CNT1. CounterSample_StartsOnStrictContextData_AndCountsAClick", ok,
                    $"diValidationIssues={issues}, booted={booted}{(bootError != null ? " (" + bootError + ")" : "")}, countAfterOneClickOf2={count}");
            }
            catch (Exception ex)
            {
                Check(ref failures, "CNT1. CounterSample_StartsOnStrictContextData_AndCountsAClick", false,
                    $"suite exception: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { context?.Dispose(); } catch { /* best-effort */ }
            }

            Console.WriteLine(failures == 0
                ? "[Nexus Benchmark] COUNTER SAMPLE PASSED ✓"
                : $"[Nexus Benchmark] {failures} COUNTER SAMPLE CHECK(S) FAILED ✗");
            return failures;
        }

        private static void Check(ref int failures, string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("CounterSample", name, ok, detail);
            if (!ok) failures++;
        }
    }
}
