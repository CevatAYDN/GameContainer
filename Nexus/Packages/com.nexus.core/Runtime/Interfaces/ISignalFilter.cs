namespace Nexus.Core
{
    /// <summary>
    /// Ref-based, zero-allocation signal filter (MessagePipe/VContainer-style middleware).
    /// Registered per signal type via <see cref="SignalBus.AddSignalFilter{T}"/> (instance form)
    /// or <see cref="SignalBus.AddSignalFilter{TSignal,TFilter}"/> (type form, container-resolved).
    /// Filters run in registration order BEFORE any dispatch; returning <c>false</c> cancels the
    /// signal before commands, subscriptions and the legacy object-based interceptors ever see it.
    /// Because the signal flows by <c>ref</c>, a filter may also mutate it (e.g. clamp a damage
    /// amount) without boxing — the struct never leaves the stack on this path.
    /// </summary>
    /// <typeparam name="T">The signal struct type.</typeparam>
    public interface ISignalFilter<T> where T : struct
    {
        /// <summary>Returns <c>false</c> to cancel the signal.</summary>
        bool OnFilter(ref T signal);
    }
}
