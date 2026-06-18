using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public class SignalSubscription<T> : ISignalSubscription where T : struct
    {
        private readonly Action<T> _handler;
        private readonly Action _onDispose;
        public bool IsActive { get; private set; } = true;
        public CancellationToken Lifetime { get; }
        private CancellationTokenRegistration _registration;

        public SignalSubscription(Action<T> handler, CancellationToken ct, Action onDispose)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            _registration = ct.Register(Dispose);
        }

        public void Invoke(T signal)
        {
            if (IsActive && !Lifetime.IsCancellationRequested)
            {
                _handler(signal);
            }
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            _registration.Dispose();
            _onDispose?.Invoke();
        }
    }

    public class AsyncSignalSubscription<T> : ISignalSubscription where T : struct
    {
        private readonly Func<T, CancellationToken, ValueTask> _handler;
        private readonly Action _onDispose;
        public bool IsActive { get; private set; } = true;
        public CancellationToken Lifetime { get; }
        private CancellationTokenRegistration _registration;

        public AsyncSignalSubscription(Func<T, CancellationToken, ValueTask> handler, CancellationToken ct, Action onDispose)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            _registration = ct.Register(Dispose);
        }

        public async ValueTask InvokeAsync(T signal, CancellationToken ct)
        {
            if (IsActive && !Lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await _handler(signal, ct);
            }
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            _registration.Dispose();
            _onDispose?.Invoke();
        }
    }
}
