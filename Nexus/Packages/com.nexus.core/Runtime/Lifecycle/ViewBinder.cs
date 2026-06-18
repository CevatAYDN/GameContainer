using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Abstract base class for Nexus views. Views are MonoBehaviour components that
    /// automatically register with the nearest <see cref="Root"/> context and bind their mediator.
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

        /// <summary>
        /// Automatically registers this view with the nearest parent Root's context.
        /// Falls back to FindObjectsByType if no parent Root is found (single-root scenes).
        /// </summary>
        protected virtual void OnEnable()
        {
            if (_isBound) return;
            var root = GetComponentInParent<Root>();
            if (root != null && root.Context != null)
            {
                root.Context.RegisterView(this);
            }
            else
            {
                // Fallback only when the scene has a single unambiguous active root.
                var roots = FindObjectsByType<Root>(FindObjectsInactive.Exclude);
                if (roots.Length == 1 && roots[0].Context != null)
                {
                    roots[0].Context.RegisterView(this);
                }
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
                if (Context != null)
                {
                    Context.UnregisterView(this);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus] View '{gameObject.name}' failed to unbind on disable: {ex.Message}");
            }
        }

        /// <summary>Binds the view to a context. Calls <see cref="OnBind"/> for derived setup.</summary>
        /// <param name="context">The context to bind to.</param>
        public void Bind(IContext context)
        {
            if (_isBound) return;
            _isBound = true;
            Context = context;
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

        /// <summary>Creates a new <see cref="ViewBinder"/> for the given context.</summary>
        /// <param name="context">The context that owns this binder.</param>
        /// <param name="container">The DI container for resolving mediators.</param>
        public ViewBinder(IContext context, NexusDI container)
        {
            _context = context;
            _container = container;
        }

        /// <summary>
        /// Registers a view, binds it to the context, and creates/assigns its mediator.
        /// If the view has a <see cref="MediatorAttribute"/>, the corresponding mediator is resolved from the pool or created.
        /// </summary>
        /// <param name="view">The view to register.</param>
        public void RegisterView(IView view)
        {
            if (_activeMediators.ContainsKey(view)) return;

            view.Bind(_context);

            var viewType = view.GetType();
            var mediatorAttr = viewType.GetCustomAttribute<MediatorAttribute>();
            if (mediatorAttr == null) return;

            var mediatorType = mediatorAttr.MediatorType;
            var mediator = GetMediator(mediatorType);

            _activeMediators[view] = mediator;
            
            // Bind view and signalBus to mediator
            mediator.Bind(view, _context.SignalBus);
        }

        /// <summary>Unregisters a view, unbinds its mediator, and returns the mediator to the pool.</summary>
        /// <param name="view">The view to unregister.</param>
        public void UnregisterView(IView view)
        {
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
                return pool.Pop();
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
            pool.Push(mediator);
        }

        private void CleanupMediator(IMediator mediator)
        {
            NexusDI.ClearInjectedReferences(mediator);
        }

        /// <summary>Disposes all active mediators and clears all pools.</summary>
        public void Dispose()
        {
            foreach (var kvp in _activeMediators)
            {
                try
                {
                    kvp.Value.Unbind();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            _activeMediators.Clear();
            _mediatorPools.Clear();
        }
    }
}
