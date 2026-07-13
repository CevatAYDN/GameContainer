using Nexus.Core;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Connects the view to the signal bus and the model. A single button click
    /// exercises every execution mode + the async command + the composite fan-in,
    /// so the sample is fully observable from one interaction.
    /// </summary>
    public class CounterMediator : Mediator<CounterView>
    {
        [Inject] public ICounterModel Model { get; set; }

        protected override void OnBind()
        {
            // ObservableProperty<T>.OnChanged takes an (oldValue, newValue) handler.
            Model.Count.OnChanged(OnCountChanged);
            View.UpdateDisplay(Model.Count.Value);
            View.OnIncrementClicked += OnIncrementClicked;
        }

        protected override void OnUnbind()
        {
            if (Model != null)
                Model.Count.RemoveOnChanged(OnCountChanged);

            if (View != null)
                View.OnIncrementClicked -= OnIncrementClicked;
        }

        private void OnCountChanged(int oldValue, int newValue)
        {
            if (View != null)
                View.UpdateDisplay(newValue);
        }

        private void OnIncrementClicked()
        {
            // Exercise every building block with one click:
            SignalBus.Fire(new CounterSignal(1));               // Sequential
            SignalBus.Fire(new CounterLoadSignal());             // Concurrent
            SignalBus.Fire(new CounterPersistSignal());          // Exclusive
            _ = SignalBus.FireAsync(new CounterAsyncSignal(1));  // Async (IAsyncCommand)
            SignalBus.Fire(new CounterAckSignal());              // Composite fan-in (1/2)
            SignalBus.Fire(new CounterDataSignal());             // Composite fan-in (2/2 -> fires)
        }
    }
}
