using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    public class TEST1Mediator : Mediator<TEST1View>
    {
        [Inject] public ITEST1Model Model { get; set; }

        protected override void OnBind()
        {
            Debug.Log($"[{nameof(TEST1Mediator)}] Binding View to Model...");

            // Listen to model changes
            Model.OnCounterChanged += OnModelCounterChanged;

            // Initialize view state
            View.UpdateCounterText(Model.Counter);

            // Respond to user interaction
            View.OnButtonClicked += OnViewButtonClicked;
        }

        protected override void OnUnbind()
        {
            Debug.Log($"[{nameof(TEST1Mediator)}] Unbinding...");

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
            Debug.Log($"[{nameof(TEST1Mediator)}] Button clicked on view! Dispatching counter signal...");
            SignalBus.Fire(new TEST1CounterSignal(1));
        }

        private void OnModelCounterChanged(int newValue)
        {
            View.UpdateCounterText(newValue);
        }
    }
}
