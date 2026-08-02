using Nexus.Core;

namespace Game
{
    public class GameMediator : Mediator<GameView>
    {
        [Inject] private GameModel _model;

        private readonly System.Action<int, int> _counterChangedHandler;
        private System.Action _incrementClickedHandler;

        public GameMediator()
        {
            _counterChangedHandler = (_, value) => View?.UpdateDisplay(value);
            _incrementClickedHandler = () => SignalBus.Fire(new GameSignal(1));
        }

        protected override void OnBind()
        {
            _model.Counter.OnChanged(_counterChangedHandler);
            View.UpdateDisplay(_model.Counter.Value);
            View.OnIncrementClicked += _incrementClickedHandler;
        }

        protected override void OnUnbind()
        {
            _model.Counter.RemoveOnChanged(_counterChangedHandler);
            if (View != null)
            {
                View.OnIncrementClicked -= _incrementClickedHandler;
            }
        }
    }
}