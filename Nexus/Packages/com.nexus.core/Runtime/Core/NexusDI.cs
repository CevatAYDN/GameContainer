using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nexus.Core
{
    public class NexusDI : IDisposable
    {
        private readonly NexusDI _parent;
        private readonly Dictionary<Type, Binding> _bindings = new();
        private readonly HashSet<object> _resolvedSingletons = new();

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
                args[i] = Resolve(parameters[i].ParameterType);
            }

            return targetCtor.Invoke(args);
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
    }
}
