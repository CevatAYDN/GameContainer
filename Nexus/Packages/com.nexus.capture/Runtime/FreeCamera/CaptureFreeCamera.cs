using System;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Capture
{
    /// <summary>
    /// Controls the editor-only free camera during Capture Mode.
    /// </summary>
    public static class CaptureFreeCamera
    {
        private static GameObject s_freeCameraObject;
        private static Camera s_disabledMainCamera;
        private static bool s_capturedMainCameraDisabled;

        /// <summary>Gets whether free camera mode is currently active.</summary>
        public static bool Active { get; private set; }

        /// <summary>Fired when free camera active state changes.</summary>
        public static event Action ActiveChanged;

        internal static GameObject CameraObject => s_freeCameraObject;

        static CaptureFreeCamera()
        {
            CaptureModeService.FreeCameraToggleRequested += OnFreeCameraToggleRequested;
            CaptureModeService.OpenChanged += OnOpenChanged;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            CaptureModeService.FreeCameraToggleRequested -= OnFreeCameraToggleRequested;
            CaptureModeService.FreeCameraToggleRequested += OnFreeCameraToggleRequested;
            CaptureModeService.OpenChanged -= OnOpenChanged;
            CaptureModeService.OpenChanged += OnOpenChanged;
        }

        /// <summary>Toggles free camera mode on or off.</summary>
        public static void Toggle()
        {
            if (Active)
            {
                Restore();
            }
            else
            {
                Enable();
            }
        }

        /// <summary>
        /// Idempotently restores the free camera state and re-enables main camera.
        /// </summary>
        internal static void Restore()
        {
            bool changed = Active;
            Active = false;

            if (s_freeCameraObject != null)
            {
                CaptureModeService.Own.Remove(s_freeCameraObject);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(s_freeCameraObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(s_freeCameraObject);
                }
                s_freeCameraObject = null;
            }

            // Restore the main camera only if Capture disabled it and it still exists.
            if (s_capturedMainCameraDisabled)
            {
                if (s_disabledMainCamera != null)
                {
                    s_disabledMainCamera.enabled = true;
                }
                s_disabledMainCamera = null;
                s_capturedMainCameraDisabled = false;
            }

            if (changed)
            {
                ActiveChanged?.Invoke();
            }
        }

        private static void Enable()
        {
            var mainCamera = Camera.main;
            if (mainCamera == null)
            {
                NexusLog.WarnAlways("[Nexus Capture] Free camera requires a Camera tagged MainCamera.");
                return;
            }

            s_freeCameraObject = new GameObject("NexusCaptureFreeCamera");
            // Not DontSave: a DontSave object survives leaving Play Mode and leaks into edit mode.
            s_freeCameraObject.hideFlags = HideFlags.HideInHierarchy;
            CaptureModeService.Own.Add(s_freeCameraObject);
            UnityEngine.Object.DontDestroyOnLoad(s_freeCameraObject);

            s_freeCameraObject.transform.position = mainCamera.transform.position;
            s_freeCameraObject.transform.rotation = mainCamera.transform.rotation;

#if NEXUS_CINEMACHINE
            var brain = mainCamera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null)
            {
                var cineCam = s_freeCameraObject.AddComponent<Unity.Cinemachine.CinemachineCamera>();
                var allCameras = UnityEngine.Object.FindObjectsByType<Unity.Cinemachine.CinemachineCamera>(FindObjectsInactive.Exclude);
                int maxPriority = 0;
                bool hasAny = false;
                for (int i = 0; i < allCameras.Length; i++)
                {
                    var other = allCameras[i];
                    if (other == null || other == cineCam) continue;
                    int p = (int)other.Priority;
                    if (!hasAny || p > maxPriority)
                    {
                        maxPriority = p;
                        hasAny = true;
                    }
                }

                cineCam.Priority = hasAny ? maxPriority + 1 : 1;
                cineCam.Lens = brain.CurrentCameraState.Lens;
                cineCam.Follow = null;
                cineCam.LookAt = null;
            }
            else
#endif
            {
                var cam = s_freeCameraObject.AddComponent<Camera>();
                cam.CopyFrom(mainCamera);

#if NEXUS_URP
                var mainData = mainCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (mainData != null)
                {
                    var newData = s_freeCameraObject.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                    if (newData == null)
                    {
                        newData = s_freeCameraObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                    }

                    // Leave default renderer: URP has no public getter for renderer index on UniversalAdditionalCameraData.
                    newData.renderPostProcessing = mainData.renderPostProcessing;
                    newData.antialiasing = mainData.antialiasing;
                    newData.antialiasingQuality = mainData.antialiasingQuality;
                    newData.stopNaN = mainData.stopNaN;
                    newData.dithering = mainData.dithering;
                    newData.renderShadows = mainData.renderShadows;
                    newData.requiresColorOption = mainData.requiresColorOption;
                    newData.requiresDepthOption = mainData.requiresDepthOption;
                    newData.volumeLayerMask = mainData.volumeLayerMask;
                    newData.volumeTrigger = s_freeCameraObject.transform;
                }
#endif
                mainCamera.enabled = false;
                s_capturedMainCameraDisabled = true;
                s_disabledMainCamera = mainCamera;
            }

            s_freeCameraObject.AddComponent<FreeCameraMover>();
            Active = true;
            ActiveChanged?.Invoke();
        }

        private static void OnFreeCameraToggleRequested()
        {
            if (CaptureModeService.IsOpen)
            {
                Toggle();
            }
        }

        private static void OnOpenChanged(bool isOpen)
        {
            if (!isOpen)
            {
                Restore();
            }
        }
    }
}
