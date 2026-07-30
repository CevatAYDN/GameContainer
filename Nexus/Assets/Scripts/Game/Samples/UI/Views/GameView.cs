using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game
{
    [Mediator(typeof(GameMediator))]
    public class GameView : View
    {
        public event System.Action OnIncrementClicked;

        [SerializeField] private Button _button;
        [SerializeField] private Text _counterText;

        protected override void OnBind(IContext context)
        {
            if (_button != null)
                _button.onClick.AddListener(() => OnIncrementClicked?.Invoke());
        }

        protected override void OnUnbind()
        {
            if (_button != null)
                _button.onClick.RemoveAllListeners();
        }

        public void UpdateDisplay(int value)
        {
            if (_counterText != null)
                _counterText.text = "Counter: " + value.ToString();
        }
    }
}