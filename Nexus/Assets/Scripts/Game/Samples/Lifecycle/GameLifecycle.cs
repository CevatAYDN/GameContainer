using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Lifecycle configuration for the Game context.
    /// Supports two clean workflows:
    /// 1. Attached as a MonoBehaviour component to the GameRoot GameObject (auto-discovered by Root.GetComponents&lt;IContextLifecycle&gt;()).
    /// 2. Implemented as a plain C# class named '{ScopeTag}Lifecycle' (e.g., GameLifecycle) when ContextData has EnableAutoDiscovery enabled.

    /// </summary>
    public class GameLifecycle : MonoBehaviour, IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            builder.BindReactiveModel<GameModel>();
            builder.BindSignal<GameSignal>().To<GameCommand>();
            builder.BindService<IGameService, GameService>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}