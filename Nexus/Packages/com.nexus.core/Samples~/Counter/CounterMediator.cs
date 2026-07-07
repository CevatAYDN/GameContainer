using Nexus.Core;

namespace Nexus.Samples.Counter
{
    public class CounterMediator : Mediator<CounterView>
    {
        [Inject] public ICounterModel Model { get; set; }

        protected override void OnBind()
        {
            Model.Count.OnChanged += OnCountChanged;
            View.UpdateDisplay(Model.Count.Value);
            View.OnIncrementClicked += OnIncrementClicked;
        }

        protected override void OnUnbind()
        {
            if (Model != null)
                Model.Count.OnChanged -= OnCountChanged;
            
            if (View != null)
                View.OnIncrementClicked -= OnIncrementClicked;
        }

        private void OnCountChanged(int currentCount)
        {
            if (View != null)
                View.UpdateDisplay(currentCount);
        }

        private void OnIncrementClicked()
        {
            SignalBus.Fire(new CounterSignal(1));
        }
    }
}
