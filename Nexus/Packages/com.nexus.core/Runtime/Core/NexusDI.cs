using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
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
        public int ActiveSingletonsCount
        {
            get
            {
                lock (_singletonLock)
                    return _resolvedSingletons.Count;
            }
        }
        public bool StrictInjection { get; set; }
        internal readonly ConcurrentQueue<INexusService> _lazyServicesPendingInit = new();
        private readonly NexusDI _parent;
        private readonly ConcurrentDictionary<Type, bool> _crossBoundaryTypes = new();
        private readonly ConcurrentDictionary<Type, Binding> _bindings = new();
        // Named bindings (Strange-style named injection): key = (type, name).
        // Resolution falls back to the default binding when a name is not registered.
        private readonly ConcurrentDictionary<(Type Type, string Name), Binding> _namedBindings = new();
        private readonly HashSet<object> _resolvedSingletons = new();
        private volatile bool _disposed;

        /// <summary>Safe editor snapshot of resolved singleton instances (thread-safe copy, no raw reference leak).</summary>
        internal IReadOnlyList<object> EditorResolvedSingletons
        {
            get
            {
                lock (_singletonLock)
                {
                    if (_resolvedSingletons.Count == 0)
                        return Array.Empty<object>();
                    var array = new object[_resolvedSingletons.Count];
                    _resolvedSingletons.CopyTo(array);
                    return array;
                }
            }
        }

        private static readonly ConcurrentDictionary<Type, Action<object, NexusDI>> s_customInjectors = new();
        private static readonly ConcurrentDictionary<Type, Action<object>> s_customClearers = new();

        private readonly ConditionalWeakTable<object, PendingInjection> _pendingInjections = new();
        private readonly object _pendingInjectionsLock = new();
        private readonly HashSet<Type> _constructingSingletons = new();
        private readonly object _singletonLock = new();
        // Per-type wait handles for singleton construction synchronization
        private readonly Dictionary<Type, ManualResetEventSlim> _constructionWaitHandles = new();
        private readonly object _constructionWaitLock = new();
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
            public bool IsLazy { get; set; }
            /// <summary>Optional named-binding discriminator ([Inject(Name = ...)]). Null = default binding.</summary>
            public string Name { get; set; }
            /// <summary>Compiled setter delegate (fallback to reflection if null).</summary>
            public Action<object, object> Setter { get; set; }
            /// <summary>Compiled getter delegate (fallback to reflection if null).</summary>
            public Func<object, object> Getter { get; set; }
        }
        internal class InjectableProperty
        {
            public PropertyInfo Property { get; set; }
            public Type Type { get; set; }
            public bool IsOptional { get; set; }
            public bool IsLazy { get; set; }
            /// <summary>Optional named-binding discriminator ([Inject(Name = ...)]). Null = default binding.</summary>
            public string Name { get; set; }
            /// <summary>Compiled setter delegate (fallback to reflection if null).</summary>
            public Action<object, object> Setter { get; set; }
        }
        internal class InjectableMethod
        {
            public MethodInfo Method { get; set; }
            public Type[] ParameterTypes { get; set; }
            public bool[] OptionalParameterMask { get; set; }
            /// <summary>Post-construct ordering when the method is [PostConstruct]-tagged.</summary>
            public int PostConstructOrder { get; set; }
            public bool IsPostConstruct { get; set; }
        }
        internal class InjectableMetadata
        {
            public InjectableField[] Fields { get; set; }
            public InjectableProperty[] Properties { get; set; }
            public InjectableMethod[] Methods { get; set; }
            public InjectableMethod[] PostConstructMethods { get; set; }
            /// <summary>Parameterless [Deconstruct]-tagged cleanup methods, ascending Order.</summary>
            public InjectableMethod[] DeconstructMethods { get; set; }
            public ConstructorInfo Constructor { get; set; }
            public Type[] ConstructorParameterTypes { get; set; }
            /// <summary>Per-parameter binding names for the injected constructor (null = default).</summary>
            public string[] ConstructorParameterNames { get; set; }
        }
        private class ClearableMetadata
        {
            public FieldInfo[] Fields { get; set; }
            public PropertyInfo[] Properties { get; set; }
            /// <summary>Compiled null-setters (fallback to reflection if null).</summary>
            public Action<object, object>[] FieldSetters { get; set; }
            public Action<object, object>[] PropertySetters { get; set; }
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
                        var injectAttr = field.GetCustomAttribute<InjectAttribute>();
                        var optionalAttr = field.GetCustomAttribute<OptionalInjectAttribute>();
                        // P-fix: OptionalInjectAttribute does NOT derive from InjectAttribute, so
                        // [OptionalInject]-only members were never added here — they were silently
                        // NEVER INJECTED even when a binding existed (e.g. EconomyService's
                        // throttler stayed null → write-coalescing silently dead). Include both.
                        if (injectAttr != null || optionalAttr != null)
                        {
                            if (field.FieldType.IsValueType)
                                throw new InvalidOperationException($"Cannot inject value type field {t.FullName}.{field.Name}. Nexus DI only supports reference-type dependencies.");
                            fieldList.Add(new InjectableField
                            {
                                Field = field,
                                Type = field.FieldType,
                                IsOptional = optionalAttr != null,
                                Name = injectAttr?.Name,
                                IsLazy = field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(LazyInjection<>),
                                Setter = CompiledAccessorEmitter.CompileFieldSetter(t, field),
                                Getter = CompiledAccessorEmitter.CompileFieldGetter(t, field)
                            });
                        }
                    }

                    var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var propList = new List<InjectableProperty>();
                    foreach (var prop in properties)
                    {
                        var injectAttr = prop.GetCustomAttribute<InjectAttribute>();
                        var optionalAttr = prop.GetCustomAttribute<OptionalInjectAttribute>();
                        // P-fix: accept [OptionalInject]-only properties (see field comment).
                        if ((injectAttr != null || optionalAttr != null) && prop.CanWrite)
                        {
                            if (prop.PropertyType.IsValueType)
                                throw new InvalidOperationException($"Cannot inject value type property {t.FullName}.{prop.Name}. Nexus DI only supports reference-type dependencies.");
                            propList.Add(new InjectableProperty
                            {
                                Property = prop,
                                Type = prop.PropertyType,
                                IsOptional = optionalAttr != null,
                                IsLazy = prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(LazyInjection<>),
                                Name = injectAttr?.Name,
                                Setter = CompiledAccessorEmitter.CompilePropertySetter(t, prop)
                            });
                        }
                    }

                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var methodList = new List<InjectableMethod>();
                    var postConstructList = new List<InjectableMethod>();
                    var deconstructList = new List<InjectableMethod>();
                    foreach (var method in methods)
                    {
                        var injectAttr = method.GetCustomAttribute<InjectAttribute>();
                        var postAttr = method.GetCustomAttribute<PostConstructAttribute>();
                        var deconstructAttr = method.GetCustomAttribute<DeconstructAttribute>();
                        if (injectAttr != null)
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
                        if (postAttr != null)
                        {
                            var parameters = method.GetParameters();
                            if (parameters.Length != 0)
                                throw new InvalidOperationException($"[PostConstruct] method {t.FullName}.{method.Name} must be parameterless.");
                            postConstructList.Add(new InjectableMethod
                            {
                                Method = method,
                                ParameterTypes = Array.Empty<Type>(),
                                OptionalParameterMask = Array.Empty<bool>(),
                                PostConstructOrder = postAttr.Order,
                                IsPostConstruct = true
                            });
                        }
                        if (deconstructAttr != null)
                        {
                            var parameters = method.GetParameters();
                            if (parameters.Length != 0)
                                throw new InvalidOperationException($"[Deconstruct] method {t.FullName}.{method.Name} must be parameterless.");
                            deconstructList.Add(new InjectableMethod
                            {
                                Method = method,
                                ParameterTypes = Array.Empty<Type>(),
                                OptionalParameterMask = Array.Empty<bool>(),
                                PostConstructOrder = deconstructAttr.Order,
                                IsPostConstruct = false
                            });
                        }
                    }
                    postConstructList.Sort((a, b) => a.PostConstructOrder.CompareTo(b.PostConstructOrder));
                    deconstructList.Sort((a, b) => a.PostConstructOrder.CompareTo(b.PostConstructOrder));

                    ConstructorInfo targetCtor = null;
                    var constructors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                    if (constructors.Length > 0)
                    {
                        foreach (var ctor in constructors)
                        {
                            // Accept both Strange-style [Construct] and Nexus-style [Inject]
                            // spellings for the preferred constructor.
                            bool marked = ctor.GetCustomAttribute<InjectAttribute>() != null
                                || ctor.GetCustomAttribute<ConstructAttribute>() != null;
                            if (marked)
                            {
                                if (targetCtor != null)
                                    throw new InvalidOperationException($"Multiple constructors marked with [Inject]/[Construct] in {t.FullName}. Only one injected constructor is allowed.");
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
                                    throw new InvalidOperationException($"No suitable constructor found for type {t.FullName}. A type must either have a parameterless constructor or a constructor decorated with [Inject]/[Construct].");
                                }
                            }
                        }
                    }

                    Type[] ctorParamTypes = null;
                    string[] ctorParamNames = null;
                    if (targetCtor != null)
                    {
                        var parameters = targetCtor.GetParameters();
                        ctorParamTypes = new Type[parameters.Length];
                        ctorParamNames = new string[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            if (parameters[i].ParameterType.IsValueType)
                                throw new InvalidOperationException($"Cannot inject value type constructor parameter {t.FullName}({parameters[i].Name}). Nexus DI only supports reference-type dependencies.");
                            ctorParamTypes[i] = parameters[i].ParameterType;
                            var paramInject = parameters[i].GetCustomAttribute<InjectAttribute>();
                            if (paramInject != null) ctorParamNames[i] = paramInject.Name;
                        }
                    }

                    return new InjectableMetadata
                    {
                        Fields = fieldList.ToArray(),
                        Properties = propList.ToArray(),
                        Methods = methodList.ToArray(),
                        PostConstructMethods = postConstructList.Count > 0 ? postConstructList.ToArray() : null,
                        DeconstructMethods = deconstructList.Count > 0 ? deconstructList.ToArray() : null,
                        Constructor = targetCtor,
                        ConstructorParameterTypes = ctorParamTypes,
                        ConstructorParameterNames = ctorParamNames
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
                        // P-fix parity: [OptionalInject]-only members must also be clearable.
                        if ((field.GetCustomAttribute<InjectAttribute>() != null
                                || field.GetCustomAttribute<OptionalInjectAttribute>() != null)
                            && !field.FieldType.IsValueType)
                            fieldList.Add(field);
                    }

                    var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var propList = new List<PropertyInfo>();
                    foreach (var prop in properties)
                    {
                        if ((prop.GetCustomAttribute<InjectAttribute>() != null
                                || prop.GetCustomAttribute<OptionalInjectAttribute>() != null)
                            && prop.CanWrite && !prop.PropertyType.IsValueType)
                            propList.Add(prop);
                    }

                    var clearFields = fieldList.ToArray();
                    var clearProps = propList.ToArray();
                    var fieldSetters = new Action<object, object>[clearFields.Length];
                    var propSetters = new Action<object, object>[clearProps.Length];
                    for (int i = 0; i < clearFields.Length; i++) fieldSetters[i] = CompiledAccessorEmitter.CompileFieldSetter(t, clearFields[i]);
                    for (int i = 0; i < clearProps.Length; i++) propSetters[i] = CompiledAccessorEmitter.CompilePropertySetter(t, clearProps[i]);

                    return new ClearableMetadata { Fields = clearFields, Properties = clearProps, FieldSetters = fieldSetters, PropertySetters = propSetters };
                });
            }

            internal static void ClearAll()
            {
                InjectMeta.Clear();
                ClearMeta.Clear();
                CompiledAccessorEmitter.ClearWarnings();
            }
            // ─── Shared setter dispatch (compiled setter with reflection fallback) ───
            internal static void ApplyFieldSetter(InjectableField field, object instance, object value)
            {
                if (field.Setter != null) field.Setter(instance, value);
                else field.Field.SetValue(instance, value);
            }

            internal static void ApplyPropertySetter(InjectableProperty property, object instance, object value)
            {
                if (property.Setter != null) property.Setter(instance, value);
                else property.Property.SetValue(instance, value);
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
                var paramNames = meta.ConstructorParameterNames;
                var args = new object[paramTypes.Length];
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    args[i] = string.IsNullOrEmpty(paramNames?[i])
                        ? _di.TryResolve(paramTypes[i])
                        : _di.TryResolve(paramTypes[i], paramNames[i]);
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
                RunPostConstructs(instance, meta);
            }

            /// <summary>
            /// Invokes every [PostConstruct]-tagged method in ascending Order after all
            /// injections are applied. Dependencies are guaranteed non-null here.
            /// </summary>
            private void RunPostConstructs(object instance, InjectableMetadata meta)
            {
                if (meta.PostConstructMethods == null) return;
                for (int i = 0; i < meta.PostConstructMethods.Length; i++)
                {
                    try { meta.PostConstructMethods[i].Method.Invoke(instance, null); }
                    catch (TargetInvocationException ex)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw;
                    }
                }
            }

            /// <summary>
            /// Invokes every [Deconstruct]-tagged method in ascending Order BEFORE the instance
            /// is disposed by the container. Dependencies are still non-null here.
            /// </summary>
            internal void RunDeconstructs(object instance, InjectableMetadata meta)
            {
                if (meta.DeconstructMethods == null) return;
                for (int i = 0; i < meta.DeconstructMethods.Length; i++)
                {
                    try { meta.DeconstructMethods[i].Method.Invoke(instance, null); }
                    catch (TargetInvocationException ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] [Deconstruct] method {instance.GetType().FullName}.{meta.DeconstructMethods[i].Method.Name} threw: {ex.InnerException?.Message}");
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] [Deconstruct] method {instance.GetType().FullName}.{meta.DeconstructMethods[i].Method.Name} threw: {ex.Message}");
                    }
                }
            }

            private void InjectFields(object instance, Type type, InjectableMetadata meta)
            {
                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    var f = meta.Fields[i];

                    if (f.IsLazy)
                    {
                        var existingLazy = f.Getter != null ? f.Getter(instance) : f.Field.GetValue(instance);
                        if (existingLazy == null)
                        {
                            // P1 fix: forward the [Inject(Name=...)] discriminator so named
                            // bindings are honored on first access instead of being dropped.
                            // Thread-safe: create lazy instance, then atomically set if still null.
                            var lazyInstance = Activator.CreateInstance(f.Type,
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.CreateInstance,
                                null, new object[] { _di, f.Name }, null);
                            // Double-check: another thread might have set it while we were creating
                            var currentValue = f.Getter != null ? f.Getter(instance) : f.Field.GetValue(instance);
                            if (currentValue == null)
                            {
                                MetadataCache.ApplyFieldSetter(f, instance, lazyInstance);
                            }
                            else
                            {
                                lazyInstance = currentValue; // Use the one created by the other thread
                            }
                        }
                        continue;
                    }

                    var resolvedValue = string.IsNullOrEmpty(f.Name)
                        ? _di.TryResolve(f.Type)
                        : _di.TryResolve(f.Type, f.Name);
                    if (resolvedValue != null)
                    {
                        MetadataCache.ApplyFieldSetter(f, instance, resolvedValue);
                    }
                    else if (f.IsOptional) { }
                    else if (_di.StrictInjection)
                    {
                        throw new InvalidOperationException(
                            $"Strict injection failed: [Inject] field '{type.FullName}.{f.Field.Name}' of type '{f.Type.FullName}'{(string.IsNullOrEmpty(f.Name) ? "" : $" (name '{f.Name}')")} is not registered. Mark with [OptionalInject] if this dependency is optional.");
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

                    if (p.IsLazy)
                    {
                        var existingLazy = p.Property.GetValue(instance);
                        if (existingLazy == null)
                        {
                            var lazyInstance = Activator.CreateInstance(p.Type,
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.CreateInstance,
                                null, new object[] { _di, p.Name }, null);
                            // Double-check: another thread might have set it while we were creating
                            var currentValue = p.Property.GetValue(instance);
                            if (currentValue == null)
                            {
                                MetadataCache.ApplyPropertySetter(p, instance, lazyInstance);
                            }
                            else
                            {
                                lazyInstance = currentValue;
                            }
                        }
                        continue;
                    }

                    var resolvedValue = string.IsNullOrEmpty(p.Name)
                        ? _di.TryResolve(p.Type)
                        : _di.TryResolve(p.Type, p.Name);
                    if (resolvedValue != null)
                    {
                        MetadataCache.ApplyPropertySetter(p, instance, resolvedValue);
                    }
                    else if (p.IsOptional) { }
                    else if (_di.StrictInjection)
                    {
                        throw new InvalidOperationException(
                            $"Strict injection failed: [Inject] property '{type.FullName}.{p.Property.Name}' of type '{p.Type.FullName}'{(string.IsNullOrEmpty(p.Name) ? "" : $" (name '{p.Name}')")} is not registered. Mark with [OptionalInject] if this dependency is optional.");
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
                    // BUG-2 fix: track whether all required parameters were resolved.
                    // If any required parameter is missing, skip the invocation entirely and
                    // record it as pending — invoking with null could cause a
                    // NullReferenceException inside user code with no clear error origin.
                    bool allRequiredResolved = true;

                    for (int j = 0; j < m.ParameterTypes.Length; j++)
                    {
                        args[j] = _di.TryResolve(m.ParameterTypes[j]);
                        if (args[j] == null)
                        {
                            if (m.OptionalParameterMask[j])
                            {
                                // Optional: null is acceptable — leave it null.
                            }
                            else if (_di.StrictInjection)
                            {
                                throw new InvalidOperationException(
                                    $"Strict injection failed: [Inject] method '{type.FullName}.{m.Method.Name}' parameter {j} of type '{m.ParameterTypes[j].FullName}' is not registered. Mark with [OptionalInject] if this dependency is optional.");
                            }
                            else
                            {
                                _di.RecordPendingMethodParam(instance, m, j);
                                allRequiredResolved = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                                NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{m.ParameterTypes[j].FullName}' for method '{type.FullName}.{m.Method.Name}' is not registered; method invocation deferred.");
#endif
                            }
                        }
                    }

                    // Only invoke the method when every required parameter is available.
                    // Methods with unresolved required parameters are re-attempted during
                    // the next ReInjectAll() pass (triggered after additional bindings are registered).
                    if (allRequiredResolved)
                    {
                        m.Method.Invoke(instance, args);
                    }
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
                {
                    var setter = meta.FieldSetters != null ? meta.FieldSetters[i] : null;
                    if (setter != null) setter(instance, null);
                    else meta.Fields[i].SetValue(instance, null);
                }
                for (int i = 0; i < meta.Properties.Length; i++)
                {
                    var setter = meta.PropertySetters != null ? meta.PropertySetters[i] : null;
                    if (setter != null) setter(instance, null);
                    else meta.Properties[i].SetValue(instance, null);
                }
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

        public void Bind(Type interfaceType, Type implementationType, bool isSingleton = true)
        {
            _bindings[interfaceType] = new Binding { ConcreteType = implementationType, IsSingleton = isSingleton };
        }

        /// <summary>Binds a named implementation (Strange-style). Resolves only against [Inject(Name=...)].</summary>
        public void Bind<TInterface, TImplementation>(string name, bool isSingleton = true) where TImplementation : class, TInterface
        {
            if (string.IsNullOrEmpty(name)) { Bind<TInterface, TImplementation>(isSingleton); return; }
            _namedBindings[(typeof(TInterface), name)] = new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton };
        }

        /// <summary>Binds a named self-referencing type.</summary>
        public void Bind<T>(string name, bool isSingleton = true) where T : class
        {
            if (string.IsNullOrEmpty(name)) { Bind<T>(isSingleton); return; }
            _namedBindings[(typeof(T), name)] = new Binding { ConcreteType = typeof(T), IsSingleton = isSingleton };
        }

        /// <summary>Binds a named type (reflection-form).</summary>
        public void Bind(Type type, string name, bool isSingleton = true)
        {
            if (string.IsNullOrEmpty(name)) { Bind(type, isSingleton); return; }
            _namedBindings[(type, name)] = new Binding { ConcreteType = type, IsSingleton = isSingleton };
        }

        /// <summary>
        /// Binds a single concrete implementation under MULTIPLE interfaces (Strange-style
        /// polymorphic binding: <c>Bind&lt;IHittable&gt;().Bind&lt;IUpdateable&gt;().To&lt;Romulan&gt;()</c>).
        /// All keys share ONE <see cref="Binding"/> object, so a singleton resolves to the same
        /// instance through every interface; a transient binding produces a fresh instance per
        /// resolve, also shared across all interfaces for that single resolve.
        /// </summary>
        public void BindMultiple<TInterface1, TInterface2, TImplementation>(bool isSingleton = true)
            where TImplementation : class, TInterface1, TInterface2
        {
            var shared = new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton };
            _bindings[typeof(TInterface1)] = shared;
            _bindings[typeof(TInterface2)] = shared;
        }

        /// <summary>Three-interface polymorphic binding (see the two-interface overload).</summary>
        public void BindMultiple<TInterface1, TInterface2, TInterface3, TImplementation>(bool isSingleton = true)
            where TImplementation : class, TInterface1, TInterface2, TInterface3
        {
            var shared = new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton };
            _bindings[typeof(TInterface1)] = shared;
            _bindings[typeof(TInterface2)] = shared;
            _bindings[typeof(TInterface3)] = shared;
            _bindings[typeof(TImplementation)] = shared;
        }

        /// <summary>Reflection-form polymorphic binding sharing ONE Binding instance across all interfaces and concrete type.</summary>
        public void BindMultiple(Type[] interfaceTypes, Type concreteType, bool isSingleton = true)
        {
            var shared = new Binding { ConcreteType = concreteType, IsSingleton = isSingleton };
            if (interfaceTypes != null)
            {
                for (int i = 0; i < interfaceTypes.Length; i++)
                {
                    _bindings[interfaceTypes[i]] = shared;
                }
            }
            _bindings[concreteType] = shared;
        }

        // ─── Cross-Boundary Binding (StrangeIoC-style cross-context injection) ───

        /// <summary>
        /// Binds an implementation as cross-boundary — registered as a singleton in the current
        /// container AND marked for parent-chain resolution in descendant containers. Descendant
        /// contexts resolve marked types via <see cref="ResolveCrossBoundary"/>.
        /// </summary>
        public void BindCrossBoundary<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            _bindings[typeof(TInterface)] = new Binding { ConcreteType = typeof(TImplementation), IsSingleton = true };
            _crossBoundaryTypes[typeof(TInterface)] = true;
        }

        /// <summary>Binds a self-referencing type as cross-boundary.</summary>
        public void BindCrossBoundary<T>() where T : class
        {
            _bindings[typeof(T)] = new Binding { ConcreteType = typeof(T), IsSingleton = true };
            _crossBoundaryTypes[typeof(T)] = true;
        }

        /// <summary>
        /// Resolves a dependency by walking UP the parent-container chain, but only for types
        /// that were explicitly marked as cross-boundary via <see cref="BindCrossBoundary{TInterface,TImplementation}"/>.
        /// Searches the current container first, then parent, then grandparent, etc.
        /// Returns the resolved instance from the owning container, or throws if not found at any level.
        /// This is the explicit opt-in equivalent of StrangeIoC's crossContextInjectionBinder.
        /// </summary>
        public object ResolveCrossBoundary(Type type)
        {
            // Check current container first — if the type is registered here and marked cross-boundary, resolve it
            if (_crossBoundaryTypes.ContainsKey(type))
            {
                if (_bindings.TryGetValue(type, out var binding))
                    return ResolveBinding(type, binding);
            }

            // Walk the parent chain, looking for cross-boundary-marked types
            var current = _parent;
            while (current != null)
            {
                if (current._crossBoundaryTypes.ContainsKey(type))
                {
                    if (current._bindings.TryGetValue(type, out var binding))
                        return current.ResolveBinding(type, binding);
                }
                current = current._parent;
            }

            throw new InvalidOperationException($"Cross-boundary dependency of type {type.FullName} is not registered in any ancestor context. Use BindCrossBoundary<TInterface, TImplementation>() in the owning context to expose it.");
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

        /// <summary>Binds a named instance value.</summary>
        public void BindInstance<T>(string name, T instance) where T : class
        {
            if (string.IsNullOrEmpty(name)) { BindInstance(instance); return; }
            _namedBindings[(typeof(T), name)] = new Binding { ConcreteType = typeof(T), Instance = instance, IsSingleton = true };
            lock (_singletonLock)
                _resolvedSingletons.Add(instance);
        }

        public void BindFactory<T>(Func<T> factory) where T : class
        {
            _bindings[typeof(T)] = new Binding { ConcreteType = typeof(T), Factory = factory, IsSingleton = false };
        }

        /// <summary>Binds a named factory (a fresh instance per resolve).</summary>
        public void BindFactory<T>(string name, Func<T> factory) where T : class
        {
            if (string.IsNullOrEmpty(name)) { BindFactory(factory); return; }
            _namedBindings[(typeof(T), name)] = new Binding { ConcreteType = typeof(T), Factory = factory, IsSingleton = false };
        }

        // ─── Public API: Resolve ───
        public T Resolve<T>() where T : class => (T)Resolve(typeof(T));
        public T TryResolve<T>() where T : class => IsRegistered(typeof(T)) ? Resolve<T>() : null;
        /// <summary>Safely resolves a named binding; returns null when the name is not registered.</summary>
        public T TryResolve<T>(string name) where T : class => TryResolve(typeof(T), name) as T;
        public object TryResolve(Type type) => (type != null && IsRegistered(type)) ? Resolve(type) : null;

        /// <summary>
        /// Resolves a named binding. An explicitly requested but unregistered name throws —
        /// it never silently falls back to the default binding (that would mask typos).
        /// An empty name delegates to the default path.
        /// </summary>
        public T Resolve<T>(string name) where T : class
            => string.IsNullOrEmpty(name) ? Resolve<T>() : (T)Resolve(typeof(T), name);

        /// <summary>Resolves a named binding (reflection-form). Throws when the name is explicitly requested but unregistered.</summary>
        public object Resolve(Type type, string name)
        {
            if (string.IsNullOrEmpty(name)) return Resolve(type);
            if (_namedBindings.TryGetValue((type, name), out var named))
                return ResolveBinding(type, named);
            if (_parent != null && _parent.IsRegistered(type, name))
                return _parent.Resolve(type, name);
            throw new InvalidOperationException($"Dependency of type {type.FullName} named '{name}' is not registered.");
        }

        /// <summary>
        /// Attempts to resolve a named binding; returns null when the name is not registered
        /// (never falls back to the default binding). Empty name delegates to the default path.
        /// </summary>
        public object TryResolve(Type type, string name)
        {
            if (type == null) return null;
            if (string.IsNullOrEmpty(name)) return TryResolve(type);
            return IsRegistered(type, name) ? Resolve(type, name) : null;
        }

        public object Resolve(Type type)
        {
            if (type == typeof(NexusDI)) return this;
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type))
                return ExternalAdapter.Resolve(type);

            if (_bindings.TryGetValue(type, out var binding))
                return ResolveBinding(type, binding);

            if (_parent != null) return _parent.Resolve(type);
            throw new InvalidOperationException($"Dependency of type {type.FullName} is not registered.");
        }

        /// <summary>
        /// Shared resolve core for both default and named bindings: singleton construction
        /// with cross-thread waiting, factory mapping, and transient instantiation.
        /// </summary>
        private object ResolveBinding(Type type, Binding binding)
        {
            if (binding.Instance != null) return binding.Instance;

            // T1 fix: cycle detection must also guard factory-produced instances. Previously
            // the factory check ran BEFORE the resolution-stack push, so a factory that
            // resolved back to its own key (directly or transitively) recursed until
            // StackOverflowException — an uncatchable process crash. Moving the factory
            // call inside the guarded block turns that into a clear, catchable
            // InvalidOperationException naming the offending type.
            s_resolutionStack ??= new HashSet<Type>();
            if (!s_resolutionStack.Add(type))
                throw new InvalidOperationException($"Circular dependency detected while resolving {type.FullName}. Resolution chain forms a cycle.");

            bool addedToConstructing = false;
            ManualResetEventSlim waitHandle = null;
            try
            {
                if (binding.Factory != null) return binding.Factory();

                if (binding.IsSingleton)
                {
                    object singletonInstance = binding.Instance;
                    if (singletonInstance != null) return singletonInstance;

                    // B2: same-thread cycles are caught by the thread-local
                    // s_resolutionStack above, so a failed Add here can only mean
                    // ANOTHER thread is mid-construction of this singleton. Wait for
                    // the builder instead of throwing a spurious "circular dependency"
                    // on a perfectly valid concurrent first-resolve.
                    while (true)
                    {
                        lock (_singletonLock)
                        {
                            if (_disposed)
                                throw new ObjectDisposedException(nameof(NexusDI), $"Cannot resolve singleton '{type.FullName}': the container has been disposed.");

                            if (binding.Instance != null) return binding.Instance;
                            if (_constructingSingletons.Add(type))
                            {
                                addedToConstructing = true;
                                break;
                            }

                            // Another thread is constructing this singleton.
                            // Get or create a wait handle for this type.
                            if (!_constructionWaitHandles.TryGetValue(type, out waitHandle))
                            {
                                waitHandle = new ManualResetEventSlim(false);
                                _constructionWaitHandles[type] = waitHandle;
                            }
                        }

                        // Wait for the constructing thread to signal completion.
                        // Use a timeout to detect deadlocks.
                        if (!waitHandle.Wait(TimeSpan.FromSeconds(10)))
                            throw new InvalidOperationException($"Timed out waiting for concurrent construction of singleton {type.FullName}.");
                        // Loop again to check if instance is now published.
                    }

                    try
                    {
                        singletonInstance = _injector.CreateInstance(binding.ConcreteType);
                        _injector.Inject(singletonInstance);
                        lock (_singletonLock)
                        {
                            if (_disposed)
                            {
                                throw new ObjectDisposedException(nameof(NexusDI), $"Cannot publish singleton '{type.FullName}': the container has been disposed.");
                            }
                            binding.Instance = singletonInstance;
                            _resolvedSingletons.Add(singletonInstance);
                        }
                    }
                    finally
                    {
                        lock (_singletonLock)
                        {
                            _constructingSingletons.Remove(type);
                            // Signal all waiting threads that construction is complete.
                            if (_constructionWaitHandles.TryGetValue(type, out var wh))
                            {
                                wh.Set();
                                _constructionWaitHandles.Remove(type);
                            }
                        }
                        addedToConstructing = false;
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
                if (addedToConstructing) 
                {
                    lock (_singletonLock)
                    {
                        _constructingSingletons.Remove(type);
                        if (_constructionWaitHandles.TryGetValue(type, out var wh))
                        {
                            wh.Set();
                            _constructionWaitHandles.Remove(type);
                        }
                    }
                }
            }
        }

        // ─── Public API: Inject (delegates to Injector) ───
        public void Inject(object instance)
        {
            _injector.Inject(instance);
        }

        /// <summary>
        /// Runs every <c>[Deconstruct]</c>-tagged cleanup method on the instance (ascending
        /// Order). Invoked automatically for container-owned singletons during Dispose;
        /// exposed publicly so the Context can run cleanup for services it disposes.
        /// </summary>
        public void RunDeconstructs(object instance)
        {
            if (instance == null) return;
            var meta = MetadataCache.GetOrCreateInjectMetadata(instance.GetType());
            _injector.RunDeconstructs(instance, meta);
        }

        // ─── Public API: ReInject (pending tracking) ───
        public bool ReInject(object instance)
        {
            if (instance == null) return true;

            lock (_pendingInjectionsLock)
            {
                if (!_pendingInjections.TryGetValue(instance, out var pending))
                    return true;

                bool allSucceeded = true;
                var type = instance.GetType();

                for (int i = pending.Fields.Count - 1; i >= 0; i--)
                {
                    var f = pending.Fields[i];
                    var resolvedValue = string.IsNullOrEmpty(f.Name) ? TryResolve(f.Type) : TryResolve(f.Type, f.Name);
                    if (resolvedValue != null) { MetadataCache.ApplyFieldSetter(f, instance, resolvedValue); pending.Fields.RemoveAt(i); }
                    else { allSucceeded = false; }
                }

                for (int i = pending.Properties.Count - 1; i >= 0; i--)
                {
                    var p = pending.Properties[i];
                    var resolvedValue = string.IsNullOrEmpty(p.Name) ? TryResolve(p.Type) : TryResolve(p.Type, p.Name);
                    if (resolvedValue != null) { MetadataCache.ApplyPropertySetter(p, instance, resolvedValue); pending.Properties.RemoveAt(i); }
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
        }

        public int ReInjectAll()
        {
            var snapshot = new List<KeyValuePair<object, PendingInjection>>();
            lock (_pendingInjectionsLock)
            {
                foreach (var kvp in _pendingInjections) snapshot.Add(kvp);
            }

            int resolved = 0;
            foreach (var kvp in snapshot)
            {
                if (ReInject(kvp.Key)) resolved++;
            }
            return resolved;
        }

        public void ClearPendingInjection(object instance)
        {
            lock (_pendingInjectionsLock)
            {
                _pendingInjections.Remove(instance);
            }
        }

        private void RecordPendingField(object instance, InjectableField field)
        {
            lock (_pendingInjectionsLock)
            {
                var pending = _pendingInjections.GetOrCreateValue(instance);
                pending.Fields.Add(field);
            }
        }

        private void RecordPendingProperty(object instance, InjectableProperty property)
        {
            lock (_pendingInjectionsLock)
            {
                var pending = _pendingInjections.GetOrCreateValue(instance);
                pending.Properties.Add(property);
            }
        }

        private void RecordPendingMethodParam(object instance, InjectableMethod method, int paramIndex)
        {
            lock (_pendingInjectionsLock)
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
        }

        // ─── Public API: Query ───
        public bool IsRegistered(Type type)
        {
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type)) return true;
            if (_bindings.ContainsKey(type)) return true;
            return _parent != null && _parent.IsRegistered(type);
        }

        /// <summary>
        /// Returns true only when a NAMED binding exists for the type+name (strict — no
        /// fallback to the default registration). This lets strict injection catch a
        /// misspelled <c>[Inject(Name = "...")]</c> instead of silently injecting the default.
        /// </summary>
        public bool IsRegistered(Type type, string name)
        {
            if (type == null) return false;
            if (string.IsNullOrEmpty(name)) return IsRegistered(type);
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type)) return true;
            if (_namedBindings.ContainsKey((type, name))) return true;
            return _parent != null && _parent.IsRegistered(type, name);
        }

        internal HashSet<Type> GetAllRegisteredTypes()
        {
            var types = new HashSet<Type>(_bindings.Keys);
            foreach (var kvp in _namedBindings) types.Add(kvp.Key.Type);
            types.Add(typeof(NexusDI));
            types.Add(typeof(IContext));
            types.Add(typeof(ISignalBus));
            if (_parent != null) types.UnionWith(_parent.GetAllRegisteredTypes());
            return types;
        }

        /// <summary>
        /// A8: returns whether the type is bound as a singleton in this container or any
        /// ancestor. Used by DI validation to detect captive dependencies (a singleton
        /// service capturing a transient dependency).
        /// </summary>
        internal bool IsSingletonBinding(Type key)
        {
            if (key == null) return false;
            if (_bindings.TryGetValue(key, out var b)) return b.IsSingleton;
            // Named bindings redirect empty names to the default binding at registration,
            // so a (key, null) named entry never exists — only the default map is checked.
            return _parent != null && _parent.IsSingletonBinding(key);
        }

        /// <summary>
        /// A8: returns whether the type is bound via a factory (BindFactory) in this
        /// container or any ancestor. Factory-managed dependencies are explicitly exempt
        /// from captive-dependency validation.
        /// </summary>
        internal bool IsFactoryBinding(Type key)
        {
            if (key == null) return false;
            if (_bindings.TryGetValue(key, out var b)) return b.Factory != null;
            return _parent != null && _parent.IsFactoryBinding(key);
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

        private readonly ConcurrentDictionary<INexusService, bool> _lazyServicesEnqueued = new();

        internal void NotifyLazyServiceResolved(Type type, object instance)
        {
            if (instance is INexusService service && _lazyServicesEnqueued.TryAdd(service, true))
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

            var asyncDisposables = new List<IAsyncDisposable>();
            foreach (var instance in singletonsCopy)
            {
                if (!alreadyDisposed.Add(instance)) continue;
                try
                {
                    // INexusService lifecycle (InitializeAsync/OnDispose) is owned by the owning
                    // Context, which disposes services in reverse registration order. Skipping
                    // them here prevents double-dispose (NexusService<T>.OnDispose → Dispose()).
                    if (instance is INexusService) continue;

                    // Strange-style [Deconstruct] cleanup hooks run before IDisposable.Dispose.
                    RunDeconstructs(instance);

                    if (instance is IAsyncDisposable asyncDisposable)
                    {
                        // İ3-fix (C2 upgrade): never block the calling thread on async teardown,
                        // but dispose async singletons in ONE ordered background chain instead of
                        // one fire-and-forget task per instance. The old per-instance `_ =` fired
                        // them in parallel with no ordering, so registration-dependency order was
                        // lost. Collecting them and awaiting sequentially on the thread pool with
                        // ConfigureAwait(false) preserves order AND prevents continuations from
                        // re-capturing the Unity SynchronizationContext of the (disposing) caller.
                        asyncDisposables.Add(asyncDisposable);
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
            if (asyncDisposables.Count > 0)
                _ = DisposeAllAsyncInBackground(asyncDisposables);
            _bindings.Clear();
            _namedBindings.Clear();
        }

        /// <summary>
        /// Disposes IAsyncDisposable singletons sequentially on the thread pool with error capture.
        /// Used by the synchronous <see cref="Dispose()"/> path so teardown never blocks
        /// the calling (Unity main) thread — the deterministic async path is
        /// <see cref="DisposeAsync()"/>. ConfigureAwait(false) keeps every continuation on the
        /// thread pool rather than hopping back to the disposing thread's SynchronizationContext.
        /// </summary>
        private static async System.Threading.Tasks.Task DisposeAllAsyncInBackground(IReadOnlyList<IAsyncDisposable> asyncDisposables)
        {
            for (int i = 0; i < asyncDisposables.Count; i++)
            {
                try { await asyncDisposables[i].DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { /* Expected on context teardown */ }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Error disposing async singleton {asyncDisposables[i].GetType().FullName}: {ex.Message}");
                }
            }
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

                        // Strange-style [Deconstruct] cleanup hooks run before disposal.
                        RunDeconstructs(instance);

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
            _namedBindings.Clear();
        }

        public static void ClearCaches()
        {
            s_customInjectors.Clear();
            s_customClearers.Clear();
            MetadataCache.ClearAll();
        }
    }
}
