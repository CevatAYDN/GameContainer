using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public class NexusTestContext : IDisposable
    {
        private readonly Dictionary<Type, object> _dispatchedSignals = new();
        private readonly List<IDisposable> _subscriptions = new();

        public Context Context { get; }

        public NexusTestContext(Context context)
        {
            Context = context;
        }

        public void Register<T>()
        {
            var type = typeof(T);
            if (type.IsValueType)
            {
                // Register signal: subscribe to it and store dispatched instances
                var method = GetType().GetMethod(nameof(RegisterSignalInternal), BindingFlags.NonPublic | BindingFlags.Instance);
                if (method != null)
                {
                    var genericMethod = method.MakeGenericMethod(type);
                    genericMethod.Invoke(this, null);
                }
            }
            else if (type.IsClass)
            {
                // Bind class in container
                bool isCommand = typeof(ICommand).IsAssignableFrom(type) || typeof(IAsyncCommand).IsAssignableFrom(type);
                if (isCommand)
                {
                    Context.Container.Bind(type, isSingleton: false);

                    var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                    bool hasAttr = false;
                    foreach (var attr in handlerAttrs)
                    {
                        hasAttr = true;
                        Context.SignalBusInternal.RegisterCommand(
                            attr.SignalType, 
                            type, 
                            attr.Mode, 
                            attr.Priority, 
                            isAsync: typeof(IAsyncCommand).IsAssignableFrom(type)
                        );
                    }

                    var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                    if (compositeAttr != null)
                    {
                        hasAttr = true;
                        Context.SignalBusInternal.RegisterCompositeCommand(
                            compositeAttr.SignalTypes, 
                            type, 
                            compositeAttr.OneShot, 
                            compositeAttr.Priority, 
                            isAsync: typeof(IAsyncCommand).IsAssignableFrom(type)
                        );
                    }

                    if (!hasAttr)
                    {
                        throw new InvalidOperationException($"Command type {type.Name} does not have any [SignalHandler] or [CompositeSignalHandler] attributes.");
                    }
                }
                else
                {
                    // Treat as standard DI dependency (e.g. Model, Service)
                    Context.Container.Bind(type, isSingleton: true);
                }
            }
        }

        public void RegisterCommand<TCommand>() where TCommand : class, ICommand
        {
            var type = typeof(TCommand);
            Context.Container.Bind(type, isSingleton: false);

            var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
            bool hasAttr = false;
            foreach (var attr in handlerAttrs)
            {
                hasAttr = true;
                Context.SignalBusInternal.RegisterCommand(
                    attr.SignalType, 
                    type, 
                    attr.Mode, 
                    attr.Priority, 
                    isAsync: false
                );
            }

            var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
            if (compositeAttr != null)
            {
                hasAttr = true;
                Context.SignalBusInternal.RegisterCompositeCommand(
                    compositeAttr.SignalTypes, 
                    type, 
                    compositeAttr.OneShot, 
                    compositeAttr.Priority, 
                    isAsync: false
                );
            }

            if (!hasAttr)
            {
                throw new InvalidOperationException($"Command type {type.Name} does not have any [SignalHandler] or [CompositeSignalHandler] attributes.");
            }
        }

        public void RegisterAsyncCommand<TCommand>() where TCommand : class, IAsyncCommand
        {
            var type = typeof(TCommand);
            Context.Container.Bind(type, isSingleton: false);

            var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
            bool hasAttr = false;
            foreach (var attr in handlerAttrs)
            {
                hasAttr = true;
                Context.SignalBusInternal.RegisterCommand(
                    attr.SignalType, 
                    type, 
                    attr.Mode, 
                    attr.Priority, 
                    isAsync: true
                );
            }

            var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
            if (compositeAttr != null)
            {
                hasAttr = true;
                Context.SignalBusInternal.RegisterCompositeCommand(
                    compositeAttr.SignalTypes, 
                    type, 
                    compositeAttr.OneShot, 
                    compositeAttr.Priority, 
                    isAsync: true
                );
            }

            if (!hasAttr)
            {
                throw new InvalidOperationException($"Command type {type.Name} does not have any [SignalHandler] or [CompositeSignalHandler] attributes.");
            }
        }

        private void RegisterSignalInternal<TSignal>() where TSignal : struct
        {
            if (!_dispatchedSignals.ContainsKey(typeof(TSignal)))
            {
                var list = new List<TSignal>();
                _dispatchedSignals[typeof(TSignal)] = list;
                _subscriptions.Add(Context.SignalBus.Subscribe<TSignal>(sig => list.Add(sig)));
            }
        }

        public void Dispatch<T>(T signal) where T : struct
        {
            Context.SignalBus.Fire(signal);
        }

        public ValueTask DispatchAsync<T>(T signal) where T : struct
        {
            return Context.SignalBus.FireAsync(signal);
        }

        public bool SignalWasDispatched<TSignal>() where TSignal : struct
        {
            return _dispatchedSignals.TryGetValue(typeof(TSignal), out var list) && ((List<TSignal>)list).Count > 0;
        }

        public int GetDispatchedSignalCount<TSignal>() where TSignal : struct
        {
            if (_dispatchedSignals.TryGetValue(typeof(TSignal), out var list))
            {
                return ((List<TSignal>)list).Count;
            }
            return 0;
        }

        public IReadOnlyList<TSignal> GetDispatchedSignals<TSignal>() where TSignal : struct
        {
            if (_dispatchedSignals.TryGetValue(typeof(TSignal), out var list))
            {
                return (List<TSignal>)list;
            }
            return Array.Empty<TSignal>();
        }

        public TSignal GetLastDispatchedSignal<TSignal>() where TSignal : struct
        {
            var list = GetDispatchedSignals<TSignal>();
            if (list.Count == 0)
            {
                throw new InvalidOperationException($"No signals of type {typeof(TSignal).Name} were dispatched.");
            }
            return list[list.Count - 1];
        }

        public void ClearDispatchedSignals()
        {
            foreach (var list in _dispatchedSignals.Values)
            {
                if (list is System.Collections.IList listInstance)
                {
                    listInstance.Clear();
                }
            }
        }

        public void AssertSignalDispatched<TSignal>() where TSignal : struct
        {
            if (!SignalWasDispatched<TSignal>())
            {
                throw new UnityEngine.Assertions.AssertionException(
                    $"Assertion failed: Expected signal of type '{typeof(TSignal).Name}' to be dispatched, but it was not.",
                    ""
                );
            }
        }

        public void AssertSignalNotDispatched<TSignal>() where TSignal : struct
        {
            if (SignalWasDispatched<TSignal>())
            {
                throw new UnityEngine.Assertions.AssertionException(
                    $"Assertion failed: Expected signal of type '{typeof(TSignal).Name}' NOT to be dispatched, but it was.",
                    ""
                );
            }
        }

        public T GetModel<T>() where T : class
        {
            return Context.Resolve<T>();
        }

        public void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            Context.Container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        public void BindModel<TImplementation>() where TImplementation : class
        {
            Context.Container.Bind<TImplementation>(isSingleton: true);
        }

        public void BindModelInstance<TInterface>(TInterface instance) where TInterface : class
        {
            Context.Container.BindInstance(instance);
        }

        public void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            Context.Container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        public void Bind<T>() where T : class
        {
            Context.Container.Bind<T>(isSingleton: true);
        }

        public void BindInstance<T>(T instance) where T : class
        {
            Context.Container.BindInstance(instance);
        }

        public async ValueTask InitializeAsync(CancellationToken ct = default)
        {
            if (Context.Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Context.Container.Resolve<IContextLifecycle>();
                await lifecycle.OnInitializeAsync(ct);
            }
        }

        public async ValueTask StartAsync(CancellationToken ct = default)
        {
            if (Context.Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Context.Container.Resolve<IContextLifecycle>();
                await lifecycle.OnStartAsync(ct);
            }
        }

        public void Dispose()
        {
            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();
            _dispatchedSignals.Clear();

            Context.Dispose();
        }
    }
}
