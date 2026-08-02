using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameService : NexusService<IGameService>, IGameService
    {
        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            return ValueTask.CompletedTask;
        }

        public override void OnDispose()
        {
        }
    }
}