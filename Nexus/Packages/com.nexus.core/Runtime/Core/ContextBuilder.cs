using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Default implementation of <see cref="IContextBuilder"/>.
    /// Provides a fluent API for registering models, commands, and general bindings during context configuration.
    /// </summary>
    [Preserve]
    public class ContextBuilder : IContextBuilder
    {
        private readonly NexusDI _container;
        private readonly SignalBus _signalBus;

        /// <summary>Creates a new <see cref="ContextBuilder"/> wrapping the given DI container and signal bus.</summary>
        /// <param name="container">The DI container for binding models and services.</param>
        /// <param name="signalBus">The signal bus for registering command handlers.</param>
        public ContextBuilder(NexusDI container, SignalBus signalBus)
        {
            _container = container;
            _signalBus = signalBus;
        }

        /// <summary>Binds a model interface to its singleton implementation.</summary>
        /// <typeparam name="TInterface">The model interface type.</typeparam>
        /// <typeparam name="TImplementation">The concrete implementation type (must be a class).</typeparam>
        public void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        /// <summary>Binds a self-referencing model as a singleton.</summary>
        /// <typeparam name="TImplementation">The concrete model type.</typeparam>
        public void BindModel<TImplementation>() where TImplementation : class
        {
            _container.Bind<TImplementation>(isSingleton: true);
        }

        /// <summary>Binds an existing model instance by interface type.</summary>
        /// <typeparam name="TInterface">The model interface type.</typeparam>
        /// <param name="instance">The existing instance to bind.</param>
        public void BindModelInstance<TInterface>(TInterface instance) where TInterface : class
        {
            _container.BindInstance(instance);
        }

        /// <summary>Binds an interface to its singleton implementation (general-purpose).</summary>
        /// <typeparam name="TInterface">The service interface type.</typeparam>
        /// <typeparam name="TImplementation">The concrete implementation type.</typeparam>
        public void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        /// <summary>Binds a self-referencing service as a singleton.</summary>
        /// <typeparam name="T">The service type.</typeparam>
        public void Bind<T>() where T : class
        {
            _container.Bind<T>(isSingleton: true);
        }

        /// <summary>Binds an existing instance by type.</summary>
        /// <typeparam name="T">The service type.</typeparam>
        /// <param name="instance">The existing instance to bind.</param>
        public void BindInstance<T>(T instance) where T : class
        {
            _container.BindInstance(instance);
        }

        /// <summary>
        /// Registers a synchronous command to handle the specified signal type.
        /// The command is bound as non-singleton (one instance per execution).
        /// </summary>
        /// <typeparam name="TSignal">The signal struct type that triggers the command.</typeparam>
        /// <typeparam name="TCommand">The command class (must implement <see cref="ICommand"/>).</typeparam>
        /// <param name="mode">Execution mode (Sequential, Concurrent, Exclusive, CompositeTrigger).</param>
        /// <param name="priority">Execution priority; lower values run first.</param>
        public void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class, ICommand
        {
            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: false);
        }

        /// <summary>
        /// Registers an asynchronous command to handle the specified signal type.
        /// The command is bound as non-singleton (one instance per execution).
        /// </summary>
        /// <typeparam name="TSignal">The signal struct type that triggers the command.</typeparam>
        /// <typeparam name="TCommand">The command class (must implement <see cref="IAsyncCommand"/>).</typeparam>
        /// <param name="mode">Execution mode (Sequential, Concurrent, Exclusive, CompositeTrigger).</param>
        /// <param name="priority">Execution priority; lower values run first.</param>
        public void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class, IAsyncCommand
        {
            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: true);
        }

        /// <summary>
        /// Fires a signal immediately through the context's signal bus.
        /// Convenience wrapper around <see cref="SignalBus.Fire{T}"/>.
        /// </summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data to fire.</param>
        public void Fire<T>(T signal) where T : struct
        {
            _signalBus.Fire(signal);
        }
    }
}
