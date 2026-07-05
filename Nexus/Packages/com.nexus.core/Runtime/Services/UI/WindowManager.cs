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
        void CloseWindow(string windowName);
        void CloseTopWindow();
        void CloseAll();
        bool IsWindowOpen(string windowName);
        GameObject GetWindow(string windowName);
    }

    [Preserve]
    public class WindowManager : IWindowManager, INexusService, IDisposable
    {
        private readonly Dictionary<string, GameObject> _activeWindows = new();
        private readonly Dictionary<UILayer, Transform> _layerRoots = new();
        private readonly List<string> _windowHistory = new();

        private Transform _canvasRoot;
        private GameObject _canvasObject;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
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

            // Create layer roots
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

        public async Task<GameObject> OpenWindowAsync(string windowName, UILayer layer = UILayer.Screen, object args = null)
        {
            if (string.IsNullOrEmpty(windowName)) return null;

            if (_activeWindows.TryGetValue(windowName, out var existing) && existing != null)
            {
                existing.transform.SetAsLastSibling();
                return existing;
            }

            var request = Resources.LoadAsync<GameObject>($"UI/Windows/{windowName}");
            while (!request.isDone)
            {
                await Task.Yield();
            }

            var prefab = request.asset as GameObject;
            if (prefab == null)
            {
                Debug.LogError($"[WindowManager] Window prefab not found at path: UI/Windows/{windowName}");
                return null;
            }

            var targetParent = _layerRoots.TryGetValue(layer, out var layerRoot) ? layerRoot : _canvasRoot;
            var inst = UnityEngine.Object.Instantiate(prefab, targetParent);
            inst.name = windowName;
            _activeWindows[windowName] = inst;
            _windowHistory.Add(windowName);

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

            return inst;
        }

        public void OpenWindow(string windowName, object args = null)
        {
            _ = OpenWindowAsync(windowName, UILayer.Screen, args);
        }

        public async void CloseWindow(string windowName)
        {
            if (!_activeWindows.TryGetValue(windowName, out var go) || go == null) return;

            _activeWindows.Remove(windowName);
            _windowHistory.Remove(windowName);

            var lifecycles = go.GetComponents<IUIWindowLifecycle>();
            for (int i = 0; i < lifecycles.Length; i++)
            {
                try { await lifecycles[i].OnClosingAsync(CancellationToken.None); }
                catch (Exception ex) { Debug.LogException(ex); }
            }

            for (int i = 0; i < lifecycles.Length; i++)
            {
                try { await lifecycles[i].OnClosedAsync(CancellationToken.None); }
                catch (Exception ex) { Debug.LogException(ex); }
            }

            UnityEngine.Object.Destroy(go);
        }

        public void CloseTopWindow()
        {
            if (_windowHistory.Count > 0)
            {
                var top = _windowHistory[_windowHistory.Count - 1];
                CloseWindow(top);
            }
        }

        public void CloseAll()
        {
            var windows = new List<string>(_activeWindows.Keys);
            foreach (var win in windows)
            {
                CloseWindow(win);
            }
            _activeWindows.Clear();
            _windowHistory.Clear();
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
            CloseAll();
            if (_canvasObject != null)
            {
                UnityEngine.Object.Destroy(_canvasObject);
                _canvasObject = null;
            }
        }
    }
}
