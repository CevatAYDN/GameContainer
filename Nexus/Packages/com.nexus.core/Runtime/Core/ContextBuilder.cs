using System;

namespace Nexus.Core
{
    public class ContextBuilder : IContextBuilder
    {
        private readonly NexusDI _container;
        private readonly SignalBus _signalBus;

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

        public void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class, ICommand
        {
            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: false);
        }

        public void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class, IAsyncCommand
        {
            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: true);
        }
    }
}
