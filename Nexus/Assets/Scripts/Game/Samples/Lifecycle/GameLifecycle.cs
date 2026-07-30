using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Must be a MonoBehaviour so Root.GetComponents&lt;IContextLifecycle&gt;() can discover it.
    /// Attach this component to the GameRoot GameObject.
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