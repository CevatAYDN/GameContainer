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
        private List<Action<T, T>> _handlers;
        private Action<T, T>[] _snapshotCache;
        private bool _snapshotDirty;
        private bool _isNotifying; // P2-3 fix: reentrancy guard
        private bool _hasPendingReentrantValue;
        private T _pendingReentrantValue;
        private readonly object _handlersLock = new();

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
                    _value = value;
                    _pendingReentrantValue = value;
                    _hasPendingReentrantValue = true;
                    return;
                }

                var old = _value;
                _value = value;
                Action<T, T>[] snapshot;
                lock (_handlersLock)
                {
                    if (_snapshotDirty)
                    {
                        _snapshotCache = _handlers != null && _handlers.Count > 0 ? _handlers.ToArray() : null;
                        _snapshotDirty = false;
                    }
                    snapshot = _snapshotCache;
                }
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
                            lock (_handlersLock)
                            {
                                if (_snapshotDirty)
                                {
                                    _snapshotCache = _handlers != null && _handlers.Count > 0 ? _handlers.ToArray() : null;
                                    _snapshotDirty = false;
                                }
                                snapshot = _snapshotCache;
                            }
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
        public void OnChanged(Action<T, T> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<T, T>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        /// <summary>Unsubscribes a previously added handler.</summary>
        public void RemoveOnChanged(Action<T, T> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                if (_handlers != null && _handlers.Remove(handler))
                {
                    _snapshotDirty = true;
                }
            }
        }

        /// <summary>Removes all change handlers.</summary>
        public void ClearOnChanged()
        {
            lock (_handlersLock)
            {
                _handlers?.Clear();
                _snapshotCache = null;
                _snapshotDirty = false;
            }
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

        // E-10 fix: use snapshot + lock pattern (like ObservableProperty) for reentrancy safety
        private readonly object _eventLock = new();
        private List<Action<int, T>> _onAdded;
        private List<Action<int, T>> _onRemoved;
        private List<Action> _onCleared;
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
                if (!_isNotifying && _onAdded != null)
                    addedSnapshot = _onAdded.ToArray();
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
                if (!_isNotifying && _onRemoved != null)
                    removedSnapshot = _onRemoved.ToArray();
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
                if (!_isNotifying && _onRemoved != null)
                    removedSnapshot = _onRemoved.ToArray();
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
                if (!_isNotifying && _onCleared != null)
                    clearedSnapshot = _onCleared.ToArray();
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
        // B4 fix: handler registration dedupes like SecureObserverSet<T> — registering the
        // same handler twice previously invoked it twice (SecureObservable never did).
        public void OnAdded(Action<int, T> handler)
        {
            if (handler == null) return;
            lock (_eventLock)
            {
                _onAdded ??= new List<Action<int, T>>(2);
                if (!_onAdded.Contains(handler)) _onAdded.Add(handler);
            }
        }
        public void RemoveOnAdded(Action<int, T> handler)
        {
            if (handler == null) return;
            lock (_eventLock)
            {
                _onAdded?.Remove(handler);
            }
        }

        public void OnRemoved(Action<int, T> handler)
        {
            if (handler == null) return;
            lock (_eventLock)
            {
                _onRemoved ??= new List<Action<int, T>>(2);
                if (!_onRemoved.Contains(handler)) _onRemoved.Add(handler);
            }
        }
        public void RemoveOnRemoved(Action<int, T> handler)
        {
            if (handler == null) return;
            lock (_eventLock)
            {
                _onRemoved?.Remove(handler);
            }
        }

        public void OnCleared(Action handler)
        {
            if (handler == null) return;
            lock (_eventLock)
            {
                _onCleared ??= new List<Action>(2);
                if (!_onCleared.Contains(handler)) _onCleared.Add(handler);
            }
        }
        public void RemoveOnCleared(Action handler)
        {
            if (handler == null) return;
            lock (_eventLock)
            {
                _onCleared?.Remove(handler);
            }
        }

        public void ClearAllCallbacks()
        {
            lock (_eventLock)
            {
                _onAdded?.Clear();
                _onRemoved?.Clear();
                _onCleared?.Clear();
            }
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
