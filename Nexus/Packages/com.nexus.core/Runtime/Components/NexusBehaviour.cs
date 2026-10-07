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
        [Tooltip("If true, dependencies marked with [Inject] on this component are injected once the owning context finishes configuring.")]
        [SerializeField] private bool _autoInject = true;

        private IContext _cachedContext;
        private List<IDisposable> _subscriptions;
        private bool _explicitContext;
        private bool _lifecycleRequested;
        private bool _awakeInvoked;
        private bool _startRequested;
        private bool _startInvoked;
        private bool _destroyed;
        private bool _initializing;
        private bool _retryInitialization;
        private bool _injectionStarted;
        private bool _releasingContext;
        private int _attachmentVersion;

        /// <summary>Whether this component automatically runs dependency injection after context configuration.</summary>
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
                if (ContextAvailability.IsAlive(_cachedContext)) return _cachedContext;
                if (_cachedContext != null) ReleaseContext();
                if (!_explicitContext) _cachedContext = ContextAvailability.Find(this);
                return _cachedContext;
            }
            set
            {
                if (ReferenceEquals(_cachedContext, value) && _explicitContext == (value != null)) return;
                ReleaseContext();
                _explicitContext = value != null;
                _cachedContext = value;
                if (_lifecycleRequested && !_destroyed) TryInitialize();
            }
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
            if (_destroyed) return;
            if (!_lifecycleRequested)
            {
                _lifecycleRequested = true;
                NexusRuntime.OnContextConfigured += OnContextAvailable;
                NexusRuntime.OnContextInitialized += OnContextAvailable;
                NexusRuntime.OnContextUnregistered += OnContextLost;
            }
            TryInitialize();
        }

        /// <summary>
        /// Explicit lifecycle teardown helper for unit tests or programmatic teardown outside PlayMode.
        /// In PlayMode, Unity calls OnDestroy automatically.
        /// </summary>
        public void DestroyLifecycle()
        {
            if (_destroyed) return;
            _destroyed = true;
            NexusRuntime.OnContextConfigured -= OnContextAvailable;
            NexusRuntime.OnContextInitialized -= OnContextAvailable;
            NexusRuntime.OnContextUnregistered -= OnContextLost;
            try
            {
                OnNexusDestroy();
            }
            finally
            {
                ReleaseContext();
            }
        }

        protected virtual void Awake()
        {
            InitializeLifecycle();
        }

        protected virtual void Start()
        {
            _startRequested = true;
            TryInitialize();
        }

        protected virtual void OnDestroy()
        {
            DestroyLifecycle();
        }

        /// <summary>Invoked once per context attachment after binding configuration and auto-injection.</summary>
        protected virtual void OnNexusAwake() { }

        /// <summary>Invoked after Unity Start and asynchronous context startup have both completed.</summary>
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

        private void OnContextAvailable(IContext context) => TryInitialize();

        private void OnContextLost(IContext context)
        {
            if (ReferenceEquals(_cachedContext, context)) ReleaseContext();
        }

        private void TryInitialize()
        {
            if (_destroyed) return;
            if (_initializing || _releasingContext) { _retryInitialization = true; return; }
            _initializing = true;
            try
            {
                // Nested ready notifications must not re-enter injection. Attachment
                // changes made by user hooks are retried after the old stack unwinds.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    _retryInitialization = false;
                    TryInitializeAttachment();
                    if (!_retryInitialization || _destroyed) return;
                }
                throw new InvalidOperationException("NexusBehaviour context changes recursively during initialization. Keep initialization hooks stable.");
            }
            finally { _initializing = false; }
        }

        private void TryInitializeAttachment()
        {
            var context = Context;
            if (!ContextAvailability.CanInject(context)) return;
            int version = _attachmentVersion;
            if (!_awakeInvoked)
            {
                if (_autoInject)
                {
                    _injectionStarted = true;
                    context.Container.Inject(this);
                    if (!IsCurrentAttachment(context, version))
                    {
                        // A setter/PostConstruct callback may switch ownership midway
                        // through Inject; remove any references the stale tail assigned.
                        NexusDI.ClearInjectedReferences(this);
                        return;
                    }
                }
                _awakeInvoked = true;
                OnNexusAwake();
                if (!IsCurrentAttachment(context, version)) return;
            }
            if (_startRequested && !_startInvoked &&
                (!(context is Context owned) || owned.IsInitialized))
            {
                _startInvoked = true;
                OnNexusStart();
            }
        }

        private bool IsCurrentAttachment(IContext context, int version) =>
            !_destroyed && version == _attachmentVersion &&
            ReferenceEquals(_cachedContext, context) && ContextAvailability.IsAlive(context);

        private void ReleaseContext()
        {
            _attachmentVersion++;
            bool clearReferences = _injectionStarted;
            _injectionStarted = false;
            _cachedContext = null;
            _awakeInvoked = false;
            _startInvoked = false;
            bool alreadyReleasing = _releasingContext;
            _releasingContext = true;
            try
            {
                DisposeSubscriptions();
                if (clearReferences) NexusDI.ClearInjectedReferences(this);
            }
            finally { _releasingContext = alreadyReleasing; }
        }
    }
}
