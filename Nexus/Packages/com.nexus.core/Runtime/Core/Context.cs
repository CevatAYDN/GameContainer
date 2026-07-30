using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Factory that owns Context sub-module wiring. Deepens the Context module by
    /// concentrating construction logic in one place. Tests inject mocks through IContext.
    /// SignalBus and HybridQueue are created inside the Context constructor (they need
    /// a reference to the Context itself), so the factory creates only NexusDI and
    /// CommandPoolManager before delegating to the internal constructor.
    /// </summary>
    [Preserve]
    public static class ContextFactory
    {
        /// <summary>Creates a fully-wired Context with all sub-modules initialized.</summary>
        public static Context Create(Context parent = null, ContextData contextData = null)
        {
            var container = new NexusDI(parent?.Container);
            container.BindInstance(container);

            var poolSize = contextData?.CommandPoolInitialSize ?? 4;
            var poolMax = contextData?.CommandPoolMaxSize ?? 64;
            var poolManager = new CommandPoolManager(container, poolSize, poolMax);
            container.BindInstance(poolManager);

            // Internal constructor creates SignalBus and HybridQueue (they need 'this')
            var context = new Context(parent, contextData, container, poolManager);
            container.BindInstance<IContext>(context);

            if (contextData?.EnableStrictInjection == true)
                container.StrictInjection = true;

            NexusRuntime.RegisterContext(context);
            return context;
        }
    }

    [Preserve]
    public class Context : IContext
    {
        private readonly Context _parent;
        private readonly ContextData _contextData;
        private readonly CancellationTokenSource _cts = new();
        private readonly ViewBinder _viewBinder;
        private readonly List<(INexusPlugin plugin, PluginContext context)> _plugins = new();
        private volatile List<(INexusPlugin plugin, PluginContext context)> _pluginsReadOnlyCopy = new();
        private readonly object _pluginsLock = new();
        private int _interceptorsCount;
        private ContextBuilder _builder;
        private volatile bool _disposed;

        private IContextLifecycle[] _configuredLifecycles = Array.Empty<IContextLifecycle>();

        internal IReadOnlyList<IContextLifecycle> ConfiguredLifecycles => _configuredLifecycles;
        public IReadOnlyList<(INexusPlugin plugin, PluginContext context)> PluginsReadOnlyCopy => _pluginsReadOnlyCopy;

        public bool HasInterceptors => System.Threading.Volatile.Read(ref _interceptorsCount) > 0;
        public void IncrementInterceptorsCount() => System.Threading.Interlocked.Increment(ref _interceptorsCount);
        public void DecrementInterceptorsCount() => System.Threading.Interlocked.Decrement(ref _interceptorsCount);

        private class ScannedHandlerData
        {
            public Type Type { get; }
            public List<SignalHandlerAttribute> Handlers { get; } = new();
            public CompositeSignalHandlerAttribute CompositeHandler { get; set; }
            public ScannedHandlerData(Type type) { Type = type; }
        }

        private static readonly Dictionary<Assembly, List<ScannedHandlerData>> s_assemblyScanCache = new();
        private static readonly object s_scanLock = new();

        public IReadOnlyList<(INexusPlugin plugin, PluginContext context)> Plugins => _plugins;
        public IReadOnlyList<(INexusPlugin plugin, PluginContext context)> GetPluginsSnapshot() => _pluginsReadOnlyCopy;

        public ISignalBus SignalBus { get; }
        public CancellationToken LifetimeToken => _cts.Token;
        public IContext Parent => _parent;
        public NexusDI Container { get; }
        public CommandPoolManager PoolManager { get; }
        public HybridQueue HybridQueue { get; }
        public string ScopeTag => _contextData?.ScopeTag;
        public ContextData ContextData => _contextData;
        public SignalBus SignalBusInternal { get; }

        /// <summary>
        /// Primary constructor used by ContextFactory. Takes NexusDI and PoolManager
        /// (created by the factory), then creates SignalBus and HybridQueue internally
        /// since they need a reference to this Context.
        /// </summary>
        internal Context(Context parent, ContextData contextData, NexusDI container,
            CommandPoolManager poolManager)
        {
            _parent = parent;
            _contextData = contextData;
            Container = container;
            PoolManager = poolManager;

            var bus = new SignalBus(Container, PoolManager, this);
            SignalBus = bus;
            SignalBusInternal = bus;
            Container.BindInstance<ISignalBus>(bus);
            Container.BindInstance(bus);

            HybridQueue = new HybridQueue(bus);
            Container.BindInstance(HybridQueue);

            _viewBinder = new ViewBinder(this, Container);
        }

        /// <summary>
        /// Backward-compatible constructor. Kept for existing callers (tests, harness).
        /// New code should prefer <see cref="ContextFactory.Create"/>.
        /// </summary>
        public Context(Context parent = null, ContextData contextData = null)
        {
            _parent = parent;
            _contextData = contextData;
            Container = new NexusDI(parent?.Container);
            Container.BindInstance(Container);
            Container.BindInstance<IContext>(this);

            var poolSize = contextData?.CommandPoolInitialSize ?? 4;
            var poolMax = contextData?.CommandPoolMaxSize ?? 64;
            PoolManager = new CommandPoolManager(Container, poolSize, poolMax);
            Container.BindInstance(PoolManager);

            var bus = new SignalBus(Container, PoolManager, this);
            SignalBus = bus;
            SignalBusInternal = bus;
            Container.BindInstance<ISignalBus>(bus);
            Container.BindInstance(bus);

            HybridQueue = new HybridQueue(bus);
            Container.BindInstance(HybridQueue);

            _viewBinder = new ViewBinder(this, Container);
            Container.BindInstance(_viewBinder);

            if (contextData?.EnableStrictInjection == true)
                Container.StrictInjection = true;

            NexusRuntime.RegisterContext(this);
        }

        public void Configure(IContextLifecycle[] lifecycles = null)
        {
            _builder = new ContextBuilder(Container, SignalBusInternal);

            var allLifecycles = new List<IContextLifecycle>();
            if (lifecycles != null) allLifecycles.AddRange(lifecycles);

            if (_contextData == null || _contextData.EnableAutoDiscovery)
            {
                if (allLifecycles.Count == 0 && !Container.IsRegistered(typeof(IContextLifecycle)))
                {
                    var lifecycleType = FindLifecycleTypeByConvention();
                    if (lifecycleType != null)
                    {
                        try
                        {
                            var instance = Activator.CreateInstance(lifecycleType) as IContextLifecycle;
                            if (instance != null)
                            {
                                Container.BindInstance<IContextLifecycle>(instance);
                                allLifecycles.Add(instance);
                            }
                        }
                        catch (Exception ex)
                        {
                            NexusRuntime.Logger?.LogError($"[Nexus] Failed to instantiate lifecycle class '{lifecycleType.Name}' by convention: {ex.Message}");
                        }
                    }
                }

                if (allLifecycles.Count == 0 && !Container.IsRegistered(typeof(IContextLifecycle)))
                    NexusRuntime.Logger?.LogWarning("[Nexus] No IContextLifecycle was discovered or registered. The context can still run, but setup may be incomplete.");
            }

            if (allLifecycles.Count == 0 && Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Container.Resolve<IContextLifecycle>();
                allLifecycles.Add(lifecycle);
            }

            _configuredLifecycles = allLifecycles.ToArray();

            foreach (var lifecycle in allLifecycles)
                lifecycle.OnConfigure(_builder);

            ScanAssembliesAndRegister(_builder);

#if UNITY_EDITOR
            var issues = _builder.Validate();
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    var message = $"[Nexus] DI Validation: {issue.Message}";
                    if (NexusRuntime.Logger != null)
                        NexusRuntime.Logger.LogError(message);
                    else
                        UnityEngine.Debug.LogError(message);
                }
            }
#endif
        }

        internal async ValueTask InitializeReactiveModelsAsync(CancellationToken ct)
        {
            if (_builder != null) await _builder.InitializeReactiveModelsAsync(SignalBus, ct);
        }

        internal async ValueTask InitializeServicesAsync(CancellationToken ct)
        {
            if (_builder != null) await _builder.InitializeServicesAsync(ct);
        }

        internal async ValueTask InitializeLifecycleAsync(IReadOnlyList<IContextLifecycle> lifecycles, CancellationToken ct)
        {
            await InitializeReactiveModelsAsync(ct);
            await InitializeServicesAsync(ct);
            Container.ReInjectAll();

            if (lifecycles != null)
            {
                for (int i = 0; i < lifecycles.Count; i++)
                    await lifecycles[i].OnInitializeAsync(ct);
            }

            Container.ReInjectAll();
            await InitializeLazyServicesAsync(ct);

            if (lifecycles != null)
            {
                for (int i = 0; i < lifecycles.Count; i++)
                    await lifecycles[i].OnStartAsync(ct);
            }
        }

        internal async ValueTask InitializeLazyServicesAsync(CancellationToken ct)
        {
            while (Container._lazyServicesPendingInit.TryDequeue(out var service))
            {
                if (ct.IsCancellationRequested) break;
                await service.InitializeAsync(ct);
            }
        }

        private Type FindLifecycleTypeByConvention()
        {
            if (string.IsNullOrEmpty(ScopeTag)) return null;
            var assemblies = (_contextData?.AssemblyScopes?.Length > 0)
                ? LoadScopedAssemblies(logWarnings: false)
                : GetDefaultScanAssemblies();
            return FindLifecycleTypeInAssemblies(assemblies, ScopeTag);
        }

        private static Type FindLifecycleTypeInAssemblies(List<Assembly> assemblies, string scopeTag)
        {
            string targetName1 = $"{scopeTag}Lifecycle";
            string targetName2 = $"{scopeTag}ContextLifecycle";
            foreach (var assembly in assemblies)
            {
                foreach (var type in GetTypesSafely(assembly))
                {
                    if (type.IsClass && !type.IsAbstract && typeof(IContextLifecycle).IsAssignableFrom(type))
                    {
                        if (string.Equals(type.Name, targetName1, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type.Name, targetName2, StringComparison.OrdinalIgnoreCase))
                            return type;
                    }
                }
            }
            return null;
        }

        private void ScanAssembliesAndRegister(ContextBuilder builder)
        {
            var assemblies = (_contextData?.AssemblyScopes?.Length > 0)
                ? LoadScopedAssemblies(logWarnings: true)
                : GetDefaultScanAssemblies();

            foreach (var assembly in assemblies)
            {
                List<ScannedHandlerData> cachedData;
                lock (s_scanLock)
                {
                    if (!s_assemblyScanCache.TryGetValue(assembly, out cachedData))
                    {
                        cachedData = new List<ScannedHandlerData>();
                        foreach (var type in GetTypesSafely(assembly))
                        {
                            if (type.IsClass && !type.IsAbstract)
                            {
                                ScannedHandlerData data = null;
                                var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                                foreach (var attr in handlerAttrs)
                                {
                                    if (data == null) data = new ScannedHandlerData(type);
                                    data.Handlers.Add(attr);
                                }
                                var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                                if (compositeAttr != null)
                                {
                                    if (data == null) data = new ScannedHandlerData(type);
                                    data.CompositeHandler = compositeAttr;
                                }
                                if (data != null) cachedData.Add(data);
                            }
                        }
                        s_assemblyScanCache[assembly] = cachedData;
                    }
                }

                for (int i = 0; i < cachedData.Count; i++)
                {
                    var data = cachedData[i];
                    var type = data.Type;

                    bool isSync = typeof(ICommand).IsAssignableFrom(type)
                        || global::Nexus.Core.SignalBus.ImplementsGenericInterface(type, typeof(ICommand<>));
                    bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(type)
                        || global::Nexus.Core.SignalBus.ImplementsGenericInterface(type, typeof(IAsyncCommand<>));
                    bool isCompositeSync = typeof(ICompositeCommand).IsAssignableFrom(type);
                    bool isCompositeAsync = typeof(IAsyncCompositeCommand).IsAssignableFrom(type);

                    if (isSync || isAsync || isCompositeSync || isCompositeAsync)
                    {
                        Container.Bind(type, isSingleton: false);
                        for (int j = 0; j < data.Handlers.Count; j++)
                        {
                            var attr = data.Handlers[j];
                            SignalBusInternal.RegisterCommand(attr.SignalType, type, attr.Mode, attr.Priority, isAsync: isAsync && !isSync);
                        }
                        if (data.CompositeHandler != null)
                        {
                            bool compositeIsAsync = (isCompositeAsync && !isCompositeSync) || (isAsync && !isSync);
                            SignalBusInternal.RegisterCompositeCommand(data.CompositeHandler.SignalTypes, type, data.CompositeHandler.OneShot, data.CompositeHandler.Priority, compositeIsAsync);
                        }
                    }
                    else
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] [SignalHandler] type '{type.FullName}' does not implement ICommand/IAsyncCommand.");
                    }
                }
            }
        }

        private List<Assembly> LoadScopedAssemblies(bool logWarnings)
        {
            var assemblies = new List<Assembly>();
            foreach (var scopeName in _contextData.AssemblyScopes)
            {
                try { var assembly = Assembly.Load(scopeName); if (assembly != null) assemblies.Add(assembly); }
                catch (Exception ex)
                {
                    if (logWarnings) NexusRuntime.Logger?.LogWarning($"[Nexus] Failed to load assembly {scopeName}: {ex.Message}");
                }
            }
            return assemblies;
        }

        private static List<Assembly> GetDefaultScanAssemblies()
        {
            if (s_defaultScanAssemblies != null) return s_defaultScanAssemblies;

            var result = new List<Assembly>();
            var nexusAssembly = typeof(Context).Assembly;
            var nexusAssemblyName = nexusAssembly.GetName().Name;

            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                if (assembly.IsDynamic) continue;
                if (ShouldSkipDefaultScanAssembly(assembly)) continue;

                bool shouldScan = assembly == nexusAssembly || assembly.GetName().Name == "Assembly-CSharp";
                if (!shouldScan)
                {
                    try
                    {
                        foreach (var reference in assembly.GetReferencedAssemblies())
                        {
                            if (reference.Name == nexusAssemblyName) { shouldScan = true; break; }
                        }
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogWarning($"[Nexus] Failed to check assembly references for '{assembly.GetName().Name}': {ex.Message}");
                        shouldScan = false;
                    }
                }
                if (shouldScan) result.Add(assembly);
            }

            if (result.Count == 0) result.Add(nexusAssembly);
            s_defaultScanAssemblies = result;
            return result;
        }

        private static List<Assembly> s_defaultScanAssemblies;
        internal static void ClearDefaultScanAssembliesCache() => s_defaultScanAssemblies = null;

        private static bool ShouldSkipDefaultScanAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name)) return true;
            var lowerName = name.ToLowerInvariant();
            return lowerName.Contains(".tests") || lowerName.EndsWith(".editor");
        }

        private static IEnumerable<Type> GetTypesSafely(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                var types = new List<Type>();
                foreach (var type in ex.Types) { if (type != null) types.Add(type); }
                return types;
            }
        }

        public T Resolve<T>() where T : class => Container.Resolve<T>();
        public T TryResolve<T>() where T : class => Container.TryResolve<T>();
        public void RegisterView(IView view) => _viewBinder.RegisterView(view);
        public void UnregisterView(IView view) => _viewBinder.UnregisterView(view);

        public void RegisterPlugin(INexusPlugin plugin)
        {
            if (plugin == null) return;
            PluginContext pluginContext = null;
            lock (_pluginsLock)
            {
                foreach (var p in _plugins) { if (p.plugin == plugin) return; }
                pluginContext = new PluginContext(plugin, this);
                _plugins.Add((plugin, pluginContext));
                _pluginsReadOnlyCopy = new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
            }
            try { plugin.OnPluginRegistered(pluginContext); }
            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
        }

        public void RemovePlugin(INexusPlugin plugin)
        {
            if (plugin == null) return;
            PluginContext removedContext = null;
            lock (_pluginsLock)
            {
                int index = -1;
                for (int i = 0; i < _plugins.Count; i++)
                {
                    if (_plugins[i].plugin == plugin) { index = i; break; }
                }
                if (index != -1)
                {
                    var p = _plugins[index];
                    _plugins.RemoveAt(index);
                    _pluginsReadOnlyCopy = new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
                    removedContext = p.context;
                }
            }
            if (removedContext == null) return;
            try { removedContext.Clear(); plugin.OnPluginRemoved(); }
            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();

            if (_configuredLifecycles.Length > 0)
            {
                for (int i = _configuredLifecycles.Length - 1; i >= 0; i--)
                {
                    try { _configuredLifecycles[i].OnDispose(); }
                    catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                }
            }
            else if (Container.IsRegistered(typeof(IContextLifecycle)))
            {
                try { Container.TryResolve<IContextLifecycle>()?.OnDispose(); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            if (_builder != null)
            {
                var serviceTypes = _builder.ServiceTypes;
                for (int i = serviceTypes.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Container.TryGetExistingInstance(serviceTypes[i], out var existing) && existing is INexusService service)
                            service.OnDispose();
                    }
                    catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                }
            }

            _viewBinder.Dispose();

            List<(INexusPlugin plugin, PluginContext context)> pluginSnapshot;
            lock (_pluginsLock)
            {
                pluginSnapshot = new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
                _plugins.Clear();
                _pluginsReadOnlyCopy = new List<(INexusPlugin plugin, PluginContext context)>();
            }

            for (int i = pluginSnapshot.Count - 1; i >= 0; i--)
            {
                try { pluginSnapshot[i].context.Clear(); pluginSnapshot[i].plugin.OnPluginRemoved(); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            NexusRuntime.UnregisterContext(this);
            SignalBusInternal.Dispose();
            HybridQueue.Clear();
            PoolManager.Clear();
            Container.Dispose();
            _cts.Dispose();
        }

        public static void ClearAssemblyScanCache()
        {
            lock (s_scanLock) { s_assemblyScanCache.Clear(); }
        }
    }
}
