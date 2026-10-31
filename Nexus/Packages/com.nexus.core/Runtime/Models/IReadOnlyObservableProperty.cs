using System;

namespace Nexus.Core
{
    /// <summary>
    /// The read side of <see cref="ObservableProperty{T}"/>. A service exposes state it alone sets through it, so a setter
    /// outside the service is a compile error (the screen stack's <c>Current</c>, ADR-0003). Notifications and their
    /// allocation contract are those of the property behind it; a mediator subscribes with <c>TrackObservable</c>.
    /// </summary>
    public interface IReadOnlyObservableProperty<T>
    {
        /// <summary>The current value.</summary>
        T Value { get; }

        /// <summary>Adds a handler called with the old and the new value on each change.</summary>
        void OnChanged(Action<T, T> handler);

        /// <summary>Removes a handler added with <see cref="OnChanged"/>.</summary>
        void RemoveOnChanged(Action<T, T> handler);
    }
}
