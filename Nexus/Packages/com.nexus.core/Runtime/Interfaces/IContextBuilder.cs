using System;

namespace Nexus.Core
{
    public interface ICommandBindingBuilder<TSignal> where TSignal : struct
    {
        ICommandBindingBuilder<TSignal> To<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class;
        ICommandBindingBuilder<TSignal> ToAsync<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class;
        /// <summary>Marks the next To/ToAsync as one-shot: it fires once then is unregistered.</summary>
        ICommandBindingBuilder<TSignal> Once();
    }

    /// <summary>
    /// Model registration role. Depend on this (instead of the whole
    /// <see cref="IContextBuilder"/>) in installers that only register models.
    /// </summary>
    public interface IModelBinder
    {
        /// <summary>
        /// Bind models inside a lifecycle's OnConfigure phase.
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
    }

    /// <summary>
    /// Service registration role — eager, lazy, and interfaces-and-self service bindings.
    /// </summary>
    public interface IServiceBinder
    {
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
        /// Binds an INexusService under all of its user-defined interfaces AND its concrete
        /// type as ONE shared singleton, and registers it for eager initialization during
        /// InitializeServicesAsync. Use for services whose InitializeAsync must run at
        /// startup and that are consumed both by interface and by concrete type.
        /// </summary>
        void BindServiceInterfacesAndSelfTo<TImplementation>()
            where TImplementation : class, INexusService;

        /// <summary>
        /// Binds a lazy service interface to its implementation. Unlike BindService, the service
        /// is NOT eagerly constructed during InitializeServicesAsync — it is resolved on first access
        /// via LazyInjection<T> or direct Resolve<T> call. Implements INexusService for lifecycle
        /// management; InitializeAsync is called during the lazy-service initialization window.
        /// </summary>
        void BindLazyService<TInterface, TImplementation>()
            where TImplementation : class, TInterface, INexusService;

        /// <summary>Binds a self-referencing lazy service as a singleton.</summary>
        void BindLazyService<TImplementation>()
            where TImplementation : class, INexusService;
    }

    /// <summary>
    /// General type registration role: plain, named, instance, polymorphic, cross-boundary
    /// and convention-scanned bindings, plus the strict-injection switch.
    /// </summary>
    public interface ITypeBinder
    {
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
        /// Binds an implementation as cross-boundary — visible to descendant (child/grandchild)
        /// contexts through explicit <see cref="IContext.ResolveCrossBoundary{T}"/> resolution
        /// (StrangeIoC-style <c>crossContextInjectionBinder</c>). Registered as a singleton in
        /// the current context AND marked for parent-chain resolution in descendant contexts.
        /// </summary>
        void BindCrossBoundary<TInterface, TImplementation>()
            where TImplementation : class, TInterface;
        /// <summary>Binds a self-referencing type as cross-boundary.</summary>
        void BindCrossBoundary<T>() where T : class;

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

        /// <summary>
        /// Automatically binds a concrete implementation class under all of its implemented interfaces
        /// (excluding system/framework interfaces) AND under its own concrete type as a shared singleton.
        /// </summary>
        void BindInterfacesAndSelfTo<TImplementation>(bool isSingleton = true) where TImplementation : class;

        /// <summary>
        /// Scans an assembly and automatically binds matching concrete types using the specified predicate.
        /// </summary>
        void BindAllClassesMatching(System.Reflection.Assembly assembly, Func<Type, bool> predicate, bool isSingleton = true);

        void EnableStrictInjection();
    }

    /// <summary>
    /// Command registration role: signal→command wiring, including one-shot and fluent forms.
    /// </summary>
    public interface ICommandBinder
    {
        void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct;
        void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct;

        /// <summary>Registers a one-shot command (Strange-style <c>.Once()</c>): fires once then unregisters.</summary>
        void BindCommandOnce<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct;
        /// <summary>Registers a one-shot async command: fires once then unregisters.</summary>
        void BindAsyncCommandOnce<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct;

        ICommandBindingBuilder<TSignal> BindSignal<TSignal>() where TSignal : struct;
    }

    /// <summary>
    /// The configuration surface handed to <see cref="IContextLifecycle.OnConfigure"/>.
    /// Composed from the narrow registration roles above: depend on
    /// <see cref="IModelBinder"/>, <see cref="IServiceBinder"/>, <see cref="ITypeBinder"/> or
    /// <see cref="ICommandBinder"/> in installers that only need one of them.
    /// </summary>
    public interface IContextBuilder : IModelBinder, IServiceBinder, ITypeBinder, ICommandBinder
    {
        /// <summary>
        /// Dispatches a signal through the context's bus.
        /// </summary>
        /// <remarks>
        /// Firing is not a registration concern and does not belong on the builder; it exists
        /// here only for backward compatibility. Resolve <see cref="ISignalPublisher"/> (or
        /// <see cref="ISignalBus"/>) and fire from a lifecycle's initialize/start phase instead —
        /// signals fired during OnConfigure reach only handlers registered before that point.
        /// </remarks>
        [Obsolete("Fire from a lifecycle's OnInitializeAsync/OnStartAsync via ISignalPublisher instead; a builder should only register bindings.", error: false)]
        void Fire<T>(T signal) where T : struct;
    }
}
