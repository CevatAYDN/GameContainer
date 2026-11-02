using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Derives a read-only property from one or more properties (issue #269). The result is recomputed on each change of a
    /// source and notifies only when it changes. Dispose it when it is no longer needed: until then the sources keep it
    /// alive and recompute it; a mediator's <c>TrackObservable</c> disposes it on unbind.
    /// <code>
    /// TrackObservable(Economy.GetObservableBalance("coins").CombineLatest(Shop.Price, (coins, price) => coins >= price),
    ///     (_, canBuy) => View.SetBuyEnabled(canBuy));
    /// </code>
    /// </summary>
    [Preserve]
    public static class ObservableOperators
    {
        /// <summary>A property holding <paramref name="selector"/> of the source's value.</summary>
        public static DerivedProperty<TResult> Select<T, TResult>(this IReadOnlyObservableProperty<T> source, Func<T, TResult> selector)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            var derived = new DerivedProperty<TResult>();
            Action<T, T> onSource = (_, value) => derived.Set(selector(value));
            derived.Start(
                subscribe: () => source.OnChanged(onSource),
                detach: () => source.RemoveOnChanged(onSource),
                compute: () => selector(source.Value));
            return derived;
        }

        /// <summary>A property holding <paramref name="combiner"/> of both sources' values.</summary>
        public static DerivedProperty<TResult> CombineLatest<T1, T2, TResult>(this IReadOnlyObservableProperty<T1> first,
            IReadOnlyObservableProperty<T2> second, Func<T1, T2, TResult> combiner)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (combiner == null) throw new ArgumentNullException(nameof(combiner));
            var derived = new DerivedProperty<TResult>();
            // the changed source's own value comes from its notification, the other's from its current value
            Action<T1, T1> onFirst = (_, value) => derived.Set(combiner(value, second.Value));
            Action<T2, T2> onSecond = (_, value) => derived.Set(combiner(first.Value, value));
            derived.Start(
                subscribe: () => { first.OnChanged(onFirst); second.OnChanged(onSecond); },
                detach: () => { first.RemoveOnChanged(onFirst); second.RemoveOnChanged(onSecond); },
                compute: () => combiner(first.Value, second.Value));
            return derived;
        }

        /// <summary>A property holding <paramref name="combiner"/> of the three sources' values.</summary>
        public static DerivedProperty<TResult> CombineLatest<T1, T2, T3, TResult>(this IReadOnlyObservableProperty<T1> first,
            IReadOnlyObservableProperty<T2> second, IReadOnlyObservableProperty<T3> third, Func<T1, T2, T3, TResult> combiner)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (third == null) throw new ArgumentNullException(nameof(third));
            if (combiner == null) throw new ArgumentNullException(nameof(combiner));
            var derived = new DerivedProperty<TResult>();
            Action<T1, T1> onFirst = (_, value) => derived.Set(combiner(value, second.Value, third.Value));
            Action<T2, T2> onSecond = (_, value) => derived.Set(combiner(first.Value, value, third.Value));
            Action<T3, T3> onThird = (_, value) => derived.Set(combiner(first.Value, second.Value, value));
            derived.Start(
                subscribe: () => { first.OnChanged(onFirst); second.OnChanged(onSecond); third.OnChanged(onThird); },
                detach: () => { first.RemoveOnChanged(onFirst); second.RemoveOnChanged(onSecond); third.RemoveOnChanged(onThird); },
                compute: () => combiner(first.Value, second.Value, third.Value));
            return derived;
        }
    }

    /// <summary>
    /// A read-only property computed from other properties by <see cref="ObservableOperators"/>. It notifies its handlers
    /// when the computed value changes, with the equality rule of <see cref="ObservableProperty{T}"/>. A selector that throws
    /// on a change leaves the value as it was; the source reports the failure as it reports any observer's (issue #189).
    /// <see cref="Dispose"/> removes its handlers from the sources and drops its own; the value stays the last one computed.
    /// </summary>
    [Preserve]
    public sealed class DerivedProperty<T> : IReadOnlyObservableProperty<T>, IDisposable
    {
        private readonly ObservableProperty<T> _value = new();
        private readonly object _lifetimeLock = new();
        private Action _detach; // guarded by _lifetimeLock
        private volatile bool _disposed;

        internal DerivedProperty() { }

        /// <summary>The value computed from the sources' latest values.</summary>
        public T Value => _value.Value;

        /// <summary>True once <see cref="Dispose"/> has run: the value no longer follows the sources.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>Adds a handler called with the old and the new computed value on each change.</summary>
        public void OnChanged(Action<T, T> handler) => _value.OnChanged(handler);

        /// <summary>Removes a handler added with <see cref="OnChanged"/>.</summary>
        public void RemoveOnChanged(Action<T, T> handler) => _value.RemoveOnChanged(handler);

        internal void Start(Action subscribe, Action detach, Func<T> compute)
        {
            _detach = detach;
            // computed after subscribing, so a change in between is not missed; a selector that throws here leaves no
            // handler on the sources
            try
            {
                subscribe();
                _value.SetWithoutNotify(compute());
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        // a source's notification can still reach a handler removed during its dispatch: a disposed property ignores it
        internal void Set(T value)
        {
            if (!_disposed) _value.Value = value;
        }

        /// <summary>Stops following the sources and drops this property's handlers. Calling it again does nothing.</summary>
        public void Dispose()
        {
            Action detach;
            lock (_lifetimeLock)
            {
                if (_disposed) return;
                _disposed = true;
                detach = _detach;
                _detach = null;
            }
            detach?.Invoke();
            _value.ClearOnChanged();
        }

        /// <summary>Returns the current value as text.</summary>
        public override string ToString() => _value.ToString();
    }
}
