using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Nexus.Core
{
    public abstract class View : MonoBehaviour, IView
    {
        protected IContext Context { get; private set; }

        protected virtual void OnEnable()
        {
            var root = GetComponentInParent<Root>();
            if (root != null)
            {
                root.Context.RegisterView(this);
            }
            else
            {
                // Fallback: find nearest active root
                var fallbackRoot = FindAnyObjectByType<Root>();
                if (fallbackRoot != null)
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
            Context = context;
            OnBind(context);
        }

        public void Unbind()
        {
            OnUnbind();
            Context = null;
        }

        protected virtual void OnBind(IContext context) { }
        protected virtual void OnUnbind() { }
    }

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
            var type = mediator.GetType();
            
            // Null out injected fields to prevent memory leaks
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<InjectAttribute>() != null && !field.FieldType.IsValueType)
                {
                    field.SetValue(mediator, null);
                }
            }

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite && !prop.PropertyType.IsValueType)
                {
                    prop.SetValue(mediator, null);
                }
            }
        }

        public void Dispose()
        {
            foreach (var kvp in _activeMediators)
            {
                try
                {
                    kvp.Value.Unbind();
                }
                catch {}
            }
            _activeMediators.Clear();
            _mediatorPools.Clear();
        }
    }
}
