using System;
using System.Collections.Generic;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Capture
{
    /// <summary>
    /// Central static service controlling Capture Mode state, time manipulation, UI hiding and hook forwarding.
    /// </summary>
    public static class CaptureModeService
    {
        private static readonly float[] s_defaultSteps = { 1f, 0.5f, 0.25f, 0.1f };

        private static bool s_timeCaptured;
        private static float s_storedTimeScale = 1f;
        private static float s_storedFixedDeltaTime = 0.02f;

        private static bool s_godModeTurnedOnByCapture;
        private static bool s_isRecording;

        private static readonly List<Canvas> s_hiddenCanvases = new List<Canvas>();
        private static readonly List<HiddenUiDocRecord> s_hiddenUiDocuments = new List<HiddenUiDocRecord>();
        private static readonly List<GameObject> s_hiddenGameObjects = new List<GameObject>();

        /// <summary>
        /// Collection of Capture Mode UI elements or GameObjects that must never be hidden by Capture Mode.
        /// </summary>
        public static readonly HashSet<UnityEngine.Object> Own = new HashSet<UnityEngine.Object>();

        /// <summary>Fired when Capture Mode opens or closes.</summary>
        public static event Action<bool> OpenChanged;

        /// <summary>Fired whenever any capture state changes.</summary>
        public static event Action StateChanged;

        /// <summary>Seam event raised when video recording toggle is requested.</summary>
        public static event Action<bool> RecordToggleRequested;

        /// <summary>Seam event raised when free camera toggle is requested.</summary>
        public static event Action FreeCameraToggleRequested;

        /// <summary>Gets whether Capture Mode is currently open.</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>Gets whether game hooks are currently registered.</summary>
        public static bool HasHooks => Hooks != null;

        /// <summary>Gets the currently registered game capture hooks.</summary>
        public static ICaptureHooks Hooks { get; private set; }

        /// <summary>Gets the current index into slow motion speed steps.</summary>
        public static int SlowMotionIndex { get; private set; }

        /// <summary>Gets the current target playback speed multiplier.</summary>
        public static float Speed => GetCurrentSpeed();

        /// <summary>Gets whether playback time is currently paused by Capture Mode.</summary>
        public static bool Paused { get; private set; }

        /// <summary>Gets whether game UI is currently hidden by Capture Mode.</summary>
        public static bool UiHidden { get; private set; }

        /// <summary>Gets whether god mode is currently enabled by Capture Mode.</summary>
        public static bool GodMode { get; private set; }

        /// <summary>Gets whether video recording is currently active.</summary>
        public static bool IsRecording => s_isRecording;

        internal static bool IsSteppingFrame { get; private set; }

        /// <summary>Registers game-specific capture hooks, or clears them if null.</summary>
        public static void RegisterHooks(ICaptureHooks hooks)
        {
            Hooks = hooks;
            StateChanged?.Invoke();
        }

        /// <summary>Opens Capture Mode in Play Mode; logs a warning and does nothing outside Play Mode.</summary>
        public static void Open()
        {
            if (!Application.isPlaying)
            {
                NexusLog.WarnAlways("[Nexus Capture] Capture mode can only be opened in Play Mode.");
                return;
            }

            if (IsOpen) return;

            IsOpen = true;
            CaptureOriginalTime();
            OpenChanged?.Invoke(true);
            StateChanged?.Invoke();
        }

        /// <summary>Closes Capture Mode and restores all captured game modifications.</summary>
        public static void Close()
        {
            if (!Application.isPlaying && !IsOpen)
                return;

            RestoreAll();
        }

        /// <summary>Toggles Capture Mode open or closed.</summary>
        public static void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        /// <summary>Steps down playback speed to the next slower slow-motion step.</summary>
        public static void Slower()
        {
            var steps = GetSteps();
            if (SlowMotionIndex < steps.Length - 1)
            {
                SlowMotionIndex++;
                ApplySpeed();
                StateChanged?.Invoke();
            }
        }

        /// <summary>Steps up playback speed to the next faster slow-motion step.</summary>
        public static void Faster()
        {
            if (SlowMotionIndex > 0)
            {
                SlowMotionIndex--;
                ApplySpeed();
                StateChanged?.Invoke();
            }
        }

        /// <summary>Toggles playback pause state.</summary>
        public static void TogglePause()
        {
            if (!s_timeCaptured)
            {
                CaptureOriginalTime();
            }

            Paused = !Paused;
            if (Paused)
            {
                Time.timeScale = 0f;
            }
            else
            {
                Time.timeScale = Speed;
            }
            StateChanged?.Invoke();
        }

        /// <summary>Advances exactly one frame at the current speed while paused.</summary>
        public static void StepFrame()
        {
            if (!Paused) return;

            IsSteppingFrame = true;
            Time.timeScale = Speed;
        }

        internal static void ResolveStepFrame()
        {
            if (!IsSteppingFrame) return;

            IsSteppingFrame = false;
            Time.timeScale = 0f;
        }

        /// <summary>Toggles game UI visibility on or off.</summary>
        public static void ToggleUi()
        {
            if (UiHidden)
                ShowUi();
            else
                HideUi();
        }

        /// <summary>Hides game Canvases, UIDocuments, and CaptureHide GameObjects.</summary>
        public static void HideUi()
        {
            if (UiHidden) return;
            UiHidden = true;

            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude);
            for (int i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];
                if (canvas == null) continue;
                if (!canvas.isRootCanvas) continue;
                if (!canvas.enabled) continue;
                if (IsOwn(canvas, canvas.gameObject)) continue;
                if (canvas.GetComponentInParent<CaptureKeepVisible>() != null) continue;

                canvas.enabled = false;
                s_hiddenCanvases.Add(canvas);
            }

            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude);
            for (int i = 0; i < docs.Length; i++)
            {
                var doc = docs[i];
                if (doc == null) continue;
                if (IsOwn(doc, doc.gameObject)) continue;
                if (doc.GetComponentInParent<CaptureKeepVisible>() != null) continue;
                var root = doc.rootVisualElement;
                if (root == null) continue;

                s_hiddenUiDocuments.Add(new HiddenUiDocRecord(doc, root.style.display));
                root.style.display = DisplayStyle.None;
            }

            var hides = UnityEngine.Object.FindObjectsByType<CaptureHide>(FindObjectsInactive.Exclude);
            for (int i = 0; i < hides.Length; i++)
            {
                var hide = hides[i];
                if (hide == null) continue;
                var go = hide.gameObject;
                if (go == null) continue;
                if (IsOwn(hide, go)) continue;
                if (!go.activeSelf) continue;

                go.SetActive(false);
                s_hiddenGameObjects.Add(go);
            }

            StateChanged?.Invoke();
        }

        /// <summary>Restores UI elements that were hidden by Capture Mode.</summary>
        public static void ShowUi()
        {
            if (!UiHidden) return;
            UiHidden = false;

            for (int i = 0; i < s_hiddenCanvases.Count; i++)
            {
                var canvas = s_hiddenCanvases[i];
                if (canvas != null)
                {
                    canvas.enabled = true;
                }
            }
            s_hiddenCanvases.Clear();

            for (int i = 0; i < s_hiddenUiDocuments.Count; i++)
            {
                var record = s_hiddenUiDocuments[i];
                if (record.Document != null && record.Document.rootVisualElement != null)
                {
                    record.Document.rootVisualElement.style.display = record.PreviousDisplay;
                }
            }
            s_hiddenUiDocuments.Clear();

            for (int i = 0; i < s_hiddenGameObjects.Count; i++)
            {
                var go = s_hiddenGameObjects[i];
                if (go != null)
                {
                    go.SetActive(true);
                }
            }
            s_hiddenGameObjects.Clear();

            StateChanged?.Invoke();
        }

        /// <summary>Toggles god mode via registered game hooks.</summary>
        public static void ToggleGodMode()
        {
            if (Hooks == null) return;

            GodMode = !GodMode;
            s_godModeTurnedOnByCapture = GodMode;
            Hooks.SetGodMode(GodMode);
            StateChanged?.Invoke();
        }

        /// <summary>Jumps to the specified level index via registered game hooks.</summary>
        public static void JumpToLevel(int index)
        {
            if (Hooks == null) return;
            Hooks.JumpToLevel(index);
        }

        /// <summary>Applies the scenario preset at the specified index via registered game hooks.</summary>
        public static void ApplyPreset(int index)
        {
            if (Hooks == null) return;
            Hooks.ApplyPreset(index);
        }

        /// <summary>Requests toggling video recording state.</summary>
        public static void RequestRecordToggle()
        {
            s_isRecording = !s_isRecording;
            RecordToggleRequested?.Invoke(s_isRecording);
            StateChanged?.Invoke();
        }

        /// <summary>Requests toggling free camera mode.</summary>
        public static void RequestFreeCameraToggle()
        {
            FreeCameraToggleRequested?.Invoke();
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Idempotently restores all modifications made by Capture Mode.
        /// </summary>
        internal static void RestoreAll()
        {
            RestoreTime();
            RestoreUi();
            RestoreHooks();
            RestoreRecording();

            if (IsOpen)
            {
                IsOpen = false;
                OpenChanged?.Invoke(false);
                StateChanged?.Invoke();
            }
        }

        private static void CaptureOriginalTime()
        {
            if (!s_timeCaptured)
            {
                s_storedTimeScale = Time.timeScale;
                s_storedFixedDeltaTime = Time.fixedDeltaTime;
                s_timeCaptured = true;
            }
        }

        private static void RestoreTime()
        {
            if (s_timeCaptured)
            {
                Time.timeScale = s_storedTimeScale;
                Time.fixedDeltaTime = s_storedFixedDeltaTime;
                s_timeCaptured = false;
            }
            Paused = false;
            IsSteppingFrame = false;
            SlowMotionIndex = 0;
        }

        private static void RestoreUi()
        {
            if (UiHidden)
            {
                ShowUi();
            }
        }

        private static void RestoreHooks()
        {
            if (s_godModeTurnedOnByCapture)
            {
                s_godModeTurnedOnByCapture = false;
                GodMode = false;
                Hooks?.SetGodMode(false);
            }
        }

        private static void RestoreRecording()
        {
            if (s_isRecording)
            {
                s_isRecording = false;
                RecordToggleRequested?.Invoke(false);
            }
        }

        private static void ApplySpeed()
        {
            if (!s_timeCaptured)
            {
                CaptureOriginalTime();
            }

            Time.fixedDeltaTime = s_storedFixedDeltaTime * Speed;
            if (!Paused)
            {
                Time.timeScale = Speed;
            }
        }

        private static float[] GetSteps()
        {
            var steps = CaptureSettings.Load()?.SlowMotionSteps;
            return steps != null && steps.Length > 0 ? steps : s_defaultSteps;
        }

        private static float GetCurrentSpeed()
        {
            var steps = GetSteps();
            int index = Math.Max(0, Math.Min(SlowMotionIndex, steps.Length - 1));
            return steps[index];
        }

        private static bool IsOwn(UnityEngine.Object obj, GameObject go)
        {
            return (obj != null && Own.Contains(obj)) || (go != null && Own.Contains(go));
        }

        private readonly struct HiddenUiDocRecord
        {
            public UIDocument Document { get; }
            public StyleEnum<DisplayStyle> PreviousDisplay { get; }

            public HiddenUiDocRecord(UIDocument doc, StyleEnum<DisplayStyle> previousDisplay)
            {
                Document = doc;
                PreviousDisplay = previousDisplay;
            }
        }
    }
}
