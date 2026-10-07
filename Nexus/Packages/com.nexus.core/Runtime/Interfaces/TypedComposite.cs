using System;

namespace Nexus.Core
{
    /// <summary>A synchronous two-signal composite with value payloads and no boxing.</summary>
    public interface ICompositeCommand<TFirst, TSecond> where TFirst : struct where TSecond : struct
    {
        void Execute(TFirst first, TSecond second);
    }

    internal sealed class TypedCompositeRegistration<TFirst, TSecond> : IDisposable
        where TFirst : struct where TSecond : struct
    {
        private readonly object _lock = new();
        private readonly Action<TFirst, TSecond> _execute;
        private readonly bool _oneShot;
        private ISignalSubscription _firstSubscription, _secondSubscription;
        private TFirst _first;
        private TSecond _second;
        private bool _hasFirst, _hasSecond, _disposed, _completed;

        internal TypedCompositeRegistration(ISignalBus bus, Action<TFirst, TSecond> execute, bool oneShot)
        {
            _execute = execute;
            _oneShot = oneShot;
            _firstSubscription = bus.Subscribe<TFirst>(ReceiveFirst);
            try
            {
                _secondSubscription = bus.Subscribe<TSecond>(ReceiveSecond);
                lock (_lock) { if (_disposed) _secondSubscription.Dispose(); }
            }
            catch { _firstSubscription.Dispose(); throw; }
        }

        private void ReceiveFirst(TFirst value)
        {
            TSecond second;
            lock (_lock)
            {
                if (_disposed || _completed) return;
                _first = value;
                _hasFirst = true;
                if (!_hasSecond) return;
                second = _second;
                ResetAfterCapture();
            }
            Execute(value, second);
        }

        private void ReceiveSecond(TSecond value)
        {
            TFirst first;
            lock (_lock)
            {
                if (_disposed || _completed) return;
                _second = value;
                _hasSecond = true;
                if (!_hasFirst) return;
                first = _first;
                ResetAfterCapture();
            }
            Execute(first, value);
        }

        private void ResetAfterCapture()
        {
            _completed = _oneShot;
            _hasFirst = _hasSecond = false;
            _first = default;
            _second = default;
        }

        private void Execute(TFirst first, TSecond second)
        {
            try { _execute(first, second); }
            catch
            {
                lock (_lock) { if (!_disposed) _completed = false; }
                throw;
            }
            if (_oneShot) Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _first = default;
                _second = default;
            }
            _firstSubscription?.Dispose();
            _secondSubscription?.Dispose();
        }
    }

    public static class TypedCompositeExtensions
    {
        /// <summary>
        /// Runs a pooled command after both distinct signal types arrive, using their latest
        /// values. Synchronous, registration-ordered; dispose the registration to unsubscribe.
        /// Immutable arbitrary-arity/async composites continue to use CompositeContext.
        /// </summary>
        public static IDisposable BindComposite<TFirst, TSecond, TCommand>(this ISignalBus bus, bool oneShot = false)
            where TFirst : struct where TSecond : struct
            where TCommand : class, ICompositeCommand<TFirst, TSecond>
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            if (bus is SignalBus concrete) return concrete.BindTypedComposite<TFirst, TSecond, TCommand>(oneShot);
            throw new NotSupportedException("Typed pooled composites require Nexus SignalBus.");
        }
    }
}
