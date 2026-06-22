using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Samples.Counter
{
    public class CounterLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            builder.BindModel<ICounterModel, CounterModel>();
            builder.BindSignal<CounterSignal>().To<CounterIncrementCommand>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
