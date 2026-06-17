using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [DefaultExecutionOrder(-1000)] // Ensure Root starts before other scripts
    [Preserve]
    public class Root : MonoBehaviour
    {
        [Header("Hierarchy")]
        [SerializeField] private Root parentRoot;

        [Header("Configuration")]
        [SerializeField] private ContextData contextData;

        public Context Context { get; private set; }
        public bool IsInitialized { get; private set; }

        private void Awake()
        {
            InitializeContext();
        }

        private void InitializeContext()
        {
            if (Context != null) return;

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
                // Wait for parent root to be initialized first
                if (parentRoot != null)
                {
                    while (!parentRoot.IsInitialized)
                    {
                        // Yield to next frame using Awaitable
                        await Awaitable.NextFrameAsync(Context.LifetimeToken);
                    }
                }

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
                Context.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Root initialization failed: {ex.Message}\n{ex.StackTrace}");
                Context.Dispose();
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
