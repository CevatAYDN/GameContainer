using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Starter
{
    /// <summary>
    /// Lifecycle wiring for the Nexus Starter template.
    /// Binds the reactive model and the signal → command pipeline.
    /// Auto-discovered by Nexus when Auto-Discovery is enabled in ContextData.
    /// </summary>
    public class NexusStartLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            // ── Bind reactive model (singleton, auto-notifies views) ──
            builder.BindReactiveModel<INexusStartModel, NexusStartModel>();

            // ── Bind signal → command (Sequential mode) ──
            builder.BindSignal<NexusStartSignal>().To<NexusStartCommand>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
