using System;
using System.Collections.Generic;
using System.Reflection;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Abstract base class for Nexus views.
    /// Views register with the nearest <see cref="Root"/> context when enabled,
    /// then receive <see cref="Bind"/> before their mediator is attached.
    /// </summary>
    [Preserve]
    public abstract class View : MonoBehaviour, IView
    {
        /// <summary>The context this view is bound to. Set after <see cref="Bind"/> is called.</summary>
        protected IContext Context { get; private set; }
        private bool _isBound;

        private void Awake()
        {
            // Plan §2.3: Context/SignalBus access is forbidden in Awake.
            // View has not been registered yet and Mediator has not been connected.
        }

        private Root _pendingRoot;

        /// <summary>
        /// Automatically registers this view with the nearest parent Root's context.
        /// Falls back to FindObjectsByType only when the scene has one unambiguous Root.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (_isBound) return;
            var root = GetComponentInParent<Root>();
            if (root != null)
            {
                if (root.Context != null)
                {
                    root.Context.RegisterView(this);
                }
                else
                {
                    _pendingRoot = root;
                    root.RegisterPendingView(this);
                }
                return;
            }

            // Fallback only when the scene has a single unambiguous active root.
            var roots = FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            if (roots.Length == 1)
            {
                var singleRoot = roots[0];
                if (singleRoot.Context != null)
                {
                    singleRoot.Context.RegisterView(this);
                }
                else
                {
                    _pendingRoot = singleRoot;
                    singleRoot.RegisterPendingView(this);
                }
            }
            else if (roots.Length > 1)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] View '{gameObject.name}' OnEnable: Multiple Root instances found. " +
                    "Auto-binding is ambiguous; keep a single active Root per scene or the view may bind to the wrong context.");
            }
            else if (roots.Length == 0)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] View '{gameObject.name}' OnEnable: No Root GameObject found in scene. " +
                    "Create a Root via GameObject → Nexus → Create Root.");
            }
        }

        /// <summary>
        /// Unregisters this view from its context when disabled.
        /// Errors during unbinding are caught and logged to avoid breaking scene teardown.
        /// </summary>
        protected virtual void OnDisable()
        {
            try
            {
                if (_pendingRoot != null)
                {
                    _pendingRoot.UnregisterPendingView(this);
                    _pendingRoot = null;
                }
                if (Context != null)
                {
                    Context.UnregisterView(this);
                }
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] View '{gameObject.name}' failed to unbind on disable: {ex.Message}");
            }
        }

        /// <summary>Binds the view to a context. Calls <see cref="OnBind"/> for derived setup.</summary>
        /// <param name="context">The context to bind to.</param>
        public void Bind(IContext context)
        {
            if (_isBound) return;
            _isBound = true;
            Context = context;
            _pendingRoot = null;
            OnBind(context);
        }

        /// <summary>Unbinds the view from its context. Calls <see cref="OnUnbind"/> for derived cleanup.</summary>
        public void Unbind()
        {
            OnUnbind();
            _isBound = false;
            Context = null;
        }

        /// <summary>Override to perform custom setup when the view is bound to a context.</summary>
        /// <param name="context">The context this view is now bound to.</param>
        protected virtual void OnBind(IContext context) { }
        /// <summary>Override to perform custom cleanup when the view is unbound.</summary>
        protected virtual void OnUnbind() { }
    }

    /// <summary>
    /// Manages view registration, mediator instantiation/pooling, and cleanup.
    /// Connects views to their associated <see cref="Mediator{TView}"/> via <see cref="MediatorAttribute"/>.
    /// </summary>
    [Preserve]
    public class ViewBinder : IDisposable
    {
        private readonly IContext _context;
        private readonly NexusDI _container;
        private readonly Dictionary<IView, IMediator> _activeMediators = new();
        private readonly Dictionary<Type, Stack<IMediator>> _mediatorPools = new();

        private readonly int _maxMediatorPoolSize = 64;

        /// <summary>Creates a new <see cref="ViewBinder"/> for the given context.</summary>
        /// <param name="context">The context that owns this binder.</param>
        /// <param name="container">The DI container for resolving mediators.</param>
        /// <param name="maxMediatorPoolSize">Maximum number of mediator instances to pool per type (default: 64).</param>
        public ViewBinder(IContext context, NexusDI container, int maxMediatorPoolSize = 64)
        {
            _context = context;
            _container = container;
            _maxMediatorPoolSize = maxMediatorPoolSize;
        }

        /// <summary>
        /// Registers a view, binds it to the context, then creates and attaches its mediator.
        /// Binding always happens before mediator setup so the view has a valid context first.
        /// </summary>
        /// <param name="view">The view to register.</param>
        public void RegisterView(IView view)
        {
            if (view == null) return;
            if (_activeMediators.ContainsKey(view)) return;

            if (_context == null)
            {
                var logger = NexusRuntime.Logger;
                logger?.LogError($"[Nexus] RegisterView failed for '{view.GetType().Name}': context is null.");
                return;
            }

            var mediatorAttr = view.GetType().GetCustomAttribute<MediatorAttribute>();
            if (mediatorAttr == null)
            {
                NexusRuntime.Logger?.Log($"[Nexus] View '{view.GetType().Name}' has no MediatorAttribute. Binding only the context.");
                _container.Inject(view);
                view.Bind(_context);
                return;
            }

            // Inject BEFORE Bind so that [Inject] properties (e.g. TickService)
            // are available when OnBind() runs. TickableView.OnBind() needs TickService
            // to register for tick callbacks — if injected after Bind, it's null.
            _container.Inject(view);
            view.Bind(_context);

            var mediatorType = mediatorAttr.MediatorType;
            var mediator = GetMediator(mediatorType);

            _activeMediators[view] = mediator;
            
            // Mediator attaches after the view is already bound to the context.
            mediator.Bind(view, _context.SignalBus);
        }

        /// <summary>Unregisters a view, unbinds its mediator, and returns the mediator to the pool.</summary>
        /// <param name="view">The view to unregister.</param>
        public void UnregisterView(IView view)
        {
            if (view == null) return;
            view.Unbind();

            if (_activeMediators.Remove(view, out var mediator))
            {
                mediator.Unbind();
                ReturnMediator(mediator);
            }
        }

        private IMediator GetMediator(Type mediatorType)
        {
            if (!_mediatorPools.TryGetValue(mediatorType, out var pool))
            {
                pool = new Stack<IMediator>();
                _mediatorPools[mediatorType] = pool;
            }

            if (pool.Count > 0)
            {
                var mediator = pool.Pop();
                _container.Inject(mediator);
                return mediator;
            }

            // Bind mediator dynamically as transient if not registered
            if (!_container.IsRegistered(mediatorType))
            {
                _container.Bind(mediatorType, isSingleton: false);
            }

            return (IMediator)_container.Resolve(mediatorType);
        }

        private void ReturnMediator(IMediator mediator)
        {
            var type = mediator.GetType();
            if (!_mediatorPools.TryGetValue(type, out var pool))
            {
                pool = new Stack<IMediator>();
                _mediatorPools[type] = pool;
            }
            
            CleanupMediator(mediator);
            if (pool.Count < _maxMediatorPoolSize)
            {
                pool.Push(mediator);
            }
        }

        private void CleanupMediator(IMediator mediator)
        {
            NexusDI.ClearInjectedReferences(mediator);
        }

        /// <summary>Disposes all active mediators and unbinds all views, then clears all pools.</summary>
        public void Dispose()
        {
            foreach (var kvp in _activeMediators)
            {
                try
                {
                    kvp.Key.Unbind();
                    kvp.Value.Unbind();
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogException(ex);
                }
            }
            _activeMediators.Clear();
            _mediatorPools.Clear();
        }
    }
}
