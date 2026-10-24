using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
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

    /// <summary>
    /// Defines how long a bound instance lives and who owns its disposal.
    /// <list type="bullet">
    /// <item><description><see cref="Lifetime.Transient"/>: a fresh instance is created on every
    /// resolve. The container never caches or disposes it — the caller owns cleanup.</description></item>
    /// <item><description><see cref="Lifetime.Scoped"/>: one instance per container (scope). It is
    /// created on first resolve in that container, cached, and disposed — in reverse creation
    /// order — when the container is disposed. Child containers that do not register the type
    /// resolve the parent's scoped instance (parent-scope semantics).</description></item>
    /// <item><description><see cref="Lifetime.Singleton"/>: one instance for the whole application.
    /// The binding is registered on the ROOT container regardless of where the Bind call happens,
    /// so every container in the hierarchy shares the same instance and it is disposed with the
    /// root (reverse creation order).</description></item>
    /// </list>
    /// The legacy <c>bool isSingleton</c> bind overloads map <c>true</c> → <see cref="Lifetime.Scoped"/>
    /// and <c>false</c> → <see cref="Lifetime.Transient"/>; they remain for backward compatibility.
    /// </summary>
    public enum Lifetime
    {
        Transient = 0,
        Scoped = 1,
        Singleton = 2,
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
                    return _resolvedSingletonOrder?.Count ?? 0;
            }
        }
        public bool StrictInjection { get; set; }
        // Created on the first lazy-service notification; most containers never queue one.
        private ConcurrentQueue<INexusService> _lazyServicesPendingInit;
        internal bool HasPendingLazyServices
        {
            get { var queue = Volatile.Read(ref _lazyServicesPendingInit); return queue != null && !queue.IsEmpty; }
        }
        internal bool TryDequeuePendingLazyService(out INexusService service)
        {
            var queue = Volatile.Read(ref _lazyServicesPendingInit);
            if (queue != null) return queue.TryDequeue(out service);
            service = null;
            return false;
        }
        private readonly NexusDI _parent;
        // Local binding changes invalidate this cache. Inherited registrations are never
        // cached here, so unrelated contexts retain their warmed resolution arrays.
        private long _bindingVersion;
        private sealed class FastResolveCache
        {
            internal readonly long Version;
            internal readonly object[] Slots;
            internal FastResolveCache(long version, int capacity)
            {
                Version = version;
                Slots = new object[capacity];
            }
        }
        private FastResolveCache _fastCache;

        /// <summary>
        /// Only resolved local singleton instances enter the fast cache. Transients were tried as cached
        /// <see cref="Binding"/> objects on 2026-10-15: the extra type test on every hit cost more on
        /// IL2CPP than the lookup it saved, and the shortcut bypassed the resolve accounting. Inherited
        /// values and adapters never enter the cache; every local bind bumps <c>_bindingVersion</c>,
        /// which drops the whole cache.
        /// </summary>
        private void CacheFastSlot(int typeId, Type type, object instance)
        {
            // Reject before taking the disposal lock; recheck cached ownership inside the lock so
            // concurrent rebinding cannot publish a stale instance.
            if (instance == null || !_bindings.TryGetValue(type, out var candidate)
                || !candidate.IsSingleton || !ReferenceEquals(candidate.Instance, instance)) return;
            lock (_disposeLock)
            {
                if (IsDisposed) return;
                if (!_bindings.TryGetValue(type, out var binding)
                    || !binding.IsSingleton || !ReferenceEquals(binding.Instance, instance)) return;
                long version = Volatile.Read(ref _bindingVersion);
                var cache = _fastCache;
                if (cache == null || cache.Version != version || typeId >= cache.Slots.Length)
                {
                    var replacement = new FastResolveCache(version, Math.Max(16, typeId + 16));
                    if (cache != null && cache.Version == version)
                        Array.Copy(cache.Slots, replacement.Slots, cache.Slots.Length);
                    cache = replacement;
                    Volatile.Write(ref _fastCache, cache);
                }
                Volatile.Write(ref cache.Slots[typeId], instance);
            }
        }
        // Optional feature maps are published on first registration, not per scope.
        // Their standard concurrency policy is unchanged once allocated.
        private ConcurrentDictionary<Type, bool> _crossBoundaryTypes;
        // Small striping: binds are rare and lock-striped, resolves are lock-free reads; the default
        // (processor-count locks, 31 buckets) charged every container for concurrency it never uses.
        // A copy-on-write Dictionary snapshot was measured on 2026-10-15 and rejected: the per-bind copy
        // doubled Mono startup and did not speed up IL2CPP transients.
        private readonly ConcurrentDictionary<Type, Binding> _bindings = new(concurrencyLevel: 1, capacity: 8);
        // Named bindings (Strange-style named injection): key = (type, name).
        // Missing explicit names never fall back to the default registration.
        private ConcurrentDictionary<(Type Type, string Name), Binding> _namedBindings;
        // Creation-ordered list of container-owned instances so Dispose tears down in REVERSE
        // creation order (a dependency created before its consumers is disposed after them,
        // so [Deconstruct]/Dispose can still touch it). The HashSet above stays for O(1)
        // membership/count/editor snapshots; this list carries the order guarantee.
        private List<object> _resolvedSingletonOrder;
        private int _disposeState;
        private readonly object _disposeLock = new();
        private ConcurrentDictionary<Type, bool> _lazyServiceTypes;
        internal Action<INexusService> LazyServiceResolvedCallback { get; set; }

        internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }

        private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;
        private void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(NexusDI));
        }

        private ConcurrentDictionary<TKey, TValue> GetOrCreateOptionalMap<TKey, TValue>(
            ref ConcurrentDictionary<TKey, TValue> map, IEqualityComparer<TKey> comparer = null)
        {
            var current = Volatile.Read(ref map);
            if (current != null) return current;
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                current = map;
                if (current == null)
                {
                    current = new ConcurrentDictionary<TKey, TValue>(comparer ?? EqualityComparer<TKey>.Default);
                    Volatile.Write(ref map, current);
                }
                return current;
            }
        }

        /// <summary>Safe editor snapshot of resolved singleton instances (thread-safe copy, no raw reference leak).</summary>
        internal IReadOnlyList<object> EditorResolvedSingletons
        {
            get
            {
                lock (_singletonLock)
                {
                    if (_resolvedSingletonOrder == null || _resolvedSingletonOrder.Count == 0)
                        return Array.Empty<object>();
                    return _resolvedSingletonOrder.ToArray();
                }
            }
        }

        private static readonly ConcurrentDictionary<Type, Action<object, NexusDI>> s_customInjectors = new();
        private static readonly ConcurrentDictionary<Type, Action<object>> s_customClearers = new();
        // Generated injectors are checked once per type against the type's real [Inject] members. A binder generated
        // before a member was added never sets that member, and no reflection pass runs while an injector exists.
        private static readonly ConcurrentDictionary<Type, bool> s_checkedInjectors = new();
        // AOT codegen constructor factories (NexusGeneratedBinder.g.cs): zero-reflection
        // instantiation on every build target. Keyed by the CONCRETE type; the generated
        // lambda is `di => new T(di.Resolve<A>(), di.Resolve<B>())` so readonly/immutable
        // constructor state works and IL2CPP never falls back to ConstructorInfo.Invoke.
        private static readonly ConcurrentDictionary<Type, Func<NexusDI, object>> s_constructorFactories = new();
        // Shared user-defined-interface filter (fluent AsImplementedInterfaces + ContextBuilder).
        // Interface metadata is immutable per Type, so the GetInterfaces() walk runs once per type.
        private static readonly ConcurrentDictionary<Type, Type[]> s_userDefinedInterfacesCache = new();

        private ConditionalWeakTable<object, PendingInjection> _pendingInjections; // created with the first pending member
        private readonly object _pendingInjectionsLock = new();
        // Keyed by the Binding object (not the requested Type): polymorphic bindings
        // (BindMultiple) expose ONE shared Binding under several interface keys, and a
        // type-keyed guard would let two threads construct the same singleton through
        // two different interfaces concurrently.
        private readonly object _singletonLock = new();
        // Background dispose task for IAsyncDisposable singletons scheduled by Dispose()
        // When Dispose() collects async disposables it runs them through DisposeAllAsyncInBackground
        // and stores the returned Task here so callers (e.g. Context.Dispose) may wait with a timeout
        // for deterministic teardown when needed.
        private Task _backgroundDisposeTask;
        // Per-binding wait handles for singleton construction synchronization
        private Dictionary<Binding, ManualResetEventSlim> _constructionWaitHandles; // first concurrent construction
        private readonly object _constructionWaitLock = new();
        private readonly Injector _injector;

        // ThreadStatic by design — cycle detection covers SYNCHRONOUS
        // resolution chains only. A factory that hands work to another thread and awaits
        // it would resume on a fresh stack slot; such async factories are outside the
        // supported contract (BindFactory is documented as synchronous).
        // Keyed by (type, name) so two DIFFERENT named bindings of the same type can
        // legitimately appear in one chain without a false circular-dependency error.
        // One thread-static reference: IL2CPP resolves every [ThreadStatic] access through thread-local
        // storage, so the chain lives in a single object read once per push and passed to the pop.
        private sealed class ResolutionStack
        {
            public Type[] Types = new Type[16];
            public string[] Names = new string[16];
            public int Depth;
        }
        [ThreadStatic] private static ResolutionStack s_resolution;

        /// <summary>Pushes (type, name) onto this thread's synchronous resolution chain; a repeat is a cycle.</summary>
        private static ResolutionStack PushResolution(Type type, string name, out int depth)
        {
            var stack = s_resolution ??= new ResolutionStack();
            var types = stack.Types;
            var names = stack.Names;
            depth = stack.Depth;
            for (int i = 0; i < depth; i++)
            {
                if (types[i] == type && string.Equals(names[i], name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Circular dependency detected while resolving {type.FullName}{(string.IsNullOrEmpty(name) ? "" : $" (name '{name}')")}. Resolution chain forms a cycle.");
            }
            if (depth == types.Length)
            {
                Array.Resize(ref stack.Types, depth * 2);
                Array.Resize(ref stack.Names, depth * 2);
                types = stack.Types;
                names = stack.Names;
            }
            types[depth] = type;
            names[depth] = name;
            stack.Depth = depth + 1;
            return stack;
        }

        private static void PopResolution(ResolutionStack stack, int depth)
        {
            stack.Depth = depth;
            stack.Types[depth] = null;
            stack.Names[depth] = null;
        }

        private class Binding
        {
            public Type ConcreteType { get; set; }
            public volatile object Instance;
            // Backed by a private field so the property name cannot shadow the Lifetime type
            // inside this class (see the qualified enum references below).
            private Lifetime _lifetime = Nexus.Core.Lifetime.Transient;
            public Lifetime Lifetime
            {
                get => _lifetime;
                set => _lifetime = value;
            }
            /// <summary>
            /// Legacy accessor: <c>true</c> → <see cref="Lifetime.Scoped"/>, <c>false</c> →
            /// <see cref="Lifetime.Transient"/>. Kept so existing object initializers
            /// (<c>new Binding { IsSingleton = ... }</c>) compile unchanged.
            /// </summary>
            public bool IsSingleton
            {
                get => _lifetime != Nexus.Core.Lifetime.Transient;
                set => _lifetime = value ? Nexus.Core.Lifetime.Scoped : Nexus.Core.Lifetime.Transient;
            }
            public Func<object> Factory { get; set; }
            /// <summary>Explicit constructor parameter values (fluent <c>WithParameter</c>). Null when none.</summary>
            public ParameterOverride[] ParameterOverrides { get; set; }

            // Transient fast path: the static lookups (generated factory, metadata, generated
            // injector) are cached here and refreshed whenever s_cacheGeneration moves.
            public int CacheGeneration = -1;
            public InjectableMetadata CachedMeta;
            public Func<NexusDI, object> CachedConstructor;
            public Action<object, NexusDI> CachedInjector;
            public bool CachedInjectorVerified;

            /// <summary>1 while a thread constructs this singleton (guarded by _singletonLock; concurrent readers wait on a handle).</summary>
            public int Constructing;
        }

        /// <summary>Bumped by every change to the static caches so per-binding caches refresh.</summary>
        private static int s_cacheGeneration;

        /// <summary>
        /// An explicit value for one constructor parameter of a binding's implementation,
        /// supplied via the fluent <see cref="FluentTypeBinder{T}.WithParameter(Type, object)"/>.
        /// Matched by exact parameter type; the first matching override wins over resolution.
        /// </summary>
        internal sealed class ParameterOverride
        {
            public readonly Type Type;
            public readonly object Value;
            public ParameterOverride(Type type, object value)
            {
                Type = type;
                Value = value;
            }
        }

        /// <summary>
        /// Fluent binding chain (Zenject/VContainer-style ergonomics):
        /// <c>di.BindFluent&lt;IFoo&gt;().To&lt;Foo&gt;().AsScoped()</c>,
        /// <c>di.BindFluent&lt;Foo&gt;().AsSingle().WithParameter(new FooOptions(1, 2))</c>.
        /// Every terminal method (<c>AsSingleton/AsScoped/AsTransient/AsSingle/AsCached/WithParameter</c>)
        /// applies the binding immediately (silently rebinding — no warning), so chain order is
        /// last-wins. The legacy <c>Bind&lt;T&gt;(bool)</c> overloads are untouched and remain canonical
        /// for existing code; this chain is a compatible addition.
        /// </summary>
        public sealed class FluentTypeBinder<T> where T : class
        {
            private readonly NexusDI _di;
            private Type _implType;
            private Lifetime _lifetime = Lifetime.Scoped;
            private bool _bindInterfaces;
            private readonly List<ParameterOverride> _overrides = new();

            internal FluentTypeBinder(NexusDI di)
            {
                _di = di;
                _implType = typeof(T);
            }

            /// <summary>Sets the implementation type (default: the key type itself).</summary>
            public FluentTypeBinder<T> To<TImplementation>() where TImplementation : class, T
            {
                _implType = typeof(TImplementation);
                return this;
            }

            /// <summary>One instance for the whole application, registered on the ROOT container.</summary>
            public FluentTypeBinder<T> AsSingleton() { _lifetime = Lifetime.Singleton; Apply(); return this; }

            /// <summary>One instance per container (scope); disposed with the container, reverse creation order.</summary>
            public FluentTypeBinder<T> AsScoped() { _lifetime = Lifetime.Scoped; Apply(); return this; }

            /// <summary>A fresh instance per resolve; never owned by the container.</summary>
            public FluentTypeBinder<T> AsTransient() { _lifetime = Lifetime.Transient; Apply(); return this; }

            /// <summary>Zenject-compat alias: one instance per container (identical to <see cref="AsScoped"/>).</summary>
            public FluentTypeBinder<T> AsSingle() { _lifetime = Lifetime.Scoped; Apply(); return this; }

            /// <summary>VContainer-compat alias for per-scope caching (identical to <see cref="AsScoped"/>).</summary>
            public FluentTypeBinder<T> AsCached() { _lifetime = Lifetime.Scoped; Apply(); return this; }

            /// <summary>
            /// Also registers the implementation under all of its user-defined interfaces
            /// (one shared binding). The key type is always bound regardless.
            /// </summary>
            public FluentTypeBinder<T> AsImplementedInterfaces() { _bindInterfaces = true; Apply(); return this; }

            /// <summary>Alias of <see cref="AsImplementedInterfaces"/> (Zenject <c>AndSelf</c> naming).</summary>
            public FluentTypeBinder<T> AndSelf() { _bindInterfaces = true; Apply(); return this; }

            /// <summary>Supplies an explicit value for the constructor parameter whose type is <typeparamref name="TParam"/>.</summary>
            public FluentTypeBinder<T> WithParameter<TParam>(TParam value) => WithParameter(typeof(TParam), value);

            /// <summary>Supplies an explicit value for the constructor parameter of the given type (reflection form).</summary>
            public FluentTypeBinder<T> WithParameter(Type parameterType, object value)
            {
                if (parameterType == null) throw new ArgumentNullException(nameof(parameterType));
                _overrides.Add(new ParameterOverride(parameterType, value));
                Apply();
                return this;
            }

            private void Apply()
            {
                var binding = new Binding
                {
                    ConcreteType = _implType,
                    Lifetime = _lifetime,
                    ParameterOverrides = _overrides.Count > 0 ? _overrides.ToArray() : null,
                };
                var target = _di.BindingTarget(_lifetime);
                lock (target._disposeLock)
                {
                    target.ThrowIfDisposed();
                    target.SetBindingCore(typeof(T), binding);
                    if (_bindInterfaces)
                    {
                        var interfaces = GetUserDefinedInterfaces(_implType);
                        for (int i = 0; i < interfaces.Length; i++)
                            target.SetBindingCore(interfaces[i], binding);
                    }
                }
            }
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
            /// <summary>Per-parameter binding names for the injected method (null = default binding).</summary>
            public string[] ParameterNames { get; set; }
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
            /// <summary>Why the container cannot construct this type, or null. Metadata is also built for
            /// instances it never constructs (injection, [Deconstruct] on dispose), so the error is raised
            /// only when construction is attempted.</summary>
            public string ConstructorError { get; set; }
            public Func<NexusDI, object> CompiledConstructor { get; set; }
            public Type[] ConstructorParameterTypes { get; set; }
            /// <summary>Per-parameter binding names for the injected constructor (null = default).</summary>
            public string[] ConstructorParameterNames { get; set; }
            /// <summary>Per-parameter C# optional-parameter flag (true = has a default value).</summary>
            public bool[] ConstructorParameterHasDefault { get; set; }
            /// <summary>Per-parameter default value when <see cref="ConstructorParameterHasDefault"/> is true.</summary>
            public object[] ConstructorParameterDefaults { get; set; }
            /// <summary>True when any [Inject] field, property or method exists (the member-injection loops have work).</summary>
            public bool HasMembers => Fields.Length > 0 || Properties.Length > 0 || Methods.Length > 0;
            /// <summary>
            /// A leaf takes nothing from the container (no constructor parameters, no members, no
            /// post-construct hooks) and therefore cannot take part in a resolution cycle.
            /// </summary>
            public bool IsLeaf => (ConstructorParameterTypes == null || ConstructorParameterTypes.Length == 0)
                && !HasMembers && PostConstructMethods == null;
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
            public bool InFlight; // a ReInject is resolving these outside the lock; guarded by _pendingInjectionsLock
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
                    // Walk the inheritance chain base-first: reflection on the leaf type
                    // never returns PRIVATE members declared on base classes, so [Inject]
                    // on a private base-class field/property/method was silently skipped.
                    // DeclaredOnly per level surfaces every declaration exactly once.
                    var typeChain = new List<Type>();
                    for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                        typeChain.Add(cur);
                    typeChain.Reverse();
                    const BindingFlags DeclaredFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                    var fieldList = new List<InjectableField>();
                    foreach (var level in typeChain)
                    foreach (var field in level.GetFields(DeclaredFlags))
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

                    var propList = new List<InjectableProperty>();
                    // Overridden virtual properties appear once per declaring level in the
                    // chain walk; dedupe by name so injection runs once (the base-level
                    // PropertyInfo still dispatches virtually to the most-derived setter).
                    var seenPropNames = new HashSet<string>();
                    foreach (var level in typeChain)
                    foreach (var prop in level.GetProperties(DeclaredFlags))
                    {
                        var injectAttr = prop.GetCustomAttribute<InjectAttribute>();
                        var optionalAttr = prop.GetCustomAttribute<OptionalInjectAttribute>();
                        // P-fix: accept [OptionalInject]-only properties (see field comment).
                        if ((injectAttr != null || optionalAttr != null) && prop.CanWrite && seenPropNames.Add(prop.Name))
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

                    var methodList = new List<InjectableMethod>();
                    var postConstructList = new List<InjectableMethod>();
                    var deconstructList = new List<InjectableMethod>();
                    // Dedupe overrides across chain levels by base definition so a virtual
                    // hook runs once (the retained MethodInfo still dispatches virtually to
                    // the most-derived override). The dedupe is applied only AFTER an
                    // attribute matched: skipping the whole method up front would drop an
                    // attribute declared on a derived override whose base declares none.
                    var seenInjectDefs = new HashSet<MethodInfo>();
                    var seenPostConstructDefs = new HashSet<MethodInfo>();
                    var seenDeconstructDefs = new HashSet<MethodInfo>();
                    foreach (var level in typeChain)
                    foreach (var method in level.GetMethods(DeclaredFlags))
                    {
                        var baseDef = method.GetBaseDefinition();
                        var injectAttr = method.GetCustomAttribute<InjectAttribute>();
                        var postAttr = method.GetCustomAttribute<PostConstructAttribute>();
                        var deconstructAttr = method.GetCustomAttribute<DeconstructAttribute>();
                        if (injectAttr != null && seenInjectDefs.Add(baseDef))
                        {
                            var parameters = method.GetParameters();
                            var paramTypes = new Type[parameters.Length];
                            var optionalMask = new bool[parameters.Length];
                            var paramNames = new string[parameters.Length];
                            for (int i = 0; i < parameters.Length; i++)
                            {
                                if (parameters[i].ParameterType.IsValueType)
                                    throw new InvalidOperationException($"Cannot inject value type parameter {t.FullName}.{method.Name}({parameters[i].Name}). Nexus DI only supports reference-type dependencies.");
                                paramTypes[i] = parameters[i].ParameterType;
                                optionalMask[i] = parameters[i].GetCustomAttribute<OptionalInjectAttribute>() != null;
                                // Honor [Inject(Name=...)] on method parameters so named
                                // bindings resolve exactly like fields/properties/ctor params.
                                var paramInject = parameters[i].GetCustomAttribute<InjectAttribute>();
                                if (paramInject != null) paramNames[i] = paramInject.Name;
                            }
                            methodList.Add(new InjectableMethod { Method = method, ParameterTypes = paramTypes, OptionalParameterMask = optionalMask, ParameterNames = paramNames });
                        }
                        if (postAttr != null && seenPostConstructDefs.Add(baseDef))
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
                        if (deconstructAttr != null && seenDeconstructDefs.Add(baseDef))
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
                    string ctorError = null;
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
                                    ctorError = $"No suitable constructor found for type {t.FullName}. A type must either have a parameterless constructor or a constructor decorated with [Inject]/[Construct].";
                                }
                            }
                        }
                    }

                    Type[] ctorParamTypes = null;
                    string[] ctorParamNames = null;
                    bool[] ctorParamHasDefault = null;
                    object[] ctorParamDefaults = null;
                    if (targetCtor != null)
                    {
                        var parameters = targetCtor.GetParameters();
                        ctorParamTypes = new Type[parameters.Length];
                        ctorParamNames = new string[parameters.Length];
                        ctorParamHasDefault = new bool[parameters.Length];
                        ctorParamDefaults = new object[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            // Value-type parameters are permitted here: they can only be
                            // satisfied by an explicit ParameterOverride (e.g. the fluent
                            // WithParameter<TParam> chain / Zenject WithArguments parity), a
                            // C# optional-parameter default (CommandPoolManager(int = 4)), or
                            // pass-through for registered value-type bindings.
                            ctorParamTypes[i] = parameters[i].ParameterType;
                            ctorParamHasDefault[i] = parameters[i].HasDefaultValue;
                            if (parameters[i].HasDefaultValue)
                                ctorParamDefaults[i] = parameters[i].DefaultValue;
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
                        ConstructorError = ctorError,
                        CompiledConstructor = CompiledAccessorEmitter.CompileConstructor(targetCtor),
                        ConstructorParameterTypes = ctorParamTypes,
                        ConstructorParameterNames = ctorParamNames,
                        ConstructorParameterHasDefault = ctorParamHasDefault,
                        ConstructorParameterDefaults = ctorParamDefaults
                    };
                });
            }

            internal static ClearableMetadata GetOrCreateClearMetadata(Type type)
            {
                return ClearMeta.GetOrAdd(type, t =>
                {
                    // Same base-first hierarchy walk as the inject metadata: private
                    // base-class [Inject] members must also be clearable.
                    var typeChain = new List<Type>();
                    for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                        typeChain.Add(cur);
                    typeChain.Reverse();
                    const BindingFlags DeclaredFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                    var fieldList = new List<FieldInfo>();
                    foreach (var level in typeChain)
                    foreach (var field in level.GetFields(DeclaredFlags))
                    {
                        // P-fix parity: [OptionalInject]-only members must also be clearable.
                        if ((field.GetCustomAttribute<InjectAttribute>() != null
                                || field.GetCustomAttribute<OptionalInjectAttribute>() != null)
                            && !field.FieldType.IsValueType)
                            fieldList.Add(field);
                    }

                    var propList = new List<PropertyInfo>();
                    var seenPropNames = new HashSet<string>();
                    foreach (var level in typeChain)
                    foreach (var prop in level.GetProperties(DeclaredFlags))
                    {
                        if ((prop.GetCustomAttribute<InjectAttribute>() != null
                                || prop.GetCustomAttribute<OptionalInjectAttribute>() != null)
                            && prop.CanWrite && !prop.PropertyType.IsValueType
                            && seenPropNames.Add(prop.Name))
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

            public object CreateInstance(Type type, IReadOnlyList<ParameterOverride> overrides = null)
            {
                // AOT codegen constructor factory (NexusGeneratedBinder.g.cs): zero-reflection
                // instantiation on every build target. The generated lambda performs the
                // constructor calls directly (`new T(di.Resolve<A>(), ...)`), so readonly
                // fields/immutability work and IL2CPP never falls back to ConstructorInfo.Invoke.
                // The factory is skipped when explicit constructor overrides (fluent
                // WithParameter) are present: the generated lambda resolves parameters from
                // the container and would silently drop the user's supplied values. The
                // reflection-with-overrides path is authoritative whenever the caller
                // provided explicit constructor arguments.
                if ((overrides == null || overrides.Count == 0)
                    && s_constructorFactories.TryGetValue(type, out var ctorFactory))
                    return ctorFactory(_di);

                var meta = MetadataCache.GetOrCreateInjectMetadata(type);
                if ((overrides == null || overrides.Count == 0) && meta.CompiledConstructor != null)
                    return meta.CompiledConstructor(_di);
                return CreateInstanceCore(type, meta, overrides);
            }

            /// <summary>
            /// Transient creation with the static lookups cached on the binding: no dictionary
            /// lookups per instance once the binding has been resolved under the current cache
            /// generation. Overrides and an external adapter keep the general path.
            /// </summary>
            public object CreateAndInject(Binding binding)
            {
                var overrides = binding.ParameterOverrides;
                if ((overrides != null && overrides.Length > 0) || _di.ExternalAdapter != null)
                {
                    var general = CreateInstance(binding.ConcreteType, overrides);
                    Inject(general);
                    return general;
                }
                int generation = Volatile.Read(ref s_cacheGeneration);
                if (Volatile.Read(ref binding.CacheGeneration) != generation)
                    RefreshBindingCache(binding, generation);
                var type = binding.ConcreteType;
                var meta = binding.CachedMeta;
                var ctor = binding.CachedConstructor;
                object instance = ctor != null ? ctor(_di) : CreateInstanceCore(type, meta, null);
                var injector = binding.CachedInjector;
                if (injector != null)
                {
                    injector(instance, _di);
                    if (!binding.CachedInjectorVerified)
                    {
                        CheckGeneratedInjectorOnce(instance, type, meta);
                        binding.CachedInjectorVerified = Volatile.Read(ref s_cacheGeneration) == generation;
                    }
                    RunPostConstructs(instance, meta);
                    return instance;
                }
                if (meta.HasMembers)
                {
                    InjectFields(instance, type, meta);
                    InjectProperties(instance, type, meta);
                    InjectMethods(instance, type, meta);
                }
                RunPostConstructs(instance, meta);
                return instance;
            }

            private static void RefreshBindingCache(Binding binding, int generation)
            {
                var type = binding.ConcreteType;
                var meta = MetadataCache.GetOrCreateInjectMetadata(type);
                s_constructorFactories.TryGetValue(type, out var factory);
                s_customInjectors.TryGetValue(type, out var injector);
                binding.CachedMeta = meta;
                binding.CachedConstructor = factory ?? meta.CompiledConstructor;
                binding.CachedInjector = injector;
                binding.CachedInjectorVerified = false;
                Volatile.Write(ref binding.CacheGeneration, generation); // publish after the fields
            }

            private object CreateInstanceCore(Type type, InjectableMetadata meta, IReadOnlyList<ParameterOverride> overrides)
            {
                if (meta.Constructor == null)
                {
                    if (meta.ConstructorError != null) throw new InvalidOperationException(meta.ConstructorError);
                    return Activator.CreateInstance(type, true);
                }

                var paramTypes = meta.ConstructorParameterTypes;
                var paramNames = meta.ConstructorParameterNames;
                var args = new object[paramTypes.Length];
                // Each override fills one parameter, in order: Create("x", "y") on (string a, string b) gave "x" to both.
                // shortcut: overrides past the 64th cannot be marked used and match first-wins; nothing passes that many.
                ulong usedOverrides = 0;
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    bool overridden = false;
                    if (overrides != null)
                    {
                        for (int o = 0; o < overrides.Count; o++)
                        {
                            if (overrides[o].Type == paramTypes[i] && (o >= 64 || (usedOverrides & (1UL << o)) == 0))
                            {
                                args[i] = overrides[o].Value;
                                if (o < 64) usedOverrides |= 1UL << o;
                                overridden = true;
                                break;
                            }
                        }
                    }
                    if (overridden)
                        continue;

                    args[i] = string.IsNullOrEmpty(paramNames?[i])
                        ? _di.TryResolve(paramTypes[i])
                        : _di.TryResolve(paramTypes[i], paramNames[i]);

                    // C# optional parameter (e.g. `CommandPoolManager(NexusDI, int initialSize = 4)`):
                    // an explicit binding/override wins, otherwise the declared default is used —
                    // this is a satisfied dependency, never a warning or strict-mode failure.
                    if (args[i] == null
                        && meta.ConstructorParameterHasDefault != null
                        && i < meta.ConstructorParameterHasDefault.Length
                        && meta.ConstructorParameterHasDefault[i])
                    {
                        args[i] = meta.ConstructorParameterDefaults[i];
                        continue;
                    }

                    if (args[i] == null && _di.StrictInjection)
                    {
                        throw new InvalidOperationException(
                            $"Strict injection failed: constructor parameter {i} of type '{paramTypes[i].FullName}' on '{type.FullName}' is not registered.");
                    }
                    // Non-strict mode previously passed null ctor args silently —
                    // a ctor that dereferences the dependency surfaced a bare NRE with no hint
                    // of WHICH binding was missing. Logged in ALL build targets: a missing
                    // binding is a configuration error and must never be silent in production.
                    if (args[i] == null && !_di.StrictInjection)
                    {
                        NexusRuntime.Logger?.LogWarning(
                            $"[Nexus] Constructor parameter {i} ('{paramTypes[i].FullName}') on '{type.FullName}' is not registered; passing null. " +
                            "Enable StrictInjection or mark the dependency [OptionalInject] to make this explicit.");
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
                    // A generated (AOT) injector replaces member injection, but [PostConstruct]
                    // must still run — the reflection path always ran it, so skipping it here
                    // would silently drop post-construction hooks on the AOT path. The metadata
                    // is cached, so this is a one-time scan per type, not a per-resolve cost.
                    injector(instance, _di);
                    var generatedMeta = MetadataCache.GetOrCreateInjectMetadata(type);
                    CheckGeneratedInjectorOnce(instance, type, generatedMeta);
                    RunPostConstructs(instance, generatedMeta);
                    return;
                }

                var meta = MetadataCache.GetOrCreateInjectMetadata(type);

                InjectFields(instance, type, meta);
                InjectProperties(instance, type, meta);
                InjectMethods(instance, type, meta);
                RunPostConstructs(instance, meta);
            }

            /// <summary>
            /// First instance of a type after a generated injector ran: if a required member the container could have
            /// supplied is still null, the binder is stale for that type; the injector and clearer are dropped (every
            /// per-binding cache refreshes through the generation bump) and this instance is injected by reflection.
            /// </summary>
            private void CheckGeneratedInjectorOnce(object instance, Type type, InjectableMetadata meta)
            {
                if (s_checkedInjectors.ContainsKey(type)) return;
                string missing = FirstMemberTheInjectorSkipped(instance, meta);
                s_checkedInjectors[type] = missing == null;
                if (missing == null) return;
                s_customInjectors.TryRemove(type, out _);
                s_customClearers.TryRemove(type, out _);
                Interlocked.Increment(ref s_cacheGeneration);
                NexusRuntime.Logger?.LogWarning($"[Nexus] The generated binder does not inject '{type.FullName}.{missing}' (it was generated before that member existed). " +
                    "Falling back to reflection for this type; regenerate the binder (Nexus > Generate AOT Binder).");
                InjectFields(instance, type, meta);
                InjectProperties(instance, type, meta);
                InjectMethods(instance, type, meta);
            }

            /// <summary>
            /// Name of the first required [Inject] field or property that a generated injector left null although this
            /// container could have supplied it, or null when the injector covered every such member. Members whose
            /// binding is absent are not counted: reflection would leave them null too, so they say nothing about the binder.
            /// </summary>
            private string FirstMemberTheInjectorSkipped(object instance, InjectableMetadata meta)
            {
                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    var f = meta.Fields[i];
                    if (f.IsOptional || f.IsLazy || f.Type.IsValueType || !_di.IsRegistered(f.Type, f.Name)) continue;
                    if ((f.Getter != null ? f.Getter(instance) : f.Field.GetValue(instance)) == null) return f.Field.Name;
                }
                for (int i = 0; i < meta.Properties.Length; i++)
                {
                    var p = meta.Properties[i];
                    if (p.IsOptional || p.IsLazy || p.Type.IsValueType || !p.Property.CanRead || !_di.IsRegistered(p.Type, p.Name)) continue;
                    if (p.Property.GetValue(instance) == null) return p.Property.Name;
                }
                return null;
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
                            // Forward the [Inject(Name=...)] discriminator so named
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
                        // Logged in ALL build targets — a silently-null [Inject] field in a
                        // production build surfaces as an unexplained NRE far from the cause.
                        NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{f.Type.FullName}' for field '{type.FullName}.{f.Field.Name}' is not registered; the field was left null.");
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
                        NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{p.Type.FullName}' for property '{type.FullName}.{p.Property.Name}' is not registered; the property was left null.");
                    }
                }
            }

            private void InjectMethods(object instance, Type type, InjectableMetadata meta)
            {
                for (int i = 0; i < meta.Methods.Length; i++)
                {
                    var m = meta.Methods[i];
                    var args = new object[m.ParameterTypes.Length];
                    // Track whether all required parameters were resolved.
                    // If any required parameter is missing, skip the invocation entirely and
                    // record it as pending — invoking with null could cause a
                    // NullReferenceException inside user code with no clear error origin.
                    bool allRequiredResolved = true;

                    for (int j = 0; j < m.ParameterTypes.Length; j++)
                    {
                        // Resolve through the named binding when [Inject(Name=...)]
                        // is present on the parameter, mirroring field/property/ctor behavior.
                        string paramName = m.ParameterNames != null ? m.ParameterNames[j] : null;
                        args[j] = string.IsNullOrEmpty(paramName)
                            ? _di.TryResolve(m.ParameterTypes[j])
                            : _di.TryResolve(m.ParameterTypes[j], paramName);
                        if (args[j] == null)
                        {
                            if (m.OptionalParameterMask[j])
                            {
                                // Optional: null is acceptable — leave it null.
                            }
                            else if (_di.StrictInjection)
                            {
                                throw new InvalidOperationException(
                                    $"Strict injection failed: [Inject] method '{type.FullName}.{m.Method.Name}' parameter {j} of type '{m.ParameterTypes[j].FullName}'{(string.IsNullOrEmpty(paramName) ? "" : $" (name '{paramName}')")} is not registered. Mark with [OptionalInject] if this dependency is optional.");
                            }
                            else
                            {
                                _di.RecordPendingMethodParam(instance, m, j);
                                allRequiredResolved = false;
                                NexusRuntime.Logger?.LogError($"[Nexus] [Inject] dependency '{m.ParameterTypes[j].FullName}' for method '{type.FullName}.{m.Method.Name}' is not registered; method invocation deferred.");
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

        /// <summary>
        /// Creates a child scope (container) for VContainer-style hierarchical lifetimes.
        /// <see cref="Lifetime.Scoped"/> instances resolved through the child are owned by the
        /// child and disposed — in reverse creation order — when the child is disposed; the parent
        /// and its instances are never affected. Types the child does not register fall back to the
        /// parent chain (parent-scope resolution). <see cref="Lifetime.Singleton"/> bindings made on
        /// the child are registered on the root container instead.
        /// </summary>
        /// <example>
        /// <code>
        /// using var child = parentContainer.CreateChildScope(scope =>
        /// {
        ///     scope.Bind&lt;ICombatModel, CombatModel&gt;(Lifetime.Scoped);
        /// });
        /// // child.Dispose() disposes CombatModel; parentContainer is unaffected.
        /// </code>
        /// </example>
        public NexusDI CreateChildScope(Action<NexusDI> configure = null)
        {
            ThrowIfDisposed();
            var child = new NexusDI(this);
            configure?.Invoke(child);
            return child;
        }

        /// <summary>Singleton bindings live on the root container; all other lifetimes bind where called.</summary>
        private NexusDI BindingTarget(Lifetime lifetime) => lifetime == Lifetime.Singleton ? GetRootContainer() : this;

        private NexusDI GetRootContainer()
        {
            var current = this;
            while (current._parent != null) current = current._parent;
            return current;
        }

        /// <summary>
        /// Starts a fluent binding chain (Zenject/VContainer-style):
        /// <c>di.BindFluent&lt;IFoo&gt;().To&lt;Foo&gt;().AsScoped().AsImplementedInterfaces()</c>.
        /// The chain applies the binding at each terminal call (<c>AsSingleton/AsScoped/AsTransient/
        /// AsSingle/AsCached/WithParameter</c>); calling <c>BindFluent</c> alone registers nothing.
        /// </summary>
        public FluentTypeBinder<T> BindFluent<T>() where T : class
        {
            ThrowIfDisposed();
            return new FluentTypeBinder<T>(this);
        }

        // ─── Public API: Bind ───

        /// <summary>
        /// Single write point for the default binding map: a rebind that silently
        /// replaces an existing registration is almost always a configuration mistake,
        /// so it is logged (the container stays last-write-wins for compatibility).
        /// </summary>
        private void SetBinding(Type key, Binding binding)
        {
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                SetBindingCore(key, binding);
            }
        }

        private void SetBindingCore(Type key, Binding binding)
        {
            if (_bindings.TryGetValue(key, out var existing) && !ReferenceEquals(existing, binding))
            {
                // Re-registering the exact same shape with no live instance is a benign
                // idempotent pattern (e.g. a command type bound once per handled signal);
                // only a materially different rebind — or one that discards an already
                // resolved singleton — indicates a probable configuration mistake.
                bool equivalent = existing.ConcreteType == binding.ConcreteType
                    && existing.Lifetime == binding.Lifetime
                    && existing.Factory == binding.Factory
                    && existing.Instance == null && binding.Instance == null;
                if (!equivalent)
                {
                    NexusRuntime.Logger?.LogWarning(
                        $"[Nexus] Rebinding '{key.FullName}': an existing registration is being replaced." +
                        (existing.Instance != null
                            ? " The previous binding already produced a singleton instance; it stays alive until the container is disposed."
                            : string.Empty));
                }
            }
            _bindings[key] = binding;
            Interlocked.Increment(ref _bindingVersion);
        }

        /// <summary>Named-map counterpart of <see cref="SetBinding"/>.</summary>
        private void SetNamedBinding((Type Type, string Name) key, Binding binding)
        {
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                SetNamedBindingCore(key, binding);
            }
        }

        private void SetNamedBindingCore((Type Type, string Name) key, Binding binding)
        {
            var namedBindings = GetOrCreateOptionalMap(ref _namedBindings);
            if (namedBindings.TryGetValue(key, out var existing) && !ReferenceEquals(existing, binding))
            {
                NexusRuntime.Logger?.LogWarning(
                    $"[Nexus] Rebinding '{key.Type.FullName}' (name '{key.Name}'): an existing registration is being replaced.");
            }
            namedBindings[key] = binding;
        }

        public void Bind<TInterface, TImplementation>(bool isSingleton = true) where TImplementation : class, TInterface
        {
            SetBinding(typeof(TInterface), new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton });
        }

        public void Bind<T>(bool isSingleton = true) where T : class
        {
            SetBinding(typeof(T), new Binding { ConcreteType = typeof(T), IsSingleton = isSingleton });
        }

        public void Bind(Type type, bool isSingleton = true)
        {
            SetBinding(type, new Binding { ConcreteType = type, IsSingleton = isSingleton });
        }

        public void Bind(Type interfaceType, Type implementationType, bool isSingleton = true)
        {
            SetBinding(interfaceType, new Binding { ConcreteType = implementationType, IsSingleton = isSingleton });
        }

        // ─── Explicit-lifetime Bind overloads (Lifetime.Scoped/Singleton/Transient) ───
        // Singleton bindings are registered on the ROOT container (BindingTarget), so every
        // container in the hierarchy shares one instance and it is disposed with the root.

        /// <summary>Binds with an explicit <see cref="Lifetime"/> (see <see cref="Lifetime"/> for semantics).</summary>
        public void Bind<TInterface, TImplementation>(Lifetime lifetime) where TImplementation : class, TInterface
        {
            BindingTarget(lifetime).SetBinding(typeof(TInterface), new Binding { ConcreteType = typeof(TImplementation), Lifetime = lifetime });
        }

        /// <summary>Binds a self-referencing type with an explicit <see cref="Lifetime"/>.</summary>
        public void Bind<T>(Lifetime lifetime) where T : class
        {
            BindingTarget(lifetime).SetBinding(typeof(T), new Binding { ConcreteType = typeof(T), Lifetime = lifetime });
        }

        /// <summary>Binds a type with an explicit <see cref="Lifetime"/> (reflection form).</summary>
        public void Bind(Type type, Lifetime lifetime)
        {
            BindingTarget(lifetime).SetBinding(type, new Binding { ConcreteType = type, Lifetime = lifetime });
        }

        /// <summary>Binds an interface to an implementation with an explicit <see cref="Lifetime"/> (reflection form).</summary>
        public void Bind(Type interfaceType, Type implementationType, Lifetime lifetime)
        {
            BindingTarget(lifetime).SetBinding(interfaceType, new Binding { ConcreteType = implementationType, Lifetime = lifetime });
        }

        /// <summary>Binds a named implementation (Strange-style). Resolves only against [Inject(Name=...)].</summary>
        public void Bind<TInterface, TImplementation>(string name, bool isSingleton = true) where TImplementation : class, TInterface
        {
            if (string.IsNullOrEmpty(name)) { Bind<TInterface, TImplementation>(isSingleton); return; }
            SetNamedBinding((typeof(TInterface), name), new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton });
        }

        /// <summary>Binds a named self-referencing type.</summary>
        public void Bind<T>(string name, bool isSingleton = true) where T : class
        {
            if (string.IsNullOrEmpty(name)) { Bind<T>(isSingleton); return; }
            SetNamedBinding((typeof(T), name), new Binding { ConcreteType = typeof(T), IsSingleton = isSingleton });
        }

        /// <summary>Binds a named implementation with an explicit <see cref="Lifetime"/>.</summary>
        public void Bind<TInterface, TImplementation>(string name, Lifetime lifetime) where TImplementation : class, TInterface
        {
            if (string.IsNullOrEmpty(name)) { Bind<TInterface, TImplementation>(lifetime); return; }
            BindingTarget(lifetime).SetNamedBinding((typeof(TInterface), name), new Binding { ConcreteType = typeof(TImplementation), Lifetime = lifetime });
        }

        /// <summary>Binds a named self-referencing type with an explicit <see cref="Lifetime"/>.</summary>
        public void Bind<T>(string name, Lifetime lifetime) where T : class
        {
            if (string.IsNullOrEmpty(name)) { Bind<T>(lifetime); return; }
            BindingTarget(lifetime).SetNamedBinding((typeof(T), name), new Binding { ConcreteType = typeof(T), Lifetime = lifetime });
        }

        /// <summary>Binds a named type with an explicit <see cref="Lifetime"/> (reflection form).</summary>
        public void Bind(Type type, string name, Lifetime lifetime)
        {
            if (string.IsNullOrEmpty(name)) { Bind(type, lifetime); return; }
            BindingTarget(lifetime).SetNamedBinding((type, name), new Binding { ConcreteType = type, Lifetime = lifetime });
        }

        /// <summary>Binds a named type (reflection-form).</summary>
        public void Bind(Type type, string name, bool isSingleton = true)
        {
            if (string.IsNullOrEmpty(name)) { Bind(type, isSingleton); return; }
            SetNamedBinding((type, name), new Binding { ConcreteType = type, IsSingleton = isSingleton });
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
            SetBinding(typeof(TInterface1), shared);
            SetBinding(typeof(TInterface2), shared);
            SetBinding(typeof(TImplementation), shared);
        }

        /// <summary>Three-interface polymorphic binding (see the two-interface overload).</summary>
        public void BindMultiple<TInterface1, TInterface2, TInterface3, TImplementation>(bool isSingleton = true)
            where TImplementation : class, TInterface1, TInterface2, TInterface3
        {
            var shared = new Binding { ConcreteType = typeof(TImplementation), IsSingleton = isSingleton };
            SetBinding(typeof(TInterface1), shared);
            SetBinding(typeof(TInterface2), shared);
            SetBinding(typeof(TInterface3), shared);
            SetBinding(typeof(TImplementation), shared);
        }

        /// <summary>Reflection-form polymorphic binding sharing ONE Binding instance across all interfaces and concrete type.</summary>
        public void BindMultiple(Type[] interfaceTypes, Type concreteType, bool isSingleton = true)
        {
            var shared = new Binding { ConcreteType = concreteType, IsSingleton = isSingleton };
            if (interfaceTypes != null)
            {
                for (int i = 0; i < interfaceTypes.Length; i++)
                {
                    SetBinding(interfaceTypes[i], shared);
                }
            }
            SetBinding(concreteType, shared);
        }

        /// <summary>Two-interface polymorphic binding with an explicit <see cref="Lifetime"/>.</summary>
        public void BindMultiple<TInterface1, TInterface2, TImplementation>(Lifetime lifetime)
            where TImplementation : class, TInterface1, TInterface2
        {
            var shared = new Binding { ConcreteType = typeof(TImplementation), Lifetime = lifetime };
            var target = BindingTarget(lifetime);
            target.SetBinding(typeof(TInterface1), shared);
            target.SetBinding(typeof(TInterface2), shared);
            target.SetBinding(typeof(TImplementation), shared);
        }

        /// <summary>Three-interface polymorphic binding with an explicit <see cref="Lifetime"/>.</summary>
        public void BindMultiple<TInterface1, TInterface2, TInterface3, TImplementation>(Lifetime lifetime)
            where TImplementation : class, TInterface1, TInterface2, TInterface3
        {
            var shared = new Binding { ConcreteType = typeof(TImplementation), Lifetime = lifetime };
            var target = BindingTarget(lifetime);
            target.SetBinding(typeof(TInterface1), shared);
            target.SetBinding(typeof(TInterface2), shared);
            target.SetBinding(typeof(TInterface3), shared);
            target.SetBinding(typeof(TImplementation), shared);
        }

        /// <summary>Reflection-form polymorphic binding with an explicit <see cref="Lifetime"/>.</summary>
        public void BindMultiple(Type[] interfaceTypes, Type concreteType, Lifetime lifetime)
        {
            var shared = new Binding { ConcreteType = concreteType, Lifetime = lifetime };
            var target = BindingTarget(lifetime);
            if (interfaceTypes != null)
            {
                for (int i = 0; i < interfaceTypes.Length; i++)
                {
                    target.SetBinding(interfaceTypes[i], shared);
                }
            }
            target.SetBinding(concreteType, shared);
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
            SetBinding(typeof(TInterface), new Binding { ConcreteType = typeof(TImplementation), IsSingleton = true });
            GetOrCreateOptionalMap(ref _crossBoundaryTypes)[typeof(TInterface)] = true;
        }

        /// <summary>Binds a self-referencing type as cross-boundary.</summary>
        public void BindCrossBoundary<T>() where T : class
        {
            SetBinding(typeof(T), new Binding { ConcreteType = typeof(T), IsSingleton = true });
            GetOrCreateOptionalMap(ref _crossBoundaryTypes)[typeof(T)] = true;
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
            if (Volatile.Read(ref _crossBoundaryTypes)?.ContainsKey(type) == true)
            {
                if (_bindings.TryGetValue(type, out var binding))
                    return ResolveBinding(type, binding);
            }

            // Walk the parent chain, looking for cross-boundary-marked types
            var current = _parent;
            while (current != null)
            {
                if (Volatile.Read(ref current._crossBoundaryTypes)?.ContainsKey(type) == true)
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
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                SetBindingCore(typeof(T), new Binding { ConcreteType = typeof(T), Instance = instance, IsSingleton = true });
                if (disposeWithContainer)
                {
                    lock (_singletonLock)
                    {
                        TrackResolvedSingleton(instance);
                    }
                }
            }
        }

        /// <summary>Binds a named instance value.</summary>
        public void BindInstance<T>(string name, T instance) where T : class
        {
            if (string.IsNullOrEmpty(name)) { BindInstance(instance); return; }
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                SetNamedBindingCore((typeof(T), name), new Binding { ConcreteType = typeof(T), Instance = instance, IsSingleton = true });
                lock (_singletonLock)
                {
                    TrackResolvedSingleton(instance);
                }
            }
        }

        public void BindFactory<T>(Func<T> factory) where T : class
        {
            SetBinding(typeof(T), new Binding { ConcreteType = typeof(T), Factory = factory, IsSingleton = false });
        }

        /// <summary>Binds a named factory (a fresh instance per resolve).</summary>
        public void BindFactory<T>(string name, Func<T> factory) where T : class
        {
            if (string.IsNullOrEmpty(name)) { BindFactory(factory); return; }
            SetNamedBinding((typeof(T), name), new Binding { ConcreteType = typeof(T), Factory = factory, IsSingleton = false });
        }

        // ─── Public API: typed factories ───
        /// <summary>
        /// Binds <see cref="IFactory{TValue}"/>: every <c>Create()</c> constructs a new
        /// <typeparamref name="TValue"/> through this container (constructor, members, [PostConstruct]).
        /// The instances are not owned by the container; dispose them yourself when they are disposable.
        /// </summary>
        public void BindTypedFactory<TValue>() where TValue : class
            => BindInstance<IFactory<TValue>>(new ContainerFactory<TValue>(this), disposeWithContainer: false);

        /// <summary>
        /// Binds <see cref="IFactory{TParam, TValue}"/>: the argument of <c>Create(param)</c> is passed to
        /// the constructor parameter of type <typeparamref name="TParam"/>; everything else is resolved.
        /// </summary>
        public void BindTypedFactory<TParam, TValue>() where TValue : class
            => BindInstance<IFactory<TParam, TValue>>(new ContainerFactory<TParam, TValue>(this), disposeWithContainer: false);

        /// <summary>Binds <see cref="IFactory{TParam1, TParam2, TValue}"/>; arguments are matched to constructor parameters by type.</summary>
        public void BindTypedFactory<TParam1, TParam2, TValue>() where TValue : class
            => BindInstance<IFactory<TParam1, TParam2, TValue>>(new ContainerFactory<TParam1, TParam2, TValue>(this), disposeWithContainer: false);

        /// <summary>Binds <see cref="IFactory{TValue}"/> to a delegate; the container does not construct or inject the result.</summary>
        public void BindTypedFactory<TValue>(Func<TValue> create)
            => BindInstance<IFactory<TValue>>(new DelegateFactory<TValue>(create), disposeWithContainer: false);

        /// <summary>Binds <see cref="IFactory{TParam, TValue}"/> to a delegate.</summary>
        public void BindTypedFactory<TParam, TValue>(Func<TParam, TValue> create)
            => BindInstance<IFactory<TParam, TValue>>(new DelegateFactory<TParam, TValue>(create), disposeWithContainer: false);

        /// <summary>Binds <see cref="IFactory{TParam1, TParam2, TValue}"/> to a delegate.</summary>
        public void BindTypedFactory<TParam1, TParam2, TValue>(Func<TParam1, TParam2, TValue> create)
            => BindInstance<IFactory<TParam1, TParam2, TValue>>(new DelegateFactory<TParam1, TParam2, TValue>(create), disposeWithContainer: false);

        // ─── Public API: Construct ───
        /// <summary>
        /// Constructs and injects a <typeparamref name="T"/> that need not be bound: constructor
        /// parameters and [Inject] members are resolved from this container, [PostConstruct] runs.
        /// The container does not own the instance.
        /// </summary>
        public T Construct<T>() where T : class => (T)Construct(typeof(T), null);

        /// <summary>Non-generic form of <see cref="Construct{T}"/>.</summary>
        public object Construct(Type type) => Construct(type, null);

        internal object Construct(Type type, IReadOnlyList<ParameterOverride> overrides)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            ThrowIfDisposed();
            var instance = _injector.CreateInstance(type, overrides);
            _injector.Inject(instance);
            return instance;
        }

        // ─── Public API: Resolve ───
        public T Resolve<T>() where T : class
        {
            ThrowIfDisposed();
            int typeId = TypeIdCache<T>.Id;
            var cache = Volatile.Read(ref _fastCache);
            if (cache != null && cache.Version == Volatile.Read(ref _bindingVersion)
                && typeId < cache.Slots.Length)
            {
                var fastInstance = Volatile.Read(ref cache.Slots[typeId]);
                if (fastInstance != null) return (T)fastInstance;
            }
            var resolved = (T)ResolveCore(typeof(T), throwIfMissing: true, out var binding);
            if (binding != null && binding.IsSingleton && ReferenceEquals(binding.Instance, resolved))
                CacheFastSlot(typeId, typeof(T), resolved);
            return resolved;
        }

        /// <summary>Null when the type is not registered anywhere in the chain; otherwise the same as <see cref="Resolve{T}"/>.</summary>
        public T TryResolve<T>() where T : class
        {
            int typeId = TypeIdCache<T>.Id;
            var cache = Volatile.Read(ref _fastCache);
            if (cache != null && cache.Version == Volatile.Read(ref _bindingVersion)
                && typeId < cache.Slots.Length)
            {
                var fastInstance = Volatile.Read(ref cache.Slots[typeId]);
                if (fastInstance != null) return (T)fastInstance;
            }
            var resolved = ResolveCore(typeof(T), throwIfMissing: false, out var binding);
            if (resolved == null) return null;
            ThrowIfDisposed();
            if (binding != null && binding.IsSingleton && ReferenceEquals(binding.Instance, resolved))
                CacheFastSlot(typeId, typeof(T), resolved);
            return (T)resolved;
        }
        /// <summary>Safely resolves a named binding; returns null when the name is not registered.</summary>
        public T TryResolve<T>(string name) where T : class => TryResolve(typeof(T), name) as T;

        /// <summary>
        /// Codegen support for the AOT binder (<see cref="NexusCodeGenerator"/>): resolves a
        /// constructor parameter with EXACTLY the reflection path's semantics
        /// (<see cref="Injector.CreateInstance"/>) — TryResolve, strict throw on missing,
        /// warn-and-pass-null otherwise. Emitted into <c>RegisterConstructorFactory</c> lambdas
        /// so IL2CPP builds keep identical behavior without <c>ConstructorInfo.Invoke</c>.
        /// </summary>
        /// <param name="parameterIndex">Zero-based constructor parameter index (for diagnostics).</param>
        /// <param name="ownerTypeName">Full name of the type being constructed (for diagnostics).</param>
        /// <param name="parameterTypeName">Full name of the parameter type (for diagnostics).</param>
        /// <param name="name">Optional <c>[Inject(Name=...)]</c> binding name.</param>
        public T ResolveConstructorParameter<T>(int parameterIndex, string ownerTypeName, string parameterTypeName, string name = null) where T : class
        {
            T value = string.IsNullOrEmpty(name) ? TryResolve<T>() : TryResolve<T>(name);
            if (value == null && StrictInjection)
            {
                throw new InvalidOperationException(
                    $"Strict injection failed: constructor parameter {parameterIndex} of type '{parameterTypeName}' on '{ownerTypeName}' is not registered.");
            }
            if (value == null && !StrictInjection)
            {
                NexusRuntime.Logger?.LogWarning(
                    $"[Nexus] Constructor parameter {parameterIndex} ('{parameterTypeName}') on '{ownerTypeName}' is not registered; passing null. " +
                    "Enable StrictInjection or mark the dependency [OptionalInject] to make this explicit.");
            }
            return value;
        }
        public object TryResolve(Type type)
        {
            if (type == null) return null;
            var resolved = ResolveCore(type, throwIfMissing: false, out _);
            if (resolved != null) ThrowIfDisposed();
            return resolved;
        }

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
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(name)) return Resolve(type);
            var namedBindings = Volatile.Read(ref _namedBindings);
            if (namedBindings != null && namedBindings.TryGetValue((type, name), out var named))
                return ResolveAndNotifyLazy(type, named, name);
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

        // Profiler marker + counter on the resolve hot path. Both exist only where Unity enables
        // the profiler (editor and development builds); release players pay nothing. See NexusProfiler.
#if ENABLE_PROFILER
        private static readonly ProfilerMarker s_ResolveMarker = new ProfilerMarker("Nexus.DI.Resolve");
#endif

        public object Resolve(Type type)
        {
            ThrowIfDisposed();
            return ResolveCore(type, throwIfMissing: true, out _);
        }

        /// <summary>
        /// One lookup per container level for both Resolve and TryResolve. LOCAL bindings win over
        /// the external adapter (an explicit Bind is never shadowed by the adapter's fast path);
        /// the parent chain is last. <paramref name="binding"/> is the local binding that served
        /// the request, or null when an adapter or parent served it.
        /// </summary>
        private object ResolveCore(Type type, bool throwIfMissing, out Binding binding)
        {
            NexusProfiler.ResolvesPerformed.Value += 1;
#if ENABLE_PROFILER
            s_ResolveMarker.Begin();
#endif
            try
            {
                binding = null;
                if (type == typeof(NexusDI)) return this;
                if (_bindings.TryGetValue(type, out binding))
                    return ResolveAndNotifyLazy(type, binding);
                if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type))
                    return ExternalAdapter.Resolve(type);
                if (_parent != null)
                    return throwIfMissing ? _parent.Resolve(type) : _parent.ResolveCore(type, false, out _);
                if (throwIfMissing)
                    throw new InvalidOperationException($"Dependency of type {type.FullName} is not registered.");
                return null;
            }
            finally
            {
#if ENABLE_PROFILER
                s_ResolveMarker.End();
#endif
            }
        }

        private object ResolveAndNotifyLazy(Type type, Binding binding, string name = null)
        {
            var instance = ResolveBinding(type, binding, name);
            NotifyMarkedLazyService(type, instance);
            return instance;
        }

        /// <summary>
        /// Shared resolve core for both default and named bindings: singleton construction
        /// with cross-thread waiting, factory mapping, and transient instantiation.
        /// </summary>
        private object ResolveBinding(Type type, Binding binding, string name = null)
        {
            ThrowIfDisposed();
            if (binding.Instance != null) return binding.Instance;

            // A transient leaf (cached metadata says it takes nothing from the container) cannot form a
            // cycle, so it skips the resolution-chain bookkeeping entirely.
            if (!binding.IsSingleton && binding.Factory == null && binding.ParameterOverrides == null
                && Volatile.Read(ref binding.CacheGeneration) == Volatile.Read(ref s_cacheGeneration)
                && binding.CachedMeta != null && binding.CachedMeta.IsLeaf)
                return _injector.CreateAndInject(binding);

            // Cycle detection must also guard factory-produced instances. Previously
            // the factory check ran BEFORE the resolution-stack push, so a factory that
            // resolved back to its own key (directly or transitively) recursed until
            // StackOverflowException — an uncatchable process crash. Moving the factory
            // call inside the guarded block turns that into a clear, catchable
            // InvalidOperationException naming the offending type.
            var resolution = PushResolution(type, name, out int depth);

            bool addedToConstructing = false;
            ManualResetEventSlim waitHandle = null;
            try
            {
                if (binding.Factory != null) return binding.Factory();

                if (binding.IsSingleton)
                {
                    object singletonInstance = binding.Instance;
                    if (singletonInstance != null) return singletonInstance;

                    // Same-thread cycles are caught by the thread-local
                    // s_resolutionStack above, so a failed Add here can only mean
                    // ANOTHER thread is mid-construction of this singleton. Wait for
                    // the builder instead of throwing a spurious "circular dependency"
                    // on a perfectly valid concurrent first-resolve.
                    while (true)
                    {
                        lock (_singletonLock)
                        {
                            if (IsDisposed)
                                throw new ObjectDisposedException(nameof(NexusDI), $"Cannot resolve singleton '{type.FullName}': the container has been disposed.");

                            if (binding.Instance != null) return binding.Instance;
                            if (Interlocked.CompareExchange(ref binding.Constructing, 1, 0) == 0)
                            {
                                addedToConstructing = true;
                                break;
                            }

                            // Another thread is constructing this singleton.
                            // Get or create a wait handle for this binding.
                            if (!(_constructionWaitHandles ??= new Dictionary<Binding, ManualResetEventSlim>()).TryGetValue(binding, out waitHandle))
                            {
                                waitHandle = new ManualResetEventSlim(false);
                                _constructionWaitHandles[binding] = waitHandle;
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
                        singletonInstance = _injector.CreateInstance(binding.ConcreteType, binding.ParameterOverrides);
                        _injector.Inject(singletonInstance);
                        bool orphaned;
                        lock (_singletonLock)
                        {
                            orphaned = IsDisposed;
                            if (!orphaned)
                            {
                                binding.Instance = singletonInstance;
                                TrackResolvedSingleton(singletonInstance);
                            }
                        }
                        if (orphaned)
                        {
                            // Disposed while it was being built: nothing tracks it (nor does the Context), so release it here, outside the lock.
                            try
                            {
                                RunDeconstructs(singletonInstance);
                                if (singletonInstance is IDisposable disposable) disposable.Dispose();
                                else if (singletonInstance is IAsyncDisposable asyncDisposable) _ = DisposeAllAsyncInBackground(new[] { asyncDisposable });
                            }
                            catch (Exception ex)
                            {
                                NexusRuntime.Logger?.LogError($"[Nexus] Error disposing singleton {singletonInstance.GetType().FullName} built during container disposal: {ex.Message}");
                            }
                            throw new ObjectDisposedException(nameof(NexusDI), $"Cannot publish singleton '{type.FullName}': the container has been disposed.");
                        }
                    }
                    finally
                    {
                        lock (_singletonLock)
                        {
                            Volatile.Write(ref binding.Constructing, 0);
                            // Signal all waiting threads that construction is complete.
                            if (_constructionWaitHandles != null && _constructionWaitHandles.TryGetValue(binding, out var wh))
                            {
                                wh.Set();
                                _constructionWaitHandles.Remove(binding);
                            }
                        }
                        addedToConstructing = false;
                    }
                    return singletonInstance;
                }

                return _injector.CreateAndInject(binding);
            }
            finally
            {
                PopResolution(resolution, depth);
                if (addedToConstructing)
                {
                    lock (_singletonLock)
                    {
                        Volatile.Write(ref binding.Constructing, 0);
                        if (_constructionWaitHandles != null && _constructionWaitHandles.TryGetValue(binding, out var wh))
                        {
                            wh.Set();
                            _constructionWaitHandles.Remove(binding);
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

            PendingInjection pending;
            InjectableField[] fields;
            InjectableProperty[] properties;
            (InjectableMethod Method, int[] ParamIndices)[] methods;
            lock (_pendingInjectionsLock)
            {
                if (_pendingInjections == null || !_pendingInjections.TryGetValue(instance, out pending))
                    return true;
                if (pending.InFlight) return false; // another thread is re-injecting it; that pass decides
                pending.InFlight = true;
                fields = pending.Fields.ToArray();
                properties = pending.Properties.ToArray();
                methods = pending.Methods.ToArray();
            }

            // Resolution (user constructors) and [Inject] methods run outside the lock: user code that waited
            // on another thread's ReInject or Inject deadlocked on it. Applied entries are removed afterwards.
            var appliedFields = new List<InjectableField>();
            var appliedProperties = new List<InjectableProperty>();
            var appliedMethods = new List<(InjectableMethod Method, int[] ParamIndices)>();
            bool done;
            try
            {
                foreach (var f in fields)
                {
                    var resolvedValue = string.IsNullOrEmpty(f.Name) ? TryResolve(f.Type) : TryResolve(f.Type, f.Name);
                    if (resolvedValue != null) { MetadataCache.ApplyFieldSetter(f, instance, resolvedValue); appliedFields.Add(f); }
                }

                foreach (var p in properties)
                {
                    var resolvedValue = string.IsNullOrEmpty(p.Name) ? TryResolve(p.Type) : TryResolve(p.Type, p.Name);
                    if (resolvedValue != null) { MetadataCache.ApplyPropertySetter(p, instance, resolvedValue); appliedProperties.Add(p); }
                }

                foreach (var entry in methods)
                {
                    var (method, paramIndices) = entry;
                    var args = new object[method.ParameterTypes.Length];
                    bool methodSucceeded = true;
                    for (int j = 0; j < method.ParameterTypes.Length; j++)
                    {
                        // Only re-resolve the parameters that were MISSING on the
                        // original Inject pass (those in paramIndices). Parameters that were
                        // already resolved must keep their original instance — re-resolving a
                        // transient here would silently swap in a NEW instance, breaking the
                        // method's captured dependency. Named bindings are honored too (D1).
                        if (Array.IndexOf(paramIndices, j) >= 0)
                        {
                            string paramName = method.ParameterNames != null ? method.ParameterNames[j] : null;
                            args[j] = string.IsNullOrEmpty(paramName)
                                ? TryResolve(method.ParameterTypes[j])
                                : TryResolve(method.ParameterTypes[j], paramName);
                            if (args[j] == null)
                                methodSucceeded = false;
                        }
                    }
                    if (methodSucceeded) { method.Method.Invoke(instance, args); appliedMethods.Add(entry); }
                }
            }
            finally
            {
                lock (_pendingInjectionsLock)
                {
                    pending.InFlight = false;
                    foreach (var f in appliedFields) pending.Fields.Remove(f);
                    foreach (var p in appliedProperties) pending.Properties.Remove(p);
                    // a method entry whose missing-parameter list grew meanwhile (new array) stays pending
                    foreach (var applied in appliedMethods)
                        pending.Methods.RemoveAll(m => m.Method == applied.Method && ReferenceEquals(m.ParamIndices, applied.ParamIndices));
                    done = pending.Fields.Count == 0 && pending.Properties.Count == 0 && pending.Methods.Count == 0;
                    if (done && _pendingInjections != null && _pendingInjections.TryGetValue(instance, out var current) && ReferenceEquals(current, pending))
                        _pendingInjections.Remove(instance);
                }
            }
            return done;
        }

        public int ReInjectAll()
        {
            var snapshot = new List<KeyValuePair<object, PendingInjection>>();
            lock (_pendingInjectionsLock)
            {
                if (_pendingInjections != null) foreach (var kvp in _pendingInjections) snapshot.Add(kvp);
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
                _pendingInjections?.Remove(instance);
            }
        }

        private void RecordPendingField(object instance, InjectableField field)
        {
            lock (_pendingInjectionsLock)
            {
                var pending = (_pendingInjections ??= new ConditionalWeakTable<object, PendingInjection>()).GetOrCreateValue(instance);
                pending.Fields.Add(field);
            }
        }

        private void RecordPendingProperty(object instance, InjectableProperty property)
        {
            lock (_pendingInjectionsLock)
            {
                var pending = (_pendingInjections ??= new ConditionalWeakTable<object, PendingInjection>()).GetOrCreateValue(instance);
                pending.Properties.Add(property);
            }
        }

        private void RecordPendingMethodParam(object instance, InjectableMethod method, int paramIndex)
        {
            lock (_pendingInjectionsLock)
            {
                var pending = (_pendingInjections ??= new ConditionalWeakTable<object, PendingInjection>()).GetOrCreateValue(instance);
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
        /// <summary>
        /// Commands are owned and reset by their pool. Keep local transient consumer
        /// factories/overrides, but never pool a DI-owned singleton or inherit the
        /// parent's automatic command binding (construction must use this scope).
        /// </summary>
        internal void EnsureCommandBinding(Type commandType)
        {
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                if (_bindings.TryGetValue(commandType, out var binding) && !binding.IsSingleton)
                    return;
                SetBindingCore(commandType, new Binding { ConcreteType = commandType, IsSingleton = false });
            }
        }

        public bool IsRegistered(Type type)
        {
            if (type == typeof(NexusDI)) return true;
            // Local bindings first (an explicit Bind must never be shadowed by the adapter);
            // adapter is a fallback for types this container does not bind.
            if (_bindings.ContainsKey(type)) return true;
            if (ExternalAdapter != null && ExternalAdapter.IsRegistered(type)) return true;
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
            if (Volatile.Read(ref _namedBindings)?.ContainsKey((type, name)) == true) return true;
            // Adapter does not participate in NAMED lookup (it has no names); a local named
            // binding must win before the parent chain is consulted.
            return _parent != null && _parent.IsRegistered(type, name);
        }

        internal sealed class ValidationBinding
        {
            internal NexusDI Owner;
            internal object Identity;
            internal Type Key, Concrete;
            internal string Name;
            internal Lifetime Lifetime;
            internal bool IsOpaque;
            internal ParameterOverride[] Overrides;
        }

        private ValidationBinding DescribeBinding(Type key, string name, Binding binding)
            => new ValidationBinding
            {
                Owner = this, Identity = binding, Key = key, Name = name,
                Concrete = binding.ConcreteType, Lifetime = binding.Lifetime,
                IsOpaque = binding.Factory != null || binding.Instance != null,
                Overrides = binding.ParameterOverrides
            };

        internal List<ValidationBinding> GetValidationBindings()
        {
            var result = new List<ValidationBinding>();
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                foreach (var pair in _bindings)
                    result.Add(DescribeBinding(pair.Key, null, pair.Value));
                var namedBindings = Volatile.Read(ref _namedBindings);
                if (namedBindings != null)
                    foreach (var pair in namedBindings)
                        result.Add(DescribeBinding(pair.Key.Type, pair.Key.Name, pair.Value));
                result.Sort((a, b) =>
                {
                    int key = string.CompareOrdinal(a.Key.FullName, b.Key.FullName);
                    return key != 0 ? key : string.CompareOrdinal(a.Name, b.Name);
                });
            }
            if (_parent != null) result.AddRange(_parent.GetValidationBindings());
            return result;
        }

        internal ValidationBinding FindValidationBinding(Type key, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                if (_bindings.TryGetValue(key, out var binding)) return DescribeBinding(key, null, binding);
                if (ExternalAdapter != null && ExternalAdapter.IsRegistered(key)) return null;
            }
            else
            {
                var namedBindings = Volatile.Read(ref _namedBindings);
                if (namedBindings != null && namedBindings.TryGetValue((key, name), out var named))
                    return DescribeBinding(key, name, named);
            }
            return _parent?.FindValidationBinding(key, name);
        }

        /// <summary>
        /// Inspects registered dependency graphs without constructing objects or invoking
        /// user factories. Reports missing dependencies, invalid metadata, eager cycles and
        /// captive transients. Factories and supplied instances are opaque graph boundaries.
        /// Call after registration on the startup thread, before gameplay; this is not a hot path.
        /// </summary>
        public List<DiValidationIssue> ValidateBindings(int maxConstructorParameters = 6)
            => DiBindingValidator.Validate(this, maxConstructorParameters);

        internal HashSet<Type> GetAllRegisteredTypes()
        {
            var types = new HashSet<Type>(_bindings.Keys);
            var namedBindings = Volatile.Read(ref _namedBindings);
            if (namedBindings != null)
                foreach (var kvp in namedBindings) types.Add(kvp.Key.Type);
            types.Add(typeof(NexusDI));
            types.Add(typeof(IContext));
            types.Add(typeof(ISignalBus));
            if (_parent != null) types.UnionWith(_parent.GetAllRegisteredTypes());
            return types;
        }

        /// <summary>
        /// Returns whether the type is bound as a singleton in this container or any
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
        /// Returns whether the type is bound via a factory (BindFactory) in this
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

        internal void PrepareInjectionMetadata(Type type)
        {
            ThrowIfDisposed();
            MetadataCache.GetOrCreateInjectMetadata(type);
            MetadataCache.GetOrCreateClearMetadata(type);
        }

        private ConcurrentDictionary<INexusService, bool> _lazyServicesEnqueued;

        internal void MarkLazyService(Type type)
        {
            if (type != null) GetOrCreateOptionalMap(ref _lazyServiceTypes)[type] = true;
        }

        private void NotifyMarkedLazyService(Type requestedType, object instance)
        {
            var lazyServiceTypes = Volatile.Read(ref _lazyServiceTypes);
            if (lazyServiceTypes != null && instance is INexusService service
                && (lazyServiceTypes.ContainsKey(requestedType) || lazyServiceTypes.ContainsKey(instance.GetType())))
                NotifyLazyServiceResolved(instance.GetType(), service);
        }

        internal void NotifyLazyServiceResolved(Type type, object instance)
        {
            if (instance is INexusService service
                && GetOrCreateOptionalMap(ref _lazyServicesEnqueued, ReferenceComparer<INexusService>.Instance).TryAdd(service, true))
            {
                var queue = Volatile.Read(ref _lazyServicesPendingInit)
                    ?? Interlocked.CompareExchange(ref _lazyServicesPendingInit, new ConcurrentQueue<INexusService>(), null)
                    ?? _lazyServicesPendingInit;
                queue.Enqueue(service);
                LazyServiceResolvedCallback?.Invoke(service);
            }
        }

        public IEnumerable<object> GetActiveSingletons()
        {
            lock (_singletonLock)
                return _resolvedSingletonOrder == null ? new List<object>() : new List<object>(_resolvedSingletonOrder);
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

        /// <summary>
        /// Registers a zero-reflection constructor factory for <typeparamref name="T"/> — emitted
        /// by the Nexus AOT code generator as <c>di =&gt; new T(di.Resolve&lt;A&gt;(), di.Resolve&lt;B&gt;())</c>.
        /// When present, <see cref="Injector.CreateInstance"/> uses it instead of reflection, so
        /// readonly/immutable constructor state works and IL2CPP never hits
        /// <c>ConstructorInfo.Invoke</c>. Field/property/method injection still runs afterwards.
        /// Explicit constructor overrides (fluent <c>WithParameter</c>) take precedence over a
        /// registered factory: if the caller supplied explicit constructor arguments,
        /// <see cref="Injector.CreateInstance"/> falls back to the reflection-with-overrides
        /// path instead of invoking the generated lambda.
        /// </summary>
        public static void RegisterConstructorFactory<T>(Func<NexusDI, T> factory) where T : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            s_constructorFactories[typeof(T)] = di => factory(di);
            Interlocked.Increment(ref s_cacheGeneration);
        }

        /// <summary>True when a generated (AOT) constructor factory is registered for <paramref name="type"/>.</summary>
        public static bool HasRegisteredConstructorFactory(Type type) => type != null && s_constructorFactories.ContainsKey(type);

        /// <summary>Types whose generated injector was found stale at runtime and replaced by reflection (regenerate the binder).</summary>
        public static Type[] GetStaleGeneratedInjectorTypes()
        {
            var stale = new List<Type>();
            foreach (var pair in s_checkedInjectors)
                if (!pair.Value) stale.Add(pair.Key);
            return stale.ToArray();
        }

        public static void RegisterInjector<T>(Action<T, NexusDI> injector) where T : class
        {
            s_customInjectors[typeof(T)] = (instance, di) => injector((T)instance, di);
            Interlocked.Increment(ref s_cacheGeneration);
        }

        public static void RegisterClearer<T>(Action<T> clearer) where T : class
        {
            s_customClearers[typeof(T)] = instance => clearer((T)instance);
            Interlocked.Increment(ref s_cacheGeneration);
        }

        // ─── Disposal ───
        /// <summary>
        /// Caller holds <c>_singletonLock</c>. The list is creation-ordered and unique by reference
        /// (an instance bound under several keys is tracked once), so teardown needs no extra set.
        /// </summary>
        private void TrackResolvedSingleton(object instance)
        {
            var order = _resolvedSingletonOrder ??= new List<object>(4);
            for (int i = 0; i < order.Count; i++)
                if (ReferenceEquals(order[i], instance)) return;
            order.Add(instance);
        }

        private bool TryBeginDispose(out List<object> singletonsInCreationOrder)
        {
            lock (_disposeLock)
            {
                if (Interlocked.Exchange(ref _disposeState, 1) != 0)
                {
                    singletonsInCreationOrder = null;
                    return false;
                }
                lock (_singletonLock)
                {
                    // Snapshot the CREATION-ORDERED list so teardown runs in reverse creation
                    // order (a dependency created before its consumers is disposed after them).
                    // The HashSet stays for O(1) membership/count/editor snapshots.
                    // Hand the creation-ordered list over instead of copying it; a disposed container never tracks again.
                    singletonsInCreationOrder = _resolvedSingletonOrder;
                    _resolvedSingletonOrder = null;
                }
                _bindings.Clear();
                Volatile.Read(ref _namedBindings)?.Clear();
                Volatile.Write(ref _fastCache, null);
            }
            return true;
        }

        public void Dispose()
        {
            if (!TryBeginDispose(out var singletonsCopy)) return;
            if (singletonsCopy == null || singletonsCopy.Count == 0) return; // nothing owned: no teardown bookkeeping to allocate

            
            List<IAsyncDisposable> asyncDisposables = null; // only pure IAsyncDisposable singletons need it
            // Reverse creation order: dependencies created first are disposed last, so
            // [Deconstruct]/Dispose on a consumer can still touch its dependencies.
            for (int i = singletonsCopy.Count - 1; i >= 0; i--)
            {
                var instance = singletonsCopy[i];
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
                        // An IAsyncDisposable that ALSO implements IDisposable is
                        // disposed synchronously here so the sync Dispose() path (used by
                        // Root.OnDestroy / Context.Dispose) tears it down deterministically
                        // instead of leaking it to a fire-and-forget background task that may
                        // never complete before the process exits. Only pure-IAsyncDisposable
                        // singletons (no IDisposable) fall through to the ordered background
                        // chain — those genuinely cannot be torn down without awaiting.
                        if (instance is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                        else
                        {
                            // Never block the calling thread on async
                            // teardown, but dispose async singletons in ONE ordered background
                            // chain instead of one fire-and-forget task per instance. The old
                            // per-instance `_ =` fired them in parallel with no ordering, so
                            // registration-dependency order was lost. Collecting them and
                            // awaiting sequentially on the thread pool with ConfigureAwait(false)
                            // preserves order AND prevents continuations from re-capturing the
                            // Unity SynchronizationContext of the (disposing) caller.
                            (asyncDisposables ??= new List<IAsyncDisposable>()).Add(asyncDisposable);
                        }
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
            if (asyncDisposables != null)
                _backgroundDisposeTask = DisposeAllAsyncInBackground(asyncDisposables);
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

        /// <summary>
        /// Blocks the calling thread up to <paramref name="timeout"/> waiting for the
        /// background async-dispose task scheduled by <see cref="Dispose()"/>, if any.
        /// Returns true if the background dispose completed within the timeout, false
        /// if it is still running or threw.
        /// </summary>
        public bool WaitForBackgroundDispose(TimeSpan timeout)
        {
            var t = _backgroundDisposeTask;
            if (t == null) return true;
            try
            {
                return t.Wait(timeout);
            }
            catch (AggregateException ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Waiting for background dispose failed: {ex.Flatten().InnerException?.Message}");
                return false;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Waiting for background dispose failed: {ex.Message}");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!TryBeginDispose(out var singletonsCopy)) return;
            if (singletonsCopy == null || singletonsCopy.Count == 0) return; // nothing owned: no teardown bookkeeping to allocate


            // Reverse creation order (see Dispose).
            for (int i = singletonsCopy.Count - 1; i >= 0; i--)
            {
                var instance = singletonsCopy[i];
                // unique by construction (TrackResolvedSingleton)
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
        }

        /// <summary>
        /// User-defined interface filter shared by the fluent <c>AsImplementedInterfaces</c> and
        /// <see cref="ContextBuilder.BindInterfacesAndSelfTo{TImplementation}(bool)"/> — one cache,
        /// one predicate, so the two surfaces can never disagree about which interfaces to bind.
        /// </summary>
        internal static Type[] GetUserDefinedInterfaces(Type type)
        {
            return s_userDefinedInterfacesCache.GetOrAdd(type, static t =>
            {
                var result = new List<Type>();
                var allInterfaces = t.GetInterfaces();
                for (int i = 0; i < allInterfaces.Length; i++)
                {
                    var iface = allInterfaces[i];
                    if (iface == typeof(IDisposable) || iface == typeof(IAsyncDisposable))
                        continue;
                    if (iface == typeof(IStartable) || iface == typeof(IAsyncStartable) || iface == typeof(IStoppable) || iface == typeof(IAsyncStoppable))
                        continue;
                    if (iface == typeof(INexusService) || iface == typeof(IReactiveModel) || iface == typeof(IContextLifecycle) || iface == typeof(IPostContextLifecycle))
                        continue;
                    if (iface.Namespace != null && (iface.Namespace.StartsWith("System") || iface.Namespace.StartsWith("UnityEngine")))
                        continue;
                    result.Add(iface);
                }
                return result.ToArray();
            });
        }

        /// <summary>
        /// Drops the derived caches (reflection metadata, interface scans, the per-type check of generated
        /// injectors) and bumps the cache generation. Generated registrations (constructor factories,
        /// injectors, clearers) are code, not caches: they stay. <see cref="NexusRuntime.Reset"/> runs this
        /// at <c>SubsystemRegistration</c>, in unspecified order relative to the generated binder's own
        /// <c>SubsystemRegistration</c> method; until 2026-10-15 it also cleared the registrations, so a
        /// binder that happened to run first was silently replaced by reflection in the player.
        /// </summary>
        public static void ClearCaches()
        {
            s_checkedInjectors.Clear();
            s_userDefinedInterfacesCache.Clear();
            MetadataCache.ClearAll();
            Interlocked.Increment(ref s_cacheGeneration);
        }

        /// <summary>
        /// Removes every generated registration (constructor factories, injectors, clearers) and the
        /// derived caches, so injection runs by reflection until a binder registers again. For tests and
        /// benchmarks that compare the reflection path; a game never needs it.
        /// </summary>
        public static void ClearGeneratedRegistrations()
        {
            s_customInjectors.Clear();
            s_customClearers.Clear();
            s_constructorFactories.Clear();
            ClearCaches();
        }

        /// <summary>Generated registrations currently live (constructor factories plus injectors); zero means injection runs by reflection.</summary>
        public static int GeneratedRegistrationCount => s_constructorFactories.Count + s_customInjectors.Count;
    }
}
