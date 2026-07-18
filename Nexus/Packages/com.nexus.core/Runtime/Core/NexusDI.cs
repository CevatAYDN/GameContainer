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
        private readonly ConcurrentDictionary<Type, Binding> _bindings = new();
        private readonly HashSet<object> _resolvedSingletons = new();
        private volatile bool _disposed;

        private static readonly ConcurrentDictionary<Type, Action<object, NexusDI>> s_customInjectors = new();

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
                            if (targetCtor != null)
                                throw new InvalidOperationException($"Multiple constructors marked with [Inject] in {t.FullName}. Only one injected constructor is allowed.");
                            targetCtor = ctor;
                        }
                    }

                    if (targetCtor == null)
                    {
                        if (constructors.Length == 1)
                        {
                            targetCtor = constructors[0];
                        }
                        else
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

                            if (targetCtor == null)
                            {
                                throw new InvalidOperationException($"No suitable constructor found for type {t.FullName}. A type must either have a parameterless constructor or a constructor decorated with [Inject].");
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

        // Cross-thread set to detect circular singleton construction.
        // ThreadStatic would not work because two threads could each see their own
        // empty set and both proceed to construct the same singleton.
        // P1-5 fix: these are PER-CONTAINER instance fields (previously static/global),
        // so parallel containers no longer serialize each other's singleton construction
        // or trigger false circular-dependency detection for the same Type.
        // Protected by _singletonLock; C# lock is reentrant so recursive Resolve() is safe.
        private readonly HashSet<Type> _constructingSingletons = new();
        private readonly object _singletonLock = new();

        private class Binding
        {
            public Type ConcreteType { get; set; }
            public volatile object Instance;
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
                lock (_singletonLock)
                {
                    _resolvedSingletons.Add(instance);
                }
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

        public T TryResolve<T>() where T : class
        {
            return IsRegistered(typeof(T)) ? Resolve<T>() : null;
        }

        public object TryResolve(Type type)
        {
            if (type == null) return null;
            return IsRegistered(type) ? Resolve(type) : null;
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
                        object instance = null;
                        lock (_singletonLock)
                        {
                            if (binding.Instance != null)
                                return binding.Instance;

                            if (!_constructingSingletons.Add(type))
                            {
                                throw new InvalidOperationException($"Circular dependency detected while resolving singleton {type.FullName}.");
                            }
                            addedToConstructing = true;

                            try
                            {
                                instance = CreateInstance(binding.ConcreteType);
                                binding.Instance = instance;
                                _resolvedSingletons.Add(instance);
                            }
                            finally
                            {
                                _constructingSingletons.Remove(type);
                                addedToConstructing = false;
                            }
                        }

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
                    if (addedToConstructing)
                    {
                        _constructingSingletons.Remove(type);
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

        /// <summary>
        /// P1-10 fix: returns an already-constructed singleton instance without
        /// triggering lazy construction. Used during context teardown so services
        /// that were never resolved are not instantiated just to be disposed.
        /// </summary>
        internal bool TryGetExistingInstance(Type type, out object instance)
        {
            instance = null;
            if (type == null) return false;

            if (_bindings.TryGetValue(type, out var binding) && binding.Instance != null)
            {
                instance = binding.Instance;
                return true;
            }

            return _parent != null && _parent.TryGetExistingInstance(type, out instance);
        }

        public void Inject(object instance)
        {
            if (instance == null) return;

            if (ExternalAdapter != null)
            {
                // P2-13 fix: when an external DI adapter is installed, it owns injection
                // for the instance. Returning here prevents double/conflicting injection
                // by Nexus's own reflection-based injector.
                ExternalAdapter.Inject(instance);
                return;
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
                var resolvedValue = TryResolve(f.Type);
                if (resolvedValue != null)
                {
                    f.Field.SetValue(instance, resolvedValue);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                else
                {
                    // P1-4 fix: surface silently-missing [Inject] dependencies in dev builds.
                    NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{f.Type.FullName}' for field '{type.FullName}.{f.Field.Name}' is not registered; the field was left null.");
                }
#endif
            }

            // Inject properties
            for (int i = 0; i < meta.Properties.Length; i++)
            {
                var p = meta.Properties[i];
                var resolvedValue = TryResolve(p.Type);
                if (resolvedValue != null)
                {
                    p.Property.SetValue(instance, resolvedValue);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                else
                {
                    // P1-4 fix: surface silently-missing [Inject] dependencies in dev builds.
                    NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{p.Type.FullName}' for property '{type.FullName}.{p.Property.Name}' is not registered; the property was left null.");
                }
#endif
            }

            // Inject methods (e.g. Construct)
            for (int i = 0; i < meta.Methods.Length; i++)
            {
                var m = meta.Methods[i];
                var args = new object[m.ParameterTypes.Length];
                for (int j = 0; j < m.ParameterTypes.Length; j++)
                {
                    args[j] = TryResolve(m.ParameterTypes[j]);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (args[j] == null)
                    {
                        // P1-4 fix: surface silently-missing [Inject] dependencies in dev builds.
                        NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{m.ParameterTypes[j].FullName}' for method '{type.FullName}.{m.Method.Name}' is not registered; null was passed.");
                    }
#endif
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
                args[i] = TryResolve(paramTypes[i]);
            }

            try
            {
                return meta.Constructor.Invoke(args);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw; // unreachable
            }
        }

        public IEnumerable<object> GetActiveSingletons()
        {
            // P1-5 fix: return a snapshot instead of the live set to avoid
            // collection-modified exceptions during enumeration.
            lock (_singletonLock)
            {
                return new List<object>(_resolvedSingletons);
            }
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
            HashSet<object> singletonsCopy;
            lock (_singletonLock)
            {
                singletonsCopy = new HashSet<object>(_resolvedSingletons);
                _resolvedSingletons.Clear();
            }

            foreach (var instance in singletonsCopy)
            {
                if (!alreadyDisposed.Add(instance)) continue;
                try
                {
                    if (instance is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    else if (instance is IAsyncDisposable asyncDisposable)
                    {
                        // P1-6 fix: never block the main thread on DisposeAsync — if the
                        // user's DisposeAsync resumes on the Unity SynchronizationContext,
                        // GetAwaiter().GetResult() would deadlock. Run fire-and-forget with
                        // error capture instead; prefer DisposeAsync() for deterministic
                        // async teardown.
                        SafeAsyncRunner.Run(() => asyncDisposable.DisposeAsync(),
                            $"Async disposal of singleton '{instance.GetType().FullName}' failed");
                    }
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Error disposing singleton {instance.GetType().FullName}: {ex.Message}");
                }
            }
            _bindings.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var alreadyDisposed = new HashSet<object>();
            HashSet<object> singletonsCopy;
            lock (_singletonLock)
            {
                singletonsCopy = new HashSet<object>(_resolvedSingletons);
                _resolvedSingletons.Clear();
            }

            foreach (var instance in singletonsCopy)
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
                        NexusRuntime.Logger?.LogError($"[Nexus] Error disposing singleton {instance.GetType().FullName}: {ex.Message}");
                    }
                }
            }
            _bindings.Clear();
        }

        public static void ClearCaches()
        {
            s_customInjectors.Clear();
            s_injectMetadataCache.Clear();
            s_clearMetadataCache.Clear();
            // Note: singleton-construction tracking is per-container (P1-5 fix),
            // so there is no global construction state left to clear here.
        }
    }
}
