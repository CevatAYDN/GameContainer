using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus
{
    // Automatically binds the view instance to its custom Mediator on context registration
    [Mediator(typeof(Test1Mediator))]
    public class Test1View : View
    {
        public event System.Action OnButtonClicked;

        [Header("UI References (Assign in Inspector)")]
        [SerializeField] private Button incrementButton;
        [SerializeField] private Text counterText;

        protected override void OnBind(IContext context)
        {
            if (incrementButton != null)
            {
                incrementButton.onClick.AddListener(() => OnButtonClicked?.Invoke());
            }
        }

        protected override void OnUnbind()
        {
            if (incrementButton != null)
            {
                incrementButton.onClick.RemoveAllListeners();
            }
        }

        [ContextMenu("Simulate Button Click")]
        public void SimulateClick()
        {
            OnButtonClicked?.Invoke();
        }

        public void UpdateCounterText(int value)
        {
            if (counterText != null)
            {
                counterText.text = $"Counter: {value}";
            }
            else
            {
                Debug.Log($"[{nameof(Test1View)}] UI Counter updated to: {value}");
            }
        }
    }
}
