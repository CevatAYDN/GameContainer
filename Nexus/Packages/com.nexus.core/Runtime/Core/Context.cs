using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Lifecycle;
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
        /// <summary>
        /// Creates a fully-wired Context with all sub-modules initialized.
        /// Single construction path: the internal Context constructor owns ALL module
        /// wiring (DI, command pools, signal bus, hybrid queue, view binder), so the
        /// factory and the backward-compatible public constructor can never drift.
        /// </summary>
        public static Context Create(Context parent = null, ContextData contextData = null)
        {
            return new Context(parent, contextData, null, null);
        }
    }

    [Preserve]
    public class Context : IContext, IAsyncDisposable
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

        private readonly ContextLifecycleOrchestrator _orchestrator = new();
        private IContextLifecycle[] _configuredLifecycles = Array.Empty<IContextLifecycle>();
        private List<IPostContextLifecycle> _postContextLifecycles = new();

        internal IReadOnlyList<IContextLifecycle> ConfiguredLifecycles => _configuredLifecycles;
        internal IReadOnlyList<IPostContextLifecycle> PostContextLifecycles => _postContextLifecycles;
        internal bool HasPostContextLifecycle => _postContextLifecycles.Count > 0;
        internal bool IsConfigured => _builder != null;
        internal ContextBuilder Builder => _builder;

        /// <summary>
        /// Returns the context's builder, creating it lazily when the context was never
        /// <see cref="Configure"/>'d (the test-harness path). Bind calls made through the
        /// harness route through the production builder, so the test surface can never
        /// drift from the runtime's registration surface.
        /// </summary>
        internal ContextBuilder GetOrCreateBuilder()
        {
            if (_builder == null) _builder = new ContextBuilder(Container, SignalBusInternal);
            return _builder;
        }
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
        /// Single construction path for every Context. ContextFactory.Create and the
        /// backward-compatible public constructor both funnel through here, so module
        /// wiring can never drift between construction routes. Accepts optional
        /// pre-built NexusDI + CommandPoolManager (null = build the standard modules).
        /// </summary>
        internal Context(Context parent, ContextData contextData, NexusDI container,
            CommandPoolManager poolManager)
        {
            _parent = parent;
            _contextData = contextData;

            if (container == null || poolManager == null)
            {
                (container, poolManager) = CreateModules(parent, contextData);
            }
            Container = container;
            Container.BindInstance(container);
            PoolManager = poolManager;
            Container.BindInstance(poolManager);

            // Sub-modules that need a reference to this Context are wired here — the
            // single wiring point for every construction path. Assignments must stay in
            // the constructor (readonly members cannot be assigned from a helper method).
            var bus = new SignalBus(Container, PoolManager, this);
            SignalBus = bus;
            SignalBusInternal = bus;
            Container.BindInstance<ISignalBus>(bus);
            Container.BindInstance(bus);

            HybridQueue = new HybridQueue(bus);
            Container.BindInstance(HybridQueue);

            _viewBinder = new ViewBinder(this, Container);
            Container.BindInstance(_viewBinder);

            Container.BindInstance<IContext>(this);
            if (contextData?.EnableStrictInjection == true)
                Container.StrictInjection = true;

            NexusRuntime.RegisterContext(this);
        }

        /// <summary>
        /// Backward-compatible constructor. Kept for existing callers (tests, harness).
        /// Thin forwarder into the single internal wiring path; new code should prefer
        /// <see cref="ContextFactory.Create"/>.
        /// </summary>
        public Context(Context parent = null, ContextData contextData = null)
            : this(parent, contextData, null, null)
        {
        }

        /// <summary>Builds the standard DI container + command pool manager pair.</summary>
        private static (NexusDI container, CommandPoolManager poolManager) CreateModules(
            Context parent, ContextData contextData)
        {
            var container = new NexusDI(parent?.Container);
            var poolSize = contextData?.CommandPoolInitialSize ?? 4;
            var poolMax = contextData?.CommandPoolMaxSize ?? 64;
            var poolManager = new CommandPoolManager(container, poolSize, poolMax);
            return (container, poolManager);
        }

        public void Configure(IContextLifecycle[] lifecycles = null)
        {
            if (_disposed) return;

            // Guard against double-Configure: if _builder already exists, merging state from
            // a second call would lose the first builder's reactive model and service type
            // registrations. Prevent accidental double-call by returning early.
            if (_builder != null)
            {
                NexusRuntime.Logger?.LogWarning(
                    $"[Nexus] Context '{ScopeTag}' Configure() called more than once. Subsequent calls are ignored.");
                return;
            }

            _builder = new ContextBuilder(Container, SignalBusInternal);

            // ... rest stays the same until after assemblies scan ...
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

            // Detect IPostContextLifecycle implementations among the registered lifecycles
            _postContextLifecycles.Clear();
            for (int i = 0; i < allLifecycles.Count; i++)
            {
                if (allLifecycles[i] is IPostContextLifecycle postCtx)
                    _postContextLifecycles.Add(postCtx);
            }

            foreach (var lifecycle in allLifecycles)
                lifecycle.OnConfigure(_builder);

            ScanAssembliesAndRegister(_builder);

            // A10 fix: DI validation (missing dependencies, constructor explosion, captive
            // dependencies) must run in ALL build targets, not just the editor — production
            // builds previously ran zero validation and silently left [Inject] fields null
            // with no diagnostic. Validate() only logs (it never throws), and games that do
            // intentional late binding can opt out via ContextBuilder.ValidateOnStartup.
            if (ContextBuilder.ValidateOnStartup)
            {
                var issues = _builder.Validate();
                if (issues.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var issue in issues)
                    {
                        var message = $"[Nexus] DI Validation: {issue.Message}";
                        if (NexusRuntime.Logger != null)
                            NexusRuntime.Logger.LogError(message);
                        else
                            UnityEngine.Debug.LogError(message);
                        sb.AppendLine(issue.Message);
                    }

                    // P0-CR fix: opt-in fail-fast — teams that enable FailOnValidationErrors
                    // in their ContextData get a hard exception at startup so DI misconfigurations
                    // cannot silently pass through to production.
                    if (_contextData != null && _contextData.FailOnValidationErrors)
                    {
                        throw new NexusDiValidationException(
                            $"DI validation failed with {issues.Count} issue(s):\n{sb}");
                    }
                }
            }
        }

        internal async ValueTask InitializeReactiveModelsAsync(CancellationToken ct)
        {
            if (_builder != null) await _builder.InitializeReactiveModelsAsync(ct);
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

            // Execute IAsyncStartable and IStartable domain lifecycles
            await _orchestrator.ExecuteStartableLifecyclesAsync(Container.GetActiveSingletons(), ct);

            // Drain lazy services first resolved during OnStartAsync (e.g. by views/mediators).
            // Previously the single drain ran before OnStartAsync, so a lazy service resolved
            // during startup would never receive InitializeAsync.
            await InitializeLazyServicesAsync(ct);
        }

        /// <summary>
        /// Runs the PostContext phase for all lifecycles that implement <see cref="IPostContextLifecycle"/>.
        /// Called by <see cref="NexusRuntime.FinalizeInitializationAsync"/> after ALL contexts have
        /// completed their standard lifecycle (OnConfigure → OnInitializeAsync → OnStartAsync).
        /// </summary>
        internal ValueTask RunPostContextAsync(CancellationToken ct)
        {
            if (_postContextLifecycles.Count == 0) return default;

            if (_builder == null)
            {
                NexusRuntime.Logger?.LogWarning(
                    $"[Nexus] Context '{ScopeTag}': RunPostContextAsync skipped because context was never configured.");
                return default;
            }

            // NOTE: We pass the EXISTING builder (not a new one) so that lifecycles calling
            // OnPostContext can add cross-context bindings and signal registrations to the
            // same container that was used during Configure. A fresh ContextBuilder would
            // share the same Container reference but lose the reactive-model and service-type
            // lists, making late BindService/BindReactiveModel calls silently non-functional.
            var postBuilder = new ContextBuilder(Container, SignalBusInternal);

            for (int i = 0; i < _postContextLifecycles.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    _postContextLifecycles[i].OnPostContext(postBuilder);
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogError(
                        $"[Nexus] PostContext lifecycle '{_postContextLifecycles[i].GetType().Name}' failed in context '{ScopeTag}': {ex.Message}\n{ex.StackTrace}");
                }
            }
            return default;
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
                                var regCmdAttrs = type.GetCustomAttributes<RegisterCommandAttribute>();
                                foreach (var attr in regCmdAttrs)
                                {
                                    if (data == null) data = new ScannedHandlerData(type);
                                    data.Handlers.Add(new SignalHandlerAttribute(attr.SignalType) { Mode = attr.Mode, Priority = attr.Priority });
                                }
                                var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                                if (compositeAttr != null)
                                {
                                    if (data == null) data = new ScannedHandlerData(type);
                                    data.CompositeHandler = compositeAttr;
                                }
                                var regCompositeAttr = type.GetCustomAttribute<RegisterCompositeCommandAttribute>();
                                if (regCompositeAttr != null)
                                {
                                    if (data == null) data = new ScannedHandlerData(type);
                                    data.CompositeHandler = new CompositeSignalHandlerAttribute(regCompositeAttr.SignalTypes)
                                    {
                                        OneShot = regCompositeAttr.OneShot,
                                        Priority = regCompositeAttr.Priority
                                    };
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

                    // Registration and sync/async classification share ONE path with the
                    // test harness (SignalBus.RegisterCommandType), so attribute parsing
                    // can never drift between the production scan and tests. The registry
                    // binds the command type itself as non-singleton.
                    if (!SignalBusInternal.RegisterCommandType(type, data.Handlers, data.CompositeHandler))
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
        public T TryResolve<T>(string name) where T : class => Container.TryResolve<T>(name);
        public T ResolveCrossBoundary<T>() where T : class
        {
            try
            {
                return (T)Container.ResolveCrossBoundary(typeof(T));
            }
            catch (InvalidCastException ex)
            {
                throw new InvalidOperationException(
                    $"Cross-boundary resolve for '{typeof(T).Name}' failed: resolved instance could not be cast to the requested type.", ex);
            }
        }
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
            DisposeShared();

            _cts.Cancel();

            // A8 fix: the sync teardown path never blocks on IAsyncDisposable singletons
            // (NexusDI.Dispose schedules their DisposeAsync on a background task). Callers
            // that can await must use DisposeAsync() for deterministic async teardown.
            Container.Dispose();
            _cts.Dispose();
            // M5 fix: UnregisterContext is owned by DisposeShared (exactly once, after the
            // signal bus and pools are torn down) — the old trailing call here was a
            // redundant second unregister that could double-fire the unregister event path.
        }

        /// <summary>
        /// A8 fix: deterministic async teardown. Awaits every IAsyncDisposable singleton's
        /// <c>DisposeAsync</c> (via <see cref="NexusDI.DisposeAsync"/>) instead of blocking
        /// the calling thread — eliminates the sync-over-async deadlock risk on the Unity
        /// main thread during teardown.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeShared();

            _cts.Cancel();

            await Container.DisposeAsync();
            _cts.Dispose();
            // M5 fix: UnregisterContext is owned by DisposeShared (exactly once, after the
            // signal bus and pools are torn down) — the old trailing call here was a
            // redundant second unregister that could double-fire the unregister event path.
        }

        /// <summary>Shared teardown for <see cref="Dispose"/> and <see cref="DisposeAsync"/>.</summary>
        private void DisposeShared()
        {
            // Execute IStoppable domain lifecycles before container teardown
            try
            {
                _orchestrator.ExecuteStoppableLifecyclesSync(Container.GetActiveSingletons());
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Exception during ExecuteStoppableLifecyclesSync: {ex.Message}");
            }

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

            // INexusService lifecycle is owned by the Context (NexusDI.Dispose skips them), so
            // dispose every resolved INexusService singleton even when no builder was configured
            // (e.g. bare test contexts that bound services directly through the container).
            var disposedServices = new HashSet<object>();
            if (_builder != null)
            {
                var serviceTypes = _builder.ServiceTypes;
                for (int i = serviceTypes.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Container.TryGetExistingInstance(serviceTypes[i], out var existing) && existing is INexusService service
                            && disposedServices.Add(existing))
                        {
                            service.OnDispose();
                        }
                    }
                    catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                }
            }

            // Dispose any remaining resolved INexusService singletons (e.g. lazy services
            // first resolved outside the eager ServiceTypes list) so nothing leaks.
            foreach (var instance in Container.GetActiveSingletons())
            {
                if (instance is INexusService service && disposedServices.Add(instance))
                {
                    try { service.OnDispose(); }
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

            SignalBusInternal.Dispose();
            HybridQueue.Clear();
            PoolManager.Clear();

            // M5 fix: unregister EXACTLY ONCE, and LAST — after the signal bus, hybrid
            // queue, and pool manager are fully torn down. OnContextUnregistered
            // subscribers then observe a fully-disposed context instead of one whose bus
            // is still alive (the old order unregistered mid-teardown and the entry points
            // unregistered a second time).
            NexusRuntime.UnregisterContext(this);
        }

        public static void ClearAssemblyScanCache()
        {
            lock (s_scanLock) { s_assemblyScanCache.Clear(); }
        }
    }
}
