using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Owns the in-game UGUI canvas hierarchy and layer interactivity policy.
    ///
    /// Shared by <see cref="UIManager"/> so the screen manager reads as a pure screen
    /// orchestrator (open/close/stack/pooling) while all canvas plumbing — root creation,
    /// per-layer transforms, modal blocking — lives here once.
    ///
    /// In-game UI is UGUI (see docs/adr); editor screens use UI Toolkit separately, so
    /// this module is the single place where the runtime canvas policy is decided.
    ///
    /// Every UIManager has one, and they share the scene's [Nexus_UICanvas] (issue #185): the layers below Modal stop
    /// taking input while any of them has a Modal-or-higher window open, and the last one to dispose destroys the
    /// canvas if a UICanvasSystem created it. A canvas the scene holds stays.
    /// </summary>
    [Preserve]
    public sealed class UICanvasSystem : IDisposable
    {
        private readonly Dictionary<UILayer, Transform> _layerRoots = new();
        private Transform _canvasRoot;
        private GameObject _canvasObject;
        private UICanvasUsers _users;

        public Transform CanvasRoot => _canvasRoot;

        /// <summary>Creates (or finds) the canvas root and all layer roots. Idempotent.</summary>
        public void EnsureInitialized()
        {
            _canvasObject = GameObject.Find("[Nexus_UICanvas]");
            bool created = _canvasObject == null;
            if (created)
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

            if (!_canvasObject.TryGetComponent(out _users))
            {
                _users = _canvasObject.AddComponent<UICanvasUsers>();
                _users.CreatedByNexus = created;
            }
            _users.Users.Add(this);
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
        /// Records whether this owner has a Modal-or-higher window open. The layers below Modal stop taking input while
        /// any owner of the shared canvas has one.
        /// </summary>
        public void SetModalOpen(bool open)
        {
            if (_users == null) return;
            if (open) _users.ModalOwners.Add(this);
            else _users.ModalOwners.Remove(this);
            ApplyInteractivity(_users);
        }

        private void ApplyInteractivity(UICanvasUsers users)
        {
            bool modalOpen = users.ModalOwners.Count > 0;
            foreach (var layerRoot in _layerRoots)
            {
                var canvasGroup = layerRoot.Value.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = layerRoot.Value.gameObject.AddComponent<CanvasGroup>();
                }

                bool interactive = !modalOpen || layerRoot.Key >= UILayer.Modal;
                canvasGroup.interactable = interactive;
                canvasGroup.blocksRaycasts = interactive;
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

        /// <summary>
        /// Leaves the shared canvas: the lower layers take input again if this owner's modal held them, and the last
        /// owner destroys a canvas a UICanvasSystem created. A call from another thread is posted to the main thread.
        /// </summary>
        public void Dispose()
        {
            if (SafeDestroyUtility.PostedToMainThread(_ => Dispose(), null)) return;
            var users = _users;
            _users = null;
            if (users != null)
            {
                users.Users.Remove(this);
                users.ModalOwners.Remove(this);
                if (users.Users.Count == 0 && users.CreatedByNexus) SafeDestroyUtility.SafeDestroy(_canvasObject);
                else ApplyInteractivity(users);
            }
            _canvasObject = null;
            _layerRoots.Clear();
            _canvasRoot = null;
        }
    }
}
