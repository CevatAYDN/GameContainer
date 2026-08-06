// Service-graph proof. Boots a REAL Context with the full package service graph
// (all runtime services + their providers and adapter factories) under the strictest
// validation flags (EnableStrictInjection + FailOnValidationErrors) and proves the
// graph validates with zero DI issues, boots, and resolves every service by interface
// AND concrete type.
//
// Why this suite exists: it is the successor to the deleted DemoCompatibilitySuite
// (which mirrored the Unity demo's binding graph). The demo scaffolding
// (Assets/Scripts/Demo/) was removed 2026-08-06 — Game/Samples is the canonical
// example — but the regression gate "the entire package service graph boots together
// under strict validation" must survive: before the provider-binding +
// BindServiceInterfacesAndSelfTo fixes, this graph failed strict validation with 6+
// unbound [Inject] dependencies (IAudioRootProvider, IUIAssetProvider, IAudioService,
// IHapticService, INetworkEconomyValidator, ILocalizationTableProvider, ...) and
// FailOnValidationErrors threw NexusDiValidationException at boot. This suite keeps
// that gate without any demo-specific types.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace NexusBench
{
    public static class ServiceGraphSuite
    {
        public static int Run()
        {
            int failures = 0;
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === Service graph (all package services boot under strict validation) ===");

            Context context = null;
            Context badContext = null;
            try
            {
                // The strictest boot flags (the deleted demo's bootstrap used exactly these).
                var data = ScriptableObject.CreateInstance<ContextData>();
                data.ScopeTag = "Global";
                data.EnableAutoDiscovery = false;
                data.EnableStrictInjection = true;
                data.FailOnValidationErrors = true;

                context = new Context(parent: null, contextData: data);
                var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
                ConfigureServiceGraph(builder);

                // ── The regression assertion: the full service graph must have ZERO DI issues ──
                var issues = builder.Validate();
                bool cleanGraph = issues.Count == 0;

                // ── Boot: full pipeline (eager services → lifecycle) ──
                bool booted = false;
                string bootError = null;
                try
                {
                    context.ConfigureWithBuilder(builder);
                    context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None).GetAwaiter().GetResult();
                    booted = true;
                }
                catch (Exception ex)
                {
                    bootError = $"{ex.GetType().Name}: {ex.Message}";
                }

                // ── Every game-consumed service must resolve (interface AND concrete) ──
                var resolves = new[]
                {
                    context.TryResolve<IPlayerPrefsService>() != null,
                    context.TryResolve<EncryptedStorageService>() != null,
                    context.TryResolve<IAudioRootProvider>() != null,
                    context.TryResolve<IUIAssetProvider>() != null,
                    context.TryResolve<ITimeProvider>() != null,
                    context.TryResolve<IObjectPoolService>() != null,
                    context.TryResolve<IUIManager>() != null,
                    context.TryResolve<UIManager>() != null,
                    context.TryResolve<IAudioService>() != null,
                    context.TryResolve<AudioService>() != null,
                    context.TryResolve<ITickService>() != null,
                    context.TryResolve<TickService>() != null,
                    context.TryResolve<ISaveThrottler>() != null,
                    context.TryResolve<SaveThrottler>() != null,
                    context.TryResolve<IProgressionService>() != null,
                    context.TryResolve<ProgressionService>() != null,
                    context.TryResolve<ILocalizationService>() != null,
                    context.TryResolve<LocalizationService>() != null,
                    context.TryResolve<IHapticService>() != null,
                    context.TryResolve<HapticService>() != null,
                    context.TryResolve<IFeedbackService>() != null,
                    context.TryResolve<FeedbackService>() != null,
                    context.TryResolve<IAdService>() != null,
                    context.TryResolve<AdService>() != null,
                    context.TryResolve<IIapService>() != null,
                    context.TryResolve<IapService>() != null,
                    context.TryResolve<IEconomyService>() != null,
                    context.TryResolve<EconomyService>() != null,
                    context.TryResolve<IAnalyticsService>() != null,
                    context.TryResolve<AnalyticsService>() != null,
                    context.TryResolve<IAdAdapterFactory>() != null,
                    context.TryResolve<AdAdapterFactory>() != null,
                    context.TryResolve<IIapAdapterFactory>() != null,
                    context.TryResolve<IapAdapterFactory>() != null,
                };
                bool allResolve = true;
                for (int i = 0; i < resolves.Length; i++)
                    if (!resolves[i]) { allResolve = false; break; }

                // ── Shared-singleton invariant: economy + progression share ONE SaveThrottler
                //    (the exact setup where single-slot clobbering lost data), and the
                //    interface and concrete key resolve the same instance ──
                var sharedThrottler = context.TryResolve<SaveThrottler>();
                var eco = context.TryResolve<EconomyService>();
                var viaInterface = context.TryResolve<ISaveThrottler>();
                bool sharedSingleton = sharedThrottler != null && eco != null
                    && ReferenceEquals(eco.SaveThrottler, sharedThrottler);
                bool sameAcrossKeys = sharedThrottler != null && viaInterface != null
                    && ReferenceEquals(sharedThrottler, viaInterface);

                // ── Negative control: the validator has teeth. Drop IUIAssetProvider →
                //    UIManager's [Inject] dependency must be flagged (the pre-fix graph). ──
                var badData = ScriptableObject.CreateInstance<ContextData>();
                badContext = new Context(parent: null, contextData: badData);
                var badBuilder = new ContextBuilder(badContext.Container, badContext.SignalBusInternal);
                badBuilder.BindServiceInterfacesAndSelfTo<UIManager>();
                bool validatorHasTeeth = badBuilder.Validate().Count >= 1;

                bool ok = cleanGraph && booted && allResolve && sharedSingleton && sameAcrossKeys && validatorHasTeeth;
                Check(ref failures, "SVC1. ServiceGraph_StrictInjection_Validates_Boots_Resolves", ok,
                    $"validateIssues={issues.Count}, booted={booted}{(bootError != null ? " (" + bootError + ")" : "")}, allResolve={allResolve}, sharedSingleton={sharedSingleton}, sameAcrossKeys={sameAcrossKeys}, validatorHasTeeth={validatorHasTeeth}");
            }
            catch (Exception ex)
            {
                Check(ref failures, "SVC1. ServiceGraph_StrictInjection_Validates_Boots_Resolves", false,
                    $"suite exception: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { context?.Dispose(); } catch { /* best-effort */ }
                try { badContext?.Dispose(); } catch { /* best-effort */ }
                // EncryptedStorageService wrote a SecureData folder under the stub persistent
                // path; remove it so soak mode sees no object creep.
                try { Directory.Delete(System.IO.Path.Combine(Application.persistentDataPath, "SecureData"), true); }
                catch { /* already gone */ }
            }

            Console.WriteLine(failures == 0
                ? "[Nexus Benchmark] SERVICE GRAPH PASSED ✓"
                : $"[Nexus Benchmark] {failures} SERVICE GRAPH CHECK(S) FAILED ✗");
            return failures;
        }

        /// <summary>Binds every runtime service, its providers, and adapter factories.</summary>
        private static void ConfigureServiceGraph(IContextBuilder builder)
        {
            // ── Storage (non-service: IPlayerPrefsService + concrete) ──
            builder.BindInterfacesAndSelfTo<EncryptedStorageService>();

            // ── Providers required by the services below ──
            builder.BindInterfacesAndSelfTo<DefaultAudioRootProvider>();  // IAudioRootProvider (AudioService)
            builder.BindInterfacesAndSelfTo<ResourcesUIAssetProvider>();  // IUIAssetProvider (UIManager)
            builder.BindInterfacesAndSelfTo<UnityTimeProvider>();         // ITimeProvider (SaveThrottler)

            // ── Eager services (InitializeAsync must run at startup) ──
            builder.BindServiceInterfacesAndSelfTo<ObjectPoolService>();
            builder.BindServiceInterfacesAndSelfTo<UIManager>();
            builder.BindServiceInterfacesAndSelfTo<AudioService>();
            builder.BindServiceInterfacesAndSelfTo<TickService>();
            builder.BindServiceInterfacesAndSelfTo<SaveThrottler>();
            builder.BindServiceInterfacesAndSelfTo<ProgressionService>();
            builder.BindServiceInterfacesAndSelfTo<LocalizationService>();

            // ── Lazy services (constructed on first resolve) ──
            builder.BindInterfacesAndSelfTo<HapticService>();
            builder.BindInterfacesAndSelfTo<FeedbackService>();
            builder.BindInterfacesAndSelfTo<AdService>();
            builder.BindInterfacesAndSelfTo<IapService>();
            builder.BindInterfacesAndSelfTo<EconomyService>();
            builder.BindInterfacesAndSelfTo<AnalyticsService>();

            // ── Adapter factories (non-service) ──
            builder.BindInterfacesAndSelfTo<AdAdapterFactory>();
            builder.BindInterfacesAndSelfTo<IapAdapterFactory>();

            // ── Harness-only accommodation: the context assembly-scans the ENTIRE harness
            //    assembly and auto-registers signal handlers from OTHER suites
            //    (CapabilitiesSuite.CapCommandA → CapTracker, ServicesSuite.SvcCounterCommand
            //    → TestCounter). In a game those types live in the game assembly and are absent
            //    here, so bind their deps so strict validation stays green. These commands never
            //    fire during this suite — the binds only satisfy the graph. ──
            builder.BindInstance(new CapTracker());
            builder.BindInstance(new TestCounter());
        }

        private static void Check(ref int failures, string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("ServiceGraph", name, ok, detail);
            if (!ok) failures++;
        }
    }
}
