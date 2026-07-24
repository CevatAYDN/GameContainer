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
                _pool.Push(_factory());
            }

            WarnIfStateLeakRisk(commandType);
        }

        private static void WarnIfStateLeakRisk(Type type)
        {
            if (s_stateLeakWarningIssued.Contains(type)) return;
            if (typeof(IResettable).IsAssignableFrom(type)) return;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.IsInitOnly || field.IsLiteral) continue;
                if (field.GetCustomAttribute<InjectAttribute>() != null) continue;
                if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && field.FieldType != typeof(decimal)) continue;

                // Non-injected, non-readonly field could leak state across pool reuse
                if (s_stateLeakWarningIssued.Add(type))
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] Command '{type.Name}' has mutable field '{field.Name}' but does not implement IResettable. " +
                    "State may leak across pooled command reuses. Implement IResettable.Reset() to clear state.");
                    return;
                }
            }
        }

        /// <summary>Retrieves a command instance from the pool, or creates a new one if the pool is empty.</summary>
        public object Get()
        {
            System.Threading.Interlocked.Increment(ref _totalGets);
            lock (_poolLock)
            {
                if (_pool.Count > 0)
                {
                    return _pool.Pop();
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
            
            Cleanup(command);
            
            lock (_poolLock)
            {
                if (_pool.Count < _maxSize)
                {
                    _pool.Push(command);
                    System.Threading.Interlocked.Increment(ref _totalReturns);
                    return;
                }
            }
            System.Threading.Interlocked.Increment(ref _totalDiscarded);
        }

        private void Cleanup(object command)
        {
            NexusDI.ClearInjectedReferences(command);
        }

        /// <summary>Clears all pooled instances.</summary>
        public void Clear()
        {
            lock (_poolLock)
            {
                _pool.Clear();
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

        public CommandPoolManager(NexusDI container, int initialSize = 4, int maxSize = 64)
        {
            _container = container;
            _initialSize = initialSize;
            _maxSize = maxSize;
        }

        public object GetCommand(Type commandType)
        {
            var pool = _pools.GetOrAdd(commandType,
                type => new CommandPool(type, () => _container.Resolve(type), _initialSize, _maxSize));
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
