using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public interface IMediator
    {
        void Bind(object view, ISignalBus signalBus);
        void Unbind();
    }

    public abstract class Mediator<TView> : IMediator where TView : class
    {
        protected TView View { get; private set; }
        protected ISignalBus SignalBus { get; private set; }
        
        private readonly List<ISignalSubscription> _subscriptions = new();

        public void Bind(object view, ISignalBus signalBus)
        {
            View = view as TView;
            if (View == null)
            {
                throw new InvalidCastException($"Cannot bind view of type {view?.GetType().Name} to Mediator of type {GetType().Name} expecting {typeof(TView).Name}");
            }
            SignalBus = signalBus;
            OnBind();
        }

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

        protected virtual void OnBind() { }
        protected virtual void OnUnbind() { }

        protected void Subscribe<T>(Action<T> handler) where T : struct
        {
            var sub = SignalBus.Subscribe(handler);
            _subscriptions.Add(sub);
        }

        protected void SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct
        {
            var sub = SignalBus.SubscribeAsync(handler);
            _subscriptions.Add(sub);
        }
    }
}
