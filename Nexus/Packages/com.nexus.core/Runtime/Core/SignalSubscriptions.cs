using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    /// <summary>
    /// A synchronous signal subscription tied to a <see cref="CancellationToken"/> lifetime.
    /// Automatically disposes when the cancellation token is triggered.
    /// </summary>
    /// <typeparam name="T">The signal struct type.</typeparam>
    public class SignalSubscription<T> : ISignalSubscription where T : struct
    {
        private readonly Action<T> _handler;
        private readonly Action _onDispose;
        /// <summary>True while this subscription is still active and can receive signals.</summary>
        public bool IsActive { get; private set; } = true;
        /// <summary>The cancellation token that controls this subscription's lifetime.</summary>
        public CancellationToken Lifetime { get; }
        private CancellationTokenRegistration _registration;

        /// <summary>Creates a new synchronous signal subscription.</summary>
        /// <param name="handler">The handler to invoke when the signal is fired.</param>
        /// <param name="ct">Cancellation token that disposes the subscription when triggered.</param>
        /// <param name="onDispose">Optional cleanup action invoked on disposal.</param>
        public SignalSubscription(Action<T> handler, CancellationToken ct, Action onDispose)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            _registration = ct.Register(Dispose);
        }

        /// <summary>Invokes the handler if the subscription is still active.</summary>
        /// <param name="signal">The signal data.</param>
        public void Invoke(T signal)
        {
            if (IsActive && !Lifetime.IsCancellationRequested)
            {
                _handler(signal);
            }
        }

        /// <summary>Disposes the subscription, unregistering from the cancellation token.</summary>
        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            _registration.Dispose();
            _onDispose?.Invoke();
        }
    }

    /// <summary>
    /// An asynchronous signal subscription tied to a <see cref="CancellationToken"/> lifetime.
    /// Automatically disposes when the cancellation token is triggered.
    /// </summary>
    /// <typeparam name="T">The signal struct type.</typeparam>
    public class AsyncSignalSubscription<T> : ISignalSubscription where T : struct
    {
        private readonly Func<T, CancellationToken, ValueTask> _handler;
        private readonly Action _onDispose;
        /// <summary>True while this subscription is still active and can receive signals.</summary>
        public bool IsActive { get; private set; } = true;
        /// <summary>The cancellation token that controls this subscription's lifetime.</summary>
        public CancellationToken Lifetime { get; }
        private CancellationTokenRegistration _registration;

        /// <summary>Creates a new asynchronous signal subscription.</summary>
        /// <param name="handler">The async handler to invoke when the signal is fired.</param>
        /// <param name="ct">Cancellation token that disposes the subscription when triggered.</param>
        /// <param name="onDispose">Optional cleanup action invoked on disposal.</param>
        public AsyncSignalSubscription(Func<T, CancellationToken, ValueTask> handler, CancellationToken ct, Action onDispose)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            _registration = ct.Register(Dispose);
        }

        /// <summary>Invokes the async handler if the subscription is still active.</summary>
        /// <param name="signal">The signal data.</param>
        /// <param name="ct">Cancellation token for the invocation.</param>
        public async ValueTask InvokeAsync(T signal, CancellationToken ct)
        {
            if (IsActive && !Lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await _handler(signal, ct);
            }
        }

        /// <summary>Disposes the subscription, unregistering from the cancellation token.</summary>
        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            _registration.Dispose();
            _onDispose?.Invoke();
        }
    }
}
