using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Starter
{
    [Mediator(typeof(NexusStartMediator))]
    public class NexusStartView : View
    {
        public event System.Action OnIncrementClicked;

        private UnityEngine.Events.UnityAction _clickHandler;

        [Header("UI References")]
        [SerializeField] private Button _incrementButton;
        [SerializeField] private Text _counterText;

        protected override void OnBind(IContext context)
        {
            if (_incrementButton == null)
                return;

            _clickHandler ??= () => OnIncrementClicked?.Invoke();
            _incrementButton.onClick.AddListener(_clickHandler);
        }

        protected override void OnUnbind()
        {
            if (_incrementButton != null && _clickHandler != null)
                _incrementButton.onClick.RemoveListener(_clickHandler);
        }

        public void UpdateCounter(int value)
        {
            if (_counterText != null)
                _counterText.text = $"Counter: {value}";
        }
    }
}
