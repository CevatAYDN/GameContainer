using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public class NexusDI : IDisposable, IAsyncDisposable
    {
        private readonly NexusDI _parent;
        private readonly Dictionary<Type, Binding> _bindings = new();
        private readonly HashSet<object> _resolvedSingletons = new();

        [ThreadStatic]
        private static HashSet<Type> s_resolutionStack;

        private class Binding
        {
            public Type ConcreteType { get; set; }
            public object Instance { get; set; }
            public bool IsSingleton { get; set; }
            public Func<object> Factory { get; set; }
        }

        public NexusDI(NexusDI parent = null)
        {
            _parent = parent;
        }

        public void Bind<TInterface, TImplementation>(bool isSingleton = true) where TImplementation : class, TInterface
        {
            _bindings[typeof(TInterface)] = new Binding
            {
                ConcreteType = typeof(TImplementation),
                IsSingleton = isSingleton
            };
        }

        public void Bind<T>(bool isSingleton = true) where T : class
        {
            _bindings[typeof(T)] = new Binding
            {
                ConcreteType = typeof(T),
                IsSingleton = isSingleton
            };
        }

        public void Bind(Type type, bool isSingleton = true)
        {
            _bindings[type] = new Binding
            {
                ConcreteType = type,
                IsSingleton = isSingleton
            };
        }

        public void BindInstance<T>(T instance) where T : class
        {
            _bindings[typeof(T)] = new Binding
            {
                ConcreteType = typeof(T),
                Instance = instance,
                IsSingleton = true
            };
        }

        public void BindFactory<T>(Func<T> factory) where T : class
        {
            _bindings[typeof(T)] = new Binding
            {
                ConcreteType = typeof(T),
                Factory = factory,
                IsSingleton = false
            };
        }

        public T Resolve<T>() where T : class
        {
            return (T)Resolve(typeof(T));
        }

        public object Resolve(Type type)
        {
            if (type == typeof(NexusDI))
            {
                return this;
            }

            if (_bindings.TryGetValue(type, out var binding))
            {
                if (binding.Instance != null)
                {
                    return binding.Instance;
                }

                if (binding.Factory != null)
                {
                    return binding.Factory();
                }

                // Circular dependency detection
                if (s_resolutionStack == null)
                    s_resolutionStack = new HashSet<Type>();

                if (!s_resolutionStack.Add(type))
                {
                    s_resolutionStack.Clear();
                    throw new InvalidOperationException($"Circular dependency detected while resolving {type.FullName}. Resolution chain forms a cycle.");
                }

                try
                {
                    if (binding.IsSingleton)
                    {
                        var instance = CreateInstance(binding.ConcreteType);
                        binding.Instance = instance;
                        _resolvedSingletons.Add(instance);
                        Inject(instance);
                        return instance;
                    }

                    var transientInstance = CreateInstance(binding.ConcreteType);
                    Inject(transientInstance);
                    return transientInstance;
                }
                finally
                {
                    s_resolutionStack.Remove(type);
                }
            }

            if (_parent != null)
            {
                return _parent.Resolve(type);
            }

            throw new InvalidOperationException($"Dependency of type {type.FullName} is not registered.");
        }

        public bool IsRegistered(Type type)
        {
            if (_bindings.ContainsKey(type))
                return true;
            return _parent != null && _parent.IsRegistered(type);
        }

        public void Inject(object instance)
        {
            if (instance == null) return;

            var type = instance.GetType();
            
            // Inject fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<InjectAttribute>() != null)
                {
                    if (field.FieldType.IsValueType)
                        continue; // Value types cannot be DI-registered (Bind<T> has where T : class constraint)
                    var resolvedValue = Resolve(field.FieldType);
                    field.SetValue(instance, resolvedValue);
                }
            }

            // Inject properties
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite)
                {
                    if (prop.PropertyType.IsValueType)
                        continue; // Value types cannot be DI-registered
                    var resolvedValue = Resolve(prop.PropertyType);
                    prop.SetValue(instance, resolvedValue);
                }
            }

            // Inject methods (e.g. Construct)
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var method in methods)
            {
                if (method.GetCustomAttribute<InjectAttribute>() != null)
                {
                    var parameters = method.GetParameters();
                    var args = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (parameters[i].ParameterType.IsValueType)
                            continue; // Value types cannot be DI-registered
                        args[i] = Resolve(parameters[i].ParameterType);
                    }
                    method.Invoke(instance, args);
                }
            }
        }

        private object CreateInstance(Type type)
        {
            // Find constructor
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length == 0)
            {
                // Fallback to parameterless constructor (even private ones)
                return Activator.CreateInstance(type, true);
            }

            // Find constructor with [Inject]
            ConstructorInfo targetCtor = null;
            foreach (var ctor in constructors)
            {
                if (ctor.GetCustomAttribute<InjectAttribute>() != null)
                {
                    targetCtor = ctor;
                    break;
                }
            }

            // Fallback to the one with the most parameters, or default
            if (targetCtor == null)
            {
                targetCtor = constructors[0];
                for (int i = 1; i < constructors.Length; i++)
                {
                    if (constructors[i].GetParameters().Length > targetCtor.GetParameters().Length)
                    {
                        targetCtor = constructors[i];
                    }
                }
            }

            var parameters = targetCtor.GetParameters();
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType.IsValueType)
                    continue; // Value types cannot be DI-registered
                args[i] = Resolve(parameters[i].ParameterType);
            }

            return targetCtor.Invoke(args);
        }

        public IEnumerable<object> GetActiveSingletons()
        {
            return _resolvedSingletons;
        }

        /// <summary>
        /// Nulls out all [Inject]-annotated reference fields and writable properties on the given instance.
        /// Used by CommandPool and ViewBinder to prepare objects for pooling reuse.
        /// </summary>
        public static void ClearInjectedReferences(object instance)
        {
            var type = instance.GetType();

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<InjectAttribute>() != null && !field.FieldType.IsValueType)
                {
                    field.SetValue(instance, null);
                }
            }

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite && !prop.PropertyType.IsValueType)
                {
                    prop.SetValue(instance, null);
                }
            }
        }

        public void Dispose()
        {
            foreach (var instance in _resolvedSingletons)
            {
                if (instance is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _resolvedSingletons.Clear();
            _bindings.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var instance in _resolvedSingletons)
            {
                if (instance is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (instance is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _resolvedSingletons.Clear();
            _bindings.Clear();
        }
    }
}
