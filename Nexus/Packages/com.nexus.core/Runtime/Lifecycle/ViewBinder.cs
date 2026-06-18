using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public abstract class View : MonoBehaviour, IView
    {
        protected IContext Context { get; private set; }
        private bool _isBound;

        private void Awake()
        {
            // Plan §2.3: Context/SignalBus access is forbidden in Awake.
            // View has not been registered yet and Mediator has not been connected.
        }

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
                // Fallback: find nearest active root
                var fallbackRoot = FindAnyObjectByType<Root>();
                if (fallbackRoot != null && fallbackRoot.Context != null)
                {
                    fallbackRoot.Context.RegisterView(this);
                }
            }
        }

        protected virtual void OnDisable()
        {
            if (Context != null)
            {
                Context.UnregisterView(this);
            }
        }

        public void Bind(IContext context)
        {
            if (_isBound) return;
            _isBound = true;
            Context = context;
            OnBind(context);
        }

        public void Unbind()
        {
            OnUnbind();
            _isBound = false;
            Context = null;
        }

        protected virtual void OnBind(IContext context) { }
        protected virtual void OnUnbind() { }
    }

    [Preserve]
    public class ViewBinder : IDisposable
    {
        private readonly IContext _context;
        private readonly NexusDI _container;
        private readonly Dictionary<IView, IMediator> _activeMediators = new();
        private readonly Dictionary<Type, Stack<IMediator>> _mediatorPools = new();

        public ViewBinder(IContext context, NexusDI container)
        {
            _context = context;
            _container = container;
        }

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
