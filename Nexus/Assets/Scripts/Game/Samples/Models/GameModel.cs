using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameModel : IReactiveModel
    {
        public ObservableProperty<int> Counter { get; } = new(0);
        public ValueTask OnBind(CancellationToken ct) => default;
    }
}