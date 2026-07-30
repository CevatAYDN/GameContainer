using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameService : NexusService<IGameService>, IGameService
    {
        public override ValueTask InitializeAsync(CancellationToken ct) => default;
        public override void OnDispose() { }
    }
}