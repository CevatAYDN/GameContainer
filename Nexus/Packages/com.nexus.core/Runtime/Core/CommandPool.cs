using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public class CommandPool
    {
        private readonly Type _commandType;
        private readonly Func<object> _factory;
        private readonly Stack<object> _pool = new();

        public CommandPool(Type commandType, Func<object> factory, int initialSize = 0)
        {
            _commandType = commandType;
            _factory = factory;

            for (int i = 0; i < initialSize; i++)
            {
                _pool.Push(_factory());
            }
        }

        public object Get()
        {
            if (_pool.Count > 0)
            {
                return _pool.Pop();
            }
            return _factory();
        }

        public void Return(object command)
        {
            if (command == null) return;
            
            Cleanup(command);
            _pool.Push(command);
        }

        private void Cleanup(object command)
        {
            if (command is IResettable resettable)
            {
                resettable.Reset();
            }

            NexusDI.ClearInjectedReferences(command);
        }

        public void Clear()
        {
            _pool.Clear();
        }
    }

    public class CommandPoolManager
    {
        private readonly NexusDI _container;
        private readonly Dictionary<Type, CommandPool> _pools = new();
        private readonly int _initialSize;

        public CommandPoolManager(NexusDI container, int initialSize = 4)
        {
            _container = container;
            _initialSize = initialSize;
        }

        public object GetCommand(Type commandType)
        {
            if (!_pools.TryGetValue(commandType, out var pool))
            {
                pool = new CommandPool(commandType, () => _container.Resolve(commandType), _initialSize);
                _pools[commandType] = pool;
            }
            return pool.Get();
        }

        public void ReturnCommand(Type commandType, object command)
        {
            if (_pools.TryGetValue(commandType, out var pool))
            {
                pool.Return(command);
            }
        }

        public void Clear()
        {
            foreach (var pool in _pools.Values)
            {
                pool.Clear();
            }
            _pools.Clear();
        }
    }
}
