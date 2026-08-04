using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Demo
{
    /// <summary>
    /// Global context lifecycle - binds all demo services, models, and commands.
    /// Registered explicitly by <see cref="DemoBootstrap"/> via <see cref="Root.RegisterLifecycle"/>.
    ///
    /// Binding rules used here (see ContextBuilder for semantics):
    ///  - <c>BindServiceInterfacesAndSelfTo&lt;T&gt;</c>: interfaces + concrete type share ONE
    ///    singleton AND the service is eagerly initialized at startup — required for services
    ///    whose InitializeAsync sets up runtime state (TickService driver, AudioService
    ///    sources, WindowManager canvas, SaveThrottler tick registration, ObjectPoolService
    ///    root, ProgressionService prefs load).
    ///  - <c>BindInterfacesAndSelfTo&lt;T&gt;</c>: same shared singleton, constructed on first
    ///    resolve — fine for services with no startup-time work.
    ///  - <c>BindService&lt;T&gt;</c>: eager, concrete key only (not used here anymore because the
    ///    demo consumes most services both by interface and concrete type).
    /// </summary>
    public class DemoGlobalLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            // ── Storage (non-service: IPlayerPrefsService + concrete) ──
            builder.BindInterfacesAndSelfTo<EncryptedStorageService>();

            // ── Providers required by the services below ──
            builder.BindInterfacesAndSelfTo<DefaultAudioRootProvider>();  // IAudioRootProvider (AudioService)
            builder.BindInterfacesAndSelfTo<ResourcesUIAssetProvider>();  // IUIAssetProvider (WindowManager/UIManager)
            builder.BindInterfacesAndSelfTo<UnityTimeProvider>();         // ITimeProvider (SaveThrottler)

            // ── Eager services (InitializeAsync must run at startup) ──
            builder.BindServiceInterfacesAndSelfTo<ObjectPoolService>();
            builder.BindServiceInterfacesAndSelfTo<WindowManager>();
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

            // ── Adapter Factories (non-service: not INexusService) ──
            builder.BindInterfacesAndSelfTo<AdAdapterFactory>();
            builder.BindInterfacesAndSelfTo<IapAdapterFactory>();

            // ── Reactive Models ───────────────────────────────────
            builder.BindReactiveModel<IDemoGameplayModel, DemoGameplayModel>();
            builder.BindReactiveModel<IDemoUIModel, DemoUIModel>();

            // ── Commands ──────────────────────────────────────────
            builder.BindSignal<DemoGameplaySignal>().To<DemoGameplayCommand>();
            builder.BindSignal<DemoUISignal>().To<DemoUICommand>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;

        public ValueTask OnStartAsync(CancellationToken ct)
        {
            // The demo starts on the main menu; gameplay starts via user action
            // (Play button fires DemoGameplaySignal.GameStarted). Nothing to do here.
            return default;
        }

        public void OnDispose() { }
    }
}
