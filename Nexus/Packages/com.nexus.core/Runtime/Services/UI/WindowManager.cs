using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public enum UILayer
    {
        Background = 0,
        HUD = 10,
        Screen = 20,
        Popup = 30,
        Modal = 40,
        Overlay = 50,
        System = 60
    }

    public interface IUIWindowLifecycle
    {
        ValueTask OnOpeningAsync(object args, CancellationToken ct);
        ValueTask OnOpenedAsync(CancellationToken ct);
        ValueTask OnClosingAsync(CancellationToken ct);
        ValueTask OnClosedAsync(CancellationToken ct);
    }

    public interface IWindowManager
    {
        Task<GameObject> OpenWindowAsync(string windowName, UILayer layer = UILayer.Screen, object args = null);
        void OpenWindow(string windowName, object args = null);
        Task CloseWindowAsync(string windowName);
        void CloseWindow(string windowName);
        Task CloseTopWindowAsync();
        void CloseTopWindow();
        Task CloseAllAsync();
        void CloseAll();
        bool IsWindowOpen(string windowName);
        GameObject GetWindow(string windowName);
    }

    [Preserve]
    public class WindowManager : IWindowManager, INexusService, IDisposable
    {
        [Inject] public IUIAssetProvider AssetProvider { get; set; }

        private readonly Dictionary<string, GameObject> _activeWindows = new();
        private readonly Dictionary<UILayer, Transform> _layerRoots = new();
        private readonly List<string> _windowHistory = new();
        private readonly SemaphoreSlim _windowLock = new(1, 1);

        private Transform _canvasRoot;
        private GameObject _canvasObject;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            if (AssetProvider == null)
            {
                AssetProvider = new ResourcesUIAssetProvider();
            }
            SetupCanvasAndLayers();
            return default;
        }

        private void SetupCanvasAndLayers()
        {
            _canvasObject = GameObject.Find("[Nexus_UICanvas]");
            if (_canvasObject == null)
            {
                _canvasObject = new GameObject("[Nexus_UICanvas]");
                UnityEngine.Object.DontDestroyOnLoad(_canvasObject);

                var canvas = _canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 100;

                var canvasScalerType = Type.GetType("UnityEngine.UI.CanvasScaler, UnityEngine.UI") ?? Type.GetType("UnityEngine.UI.CanvasScaler, UnityEngine.UIModule");
                if (canvasScalerType != null)
                {
                    var scaler = _canvasObject.AddComponent(canvasScalerType);
                    var scaleModeProp = canvasScalerType.GetProperty("uiScaleMode");
                    var refResProp = canvasScalerType.GetProperty("referenceResolution");
                    var matchProp = canvasScalerType.GetProperty("matchWidthOrHeight");

                    var scaleModeType = Type.GetType("UnityEngine.UI.CanvasScaler+ScaleMode, UnityEngine.UI") ?? Type.GetType("UnityEngine.UI.CanvasScaler+ScaleMode, UnityEngine.UIModule");
                    if (scaleModeType != null && scaleModeProp != null)
                    {
                        var scaleWithScreen = Enum.Parse(scaleModeType, "ScaleWithScreenSize");
                        scaleModeProp.SetValue(scaler, scaleWithScreen);
                    }
                    refResProp?.SetValue(scaler, new Vector2(1080, 1920));
                    matchProp?.SetValue(scaler, 0.5f);
                }

                var raycasterType = Type.GetType("UnityEngine.UI.GraphicRaycaster, UnityEngine.UI") ?? Type.GetType("UnityEngine.UI.GraphicRaycaster, UnityEngine.UIModule");
                if (raycasterType != null)
                {
                    _canvasObject.AddComponent(raycasterType);
                }
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

                    var raycasterType = Type.GetType("UnityEngine.UI.GraphicRaycaster, UnityEngine.UI") ?? Type.GetType("UnityEngine.UI.GraphicRaycaster, UnityEngine.UIModule");
                    if (raycasterType != null)
                    {
                        layerGo.AddComponent(raycasterType);
                    }
                }
                _layerRoots[layer] = layerGo.transform;
            }
        }

        private void UpdateLayerInteractivity()
        {
            UILayer highestActiveLayer = UILayer.Background;
            bool hasModalOrHigher = false;

            foreach (var kvp in _activeWindows)
            {
                if (kvp.Value != null)
                {
                    foreach (var layerRoot in _layerRoots)
                    {
                        if (kvp.Value.transform.parent == layerRoot.Value)
                        {
                            if (layerRoot.Key >= UILayer.Modal)
                            {
                                hasModalOrHigher = true;
                            }
                            if (layerRoot.Key > highestActiveLayer)
                            {
                                highestActiveLayer = layerRoot.Key;
                            }
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

        public async Task<GameObject> OpenWindowAsync(string windowName, UILayer layer = UILayer.Screen, object args = null)
        {
            if (string.IsNullOrEmpty(windowName)) return null;

            await _windowLock.WaitAsync();
            bool alreadyOpen = false;
            GameObject existing = null;
            try
            {
                // Clean up any externally destroyed windows
                var keysToRemove = new List<string>();
                foreach (var kvp in _activeWindows)
                {
                    if (kvp.Value == null)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    _activeWindows.Remove(key);
                    _windowHistory.Remove(key);
                }

                alreadyOpen = _activeWindows.TryGetValue(windowName, out existing) && existing != null;
                if (alreadyOpen)
                {
                    existing.transform.SetAsLastSibling();
                }
            }
            finally
            {
                _windowLock.Release();
            }

            if (alreadyOpen)
            {
                return existing;
            }

            var targetParent = _layerRoots.TryGetValue(layer, out var layerRoot) ? layerRoot : _canvasRoot;
            var inst = await AssetProvider.InstantiateWindowAsync(windowName, targetParent);
            if (inst == null)
            {
                NexusRuntime.Logger?.LogError($"[WindowManager] Failed to instantiate window: {windowName}");
                return null;
            }

            try
            {
                var lifecycles = inst.GetComponents<IUIWindowLifecycle>();
                for (int i = 0; i < lifecycles.Length; i++)
                {
                    await lifecycles[i].OnOpeningAsync(args, CancellationToken.None);
                }

                inst.SetActive(true);

                for (int i = 0; i < lifecycles.Length; i++)
                {
                    await lifecycles[i].OnOpenedAsync(CancellationToken.None);
                }

                await _windowLock.WaitAsync();
                try
                {
                    _activeWindows[windowName] = inst;
                    _windowHistory.Add(windowName);
                    UpdateLayerInteractivity();
                }
                finally
                {
                    _windowLock.Release();
                }

                return inst;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[WindowManager] Failed to open window '{windowName}': {ex.Message}");
                UnityEngine.Object.Destroy(inst);
                return null;
            }
        }

        public void OpenWindow(string windowName, object args = null)
        {
            _ = OpenWindowAsync(windowName, UILayer.Screen, args);
        }

        public async Task CloseWindowAsync(string windowName)
        {
            GameObject go = null;
            await _windowLock.WaitAsync();
            try
            {
                // Clean up externally destroyed windows first
                var keysToRemove = new List<string>();
                foreach (var kvp in _activeWindows)
                {
                    if (kvp.Value == null)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    _activeWindows.Remove(key);
                    _windowHistory.Remove(key);
                }

                _activeWindows.TryGetValue(windowName, out go);
            }
            finally
            {
                _windowLock.Release();
            }

            if (go == null) return;

            var lifecycles = go.GetComponents<IUIWindowLifecycle>();
            for (int i = 0; i < lifecycles.Length; i++)
            {
                try { await lifecycles[i].OnClosingAsync(CancellationToken.None); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            for (int i = 0; i < lifecycles.Length; i++)
            {
                try { await lifecycles[i].OnClosedAsync(CancellationToken.None); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            await _windowLock.WaitAsync();
            try
            {
                _activeWindows.Remove(windowName);
                _windowHistory.Remove(windowName);
                UpdateLayerInteractivity();
            }
            finally
            {
                _windowLock.Release();
            }

            AssetProvider.ReleaseWindow(go);
        }

        public void CloseWindow(string windowName)
        {
            _ = CloseWindowAsync(windowName);
        }

        public async Task CloseTopWindowAsync()
        {
            if (_windowHistory.Count > 0)
            {
                var top = _windowHistory[_windowHistory.Count - 1];
                await CloseWindowAsync(top);
            }
        }

        public void CloseTopWindow()
        {
            _ = CloseTopWindowAsync();
        }

        public async Task CloseAllAsync()
        {
            List<string> windows;
            await _windowLock.WaitAsync();
            try
            {
                windows = new List<string>(_activeWindows.Keys);
            }
            finally
            {
                _windowLock.Release();
            }

            foreach (var win in windows)
            {
                await CloseWindowAsync(win);
            }
        }

        public void CloseAll()
        {
            _ = CloseAllAsync();
        }

        public bool IsWindowOpen(string windowName) => _activeWindows.ContainsKey(windowName);

        public GameObject GetWindow(string windowName)
        {
            _activeWindows.TryGetValue(windowName, out var go);
            return go;
        }

        public void OnDispose() => Dispose();

        public void Dispose()
        {
            // Destroy all active windows directly; lifecycle events are skipped during teardown
            foreach (var kvp in _activeWindows)
            {
                if (kvp.Value != null)
                    UnityEngine.Object.Destroy(kvp.Value);
            }
            _activeWindows.Clear();
            _windowHistory.Clear();

            if (_canvasObject != null)
            {
                UnityEngine.Object.Destroy(_canvasObject);
                _canvasObject = null;
            }

            _windowLock.Dispose();
        }
    }
}
