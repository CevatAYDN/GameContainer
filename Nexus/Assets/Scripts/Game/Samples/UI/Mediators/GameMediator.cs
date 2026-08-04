using Nexus.Core;

namespace Game
{
    public class GameMediator : Mediator<GameView>
    {
        [Inject] private GameModel _model;

        protected override void OnBind()
        {
            _model.Counter.OnChanged((o, n) => View.UpdateDisplay(n));
            View.UpdateDisplay(_model.Counter.Value);
            View.OnIncrementClicked += () => SignalBus.Fire(new GameSignal(1));
        }

        protected override void OnUnbind()
        {
            _model.Counter.ClearOnChanged();
        }
    }
}