using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// MonoBehaviour root for a Nexus context. Each Root creates and manages a <see cref="Context"/>.
    /// Supports hierarchical parent-child relationships and priority-based sibling initialization ordering.
    /// </summary>
    [DefaultExecutionOrder(-1000)] // Ensure Root starts before other scripts
    [Preserve]
    public class Root : MonoBehaviour
    {
        [Header("Hierarchy")]
#pragma warning disable 0649 // serialized fields: assigned by the Unity inspector
        [SerializeField] private Root parentRoot;

        [Header("Configuration")]
        [SerializeField] private ContextData contextData;
        [SerializeField] private int initializationPriority = 0;
        // Frame-count timeouts made the wait wall-clock dependent on the
        // device's frame rate (900 frames = 15 s @60 fps but 30 s @30 fps). Seconds are
        // frame-rate independent.
        // R2026-FIX (migration): FormerlySerializedAs was REMOVED deliberately — the old
        // int fields stored FRAMES (900), and Unity's int→float coercion would read that
        // as 900 SECONDS (15-minute waits → PlayMode tests appear to hang). Fresh field
        // names make existing scenes fall back to the 15 s default instead.
        [Tooltip("Seconds to wait for the parent root to initialize before failing.")]
        [SerializeField] private float parentTimeoutSeconds = 15f;
        [Tooltip("Seconds to wait for each higher-priority sibling root before failing.")]
        [SerializeField] private float siblingTimeoutSeconds = 15f;
#pragma warning restore 0649

        /// <summary>The Nexus context owned by this root.</summary>
        public Context Context { get; private set; }
        // Volatile: written on the main thread but polled by other Roots' startup loops,
        // which can resume on a worker thread when no SynchronizationContext is present.
        private volatile bool _isInitialized;
        /// <summary>True after async initialization (OnInitializeAsync + OnStartAsync) completes.</summary>
        public bool IsInitialized => _isInitialized;
        /// <summary>Priority for sibling sorting; higher values initialize earlier.</summary>
        public int InitializationPriority => initializationPriority;
        /// <summary>Configuration data for this context.</summary>
        public ContextData ContextData => contextData;
        /// <summary>Parent root in the context hierarchy (null for root contexts).</summary>
        public Root ParentRoot => parentRoot;

        // Lifecycle components discovered during InitializeContext, cached for Start().
        // This avoids the Dictionary key collision in BindInstance<IContextLifecycle> 
        // which would overwrite all but the last lifecycle.
        private IContextLifecycle[] _lifecycles = Array.Empty<IContextLifecycle>();

        // Lifecycles registered programmatically via RegisterLifecycle (instead of being
        // attached as components to this GameObject). Immutable after InitializeContext runs.
        private readonly List<IContextLifecycle> _registeredLifecycles = new();

        // Reusable list for sibling wait collection to avoid per-Start allocation.
        private readonly List<Root> _siblingsToWait = new();

        // Pending views registered before Context is initialized
        private readonly List<IView> _pendingViews = new();
        // Parallel HashSet so RegisterPendingView's dedup check is O(1)
        // instead of a linear List.Contains scan.
        private readonly HashSet<IView> _pendingViewsSet = new(ReferenceComparer<IView>.Instance);

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        // Main-thread id captured in Awake. async void Start() awaits user lifecycle code;
        // if a lifecycle implementation switches threads internally (ConfigureAwait(false),
        // Task.Run) and Unity's SynchronizationContext is absent (batch mode, headless
        // tests, or Start invoked off-thread), the continuation can resume on a worker
        // thread. We guard post-await state writes against that.
        private int _mainThreadId = -1;

        public void RegisterPendingView(IView view)
        {
            if (Context != null)
            {
                Context.RegisterView(view);
            }
            else
            {
                if (_pendingViewsSet.Add(view))
                {
                    _pendingViews.Add(view);
                }
            }
        }

        public void UnregisterPendingView(IView view)
        {
            _pendingViews.Remove(view);
            _pendingViewsSet.Remove(view);
        }

        // Registry to avoid FindObjectsByType in every Start()
        // HashSet gives O(1) Contains/Add/Remove (the old List made
        // OnEnable's dedup check O(N) per root). Iteration order is irrelevant: sibling
        // ordering is decided by priority + name comparison, never insertion order.
        private static readonly HashSet<Root> s_allRoots = new();
        private static readonly object s_rootLock = new();

        private void OnEnable()
        {
            lock (s_rootLock)
            {
                s_allRoots.Add(this); // HashSet.Add is idempotent — no Contains check needed
            }
        }

        private void OnDisable()
        {
            lock (s_rootLock)
            {
                s_allRoots.Remove(this);
            }
        }

        internal static void ClearRegistry()
        {
            lock (s_rootLock)
            {
                s_allRoots.Clear();
            }
        }

        private static void EnsureRegistry()
        {
            lock (s_rootLock)
            {
                // Purge destroyed native objects dynamically
                s_allRoots.RemoveWhere(r => r == null);
            }
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            if (parentRoot == this)
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] Auto-fixed circular reference on Root '{gameObject.name}': parentRoot was set to itself. Resetting parentRoot to null.");
                parentRoot = null;
            }
#endif
        }

        /// <summary>
        /// Runtime configuration entry point. MUST be called before the GameObject is
        /// activated (Awake) — the typical pattern is: create the GameObject inactive,
        /// AddComponent&lt;Root&gt;(), call SetUp(), then SetActive(true). Not needed when
        /// the serialized fields are assigned in the Inspector.
        /// </summary>
        public void SetUp(ContextData data, Root parent = null, int priority = 0)
        {
            if (Context != null)
            {
                throw new InvalidOperationException(
                    "[Nexus] Root.SetUp() called after Awake already created the context. " +
                    "Call SetUp() BEFORE activating the GameObject (SetActive(true)); " +
                    "otherwise the configuration is silently ignored.");
            }
            contextData = data;
            parentRoot = parent;
            initializationPriority = priority;
        }

        /// <summary>
        /// Registers a lifecycle instance programmatically (alternative to attaching an
        /// <see cref="IContextLifecycle"/> component to this GameObject). Must be called
        /// before the GameObject is activated, alongside <see cref="SetUp"/>. Supports
        /// multiple lifecycles: they run in registration order.
        /// </summary>
        public void RegisterLifecycle(IContextLifecycle lifecycle)
        {
            if (lifecycle == null) throw new ArgumentNullException(nameof(lifecycle));
            if (Context != null)
            {
                throw new InvalidOperationException(
                    "[Nexus] Root.RegisterLifecycle() called after Awake already consumed the " +
                    "registered lifecycles. Register lifecycles BEFORE activating the GameObject " +
                    "(SetActive(true)); otherwise the lifecycle is silently dropped.");
            }
            _registeredLifecycles.Add(lifecycle);
        }

        protected virtual void Awake()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureSupportComponents();
            InitializeContext();
        }

        private void EnsureSupportComponents()
        {
            // QueueDrainer and MetricsSampler are documented as living on the Root's
            // GameObject, but nothing ever added them for programmatically created Roots
            // (Dashboard "Create Root", AddComponent<Root>(), wizard scenes without the
            // hand-added components) — only the starter scene had them. Without
            // QueueDrainer the HybridQueue never drains, so queued signals
            // (FireThreadSafe/FireNextFrame) silently never run; without MetricsSampler
            // the game never records FPS/memory/GC, so the Performance Dashboard reads a
            // flat 0.0. Adding here covers every creation path at runtime; TryGetComponent
            // guards against double-add when a scene already carries them (REFACTOR PLAN
            // §1.7 — TryGetComponent avoids the null-check allocation of GetComponent).
            if (!gameObject.TryGetComponent<QueueDrainer>(out _))
                gameObject.AddComponent<QueueDrainer>();
            if (!gameObject.TryGetComponent<MetricsSampler>(out _))
                gameObject.AddComponent<MetricsSampler>();
        }

        private void InitializeContext()
        {
            if (Context != null) return;

            if (parentRoot != null && parentRoot != this)
            {
                parentRoot.InitializeContext();
            }

            Context parentContext = parentRoot != null ? parentRoot.Context : null;
            Context = new Context(parentContext, contextData);

            // Register any IContextLifecycle component on this GameObject
            // Also cache locally because NexusDI's Dictionary<Type, Binding> only stores one
            // binding per key — BindInstance<IContextLifecycle> would overwrite earlier entries.
            var componentLifecycles = GetComponents<IContextLifecycle>();
            if (componentLifecycles.Length > 0 || _registeredLifecycles.Count > 0)
            {
                _lifecycles = new IContextLifecycle[componentLifecycles.Length + _registeredLifecycles.Count];
                Array.Copy(componentLifecycles, _lifecycles, componentLifecycles.Length);
                for (int i = 0; i < _registeredLifecycles.Count; i++)
                    _lifecycles[componentLifecycles.Length + i] = _registeredLifecycles[i];
                _registeredLifecycles.Clear();
            }

            for (int i = 0; i < _lifecycles.Length; i++)
            {
                Context.Container.BindInstance(_lifecycles[i]);
            }

            Context.Configure(_lifecycles);

            // Flush pending views
            for (int i = 0; i < _pendingViews.Count; i++)
            {
                Context.RegisterView(_pendingViews[i]);
            }
            _pendingViews.Clear();
            _pendingViewsSet.Clear();
        }

        private async void Start()
        {
            try
            {
                await StartInternal();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected teardown signal (context
                // disposal cancels LifetimeToken), NOT a startup failure — logging it
                // as an error produced scary red noise on every scene unload.
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Root startup failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task StartInternal()
        {
            if (Context == null)
            {
                InitializeContext();
            }

            try
            {
                if (HasParentCycle())
                    throw new InvalidOperationException($"Root '{name}' has a parent cycle. Fix parentRoot references before starting the scene.");

                // Wait for parent root to be initialized first (with timeout).
                // Wall-clock seconds via realtimeSinceStartupAsDouble — a
                // frame-count timeout punished low-FPS devices with double-length waits.
                if (parentRoot != null)
                {
                    double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + parentTimeoutSeconds;
                    while (!parentRoot.IsInitialized && UnityEngine.Time.realtimeSinceStartupAsDouble < deadline)
                    {
                        await Task.Yield();
                        Context.LifetimeToken.ThrowIfCancellationRequested();
                    }

                    if (!parentRoot.IsInitialized)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Parent root '{parentRoot.name}' failed to initialize within {parentTimeoutSeconds:0.#}s. Continuing would leave dependent views and services in an undefined state.");
                        throw new TimeoutException($"Parent root '{parentRoot.name}' did not initialize in time.");
                    }
                }

                // Wait for sibling roots with higher priority to initialize (with timeout)
                EnsureRegistry();
                _siblingsToWait.Clear();
                // Snapshot under s_rootLock: every other s_allRoots access is locked, and a
                // concurrent OnEnable/OnDisable would otherwise mutate the set mid-enumeration.
                lock (s_rootLock)
                {
                    foreach (var r in s_allRoots)
                    {
                        if (r == null || r == this) continue;
                        if (r.parentRoot != this.parentRoot) continue;
                        if (!r.gameObject.activeInHierarchy || !r.enabled) continue;

                        bool runsBeforeUs = r.InitializationPriority > this.InitializationPriority;
                        if (r.InitializationPriority == this.InitializationPriority)
                        {
                            if (string.Compare(r.gameObject.name, this.gameObject.name, StringComparison.Ordinal) < 0)
                            {
                                runsBeforeUs = true;
                            }
                        }

                        if (runsBeforeUs)
                        {
                            _siblingsToWait.Add(r);
                        }
                    }
                }

                foreach (var sibling in _siblingsToWait)
                {
                    double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + siblingTimeoutSeconds;
                    while (sibling != null && !sibling.IsInitialized && UnityEngine.Time.realtimeSinceStartupAsDouble < deadline)
                    {
                        await Task.Yield();
                        Context.LifetimeToken.ThrowIfCancellationRequested();
                    }

                    if (sibling != null && !sibling.IsInitialized)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Root '{gameObject.name}' timed out after {siblingTimeoutSeconds:0.#}s waiting for sibling root '{sibling.gameObject.name}' to initialize. Continuing would make sibling ordering nondeterministic.");
                        throw new TimeoutException($"Sibling root '{sibling.gameObject.name}' did not initialize in time.");
                    }
                }

                await Context.InitializeLifecycleAsync(_lifecycles, Context.LifetimeToken);

                // Guard: user lifecycle code may have escaped the Unity main thread
                // (ConfigureAwait(false) / Task.Run) in environments without a
                // SynchronizationContext. Only the main thread may publish IsInitialized
                // and let dependent siblings/parents proceed — otherwise we dispose
                // deterministically instead of corrupting Unity state from a worker thread.
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                {
                    // Single log source: the catch below logs this exception with its
                    // stack trace, so no separate LogError here (avoids double logging).
                    throw new ThreadStateException(
                        $"[Nexus] Root '{gameObject.name}' lifecycle initialization resumed off the main thread (worker id {Thread.CurrentThread.ManagedThreadId}). " +
                        "A lifecycle implementation likely called ConfigureAwait(false) or Task.Run without marshalling back to the main thread.");
                }

                _isInitialized = true;

                // The last active Root to finish startup runs the cross-context phase.
                // Per-context idempotency makes this safe when scenes add another Root later:
                // existing contexts are skipped and only newly configured ones participate.
                if (AreAllActiveRootsInitialized())
                    await NexusRuntime.FinalizeInitializationAsync(Context.LifetimeToken);
            }
            catch (OperationCanceledException)
            {
                // Cancelled, dispose context safely
                if (Context != null)
                {
                    Context.Dispose();
                    Context = null;
                }
                _isInitialized = false;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Root initialization failed: {ex.Message}\n{ex.StackTrace}");
                if (Context != null)
                {
                    Context.Dispose();
                    Context = null;
                }
                _isInitialized = false;
            }
        }

        private static bool AreAllActiveRootsInitialized()
        {
            lock (s_rootLock)
            {
                foreach (var root in s_allRoots)
                {
                    if (root == null || !root.enabled || !root.gameObject.activeInHierarchy) continue;
                    if (!root.IsInitialized) return false;
                }
                return true;
            }
        }

        private bool HasParentCycle()
        {
            var visited = new List<Root>();
            var cursor = this;
            while (cursor != null)
            {
                for (int i = 0; i < visited.Count; i++)
                    if (ReferenceEquals(visited[i], cursor)) return true;
                visited.Add(cursor);
                cursor = cursor.parentRoot;
            }
            return false;
        }

        // Queue draining and metrics sampling are handled by <see cref="QueueDrainer"/>
        // and <see cref="MetricsSampler"/> MonoBehaviours on the same GameObject.
        // Root focuses solely on context lifecycle.

        private void OnDestroy()
        {
            lock (s_rootLock)
            {
                s_allRoots.Remove(this);
            }

            if (Context != null)
            {
                Context.Dispose();
                Context = null;
            }
            _isInitialized = false;
        }
    }
}
