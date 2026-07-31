using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        // ─── Public surface: Bind, Resolve, IsRegistered ───
        public IDependencyAdapter ExternalAdapter { get; set; }
        public int ActiveSingletonsCount => _resolvedSingletons.Count;
        public bool StrictInjection { get; set; }
        internal readonly ConcurrentQueue<INexusService> _lazyServicesPendingInit = new();
        private readonly NexusDI _parent;
        private readonly ConcurrentDictionary<Type, Binding> _bindings = new();
        private readonly HashSet<object> _resolvedSingletons = new();
        private volatile bool _disposed;

        // Editor accessor properties — avoid fragile reflection in NexusEditorDataProvider / ExplorerPlugin.
        internal HashSet<object> EditorResolvedSingletons => _resolvedSingletons;

        private static readonly ConcurrentDictionary<Type, Action<object, NexusDI>> s_customInjectors = new();
        private static readonly ConcurrentDictionary<Type, Action<object>> s_customClearers = new();

        private readonly ConditionalWeakTable<object, PendingInjection> _pendingInjections = new();
        private readonly HashSet<Type> _constructingSingletons = new();
        private readonly object _singletonLock = new();
        private readonly Injector _injector;

        [ThreadStatic]
        private static HashSet<Type> s_resolutionStack;

        private class Binding
        {
            public Type ConcreteType { get; set; }
            public volatile object Instance;
            public bool IsSingleton { get; set; }
            public Func<object> Factory { get; set; }
        }

        // ─── Internal types shared by DI internals ───
        internal class InjectableField
        {
            public FieldInfo Field { get; set; }
            public Type Type { get; set; }
            public bool IsOptional { get; set; }
        }
        internal class InjectableProperty
        {
            public PropertyInfo Property { get; set; }
            public Type Type { get; set; }
            public bool IsOptional { get; set; }
        }
        internal class InjectableMethod
        {
            public MethodInfo Method { get; set; }
            public Type[] ParameterTypes { get; set; }
            public bool[] OptionalParameterMask { get; set; }
        }
        internal class InjectableMetadata
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
        private class PendingInjection
        {
            public readonly List<InjectableField> Fields = new();
            public readonly List<InjectableProperty> Properties = new();
            public readonly List<(InjectableMethod Method, int[] ParamIndices)> Methods = new();
        }

        // ─── Metadata cache (static, shared across containers) ───
        private static class MetadataCache
        {
            internal static readonly ConcurrentDictionary<Type, InjectableMetadata> InjectMeta = new();
            internal static readonly ConcurrentDictionary<Type, ClearableMetadata> ClearMeta = new();

            internal static InjectableMetadata GetOrCreateInjectMetadata(Type type)
            {
                return InjectMeta.GetOrAdd(type, t =>
                {
                    var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var fieldList = new List<InjectableField>();
                    foreach (var field in fields)
                    {
                        if (field.GetCustomAttribute<InjectAttribute>() != null)
                        {
                            if (field.FieldType.IsValueType)
                                throw new InvalidOperationException($"Cannot inject value type field {t.FullName}.{field.Name}. Nexus DI only supports reference-type dependencies.");
                            fieldList.Add(new InjectableField
                            {
                                Field = field,
                                Type = field.FieldType,
                                IsOptional = field.GetCustomAttribute<OptionalInjectAttribute>() != null
                            });
                        }
                    }

                    var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var propList = new List<InjectableProperty>();
                    foreach (var prop in properties)
                    {
                        if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite)
                        {
                            if (prop.PropertyType.IsValueType)
                                throw new InvalidOperationException($"Cannot inject value type property {t.FullName}.{prop.Name}. Nexus DI only supports reference-type dependencies.");
                            propList.Add(new InjectableProperty
                            {
                                Property = prop,
                                Type = prop.PropertyType,
                                IsOptional = prop.GetCustomAttribute<OptionalInjectAttribute>() != null
                            });
                        }
                    }

                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var methodList = new List<InjectableMethod>();
                    foreach (var method in methods)
                    {
                        if (method.GetCustomAttribute<InjectAttribute>() != null)
                        {
                            var parameters = method.GetParameters();
                            var paramTypes = new Type[parameters.Length];
                            var optionalMask = new bool[parameters.Length];
                            for (int i = 0; i < parameters.Length; i++)
                            {
                                if (parameters[i].ParameterType.IsValueType)
                                    throw new InvalidOperationException($"Cannot inject value type parameter {t.FullName}.{method.Name}({parameters[i].Name}). Nexus DI only supports reference-type dependencies.");
                                paramTypes[i] = parameters[i].ParameterType;
                                optionalMask[i] = parameters[i].GetCustomAttribute<OptionalInjectAttribute>() != null;
                            }
                            methodList.Add(new InjectableMethod { Method = method, ParameterTypes = paramTypes, OptionalParameterMask = optionalMask });
                        }
                    }

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

            internal static ClearableMetadata GetOrCreateClearMetadata(Type type)
            {
                return ClearMeta.GetOrAdd(type, t =>
                {
                    var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var fieldList = new List<FieldInfo>();
                    foreach (var field in fields)
                    {
                        if (field.GetCustomAttribute<InjectAttribute>() != null && !field.FieldType.IsValueType)
                            fieldList.Add(field);
                    }

                    var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var propList = new List<PropertyInfo>();
                    foreach (var prop in properties)
                    {
                        if (prop.GetCustomAttribute<InjectAttribute>() != null && prop.CanWrite && !prop.PropertyType.IsValueType)
                            propList.Add(prop);
                    }

                    return new ClearableMetadata { Fields = fieldList.ToArray(), Properties = propList.ToArray() };
                });
            }

            internal static void ClearAll()
            {
                InjectMeta.Clear();
                ClearMeta.Clear();
            }
        }

        // ─── Injector (instance-level injection logic) ───
        private class Injector
        {
            private readonly NexusDI _di;

            public Injector(NexusDI di) { _di = di; }

            public object CreateInstance(Type type)
            {
                var meta = MetadataCache.GetOrCreateInjectMetadata(type);
                if (meta.Constructor == null)
                    return Activator.CreateInstance(type, true);

                var paramTypes = meta.ConstructorParameterTypes;
                var args = new object[paramTypes.Length];
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    args[i] = _di.TryResolve(paramTypes[i]);
                    if (args[i] == null && _di.StrictInjection)
                    {
                        throw new InvalidOperationException(
                            $"Strict injection failed: constructor parameter {i} of type '{paramTypes[i].FullName}' on '{type.FullName}' is not registered.");
                    }
                }

                try { return meta.Constructor.Invoke(args); }
                catch (TargetInvocationException ex)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }

            public void Inject(object instance)
            {
                if (instance == null) return;

                if (_di.ExternalAdapter != null)
                {
                    _di.ExternalAdapter.Inject(instance);
                    return;
                }

                var type = instance.GetType();
                if (s_customInjectors.TryGetValue(type, out var injector))
                {
                    injector(instance, _di);
                    return;
                }

                var meta = MetadataCache.GetOrCreateInjectMetadata(type);

                InjectFields(instance, type, meta);
                InjectProperties(instance, type, meta);
                InjectMethods(instance, type, meta);
            }

            private void InjectFields(object instance, Type type, InjectableMetadata meta)
            {
                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    var f = meta.Fields[i];

                    if (f.Type.IsGenericType && f.Type.GetGenericTypeDefinition() == typeof(LazyInjection<>))
                    {
                        var lazyInstance = Activator.CreateInstance(f.Type, _di);
                        f.Field.SetValue(instance, lazyInstance);
                        continue;
                    }

                    var resolvedValue = _di.TryResolve(f.Type);
                    if (resolvedValue != null)
                    {
                        f.Field.SetValue(instance, resolvedValue);
                    }
                    else if (f.IsOptional) { }
                    else if (_di.StrictInjection)
                    {
                        throw new InvalidOperationException(
                            $"Strict injection failed: [Inject] field '{type.FullName}.{f.Field.Name}' of type '{f.Type.FullName}' is not registered. Mark with [OptionalInject] if this dependency is optional.");
                    }
                    else
                    {
                        _di.RecordPendingField(instance, f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{f.Type.FullName}' for field '{type.FullName}.{f.Field.Name}' is not registered; the field was left null.");
#endif
                    }
                }
            }

            private void InjectProperties(object instance, Type type, InjectableMetadata meta)
            {
                for (int i = 0; i < meta.Properties.Length; i++)
                {
                    var p = meta.Properties[i];
                    var resolvedValue = _di.TryResolve(p.Type);
                    if (resolvedValue != null)
                    {
                        p.Property.SetValue(instance, resolvedValue);
                    }
                    else if (p.IsOptional) { }
                    else if (_di.StrictInjection)
                    {
                        throw new InvalidOperationException(
                            $"Strict injection failed: [Inject] property '{type.FullName}.{p.Property.Name}' of type '{p.Type.FullName}' is not registered. Mark with [OptionalInject] if this dependency is optional.");
                    }
                    else
                    {
                        _di.RecordPendingProperty(instance, p);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{p.Type.FullName}' for property '{type.FullName}.{p.Property.Name}' is not registered; the property was left null.");
#endif
                    }
                }
            }

            private void InjectMethods(object instance, Type type, InjectableMetadata meta)
            {
                for (int i = 0; i < meta.Methods.Length; i++)
                {
                    var m = meta.Methods[i];
                    var args = new object[m.ParameterTypes.Length];
                    for (int j = 0; j < m.ParameterTypes.Length; j++)
                    {
                        args[j] = _di.TryResolve(m.ParameterTypes[j]);
                        if (args[j] == null)
                        {
                            if (m.OptionalParameterMask[j]) { }
                            else if (_di.StrictInjection)
                            {
                                throw new InvalidOperationException(
                                    $"Strict injection failed: [Inject] method '{type.FullName}.{m.Method.Name}' parameter {j} of type '{m.ParameterTypes[j].FullName}' is not registered. Mark with [OptionalInject] if this dependency is optional.");
                            }
                            else
                            {
                                _di.RecordPendingMethodParam(instance, m, j);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{m.ParameterTypes[j].FullName}' for method '{type.FullName}.{m.Method.Name}' is not registered; null was passed.");
#endif
                            }
                        }
                    }
                    m.Method.Invoke(instance, args);
                }
            }
        }

        // ─── Clearer (static injected-reference clearing) ───
        private static class Clearer
        {
            public static void ClearInjectedReferences(object instance)
            {
                if (instance == null) return;
                if (instance is IResettable resettable)
                    resettable.Reset();

                var type = instance.GetType();
                if (s_customClearers.TryGetValue(type, out var clearer))
                {
                    clearer(instance);
                    return;
                }

                var meta = MetadataCache.GetOrCreateClearMetadata(type);
                for (int i = 0; i < meta.Fields.Length; i++)
                    meta.Fields[i].SetValue(instance, null);
                for (int i = 0; i < meta.Properties.Length; i++)
                    meta.Properties[i].SetValue(instance, null);
            }
        }

        // ─── Constructor ───
        public NexusDI(NexusDI parent = null)
        {
            _parent = parent;
            _injector = new Injector(this);
        }

        // ─── Public API: Bind ───
        public void Bind<TInterface, TImplementation>(bool isSingleton = true) where TImplementation : class, TInterface
        {
            _bindings[typeof(TInterface)] = new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton };
        }

        public void Bind<T>(bool isSingleton = true) where T : class
        {
            _bindings[typeof(T)] = new Binding { ConcreteType = typeof(T), IsSingleton = isSingleton };
        }

        public void Bind(Type type, bool isSingleton = true)
        {
            _bindings[type] = new Binding { ConcreteType = type, IsSingleton = isSingleton };
        }

        public void BindInstance<T>(T instance) where T : class
        {
            BindInstance(instance, disposeWithContainer: true);
        }

        public void BindInstance<T>(T instance, bool disposeWithContainer) where T : class
        {
            _bindings[typeof(T)] = new Binding { ConcreteType = typeof(T), Instance = instance, IsSingleton = true };
            if (disposeWithContainer)
            {
                lock (_singletonLock)
                    _resolvedSingletons.Add(instance);
            }
        }

        public void BindFactory<T>(Func<T> factory) where T : class
        {
            _bindings[typeof(T)] = new Binding { ConcreteType = typeof(T), Factory = factory, IsSingleton = false };
        }

        // ─── Public API: Resolve ───
        public T Resolve<T>() where T : class => (T)Resolve(typeof(T));
        public T TryResolve<T>() where T : class => IsRegistered(typeof(T)) ? Resolve<T>() : null;
        public object TryResolve(Type type) => (type != null && IsRegistered(type)) ? Resolve(type) : null;

        public object Resolve(Type type)
        {
            if (type == typeof(NexusDI)) return this;
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type))
                return ExternalAdapter.Resolve(type);

            if (_bindings.TryGetValue(type, out var binding))
            {
                if (binding.Instance != null) return binding.Instance;
                if (binding.Factory != null) return binding.Factory();

                s_resolutionStack ??= new HashSet<Type>();
                if (!s_resolutionStack.Add(type))
                    throw new InvalidOperationException($"Circular dependency detected while resolving {type.FullName}. Resolution chain forms a cycle.");

                bool addedToConstructing = false;
                try
                {
                    if (binding.IsSingleton)
                    {
                        object singletonInstance;
                        lock (_singletonLock)
                        {
                            if (_disposed)
                                throw new ObjectDisposedException(nameof(NexusDI), $"Cannot resolve singleton '{type.FullName}': the container has been disposed.");

                            if (binding.Instance != null) return binding.Instance;
                            if (!_constructingSingletons.Add(type))
                                throw new InvalidOperationException($"Circular dependency detected while resolving singleton {type.FullName}.");
                            addedToConstructing = true;

                            try
                            {
                                singletonInstance = _injector.CreateInstance(binding.ConcreteType);
                                _injector.Inject(singletonInstance);
                                binding.Instance = singletonInstance;
                                _resolvedSingletons.Add(singletonInstance);
                            }
                            finally
                            {
                                _constructingSingletons.Remove(type);
                                addedToConstructing = false;
                            }
                        }
                        return singletonInstance;
                    }

                    var transientInstance = _injector.CreateInstance(binding.ConcreteType);
                    _injector.Inject(transientInstance);
                    return transientInstance;
                }
                finally
                {
                    s_resolutionStack.Remove(type);
                    if (addedToConstructing) _constructingSingletons.Remove(type);
                }
            }

            if (_parent != null) return _parent.Resolve(type);
            throw new InvalidOperationException($"Dependency of type {type.FullName} is not registered.");
        }

        // ─── Public API: Inject (delegates to Injector) ───
        public void Inject(object instance)
        {
            _injector.Inject(instance);
        }

        // ─── Public API: ReInject (pending tracking) ───
        public bool ReInject(object instance)
        {
            if (instance == null || !_pendingInjections.TryGetValue(instance, out var pending))
                return true;

            bool allSucceeded = true;
            var type = instance.GetType();

            for (int i = pending.Fields.Count - 1; i >= 0; i--)
            {
                var f = pending.Fields[i];
                var resolvedValue = TryResolve(f.Type);
                if (resolvedValue != null) { f.Field.SetValue(instance, resolvedValue); pending.Fields.RemoveAt(i); }
                else { allSucceeded = false; }
            }

            for (int i = pending.Properties.Count - 1; i >= 0; i--)
            {
                var p = pending.Properties[i];
                var resolvedValue = TryResolve(p.Type);
                if (resolvedValue != null) { p.Property.SetValue(instance, resolvedValue); pending.Properties.RemoveAt(i); }
                else { allSucceeded = false; }
            }

            for (int i = pending.Methods.Count - 1; i >= 0; i--)
            {
                var (method, paramIndices) = pending.Methods[i];
                var args = new object[method.ParameterTypes.Length];
                bool methodSucceeded = true;
                for (int j = 0; j < method.ParameterTypes.Length; j++)
                {
                    args[j] = TryResolve(method.ParameterTypes[j]);
                    if (args[j] == null && Array.IndexOf(paramIndices, j) >= 0)
                        methodSucceeded = false;
                }
                if (methodSucceeded) { method.Method.Invoke(instance, args); pending.Methods.RemoveAt(i); }
                else { allSucceeded = false; }
            }

            if (allSucceeded) _pendingInjections.Remove(instance);
            return allSucceeded;
        }

        public int ReInjectAll()
        {
            var snapshot = new List<KeyValuePair<object, PendingInjection>>();
            foreach (var kvp in _pendingInjections) snapshot.Add(kvp);

            int resolved = 0;
            foreach (var kvp in snapshot)
            {
                if (ReInject(kvp.Key)) resolved++;
            }
            return resolved;
        }

        public void ClearPendingInjection(object instance)
        {
            _pendingInjections.Remove(instance);
        }

        private void RecordPendingField(object instance, InjectableField field)
        {
            var pending = _pendingInjections.GetOrCreateValue(instance);
            pending.Fields.Add(field);
        }

        private void RecordPendingProperty(object instance, InjectableProperty property)
        {
            var pending = _pendingInjections.GetOrCreateValue(instance);
            pending.Properties.Add(property);
        }

        private void RecordPendingMethodParam(object instance, InjectableMethod method, int paramIndex)
        {
            var pending = _pendingInjections.GetOrCreateValue(instance);
            for (int i = 0; i < pending.Methods.Count; i++)
            {
                if (pending.Methods[i].Method == method)
                {
                    var existing = pending.Methods[i];
                    var indices = existing.ParamIndices;
                    if (Array.IndexOf(indices, paramIndex) < 0)
                    {
                        var newIndices = new int[indices.Length + 1];
                        Array.Copy(indices, newIndices, indices.Length);
                        newIndices[newIndices.Length - 1] = paramIndex;
                        pending.Methods[i] = (method, newIndices);
                    }
                    return;
                }
            }
            pending.Methods.Add((method, new[] { paramIndex }));
        }

        // ─── Public API: Query ───
        public bool IsRegistered(Type type)
        {
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type)) return true;
            if (_bindings.ContainsKey(type)) return true;
            return _parent != null && _parent.IsRegistered(type);
        }

        internal HashSet<Type> GetAllRegisteredTypes()
        {
            var types = new HashSet<Type>(_bindings.Keys);
            types.Add(typeof(NexusDI));
            types.Add(typeof(IContext));
            types.Add(typeof(ISignalBus));
            if (_parent != null) types.UnionWith(_parent.GetAllRegisteredTypes());
            return types;
        }

        /// <summary>Safe editor snapshot of resolved singleton instances.</summary>
        internal List<(Type InterfaceType, object Instance)> GetEditorSingletonSnapshot()
        {
            var result = new List<(Type, object)>();
            foreach (var kvp in _bindings)
            {
                if (kvp.Value.IsSingleton && kvp.Value.Instance != null)
                    result.Add((kvp.Key, kvp.Value.Instance));
            }
            return result;
        }

        /// <summary>Safe editor snapshot of interface→concrete type mappings (no private-type leak).</summary>
        internal List<(Type InterfaceType, Type ConcreteType)> GetEditorTypeMappings()
        {
            var result = new List<(Type, Type)>();
            foreach (var kvp in _bindings)
                result.Add((kvp.Key, kvp.Value.ConcreteType ?? kvp.Key));
            return result;
        }

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

        /// <summary>
        /// Forwarding method for external callers (ContextBuilder, BuildValidation).
        /// Delegates to the internal MetadataCache.
        /// </summary>
        internal static InjectableMetadata GetOrCreateInjectMetadata(Type type) => MetadataCache.GetOrCreateInjectMetadata(type);

        internal void NotifyLazyServiceResolved(Type type, object instance)
        {
            if (instance is INexusService service)
                _lazyServicesPendingInit.Enqueue(service);
        }

        public IEnumerable<object> GetActiveSingletons()
        {
            lock (_singletonLock)
                return new List<object>(_resolvedSingletons);
        }

        public Dictionary<Type, object> GetRegisteredSingletons()
        {
            var result = new Dictionary<Type, object>();
            foreach (var kvp in _bindings)
            {
                if (kvp.Value.IsSingleton && kvp.Value.Instance != null)
                    result[kvp.Key] = kvp.Value.Instance;
            }
            if (_parent != null)
            {
                var parentSingletons = _parent.GetRegisteredSingletons();
                foreach (var kvp in parentSingletons)
                {
                    if (!result.ContainsKey(kvp.Key)) result[kvp.Key] = kvp.Value;
                }
            }
            return result;
        }

        // ─── Public API: Clearing ───
        public static void ClearInjectedReferences(object instance)
        {
            Clearer.ClearInjectedReferences(instance);
        }

        public static void RegisterInjector<T>(Action<T, NexusDI> injector) where T : class
        {
            s_customInjectors[typeof(T)] = (instance, di) => injector((T)instance, di);
        }

        public static void RegisterClearer<T>(Action<T> clearer) where T : class
        {
            s_customClearers[typeof(T)] = instance => clearer((T)instance);
        }

        // ─── Disposal ───
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
                    // INexusService lifecycle (InitializeAsync/OnDispose) is owned by the owning
                    // Context, which disposes services in reverse registration order. Skipping
                    // them here prevents double-dispose (NexusService<T>.OnDispose → Dispose()).
                    if (instance is INexusService) continue;
                    if (instance is IDisposable disposable) disposable.Dispose();
                    else if (instance is IAsyncDisposable asyncDisposable)
                        SafeAsyncRunner.Run(() => asyncDisposable.DisposeAsync(),
                            $"Async disposal of singleton '{instance.GetType().FullName}' failed");
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
                        // Same contract as Dispose(): INexusService lifecycle is owned by the
                        // owning Context, so skip here to avoid double-dispose.
                        if (instance is INexusService) continue;
                        if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                        else if (instance is IDisposable disposable) disposable.Dispose();
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
            s_customClearers.Clear();
            MetadataCache.ClearAll();
        }
    }
}
