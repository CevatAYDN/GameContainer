// Demo compatibility proof. The real demo (Nexus/Assets/Scripts/Demo) is Unity-coupled
// and cannot compile in this harness, so this suite replicates DemoGlobalLifecycle's exact
// OnConfigure binding graph with harness-stubbed stand-ins for the Unity-only bits
// (models/commands) and boots a REAL Context with the demo's exact ContextData flags
// (DemoBootstrap.CreateRoot: EnableStrictInjection + FailOnValidationErrors).
//
// Why this suite exists: before the provider-binding + BindServiceInterfacesAndSelfTo
// fixes, this graph failed strict validation with 6+ unbound [Inject] dependencies
// (IAudioRootProvider, IUIAssetProvider, IAudioService, IHapticService,
// INetworkEconomyValidator, ILocalizationTableProvider, ...) and FailOnValidationErrors
// made the demo throw NexusDiValidationException at boot — the demo could not start at
// all. This suite is the harness-side regression gate for "the demo boots".

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace NexusBench
{
    // ── Demo graph stand-ins (Unity demo files can't compile here) ──────────────
    public struct DemoCompatGameplaySignal { public int Val; }
    public struct DemoCompatUISignal { public int Val; }

    public interface IDemoCompatGameplayModel : IReactiveModel { }
    public class DemoCompatGameplayModel : IDemoCompatGameplayModel
    {
        public ValueTask OnBind(CancellationToken ct) => default;
    }

    public interface IDemoCompatUIModel : IReactiveModel { }
    public class DemoCompatUIModel : IDemoCompatUIModel
    {
        public ValueTask OnBind(CancellationToken ct) => default;
    }

    public class DemoCompatGameplayCommand : ICommand<DemoCompatGameplaySignal>
    {
        public static int FiredCount;
        public void Execute(DemoCompatGameplaySignal signal) => FiredCount++;
    }

    public class DemoCompatUICommand : ICommand<DemoCompatUISignal>
    {
        public static int FiredCount;
        public void Execute(DemoCompatUISignal signal) => FiredCount++;
    }

    public static class DemoCompatibilitySuite
    {
        public static int Run()
        {
            int failures = 0;
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === Demo compatibility (binding graph boots under strict validation) ===");

            Context context = null;
            Context badContext = null;
            try
            {
                // The demo's exact flags (DemoBootstrap.CreateRoot).
                var data = ScriptableObject.CreateInstance<ContextData>();
                data.ScopeTag = "Global";
                data.EnableAutoDiscovery = false;
                data.EnableStrictInjection = true;
                data.FailOnValidationErrors = true;

                context = new Context(parent: null, contextData: data);
                var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
                ConfigureDemoGraph(builder);

                // ── The regression assertion: the full demo graph must have ZERO DI issues ──
                var issues = builder.Validate();
                bool cleanGraph = issues.Count == 0;

                // ── Boot: full pipeline (reactive models → eager services → lifecycle) ──
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

                // ── Every demo-consumed service must resolve (interface AND concrete) ──
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
                    context.TryResolve<IDemoCompatGameplayModel>() != null,
                    context.TryResolve<IDemoCompatUIModel>() != null,
                };
                bool allResolve = true;
                for (int i = 0; i < resolves.Length; i++)
                    if (!resolves[i]) { allResolve = false; break; }

                // ── The demo's shared-singleton invariant: economy + progression share ONE
                //    SaveThrottler (the exact setup where single-slot clobbering lost data) ──
                var sharedThrottler = context.TryResolve<SaveThrottler>();
                var eco = context.TryResolve<EconomyService>();
                var viaInterface = context.TryResolve<ISaveThrottler>();
                bool sharedSingleton = sharedThrottler != null && eco != null
                    && ReferenceEquals(eco.SaveThrottler, sharedThrottler);
                bool sameAcrossKeys = sharedThrottler != null && viaInterface != null
                    && ReferenceEquals(sharedThrottler, viaInterface);


                // ── Signal→command wiring works through the real bus ──
                DemoCompatUICommand.FiredCount = 0;
                context.SignalBus.Fire(new DemoCompatUISignal { Val = 1 });
                bool commandWired = DemoCompatUICommand.FiredCount == 1;

                // ── Negative control: the validator has teeth. Drop IUIAssetProvider →
                //    UIManager's [Inject] dependency must be flagged (the pre-fix demo). ──
                var badData = ScriptableObject.CreateInstance<ContextData>();
                badContext = new Context(parent: null, contextData: badData);
                var badBuilder = new ContextBuilder(badContext.Container, badContext.SignalBusInternal);
                badBuilder.BindServiceInterfacesAndSelfTo<UIManager>();
                bool validatorHasTeeth = badBuilder.Validate().Count >= 1;

                bool ok = cleanGraph && booted && allResolve && sharedSingleton && commandWired && validatorHasTeeth;
                Check(ref failures, "DEMO. DemoBindingGraph_Validates_Boots_Resolves", ok,
                    $"validateIssues={issues.Count}, booted={booted}{(bootError != null ? " (" + bootError + ")" : "")}, allResolve={allResolve}, sharedSingleton={sharedSingleton}, sameAcrossKeys={sameAcrossKeys}, commandWired={commandWired}, validatorHasTeeth={validatorHasTeeth}");
            }
            catch (Exception ex)
            {
                Check(ref failures, "DEMO. DemoBindingGraph_Validates_Boots_Resolves", false,
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
                ? "[Nexus Benchmark] DEMO COMPATIBILITY PASSED ✓"
                : $"[Nexus Benchmark] {failures} DEMO COMPATIBILITY CHECK(S) FAILED ✗");
            return failures;
        }

        /// <summary>Mirrors DemoGlobalLifecycle.OnConfigure exactly.</summary>
        private static void ConfigureDemoGraph(IContextBuilder builder)
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

            // ── Reactive models + commands (stand-ins for the Unity demo types) ──
            builder.BindReactiveModel<IDemoCompatGameplayModel, DemoCompatGameplayModel>();
            builder.BindReactiveModel<IDemoCompatUIModel, DemoCompatUIModel>();
            builder.BindSignal<DemoCompatGameplaySignal>().To<DemoCompatGameplayCommand>();
            builder.BindSignal<DemoCompatUISignal>().To<DemoCompatUICommand>();

            // ── Harness-only accommodation: the demo context assembly-scans the ENTIRE
            //    harness assembly and auto-registers signal handlers from OTHER suites
            //    (CapabilitiesSuite.CapCommandA → CapTracker, ServicesSuite.SvcCounterCommand
            //    → TestCounter). In Unity those types live in the demo assembly and are absent
            //    here, so bind their deps so strict validation stays green. These commands never
            //    fire during this suite — the binds only satisfy the graph. ──
            builder.BindInstance(new CapTracker());
            builder.BindInstance(new TestCounter());
        }

        private static void Check(ref int failures, string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("DemoCompatibility", name, ok, detail);
            if (!ok) failures++;
        }
    }
}
