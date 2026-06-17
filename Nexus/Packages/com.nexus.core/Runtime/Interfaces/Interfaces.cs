using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public interface ICommand
    {
        void Execute();
    }

    public interface IAsyncCommand
    {
        ValueTask ExecuteAsync(CancellationToken ct);
    }

    public interface IResettable
    {
        void Reset();
    }

    public interface IContextBuilder
    {
        void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface;
        void BindModel<TImplementation>() where TImplementation : class;
        void BindModelInstance<TInterface>(TInterface instance) where TInterface : class;
        
        void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface;
        void Bind<T>() where T : class;
        void BindInstance<T>(T instance) where T : class;
        
        void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class, ICommand;
        void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class, IAsyncCommand;
    }

    public interface IContextLifecycle
    {
        void OnConfigure(IContextBuilder builder);
        ValueTask OnInitializeAsync(CancellationToken ct);
        ValueTask OnStartAsync(CancellationToken ct);
        void OnDispose();
    }

    public interface IContext : IDisposable
    {
        ISignalBus SignalBus { get; }
        CancellationToken LifetimeToken { get; }
        void RegisterView(IView view);
        void UnregisterView(IView view);
        T Resolve<T>() where T : class;
        IContext Parent { get; }
        void RegisterPlugin(INexusPlugin plugin);
        void RemovePlugin(INexusPlugin plugin);
    }

    public interface ISignalBus
    {
        void Fire<T>(T signal) where T : struct;
        ValueTask FireAsync<T>(T signal) where T : struct;
        void FireThreadSafe<T>(T signal) where T : struct;
        void FireNextFrame<T>(T signal) where T : struct;
        ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct;
        ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct;
    }

    public interface ISignalSubscription : IDisposable
    {
        bool IsActive { get; }
        CancellationToken Lifetime { get; }
    }

    public interface IView
    {
        void Bind(IContext context);
        void Unbind();
    }

    public interface IRecoveryStrategy
    {
        RecoveryDecision OnCommandFailed(CommandFailureContext failure);
    }

    // Exceptions
    public class NexusReentrancyException : Exception
    {
        public NexusReentrancyException(string message) : base(message) { }
    }

    public class NexusAsyncOverflowException : Exception
    {
        public NexusAsyncOverflowException(string message) : base(message) { }
    }

    public class UnauthorizedPluginAccessException : Exception
    {
        public UnauthorizedPluginAccessException(string message) : base(message) { }
    }
}
