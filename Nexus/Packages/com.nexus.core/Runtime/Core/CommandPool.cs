using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>Read-only live utilization snapshot of a single <see cref="CommandPool"/>.</summary>
    public readonly struct CommandPoolStats
    {
        public readonly Type CommandType;
        public readonly int Available;
        public readonly int MaxSize;
        public readonly long TotalGets;
        public readonly long TotalCreated;
        public readonly long TotalReturns;
        public readonly long TotalDiscarded;

        public CommandPoolStats(Type commandType, int available, int maxSize,
            long totalGets, long totalCreated, long totalReturns, long totalDiscarded)
        {
            CommandType = commandType;
            Available = available;
            MaxSize = maxSize;
            TotalGets = totalGets;
            TotalCreated = totalCreated;
            TotalReturns = totalReturns;
            TotalDiscarded = totalDiscarded;
        }

        /// <summary>Fraction of Get() calls served from the pool rather than freshly created (0..1).</summary>
        public float ReuseRatio => TotalGets > 0 ? (float)(TotalGets - TotalCreated) / TotalGets : 0f;
    }

    /// <summary>
    /// Object pool for command instances. Reuses command objects to reduce GC pressure.
    /// Calls <see cref="IResettable.Reset"/> on pooled commands that implement it before returning them to the pool.
    /// Emits a warning if a pooled command type has mutable fields but does not implement <see cref="IResettable"/>.
    /// </summary>
    [Preserve]
    public class CommandPool
    {
        private readonly Type _commandType;
        private readonly Func<object> _factory;
        private readonly Stack<object> _pool = new();

        // Instances currently held by the pool. Guards against double-return, which would
        // otherwise put the same instance in the pool twice (later producing two Get() results
        // pointing at the same object) and would re-run cleanup on an instance in active use.
        private class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private readonly HashSet<object> _pooledInstances = new(ReferenceComparer.Instance);
        private readonly object _poolLock = new();
        private readonly int _maxSize;
        private static readonly HashSet<Type> s_stateLeakWarningIssued = new();

        // Editor introspection (G-4): cumulative utilization counters.
        private long _totalGets;
        private long _totalCreated;
        private long _totalReturns;
        private long _totalDiscarded;

        /// <summary>Creates a new command pool for the specified command type.</summary>
        /// <param name="commandType">The <see cref="Type"/> of the command to pool.</param>
        /// <param name="factory">Factory delegate that creates new command instances.</param>
        /// <param name="initialSize">Number of pre-allocated instances.</param>
        /// <param name="maxSize">Maximum pool capacity (beyond this, returned commands are discarded).</param>
        public CommandPool(Type commandType, Func<object> factory, int initialSize = 0, int maxSize = 64)
        {
            _commandType = commandType;
            _factory = factory;
            _maxSize = maxSize;

            for (int i = 0; i < initialSize; i++)
            {
                var instance = _factory();
                _pool.Push(instance);
                _pooledInstances.Add(instance);
            }

            WarnIfStateLeakRisk(commandType);
        }

        private static void WarnIfStateLeakRisk(Type type)
        {
            if (typeof(IResettable).IsAssignableFrom(type)) return;

            // Claim the warning slot under the lock FIRST, then scan + emit OUTSIDE it:
            // the reflection scan is slow and must not serialize every pool construction
            // behind the global lock while it runs. A concurrent duplicate claim simply
            // returns before scanning.
            lock (s_stateLeakWarningIssued)
            {
                if (!s_stateLeakWarningIssued.Add(type)) return;
            }

            // Collect ALL risky (mutable, non-injected, non-primitive) fields
            // and report them together in a single warning instead of stopping at the first one.
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var riskyFields = new System.Text.StringBuilder();
            foreach (var field in fields)
            {
                if (field.IsInitOnly || field.IsLiteral) continue;
                // IsDefined scans metadata only — no attribute instantiation (GetCustomAttribute
                // allocates the attribute instance per member on this startup-time path).
                if (field.IsDefined(typeof(InjectAttribute), false)) continue;
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum) continue;

                if (riskyFields.Length > 0) riskyFields.Append(", ");
                riskyFields.Append(field.Name);
            }

            if (riskyFields.Length == 0) return;

            NexusRuntime.Logger?.LogWarning(
                $"[Nexus] Command '{type.Name}' has mutable field(s) [{riskyFields}] but does not implement IResettable. " +
                "State may leak across pooled command reuses. Implement IResettable.Reset() to clear state.");
        }

        /// <summary>Retrieves a command instance from the pool, or creates a new one if the pool is empty.
        /// Pooled instances were already reset on return (see <see cref="Return"/>), so no
        /// additional reset runs here.</summary>
        public object Get()
        {
            System.Threading.Interlocked.Increment(ref _totalGets);
            lock (_poolLock)
            {
                if (_pool.Count > 0)
                {
                    var instance = _pool.Pop();
                    _pooledInstances.Remove(instance);
                    // The T6 pop-side Reset() was REMOVED. Return() already
                    // resets every pooled instance exactly once — NexusDI.ClearInjectedReferences
                    // invokes IResettable.Reset() AND nulls [Inject] fields/properties/method
                    // params — so the pop-side call made every IResettable command pay two
                    // Reset() dispatches per fire (2× reflection `is` + 2× virtual calls on
                    // hot signals). Instances in the pool are therefore already clean.
                    return instance;
                }
            }
            System.Threading.Interlocked.Increment(ref _totalCreated);
            return _factory();
        }

        /// <summary>Returns a command to the pool after cleanup. Discards if the pool is full.</summary>
        /// <param name="command">The command instance to return. Null values are silently ignored.</param>
        public void Return(object command)
        {
            if (command == null) return;

            lock (_poolLock)
            {
                // Double-return guard FIRST (audit fix 2.4): an instance already in the pool
                // must not be pooled again — and must not be re-cleaned either. Previously
                // Cleanup ran before the guard, so a concurrent double-return reset the
                // pooled instance a second time, potentially clobbering state a fresh Get()
                // consumer had already started using.
                if (!_pooledInstances.Add(command))
                {
                    System.Threading.Interlocked.Increment(ref _totalDiscarded);
                    return;
                }

                try
                {
                    Cleanup(command);
                }
                catch
                {
                    // Roll back the membership marker so the instance is not left registered
                    // as pooled while never reaching the pool stack.
                    _pooledInstances.Remove(command);
                    throw;
                }

                if (_pool.Count < _maxSize)
                {
                    _pool.Push(command);
                    System.Threading.Interlocked.Increment(ref _totalReturns);
                    return;
                }

                // Pool full: roll back the membership marker before discarding.
                _pooledInstances.Remove(command);
                System.Threading.Interlocked.Increment(ref _totalDiscarded);
            }
        }

        private void Cleanup(object command)
        {
            if (command != null)
            {
                if (command is IResettableCommand resettable)
                    resettable.ResetState();
                NexusDI.ClearInjectedReferences(command);
            }
        }

        /// <summary>Clears all pooled instances.</summary>
        public void Clear()
        {
            lock (_poolLock)
            {
                _pool.Clear();
                _pooledInstances.Clear();
            }
        }

        internal static void ClearStateLeakWarningsStatic()
        {
            lock (s_stateLeakWarningIssued)
            {
                s_stateLeakWarningIssued.Clear();
            }
        }

        /// <summary>Editor introspection: current utilization snapshot of this pool.</summary>
        public CommandPoolStats GetStats()
        {
            int available;
            lock (_poolLock)
            {
                available = _pool.Count;
            }
            return new CommandPoolStats(
                _commandType, available, _maxSize,
                System.Threading.Interlocked.Read(ref _totalGets),
                System.Threading.Interlocked.Read(ref _totalCreated),
                System.Threading.Interlocked.Read(ref _totalReturns),
                System.Threading.Interlocked.Read(ref _totalDiscarded));
        }
    }

    /// <summary>
    /// Manages multiple <see cref="CommandPool"/> instances, one per command type.
    /// Provides centralized Get/Return operations for the command execution pipeline.
    /// </summary>
    public class CommandPoolManager
    {
        private readonly NexusDI _container;
        private readonly ConcurrentDictionary<Type, CommandPool> _pools = new();
        private readonly int _initialSize;
        private readonly int _maxSize;

        // Cached factory delegate (one per manager, NOT one per call). A lambda that
        // captures `this` would allocate a NEW closure object on EVERY GetCommand call —
        // i.e. once per command execution per signal fire — which is exactly the kind of
        // heap churn this framework exists to eliminate on mobile. Caching the delegate in
        // the constructor keeps the hot path allocation-free while staying on the classic
        // GetOrAdd(TKey, Func<TKey,TValue>) overload, which is available in .NET Standard
        // 2.0 / .NET Framework 4.x (the GetOrAdd<TArg>(..., TArg) overload that passes the
        // manager as an argument only exists in .NET Standard 2.1+, so it would break the
        // build under Unity's default .NET Standard 2.0 API compatibility level).
        private readonly Func<Type, CommandPool> _createPool;

        public CommandPoolManager(NexusDI container, int initialSize = 4, int maxSize = 64)
        {
            _container = container;
            _initialSize = initialSize;
            _maxSize = maxSize;
            _createPool = type => new CommandPool(type, () => _container.Resolve(type), _initialSize, _maxSize);
        }

        public object GetCommand(Type commandType)
        {
            if (!_pools.TryGetValue(commandType, out var pool))
            {
                pool = _pools.GetOrAdd(commandType, _createPool);
            }
            return pool.Get();
        }

        /// <summary>Returns a command instance to its pool.</summary>
        /// <param name="commandType">The command type.</param>
        /// <param name="command">The command instance to return.</param>
        public void ReturnCommand(Type commandType, object command)
        {
            if (_pools.TryGetValue(commandType, out var pool))
            {
                pool.Return(command);
            }
        }

        /// <summary>Clears all command pools.</summary>
        public void Clear()
        {
            foreach (var kvp in _pools)
            {
                kvp.Value.Clear();
            }
            _pools.Clear();
        }

        /// <summary>Editor introspection: per-type utilization snapshot of all live command pools.</summary>
        public IReadOnlyList<CommandPoolStats> GetPoolStatsSnapshot()
        {
            var result = new List<CommandPoolStats>(_pools.Count);
            foreach (var kvp in _pools)
            {
                result.Add(kvp.Value.GetStats());
            }
            return result;
        }
    }

    internal static class CommandPoolStatics
    {
        internal static void ClearStateLeakWarnings()
        {
            CommandPool.ClearStateLeakWarningsStatic();
        }
    }
}
