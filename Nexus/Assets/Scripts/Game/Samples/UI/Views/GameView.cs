using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game
{
    [Mediator(typeof(GameMediator))]
    public class GameView : View
    {
        public event System.Action OnIncrementClicked;

        private UnityEngine.Events.UnityAction _clickHandler;

        [SerializeField] private Button _button;
        [SerializeField] private Text _counterText;

        protected override void OnBind(IContext context)
        {
            if (_button == null)
                return;

            _clickHandler ??= () => OnIncrementClicked?.Invoke();
            _button.onClick.AddListener(_clickHandler);
        }

        protected override void OnUnbind()
        {
            if (_button != null && _clickHandler != null)
                _button.onClick.RemoveListener(_clickHandler);
        }

        public void UpdateDisplay(int value)
        {
            if (_counterText != null)
                _counterText.text = "Counter: " + value.ToString();
        }
    }
}
