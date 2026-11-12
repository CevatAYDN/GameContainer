using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Nexus.Capture.Tests
{
    /// <summary>
    /// PlayMode tests locking the core behaviors of CaptureModeService.
    /// </summary>
    public sealed class CaptureModeServiceTests
    {
        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();

        [TearDown]
        public void Teardown()
        {
            CaptureModeService.Close();
            CaptureModeService.RegisterHooks(null);

            for (int i = 0; i < _createdObjects.Count; i++)
            {
                if (_createdObjects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
                }
            }
            _createdObjects.Clear();
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            if (obj != null)
            {
                _createdObjects.Add(obj);
            }
            return obj;
        }

        [Test]
        public void WhileClosed_SpeedPauseUiAndRecording_DoNothing()
        {
            float initialTimeScale = Time.timeScale;
            var canvas = Track(new GameObject("CanvasWhileClosed")).AddComponent<Canvas>();

            CaptureModeService.Slower();
            CaptureModeService.TogglePause();
            CaptureModeService.HideUi();
            CaptureModeService.RequestRecordToggle();

            Assert.That(Time.timeScale, Is.EqualTo(initialTimeScale), "Nothing restores a change made while Capture Mode is closed.");
            Assert.That(CaptureModeService.Paused, Is.False);
            Assert.That(CaptureModeService.UiHidden, Is.False);
            Assert.That(canvas.enabled, Is.True);
            Assert.That(CaptureModeService.IsRecording, Is.False);
        }

        [Test]
        public void Close_RestoresTimeScaleAndFixedDeltaTime_AfterSlowMotionAndPause()
        {
            float initialTimeScale = Time.timeScale;
            float initialFixedDeltaTime = Time.fixedDeltaTime;

            CaptureModeService.Open();
            CaptureModeService.Slower();

            Assert.That(Time.timeScale, Is.Not.EqualTo(initialTimeScale));
            Assert.That(Time.fixedDeltaTime, Is.Not.EqualTo(initialFixedDeltaTime));

            CaptureModeService.TogglePause();
            Assert.That(CaptureModeService.Paused, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(0f));

            CaptureModeService.Close();

            Assert.That(CaptureModeService.IsOpen, Is.False);
            Assert.That(CaptureModeService.Paused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(initialTimeScale));
            // Unity's fixedDeltaTime setter does not round-trip a float exactly.
            Assert.That(Time.fixedDeltaTime, Is.EqualTo(initialFixedDeltaTime).Within(1e-6f));
        }

        [UnityTest]
        public IEnumerator HideUi_HidesCanvasUiDocumentAndCaptureHide_ButKeepsCaptureKeepVisible()
        {
            var goCanvas = Track(new GameObject("CanvasNormal"));
            var canvas = goCanvas.AddComponent<Canvas>();
            canvas.enabled = true;

            var goDoc = Track(new GameObject("UIDocumentNormal"));
            var uiDoc = goDoc.AddComponent<UIDocument>();
            var panelSettings = Track(ScriptableObject.CreateInstance<PanelSettings>());
            panelSettings.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(CaptureOverlay.ThemePath); // no "No Theme Style Sheet" warning
            uiDoc.panelSettings = panelSettings;

            var goHide = Track(new GameObject("CaptureHideTarget"));
            goHide.AddComponent<CaptureHide>();
            goHide.SetActive(true);

            var goKeep = Track(new GameObject("CanvasKeepVisible"));
            var canvasKeep = goKeep.AddComponent<Canvas>();
            goKeep.AddComponent<CaptureKeepVisible>();
            canvasKeep.enabled = true;

            yield return null;

            CaptureModeService.Open();
            CaptureModeService.HideUi();

            Assert.That(canvas.enabled, Is.False, "Normal canvas must be disabled.");
            Assert.That(uiDoc.rootVisualElement, Is.Not.Null, "The UIDocument must have built its root by now.");
            Assert.That(uiDoc.rootVisualElement.style.display.value, Is.EqualTo(DisplayStyle.None), "UIDocument root must be set to DisplayStyle.None.");
            Assert.That(goHide.activeSelf, Is.False, "GameObject with CaptureHide must be deactivated.");
            Assert.That(canvasKeep.enabled, Is.True, "Canvas on GameObject with CaptureKeepVisible must stay enabled.");
        }

        [Test]
        public void ShowUi_RestoresOnlyWhatCaptureHid()
        {
            var goA = Track(new GameObject("CanvasEnabled"));
            var canvasA = goA.AddComponent<Canvas>();
            canvasA.enabled = true;

            var goB = Track(new GameObject("CanvasPreDisabled"));
            var canvasB = goB.AddComponent<Canvas>();
            canvasB.enabled = false;

            CaptureModeService.Open();
            CaptureModeService.HideUi();

            Assert.That(canvasA.enabled, Is.False);
            Assert.That(canvasB.enabled, Is.False);

            CaptureModeService.ShowUi();

            Assert.That(canvasA.enabled, Is.True, "Canvas that was enabled before capture must be restored to enabled.");
            Assert.That(canvasB.enabled, Is.False, "Canvas disabled by the game itself must remain disabled after restore.");
        }

        [Test]
        public void WithoutHooks_HookFeaturesDoNothingAndLogNoError()
        {
            CaptureModeService.RegisterHooks(null);
            Assert.That(CaptureModeService.HasHooks, Is.False);

            CaptureModeService.Open();
            CaptureModeService.ToggleGodMode();
            CaptureModeService.JumpToLevel(1);
            CaptureModeService.ApplyPreset(0);

            Assert.That(CaptureModeService.GodMode, Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void GodMode_IsTurnedOffByClose_WhenCaptureTurnedItOn()
        {
            var hooks = new FakeHooks();
            CaptureModeService.RegisterHooks(hooks);
            CaptureModeService.Open();

            CaptureModeService.ToggleGodMode();

            Assert.That(CaptureModeService.GodMode, Is.True);
            Assert.That(hooks.GodModeEnabled, Is.True);

            CaptureModeService.Close();

            Assert.That(CaptureModeService.GodMode, Is.False);
            Assert.That(hooks.GodModeEnabled, Is.False);
        }

        [Test]
        public void Overlay_IsHiddenWhileRecording_AndShownAfter()
        {
            CaptureModeService.Open();

            UIDocument overlayDoc = null;
            foreach (var item in CaptureModeService.Own)
            {
                if (item is UIDocument doc)
                {
                    overlayDoc = doc;
                    break;
                }
            }

            Assert.That(overlayDoc, Is.Not.Null, "Overlay UIDocument must be registered in CaptureModeService.Own.");
            Assert.That(overlayDoc.rootVisualElement, Is.Not.Null, "Overlay rootVisualElement must exist.");
            Assert.That(overlayDoc.panelSettings.themeStyleSheet, Is.Not.Null, "Without a theme the overlay draws no text and Unity warns on every open.");
            Assert.That(overlayDoc.rootVisualElement.style.display.value, Is.EqualTo(DisplayStyle.Flex), "Overlay must be visible initially.");

            // SetRecording flips the state the way the recorder does, without starting a real Recorder.
            CaptureModeService.SetRecording(true);
            Assert.That(CaptureModeService.IsRecording, Is.True);
            Assert.That(overlayDoc.rootVisualElement.style.display.value, Is.EqualTo(DisplayStyle.None), "Overlay must be hidden while recording.");

            CaptureModeService.SetRecording(false);
            Assert.That(CaptureModeService.IsRecording, Is.False);
            Assert.That(overlayDoc.rootVisualElement.style.display.value, Is.EqualTo(DisplayStyle.Flex), "Overlay must be shown again when recording stops.");

            CaptureModeService.Close();
        }

        [UnityTest]
        public IEnumerator Close_DestroysOverlay()
        {
            CaptureModeService.Open();

            UIDocument overlayDoc = null;
            foreach (var item in CaptureModeService.Own)
            {
                if (item is UIDocument doc)
                {
                    overlayDoc = doc;
                    break;
                }
            }

            Assert.That(overlayDoc, Is.Not.Null, "Overlay UIDocument must be registered in CaptureModeService.Own on Open.");
            var overlayGo = overlayDoc.gameObject;

            CaptureModeService.Close();
            yield return null;

            Assert.That(overlayGo == null || overlayGo.Equals(null), Is.True, "Overlay GameObject must be destroyed after Close.");
            Assert.That(CaptureModeService.Own, Has.No.Member(overlayDoc), "Overlay UIDocument must be removed from Own collection.");
            Assert.That(CaptureModeService.Own, Has.No.Member(overlayGo), "Overlay GameObject must be removed from Own collection.");
        }

        private sealed class FakeHooks : ICaptureHooks
        {
            public string GameName => "TestGame";
            public bool GodModeEnabled { get; private set; }
            public void SetGodMode(bool on) => GodModeEnabled = on;
            public int LevelCount => 10;
            public void JumpToLevel(int index) { }
            public IReadOnlyList<string> Presets => new[] { "Preset0", "Preset1" };
            public void ApplyPreset(int index) { }
        }
    }
}
