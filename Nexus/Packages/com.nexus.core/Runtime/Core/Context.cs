using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace Nexus.Core
{
    public class Context : IContext
    {
        private readonly Context _parent;
        private readonly ContextData _contextData;
        private readonly CancellationTokenSource _cts = new();
        private readonly ViewBinder _viewBinder;
        
        public ISignalBus SignalBus { get; }
        public CancellationToken LifetimeToken => _cts.Token;
        public IContext Parent => _parent;
        
        public NexusDI Container { get; }
        public CommandPoolManager PoolManager { get; }
        public HybridQueue HybridQueue { get; }
        public string ScopeTag => _contextData != null ? _contextData.ScopeTag : null;
        public SignalBus SignalBusInternal => (SignalBus)SignalBus;

        public Context(Context parent = null, ContextData contextData = null)
        {
            _parent = parent;
            _contextData = contextData;
            
            Container = new NexusDI(parent?.Container);
            
            Container.BindInstance(Container);
            Container.BindInstance<IContext>(this);

            var poolSize = contextData != null ? contextData.CommandPoolInitialSize : 4;
            PoolManager = new CommandPoolManager(Container, poolSize);
            Container.BindInstance(PoolManager);

            var bus = new SignalBus(Container, PoolManager, this);
            SignalBus = bus;
            Container.BindInstance<ISignalBus>(bus);
            Container.BindInstance(bus);

            HybridQueue = new HybridQueue(bus);
            Container.BindInstance(HybridQueue);

            _viewBinder = new ViewBinder(this, Container);
            Container.BindInstance(_viewBinder);
            
            NexusRuntime.RegisterContext(this);
        }

        public void Configure()
        {
            var builder = new ContextBuilder(Container, SignalBusInternal);
            
            // Call user lifecycle OnConfigure if registered
            if (Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Container.Resolve<IContextLifecycle>();
                lifecycle.OnConfigure(builder);
            }

            // Scan and register attributes
            ScanAssembliesAndRegister(builder);
        }

        private void ScanAssembliesAndRegister(ContextBuilder builder)
        {
            if (_contextData == null || _contextData.AssemblyScopes == null) return;

            var assemblies = new List<Assembly>();
            if (_contextData.AssemblyScopes.Length == 0)
            {
                // Fallback to active assembly
                assemblies.Add(Assembly.GetExecutingAssembly());
            }
            else
            {
                foreach (var scopeName in _contextData.AssemblyScopes)
                {
                    try
                    {
                        var assembly = Assembly.Load(scopeName);
                        if (assembly != null)
                        {
                            assemblies.Add(assembly);
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[Nexus] Failed to load assembly {scopeName}: {ex.Message}");
                    }
                }
            }

            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsClass && !type.IsAbstract)
                    {
                        // Scan [SignalHandler]
                        var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                        foreach (var attr in handlerAttrs)
                        {
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


                        // Scan [CompositeSignalHandler]
                        var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                        if (compositeAttr != null)
                        {
                            bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(type);
                            SignalBusInternal.RegisterCompositeCommand(compositeAttr.SignalTypes, type, compositeAttr.OneShot, compositeAttr.Priority, isAsync);
                        }
                    }
                }
            }
        }

        public T Resolve<T>() where T : class
        {
            return Container.Resolve<T>();
        }

        public void RegisterView(IView view)
        {
            _viewBinder.RegisterView(view);
        }

        public void UnregisterView(IView view)
        {
            _viewBinder.UnregisterView(view);
        }

        public void Dispose()
        {
            _cts.Cancel();
            
            _viewBinder.Dispose();

            NexusRuntime.UnregisterContext(this);
            
            SignalBusInternal.Dispose();
            HybridQueue.Clear();
            PoolManager.Clear();
            Container.Dispose();
            _cts.Dispose();
        }
    }
}
