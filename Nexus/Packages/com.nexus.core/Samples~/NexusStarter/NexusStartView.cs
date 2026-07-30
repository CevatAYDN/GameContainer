using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Starter
{
    [Mediator(typeof(NexusStartMediator))]
    public class NexusStartView : View
    {
        public event System.Action OnIncrementClicked;

        [Header("UI References")]
        [SerializeField] private Button _incrementButton;
        [SerializeField] private Text _counterText;

        protected override void OnBind(IContext context)
        {
            if (_incrementButton != null)
                _incrementButton.onClick.AddListener(() => OnIncrementClicked?.Invoke());
        }

        protected override void OnUnbind()
        {
            if (_incrementButton != null)
                _incrementButton.onClick.RemoveAllListeners();
        }

        public void UpdateCounter(int value)
        {
            if (_counterText != null)
                _counterText.text = $"Counter: {value}";
        }
    }
}
