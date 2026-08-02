using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public interface ICommand
    {
        void Execute();
    }

    public interface ICommand<in TSignal> where TSignal : struct
    {
        void Execute(TSignal signal);
    }

    public interface IAsyncCommand
    {
        ValueTask ExecuteAsync(CancellationToken ct);
    }

    public interface IAsyncCommand<in TSignal> where TSignal : struct
    {
        ValueTask ExecuteAsync(TSignal signal, CancellationToken ct);
    }

    /// <summary>
    /// Read-only view of the signal payloads that satisfied a composite trigger.
    /// Supplied to <see cref="ICompositeCommand"/> / <see cref="IAsyncCompositeCommand"/> once all
    /// required signals have been received. Each required signal type is represented by the most
    /// recent instance captured before the trigger completed.
    /// </summary>
    public readonly struct CompositeContext
    {
        private readonly Type[] _signalTypes;
        private readonly object[] _payloads;

        internal CompositeContext(Type[] signalTypes, object[] payloads)
        {
            _signalTypes = signalTypes;
            _payloads = payloads;
        }

        /// <summary>Number of required signal slots in this composite.</summary>
        public int Count => _signalTypes?.Length ?? 0;

        /// <summary>
        /// Attempts to retrieve the captured payload for signal type <typeparamref name="T"/>.
        /// Returns false if the type is not part of this composite or was never captured.
        /// </summary>
        public bool TryGet<T>(out T signal) where T : struct
        {
            if (_signalTypes != null)
            {
                for (int i = 0; i < _signalTypes.Length; i++)
                {
                    if (_signalTypes[i] == typeof(T) && _payloads[i] is T typed)
                    {
                        signal = typed;
                        return true;
                    }
                }
            }
            signal = default;
            return false;
        }

        /// <summary>Retrieves the captured payload for signal type <typeparamref name="T"/>, or default if absent.</summary>
        public T Get<T>() where T : struct => TryGet<T>(out var s) ? s : default;
    }

    /// <summary>
    /// A composite command triggered only after all of its required signals have been received.
    /// Unlike <see cref="ICommand{TSignal}"/>, a composite spans multiple signal types; the payloads
    /// that satisfied the trigger are supplied via <see cref="CompositeContext"/>.
    /// </summary>
    public interface ICompositeCommand
    {
        void Execute(CompositeContext signals);
    }

    /// <summary>Asynchronous counterpart of <see cref="ICompositeCommand"/>.</summary>
    public interface IAsyncCompositeCommand
    {
        ValueTask ExecuteAsync(CompositeContext signals, CancellationToken ct);
    }

    public interface IResettable
    {
        void Reset();
    }

    /// <summary>
    /// Model that can be reset to its initial state (e.g. for pooling or replay).
    /// Plan §4 — Memory Ownership Model.
    /// </summary>
    public interface IResettableModel
    {
        void Reset();
    }

    /// <summary>
    /// Model with explicit dispose lifecycle. BuildValidation checks that
    /// all IDisposableModel instances are disposed in the disposal chain.
    /// Plan §4 — Memory Ownership Model.
    /// </summary>
    public interface IDisposableModel : IDisposable
    {
    }

    /// <summary>
    /// Defines a model that supports saving and restoring its internal state snapshot.
    /// Used by NetworkSignalBus to handle deterministic rollback and state recovery in multiplayer games.
    /// </summary>
    public interface ISnapshotableModel<TState> where TState : struct
    {
        TState CaptureSnapshot();
        void RestoreSnapshot(TState snapshot);
    }

    public interface ICommandBindingBuilder<TSignal> where TSignal : struct
    {
        ICommandBindingBuilder<TSignal> To<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class;
        ICommandBindingBuilder<TSignal> ToAsync<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class;
        /// <summary>Marks the next To/ToAsync as one-shot: it fires once then is unregistered.</summary>
        ICommandBindingBuilder<TSignal> Once();
    }

    public interface IContextBuilder
    {
        /// <summary>
        /// Bind models, services, and commands inside a lifecycle's OnConfigure phase.
        /// Bindings made here are available before initialization begins.
        /// </summary>
        void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface;
        void BindModel<TImplementation>() where TImplementation : class;
        void BindModelInstance<TInterface>(TInterface instance) where TInterface : class;
        
        /// <summary>
        /// Binds a reactive model (implements <see cref="IReactiveModel"/>) as a singleton.
        /// After configuration, the Nexus runtime automatically calls <see cref="IReactiveModel.OnBind"/>
        /// on all registered reactive models.
        /// </summary>
        void BindReactiveModel<TInterface, TImplementation>() 
            where TImplementation : class, TInterface, IReactiveModel;

        /// <summary>Binds a self-referencing reactive model as a singleton.</summary>
        void BindReactiveModel<TImplementation>() 
            where TImplementation : class, IReactiveModel;
        
        /// <summary>
        /// Binds a service interface to its implementation. Services implement <see cref="INexusService"/>
        /// and receive automatic lifecycle management (initialization + disposal).
        /// </summary>
        void BindService<TInterface, TImplementation>() 
            where TImplementation : class, TInterface, INexusService;

        /// <summary>Binds a self-referencing service as a singleton.</summary>
        void BindService<TImplementation>()
            where TImplementation : class, INexusService;

        /// <summary>
        /// Binds a lazy service interface to its implementation. Unlike BindService, the service
        /// is NOT eagerly constructed during InitializeServicesAsync — it is resolved on first access
        /// via LazyInjection&lt;T&gt; or direct Resolve&lt;T&gt; call. Implements INexusService for lifecycle
        /// management; InitializeAsync is called during the lazy-service initialization window.
        /// </summary>
        void BindLazyService<TInterface, TImplementation>()
            where TImplementation : class, TInterface, INexusService;

        /// <summary>Binds a self-referencing lazy service as a singleton.</summary>
        void BindLazyService<TImplementation>()
            where TImplementation : class, INexusService;
        
        /// <summary>Low-level bind for registering any implementation during OnConfigure.</summary>
        void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface;
        void Bind<T>() where T : class;
        void BindInstance<T>(T instance) where T : class;

        /// <summary>Binds a named implementation (Strange-style named injection).</summary>
        void Bind<TInterface, TImplementation>(string name) where TImplementation : class, TInterface;
        /// <summary>Binds a named self-referencing type.</summary>
        void Bind<T>(string name) where T : class;
        /// <summary>Binds a named instance value.</summary>
        void BindInstance<T>(string name, T instance) where T : class;

        /// <summary>
        /// Creates and registers a general-purpose <see cref="NexusBinder{TKey,TValue}"/> as a
        /// singleton so it can be injected anywhere (Strange-style generic binder).
        /// <c>TKey</c> may be any reference or value type (enums are the canonical catalog key).
        /// </summary>
        void BindBinder<TKey, TValue>() where TKey : notnull;

        /// <summary>
        /// Binds one concrete implementation under MULTIPLE interfaces (Strange-style
        /// polymorphic binding). All keys share a single singleton instance.
        /// </summary>
        void BindMultiple<TInterface1, TInterface2, TImplementation>()
            where TImplementation : class, TInterface1, TInterface2;
        /// <summary>Three-interface polymorphic binding (see the two-interface overload).</summary>
        void BindMultiple<TInterface1, TInterface2, TInterface3, TImplementation>()
            where TImplementation : class, TInterface1, TInterface2, TInterface3;

        void EnableStrictInjection();
        
        void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class where TSignal : struct;
        void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class where TSignal : struct;

        /// <summary>Registers a one-shot command: it fires once then is unregistered (Strange-style <c>.Once()</c>).</summary>
        void BindCommandOnce<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct;
        /// <summary>Registers a one-shot async command: it fires once then is unregistered.</summary>
        void BindAsyncCommandOnce<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct;

        ICommandBindingBuilder<TSignal> BindSignal<TSignal>() where TSignal : struct;

        void Fire<T>(T signal) where T : struct;
    }

    public interface IContextLifecycle
    {
        /// <summary>
        /// Called first. Use this to bind models, commands, and services.
        /// </summary>
        void OnConfigure(IContextBuilder builder);
        /// <summary>
        /// Called after configuration and reactive-model initialization.
        /// Use this for async setup work that depends on bindings being ready.
        /// </summary>
        ValueTask OnInitializeAsync(CancellationToken ct);
        /// <summary>
        /// Called after OnInitializeAsync for final startup work.
        /// Use this for signal subscriptions, view hookup, and runtime kickoff.
        /// </summary>
        ValueTask OnStartAsync(CancellationToken ct);
        void OnDispose();
    }

    public interface IContext : IDisposable
    {
        ISignalBus SignalBus { get; }
        CancellationToken LifetimeToken { get; }
        string ScopeTag { get; }
        void RegisterView(IView view);
        void UnregisterView(IView view);
        T Resolve<T>() where T : class;
        T TryResolve<T>() where T : class;
        IContext Parent { get; }
        void RegisterPlugin(INexusPlugin plugin);
        void RemovePlugin(INexusPlugin plugin);
    }

    public interface ISignalBus
    {
        /// <summary>
        /// Enumerates all signal→handler registrations discovered through configuration
        /// and attribute scanning. Key is the signal type; value is the registered handlers.
        /// Empty until the owning context has been configured.
        /// </summary>
        IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers { get; }

        /// <summary>Dispatches immediately on the current thread.</summary>
        void Fire<T>(T signal) where T : struct;
        /// <summary>Dispatches asynchronously and waits for the handler chain.</summary>
        ValueTask FireAsync<T>(T signal) where T : struct;
        /// <summary>Dispatches from any thread by marshaling to the signal bus queue.</summary>
        void FireThreadSafe<T>(T signal) where T : struct;
        /// <summary>Defers dispatch until the next frame.</summary>
        void FireNextFrame<T>(T signal) where T : struct;

        /// <summary>
        /// Fires a signal asynchronously with a timeout. If the command chain does not complete
        /// within the specified milliseconds, a <see cref="OperationCanceledException"/> is thrown.
        /// </summary>
        /// <param name="signal">The signal data.</param>
        /// <param name="timeoutMilliseconds">Maximum execution time in milliseconds.</param>
        ValueTask FireAsyncWithTimeout<T>(T signal, int timeoutMilliseconds) where T : struct;

        /// <summary>
        /// Fires a signal asynchronously without awaiting the result. Errors are logged by default;
        /// provide an <paramref name="onError"/> callback for custom error handling.
        /// </summary>
        ValueTask FireAsyncAndForget<T>(T signal, Action<Exception> onError = null) where T : struct;

        ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct;
        ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct;

        /// <summary>Returns true when at least one command handler is registered for the signal type.</summary>
        bool HasCommandHandler(Type signalType);
        /// <summary>Generic form of <see cref="HasCommandHandler(Type)"/>.</summary>
        bool HasCommandHandler<TSignal>() where TSignal : struct;
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

    public class NexusSyncAsyncMismatchException : Exception
    {
        public NexusSyncAsyncMismatchException(string message) : base(message) { }
    }
}
