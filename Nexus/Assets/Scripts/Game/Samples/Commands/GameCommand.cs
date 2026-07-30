using Nexus.Core;

namespace Game
{
    public class GameCommand : ICommand<GameSignal>
    {
        [Inject] private GameModel _model;

        public void Execute(GameSignal signal) => _model.Counter.Value += signal.Value;
    }
}