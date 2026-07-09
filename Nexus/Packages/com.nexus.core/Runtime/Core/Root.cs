using System;
using System.Collections;
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
            lock (s_allRoots)
            {
                s_allRoots.Clear();
            }
            s_registryDirty = true;
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
                NexusRuntime.CurrentContext?.Resolve<Nexus.Core.Services.ILoggerService>()?.LogWarning($"[Nexus] Auto-fixed circular reference on Root '{gameObject.name}': parentRoot was set to itself. Resetting parentRoot to null.");
                parentRoot = null;
            }
#endif
        }

        private void Awake()
        {
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
                        NexusRuntime.CurrentContext?.Resolve<Nexus.Core.Services.ILoggerService>()?.LogError($"[Nexus] Parent root '{parentRoot.name}' failed to initialize within timeout. Continuing would leave dependent views and services in an undefined state.");
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
                        NexusRuntime.CurrentContext?.Resolve<Nexus.Core.Services.ILoggerService>()?.LogError($"[Nexus] Root '{gameObject.name}' timed out waiting for sibling root '{sibling.gameObject.name}' to initialize. Continuing would make sibling ordering nondeterministic.");
                        throw new TimeoutException($"Sibling root '{sibling.gameObject.name}' did not initialize in time.");
                    }
                }

                // Initialize reactive models (IReactiveModel.OnBind) after configuration
                await Context.InitializeReactiveModelsAsync(Context.LifetimeToken);
                
                // Initialize services (INexusService.InitializeAsync)
                await Context.InitializeServicesAsync(Context.LifetimeToken);

                // Run all registered lifecycles. We iterate the cached _lifecycles array
                // instead of resolving from DI because NexusDI stores only one binding per type.
                for (int i = 0; i < _lifecycles.Length; i++)
                {
                    await _lifecycles[i].OnInitializeAsync(Context.LifetimeToken);
                }
                for (int i = 0; i < _lifecycles.Length; i++)
                {
                    await _lifecycles[i].OnStartAsync(Context.LifetimeToken);
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
                NexusRuntime.CurrentContext?.Resolve<Nexus.Core.Services.ILoggerService>()?.LogError($"[Nexus] Root initialization failed: {ex.Message}\n{ex.StackTrace}");
                if (Context != null)
                {
                    Context.Dispose();
                    Context = null;
                }
                IsInitialized = false;
            }
        }

        private void Update()
        {
            if (Context != null && IsInitialized)
            {
                Context.HybridQueue.DrainThreadSafe();
            }

            // Update performance metrics
            PerformanceMonitor.UpdateFrameMetrics();
        }

        private void LateUpdate()
        {
            if (Context != null && IsInitialized)
            {
                Context.HybridQueue.DrainNextFrame();
            }

            // Update memory and GC metrics every 60 frames (approximately 1 second)
            if (Time.frameCount % 60 == 0)
            {
                PerformanceMonitor.UpdateMemoryMetrics();
                PerformanceMonitor.UpdateGCMetrics();
            }
        }

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
