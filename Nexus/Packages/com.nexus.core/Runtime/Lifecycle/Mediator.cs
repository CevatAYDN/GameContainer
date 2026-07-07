using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>Interface for mediator binding lifecycle management.</summary>
    public interface IMediator
    {
        /// <summary>Binds the mediator to a view and signal bus.</summary>
        void Bind(object view, ISignalBus signalBus);
        /// <summary>Unbinds the mediator and disposes all subscriptions.</summary>
        void Unbind();
    }

    /// <summary>
    /// Abstract base class for mediators in the MVCS pattern.
    /// Mediators connect views to the signal system and models, handling view lifecycle and signal subscriptions.
    /// </summary>
    /// <typeparam name="TView">The view type this mediator manages (must be a class).</typeparam>
    [Preserve]
    public abstract class Mediator<TView> : IMediator where TView : class
    {
        /// <summary>The bound view instance. Set after <see cref="Bind"/> is called.</summary>
        protected TView View { get; private set; }
        /// <summary>The signal bus for subscribing to signals.</summary>
        protected ISignalBus SignalBus { get; private set; }
        
        private readonly List<ISignalSubscription> _subscriptions = new();

        /// <summary>Binds the mediator to a view and signal bus. Throws <see cref="InvalidCastException"/> if the view type mismatch.</summary>
        /// <param name="view">The view instance (will be cast to TView).</param>
        /// <param name="signalBus">The signal bus for subscriptions.</param>
        public void Bind(object view, ISignalBus signalBus)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));
            if (signalBus == null)
                throw new ArgumentNullException(nameof(signalBus));

            View = view as TView;
            if (View == null)
            {
                throw new InvalidCastException($"Cannot bind view of type {view.GetType().Name} to Mediator of type {GetType().Name} expecting {typeof(TView).Name}");
            }
            SignalBus = signalBus;
            OnBind();
        }

        /// <summary>Unbinds the mediator, disposing all signal subscriptions.</summary>
        public void Unbind()
        {
            OnUnbind();
            
            // Auto dispose all subscriptions registered in this mediator
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                _subscriptions[i].Dispose();
            }
            _subscriptions.Clear();
            
            View = null;
            SignalBus = null;
        }

        /// <summary>Override to perform custom logic when the mediator is bound to a view.</summary>
        protected virtual void OnBind() { }
        /// <summary>Override to perform custom cleanup when the mediator is unbound.</summary>
        protected virtual void OnUnbind() { }

        /// <summary>Subscribes to a signal type. The subscription is auto-disposed on <see cref="Unbind"/>.</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="handler">The handler to invoke.</param>
        protected void Subscribe<T>(Action<T> handler) where T : struct
        {
            if (SignalBus == null)
            {
                throw new InvalidOperationException($"Cannot subscribe to signal '{typeof(T).Name}' in the Mediator constructor. Place all subscriptions inside OnBind() instead.");
            }
            var sub = SignalBus.Subscribe(handler);
            _subscriptions.Add(sub);
        }
 
        /// <summary>Subscribes an async handler to a signal type. Auto-disposed on <see cref="Unbind"/>.</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="handler">The async handler to invoke.</param>
        protected void SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct
        {
            if (SignalBus == null)
            {
                throw new InvalidOperationException($"Cannot subscribe asynchronously to signal '{typeof(T).Name}' in the Mediator constructor. Place all subscriptions inside OnBind() instead.");
            }
            var sub = SignalBus.SubscribeAsync(handler);
            _subscriptions.Add(sub);
        }

        /// <summary>
        /// Gets whether the mediator is currently bound to a valid, non-destroyed view.
        /// Helps prevent NullReferenceException when model events fire after/during view teardown.
        /// </summary>
        protected bool IsViewValid
        {
            get
            {
                if (View == null) return false;
                if (View is UnityEngine.Object obj)
                {
                    return obj != null;
                }
                return true;
            }
        }

        /// <summary>
        /// Executes the specified action on the view if it is still valid and not destroyed.
        /// </summary>
        protected void ExecuteIfViewValid(Action<TView> action)
        {
            if (IsViewValid)
            {
                action?.Invoke(View);
            }
        }
    }
}
