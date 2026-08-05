using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public interface ISignalBus
    {
        /// <summary>
        /// Enumerates all signal→handler registrations discovered through configuration
        /// and attribute scanning. Key is the signal type; value is the registered handlers.
        /// Empty until the owning context has been configured.
        /// </summary>
        IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers { get; }

        /// <summary>Dispatches immediately on the current thread.</summary>
        void Fire<T>(T signal) where T : struct;
        /// <summary>Dispatches asynchronously and waits for the handler chain.</summary>
        ValueTask FireAsync<T>(T signal) where T : struct;
        /// <summary>Dispatches from any thread by marshaling to the signal bus queue.</summary>
        void FireThreadSafe<T>(T signal) where T : struct;
        /// <summary>Defers dispatch until the next frame.</summary>
        void FireNextFrame<T>(T signal) where T : struct;

        /// <summary>
        /// Fires a signal asynchronously with a timeout. If the command chain does not complete
        /// within the specified milliseconds, a <see cref="OperationCanceledException"/> is thrown.
        /// </summary>
        /// <param name="signal">The signal data.</param>
        /// <param name="timeoutMilliseconds">Maximum execution time in milliseconds.</param>
        ValueTask FireAsyncWithTimeout<T>(T signal, int timeoutMilliseconds) where T : struct;

        /// <summary>
        /// Fires a signal asynchronously without awaiting the result — true fire-and-forget.
        /// Returns immediately; the async dispatch continues on the thread pool. Errors are
        /// routed to <paramref name="onError"/> (or the global unhandled-exception handler
        /// when null) and never crash the process.
        /// </summary>
        void FireAsyncAndForget<T>(T signal, Action<Exception> onError = null) where T : struct;

        ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct;
        ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct;

        /// <summary>Returns true when at least one command handler is registered for the signal type.</summary>
        bool HasCommandHandler(Type signalType);
        /// <summary>Generic form of <see cref="HasCommandHandler(Type)"/>.</summary>
        bool HasCommandHandler<TSignal>() where TSignal : struct;
    }

    public interface ISignalSubscription : IDisposable
    {
        bool IsActive { get; }
        CancellationToken Lifetime { get; }
    }
}
