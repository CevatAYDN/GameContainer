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
        private UnityEngine.Events.UnityAction _buttonClickHandler;

        protected override void OnBind(IContext context)
        {
            if (_button != null)
            {
                _buttonClickHandler = () => OnIncrementClicked?.Invoke();
                _button.onClick.AddListener(_buttonClickHandler);
            }
        }

        protected override void OnUnbind()
        {
            if (_button != null && _buttonClickHandler != null)
            {
                _button.onClick.RemoveListener(_buttonClickHandler);
                _buttonClickHandler = null;
            }
        }

        public void UpdateDisplay(int value)
        {
            if (_counterText != null)
                _counterText.text = "Counter: " + value.ToString();
        }
    }
}