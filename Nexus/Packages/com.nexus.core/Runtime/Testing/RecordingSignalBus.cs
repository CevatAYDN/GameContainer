using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    /// <summary>
    /// A signal bus for a unit test of a model, service or view without a context: records every fired signal in order
    /// and calls the handlers the test subscribed, on the calling thread. Commands are not run; use
    /// <see cref="NexusTestHarness"/> for a context with commands. As on the real bus, <c>Fire</c> refuses a signal
    /// with async subscribers (use <c>FireAsync</c>). Issue #74.
    /// </summary>
    public sealed class RecordingSignalBus : ISignalBus
    {
        private static readonly IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> NoHandlers =
            new Dictionary<Type, IReadOnlyList<CommandHandlerInfo>>();

        private readonly List<object> _fired = new List<object>();
        private readonly List<Subscription> _subscriptions = new List<Subscription>();

        /// <summary>Every signal fired, in order (boxed).</summary>
        public IReadOnlyList<object> Fired => _fired;

        public int Count<T>() where T : struct
        {
            int count = 0;
            for (int i = 0; i < _fired.Count; i++) if (_fired[i] is T) count++;
            return count;
        }

        /// <summary>The last <typeparamref name="T"/> fired; throws when none was, so a test never reads a default by mistake.</summary>
        public T Last<T>() where T : struct
        {
            for (int i = _fired.Count - 1; i >= 0; i--) if (_fired[i] is T signal) return signal;
            throw new InvalidOperationException($"No {typeof(T).Name} was fired.");
        }

        public void Clear() => _fired.Clear();

        public void Fire<T>(T signal) where T : struct
        {
            var handlers = Snapshot<T>();
            foreach (var s in handlers)
                if (s.Handler is Func<T, CancellationToken, ValueTask>)
                    throw new NexusSyncAsyncMismatchException($"Synchronous Fire() was called for signal '{typeof(T).FullName}', which has async subscribers; use FireAsync().");
            _fired.Add(signal);
            foreach (var s in handlers)
                if (s.IsActive) ((Action<T>)s.Handler)(signal);
        }

        public async ValueTask FireAsync<T>(T signal) where T : struct
        {
            _fired.Add(signal);
            foreach (var s in Snapshot<T>())
            {
                if (!s.IsActive) continue;
                if (s.Handler is Action<T> sync) sync(signal);
                else await ((Func<T, CancellationToken, ValueTask>)s.Handler)(signal, CancellationToken.None);
            }
        }

        /// <summary>Runs at once on the calling thread.</summary>
        public void FireThreadSafe<T>(T signal) where T : struct => Fire(signal);
        /// <summary>Runs at once; a test has no next frame to wait for.</summary>
        public void FireNextFrame<T>(T signal) where T : struct => Fire(signal);
        /// <summary>Same as <see cref="FireAsync{T}"/>; the timeout is not applied.</summary>
        public ValueTask FireAsyncWithTimeout<T>(T signal, int timeoutMilliseconds) where T : struct => FireAsync(signal);

        public void FireAsyncAndForget<T>(T signal, Action<Exception> onError = null) where T : struct => _ = Forget(signal, onError);

        private async Task Forget<T>(T signal, Action<Exception> onError) where T : struct
        {
            try { await FireAsync(signal); }
            catch (Exception ex)
            {
                if (onError != null) onError(ex);
                else NexusRuntime.Logger?.LogException(ex);
            }
        }

        public ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct => Add(typeof(T), handler);
        public ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct => Add(typeof(T), handler);

        public IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers => NoHandlers;
        public bool HasCommandHandler(Type signalType) => false;
        public bool HasCommandHandler<TSignal>() where TSignal : struct => false;

        private ISignalSubscription Add(Type type, Delegate handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var subscription = new Subscription(this, type, handler);
            _subscriptions.Add(subscription);
            return subscription;
        }

        private List<Subscription> Snapshot<T>()
        {
            var list = new List<Subscription>();
            foreach (var s in _subscriptions) if (s.Type == typeof(T)) list.Add(s);
            return list;
        }

        private sealed class Subscription : ISignalSubscription
        {
            private readonly RecordingSignalBus _bus;
            public readonly Type Type;
            public readonly Delegate Handler;
            public Subscription(RecordingSignalBus bus, Type type, Delegate handler) { _bus = bus; Type = type; Handler = handler; }
            public bool IsActive { get; private set; } = true;
            public CancellationToken Lifetime => CancellationToken.None;

            public void Dispose()
            {
                if (!IsActive) return;
                IsActive = false;
                _bus._subscriptions.Remove(this);
            }
        }
    }
}
