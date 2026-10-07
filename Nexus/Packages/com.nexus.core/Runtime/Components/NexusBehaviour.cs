using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Lightweight, productive MonoBehaviour base class for rapid development and clean MVCS integration.
    /// Provides zero-boilerplate access to SignalBus, DI Resolution, Injection, and auto-cleaned Event Subscriptions.
    /// Designed for developers of all skill levels across all game genres.
    /// </summary>
    public abstract class NexusBehaviour : MonoBehaviour
    {
        [Header("Nexus Options")]
        [Tooltip("If true, dependencies marked with [Inject] on this component are automatically injected on Awake.")]
        [SerializeField] private bool _autoInject = true;

        private IContext _cachedContext;
        private List<IDisposable> _subscriptions;

        /// <summary>Whether this component automatically runs dependency injection on Awake.</summary>
        public bool AutoInject
        {
            get => _autoInject;
            set => _autoInject = value;
        }

        /// <summary>
        /// Gets the active Nexus context associated with this GameObject hierarchy,
        /// or the global/default runtime context.
        /// </summary>
        public IContext Context
        {
            get
            {
                if (_cachedContext != null) return _cachedContext;
                _cachedContext = FindActiveContext();
                return _cachedContext;
            }
            set => _cachedContext = value;
        }

        /// <summary>
        /// Dispatches a signal to the Nexus SignalBus with zero GC allocation on the hot path.
        /// </summary>
        public void Fire<T>(T signal) where T : struct
        {
            var ctx = Context;
            if (ctx?.SignalBus != null)
            {
                ctx.SignalBus.Fire(signal);
            }
            else
            {
                NexusRuntime.Logger?.LogWarning($"[NexusBehaviour] Cannot Fire<{typeof(T).Name}>: No active Nexus Context found.");
            }
        }

        /// <summary>
        /// Subscribes to a signal on the Nexus SignalBus. The subscription is automatically
        /// tracked and disposed when this GameObject or component is destroyed (no memory leaks).
        /// </summary>
        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            var ctx = Context;
            if (ctx?.SignalBus == null)
            {
                NexusRuntime.Logger?.LogWarning($"[NexusBehaviour] Cannot Subscribe<{typeof(T).Name}>: No active Nexus Context found.");
                return null;
            }

            var sub = ctx.SignalBus.Subscribe(handler);
            if (sub != null)
            {
                _subscriptions ??= new List<IDisposable>(4);
                _subscriptions.Add(sub);
            }
            return sub;
        }

        /// <summary>
        /// Resolves a registered service or singleton contract from the active Nexus DI Container.
        /// </summary>
        public T Resolve<T>() where T : class
        {
            var ctx = Context;
            if (ctx == null)
            {
                NexusRuntime.Logger?.LogError($"[NexusBehaviour] Cannot Resolve<{typeof(T).Name}>: No active Nexus Context found.");
                return null;
            }
            return ctx.Resolve<T>();
        }

        /// <summary>
        /// Safely attempts to resolve a registered service or singleton contract from the active Nexus DI Container.
        /// </summary>
        public bool TryResolve<T>(out T service) where T : class
        {
            var ctx = Context;
            if (ctx != null && ctx.Container != null && ctx.Container.IsRegistered(typeof(T)))
            {
                service = ctx.Resolve<T>();
                return service != null;
            }
            service = null;
            return false;
        }

        /// <summary>
        /// Explicitly injects dependencies into this component or a target object.
        /// </summary>
        public void Inject(object target = null)
        {
            var ctx = Context;
            if (ctx?.Container != null)
            {
                ctx.Container.Inject(target ?? this);
            }
        }

        /// <summary>
        /// Explicit lifecycle initialization helper for unit tests or programmatic setup outside PlayMode.
        /// In PlayMode, Unity calls Awake automatically.
        /// </summary>
        public void InitializeLifecycle()
        {
            if (_autoInject)
            {
                Inject(this);
            }
            OnNexusAwake();
        }

        /// <summary>
        /// Explicit lifecycle teardown helper for unit tests or programmatic teardown outside PlayMode.
        /// In PlayMode, Unity calls OnDestroy automatically.
        /// </summary>
        public void DestroyLifecycle()
        {
            try
            {
                OnNexusDestroy();
            }
            finally
            {
                DisposeSubscriptions();
            }
        }

        protected virtual void Awake()
        {
            InitializeLifecycle();
        }

        protected virtual void Start()
        {
            OnNexusStart();
        }

        protected virtual void OnDestroy()
        {
            DestroyLifecycle();
        }

        /// <summary>Virtual lifecycle hook invoked during Awake after auto-injection.</summary>
        protected virtual void OnNexusAwake() { }

        /// <summary>Virtual lifecycle hook invoked during Start.</summary>
        protected virtual void OnNexusStart() { }

        /// <summary>Virtual lifecycle hook invoked during OnDestroy before subscriptions are cleared.</summary>
        protected virtual void OnNexusDestroy() { }

        private void DisposeSubscriptions()
        {
            if (_subscriptions != null)
            {
                for (int i = _subscriptions.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _subscriptions[i]?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogWarning($"[NexusBehaviour] Error disposing subscription: {ex.Message}");
                    }
                }
                _subscriptions.Clear();
            }
        }

        private IContext FindActiveContext()
        {
            // 1. Try finding parent Root in hierarchy
            var parentRoot = GetComponentInParent<Root>();
            if (parentRoot != null && parentRoot.Context != null)
            {
                return parentRoot.Context;
            }

            // 2. Try global project context across scenes
            if (NexusRuntime.GlobalContext != null)
            {
                return NexusRuntime.GlobalContext;
            }

            // 3. Fall back to active default context in NexusRuntime
            return NexusRuntime.GetDefaultContext();
        }
    }
}
