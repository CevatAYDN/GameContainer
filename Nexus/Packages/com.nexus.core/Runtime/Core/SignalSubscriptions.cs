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
        private int _disposedFlag;

        /// <summary>Creates a new synchronous signal subscription.</summary>
        /// <param name="handler">The handler to invoke when the signal is fired.</param>
        /// <param name="ct">Cancellation token that disposes the subscription when triggered.</param>
        /// <param name="onDispose">Optional cleanup action invoked on disposal.</param>
        public SignalSubscription(Action<T> handler, CancellationToken ct, Action onDispose)
            : this(handler, ct, onDispose, deferLifetimeRegistration: false)
        {
        }

        /// <summary>
        /// Internal overload used by <see cref="SubscriptionRegistry"/>: with
        /// <paramref name="deferLifetimeRegistration"/> true the cancellation callback is NOT
        /// registered here — the registry calls <see cref="RegisterLifetimeCallback"/> AFTER the
        /// subscription is fully constructed and its node added, so a token that cancels during
        /// that window can still unsubscribe the node (no permanent dead-node leak).
        /// </summary>
        internal SignalSubscription(Action<T> handler, CancellationToken ct, Action onDispose, bool deferLifetimeRegistration)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            if (ct.IsCancellationRequested)
            {
                // Already-cancelled lifetime: become a disposed no-op WITHOUT running Dispose
                // (which would fire the unsubscribe closure before the caller has assigned
                // its `sub` variable — the classic constructor-reentrancy leak).
                _disposedFlag = 1;
                IsActive = false;
                return;
            }
            if (!deferLifetimeRegistration)
            {
                _registration = ct.Register(Dispose);
            }
        }

        /// <summary>Registers the auto-dispose cancellation callback. Called by the registry
        /// once the subscription is fully constructed and its node is added.</summary>
        internal void RegisterLifetimeCallback()
        {
            if (_disposedFlag == 0)
            {
                // If the token cancels between construction and this call, Register invokes
                // Dispose synchronously — safe now, because the node exists and the
                // unsubscribe closure resolves to the fully assigned subscription.
                _registration = Lifetime.Register(Dispose);
            }
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
            // Atomic check-and-set using Interlocked.Exchange on int (0/1) to prevent
            // double-invocation of _onDispose when two threads call Dispose() concurrently.
            // NOTE: bool cannot be used with Interlocked.Exchange in .NET Standard 2.0.
            if (System.Threading.Interlocked.Exchange(ref _disposedFlag, 1) == 0)
            {
                IsActive = false;
                _registration.Dispose();
                _onDispose?.Invoke();
            }
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
        private int _disposedFlag;

        /// <summary>Creates a new asynchronous signal subscription.</summary>
        /// <param name="handler">The async handler to invoke when the signal is fired.</param>
        /// <param name="ct">Cancellation token that disposes the subscription when triggered.</param>
        /// <param name="onDispose">Optional cleanup action invoked on disposal.</param>
        public AsyncSignalSubscription(Func<T, CancellationToken, ValueTask> handler, CancellationToken ct, Action onDispose)
            : this(handler, ct, onDispose, deferLifetimeRegistration: false)
        {
        }

        /// <summary>
        /// Internal overload used by <see cref="SubscriptionRegistry"/> — see the
        /// <see cref="SignalSubscription{T}"/> counterpart for the deferred-registration rationale.
        /// </summary>
        internal AsyncSignalSubscription(Func<T, CancellationToken, ValueTask> handler, CancellationToken ct, Action onDispose, bool deferLifetimeRegistration)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            if (ct.IsCancellationRequested)
            {
                // Already-cancelled lifetime: disposed no-op — see SignalSubscription<T>.
                _disposedFlag = 1;
                IsActive = false;
                return;
            }
            if (!deferLifetimeRegistration)
            {
                _registration = ct.Register(Dispose);
            }
        }

        /// <summary>Registers the auto-dispose cancellation callback. Called by the registry
        /// once the subscription is fully constructed and its node is added.</summary>
        internal void RegisterLifetimeCallback()
        {
            if (_disposedFlag == 0)
            {
                _registration = Lifetime.Register(Dispose);
            }
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
            // Atomic check-and-set for AsyncSignalSubscription too.
            if (System.Threading.Interlocked.Exchange(ref _disposedFlag, 1) == 0)
            {
                IsActive = false;
                _registration.Dispose();
                _onDispose?.Invoke();
            }
        }
    }
}
