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

        // Registry to avoid FindObjectsByType in every Start()
        private static readonly List<Root> s_allRoots = new();
        private static bool s_registryDirty = true;

        private void OnEnable()
        {
            s_registryDirty = true;
        }

        private void OnDisable()
        {
            s_registryDirty = true;
            s_allRoots.Clear();
        }

        private static void EnsureRegistry()
        {
            if (!s_registryDirty) return;
            s_allRoots.Clear();
            s_allRoots.AddRange(FindObjectsByType<Root>(FindObjectsInactive.Exclude));
            s_registryDirty = false;
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            if (parentRoot == this)
            {
                Debug.LogWarning($"[Nexus] Auto-fixed circular reference on Root '{gameObject.name}': parentRoot was set to itself. Resetting parentRoot to null.");
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
            var lifecycles = GetComponents<IContextLifecycle>();
            foreach (var lifecycle in lifecycles)
            {
                Context.Container.BindInstance(lifecycle);
            }

            Context.Configure();
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
                    int timeoutFrames = 900; // ~15 seconds at 60fps
                    while (!parentRoot.IsInitialized && timeoutFrames > 0)
                    {
                        await Awaitable.NextFrameAsync(Context.LifetimeToken);
                        timeoutFrames--;
                    }

                    if (!parentRoot.IsInitialized)
                    {
                        Debug.LogWarning($"[Nexus] Parent root '{parentRoot.name}' failed to initialize within timeout. Proceeding independently.");
                    }
                }

                // Wait for sibling roots with higher priority to initialize (with timeout)
                EnsureRegistry();
                var siblingsToWait = new List<Root>();
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
                        siblingsToWait.Add(r);
                    }
                }

                foreach (var sibling in siblingsToWait)
                {
                    int timeoutFrames = 900;
                    while (sibling != null && !sibling.IsInitialized && timeoutFrames > 0)
                    {
                        await Awaitable.NextFrameAsync(Context.LifetimeToken);
                        timeoutFrames--;
                    }

                    if (sibling != null && !sibling.IsInitialized)
                    {
                        Debug.LogWarning($"[Nexus] Root '{gameObject.name}' timed out waiting for sibling root '{sibling.gameObject.name}' to initialize. Proceeding independently.");
                    }
                }

                // Initialize reactive models (IReactiveModel.OnBind) after configuration
                await Context.InitializeReactiveModelsAsync(Context.LifetimeToken);
                
                // Initialize services (INexusService.InitializeAsync)
                await Context.InitializeServicesAsync(Context.LifetimeToken);

                if (Context.Container.IsRegistered(typeof(IContextLifecycle)))
                {
                    var lifecycle = Context.Container.Resolve<IContextLifecycle>();
                    
                    // Asynchronous initialization phase
                    await lifecycle.OnInitializeAsync(Context.LifetimeToken);
                    
                    // Asynchronous start phase
                    await lifecycle.OnStartAsync(Context.LifetimeToken);
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
                Debug.LogError($"[Nexus] Root initialization failed: {ex.Message}\n{ex.StackTrace}");
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
        }

        private void LateUpdate()
        {
            if (Context != null && IsInitialized)
            {
                Context.HybridQueue.DrainNextFrame();
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
