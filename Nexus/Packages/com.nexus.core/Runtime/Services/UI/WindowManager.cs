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
    public class WindowManager : NexusService<IWindowManager>, IWindowManager
    {
        [Inject] public IUIAssetProvider AssetProvider { get; set; }

        private readonly Dictionary<string, GameObject> _activeWindows = new();
        private readonly List<string> _windowHistory = new();
        private readonly SemaphoreSlim _windowLock = new(1, 1);
        private readonly HashSet<string> _pendingOpenWindows = new();
        private readonly Dictionary<string, TaskCompletionSource<GameObject>> _pendingOpenCompletions = new();

        private readonly UICanvasSystem _canvas = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (AssetProvider == null)
            {
                AssetProvider = new ResourcesUIAssetProvider();
            }
            _canvas.EnsureInitialized();
            return default;
        }

        public async Task<GameObject> OpenWindowAsync(string windowName, UILayer layer = UILayer.Screen, object args = null)
        {
            if (string.IsNullOrEmpty(windowName)) return null;

            GameObject existing = null;

            // Phase 1: registration check (under lock)
            await _windowLock.WaitAsync();
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

                // If another thread/task is already opening this window, await its completion
                if (_pendingOpenCompletions.TryGetValue(windowName, out var pendingTcs))
                {
                    _windowLock.Release();
                    return await pendingTcs.Task;
                }

                // Designated opener
                var tcs = new TaskCompletionSource<GameObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingOpenCompletions[windowName] = tcs;
                _pendingOpenWindows.Add(windowName);
            }
            finally
            {
                if (_windowLock.CurrentCount == 0)
                    _windowLock.Release();
            }

            // Phase 2: instantiate outside lock (may be slow - asset loading)
            var targetParent = _canvas.GetLayerRoot(layer);
            GameObject inst = null;
            try
            {
                inst = await AssetProvider.InstantiateWindowAsync(windowName, targetParent);
                if (inst == null)
                {
                    NexusRuntime.Logger?.LogError($"[WindowManager] Failed to instantiate window: {windowName}");
                    await _windowLock.WaitAsync();
                    try
                    {
                        _pendingOpenWindows.Remove(windowName);
                        if (_pendingOpenCompletions.Remove(windowName, out var failedTcs))
                            failedTcs.TrySetResult(null);
                    }
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
                    if (!inst) // GameObject was destroyed externally
                    {
                        _pendingOpenWindows.Remove(windowName);
                        if (_pendingOpenCompletions.Remove(windowName, out var cancelledTcs))
                            cancelledTcs.TrySetResult(null);
                        return null;
                    }
                    _activeWindows[windowName] = inst;
                    _windowHistory.Add(windowName);
                    _pendingOpenWindows.Remove(windowName);
                    if (_pendingOpenCompletions.Remove(windowName, out var successTcs))
                        successTcs.TrySetResult(inst);
                    _canvas.UpdateLayerInteractivity(_activeWindows);
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
                try
                {
                    _pendingOpenWindows.Remove(windowName);
                    if (_pendingOpenCompletions.Remove(windowName, out var errTcs))
                        errTcs.TrySetResult(null);
                }
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
                    _canvas.UpdateLayerInteractivity(_activeWindows);
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
            _windowLock.Wait();
            try
            {
                return _activeWindows.TryGetValue(windowName, out var go) && go != null;
            }
            finally
            {
                _windowLock.Release();
            }
        }

        public GameObject GetWindow(string windowName)
        {
            if (string.IsNullOrEmpty(windowName)) return null;
            _windowLock.Wait();
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
            if (!_windowLock.Wait(50)) return result;
            try
            {
                foreach (var kvp in _activeWindows)
                {
                    int order = _windowHistory.LastIndexOf(kvp.Key);
                    result.Add(new WindowInfo(kvp.Key, _canvas.ResolveLayer(kvp.Value), order, kvp.Value != null));
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
                if (!_windowLock.Wait(50)) return 0;
                try { return _pendingOpenWindows.Count; }
                finally { _windowLock.Release(); }
            }
        }

        public override void Dispose()
        {
            // Destroy all active windows directly; lifecycle events are skipped during teardown
            foreach (var kvp in _activeWindows)
            {
                if (kvp.Value != null)
                    UnityEngine.Object.Destroy(kvp.Value);
            }
            _activeWindows.Clear();
            _windowHistory.Clear();

            _canvas.Dispose();

            _windowLock.Dispose();
        }
    }
}
