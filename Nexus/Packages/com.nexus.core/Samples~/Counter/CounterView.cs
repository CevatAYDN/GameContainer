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

        [SerializeField] private Button incrementButton;
        [SerializeField] private Text countText;

        protected override void OnBind(IContext context)
        {
            if (incrementButton != null)
                incrementButton.onClick.AddListener(() => OnIncrementClicked?.Invoke());
        }

        protected override void OnUnbind()
        {
            if (incrementButton != null)
                incrementButton.onClick.RemoveAllListeners();
        }

        public void UpdateDisplay(int count)
        {
            if (countText != null)
                countText.text = $"Count: {count}";
        }
    }
}
