using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    public class Test1Mediator : Mediator<Test1View>
    {
        [Inject] public ITest1Model Model { get; set; }

        protected override void OnBind()
        {
            Debug.Log($"[{nameof(Test1Mediator)}] Binding View to Model...");

            // Listen to model changes
            Model.OnCounterChanged += OnModelCounterChanged;

            // Initialize view state
            View.UpdateCounterText(Model.Counter);

            // Respond to user interaction
            View.OnButtonClicked += OnViewButtonClicked;
        }

        protected override void OnUnbind()
        {
            Debug.Log($"[{nameof(Test1Mediator)}] Unbinding...");

            if (Model != null)
            {
                Model.OnCounterChanged -= OnModelCounterChanged;
            }

            if (View != null)
            {
                View.OnButtonClicked -= OnViewButtonClicked;
            }
        }

        private void OnViewButtonClicked()
        {
            Debug.Log($"[{nameof(Test1Mediator)}] Button clicked on view! Dispatching counter signal...");
            SignalBus.Fire(new Test1CounterSignal(1));
        }

        private void OnModelCounterChanged(int newValue)
        {
            View.UpdateCounterText(newValue);
        }
    }
}
