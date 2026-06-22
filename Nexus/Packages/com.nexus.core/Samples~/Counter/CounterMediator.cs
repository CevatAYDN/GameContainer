using Nexus.Core;

namespace Nexus.Samples.Counter
{
    public class CounterMediator : Mediator<CounterView>
    {
        [Inject] public ICounterModel Model { get; set; }

        protected override void OnBind()
        {
            Model.OnCountChanged += OnCountChanged;
            View.UpdateDisplay(Model.Count);
            View.OnIncrementClicked += OnIncrementClicked;
        }

        protected override void OnUnbind()
        {
            if (Model != null)
                Model.OnCountChanged -= OnCountChanged;
            
            if (View != null)
                View.OnIncrementClicked -= OnIncrementClicked;
        }

        private void OnCountChanged(int currentCount)
        {
            View.UpdateDisplay(currentCount);
        }

        private void OnIncrementClicked()
        {
            SignalBus.Fire(new CounterSignal(1));
        }
    }
}
