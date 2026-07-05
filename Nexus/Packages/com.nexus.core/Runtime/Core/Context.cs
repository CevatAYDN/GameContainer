using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.Core
{
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

        public List<(INexusPlugin plugin, PluginContext context)> PluginsReadOnlyCopy => _pluginsReadOnlyCopy;
        public bool HasInterceptors => _interceptorsCount > 0;
        public void IncrementInterceptorsCount() => System.Threading.Interlocked.Increment(ref _interceptorsCount);
        public void DecrementInterceptorsCount() => System.Threading.Interlocked.Decrement(ref _interceptorsCount);

        private class ScannedHandlerData
        {
            public Type Type { get; }
            public List<SignalHandlerAttribute> Handlers { get; } = new();
            public CompositeSignalHandlerAttribute CompositeHandler { get; set; }

            public ScannedHandlerData(Type type)
            {
                Type = type;
            }
        }

        private static readonly Dictionary<Assembly, List<ScannedHandlerData>> s_assemblyScanCache = new();
        private static readonly object s_scanLock = new();
        
        public IReadOnlyList<(INexusPlugin plugin, PluginContext context)> Plugins => _plugins;
        
        /// <summary>
        /// Returns a snapshot of the plugins list to allow safe iteration during dispatch
        /// when plugins may register/unregister other plugins via interceptors.
        /// </summary>
        public List<(INexusPlugin plugin, PluginContext context)> GetPluginsSnapshot()
        {
            lock (_pluginsLock)
            {
                return new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
            }
        }
        
        public ISignalBus SignalBus { get; }
        public CancellationToken LifetimeToken => _cts.Token;
        public IContext Parent => _parent;
        
        public NexusDI Container { get; }
        public CommandPoolManager PoolManager { get; }
        public HybridQueue HybridQueue { get; }
        public string ScopeTag => _contextData != null ? _contextData.ScopeTag : null;
        public ContextData ContextData => _contextData;
        public SignalBus SignalBusInternal { get; }

        public Context(Context parent = null, ContextData contextData = null)
        {
            _parent = parent;
            _contextData = contextData;
            
            Container = new NexusDI(parent?.Container);
            
            Container.BindInstance(Container);
            Container.BindInstance<IContext>(this);

            var poolSize = contextData != null ? contextData.CommandPoolInitialSize : 4;
            var poolMax = contextData != null ? contextData.CommandPoolMaxSize : 64;
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
            
            NexusRuntime.RegisterContext(this);
        }

        public void Configure(IContextLifecycle[] lifecycles = null)
        {
            _builder = new ContextBuilder(Container, SignalBusInternal);

            var allLifecycles = new List<IContextLifecycle>();
            if (lifecycles != null)
            {
                allLifecycles.AddRange(lifecycles);
            }

            // Auto-discover lifecycle class only when enabled in data and not explicitly provided
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
                    var logger = TryResolve<ILoggerService>();
                    logger?.LogError($"[Nexus] Failed to instantiate lifecycle class '{lifecycleType.Name}' by convention: {ex.Message}");
                }
                    }
                }
            }

            // Fallback: DI-registered lifecycle (for backward compatibility)
            if (allLifecycles.Count == 0 && Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Container.Resolve<IContextLifecycle>();
                allLifecycles.Add(lifecycle);
            }

            // Call OnConfigure for all registered lifecycles
            foreach (var lifecycle in allLifecycles)
            {
                lifecycle.OnConfigure(_builder);
            }

            // Scan and register attributes
            ScanAssembliesAndRegister(_builder);
        }

        internal async ValueTask InitializeReactiveModelsAsync(CancellationToken ct)
        {
            if (_builder != null)
                await _builder.InitializeReactiveModelsAsync(SignalBus, ct);
        }

        internal async ValueTask InitializeServicesAsync(CancellationToken ct)
        {
            if (_builder != null)
                await _builder.InitializeServicesAsync(ct);
        }

        private Type FindLifecycleTypeByConvention()
        {
            if (string.IsNullOrEmpty(ScopeTag)) return null;

            if (_contextData == null || _contextData.AssemblyScopes == null || _contextData.AssemblyScopes.Length == 0)
            {
                return FindLifecycleTypeInAssemblies(GetDefaultScanAssemblies(), ScopeTag);
            }

            var assemblies = LoadScopedAssemblies(logWarnings: false);
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
                        {
                            return type;
                        }
                    }
                }
            }
            return null;
        }

        private void ScanAssembliesAndRegister(ContextBuilder builder)
        {
            var assemblies = _contextData == null || _contextData.AssemblyScopes == null || _contextData.AssemblyScopes.Length == 0
                ? GetDefaultScanAssemblies()
                : LoadScopedAssemblies(logWarnings: true);

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

                                if (data != null)
                                {
                                    cachedData.Add(data);
                                }
                            }
                        }
                        s_assemblyScanCache[assembly] = cachedData;
                    }
                }

                for (int i = 0; i < cachedData.Count; i++)
                {
                    var data = cachedData[i];
                    var type = data.Type;

                    for (int j = 0; j < data.Handlers.Count; j++)
                    {
                        var attr = data.Handlers[j];
                        if (typeof(ICommand).IsAssignableFrom(type))
                        {
                            Container.Bind(type, isSingleton: false);
                            SignalBusInternal.RegisterCommand(attr.SignalType, type, attr.Mode, attr.Priority, isAsync: false);
                        }
                        else if (typeof(IAsyncCommand).IsAssignableFrom(type))
                        {
                            Container.Bind(type, isSingleton: false);
                            SignalBusInternal.RegisterCommand(attr.SignalType, type, attr.Mode, attr.Priority, isAsync: true);
                        }
                    }

                    if (data.CompositeHandler != null)
                    {
                        bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(type);
                        SignalBusInternal.RegisterCompositeCommand(data.CompositeHandler.SignalTypes, type, data.CompositeHandler.OneShot, data.CompositeHandler.Priority, isAsync);
                    }
                }
            }
        }

        private List<Assembly> LoadScopedAssemblies(bool logWarnings)
        {
            var assemblies = new List<Assembly>();
            foreach (var scopeName in _contextData.AssemblyScopes)
            {
                try
                {
                    var assembly = Assembly.Load(scopeName);
                    if (assembly != null) assemblies.Add(assembly);
                }
                catch (Exception ex)
                {
                    if (logWarnings)
                    {
                        TryResolve<ILoggerService>()?.LogWarning($"[Nexus] Failed to load assembly {scopeName}: {ex.Message}");
                    }
                }
            }
            return assemblies;
        }

        private static List<Assembly> GetDefaultScanAssemblies()
        {
            // Cache the result since assemblies don't change at runtime (only on domain reload)
            if (s_defaultScanAssemblies != null)
                return s_defaultScanAssemblies;

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
                            if (reference.Name == nexusAssemblyName)
                            {
                                shouldScan = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogWarning($"[Nexus] Failed to check assembly references for '{assembly.GetName().Name}': {ex.Message}");
                        shouldScan = false;
                    }
                }

                if (shouldScan)
                {
                    result.Add(assembly);
                }
            }

            if (result.Count == 0)
            {
                result.Add(nexusAssembly);
            }

            s_defaultScanAssemblies = result;
            return result;
        }

        private static List<Assembly> s_defaultScanAssemblies;

        private static bool ShouldSkipDefaultScanAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name)) return true;

            var lowerName = name.ToLowerInvariant();
            return lowerName.Contains(".tests") || lowerName.EndsWith(".editor");
        }

        private static IEnumerable<Type> GetTypesSafely(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var types = new List<Type>();
                foreach (var type in ex.Types)
                {
                    if (type != null) types.Add(type);
                }
                return types;
            }
        }

        public T Resolve<T>() where T : class
        {
            return Container.Resolve<T>();
        }

        public T TryResolve<T>() where T : class
        {
            return Container.TryResolve<T>();
        }

        public void RegisterView(IView view)
        {
            _viewBinder.RegisterView(view);
        }

        public void UnregisterView(IView view)
        {
            _viewBinder.UnregisterView(view);
        }

        public void RegisterPlugin(INexusPlugin plugin)
        {
            if (plugin == null) return;
            lock (_pluginsLock)
            {
                foreach (var p in _plugins)
                {
                    if (p.plugin == plugin) return;
                }

                var pluginContext = new PluginContext(plugin, this);
                _plugins.Add((plugin, pluginContext));
                _pluginsReadOnlyCopy = new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
                plugin.OnPluginRegistered(pluginContext);
            }
        }

        public void RemovePlugin(INexusPlugin plugin)
        {
            if (plugin == null) return;
            lock (_pluginsLock)
            {
                int index = -1;
                for (int i = 0; i < _plugins.Count; i++)
                {
                    if (_plugins[i].plugin == plugin)
                    {
                        index = i;
                        break;
                    }
                }

                if (index != -1)
                {
                    var p = _plugins[index];
                    _plugins.RemoveAt(index);
                    _pluginsReadOnlyCopy = new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
                    p.context.Clear();
                    p.plugin.OnPluginRemoved();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();

            if (Container.IsRegistered(typeof(IContextLifecycle)))
            {
                try
                {
                    var lifecycle = Container.Resolve<IContextLifecycle>();
                    lifecycle.OnDispose();
                }
                catch (Exception ex)
                {
                    TryResolve<ILoggerService>()?.LogException(ex);
                }
            }

            // Dispose all registered services in reverse order
            if (_builder != null)
            {
                var serviceTypes = _builder.ServiceTypes;
                for (int i = serviceTypes.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var service = Container.Resolve(serviceTypes[i]) as INexusService;
                        service?.OnDispose();
                    }
                    catch (Exception ex)
                    {
                        TryResolve<ILoggerService>()?.LogException(ex);
                    }
                }
            }

            _viewBinder.Dispose();

            // Clean up plugins in reverse order under lock snapshot
            List<(INexusPlugin plugin, PluginContext context)> pluginSnapshot;
            lock (_pluginsLock)
            {
                pluginSnapshot = new List<(INexusPlugin plugin, PluginContext context)>(_plugins);
                _plugins.Clear();
                _pluginsReadOnlyCopy = new List<(INexusPlugin plugin, PluginContext context)>();
            }

            for (int i = pluginSnapshot.Count - 1; i >= 0; i--)
            {
                try
                {
                    pluginSnapshot[i].context.Clear();
                    pluginSnapshot[i].plugin.OnPluginRemoved();
                }
                catch (Exception ex)
                {
                    TryResolve<ILoggerService>()?.LogException(ex);
                }
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
            lock (s_scanLock)
            {
                s_assemblyScanCache.Clear();
            }
        }
    }
}
