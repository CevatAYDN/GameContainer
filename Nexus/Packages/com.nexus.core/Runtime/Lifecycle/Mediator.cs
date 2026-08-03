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
    /// <remarks>
    /// Implements <see cref="IResettable"/> so pooled reuse hygiene is guaranteed for ALL
    /// mediators, not just those that opt in: <see cref="ViewBinder.GetMediator"/> calls
    /// <see cref="Reset"/> on pool pop and <see cref="NexusDI.ClearInjectedReferences"/>
    /// calls it on pool return. Override <see cref="OnReset"/> to clear derived private state.
    /// </remarks>
    [Preserve]
    public abstract class Mediator<TView> : IMediator, IResettable where TView : class
    {
        /// <summary>The bound view instance. Set after <see cref="Bind"/> is called.</summary>
        protected TView View { get; private set; }
        /// <summary>The signal bus for subscribing to signals.</summary>
        [Inject] public ISignalBus SignalBus { get; set; }
        
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

        /// <summary>Subscribes to an ObservableProperty and tracks the subscription for automatic unbind cleanup.</summary>
        protected IDisposable TrackObservable<T>(ObservableProperty<T> property, Action<T, T> handler)
        {
            if (property == null) throw new ArgumentNullException(nameof(property));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            property.OnChanged(handler);
            var sub = new ObservableSubscription<T>(property, handler);
            _subscriptions.Add(sub);
            return sub;
        }

        private sealed class ObservableSubscription<T> : ISignalSubscription
        {
            private ObservableProperty<T> _property;
            private Action<T, T> _handler;

            public bool IsActive => _property != null;
            public CancellationToken Lifetime => CancellationToken.None;

            public ObservableSubscription(ObservableProperty<T> property, Action<T, T> handler)
            {
                _property = property;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_property != null && _handler != null)
                {
                    _property.RemoveOnChanged(_handler);
                    _property = null;
                    _handler = null;
                }
            }
        }

        /// <summary>Override to perform custom logic when the mediator is bound to a view.</summary>
        protected virtual void OnBind() { }
        /// <summary>Override to perform custom cleanup when the mediator is unbound.</summary>
        protected virtual void OnUnbind() { }

        /// <summary>
        /// Resets the mediator to a clean, unbound state for pool reuse. Disposes any
        /// subscriptions that survived (normally Unbind clears them), nulls the view and
        /// signal bus, then invokes <see cref="OnReset"/> for derived private state.
        /// Idempotent and safe on a freshly created mediator.
        ///
        /// NOTE: Unlike <see cref="Unbind"/> (which calls <see cref="OnUnbind"/> while the
        /// view/signal bus are still set), Reset nulls the view and signal bus BEFORE calling
        /// <see cref="OnReset"/> — derived code must not expect to reach the old view from its
        /// reset hook. Also note Reset does NOT invoke <see cref="OnUnbind"/>; the two hooks have
        /// different contracts (OnUnbind = teardown, OnReset = pool-reuse hygiene).
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                _subscriptions[i].Dispose();
            }
            _subscriptions.Clear();
            View = null;
            SignalBus = null;
            OnReset();
        }

        /// <summary>
        /// Override to reset derived private state when the mediator is reused from the pool.
        /// MUST be idempotent: Reset() is invoked twice per pool cycle — once on return
        /// (ClearInjectedReferences) and once defensively on pop (GetMediator) — so derived
        /// state cleared here must be safe to clear again. Runs after View/SignalBus are nulled.
        /// </summary>
        protected virtual void OnReset() { }

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
                if (View is IView iview) return iview.IsAlive;
                if (View is UnityEngine.Object obj) return obj != null;
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
