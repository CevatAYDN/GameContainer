using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// A lightweight, zero-additional-allocation (steady-state) observable value wrapper.
    ///
    /// Supports multicast subscriptions. Subscribe/unsubscribe may allocate during
    /// initial registration (list growth), but the notification hot path is allocation-free.
    ///
    /// Use in any model that needs to notify listeners when a value changes.
    /// <code>
    /// public class PlayerModel : IReactiveModel
    /// {
    ///     public readonly ObservableProperty&lt;int&gt; Score = new(0);
    ///     public readonly ObservableProperty&lt;string&gt; Name = new("Player");
    /// }
    /// </code>
    /// </summary>
    [Preserve]
    public sealed class ObservableProperty<T>
    {
        // ── State ──────────────────────────────────────────────
        private T _value;
        // N1: handler list, zero-GC snapshot cache, dirty flag and handler lock now
        // live once in the shared SecureObserverSet<T> core instead of being copied here.
        private readonly SecureObserverSet<T> _observers = new();
        private bool _isNotifying; // P2-3 fix: reentrancy guard
        private bool _hasPendingReentrantValue;
        private T _pendingReentrantValue;

        // ── Construction ───────────────────────────────────────
        /// <summary>Creates an observable property with the given initial value.</summary>
        public ObservableProperty(T initialValue = default)
        {
            _value = initialValue;
        }

        // ── Value ──────────────────────────────────────────────
        /// <summary>Gets or sets the current value. Setting triggers OnChanged without heap allocations.</summary>
        public T Value
        {
            get => _value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value))
                    return;

                if (_isNotifying)
                {
                    _pendingReentrantValue = value;
                    _hasPendingReentrantValue = true;
                    return;
                }

                var old = _value;
                _value = value;
                Action<T, T>[] snapshot = _observers.GetSnapshot();
                if (snapshot != null)
                {
                    _isNotifying = true;
                    try
                    {
                        while (true)
                        {
                            _hasPendingReentrantValue = false;
                            for (int i = 0; i < snapshot.Length; i++)
                            {
                                snapshot[i]?.Invoke(old, _value);
                            }
                            if (!_hasPendingReentrantValue) break;
                            old = _value;
                            _value = _pendingReentrantValue;
                            snapshot = _observers.GetSnapshot();
                            if (snapshot == null) break;
                        }
                    }
                    finally
                    {
                        _isNotifying = false;
                        _hasPendingReentrantValue = false;
                    }
                }
            }
        }

        /// <summary>Sets the underlying value without firing the change callback.</summary>
        public void SetWithoutNotify(T value)
        {
            _value = value;
        }

        // ── Observation ────────────────────────────────────────
        /// <summary>Subscribes a handler invoked when the value changes.</summary>
        public void OnChanged(Action<T, T> handler) => _observers.OnChanged(handler);

        /// <summary>Unsubscribes a previously added handler.</summary>
        public void RemoveOnChanged(Action<T, T> handler) => _observers.RemoveOnChanged(handler);

        /// <summary>Removes all change handlers.</summary>
        public void ClearOnChanged() => _observers.Clear();

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

        // N1: the three callback channels share the SnapshotDelegateSet core (dedupe +
        // zero-GC snapshot cache) instead of hand-rolled lists + per-mutation ToArray copies.
        private readonly SnapshotDelegateSet<Action<int, T>> _onAdded = new();
        private readonly SnapshotDelegateSet<Action<int, T>> _onRemoved = new();
        private readonly SnapshotDelegateSet<Action> _onCleared = new();

        // E-10 fix: snapshot + lock pattern (like ObservableProperty) for reentrancy safety
        private readonly object _eventLock = new();
        private bool _isNotifying;

        // ── Access ─────────────────────────────────────────────
        public int Count => _items.Count;
        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public ReadOnlyListWrapper<T> AsReadOnly() => new(_items);

        // ── Mutation ───────────────────────────────────────────
        public void Add(T item)
        {
            Action<int, T>[] addedSnapshot = null;
            int index;
            lock (_eventLock)
            {
                index = _items.Count;
                _items.Add(item);
                if (!_isNotifying)
                    addedSnapshot = _onAdded.GetSnapshot();
            }
            if (addedSnapshot != null)
            {
                _isNotifying = true;
                try
                {
                    for (int i = 0; i < addedSnapshot.Length; i++)
                        addedSnapshot[i]?.Invoke(index, item);
                }
                finally { _isNotifying = false; }
            }
        }

        public bool Remove(T item)
        {
            Action<int, T>[] removedSnapshot = null;
            int index;
            lock (_eventLock)
            {
                index = _items.IndexOf(item);
                if (index < 0) return false;
                _items.RemoveAt(index);
                if (!_isNotifying)
                    removedSnapshot = _onRemoved.GetSnapshot();
            }
            if (removedSnapshot != null)
            {
                _isNotifying = true;
                try
                {
                    for (int i = 0; i < removedSnapshot.Length; i++)
                        removedSnapshot[i]?.Invoke(index, item);
                }
                finally { _isNotifying = false; }
            }
            return true;
        }

        public void RemoveAt(int index)
        {
            Action<int, T>[] removedSnapshot = null;
            T item;
            lock (_eventLock)
            {
                item = _items[index];
                _items.RemoveAt(index);
                if (!_isNotifying)
                    removedSnapshot = _onRemoved.GetSnapshot();
            }
            if (removedSnapshot != null)
            {
                _isNotifying = true;
                try
                {
                    for (int i = 0; i < removedSnapshot.Length; i++)
                        removedSnapshot[i]?.Invoke(index, item);
                }
                finally { _isNotifying = false; }
            }
        }

        public void Clear()
        {
            Action[] clearedSnapshot = null;
            lock (_eventLock)
            {
                _items.Clear();
                if (!_isNotifying)
                    clearedSnapshot = _onCleared.GetSnapshot();
            }
            if (clearedSnapshot != null)
            {
                _isNotifying = true;
                try
                {
                    for (int i = 0; i < clearedSnapshot.Length; i++)
                        clearedSnapshot[i]?.Invoke();
                }
                finally { _isNotifying = false; }
            }
        }

        public bool Contains(T item) => _items.Contains(item);
        public int IndexOf(T item) => _items.IndexOf(item);

        // ── Observation ────────────────────────────────────────
        // B4 fix preserved: registration dedupes via the shared core — registering the
        // same handler twice previously invoked it twice (SecureObservable never did).
        public void OnAdded(Action<int, T> handler) => _onAdded.Add(handler);
        public void RemoveOnAdded(Action<int, T> handler) => _onAdded.Remove(handler);
        public void OnRemoved(Action<int, T> handler) => _onRemoved.Add(handler);
        public void RemoveOnRemoved(Action<int, T> handler) => _onRemoved.Remove(handler);
        public void OnCleared(Action handler) => _onCleared.Add(handler);
        public void RemoveOnCleared(Action handler) => _onCleared.Remove(handler);

        public void ClearAllCallbacks()
        {
            _onAdded.Clear();
            _onRemoved.Clear();
            _onCleared.Clear();
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
