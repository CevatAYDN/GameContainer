using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public interface IDependencyAdapter
    {
        object Resolve(Type type);
        void Inject(object instance);
        bool IsRegistered(Type type);
    }

    [Preserve]
    public class NexusDI : IDisposable, IAsyncDisposable
    {
        public IDependencyAdapter ExternalAdapter { get; set; }
        public int ActiveSingletonsCount => _resolvedSingletons.Count;
        private readonly NexusDI _parent;
        private readonly Dictionary<Type, Binding> _bindings = new();
        private readonly HashSet<object> _resolvedSingletons = new();
        private volatile bool _disposed;

        private static readonly Dictionary<Type, Action<object, NexusDI>> s_customInjectors = new();

        /// <summary>
        /// Registers a compile-time generated injector action for a class to bypass runtime reflection in AOT.
        /// </summary>
        public static void RegisterInjector<T>(Action<T, NexusDI> injector) where T : class
        {
            s_customInjectors[typeof(T)] = (instance, di) => injector((T)instance, di);
        }

        private class InjectableField
        {
            public FieldInfo Field { get; set; }
            public Type Type { get; set; }
        }
        private class InjectableProperty
        {
            public PropertyInfo Property { get; set; }
            public Type Type { get; set; }
        }
        private class InjectableMethod
        {
            public MethodInfo Method { get; set; }
            public Type[] ParameterTypes { get; set; }
        }
        private class InjectableMetadata
        {
            public InjectableField[] Fields { get; set; }
            public InjectableProperty[] Properties { get; set; }
            public InjectableMethod[] Methods { get; set; }
            public ConstructorInfo Constructor { get; set; }
            public Type[] ConstructorParameterTypes { get; set; }
        }
        private class ClearableMetadata
        {
            public FieldInfo[] Fields { get; set; }
            public PropertyInfo[] Properties { get; set; }
        }

        private static readonly ConcurrentDictionary<Type, InjectableMetadata> s_injectMetadataCache = new();
        private static readonly ConcurrentDictionary<Type, ClearableMetadata> s_clearMetadataCache = new();

        private static InjectableMetadata GetOrCreateInjectMetadata(Type type)
        {
            return s_injectMetadataCache.GetOrAdd(type, t =>
            {
                // Fields
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fieldList = new List<InjectableField>();
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<InjectAttribute>() != null)
                    {
                        if (field.FieldType.IsValueType)
                            throw new InvalidOperationException($"Cannot inject value type field {t.FullName}.{field.Name}. Nexus DI only supports reference-type dependencies.");
                        fieldList.Add(new InjectableField { Field = field, Type = field.FieldType });
                    }
                }

                // Properties
                var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var propList = new List<InjectableProperty>();
                foreach (var prop in properties)
                {
                    if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite)
                    {
                        if (prop.PropertyType.IsValueType)
                            throw new InvalidOperationException($"Cannot inject value type property {t.FullName}.{prop.Name}. Nexus DI only supports reference-type dependencies.");
                        propList.Add(new InjectableProperty { Property = prop, Type = prop.PropertyType });
                    }
                }

                // Methods
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var methodList = new List<InjectableMethod>();
                foreach (var method in methods)
                {
                    if (method.GetCustomAttribute<InjectAttribute>() != null)
                    {
                        var parameters = method.GetParameters();
                        var paramTypes = new Type[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            if (parameters[i].ParameterType.IsValueType)
                                throw new InvalidOperationException($"Cannot inject value type parameter {t.FullName}.{method.Name}({parameters[i].Name}). Nexus DI only supports reference-type dependencies.");
                            paramTypes[i] = parameters[i].ParameterType;
                        }
                        methodList.Add(new InjectableMethod { Method = method, ParameterTypes = paramTypes });
                    }
                }

                // Constructor
                ConstructorInfo targetCtor = null;
                var constructors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                if (constructors.Length > 0)
                {
                    foreach (var ctor in constructors)
                    {
                        if (ctor.GetCustomAttribute<InjectAttribute>() != null)
                        {
                            targetCtor = ctor;
                            break;
                        }
                    }

                    if (targetCtor == null)
                    {
                        // Try parameterless constructor first (safest)
                        foreach (var ctor in constructors)
                        {
                            if (ctor.GetParameters().Length == 0)
                            {
                                targetCtor = ctor;
                                break;
                            }
                        }

                        // Fallback: constructor with most reference-type parameters
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
                    }
                }

                Type[] ctorParamTypes = null;
                if (targetCtor != null)
                {
                    var parameters = targetCtor.GetParameters();
                    ctorParamTypes = new Type[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (parameters[i].ParameterType.IsValueType)
                            throw new InvalidOperationException($"Cannot inject value type constructor parameter {t.FullName}({parameters[i].Name}). Nexus DI only supports reference-type dependencies.");
                        ctorParamTypes[i] = parameters[i].ParameterType;
                    }
                }

                return new InjectableMetadata
                {
                    Fields = fieldList.ToArray(),
                    Properties = propList.ToArray(),
                    Methods = methodList.ToArray(),
                    Constructor = targetCtor,
                    ConstructorParameterTypes = ctorParamTypes
                };
            });
        }

        [ThreadStatic]
        private static HashSet<Type> s_resolutionStack;

        // Global (cross-thread) set to detect circular singleton construction.
        // ThreadStatic would not work because two threads could each see their own
        // empty set and both proceed to construct the same singleton.
        // Protected by s_singletonLock; C# lock is reentrant so recursive Resolve() is safe.
        private static readonly HashSet<Type> s_constructingSingletons = new();
        private static readonly object s_singletonLock = new();

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
            BindInstance(instance, disposeWithContainer: true);
        }

        public void BindInstance<T>(T instance, bool disposeWithContainer) where T : class
        {
            _bindings[typeof(T)] = new Binding
            {
                ConcreteType = typeof(T),
                Instance = instance,
                IsSingleton = true
            };
            if (disposeWithContainer)
            {
                _resolvedSingletons.Add(instance);
            }
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

            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type))
            {
                return ExternalAdapter.Resolve(type);
            }

            if (_bindings.TryGetValue(type, out var binding))
            {
                if (binding.Instance != null)
                {
                    if (s_constructingSingletons.Contains(type))
                    {
                        throw new InvalidOperationException($"Circular dependency detected: Type {type.FullName} is already in construction/injection phase.");
                    }
                    return binding.Instance;
                }

                if (binding.Factory != null)
                {
                    return binding.Factory();
                }

                if (s_resolutionStack == null)
                    s_resolutionStack = new HashSet<Type>();

                if (!s_resolutionStack.Add(type))
                {
                    throw new InvalidOperationException($"Circular dependency detected while resolving {type.FullName}. Resolution chain forms a cycle.");
                }

                bool addedToConstructing = false;
                try
                {
                    if (binding.IsSingleton)
                    {
                        // Double-checked locking to prevent two threads from creating
                        // the same singleton. C# lock is reentrant, so recursive Resolve()
                        // calls from Inject() on the same thread will not deadlock.
                        lock (s_singletonLock)
                        {
                            if (binding.Instance != null)
                                return binding.Instance;

                            s_constructingSingletons.Add(type);
                            addedToConstructing = true;

                            var instance = CreateInstance(binding.ConcreteType);
                            binding.Instance = instance;
                            _resolvedSingletons.Add(instance);

                            Inject(instance);
                            return instance;
                        }
                    }

                    var transientInstance = CreateInstance(binding.ConcreteType);
                    Inject(transientInstance);
                    return transientInstance;
                }
                finally
                {
                    s_resolutionStack.Remove(type);
                    if (addedToConstructing)
                    {
                        s_constructingSingletons.Remove(type);
                    }
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
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type))
                return true;
            if (_bindings.ContainsKey(type))
                return true;
            return _parent != null && _parent.IsRegistered(type);
        }

        public void Inject(object instance)
        {
            if (instance == null) return;

            if (ExternalAdapter != null)
            {
                ExternalAdapter.Inject(instance);
            }

            var type = instance.GetType();

            if (s_customInjectors.TryGetValue(type, out var injector))
            {
                injector(instance, this);
                return;
            }
            
            var meta = GetOrCreateInjectMetadata(type);
            
            // Inject fields
            for (int i = 0; i < meta.Fields.Length; i++)
            {
                var f = meta.Fields[i];
                var resolvedValue = Resolve(f.Type);
                f.Field.SetValue(instance, resolvedValue);
            }

            // Inject properties
            for (int i = 0; i < meta.Properties.Length; i++)
            {
                var p = meta.Properties[i];
                var resolvedValue = Resolve(p.Type);
                p.Property.SetValue(instance, resolvedValue);
            }

            // Inject methods (e.g. Construct)
            for (int i = 0; i < meta.Methods.Length; i++)
            {
                var m = meta.Methods[i];
                var args = new object[m.ParameterTypes.Length];
                for (int j = 0; j < m.ParameterTypes.Length; j++)
                {
                    args[j] = Resolve(m.ParameterTypes[j]);
                }
                m.Method.Invoke(instance, args);
            }
        }

        private object CreateInstance(Type type)
        {
            var meta = GetOrCreateInjectMetadata(type);
            if (meta.Constructor == null)
            {
                // Fallback to parameterless constructor (even private ones)
                return Activator.CreateInstance(type, true);
            }

            var paramTypes = meta.ConstructorParameterTypes;
            var args = new object[paramTypes.Length];
            for (int i = 0; i < paramTypes.Length; i++)
            {
                args[i] = Resolve(paramTypes[i]);
            }

            return meta.Constructor.Invoke(args);
        }

        public IEnumerable<object> GetActiveSingletons()
        {
            return _resolvedSingletons;
        }

        public Dictionary<Type, object> GetRegisteredSingletons()
        {
            var result = new Dictionary<Type, object>();
            foreach (var kvp in _bindings)
            {
                if (kvp.Value.IsSingleton && kvp.Value.Instance != null)
                {
                    result[kvp.Key] = kvp.Value.Instance;
                }
            }
            if (_parent != null)
            {
                var parentSingletons = _parent.GetRegisteredSingletons();
                foreach (var kvp in parentSingletons)
                {
                    if (!result.ContainsKey(kvp.Key))
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Nulls out all [Inject]-annotated reference fields and writable properties on the given instance.
        /// Used by CommandPool and ViewBinder to prepare objects for pooling reuse.
        /// </summary>
        public static void ClearInjectedReferences(object instance)
        {
            if (instance == null) return;
            if (instance is IResettable resettable)
            {
                resettable.Reset();
            }
            var type = instance.GetType();

            var meta = s_clearMetadataCache.GetOrAdd(type, t =>
            {
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fieldList = new List<FieldInfo>();
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<InjectAttribute>() != null && !field.FieldType.IsValueType)
                    {
                        fieldList.Add(field);
                    }
                }

                var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var propList = new List<PropertyInfo>();
                foreach (var prop in properties)
                {
                    if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite && !prop.PropertyType.IsValueType)
                    {
                        propList.Add(prop);
                    }
                }

                return new ClearableMetadata
                {
                    Fields = fieldList.ToArray(),
                    Properties = propList.ToArray()
                };
            });

            for (int i = 0; i < meta.Fields.Length; i++)
            {
                meta.Fields[i].SetValue(instance, null);
            }

            for (int i = 0; i < meta.Properties.Length; i++)
            {
                meta.Properties[i].SetValue(instance, null);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var alreadyDisposed = new HashSet<object>();
            foreach (var instance in _resolvedSingletons)
            {
                if (instance is IDisposable disposable && alreadyDisposed.Add(instance))
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[Nexus] Error disposing singleton {instance.GetType().FullName}: {ex.Message}");
                    }
                }
            }
            _resolvedSingletons.Clear();
            _bindings.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var alreadyDisposed = new HashSet<object>();
            foreach (var instance in _resolvedSingletons)
            {
                if (alreadyDisposed.Add(instance))
                {
                    try
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
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[Nexus] Error disposing singleton {instance.GetType().FullName}: {ex.Message}");
                    }
                }
            }
            _resolvedSingletons.Clear();
            _bindings.Clear();
        }

        public static void ClearCaches()
        {
            s_customInjectors.Clear();
            s_injectMetadataCache.Clear();
            s_clearMetadataCache.Clear();
            lock (s_singletonLock)
            {
                s_constructingSingletons.Clear();
            }
        }
    }
}
