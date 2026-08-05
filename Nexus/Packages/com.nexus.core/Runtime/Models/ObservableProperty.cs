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
        // Audit fix 1.3: the old `volatile bool` guard was check-then-set — two threads
        // could both observe "not notifying" and double-dispatch (subscribers saw the same
        // old value twice and mediator/view state could desync). The guard now lives under
        // a tiny lock: the claim, the reentrant queue-write, and the dispatcher's exit
        // decision are one atomic protocol, so a queued write can never be dropped and a
        // second dispatcher can never slip in. Dispatch itself still runs OUTSIDE the lock
        // (handlers never execute under _dispatchLock), so same-thread reentrancy cannot
        // deadlock and cross-thread writers block only for the ~20ns critical section.
        private readonly object _dispatchLock = new();
        private bool _isNotifying; // guarded by _dispatchLock
        private bool _hasPendingReentrantValue; // guarded by _dispatchLock
        private T _pendingReentrantValue; // guarded by _dispatchLock

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

                // Claim-or-queue under the lock (audit fix 1.3): exactly one thread becomes
                // the dispatcher; everyone else — including same-thread reentrant writes from
                // inside a handler — coalesces into the pending slot.
                lock (_dispatchLock)
                {
                    if (_isNotifying)
                    {
                        _pendingReentrantValue = value;
                        _hasPendingReentrantValue = true;
                        return;
                    }
                    _isNotifying = true;
                }

                try
                {
                    var old = _value;
                    _value = value;
                    while (true)
                    {
                        Action<T, T>[] snapshot = _observers.GetSnapshot();
                        if (snapshot != null)
                        {
                            for (int i = 0; i < snapshot.Length; i++)
                            {
                                snapshot[i]?.Invoke(old, _value);
                            }
                        }

                        // The exit decision and the guard clear are ONE critical section:
                        // a writer that queues after this point observes _isNotifying == false
                        // and becomes the dispatcher itself — the handoff cannot drop a write.
                        T pending;
                        bool hasPending;
                        lock (_dispatchLock)
                        {
                            hasPending = _hasPendingReentrantValue;
                            if (hasPending)
                            {
                                pending = _pendingReentrantValue;
                                _hasPendingReentrantValue = false;
                            }
                            else
                            {
                                _isNotifying = false;
                                pending = default;
                            }
                        }
                        if (!hasPending) break;
                        old = _value;
                        _value = pending;
                    }
                }
                finally
                {
                    // Defensive: an exception escaping a handler must not leave the guard
                    // claimed forever (all future writes would silently queue, never dispatch).
                    lock (_dispatchLock)
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

        // Fix: _isNotifying must be volatile so cross-thread visibility is guaranteed,
        // and all reads/writes stay within the same lock scope to prevent races between
        // concurrent Add/Remove calls on multiple threads.
        private readonly object _eventLock = new();
        private volatile bool _isNotifying;

        // M4: structural changes that arrived while a notification dispatch was in progress
        // (reentrant Add/Remove/Clear, same or other thread). Previously such nested
        // mutations silently SKIPPED their callbacks — handlers were never told the list
        // changed, so views could keep stale data while the backing list moved on. Changes
        // are now queued under _eventLock and dispatched once the outer notification loop
        // finishes (DrainPendingChanges), so every structural change is observed exactly
        // once. The queue only grows while a dispatch is active and is drained in the same
        // call stack, so steady state stays zero-alloc.
        private readonly List<PendingChange> _pendingChanges = new();

        private enum PendingChangeOp : byte { Add = 0, Remove = 1, Clear = 2 }

        private readonly struct PendingChange
        {
            public readonly PendingChangeOp Op;
            public readonly int Index;
            public readonly T Item;
            public PendingChange(PendingChangeOp op, int index, T item)
            {
                Op = op;
                Index = index;
                Item = item;
            }
        }

        // ── Access ─────────────────────────────────────────────
        // Audit fix 3.7: Count was taking _eventLock on every read — a 100-item inventory
        // bound every frame paid 100+ lock acquisitions. A volatile count field updated
        // under _eventLock on every mutation gives lock-free reads.
        private volatile int _count;
        // Audit fix 3.8: AsReadOnly allocated a full new List<T> on every call (per-frame
        // UI refresh churn). The snapshot is now version-cached: repeated calls between
        // mutations reuse the same immutable list (never mutated after publish, so stale
        // wrappers held by older callers stay consistent).
        private int _version;
        private List<T> _readOnlyCache;
        private int _readOnlyCacheVersion = -1;

        public int Count => _count;
        public T this[int index]
        {
            get { lock (_eventLock) return _items[index]; }
            set
            {
                lock (_eventLock)
                {
                    _items[index] = value;
                    _version++;
                }
            }
        }

        public ReadOnlyListWrapper<T> AsReadOnly()
        {
            lock (_eventLock)
            {
                if (_readOnlyCacheVersion != _version)
                {
                    _readOnlyCache = new List<T>(_items);
                    _readOnlyCacheVersion = _version;
                }
                return new ReadOnlyListWrapper<T>(_readOnlyCache);
            }
        }

        /// <summary>
        /// Audit fix 3.7: copies the current items into <paramref name="destination"/> under a
        /// SINGLE lock acquisition — UI bind loops no longer pay N+1 lock acquisitions per frame.
        /// Returns the number of items copied (the lesser of list count and destination length).
        /// </summary>
        public int CopyTo(T[] destination)
        {
            if (destination == null) return 0;
            lock (_eventLock)
            {
                int n = _items.Count < destination.Length ? _items.Count : destination.Length;
                _items.CopyTo(0, destination, 0, n);
                return n;
            }
        }

        // ── Mutation ───────────────────────────────────────────
        public void Add(T item)
        {
            int index;
            Action<int, T>[] addedSnapshot;
            lock (_eventLock)
            {
                index = _items.Count;
                _items.Add(item);
                _count = _items.Count;
                _version++;
                // M4: reentrant Add during an in-flight notification is QUEUED, not dropped.
                if (_isNotifying)
                {
                    _pendingChanges.Add(new PendingChange(PendingChangeOp.Add, index, item));
                    return;
                }
                // Capture snapshot inside the lock so handler set cannot change mid-dispatch
                addedSnapshot = _onAdded.GetSnapshot();
                if (addedSnapshot != null) _isNotifying = true;
            }

            if (addedSnapshot != null)
            {
                try
                {
                    for (int i = 0; i < addedSnapshot.Length; i++)
                        addedSnapshot[i]?.Invoke(index, item);
                }
                finally
                {
                    lock (_eventLock) _isNotifying = false;
                }
                DrainPendingChanges();
            }
        }

        public bool Remove(T item)
        {
            int index;
            Action<int, T>[] removedSnapshot;
            lock (_eventLock)
            {
                index = _items.IndexOf(item);
                if (index < 0) return false;
                _items.RemoveAt(index);
                _count = _items.Count;
                _version++;
                // M4: reentrant Remove during an in-flight notification is QUEUED, not dropped.
                if (_isNotifying)
                {
                    _pendingChanges.Add(new PendingChange(PendingChangeOp.Remove, index, item));
                    return true;
                }
                removedSnapshot = _onRemoved.GetSnapshot();
                if (removedSnapshot != null) _isNotifying = true;
            }

            if (removedSnapshot != null)
            {
                try
                {
                    for (int i = 0; i < removedSnapshot.Length; i++)
                        removedSnapshot[i]?.Invoke(index, item);
                }
                finally
                {
                    lock (_eventLock) _isNotifying = false;
                }
                DrainPendingChanges();
            }
            return true;
        }

        public void RemoveAt(int index)
        {
            T item;
            Action<int, T>[] removedSnapshot;
            lock (_eventLock)
            {
                item = _items[index];
                _items.RemoveAt(index);
                _count = _items.Count;
                _version++;
                // M4: reentrant RemoveAt during an in-flight notification is QUEUED, not dropped.
                if (_isNotifying)
                {
                    _pendingChanges.Add(new PendingChange(PendingChangeOp.Remove, index, item));
                    return;
                }
                removedSnapshot = _onRemoved.GetSnapshot();
                if (removedSnapshot != null) _isNotifying = true;
            }

            if (removedSnapshot != null)
            {
                try
                {
                    for (int i = 0; i < removedSnapshot.Length; i++)
                        removedSnapshot[i]?.Invoke(index, item);
                }
                finally
                {
                    lock (_eventLock) _isNotifying = false;
                }
                DrainPendingChanges();
            }
        }

        public void Clear()
        {
            Action[] clearedSnapshot;
            lock (_eventLock)
            {
                _items.Clear();
                _count = 0;
                _version++;
                // M4: reentrant Clear during an in-flight notification is QUEUED, not dropped.
                if (_isNotifying)
                {
                    _pendingChanges.Add(new PendingChange(PendingChangeOp.Clear, -1, default));
                    return;
                }
                clearedSnapshot = _onCleared.GetSnapshot();
                if (clearedSnapshot != null) _isNotifying = true;
            }

            if (clearedSnapshot != null)
            {
                try
                {
                    for (int i = 0; i < clearedSnapshot.Length; i++)
                        clearedSnapshot[i]?.Invoke();
                }
                finally
                {
                    lock (_eventLock) _isNotifying = false;
                }
                DrainPendingChanges();
            }
        }

        // ── M4: reentrant-notification drain ───────────────────
        /// <summary>
        /// Dispatches structural changes that arrived while an earlier notification was
        /// in flight. Runs after the outer dispatch unwinds; drains to empty so changes
        /// queued DURING this drain (deeper reentrancy) are also delivered. Each queued
        /// change is dispatched with the same snapshot-under-lock discipline as the
        /// primary mutations, so handler sets cannot change mid-dispatch.
        /// </summary>
        private void DrainPendingChanges()
        {
            while (true)
            {
                PendingChange[] pending;
                lock (_eventLock)
                {
                    if (_pendingChanges.Count == 0) return;
                    pending = _pendingChanges.ToArray();
                    _pendingChanges.Clear();
                }

                for (int i = 0; i < pending.Length; i++)
                {
                    switch (pending[i].Op)
                    {
                        case PendingChangeOp.Add:
                            DispatchPendingAdded(pending[i].Index, pending[i].Item);
                            break;
                        case PendingChangeOp.Remove:
                            DispatchPendingRemoved(pending[i].Index, pending[i].Item);
                            break;
                        case PendingChangeOp.Clear:
                            DispatchPendingCleared();
                            break;
                    }
                }
            }
        }

        private void DispatchPendingAdded(int index, T item)
        {
            Action<int, T>[] snapshot;
            lock (_eventLock)
            {
                snapshot = _onAdded.GetSnapshot();
                if (snapshot == null || snapshot.Length == 0) return;
                _isNotifying = true;
            }
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i]?.Invoke(index, item);
            }
            finally
            {
                lock (_eventLock) _isNotifying = false;
            }
        }

        private void DispatchPendingRemoved(int index, T item)
        {
            Action<int, T>[] snapshot;
            lock (_eventLock)
            {
                snapshot = _onRemoved.GetSnapshot();
                if (snapshot == null || snapshot.Length == 0) return;
                _isNotifying = true;
            }
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i]?.Invoke(index, item);
            }
            finally
            {
                lock (_eventLock) _isNotifying = false;
            }
        }

        private void DispatchPendingCleared()
        {
            Action[] snapshot;
            lock (_eventLock)
            {
                snapshot = _onCleared.GetSnapshot();
                if (snapshot == null || snapshot.Length == 0) return;
                _isNotifying = true;
            }
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i]?.Invoke();
            }
            finally
            {
                lock (_eventLock) _isNotifying = false;
            }
        }

        public bool Contains(T item) { lock (_eventLock) return _items.Contains(item); }
        public int IndexOf(T item) { lock (_eventLock) return _items.IndexOf(item); }

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
