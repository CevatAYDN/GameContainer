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
        public override string DisplayName => NexusLang.Get("action_casual_services_title");
        public override int Order => 25;

        private VisualElement _container;
        private Label _statusLabel;
        private Slider _timeScaleSlider;
        private TextField _currencyNameField;
        private LongField _currencyAmountField;
        private IntegerField _levelField;
        private TextField _windowNameField;

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
                _statusLabel = new Label("Enter Play Mode to debug Economy, Progression, UI, Audio, Haptics, and TimeScale live.");
                _statusLabel.style.color = new Color(0.9f, 0.6f, 0.2f);
                _container.Add(_statusLabel);
                return _container;
            }

            // TimeScale Section
            var timeSection = CreateSectionBox("TimeScale & Loop Controls");
            _timeScaleSlider = new Slider("Time Scale", 0f, 5f) { value = Time.timeScale };
            _timeScaleSlider.RegisterValueChangedCallback(evt => Time.timeScale = evt.newValue);
            timeSection.Add(_timeScaleSlider);
            var pauseBtn = new Button(() => Time.timeScale = Time.timeScale == 0f ? 1f : 0f) { text = "Toggle Pause" };
            timeSection.Add(pauseBtn);
            _container.Add(timeSection);

            // Economy Section
            var ecoSection = CreateSectionBox("Economy Debugger");
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
            var progSection = CreateSectionBox("Progression Debugger");
            _levelField = new IntegerField("Jump To Level") { value = 1 };
            progSection.Add(_levelField);
            var setLevelBtn = new Button(OnSetLevel) { text = "Set Level" };
            progSection.Add(setLevelBtn);
            _container.Add(progSection);

            // UI Window Stack Section
            var uiSection = CreateSectionBox("UI Window Navigation");
            _windowNameField = new TextField("Window Name") { value = "ShopScreen" };
            uiSection.Add(_windowNameField);
            var openWinBtn = new Button(OnOpenWindow) { text = "Open Window" };
            var closeTopBtn = new Button(OnCloseTopWindow) { text = "Close Top Window" };
            uiSection.Add(openWinBtn);
            uiSection.Add(closeTopBtn);
            _container.Add(uiSection);

            // Haptics & Feedback Section
            var feedbackSection = CreateSectionBox("Haptics & Feedback Tester");
            var lightHapticBtn = new Button(() => OnTestHaptic(HapticType.Light)) { text = "Trigger Light Haptic" };
            var heavyHapticBtn = new Button(() => OnTestHaptic(HapticType.Heavy)) { text = "Trigger Heavy Haptic" };
            var successFeedbackBtn = new Button(OnTestSuccessFeedback) { text = "Play Success Feedback" };
            feedbackSection.Add(lightHapticBtn);
            feedbackSection.Add(heavyHapticBtn);
            feedbackSection.Add(successFeedbackBtn);
            _container.Add(feedbackSection);

            return _container;
        }

        private static Box CreateSectionBox(string titleText)
        {
            var box = new Box();
            box.style.paddingLeft = 10;
            box.style.paddingRight = 10;
            box.style.paddingTop = 10;
            box.style.paddingBottom = 10;
            box.style.marginBottom = 10;

            var title = new Label(titleText);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            box.Add(title);
            return box;
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

        private void OnOpenWindow()
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IWindowManager)))
            {
                var winMgr = root.Context.Resolve<IWindowManager>();
                winMgr?.OpenWindow(_windowNameField.value);
            }
        }

        private void OnCloseTopWindow()
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IWindowManager)))
            {
                var winMgr = root.Context.Resolve<IWindowManager>();
                winMgr?.CloseTopWindow();
            }
        }

        private void OnTestHaptic(HapticType type)
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IHapticService)))
            {
                var haptic = root.Context.Resolve<IHapticService>();
                haptic?.Vibrate(type);
            }
        }

        private void OnTestSuccessFeedback()
        {
            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IFeedbackService)))
            {
                var feedback = root.Context.Resolve<IFeedbackService>();
                feedback?.Play(FeedbackPreset.SuccessFanfare);
            }
        }
    }
}
