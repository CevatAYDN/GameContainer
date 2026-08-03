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
            TrackObservable(_model.Counter, _counterChangedHandler);
            View.UpdateDisplay(_model.Counter.Value);
            View.OnIncrementClicked += _incrementClickedHandler;
        }

        protected override void OnUnbind()
        {
            if (View != null)
            {
                View.OnIncrementClicked -= _incrementClickedHandler;
            }
        }
    }
}