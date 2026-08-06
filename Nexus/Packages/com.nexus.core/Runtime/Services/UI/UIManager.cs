using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>Fired by the UIManager when a screen fully opens.</summary>
    public readonly struct ScreenOpenedSignal
    {
        public readonly string ScreenName;
        public readonly object Args;
        public ScreenOpenedSignal(string screenName, object args) { ScreenName = screenName; Args = args; }
    }

    /// <summary>Fired by the UIManager when a screen fully closes.</summary>
    public readonly struct ScreenClosedSignal
    {
        public readonly string ScreenName;
        public ScreenClosedSignal(string screenName) { ScreenName = screenName; }
    }

    /// <summary>
    /// Type-safe Nexus UI screen manager. Owns the shared UI canvas hierarchy and layer roots
    /// (via <see cref="UICanvasSystem"/>) and opens/closes <see cref="ScreenView"/> prefabs by
    /// type. Screen views bind their mediators automatically through the standard ViewBinder
    /// pipeline — UIManager never touches mediators, it only handles instantiation, layering,
    /// pooling and lifecycle ordering.
    /// </summary>
    public interface IUIManager : INexusService
    {
        /// <summary>Root transform of the managed UI canvas.</summary>
        Transform CanvasRoot { get; }

        /// <summary>Number of screens currently open.</summary>
        int OpenScreenCount { get; }

        /// <summary>
        /// Opens a screen of type <typeparamref name="TScreen"/>. The screen prefab is resolved
        /// from the registered prefab map or through the <see cref="IUIAssetProvider"/> by the
        /// screen name. Returns the opened screen (or null if it could not be opened).
        /// </summary>
        Task<TScreen> OpenScreenAsync<TScreen>(object args = null, UILayer layer = UILayer.Screen, bool pooled = true)
            where TScreen : ScreenView;

        /// <summary>Fire-and-forget variant of <see cref="OpenScreenAsync{TScreen}"/>.</summary>
        void OpenScreen<TScreen>(object args = null, UILayer layer = UILayer.Screen) where TScreen : ScreenView;

        /// <summary>Closes the open screen of type <typeparamref name="TScreen"/> if present.</summary>
        Task CloseScreenAsync<TScreen>() where TScreen : ScreenView;

        /// <summary>Fire-and-forget variant of <see cref="CloseScreenAsync{TScreen}"/>.</summary>
        void CloseScreen<TScreen>() where TScreen : ScreenView;

        /// <summary>Closes the most recently opened screen.</summary>
        Task CloseTopScreenAsync();

        /// <summary>Closes all open screens.</summary>
        Task CloseAllAsync();

        /// <summary>Returns true if a screen of the given type is currently open.</summary>
        bool IsScreenOpen<TScreen>() where TScreen : ScreenView;

        /// <summary>Returns the open screen instance of the given type, or null.</summary>
        TScreen GetScreen<TScreen>() where TScreen : ScreenView;

        /// <summary>Registers a prefab so screens of this type can be opened without asset loading.</summary>
        void RegisterScreenPrefab<TScreen>(GameObject prefab) where TScreen : ScreenView;

        /// <summary>Unregisters a previously registered screen prefab.</summary>
        void UnregisterScreenPrefab<TScreen>() where TScreen : ScreenView;
    }

    /// <summary>
    /// Default <see cref="IUIManager"/> implementation.
    ///
    /// Performance notes:
    /// — Screens are pooled by default: closing deactivates the instance (SetActive(false))
    ///   instead of destroying it, and reopening reuses the pooled instance — including its
    ///   mediator, which the ViewBinder already pools.
    /// — Layer roots are created once by <see cref="UICanvasSystem"/> and reused.
    /// — Prefab registration avoids per-open asset lookups entirely.
    /// </summary>
    [Preserve]
    public class UIManager : NexusService<IUIManager>, IUIManager
    {
        [Inject] public IUIAssetProvider AssetProvider { get; set; }

        private readonly UICanvasSystem _canvas = new();
        private readonly Dictionary<string, ScreenView> _activeScreens = new();
        private readonly Dictionary<string, GameObject> _registeredPrefabs = new();
        private readonly Dictionary<string, Stack<ScreenView>> _pools = new();
        private readonly List<string> _history = new();

        // Per-screen cap on pooled (closed-but-retained) instances. Prevents a
        // screen toggled open/closed thousands of times from retaining one deactivated
        // instance per toggle.
        private const int MaxPooledPerScreenKey = 16;
        // Keys mid-open (awaiting instantiation) so concurrent opens of the same screen
        // cannot double-instantiate. Guarded by _lock.
        private readonly HashSet<string> _pendingOpens = new();
        private readonly object _lock = new();
        private volatile bool _disposed;

        /// <summary>Root transform of the managed UI canvas.</summary>
        public Transform CanvasRoot => _canvas.CanvasRoot;

        /// <summary>Number of screens currently open.</summary>
        public int OpenScreenCount
        {
            get { lock (_lock) return _activeScreens.Count; }
        }

        private CancellationToken LifetimeToken => Context?.LifetimeToken ?? CancellationToken.None;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (AssetProvider == null)
                AssetProvider = new ResourcesUIAssetProvider();
            _canvas.EnsureInitialized();
            return default;
        }

        // ── Prefab registration ───────────────────────────────────────

        public void RegisterScreenPrefab<TScreen>(GameObject prefab) where TScreen : ScreenView
        {
            if (prefab == null) return;
            string key = ScreenKey<TScreen>();
            lock (_lock)
            {
                _registeredPrefabs[key] = prefab;
            }
        }

        public void UnregisterScreenPrefab<TScreen>() where TScreen : ScreenView
        {
            lock (_lock)
            {
                _registeredPrefabs.Remove(ScreenKey<TScreen>());
            }
        }

        // ── Open ──────────────────────────────────────────────────────

        public async Task<TScreen> OpenScreenAsync<TScreen>(object args = null, UILayer layer = UILayer.Screen, bool pooled = true)
            where TScreen : ScreenView
        {
            if (_disposed) return null;

            string key = ScreenKey<TScreen>();
            Transform layerRoot = _canvas.GetLayerRoot(layer);

            // Atomically check active, pending, and reserve — all under a single lock
            // acquisition so no concurrent OpenScreenAsync can slip between the two checks.
            lock (_lock)
            {
                // 1. Already open → bring to front, refresh, return existing instance.
                if (_activeScreens.TryGetValue(key, out var existing) && existing != null)
                {
                    existing.transform.SetAsLastSibling();
                    _history.Remove(key);
                    _history.Add(key);
                    _canvas.UpdateLayerInteractivity(GetActiveGameObjects());
                    return (TScreen)existing;
                }

                // 2. Another call is already opening this screen — reject the duplicate.
                if (_pendingOpens.Contains(key))
                {
                    NexusRuntime.Logger?.LogWarning($"[UIManager] Screen '{key}' is already being opened; ignoring duplicate request.");
                    return null;
                }

                // 3. Reserve the key so concurrent calls cannot double-instantiate.
                _pendingOpens.Add(key);
            }

            try
            {
                // 2. Pool reuse (deactivated instance from a previous close).
                ScreenView screen = null;
                bool instantiated = false;
                if (pooled)
                {
                    lock (_lock)
                    {
                        if (_pools.TryGetValue(key, out var pool) && pool.Count > 0)
                            screen = pool.Pop();
                    }
                }

                if (screen == null)
                {
                    // 3. Instantiate a new instance under the target layer root.
                    screen = await InstantiateScreenAsync<TScreen>(key, layerRoot);
                    if (screen == null) return null;
                    instantiated = true;
                }
                else
                {
                    // Reparent pooled instance to the (possibly different) layer root.
                    screen.transform.SetParent(layerRoot, false);
                }

                // 4. Run the lifecycle. SetActive(true) is idempotent: for a freshly instantiated
                //    active prefab OnEnable already ran (view registered + mediator bound); for a
                //    pooled inactive instance this re-triggers OnEnable → re-register + rebind.
                try
                {
                    await screen.OnOpeningAsync(args, LifetimeToken);
                    screen.gameObject.SetActive(true);
                    await screen.OnOpenedAsync(LifetimeToken);
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogException(ex);
                    if (instantiated)
                    {
                        SafeDestroy(screen.gameObject);
                    }
                    else
                    {
                        ReturnToPool(key, screen);
                    }
                    return null;
                }

                // 5. Register as active.
                lock (_lock)
                {
                    if (!_activeScreens.ContainsKey(key))
                    {
                        _activeScreens[key] = screen;
                        _history.Remove(key);
                        _history.Add(key);
                        _activeGameObjectsDirty = true; // R2026-M3: invalidate cache
                    }
                    else if (instantiated)
                    {
                        // Defensive: reservation should prevent this, but if a pool was
                        // reused while an instance is active, destroy the duplicate.
                        NexusRuntime.Logger?.LogWarning($"[UIManager] Screen '{key}' already active; discarding duplicate instance.");
                        SafeDestroy(screen.gameObject);
                        return _activeScreens[key] as TScreen;
                    }
                }

                _canvas.UpdateLayerInteractivity(GetActiveGameObjects());
                SignalBus?.Fire(new ScreenOpenedSignal(key, args));
                return (TScreen)screen;
            }
            finally
            {
                // Release the reservation on every exit path (success, error, duplicate).
                lock (_lock)
                {
                    _pendingOpens.Remove(key);
                }
            }
        }

        public void OpenScreen<TScreen>(object args = null, UILayer layer = UILayer.Screen) where TScreen : ScreenView
        {
            SafeAsyncRunner.Run(OpenScreenAsync<TScreen>(args, layer), $"OpenScreen<{typeof(TScreen).Name}>");
        }

        private async Task<ScreenView> InstantiateScreenAsync<TScreen>(string key, Transform layerRoot) where TScreen : ScreenView
        {
            GameObject prefab = null;
            lock (_lock)
            {
                _registeredPrefabs.TryGetValue(key, out prefab);
            }

            GameObject instance;
            if (prefab != null)
            {
                instance = UnityEngine.Object.Instantiate(prefab, layerRoot);
            }
            else if (AssetProvider != null)
            {
                instance = await AssetProvider.InstantiateWindowAsync(key, layerRoot);
            }
            else
            {
                NexusRuntime.Logger?.LogError($"[UIManager] No prefab registered and no asset provider for screen '{key}'.");
                return null;
            }

            if (instance == null)
            {
                NexusRuntime.Logger?.LogError($"[UIManager] Failed to instantiate screen '{key}'.");
                return null;
            }

            var screen = instance.GetComponent<TScreen>();
            if (screen == null)
            {
                NexusRuntime.Logger?.LogError($"[UIManager] Prefab for '{key}' has no {typeof(TScreen).Name} component.");
                SafeDestroy(instance);
                return null;
            }
            return screen;
        }

        private void ReturnToPool(string key, ScreenView screen)
        {
            screen.gameObject.SetActive(false);
            lock (_lock)
            {
                if (!_pools.TryGetValue(key, out var pool))
                {
                    pool = new Stack<ScreenView>();
                    _pools[key] = pool;
                }
                if (pool.Count < MaxPooledPerScreenKey)
                    pool.Push(screen);
                else
                    SafeDestroy(screen.gameObject);
            }
        }

        // ── Close ─────────────────────────────────────────────────────

        public Task CloseScreenAsync<TScreen>() where TScreen : ScreenView
        {
            if (_disposed) return Task.CompletedTask;
            return CloseScreenCoreAsync(ScreenKey<TScreen>());
        }

        public void CloseScreen<TScreen>() where TScreen : ScreenView
        {
            SafeAsyncRunner.Run(CloseScreenAsync<TScreen>(), $"CloseScreen<{typeof(TScreen).Name}>");
        }

        public async Task CloseTopScreenAsync()
        {
            string top = null;
            lock (_lock)
            {
                if (_history.Count > 0)
                {
                    top = _history[_history.Count - 1];
                    _history.Remove(top);
                }
            }
            if (top == null) return;
            await CloseScreenCoreAsync(top);
        }

        public async Task CloseAllAsync()
        {
            string[] keys;
            lock (_lock)
            {
                keys = new string[_activeScreens.Count];
                _activeScreens.Keys.CopyTo(keys, 0);
            }
            foreach (var key in keys)
                await CloseScreenCoreAsync(key);
        }

        /// <summary>Closes the screen registered under <paramref name="key"/>, if any.</summary>
        private async Task CloseScreenCoreAsync(string key)
        {
            ScreenView screen;
            lock (_lock)
            {
                if (!_activeScreens.TryGetValue(key, out screen) || screen == null)
                    return;
            }

            try
            {
                await screen.OnClosingAsync(LifetimeToken);
                await screen.OnClosedAsync(LifetimeToken);
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogException(ex);
            }

            lock (_lock)
            {
                if (_activeScreens.TryGetValue(key, out var current) && ReferenceEquals(current, screen))
                {
                    _activeScreens.Remove(key);
                    _history.Remove(key);
                    _activeGameObjectsDirty = true; // R2026-M3: invalidate cache
                }
            }

            if (!_disposed)
            {
                screen.gameObject.SetActive(false);
                bool pooled = false;
                lock (_lock)
                {
                    if (!_pools.TryGetValue(key, out var pool))
                    {
                        pool = new Stack<ScreenView>();
                        _pools[key] = pool;
                    }
                    // Bound pool growth — overflow instances are destroyed, not retained.
                    if (pool.Count < MaxPooledPerScreenKey)
                    {
                        pool.Push(screen);
                        pooled = true;
                    }
                }
                if (!pooled)
                {
                    SafeDestroy(screen.gameObject);
                }
            }
            else
            {
                SafeDestroy(screen.gameObject);
            }

            _canvas.UpdateLayerInteractivity(GetActiveGameObjects());
            SignalBus?.Fire(new ScreenClosedSignal(key));
        }

        // ── Queries ───────────────────────────────────────────────────

        public bool IsScreenOpen<TScreen>() where TScreen : ScreenView
        {
            lock (_lock)
            {
                return _activeScreens.ContainsKey(ScreenKey<TScreen>());
            }
        }

        public TScreen GetScreen<TScreen>() where TScreen : ScreenView
        {
            lock (_lock)
            {
                return _activeScreens.TryGetValue(ScreenKey<TScreen>(), out var screen) ? screen as TScreen : null;
            }
        }

        // ── Editor introspection ────────────────────────────────────────

        /// <summary>Immutable description of one open screen for editor visibility.</summary>
        public readonly struct ScreenInfo
        {
            public readonly string Name;
            public readonly UILayer Layer;
            public readonly int HistoryOrder;
            public readonly bool IsAlive;
            public ScreenInfo(string name, UILayer layer, int historyOrder, bool isAlive)
            {
                Name = name; Layer = layer; HistoryOrder = historyOrder; IsAlive = isAlive;
            }
        }

        /// <summary>
        /// Non-blocking snapshot of currently open screens with their UI layer and open order.
        /// Read-only: never mutates manager state. Intended for editor tooling
        /// (CasualServicesPlugin's live screen stack). Returns an empty list while the
        /// manager is disposing or the lock is contended.
        /// </summary>
        public IReadOnlyList<ScreenInfo> GetOpenScreensSnapshot()
        {
            var result = new List<ScreenInfo>();
            if (_disposed || !Monitor.TryEnter(_lock)) return result;
            try
            {
                foreach (var kvp in _activeScreens)
                {
                    int order = _history.LastIndexOf(kvp.Key);
                    // Check aliveness FIRST: a Unity screen whose GameObject was destroyed
                    // externally compares == null while still being a tracked entry —
                    // dereferencing .gameObject on it would throw MissingReferenceException.
                    bool alive = kvp.Value != null;
                    UILayer layer = alive ? _canvas.ResolveLayer(kvp.Value.gameObject) : UILayer.Screen;
                    result.Add(new ScreenInfo(kvp.Key, layer, order, alive));
                }
            }
            finally
            {
                Monitor.Exit(_lock);
            }
            result.Sort((a, b) => a.HistoryOrder.CompareTo(b.HistoryOrder));
            return result;
        }

        /// <summary>Number of screens currently mid-open (awaiting instantiation).</summary>
        public int PendingScreenCount
        {
            get
            {
                if (_disposed || !Monitor.TryEnter(_lock)) return 0;
                try { return _pendingOpens.Count; }
                finally { Monitor.Exit(_lock); }
            }
        }

        // ── Internals ─────────────────────────────────────────────────

        // Dirty-flagged cache for the active-GameObjects view. Every
        // OpenScreenAsync/CloseScreenCoreAsync previously allocated a fresh Dictionary —
        // GC churn on UI-heavy flows. Invalidated by every _activeScreens mutation below.
        private Dictionary<string, GameObject> _cachedActiveGameObjects;
        private bool _activeGameObjectsDirty = true;

        /// <summary>Builds the GameObject view of active screens for layer interactivity.</summary>
        private Dictionary<string, GameObject> GetActiveGameObjects()
        {
            lock (_lock)
            {
                if (_activeGameObjectsDirty || _cachedActiveGameObjects == null)
                {
                    _cachedActiveGameObjects = new Dictionary<string, GameObject>(_activeScreens.Count);
                    foreach (var kvp in _activeScreens)
                        _cachedActiveGameObjects[kvp.Key] = kvp.Value.gameObject;
                    _activeGameObjectsDirty = false;
                }
                return _cachedActiveGameObjects;
            }
        }

        private static string ScreenKey<TScreen>() where TScreen : ScreenView
        {
            // Key by type name by default; screens may override ScreenName, but type-level
            // registration keeps the API type-safe and pool keys deterministic.
            return typeof(TScreen).Name;
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var toDestroy = new List<ScreenView>(_activeScreens.Count + 8);
            lock (_lock)
            {
                toDestroy.AddRange(_activeScreens.Values);
                foreach (var pool in _pools.Values)
                    toDestroy.AddRange(pool);
                _activeScreens.Clear();
                _history.Clear();
                _pools.Clear();
                _registeredPrefabs.Clear();
                _pendingOpens.Clear();
            }

            foreach (var screen in toDestroy)
            {
                if (screen != null)
                    SafeDestroy(screen.gameObject);
            }

            // NOTE: do not destroy the shared [Nexus_UICanvas] here. It is a DontDestroyOnLoad
            // singleton shared by every UI canvas owner across scene/context teardown;
            // destroying it from any single owner would break the next owner. Scene/context
            // teardown cleans it up.
            base.Dispose();
        }
    }
}
