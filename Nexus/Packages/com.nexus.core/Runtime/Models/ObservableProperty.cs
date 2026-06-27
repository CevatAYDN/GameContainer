using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// A lightweight, zero-additional-allocation (after construction) observable value wrapper.
    ///
    /// Use in any model that needs to notify listeners when a value changes.
    /// <code>
    /// public class PlayerModel : IReactiveModel
    /// {
    ///     public readonly ObservableProperty<int> Score = new(0);
    ///     public readonly ObservableProperty<string> Name = new("Player");
    /// }
    /// </code>
    ///
    /// Listeners subscribe via <see cref="OnChanged"/> (a single-cast callback to avoid
    /// multicast delegate allocations).  For multiple listeners compose at the model level.
    /// </summary>
    [Preserve]
    public sealed class ObservableProperty<T>
    {
        // ── State ──────────────────────────────────────────────
        private T _value;

        // Single-cast: the model (or a dedicated aggregator) bridges to the SignalBus.
        // This keeps the hot-path allocation-free.
        private Action<T, T> _onChanged; // (oldValue, newValue)

        // ── Construction ───────────────────────────────────────
        /// <summary>Creates an observable property with the given initial value.</summary>
        public ObservableProperty(T initialValue = default)
        {
            _value = initialValue;
        }

        // ── Value ──────────────────────────────────────────────
        /// <summary>Gets or sets the current value.  Setting triggers <see cref="OnChanged"/>.</summary>
        public T Value
        {
            get => _value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value))
                    return;

                var old = _value;
                _value = value;
                _onChanged?.Invoke(old, value);
            }
        }

        /// <summary>Sets the underlying value without firing the change callback.</summary>
        public void SetWithoutNotify(T value)
        {
            _value = value;
        }

        // ── Observation ────────────────────────────────────────
        /// <summary>
        /// Registers a single-cast callback invoked when the value changes.
        /// Replace an existing callback by calling again (each call overwrites).
        /// Pass <c>null</c> to clear.
        /// </summary>
        public void OnChanged(Action<T, T> handler)
        {
            _onChanged = handler;
        }

        /// <summary>Removes the change callback.</summary>
        public void ClearOnChanged()
        {
            _onChanged = null;
        }

        // ── Implicit conversion (read convenience) ─────────────
        public static implicit operator T(ObservableProperty<T> prop) => prop._value;

        /// <summary>Returns the current value (same as <see cref="Value"/> getter).</summary>
        public override string ToString() => _value?.ToString() ?? "(null)";
    }

    // ── Reactive collection (optional, for list-backed properties) ──

    /// <summary>
    /// An observable list that fires callbacks on structural changes.
    /// Useful for model properties that are collections (inventory, quests, etc.).
    /// </summary>
    [Preserve]
    public sealed class ObservableList<T>
    {
        private readonly List<T> _items = new();

        // Callbacks
        private Action<int, T> _onAdded;   // (index, item)
        private Action<int, T> _onRemoved; // (index, item)
        private Action _onCleared;

        // ── Access ─────────────────────────────────────────────
        public int Count => _items.Count;
        public T this[int index]
        {
            get => _items[index];
            set
            {
                _items[index] = value;
                // Optional: fire a "changed at index" callback if needed
            }
        }

        public ReadOnlyListWrapper<T> AsReadOnly() => new(_items);

        // ── Mutation ───────────────────────────────────────────
        public void Add(T item)
        {
            var index = _items.Count;
            _items.Add(item);
            _onAdded?.Invoke(index, item);
        }

        public bool Remove(T item)
        {
            var index = _items.IndexOf(item);
            if (index < 0) return false;
            _items.RemoveAt(index);
            _onRemoved?.Invoke(index, item);
            return true;
        }

        public void RemoveAt(int index)
        {
            var item = _items[index];
            _items.RemoveAt(index);
            _onRemoved?.Invoke(index, item);
        }

        public void Clear()
        {
            _items.Clear();
            _onCleared?.Invoke();
        }

        public bool Contains(T item) => _items.Contains(item);
        public int IndexOf(T item) => _items.IndexOf(item);

        // ── Observation ────────────────────────────────────────
        public void OnAdded(Action<int, T> handler) => _onAdded = handler;
        public void OnRemoved(Action<int, T> handler) => _onRemoved = handler;
        public void OnCleared(Action handler) => _onCleared = handler;

        public void ClearAllCallbacks()
        {
            _onAdded = null;
            _onRemoved = null;
            _onCleared = null;
        }

        // ── Enumeration ────────────────────────────────────────
        public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
    }

    /// <summary>Minimal read-only wrapper to avoid exposing List mutators.</summary>
    [Preserve]
    public readonly struct ReadOnlyListWrapper<T>
    {
        private readonly List<T> _source;
        internal ReadOnlyListWrapper(List<T> source) => _source = source;
        public int Count => _source.Count;
        public T this[int index] => _source[index];
        public List<T>.Enumerator GetEnumerator() => _source.GetEnumerator();
    }
}
