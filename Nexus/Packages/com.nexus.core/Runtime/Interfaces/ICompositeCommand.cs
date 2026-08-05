using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    /// <summary>
    /// Read-only view of the signal payloads that satisfied a composite trigger.
    /// Supplied to <see cref="ICompositeCommand"/> / <see cref="IAsyncCompositeCommand"/> once all
    /// required signals have been received. Each required signal type is represented by the most
    /// recent instance captured before the trigger completed.
    /// </summary>
    public readonly struct CompositeContext
    {
        private readonly Type[] _signalTypes;
        private readonly object[] _payloads;

        internal CompositeContext(Type[] signalTypes, object[] payloads)
        {
            _signalTypes = signalTypes;
            _payloads = payloads;
        }

        /// <summary>Number of required signal slots in this composite.</summary>
        public int Count => _signalTypes?.Length ?? 0;

        /// <summary>
        /// Attempts to retrieve the captured payload for signal type <typeparamref name="T"/>.
        /// Returns false if the type is not part of this composite or was never captured.
        /// </summary>
        public bool TryGet<T>(out T signal) where T : struct
        {
            if (_signalTypes != null)
            {
                for (int i = 0; i < _signalTypes.Length; i++)
                {
                    if (_signalTypes[i] == typeof(T) && _payloads[i] is T typed)
                    {
                        signal = typed;
                        return true;
                    }
                }
            }
            signal = default;
            return false;
        }

        /// <summary>Retrieves the captured payload for signal type <typeparamref name="T"/>, or default if absent.</summary>
        public T Get<T>() where T : struct => TryGet<T>(out var s) ? s : default;
    }

    /// <summary>
    /// A composite command triggered only after all of its required signals have been received.
    /// Unlike <see cref="ICommand{TSignal}"/>, a composite spans multiple signal types; the payloads
    /// that satisfied the trigger are supplied via <see cref="CompositeContext"/>.
    /// </summary>
    public interface ICompositeCommand
    {
        void Execute(CompositeContext signals);
    }

    /// <summary>Asynchronous counterpart of <see cref="ICompositeCommand"/>.</summary>
    public interface IAsyncCompositeCommand
    {
        ValueTask ExecuteAsync(CompositeContext signals, CancellationToken ct);
    }
}
