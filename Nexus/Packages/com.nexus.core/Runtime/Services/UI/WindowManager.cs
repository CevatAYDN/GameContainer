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

        // A4b: lock-free read snapshot. Every mutation to _activeWindows happens under
        // _windowLock and is followed by RefreshReadSnapshot(); readers (IsWindowOpen /
        // GetWindow) answer from this volatile snapshot without ever taking the
        // semaphore — so they cannot block the main thread and cannot return a false
        // negative from a transient lock hold.
        private volatile Dictionary<string, GameObject> _activeWindowsRead = new();

        // A4a: completion signal fired (under the lock) whenever the pending set
        // changes, so a concurrent opener waits on the actual state change instead of
        // polling every 10 ms (~3000 timer allocations over a 30 s contention window).
        private TaskCompletionSource<bool> _pendingChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // B3: set at the start of Dispose so in-flight async open/close loops bail out
        // instead of touching a disposed semaphore / destroyed GameObjects.
        private volatile bool _disposed;

        private readonly UICanvasSystem _canvas = new();

        /// <summary>A4c: context lifetime token so window lifecycle callbacks are
        /// cancelled during context teardown instead of running with CancellationToken.None.</summary>
        private CancellationToken LifetimeToken => Context?.LifetimeToken ?? CancellationToken.None;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (AssetProvider == null)
            {
                AssetProvider = new ResourcesUIAssetProvider();
            }
            _canvas.EnsureInitialized();
            return default;
        }

        /// <summary>Refreshes the lock-free read snapshot. Call under _windowLock.</summary>
        private void RefreshReadSnapshot()
        {
            _activeWindowsRead = new Dictionary<string, GameObject>(_activeWindows);
        }

        /// <summary>Signals waiters that the pending-open set changed. Call under _windowLock.</summary>
        private void SignalPendingChanged()
        {
            var previous = _pendingChanged;
            _pendingChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult(true);
        }

        /// <summary>
        /// Acquires the window lock, returning false if the manager is disposing (or a
        /// concurrent Dispose has already released the semaphore) — callers bail out
        /// instead of crashing on ObjectDisposedException.
        /// </summary>
        private async Task<bool> TryAcquireWindowLockAsync()
        {
            if (_disposed) return false;
            try
            {
                await _windowLock.WaitAsync();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            if (_disposed)
            {
                try { _windowLock.Release(); } catch (ObjectDisposedException) { /* Ignored: Semaphore was disposed during context teardown */ }
                return false;
            }
            return true;
        }

        public async Task<GameObject> OpenWindowAsync(string windowName, UILayer layer = UILayer.Screen, object args = null)
        {
            if (string.IsNullOrEmpty(windowName)) return null;
            if (_disposed) return null;

            // E-5 fix: extended lock scope to eliminate the race window entirely.
            // We release the lock ONLY for the async instantiation (which may be slow),
            // but re-check conditions immediately after re-acquiring.
            // The wait uses a completion signal (A4a) with a max-retry timeout instead
            // of a 10 ms poll loop.
            GameObject existing = null;
            const int maxPendingWaitMs = 30000; // 30-second timeout for pending opens

            // Phase 1: registration check (under lock)
            if (!await TryAcquireWindowLockAsync()) return null;
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
                if (keysToRemove.Count > 0)
                {
                    foreach (var key in keysToRemove)
                    {
                        _activeWindows.Remove(key);
                        _windowHistory.Remove(key);
                    }
                    RefreshReadSnapshot();
                }

                if (_activeWindows.TryGetValue(windowName, out existing) && existing != null)
                {
                    existing.transform.SetAsLastSibling();
                    // B6: keep the history order in sync with the visual top-most window so
                    // CloseTopWindowAsync closes what the player actually sees on top.
                    _windowHistory.Remove(windowName);
                    _windowHistory.Add(windowName);
                    return existing;
                }

                // If another thread is already opening this window, wait for the pending-set
                // change signal instead of polling. Each iteration allocates ONE delay task
                // for the remaining timeout, never 3000×10 ms timers.
                var pendingWait = System.Diagnostics.Stopwatch.StartNew();
                while (_pendingOpenWindows.Contains(windowName))
                {
                    // T2 fix: capture the completion signal UNDER the lock. SignalPendingChanged()
                    // only ever runs while holding _windowLock, so reading _pendingChanged here
                    // cannot race a concurrent completion. Previously the TCS was read AFTER
                    // releasing the lock — a completion that landed between the release and the
                    // read had already swapped _pendingChanged, so this waiter grabbed the NEW
                    // (unsignaled) TCS and slept the full 30 s timeout even though the window
                    // was already open (a spurious "timed out" error for a completed open).
                    var signal = _pendingChanged.Task;
                    _windowLock.Release();
                    lockHeld = false;

                    int remainingMs = maxPendingWaitMs - (int)pendingWait.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        NexusRuntime.Logger?.LogError($"[WindowManager] Timed out waiting for pending window: {windowName}");
                        return null;
                    }
                    var completed = await Task.WhenAny(signal, Task.Delay(remainingMs));
                    if (completed != signal)
                    {
                        NexusRuntime.Logger?.LogError($"[WindowManager] Timed out waiting for pending window: {windowName}");
                        return null;
                    }

                    if (!await TryAcquireWindowLockAsync()) return null;
                    lockHeld = true;

                    // Re-check: if window appeared while we were waiting, return it
                    if (_activeWindows.TryGetValue(windowName, out existing) && existing != null)
                    {
                        return existing;
                    }
                }

                // We are now the designated opener
                _pendingOpenWindows.Add(windowName);
                SignalPendingChanged();
            }
            finally
            {
                if (lockHeld)
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
                    if (await TryAcquireWindowLockAsync())
                    {
                        try { _pendingOpenWindows.Remove(windowName); SignalPendingChanged(); }
                        finally { _windowLock.Release(); }
                    }
                    return null;
                }

                var lifecycles = inst.GetComponents<IUIWindowLifecycle>();
                for (int i = 0; i < lifecycles.Length; i++)
                {
                    await lifecycles[i].OnOpeningAsync(args, LifetimeToken);
                }

                inst.SetActive(true);

                for (int i = 0; i < lifecycles.Length; i++)
                {
                    await lifecycles[i].OnOpenedAsync(LifetimeToken);
                }

                // Phase 3: register under lock (atomic add + pending removal)
                if (!await TryAcquireWindowLockAsync())
                {
                    if (inst != null) SafeDestroyUtility.SafeDestroy(inst);
                    return null;
                }
                try
                {
                    // E-5 fix: guard against a concurrent close that snuck in while we were instantiating
                    if (!inst) // GameObject was destroyed externally
                    {
                        _pendingOpenWindows.Remove(windowName);
                        SignalPendingChanged();
                        return null;
                    }
                    _activeWindows[windowName] = inst;
                    _windowHistory.Add(windowName);
                    _pendingOpenWindows.Remove(windowName);
                    SignalPendingChanged();
                    RefreshReadSnapshot();
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
                    SafeDestroyUtility.SafeDestroy(inst);
                }
                if (await TryAcquireWindowLockAsync())
                {
                    try { _pendingOpenWindows.Remove(windowName); SignalPendingChanged(); }
                    finally { _windowLock.Release(); }
                }
                return null;
            }
        }

        public void OpenWindow(string windowName, object args = null)
        {
            _ = SafeFireAndForget(OpenWindowAsync(windowName, UILayer.Screen, args), $"OpenWindow '{windowName}'");
        }

        public async Task CloseWindowAsync(string windowName)
        {
            GameObject go = null;
            if (!await TryAcquireWindowLockAsync()) return;
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
                if (keysToRemove.Count > 0)
                {
                    foreach (var key in keysToRemove)
                    {
                        _activeWindows.Remove(key);
                        _windowHistory.Remove(key);
                    }
                    RefreshReadSnapshot();
                }

                _activeWindows.TryGetValue(windowName, out go);
            }
            finally
            {
                _windowLock.Release();
            }

            if (go == null) return;
            await FinalizeClosedWindowAsync(windowName, go, alreadyRemoved: false);
        }

        public void CloseWindow(string windowName)
        {
            _ = SafeFireAndForget(CloseWindowAsync(windowName), $"CloseWindow '{windowName}'");
        }

        public async Task CloseTopWindowAsync()
        {
            if (!await TryAcquireWindowLockAsync()) return;
            string top = null;
            GameObject go = null;
            try
            {
                if (_windowHistory.Count == 0) return;
                top = _windowHistory[_windowHistory.Count - 1];
                _windowHistory.RemoveAt(_windowHistory.Count - 1);
                if (_activeWindows.TryGetValue(top, out go) && go != null)
                {
                    _activeWindows.Remove(top);
                    SignalPendingChanged();
                    RefreshReadSnapshot();
                    _canvas.UpdateLayerInteractivity(_activeWindows);
                }
            }
            finally
            {
                _windowLock.Release();
            }

            if (go == null) return;
            await FinalizeClosedWindowAsync(top, go, alreadyRemoved: true);
        }

        public void CloseTopWindow()
        {
            _ = SafeFireAndForget(CloseTopWindowAsync(), "CloseTopWindow");
        }

        public async Task CloseAllAsync()
        {
            List<string> windows;
            if (!await TryAcquireWindowLockAsync()) return;
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
            _ = SafeFireAndForget(CloseAllAsync(), "CloseAll");
        }

        /// <summary>
        /// Fire-and-forget helper that logs any exception which escapes the task.
        /// Replaces bare "_ = task" discards so exceptions are never silently swallowed.
        /// OperationCanceledException is suppressed — it is expected during context teardown.
        /// </summary>
        private static async System.Threading.Tasks.Task SafeFireAndForget(System.Threading.Tasks.Task task, string context)
        {
            try { await task; }
            catch (OperationCanceledException) { /* expected during context teardown */ }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[WindowManager] {context} failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task FinalizeClosedWindowAsync(string windowName, GameObject go, bool alreadyRemoved)
        {
            var lifecycles = go.GetComponents<IUIWindowLifecycle>();
            for (int i = 0; i < lifecycles.Length; i++)
            {
                try { await lifecycles[i].OnClosingAsync(LifetimeToken); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            for (int i = 0; i < lifecycles.Length; i++)
            {
                try { await lifecycles[i].OnClosedAsync(LifetimeToken); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            bool isSameObject = alreadyRemoved;
            if (!alreadyRemoved && await TryAcquireWindowLockAsync())
            {
                try
                {
                    if (_activeWindows.TryGetValue(windowName, out var current) && current == go)
                    {
                        _activeWindows.Remove(windowName);
                        _windowHistory.Remove(windowName);
                        SignalPendingChanged();
                        RefreshReadSnapshot();
                        _canvas.UpdateLayerInteractivity(_activeWindows);
                        isSameObject = true;
                    }
                }
                finally
                {
                    _windowLock.Release();
                }
            }

            if (isSameObject)
            {
                AssetProvider.ReleaseWindow(go);
            }
        }

        // A4b: lock-free reads from the volatile snapshot — never take the semaphore, so
        // these cannot block the main thread and cannot false-negative on a lock hold.
        public bool IsWindowOpen(string windowName)
        {
            if (string.IsNullOrEmpty(windowName)) return false;
            return _activeWindowsRead.ContainsKey(windowName);
        }

        public GameObject GetWindow(string windowName)
        {
            if (string.IsNullOrEmpty(windowName)) return null;
            _activeWindowsRead.TryGetValue(windowName, out var go);
            return go;
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
            // B3: bail out cleanly if the manager is being disposed (no ObjectDisposedException).
            if (_disposed || !_windowLock.Wait(50)) return result;
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
                if (_disposed || !_windowLock.Wait(50)) return 0;
                try { return _pendingOpenWindows.Count; }
                finally { _windowLock.Release(); }
            }
        }

        public override void Dispose()
        {
            // B3: mark disposed FIRST so in-flight async loops bail out, then wake any
            // pending waiters so they stop waiting instead of timing out for 30 s.
            _disposed = true;
            _pendingOpenWindows.Clear();
            SignalPendingChanged();

            // Destroy all active windows directly; lifecycle events are skipped during teardown
            foreach (var kvp in _activeWindows)
            {
                if (kvp.Value != null)
                    SafeDestroyUtility.SafeDestroy(kvp.Value);
            }
            _activeWindows.Clear();
            _windowHistory.Clear();
            _activeWindowsRead = new Dictionary<string, GameObject>();

            _canvas.Dispose();

            _windowLock.Dispose();
        }
    }
}
