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
        private ScrollView _content;
        private Label _statusLabel;
        private Slider _timeScaleSlider;
        private TextField _currencyNameField;
        private LongField _currencyAmountField;
        private IntegerField _levelField;
        private TextField _windowNameField;
        private VisualElement _openWindowsList;
        private IVisualElementScheduledItem _refreshSchedule;

        public override VisualElement CreateView()
        {
            _container = new VisualElement();
            _container.style.flexGrow = 1;
            _container.style.paddingLeft = 10;
            _container.style.paddingRight = 10;
            _container.style.paddingTop = 10;

            var title = new Label(NexusLang.Get("cs_title"));
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 10;
            _container.Add(title);

            if (!Application.isPlaying)
            {
                _statusLabel = new Label(NexusLang.Get("cs_editmode_prompt"));
                _statusLabel.style.color = new Color(0.9f, 0.6f, 0.2f);
                _statusLabel.style.whiteSpace = WhiteSpace.Normal;
                _container.Add(_statusLabel);
                return _container;
            }

            // Scrollable content host so sections are never clipped in a short window.
            _content = new ScrollView { style = { flexGrow = 1 } };
            _container.Add(_content);

            // TimeScale Section
            var timeSection = CreateSectionBox(NexusLang.Get("cs_sec_timescale"));
            _timeScaleSlider = new Slider(NexusLang.Get("cs_time_scale"), 0f, 5f) { value = Time.timeScale };
            _timeScaleSlider.RegisterValueChangedCallback(evt => Time.timeScale = evt.newValue);
            timeSection.Add(_timeScaleSlider);
            var pauseBtn = new Button(() => Time.timeScale = Time.timeScale == 0f ? 1f : 0f) { text = NexusLang.Get("cs_toggle_pause") };
            timeSection.Add(pauseBtn);
            _content.Add(timeSection);

            // Economy Section
            var ecoSection = CreateSectionBox(NexusLang.Get("cs_sec_economy"));
            _currencyNameField = new TextField(NexusLang.Get("cs_currency_id")) { value = NexusLang.Get("cs_default_currency") };
            _currencyAmountField = new LongField(NexusLang.Get("cs_amount")) { value = 100 };
            ecoSection.Add(_currencyNameField);
            ecoSection.Add(_currencyAmountField);
            var addBtn = new Button(OnAddCurrency) { text = NexusLang.Get("cs_earn_currency") };
            var spendBtn = new Button(OnSpendCurrency) { text = NexusLang.Get("cs_spend_currency") };
            ecoSection.Add(addBtn);
            ecoSection.Add(spendBtn);

            var root = FindActiveRoot();
            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IPlayerPrefsService)))
            {
                var prefs = root.Context.Resolve<IPlayerPrefsService>();
                string storageType = prefs.GetType().Name;
                string saveInfo = string.Format(NexusLang.Get("cs_active_storage"), storageType);
                if (prefs is EncryptedStorageService secureStorage)
                {
                    saveInfo += string.Format(NexusLang.Get("cs_autosave"), secureStorage.AutoSave);
                }
                var storageLabel = new Label(saveInfo);
                storageLabel.style.fontSize = 10;
                storageLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
                storageLabel.style.marginBottom = 4;
                storageLabel.style.marginTop = 4;
                ecoSection.Add(storageLabel);

                var flushBtn = new Button(() =>
                {
                    prefs.Save();
                    Debug.Log("[Nexus Editor] Storage changes flushed to disk.");
                }) { text = NexusLang.Get("cs_save_flush") };
                ecoSection.Add(flushBtn);
            }
            _content.Add(ecoSection);

            // Progression Section
            var progSection = CreateSectionBox(NexusLang.Get("cs_sec_progression"));
            _levelField = new IntegerField(NexusLang.Get("cs_jump_to_level")) { value = 1 };
            progSection.Add(_levelField);
            var setLevelBtn = new Button(OnSetLevel) { text = NexusLang.Get("cs_set_level") };
            progSection.Add(setLevelBtn);
            _content.Add(progSection);

            // UI Window Stack Section
            var uiSection = CreateSectionBox(NexusLang.Get("cs_sec_ui"));
            _windowNameField = new TextField(NexusLang.Get("cs_window_name")) { value = "ShopScreen" };
            uiSection.Add(_windowNameField);
            var openWinBtn = new Button(OnOpenWindow) { text = NexusLang.Get("cs_open_window") };
            var closeTopBtn = new Button(OnCloseTopWindow) { text = NexusLang.Get("cs_close_top") };
            uiSection.Add(openWinBtn);
            uiSection.Add(closeTopBtn);

            if (root?.Context != null && root.Context.Container.IsRegistered(typeof(IWindowManager)))
            {
                var winMgr = root.Context.Resolve<IWindowManager>();
                if (winMgr is WindowManager concreteWinMgr && concreteWinMgr.AssetProvider != null)
                {
                    var providerLabel = new Label(string.Format(NexusLang.Get("cs_asset_provider"), concreteWinMgr.AssetProvider.GetType().Name));
                    providerLabel.style.fontSize = 10;
                    providerLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
                    providerLabel.style.marginTop = 4;
                    uiSection.Add(providerLabel);
                }
            }

            // Live open-window stack (G-3): refreshed on a 500 ms schedule.
            var winStackTitle = new Label(NexusLang.Get("cs_open_stack"));
            winStackTitle.style.fontSize = 11;
            winStackTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            winStackTitle.style.marginTop = 6;
            uiSection.Add(winStackTitle);
            _openWindowsList = new VisualElement();
            uiSection.Add(_openWindowsList);
            RefreshOpenWindows();

            _content.Add(uiSection);

            // Haptics & Feedback Section
            var feedbackSection = CreateSectionBox(NexusLang.Get("cs_sec_haptics"));
            var lightHapticBtn = new Button(() => OnTestHaptic(HapticType.Light)) { text = NexusLang.Get("cs_light_haptic") };
            var heavyHapticBtn = new Button(() => OnTestHaptic(HapticType.Heavy)) { text = NexusLang.Get("cs_heavy_haptic") };
            var successFeedbackBtn = new Button(OnTestSuccessFeedback) { text = NexusLang.Get("cs_success_feedback") };
            feedbackSection.Add(lightHapticBtn);
            feedbackSection.Add(heavyHapticBtn);
            feedbackSection.Add(successFeedbackBtn);
            _content.Add(feedbackSection);

            _refreshSchedule = _container.schedule.Execute(RefreshOpenWindows).Every(500);

            return _container;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            base.OnDisable();
        }

        private void RefreshOpenWindows()
        {
            if (_openWindowsList == null || !Application.isPlaying) return;
            _openWindowsList.Clear();

            var root = FindActiveRoot();
            if (root?.Context == null || !root.Context.Container.IsRegistered(typeof(IWindowManager)))
            {
                _openWindowsList.Add(MakeDimLabel(NexusLang.Get("cs_no_windowmanager")));
                return;
            }
            if (root.Context.Resolve<IWindowManager>() is not WindowManager winMgr)
            {
                _openWindowsList.Add(MakeDimLabel(NexusLang.Get("cs_custom_windowmanager")));
                return;
            }

            var windows = winMgr.GetOpenWindowsSnapshot();
            var header = new Label(string.Format(NexusLang.Get("cs_stack_header"), windows.Count, winMgr.PendingWindowCount));
            header.style.fontSize = 10;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.color = new StyleColor(new Color(0.7f, 0.9f, 1f));
            _openWindowsList.Add(header);

            if (windows.Count == 0)
            {
                _openWindowsList.Add(MakeDimLabel(NexusLang.Get("cs_stack_empty")));
                return;
            }

            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                var row = new Label($"  {i + 1}. {w.Name}   [{w.Layer}]{(w.IsAlive ? "" : NexusLang.Get("cs_destroyed_suffix"))}");
                row.style.fontSize = 10;
                row.style.color = new StyleColor(w.IsAlive ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.8f, 0.4f, 0.4f));
                _openWindowsList.Add(row);
            }
        }

        private static Label MakeDimLabel(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 10;
            l.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            return l;
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
