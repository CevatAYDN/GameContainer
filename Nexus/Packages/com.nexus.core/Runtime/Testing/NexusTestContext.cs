using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    /// <summary>
    /// Test utility for writing Nexus integration tests.
    /// Provides helper methods for registering commands/models, dispatching signals, and asserting signal dispatch.
    /// </summary>
    public class NexusTestContext : IDisposable
    {
        private readonly Dictionary<Type, object> _dispatchedSignals = new();
        private readonly List<IDisposable> _subscriptions = new();

        /// <summary>The underlying Nexus context.</summary>
        public Context Context { get; }

        /// <summary>Wraps a <see cref="Context"/> for test use.</summary>
        /// <param name="context">The context to wrap.</param>
        public NexusTestContext(Context context)
        {
            Context = context;
        }

        /// <summary>
        /// Registers a type for test. Signal structs are subscribed for dispatch tracking.
        /// Commands are bound transient and wired to their signal handlers. Other classes are bound as singletons.
        /// </summary>
        /// <typeparam name="T">The type to register (signal struct, command class, or service class).</typeparam>
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
                bool isCommand = typeof(ICommand).IsAssignableFrom(type) || ImplementsGenericInterface(type, typeof(ICommand<>));
                bool isAsyncCommand = typeof(IAsyncCommand).IsAssignableFrom(type) || ImplementsGenericInterface(type, typeof(IAsyncCommand<>));
                if (isCommand || isAsyncCommand)
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
                            isAsync: isAsyncCommand
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

        /// <summary>Registers a synchronous command type and wires it to its signal handlers.</summary>
        /// <typeparam name="TCommand">The command type.</typeparam>
        public void RegisterCommand<TCommand>() where TCommand : class
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

        /// <summary>Registers an asynchronous command type and wires it to its signal handlers.</summary>
        /// <typeparam name="TCommand">The command type.</typeparam>
        public void RegisterAsyncCommand<TCommand>() where TCommand : class
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

        /// <summary>Fires a signal synchronously for test purposes.</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public void Dispatch<T>(T signal) where T : struct
        {
            Context.SignalBus.Fire(signal);
        }

        /// <summary>Fires a signal asynchronously for test purposes.</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public ValueTask DispatchAsync<T>(T signal) where T : struct
        {
            return Context.SignalBus.FireAsync(signal);
        }

        /// <summary>Returns true if the specified signal type has been dispatched at least once.</summary>
        /// <typeparam name="TSignal">The signal struct type.</typeparam>
        public bool SignalWasDispatched<TSignal>() where TSignal : struct
        {
            return _dispatchedSignals.TryGetValue(typeof(TSignal), out var list) && ((List<TSignal>)list).Count > 0;
        }

        /// <summary>Returns the number of times the specified signal has been dispatched.</summary>
        /// <typeparam name="TSignal">The signal struct type.</typeparam>
        public int GetDispatchedSignalCount<TSignal>() where TSignal : struct
        {
            if (_dispatchedSignals.TryGetValue(typeof(TSignal), out var list))
            {
                return ((List<TSignal>)list).Count;
            }
            return 0;
        }

        /// <summary>Returns all dispatched instances of the specified signal type.</summary>
        /// <typeparam name="TSignal">The signal struct type.</typeparam>
        public IReadOnlyList<TSignal> GetDispatchedSignals<TSignal>() where TSignal : struct
        {
            if (_dispatchedSignals.TryGetValue(typeof(TSignal), out var list))
            {
                return (List<TSignal>)list;
            }
            return Array.Empty<TSignal>();
        }

        /// <summary>Returns the last dispatched instance of the specified signal type.</summary>
        /// <typeparam name="TSignal">The signal struct type.</typeparam>
        /// <exception cref="InvalidOperationException">Thrown if no signal of this type was dispatched.</exception>
        public TSignal GetLastDispatchedSignal<TSignal>() where TSignal : struct
        {
            var list = GetDispatchedSignals<TSignal>();
            if (list.Count == 0)
            {
                throw new InvalidOperationException($"No signals of type {typeof(TSignal).Name} were dispatched.");
            }
            return list[list.Count - 1];
        }

        /// <summary>Clears all tracked dispatched signal data.</summary>
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

        /// <summary>Asserts that the specified signal type was dispatched.</summary>
        /// <typeparam name="TSignal">The signal struct type.</typeparam>
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

        /// <summary>Asserts that the specified signal type was NOT dispatched.</summary>
        /// <typeparam name="TSignal">The signal struct type.</typeparam>
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

        /// <summary>Resolves a model from the DI container.</summary>
        /// <typeparam name="T">The model type.</typeparam>
        public T GetModel<T>() where T : class
        {
            return Context.Resolve<T>();
        }

        /// <summary>Binds a model interface to its singleton implementation.</summary>
        /// <typeparam name="TInterface">The model interface.</typeparam>
        /// <typeparam name="TImplementation">The concrete type.</typeparam>
        public void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            Context.Container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        /// <summary>Binds a self-referencing model as a singleton.</summary>
        /// <typeparam name="TImplementation">The concrete model type.</typeparam>
        public void BindModel<TImplementation>() where TImplementation : class
        {
            Context.Container.Bind<TImplementation>(isSingleton: true);
        }

        /// <summary>Binds an existing model instance.</summary>
        /// <typeparam name="TInterface">The model interface type.</typeparam>
        /// <param name="instance">The instance to bind.</param>
        public void BindModelInstance<TInterface>(TInterface instance) where TInterface : class
        {
            Context.Container.BindInstance(instance);
        }

        /// <summary>Binds a service interface to its singleton implementation.</summary>
        /// <typeparam name="TInterface">The interface type.</typeparam>
        /// <typeparam name="TImplementation">The concrete type.</typeparam>
        public void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            Context.Container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        /// <summary>Binds a self-referencing service as a singleton.</summary>
        /// <typeparam name="T">The service type.</typeparam>
        public void Bind<T>() where T : class
        {
            Context.Container.Bind<T>(isSingleton: true);
        }

        /// <summary>Binds an existing instance by type.</summary>
        /// <typeparam name="T">The type.</typeparam>
        /// <param name="instance">The instance to bind.</param>
        public void BindInstance<T>(T instance) where T : class
        {
            Context.Container.BindInstance(instance);
        }

        /// <summary>Calls OnInitializeAsync on the registered lifecycle, if any.</summary>
        /// <param name="ct">Cancellation token.</param>
        public async ValueTask InitializeAsync(CancellationToken ct = default)
        {
            if (Context.Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Context.Container.Resolve<IContextLifecycle>();
                await lifecycle.OnInitializeAsync(ct);
            }
        }

        /// <summary>Calls OnStartAsync on the registered lifecycle, if any.</summary>
        /// <param name="ct">Cancellation token.</param>
        public async ValueTask StartAsync(CancellationToken ct = default)
        {
            if (Context.Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = Context.Container.Resolve<IContextLifecycle>();
                await lifecycle.OnStartAsync(ct);
            }
        }

        /// <summary>Disposes all subscriptions, tracked signals, and the underlying context.</summary>
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

        private static bool ImplementsGenericInterface(Type type, Type genericInterface)
        {
            if (type == null) return false;
            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == genericInterface)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
