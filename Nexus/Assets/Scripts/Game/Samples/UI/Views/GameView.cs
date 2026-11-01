using Nexus.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    [Mediator(typeof(GameMediator))]
    [RequireComponent(typeof(UIDocument))]
    public class GameView : View
    {
        public event System.Action OnIncrementClicked;

        private VisualElement _panel;
        private Label _counterText;

        protected override void OnBind(IContext context)
        {
            // UIDocument builds a new root each time it is enabled, before this view binds (its execution order is -100),
            // so the elements are made at each bind and taken out at each unbind.
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0;
            _panel.style.top = 0;
            _panel.style.right = 0;
            _panel.style.bottom = 0;
            _panel.style.alignItems = Align.Center;
            _panel.style.justifyContent = Justify.Center;

            _counterText = new Label("Counter: 0");
            _counterText.style.fontSize = 32;
            _counterText.style.color = Color.white;
            _counterText.style.marginBottom = 20;
            _panel.Add(_counterText);

            var button = new Button(OnClicked) { text = "Increment" };
            button.style.width = 200;
            button.style.height = 60;
            button.style.fontSize = 24;
            _panel.Add(button);

            GetComponent<UIDocument>().rootVisualElement.Add(_panel);
        }

        protected override void OnUnbind()
        {
            _panel?.RemoveFromHierarchy();
            _panel = null;
            _counterText = null;
        }

        private void OnClicked() => OnIncrementClicked?.Invoke();

        public void UpdateDisplay(int value)
        {
            if (_counterText != null)
                _counterText.text = "Counter: " + value.ToString();
        }
    }
}