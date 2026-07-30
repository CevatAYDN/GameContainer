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
        private readonly HashSet<string> _pendingOpenWindows = new();

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

            // E-5 fix: extended lock scope to eliminate the race window entirely.
            // We release the lock ONLY for the async instantiation (which may be slow),
            // but re-check conditions immediately after re-acquiring.
            // The wait-loop uses a max-retry count to prevent infinite spinning.
            GameObject existing = null;
            const int maxPendingWaitMs = 30000; // 30-second timeout for pending opens
            int totalWaitMs = 0;

            // Phase 1: registration check (under lock)
            // E-5 fix: use lockHeld guard to prevent SemaphoreFullException if the inner
            // while-loop releases the lock and an exception occurs before re-acquiring.
            await _windowLock.WaitAsync();
            bool lockHeld = true;
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

                if (_activeWindows.TryGetValue(windowName, out existing) && existing != null)
                {
                    existing.transform.SetAsLastSibling();
                    return existing;
                }

                // If another thread is already opening this window, wait for it
                while (_pendingOpenWindows.Contains(windowName))
                {
                    _windowLock.Release();
                    lockHeld = false;
                    await Task.Delay(10);
                    totalWaitMs += 10;
                    if (totalWaitMs >= maxPendingWaitMs)
                    {
                        NexusRuntime.Logger?.LogError($"[WindowManager] Timed out waiting for pending window: {windowName}");
                        return null;
                    }
                    await _windowLock.WaitAsync();
                    lockHeld = true;

                    // Re-check: if window appeared while we were waiting, return it
                    if (_activeWindows.TryGetValue(windowName, out existing) && existing != null)
                    {
                        return existing;
                    }
                }

                // We are now the designated opener
                _pendingOpenWindows.Add(windowName);
            }
            finally
            {
                if (lockHeld)
                    _windowLock.Release();
            }

            // Phase 2: instantiate outside lock (may be slow - asset loading)
            var targetParent = _layerRoots.TryGetValue(layer, out var layerRoot) ? layerRoot : _canvasRoot;
            GameObject inst = null;
            try
            {
                inst = await AssetProvider.InstantiateWindowAsync(windowName, targetParent);
                if (inst == null)
                {
                    NexusRuntime.Logger?.LogError($"[WindowManager] Failed to instantiate window: {windowName}");
                    await _windowLock.WaitAsync();
                    try { _pendingOpenWindows.Remove(windowName); }
                    finally { _windowLock.Release(); }
                    return null;
                }

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

                // Phase 3: register under lock (atomic add + pending removal)
                await _windowLock.WaitAsync();
                try
                {
                    // E-5 fix: guard against a concurrent close that snuck in while we were instantiating
                    if (!inst) // GameObject was destroyed externally
                    {
                        _pendingOpenWindows.Remove(windowName);
                        return null;
                    }
                    _activeWindows[windowName] = inst;
                    _windowHistory.Add(windowName);
                    _pendingOpenWindows.Remove(windowName);
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
                if (inst != null)
                {
                    UnityEngine.Object.Destroy(inst);
                }
                await _windowLock.WaitAsync();
                try { _pendingOpenWindows.Remove(windowName); }
                finally { _windowLock.Release(); }
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

            AssetProvider.ReleaseWindow(go);

            // Only remove if still the same GameObject — a concurrent OpenWindowAsync
            // may have reopened the same name while callbacks ran outside the lock.
            await _windowLock.WaitAsync();
            try
            {
                if (_activeWindows.TryGetValue(windowName, out var current) && current == go)
                {
                    _activeWindows.Remove(windowName);
                    _windowHistory.Remove(windowName);
                    UpdateLayerInteractivity();
                }
            }
            finally
            {
                _windowLock.Release();
            }
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

        public bool IsWindowOpen(string windowName)
        {
            if (string.IsNullOrEmpty(windowName)) return false;
            // Non-blocking — avoids deadlocking the main thread when an async method holds the lock.
            if (!_windowLock.Wait(0)) return false;
            try
            {
                return _activeWindows.ContainsKey(windowName);
            }
            finally
            {
                _windowLock.Release();
            }
        }

        public GameObject GetWindow(string windowName)
        {
            if (string.IsNullOrEmpty(windowName)) return null;
            if (!_windowLock.Wait(0)) return null;
            try
            {
                _activeWindows.TryGetValue(windowName, out var go);
                return go;
            }
            finally
            {
                _windowLock.Release();
            }
        }

        public void OnDispose() => Dispose();

        // ── Editor introspection (G-3) ────────────────────────────

        /// <summary>Immutable description of one open window for editor visibility.</summary>
        public readonly struct WindowInfo
        {
            public readonly string Name;
            public readonly UILayer Layer;
            public readonly int HistoryOrder;
            public readonly bool IsAlive;
            public WindowInfo(string name, UILayer layer, int historyOrder, bool isAlive)
            {
                Name = name; Layer = layer; HistoryOrder = historyOrder; IsAlive = isAlive;
            }
        }

        /// <summary>
        /// Thread-safe snapshot of currently open windows with their UI layer and open order.
        /// Read-only: never mutates manager state. Intended for editor tooling.
        /// </summary>
        public IReadOnlyList<WindowInfo> GetOpenWindowsSnapshot()
        {
            var result = new List<WindowInfo>();
            _windowLock.Wait();
            try
            {
                foreach (var kvp in _activeWindows)
                {
                    int order = _windowHistory.LastIndexOf(kvp.Key);
                    result.Add(new WindowInfo(kvp.Key, ResolveLayer(kvp.Value), order, kvp.Value != null));
                }
            }
            finally
            {
                _windowLock.Release();
            }
            result.Sort((a, b) => a.HistoryOrder.CompareTo(b.HistoryOrder));
            return result;
        }

        /// <summary>Number of windows currently mid-open (awaiting instantiation).</summary>
        public int PendingWindowCount
        {
            get
            {
                _windowLock.Wait();
                try { return _pendingOpenWindows.Count; }
                finally { _windowLock.Release(); }
            }
        }

        private UILayer ResolveLayer(GameObject go)
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
