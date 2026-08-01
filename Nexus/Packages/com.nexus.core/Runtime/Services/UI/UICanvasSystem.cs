using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Owns the in-game UGUI canvas hierarchy and layer interactivity policy.
    ///
    /// Extracted from <see cref="WindowManager"/> so the window manager reads as a pure
    /// window-lifecycle orchestrator (open/close/stack/locking) while all canvas
    /// plumbing — root creation, per-layer transforms, modal blocking — lives here once.
    ///
    /// In-game UI is UGUI (see docs/adr); editor screens use UI Toolkit separately, so
    /// this module is the single place where the runtime canvas policy is decided.
    /// </summary>
    [Preserve]
    public sealed class UICanvasSystem : IDisposable
    {
        private readonly Dictionary<UILayer, Transform> _layerRoots = new();
        private Transform _canvasRoot;
        private GameObject _canvasObject;

        public Transform CanvasRoot => _canvasRoot;

        /// <summary>Creates (or finds) the canvas root and all layer roots. Idempotent.</summary>
        public void EnsureInitialized()
        {
            _canvasObject = GameObject.Find("[Nexus_UICanvas]");
            if (_canvasObject == null)
            {
                _canvasObject = new GameObject("[Nexus_UICanvas]");
                UnityEngine.Object.DontDestroyOnLoad(_canvasObject);

                var canvas = _canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 100;

                var scaler = _canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920);
                scaler.matchWidthOrHeight = 0.5f;

                _canvasObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }

            _canvasRoot = _canvasObject.transform;

            foreach (UILayer layer in Enum.GetValues(typeof(UILayer)))
            {
                var layerGo = _canvasRoot.Find(layer.ToString())?.gameObject;
                if (layerGo == null)
                {
                    layerGo = new GameObject(layer.ToString());
                    var rect = layerGo.AddComponent<RectTransform>();
                    rect.SetParent(_canvasRoot, false);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;

                    var canvasComponent = layerGo.AddComponent<Canvas>();
                    canvasComponent.overrideSorting = true;
                    canvasComponent.sortingOrder = (int)layer;

                    layerGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                }
                _layerRoots[layer] = layerGo.transform;
            }
        }

        /// <summary>Returns the transform a window at the given layer should parent to.</summary>
        public Transform GetLayerRoot(UILayer layer)
        {
            return _layerRoots.TryGetValue(layer, out var layerRoot) ? layerRoot : _canvasRoot;
        }

        /// <summary>
        /// Recomputes CanvasGroup blocking across layer roots based on the currently
        /// active windows. Modal-or-higher windows block all lower layers.
        /// </summary>
        public void UpdateLayerInteractivity(IReadOnlyDictionary<string, GameObject> activeWindows)
        {
            bool hasModalOrHigher = false;

            foreach (var kvp in activeWindows)
            {
                if (kvp.Value != null)
                {
                    foreach (var layerRoot in _layerRoots)
                    {
                        if (kvp.Value.transform.parent == layerRoot.Value && layerRoot.Key >= UILayer.Modal)
                        {
                            hasModalOrHigher = true;
                        }
                    }
                }
            }

            foreach (var layerRoot in _layerRoots)
            {
                var canvasGroup = layerRoot.Value.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = layerRoot.Value.gameObject.AddComponent<CanvasGroup>();
                }

                if (hasModalOrHigher && layerRoot.Key < UILayer.Modal)
                {
                    canvasGroup.interactable = false;
                    canvasGroup.blocksRaycasts = false;
                }
                else
                {
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                }
            }
        }

        /// <summary>Resolves which UILayer a window's transform currently sits in.</summary>
        public UILayer ResolveLayer(GameObject go)
        {
            if (go == null) return UILayer.Screen;
            var parent = go.transform.parent;
            foreach (var kvp in _layerRoots)
            {
                if (kvp.Value == parent) return kvp.Key;
            }
            return UILayer.Screen;
        }

        public void Dispose()
        {
            if (_canvasObject != null)
            {
                UnityEngine.Object.Destroy(_canvasObject);
                _canvasObject = null;
            }
            _layerRoots.Clear();
            _canvasRoot = null;
        }
    }
}
