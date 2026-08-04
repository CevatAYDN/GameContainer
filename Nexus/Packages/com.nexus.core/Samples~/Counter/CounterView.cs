using System;
using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Samples.Counter
{
    [Mediator(typeof(CounterMediator))]
    public class CounterView : View
    {
        public event Action OnIncrementClicked;

        private UnityEngine.Events.UnityAction _clickHandler;

        [SerializeField] private Button incrementButton;
        [SerializeField] private Text countText;

        protected override void OnBind(IContext context)
        {
            if (incrementButton == null)
                return;

            _clickHandler ??= () => OnIncrementClicked?.Invoke();
            incrementButton.onClick.AddListener(_clickHandler);
        }

        protected override void OnUnbind()
        {
            if (incrementButton != null && _clickHandler != null)
                incrementButton.onClick.RemoveListener(_clickHandler);
        }

        public void UpdateDisplay(int count)
        {
            if (countText != null)
                countText.text = $"Count: {count}";
        }
    }
}
