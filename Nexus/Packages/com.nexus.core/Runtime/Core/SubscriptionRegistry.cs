using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    /// <summary>
    /// Subscription node for the zero-allocation linked-list subscription registry.
    /// Public because <see cref="SubscriptionRegistry.SubscriptionsReadCopy"/> exposes it to
    /// dispatch iterators (the owning bus walks the linked list lock-free). Pooled to achieve
    /// 0-GC on Subscribe/Unsubscribe.
    /// </summary>
    public sealed class SubscriptionNode
    {
        public object Handler;
        public object RawSubscription;
        public bool IsActive = true;
        public bool IsAsync;
        public SubscriptionNode Next;

        /// <summary>Resets every field so the node can be returned to the pool.</summary>
        public void Reset()
        {
            Handler = null;
            RawSubscription = null;
            IsActive = true;
            IsAsync = false;
            Next = null;
        }
    }

    /// <summary>
    /// Thread-safe pool for SubscriptionNode instances — zero allocation on Subscribe/Unsubscribe.
    /// </summary>
    internal static class SubscriptionNodePool
    {
        private static readonly Stack<SubscriptionNode> s_pool = new();
        private static readonly object s_lock = new();

        public static SubscriptionNode Rent(object handler, object rawSub, bool isAsync)
        {
            lock (s_lock)
            {
                if (s_pool.Count > 0)
                {
                    var node = s_pool.Pop();
                    node.Handler = handler;
                    node.RawSubscription = rawSub;
                    node.IsActive = true;
                    node.IsAsync = isAsync;
                    node.Next = null;
                    return node;
                }
            }
            return new SubscriptionNode { Handler = handler, RawSubscription = rawSub, IsAsync = isAsync };
        }

        public static void Return(SubscriptionNode node)
        {
            node.Reset();
            lock (s_lock) { s_pool.Push(node); }
        }

        public static void Clear() { lock (s_lock) { s_pool.Clear(); } }
    }

    /// <summary>
    /// Subscription registry using a per-type linked list of pooled nodes. Node allocation is
    /// fully pooled (Subscribe/Unsubscribe never allocate a new node after warmup); the volatile
    /// read-copy dictionary is rebuilt per mutation, exactly matching SignalBus's subscription path.
    /// Readers get a volatile snapshot (Dictionary&lt;Type, SubscriptionNode&gt;) for lock-free iteration.
    /// <para>
    /// Dispatch-depth tracking lives in the owning bus (SignalBus tracks it via its own
    /// reentrancy counter); the registry exposes <see cref="SweepDeadNodes"/> so a dispatching
    /// bus can defer cleanup until dispatch unwinds, exactly like SignalBus's subscription path.
    /// </para>
    /// </summary>
    public sealed class SubscriptionRegistry : IDisposable
    {
        private readonly Dictionary<Type, SubscriptionNode> _subscriptions = new();
        private volatile Dictionary<Type, SubscriptionNode> _subscriptionsReadCopy = new();
        private readonly object _subLock = new();
        private bool _pendingCleanups;
        private readonly List<Type> _sweepKeysCache = new();

        // Dispatch-depth tracking for the immediate-when-idle sweep (see ImmediateSweepWhenIdle).
        // Shared across threads via Interlocked so a dispose on thread A defers while thread B
        // is mid-dispatch (a ThreadStatic depth would reintroduce the mid-iteration pooling race).
        private int _dispatchDepth;

        /// <summary>Gets the lock-free read copy for dispatch iteration.</summary>
        public IReadOnlyDictionary<Type, SubscriptionNode> SubscriptionsReadCopy => _subscriptionsReadCopy;

        /// <summary>True when at least one subscription is marked inactive and awaits a sweep.</summary>
        public bool HasPendingCleanups => _pendingCleanups;

        /// <summary>
        /// When true (set by an owning bus like SignalBus), an Unsubscribe that happens while NO
        /// dispatch is in progress sweeps the dead node immediately — the pre-refactor SignalBus
        /// behavior (immediate reclaim when s_stackDepth == 0). During dispatch the sweep stays
        /// deferred until the owning bus's ExitDispatch unwinds. Standalone registries keep the
        /// documented deferred-only contract so callers control the sweep explicitly (SR4).
        /// </summary>
        public bool ImmediateSweepWhenIdle { get; set; }

        // Enter/ExitDispatch take _subLock so the depth check inside Unsubscribe's immediate
        // sweep is ATOMIC against a dispatch starting: a sweep can never pool a node while a
        // fresh dispatch is about to iterate the shared chain (the residual TOCTOU a lock-free
        // Interlocked depth would leave). The uncontended lock costs ~20ns on the hot path
        // (well under the benchmark limits) and allocates nothing.

        /// <summary>Marks the start of a dispatch; the owning bus wraps its dispatch body with this.</summary>
        public void EnterDispatch()
        {
            lock (_subLock) { _dispatchDepth++; }
        }

        /// <summary>Marks the end of a dispatch; sweeps deferred cleanups when the last one unwinds.</summary>
        public void ExitDispatch()
        {
            lock (_subLock)
            {
                _dispatchDepth--;
                // Sweep INSIDE the lock (SweepDeadNodes re-enters the same monitor on this
                // thread): EnterDispatch also takes _subLock, so no new dispatch can start
                // between the depth check and the node pooling — airtight against the TOCTOU.
                if (_dispatchDepth == 0 && _pendingCleanups)
                {
                    SweepDeadNodes();
                }
            }
        }

        /// <summary>
        /// Inserts a subscription into the per-type linked list using a pooled node and rebuilds
        /// the volatile read copy. Shared by the registry's own Subscribe/SubscribeAsync and by
        /// <see cref="SignalBus"/> (which supplies its own subscription wrapper so it can sweep
        /// immediately when nothing is dispatching).
        /// </summary>
        public void AddSubscription(Type signalType, object rawSubscription, object handler, bool isAsync)
        {
            lock (_subLock)
            {
                _subscriptions.TryGetValue(signalType, out var head);
                var node = SubscriptionNodePool.Rent(handler, rawSubscription, isAsync);
                node.Next = head;
                _subscriptions[signalType] = node;
                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);
            }
        }

        /// <summary>Subscribes a synchronous handler to a signal type.</summary>
        public ISignalSubscription Subscribe<T>(Action<T> handler, CancellationToken lifetimeToken) where T : struct
        {
            var type = typeof(T);
            SignalSubscription<T> sub = null;
            // Closure captures `sub` itself so Unsubscribe matches by RawSubscription identity
            // (same pattern as SignalBus's subscription path).
            sub = new SignalSubscription<T>(handler, lifetimeToken, () => Unsubscribe(type, sub));
            AddSubscription(type, sub, handler, isAsync: false);
            return sub;
        }

        /// <summary>Subscribes an asynchronous handler to a signal type.</summary>
        public ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler, CancellationToken lifetimeToken) where T : struct
        {
            var type = typeof(T);
            AsyncSignalSubscription<T> sub = null;
            sub = new AsyncSignalSubscription<T>(handler, lifetimeToken, () => Unsubscribe(type, sub));
            AddSubscription(type, sub, handler, isAsync: true);
            return sub;
        }

        /// <summary>
        /// Unsubscribes a specific subscription by its raw token: marks the node inactive and
        /// queues a cleanup. The caller (or the owning bus's dispatch unwind) must invoke
        /// <see cref="SweepDeadNodes"/> to reclaim the node. Sweeping is deliberately NOT done
        /// here: a dispatch loop iterating the read-copy snapshot shares node objects with the
        /// live registry, and pooling a node mid-iteration (Reset nulls Handler/Next, and the
        /// pool can re-rent it) would truncate the reader's chain or dispatch a wrong handler.
        /// This matches SignalBus's contract, where cleanup is deferred until dispatch unwinds.
        /// </summary>
        public void Unsubscribe(Type signalType, object rawSubscription)
        {
            lock (_subLock)
            {
                if (_subscriptions.TryGetValue(signalType, out var current))
                {
                    while (current != null)
                    {
                        if (current.RawSubscription == rawSubscription)
                        {
                            current.IsActive = false;
                            _pendingCleanups = true;
                            break;
                        }
                        current = current.Next;
                    }
                }

                // Immediate reclaim when the owning bus is idle (matches pre-refactor SignalBus).
                // Safe to sweep INSIDE the lock: EnterDispatch also takes _subLock, so no dispatch
                // can start between the depth check and the node pooling. During a dispatch the
                // bus's ExitDispatch sweeps on unwind instead. (SweepDeadNodes re-enters the same
                // monitor on this thread — C# locks are re-entrant.)
                if (ImmediateSweepWhenIdle && _dispatchDepth == 0)
                {
                    SweepDeadNodes();
                }
            }
        }

        /// <summary>Checks if a signal type has any active async subscriptions.</summary>
        public bool HasAsyncSubscriptions(Type signalType)
        {
            if (!_subscriptionsReadCopy.TryGetValue(signalType, out var node))
                return false;

            var current = node;
            while (current != null)
            {
                if (current.IsActive && current.IsAsync)
                    return true;
                current = current.Next;
            }
            return false;
        }

        /// <summary>Sweeps dead (unsubscribed) nodes from all subscription lists.</summary>
        public void SweepDeadNodes()
        {
            lock (_subLock)
            {
                if (!_pendingCleanups) return;
                _pendingCleanups = false;

                var keys = _sweepKeysCache;
                keys.Clear();
                foreach (var key in _subscriptions.Keys)
                {
                    keys.Add(key);
                }

                foreach (var type in keys)
                {
                    if (_subscriptions.TryGetValue(type, out var current))
                    {
                        SubscriptionNode prev = null;
                        while (current != null)
                        {
                            if (!current.IsActive)
                            {
                                var next = current.Next;
                                if (prev == null)
                                {
                                    if (next == null) _subscriptions.Remove(type);
                                    else _subscriptions[type] = next;
                                }
                                else
                                {
                                    prev.Next = next;
                                }
                                var temp = current;
                                current = next;
                                SubscriptionNodePool.Return(temp);
                            }
                            else
                            {
                                prev = current;
                                current = current.Next;
                            }
                        }
                    }
                }

                // Rebuild read copy
                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);
            }
        }

        /// <summary>Unsubscribes everything and returns all pooled nodes.</summary>
        public void Dispose()
        {
            lock (_subLock)
            {
                foreach (var node in _subscriptions.Values)
                {
                    var current = node;
                    while (current != null)
                    {
                        var next = current.Next;
                        SubscriptionNodePool.Return(current);
                        current = next;
                    }
                }
                _subscriptions.Clear();
                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>();
                _pendingCleanups = false;
                SubscriptionNodePool.Clear();
            }
        }
    }
}
