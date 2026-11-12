using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Capture.Editor
{
    /// <summary>
    /// Nexus window plugin providing live controls, settings, and status for Capture Mode.
    /// </summary>
    public sealed class CaptureModePlugin : NexusEditorPlugin
    {
        public override string Id => "Capture";
        public override string DisplayName => NexusLang.Get("capture_tab_title");
        public override int Order => 26;
        public override string Category => "cat_live";
        public override Color IconColor => NexusEditorStyles.AccentOrange;

        private VisualElement _view;
        private ScrollView _content;
        private Label _statusBar;
        private Label _speedLabel;
        private bool _subscribed;

        [MenuItem("Nexus/Capture Mode", priority = 45)]
        private static void OpenTab() => NexusWindow.Open("Capture");

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };
            _view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("capture_toolbar")));

            _content = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 8 } };
            _view.Add(_content);

            _statusBar = NexusEditorStyles.CreateStatusBar();
            _view.Add(_statusBar);

            Subscribe();
            Render();
            return _view;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (_speedLabel != null && Application.isPlaying)
            {
                _speedLabel.text = string.Format(NexusLang.Get("capture_speed"), CaptureModeService.Speed);
            }
        }

        public override void OnDisable()
        {
            Unsubscribe();
            _speedLabel = null;
            base.OnDisable();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            CaptureModeService.StateChanged += RebuildView;
            CaptureModeService.OpenChanged += OnOpenStateChanged;
            CaptureFreeCamera.ActiveChanged += RebuildView;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            CaptureModeService.StateChanged -= RebuildView;
            CaptureModeService.OpenChanged -= OnOpenStateChanged;
            CaptureFreeCamera.ActiveChanged -= RebuildView;
            _subscribed = false;
        }

        private void OnOpenStateChanged(bool open) => RebuildView();

        private void RebuildView()
        {
            if (_content == null) return;
            Render();
        }

        private void Render()
        {
            _content.Clear();
            _speedLabel = null;

            if (!Application.isPlaying)
            {
                RenderEditMode();
            }
            else
            {
                RenderPlayMode();
            }
        }

        private void RenderEditMode()
        {
            var settings = CaptureSettings.Load();
            bool isAssetOnDisk = !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(settings));

            // Hint card explaining Play Mode requirement
            var hintCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            hintCard.style.marginBottom = 8;
            hintCard.style.paddingLeft = 10;
            hintCard.style.paddingRight = 10;
            hintCard.style.paddingTop = 8;
            hintCard.style.paddingBottom = 8;
            hintCard.Add(NexusEditorStyles.CreateTitle(NexusLang.Get("capture_tab_title"), NexusEditorStyles.AccentOrange));
            hintCard.Add(NexusEditorStyles.CreateHint(NexusLang.Get("capture_playmode_hint")));
            _content.Add(hintCard);

            // Settings Asset Management Card
            var assetCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            assetCard.style.marginBottom = 8;
            assetCard.style.paddingLeft = 10;
            assetCard.style.paddingRight = 10;
            assetCard.style.paddingTop = 8;
            assetCard.style.paddingBottom = 8;

            if (isAssetOnDisk)
            {
                assetCard.Add(NexusEditorStyles.CreateButton(NexusLang.Get("capture_select_settings"), () =>
                {
                    Selection.activeObject = settings;
                    EditorGUIUtility.PingObject(settings);
                }, NexusEditorStyles.BtnBlue));
            }
            else
            {
                assetCard.Add(NexusEditorStyles.CreateButton(NexusLang.Get("capture_create_settings"), () =>
                {
                    CreateSettingsAsset();
                    Render();
                }, NexusEditorStyles.BtnBlue));
            }
            _content.Add(assetCard);

            // Shortcuts Card
            var shortcutsCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            shortcutsCard.style.marginBottom = 8;
            shortcutsCard.style.paddingLeft = 10;
            shortcutsCard.style.paddingRight = 10;
            shortcutsCard.style.paddingTop = 8;
            shortcutsCard.style.paddingBottom = 8;
            shortcutsCard.Add(NexusEditorStyles.CreateTitle(NexusLang.Get("capture_shortcuts_title"), NexusEditorStyles.AccentBlue));

            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_toggle"), settings.ToggleKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_record"), settings.RecordKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_hide_ui"), settings.HideUiKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_slower"), settings.SlowerKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_faster"), settings.FasterKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_pause"), settings.PauseKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_step"), settings.StepKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_free_cam"), settings.FreeCameraKey.ToString());
            AddShortcutRow(shortcutsCard, NexusLang.Get("capture_shortcut_god_mode"), settings.GodModeKey.ToString());
            _content.Add(shortcutsCard);

            if (_statusBar != null)
            {
                _statusBar.text = NexusLang.Get("status_edit_mode");
            }
        }

        private void RenderPlayMode()
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            card.style.marginBottom = 8;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;

            // Open/Close button
            var openBtn = NexusEditorStyles.CreateButton(
                CaptureModeService.IsOpen ? NexusLang.Get("capture_close") : NexusLang.Get("capture_open"),
                () => CaptureModeService.Toggle(),
                CaptureModeService.IsOpen ? NexusEditorStyles.BtnRed : NexusEditorStyles.BtnBlue);
            openBtn.style.marginBottom = 8;
            card.Add(openBtn);

            if (!CaptureModeService.IsOpen)
            {
                _content.Add(card);
                if (_statusBar != null) _statusBar.text = NexusLang.Get("capture_status_closed");
                return;
            }

            // Record/Stop row with preset dropdown
            var recordRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            var recordBtn = NexusEditorStyles.CreateButton(
                CaptureModeService.IsRecording ? NexusLang.Get("capture_stop_record") : NexusLang.Get("capture_record"),
                () => CaptureModeService.RequestRecordToggle(),
                CaptureModeService.IsRecording ? NexusEditorStyles.BtnRed : NexusEditorStyles.BtnGreen);
            recordBtn.style.marginRight = 8;
            recordBtn.style.minWidth = 120;
            recordRow.Add(recordBtn);

            var presets = CaptureSettings.Load()?.ResolutionPresets;
            var presetNames = new List<string>();
            if (presets != null && presets.Count > 0)
            {
                for (int i = 0; i < presets.Count; i++)
                {
                    presetNames.Add($"{presets[i].name} ({presets[i].width}x{presets[i].height})");
                }
            }
            else
            {
                presetNames.Add(NexusLang.Get("capture_resolution_default"));
            }

            int selectedIndex = Math.Max(0, Math.Min(CaptureRecorder.SelectedPreset, presetNames.Count - 1));
            var presetDropdown = new DropdownField(NexusLang.Get("capture_resolution"), presetNames, selectedIndex);
            presetDropdown.style.flexGrow = 1;
            presetDropdown.SetEnabled(!CaptureModeService.IsRecording);
            presetDropdown.RegisterValueChangedCallback(evt =>
            {
                CaptureRecorder.SelectedPreset = presetDropdown.index;
            });
            recordRow.Add(presetDropdown);
            card.Add(recordRow);

            // Speed controls row
            var speedRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            var slowerBtn = NexusEditorStyles.CreateButton(NexusLang.Get("capture_slower"), () => CaptureModeService.Slower(), NexusEditorStyles.BtnGray);
            speedRow.Add(slowerBtn);

            _speedLabel = NexusEditorStyles.CreateBody(string.Format(NexusLang.Get("capture_speed"), CaptureModeService.Speed));
            _speedLabel.style.marginLeft = 10;
            _speedLabel.style.marginRight = 10;
            _speedLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            speedRow.Add(_speedLabel);

            var fasterBtn = NexusEditorStyles.CreateButton(NexusLang.Get("capture_faster"), () => CaptureModeService.Faster(), NexusEditorStyles.BtnGray);
            speedRow.Add(fasterBtn);
            card.Add(speedRow);

            // Pause and Step row
            var pauseStepRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            var pauseBtn = NexusEditorStyles.CreateButton(
                CaptureModeService.Paused ? NexusLang.Get("capture_resume") : NexusLang.Get("capture_pause"),
                () => CaptureModeService.TogglePause(),
                NexusEditorStyles.BtnGray);
            pauseBtn.style.marginRight = 8;
            pauseStepRow.Add(pauseBtn);

            var stepBtn = NexusEditorStyles.CreateButton(
                NexusLang.Get("capture_step"),
                () => CaptureModeService.StepFrame(),
                NexusEditorStyles.BtnGray);
            pauseStepRow.Add(stepBtn);
            card.Add(pauseStepRow);

            // Hide UI toggle
            var hideUiToggle = new Toggle(NexusLang.Get("capture_hide_ui")) { value = CaptureModeService.UiHidden };
            hideUiToggle.style.marginBottom = 6;
            hideUiToggle.RegisterValueChangedCallback(evt =>
            {
                if (CaptureModeService.UiHidden != evt.newValue)
                {
                    CaptureModeService.ToggleUi();
                }
            });
            card.Add(hideUiToggle);

            // Free camera toggle
            var freeCamToggle = new Toggle(NexusLang.Get("capture_free_camera")) { value = CaptureFreeCamera.Active };
            freeCamToggle.style.marginBottom = 8;
            freeCamToggle.RegisterValueChangedCallback(evt =>
            {
                if (CaptureFreeCamera.Active != evt.newValue)
                {
                    CaptureFreeCamera.Toggle();
                }
            });
            card.Add(freeCamToggle);

            // Hooks section
            if (CaptureModeService.HasHooks)
            {
                var hooks = CaptureModeService.Hooks;

                var godModeToggle = new Toggle(NexusLang.Get("capture_god_mode")) { value = CaptureModeService.GodMode };
                godModeToggle.style.marginBottom = 6;
                godModeToggle.RegisterValueChangedCallback(evt =>
                {
                    if (CaptureModeService.GodMode != evt.newValue)
                    {
                        CaptureModeService.ToggleGodMode();
                    }
                });
                card.Add(godModeToggle);

                if (hooks.LevelCount > 0)
                {
                    var levelChoices = new List<string>(hooks.LevelCount);
                    for (int i = 0; i < hooks.LevelCount; i++)
                    {
                        levelChoices.Add(string.Format(NexusLang.Get("capture_level_item"), i + 1));
                    }
                    var levelDropdown = new DropdownField(NexusLang.Get("capture_level"), levelChoices, 0);
                    levelDropdown.style.marginBottom = 8;
                    levelDropdown.RegisterValueChangedCallback(evt =>
                    {
                        CaptureModeService.JumpToLevel(levelDropdown.index);
                    });
                    card.Add(levelDropdown);
                }

                if (hooks.Presets != null && hooks.Presets.Count > 0)
                {
                    card.Add(NexusEditorStyles.CreateTitle(NexusLang.Get("capture_presets"), NexusEditorStyles.AccentPurple, 10));
                    var presetsWrap = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 4 } };
                    for (int i = 0; i < hooks.Presets.Count; i++)
                    {
                        int index = i;
                        var btn = NexusEditorStyles.CreateButton(hooks.Presets[i], () => CaptureModeService.ApplyPreset(index), NexusEditorStyles.BtnGray);
                        btn.style.marginRight = 4;
                        btn.style.marginBottom = 4;
                        presetsWrap.Add(btn);
                    }
                    card.Add(presetsWrap);
                }
            }
            else
            {
                card.Add(NexusEditorStyles.CreateHint(NexusLang.Get("capture_no_hooks_hint")));
            }

            _content.Add(card);

            if (_statusBar != null)
            {
                _statusBar.text = string.Format(NexusLang.Get("capture_status_open"), CaptureModeService.Speed);
            }
        }

        private static void AddShortcutRow(VisualElement parent, string action, string key)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween, marginTop = 3, marginBottom = 3 } };
            row.Add(NexusEditorStyles.CreateBody(action));
            row.Add(NexusEditorStyles.CreatePill(key, NexusEditorStyles.CardBgBlue, NexusEditorStyles.AccentBlueText));
            parent.Add(row);
        }

        private static void CreateSettingsAsset()
        {
            if (!Directory.Exists("Assets/Settings"))
            {
                Directory.CreateDirectory("Assets/Settings");
            }

            var asset = ScriptableObject.CreateInstance<CaptureSettings>();
            AssetDatabase.CreateAsset(asset, "Assets/Settings/CaptureSettings.asset");
            AssetDatabase.SaveAssets();
            CaptureSettings.ClearCache();
            Selection.activeObject = CaptureSettings.Load();
        }
    }
}
