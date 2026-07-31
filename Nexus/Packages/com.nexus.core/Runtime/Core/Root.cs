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
        [SerializeField] private Root parentRoot;

        [Header("Configuration")]
        [SerializeField] private ContextData contextData;
        [SerializeField] private int initializationPriority = 0;
        [SerializeField] private int parentTimeoutFrames = 900;
        [SerializeField] private int siblingTimeoutFrames = 900;

        /// <summary>The Nexus context owned by this root.</summary>
        public Context Context { get; private set; }
        /// <summary>True after async initialization (OnInitializeAsync + OnStartAsync) completes.</summary>
        public bool IsInitialized { get; private set; }
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

        // Reusable list for sibling wait collection to avoid per-Start allocation.
        private readonly List<Root> _siblingsToWait = new();

        // Pending views registered before Context is initialized
        private readonly List<IView> _pendingViews = new();

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
                if (!_pendingViews.Contains(view))
                {
                    _pendingViews.Add(view);
                }
            }
        }

        public void UnregisterPendingView(IView view)
        {
            _pendingViews.Remove(view);
        }

        // Registry to avoid FindObjectsByType in every Start()
        private static readonly List<Root> s_allRoots = new();
        private static readonly object s_rootLock = new();
        private static bool s_registryDirty = true;

        private void OnEnable()
        {
            lock (s_rootLock)
            {
                s_registryDirty = true;
            }
        }

        private void OnDisable()
        {
            lock (s_rootLock)
            {
                s_registryDirty = true;
            }
        }

        internal static void ClearRegistry()
        {
            // P0-8 fix: use the same lock object (s_rootLock) as OnEnable/OnDisable/EnsureRegistry,
            // and set the dirty flag inside the lock.
            lock (s_rootLock)
            {
                s_allRoots.Clear();
                s_registryDirty = true;
            }
        }

        private static void EnsureRegistry()
        {
            if (!s_registryDirty) return;
            lock (s_rootLock)
            {
                if (!s_registryDirty) return;
                s_allRoots.Clear();
                s_allRoots.AddRange(FindObjectsByType<Root>(FindObjectsInactive.Exclude));
                s_registryDirty = false;
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

        private void Awake()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            InitializeContext();
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
            _lifecycles = GetComponents<IContextLifecycle>();
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
        }

        private async void Start()
        {
            if (Context == null)
            {
                InitializeContext();
            }

            try
            {
                // Wait for parent root to be initialized first (with timeout)
                if (parentRoot != null)
                {
                    int timeoutFrames = parentTimeoutFrames;
                    while (!parentRoot.IsInitialized && timeoutFrames > 0)
                    {
                        await Task.Yield();
                        Context.LifetimeToken.ThrowIfCancellationRequested();
                        timeoutFrames--;
                    }

                    if (!parentRoot.IsInitialized)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Parent root '{parentRoot.name}' failed to initialize within timeout. Continuing would leave dependent views and services in an undefined state.");
                        throw new TimeoutException($"Parent root '{parentRoot.name}' did not initialize in time.");
                    }
                }

                // Wait for sibling roots with higher priority to initialize (with timeout)
                EnsureRegistry();
                _siblingsToWait.Clear();
                foreach (var r in s_allRoots)
                {
                    if (r == this) continue;
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

                foreach (var sibling in _siblingsToWait)
                {
                    int timeoutFrames = siblingTimeoutFrames;
                    while (sibling != null && !sibling.IsInitialized && timeoutFrames > 0)
                    {
                        await Task.Yield();
                        Context.LifetimeToken.ThrowIfCancellationRequested();
                        timeoutFrames--;
                    }

                    if (sibling != null && !sibling.IsInitialized)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Root '{gameObject.name}' timed out waiting for sibling root '{sibling.gameObject.name}' to initialize. Continuing would make sibling ordering nondeterministic.");
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

                IsInitialized = true;
            }
            catch (OperationCanceledException)
            {
                // Cancelled, dispose context safely
                if (Context != null)
                {
                    Context.Dispose();
                    Context = null;
                }
                IsInitialized = false;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Root initialization failed: {ex.Message}\n{ex.StackTrace}");
                if (Context != null)
                {
                    Context.Dispose();
                    Context = null;
                }
                IsInitialized = false;
            }
        }

        // Queue draining and metrics sampling are handled by <see cref="QueueDrainer"/>
        // and <see cref="MetricsSampler"/> MonoBehaviours on the same GameObject.
        // Root focuses solely on context lifecycle.

        private void OnDestroy()
        {
            if (Context != null)
            {
                Context.Dispose();
                Context = null;
            }
            IsInitialized = false;
        }
    }
}
