using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Editor.Plugins
{
    public class CasualServicesPlugin : NexusEditorPlugin
    {
        public override string Id => "casual_services";
        public override string DisplayName => "Casual Debugger";
        public override int Order => 25;

        private VisualElement _container;
        private Label _statusLabel;
        private Slider _timeScaleSlider;
        private TextField _currencyNameField;
        private LongField _currencyAmountField;
        private IntegerField _levelField;

        public override VisualElement CreateView()
        {
            _container = new VisualElement();
            _container.style.paddingLeft = 10;
            _container.style.paddingRight = 10;
            _container.style.paddingTop = 10;

            var title = new Label("Nexus Casual Services Debugger");
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 10;
            _container.Add(title);

            if (!Application.isPlaying)
            {
                _statusLabel = new Label("Enter Play Mode to debug Economy, Progression, and TimeScale live.");
                _statusLabel.style.color = new Color(0.9f, 0.6f, 0.2f);
                _container.Add(_statusLabel);
                return _container;
            }

            // TimeScale Section
            var timeSection = new Box();
            timeSection.style.paddingLeft = 10;
            timeSection.style.paddingRight = 10;
            timeSection.style.paddingTop = 10;
            timeSection.style.paddingBottom = 10;
            timeSection.style.marginBottom = 10;

            var timeTitle = new Label("TimeScale & Loop Controls");
            timeTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            timeSection.Add(timeTitle);

            _timeScaleSlider = new Slider("Time Scale", 0f, 5f) { value = Time.timeScale };
            _timeScaleSlider.RegisterValueChangedCallback(evt => Time.timeScale = evt.newValue);
            timeSection.Add(_timeScaleSlider);

            var pauseBtn = new Button(() => Time.timeScale = Time.timeScale == 0f ? 1f : 0f) { text = "Toggle Pause" };
            timeSection.Add(pauseBtn);
            _container.Add(timeSection);

            // Economy Section
            var ecoSection = new Box();
            ecoSection.style.paddingLeft = 10;
            ecoSection.style.paddingRight = 10;
            ecoSection.style.paddingTop = 10;
            ecoSection.style.paddingBottom = 10;
            ecoSection.style.marginBottom = 10;

            var ecoTitle = new Label("Economy Debugger");
            ecoTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            ecoSection.Add(ecoTitle);

            _currencyNameField = new TextField("Currency ID") { value = "Coins" };
            _currencyAmountField = new LongField("Amount") { value = 100 };
            ecoSection.Add(_currencyNameField);
            ecoSection.Add(_currencyAmountField);

            var addBtn = new Button(OnAddCurrency) { text = "Earn Currency" };
            var spendBtn = new Button(OnSpendCurrency) { text = "Spend Currency" };
            ecoSection.Add(addBtn);
            ecoSection.Add(spendBtn);
            _container.Add(ecoSection);

            // Progression Section
            var progSection = new Box();
            progSection.style.paddingLeft = 10;
            progSection.style.paddingRight = 10;
            progSection.style.paddingTop = 10;
            progSection.style.paddingBottom = 10;

            var progTitle = new Label("Progression Debugger");
            progTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            progSection.Add(progTitle);

            _levelField = new IntegerField("Jump To Level") { value = 1 };
            progSection.Add(_levelField);

            var setLevelBtn = new Button(OnSetLevel) { text = "Set Level" };
            progSection.Add(setLevelBtn);
            _container.Add(progSection);

            return _container;
        }

        private Root FindActiveRoot()
        {
            var roots = Object.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            return roots.Length > 0 ? roots[0] : null;
        }

        private void OnAddCurrency()
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IEconomyService)))
            {
                var eco = root.Context.Resolve<IEconomyService>();
                eco?.Earn(_currencyNameField.value, _currencyAmountField.value);
            }
        }

        private void OnSpendCurrency()
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IEconomyService)))
            {
                var eco = root.Context.Resolve<IEconomyService>();
                eco?.Spend(_currencyNameField.value, _currencyAmountField.value);
            }
        }

        private void OnSetLevel()
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IProgressionService)))
            {
                var prog = root.Context.Resolve<IProgressionService>();
                prog?.SetLevel(_levelField.value);
            }
        }
    }
}
