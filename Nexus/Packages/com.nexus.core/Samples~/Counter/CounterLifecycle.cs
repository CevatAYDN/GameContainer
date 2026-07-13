using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Context lifecycle: binds the model, services, recovery strategy and every
    /// command (all four execution modes + async + composite). This single class is
    /// the "wiring" surface that demonstrates the full Nexus building-block set.
    /// </summary>
    public class CounterLifecycle : IContextLifecycle
    {
        private readonly CounterTraceSink _traceSink = new();

        public void OnConfigure(IContextBuilder builder)
        {
            // ── Model ─────────────────────────────────────────────
            // Reactive singleton; the runtime calls OnBind() automatically.
            builder.BindReactiveModel<ICounterModel, CounterModel>();

            // ── Services ──────────────────────────────────────────
            // BindService auto-manages InitializeAsync / OnDispose (INexusService).
            builder.BindService<ICounterTelemetryService, CounterTelemetryService>();
            // Bind a built-in service (zero-dependency Storage implementation).
            builder.Bind<IPlayerPrefsService, UnityPlayerPrefsService>();

            // Representative built-in Nexus services (INexusService).
            builder.BindService<IEconomyService, EconomyService>();
            builder.BindService<IProgressionService, ProgressionService>();
            builder.BindService<IFeedbackService, FeedbackService>();
            builder.BindService<IWindowManager, WindowManager>();

            // ── Error Recovery ────────────────────────────────────
            // SignalBus auto-resolves this when a command throws.
            builder.Bind<IRecoveryStrategy, CounterRecoveryStrategy>();

            // ── Commands: all four execution modes + async ────────
            // Sequential (default) — existing increment.
            builder.BindSignal<CounterSignal>().To<CounterIncrementCommand>();

            // Concurrent — parallel I/O-style handlers for the same signal.
            builder.BindCommand<CounterLoadSignal, CounterLoadCommand>(ExecutionMode.Concurrent);

            // Exclusive — single-writer guarantee.
            builder.BindCommand<CounterPersistSignal, CounterPersistCommand>(ExecutionMode.Exclusive);

            // Async command (IAsyncCommand) with a [CommandTimeout].
            builder.BindAsyncCommand<CounterAsyncSignal, CounterAsyncCommand>();

            // Composite (fan-in) — discovered automatically via
            // [CompositeSignalHandler] on CounterCompositeCommand.
            // No explicit BindCommand call is needed.
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;

        public ValueTask OnStartAsync(CancellationToken ct)
        {
            // Attach a causal-tracing sink to observe the full signal/command chain.
            NexusTrace.AddSink(_traceSink);
            return default;
        }

        public void OnDispose()
        {
            NexusTrace.RemoveSink(_traceSink);
        }
    }
}
