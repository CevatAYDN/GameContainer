using Nexus.Core;

namespace Nexus.Starter
{
    /// <summary>
    /// Connects the view to the model and signal bus.
    /// Subscribes to model changes and forwards user input as signals.
    /// </summary>
    public class NexusStartMediator : Mediator<NexusStartView>
    {
        [Inject] private INexusStartModel _model;

        protected override void OnBind()
        {
            _model.Counter.OnChanged(OnCounterChanged);
            View.UpdateCounter(_model.Counter.Value);
            View.OnIncrementClicked += OnIncrementClicked;
        }

        protected override void OnUnbind()
        {
            _model.Counter.RemoveOnChanged(OnCounterChanged);
            View.OnIncrementClicked -= OnIncrementClicked;
        }

        private void OnCounterChanged(int oldValue, int newValue)
        {
            View.UpdateCounter(newValue);
        }

        private void OnIncrementClicked()
        {
            SignalBus.Fire(new NexusStartSignal(1));
        }
    }
}
