using Nexus.Core;

namespace Game
{
    public class GameMediator : Mediator<GameView>
    {
        [Inject] private GameModel _model;

        protected override void OnBind()
        {
            // Named handlers, not inline lambdas: OnUnbind must be able to remove the
            // EXACT delegate that was added. Inline lambdas cannot be unsubscribed, so
            // pooled mediator reuse would stack duplicate handlers.
            _model.Counter.OnChanged(OnCounterChanged);
            View.UpdateDisplay(_model.Counter.Value);
            View.OnIncrementClicked += OnIncrementClicked;
        }

        protected override void OnUnbind()
        {
            // Remove only THIS mediator's handler — ClearOnChanged() would wipe every
            // other subscriber of the shared model property.
            _model.Counter.RemoveOnChanged(OnCounterChanged);
            View.OnIncrementClicked -= OnIncrementClicked;
        }

        private void OnCounterChanged(int oldValue, int newValue)
        {
            View.UpdateDisplay(newValue);
        }

        private void OnIncrementClicked()
        {
            SignalBus.Fire(new GameSignal(1));
        }
    }
}