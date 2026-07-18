using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public class ContextBuilder : IContextBuilder
    {
        private readonly NexusDI _container;
        private readonly SignalBus _signalBus;
        private readonly List<Type> _reactiveModelTypes = new();
        private readonly List<Type> _serviceTypes = new();

        public ContextBuilder(NexusDI container, SignalBus signalBus)
        {
            _container = container;
            _signalBus = signalBus;
        }

        public void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        public void BindModel<TImplementation>() where TImplementation : class
        {
            _container.Bind<TImplementation>(isSingleton: true);
        }

        public void BindModelInstance<TInterface>(TInterface instance) where TInterface : class
        {
            _container.BindInstance(instance);
        }

        public void BindReactiveModel<TInterface, TImplementation>()
            where TImplementation : class, TInterface, IReactiveModel
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
            _reactiveModelTypes.Add(typeof(TInterface));
        }

        public void BindReactiveModel<TImplementation>()
            where TImplementation : class, IReactiveModel
        {
            _container.Bind<TImplementation>(isSingleton: true);
            _reactiveModelTypes.Add(typeof(TImplementation));
        }

        public void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        public void Bind<T>() where T : class
        {
            _container.Bind<T>(isSingleton: true);
        }

        public void BindInstance<T>(T instance) where T : class
        {
            _container.BindInstance(instance);
        }

        public void BindService<TInterface, TImplementation>()
            where TImplementation : class, TInterface, INexusService
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
            _serviceTypes.Add(typeof(TInterface));
        }

        public void BindService<TImplementation>()
            where TImplementation : class, INexusService
        {
            _container.Bind<TImplementation>(isSingleton: true);
            _serviceTypes.Add(typeof(TImplementation));
        }

        /// <summary>
        /// Registers a synchronous command to handle the specified signal type.
        /// The command is bound as non-singleton (one instance per execution).
        /// </summary>
        /// <typeparam name="TSignal">The signal struct type that triggers the command.</typeparam>
        /// <typeparam name="TCommand">The command class (must implement <see cref="ICommand"/>).</typeparam>
        /// <param name="mode">Execution mode (Sequential, Concurrent, or Exclusive). Composite triggers must be registered via [CompositeSignalHandler] instead.</param>
        /// <param name="priority">Execution priority; higher values run first.</param>
        public void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class where TSignal : struct
        {
            // P2-17 fix: Composite registration has its own path (CompositeSignalHandler);
            // passing it here would silently register a normal sequential-like handler.
            if (mode == ExecutionMode.Composite)
            {
                throw new ArgumentException($"ExecutionMode.Composite cannot be used with BindCommand. Use the [CompositeSignalHandler] attribute (or SignalBus.RegisterCompositeCommand) to register composite triggers.", nameof(mode));
            }

            // Validate that the command implements either ICommand or ICommand<TSignal>
            bool isGeneric = typeof(ICommand<TSignal>).IsAssignableFrom(typeof(TCommand));
            bool isNormal = typeof(ICommand).IsAssignableFrom(typeof(TCommand));
            if (!isGeneric && !isNormal)
            {
                throw new ArgumentException($"Command type {typeof(TCommand).Name} must implement either ICommand or ICommand<{typeof(TSignal).Name}>");
            }

            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: false);
        }

        /// <summary>
        /// Registers an asynchronous command to handle the specified signal type.
        /// The command is bound as non-singleton (one instance per execution).
        /// </summary>
        /// <typeparam name="TSignal">The signal struct type that triggers the command.</typeparam>
        /// <typeparam name="TCommand">The command class.</typeparam>
        /// <param name="mode">Execution mode (Sequential, Concurrent, or Exclusive). Composite triggers must be registered via [CompositeSignalHandler] instead.</param>
        /// <param name="priority">Execution priority; higher values run first.</param>
        public void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class where TSignal : struct
        {
            // P2-17 fix: Composite registration has its own path (CompositeSignalHandler).
            if (mode == ExecutionMode.Composite)
            {
                throw new ArgumentException($"ExecutionMode.Composite cannot be used with BindAsyncCommand. Use the [CompositeSignalHandler] attribute (or SignalBus.RegisterCompositeCommand) to register composite triggers.", nameof(mode));
            }

            // Validate that the command implements either IAsyncCommand or IAsyncCommand<TSignal>
            bool isGeneric = typeof(IAsyncCommand<TSignal>).IsAssignableFrom(typeof(TCommand));
            bool isNormal = typeof(IAsyncCommand).IsAssignableFrom(typeof(TCommand));
            if (!isGeneric && !isNormal)
            {
                throw new ArgumentException($"Command type {typeof(TCommand).Name} must implement either IAsyncCommand or IAsyncCommand<{typeof(TSignal).Name}>");
            }

            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: true);
        }

        public ICommandBindingBuilder<TSignal> BindSignal<TSignal>() where TSignal : struct
        {
            return new CommandBindingBuilder<TSignal>(this);
        }

        public void Fire<T>(T signal) where T : struct
        {
            _signalBus.Fire(signal);
        }

        internal IReadOnlyList<Type> ReactiveModelTypes => _reactiveModelTypes;
        internal IReadOnlyList<Type> ServiceTypes => _serviceTypes;

        internal async ValueTask InitializeReactiveModelsAsync(ISignalBus signalBus, CancellationToken ct)
        {
            foreach (var modelType in _reactiveModelTypes)
            {
                if (ct.IsCancellationRequested) break;

                var model = _container.Resolve(modelType) as IReactiveModel;
                if (model != null)
                {
                    await model.OnBind(ct);
                }
            }
        }

        internal async ValueTask InitializeServicesAsync(CancellationToken ct)
        {
            foreach (var serviceType in _serviceTypes)
            {
                if (ct.IsCancellationRequested) break;

                var service = _container.Resolve(serviceType) as INexusService;
                if (service != null)
                {
                    await service.InitializeAsync(ct);
                }
            }
        }
    }

    [Preserve]
    internal class CommandBindingBuilder<TSignal> : ICommandBindingBuilder<TSignal> where TSignal : struct
    {
        private readonly ContextBuilder _builder;

        public CommandBindingBuilder(ContextBuilder builder)
        {
            _builder = builder;
        }

        public ICommandBindingBuilder<TSignal> To<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class
        {
            _builder.BindCommand<TSignal, TCommand>(mode, priority);
            return this;
        }

        public ICommandBindingBuilder<TSignal> ToAsync<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class
        {
            _builder.BindAsyncCommand<TSignal, TCommand>(mode, priority);
            return this;
        }
    }
}
