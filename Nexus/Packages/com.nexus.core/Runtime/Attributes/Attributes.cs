using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>Defines how a command handler executes relative to other handlers for the same signal.</summary>
    [Preserve]
    public enum ExecutionMode
    {
        /// <summary>Handlers run in priority order, one at a time.</summary>
        Sequential = 0,
        /// <summary>Handlers run in parallel (typically for I/O-bound operations).</summary>
        Concurrent = 1,
        /// <summary>Only one handler is allowed for this signal.</summary>
        Exclusive = 2,
        /// <summary>Handler waits for multiple signals (fan-in) before executing.</summary>
        Composite = 3
    }

    /// <summary>Marks a command class to handle a specific signal type. Supports multiple signals per command.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    [Preserve]
    public sealed class SignalHandlerAttribute : Attribute
    {
        /// <summary>The signal type this command handles.</summary>
        public Type SignalType { get; }
        /// <summary>
        /// Execution priority.
        /// <b>Higher values run first.</b>
        /// </summary>
        public int Priority { get; set; }
        /// <summary>Execution mode for this handler.</summary>
        public ExecutionMode Mode { get; set; } = ExecutionMode.Sequential;
        /// <summary>If true, the handler runs for the first matching signal only and is then consumed.</summary>
        public bool OneShot { get; set; }
        /// <summary>
        /// Forces the sync/async classification instead of deriving it from the command's
        /// interfaces. Null (default) = derive. Used when a command implements both the
        /// sync and async command interfaces.
        /// </summary>
        public bool? IsAsync { get; set; }

        /// <summary>Marks a command to handle the specified signal type.</summary>
        /// <param name="signalType">The signal struct type.</param>
        public SignalHandlerAttribute(Type signalType)
        {
            SignalType = signalType;
        }
    }

    /// <summary>Marks a command as a composite trigger that fires only after all specified signal types have been received.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [Preserve]
    public sealed class CompositeSignalHandlerAttribute : Attribute
    {
        /// <summary>The signal types that must all be received before the command fires.</summary>
        public Type[] SignalTypes { get; }
        /// <summary>If true, the composite trigger fires only once and is then removed.</summary>
        public bool OneShot { get; set; } = false;
        /// <summary>
        /// Execution priority.
        /// <b>Higher values run first.</b>
        /// </summary>
        public int Priority { get; set; } = 0;
        /// <summary>
        /// Forces the sync/async classification instead of deriving it from the command's
        /// interfaces. Null (default) = derive.
        /// </summary>
        public bool? IsAsync { get; set; }

        /// <summary>Marks a command as a composite trigger requiring multiple signals.</summary>
        /// <param name="signalTypes">The signal types required for the trigger.</param>
        public CompositeSignalHandlerAttribute(params Type[] signalTypes)
        {
            SignalTypes = signalTypes;
        }
    }

    /// <summary>
    /// Marks a signal or model as visible across context boundaries.
    /// Optionally restricts to a specific scope tag.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class CrossContextAttribute : Attribute
    {
        /// <summary>Optional scope tag to restrict cross-context visibility.</summary>
        public string ScopeTag { get; set; }

        /// <summary>Marks a type as cross-context visible for all scopes.</summary>
        public CrossContextAttribute() { }

        /// <summary>Marks a type as cross-context visible only within the specified scope.</summary>
        /// <param name="scopeTag">The scope tag to restrict visibility to.</param>
        public CrossContextAttribute(string scopeTag)
        {
            ScopeTag = scopeTag;
        }
    }

    /// <summary>
    /// Marks a class or field for live reload support during Play Mode in the Editor.
    /// When applied to a field, changes are synced without restarting the context.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class LiveReloadAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a constructor, field, property, or method for dependency injection by the Nexus DI container.
    /// Supports an optional binding name (Strange-style named injection) so multiple
    /// implementations of the same interface can coexist:
    /// <code>[Inject(Name = "primary")] public IStorage Storage { get; set; }</code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class InjectAttribute : Attribute
    {
        /// <summary>
        /// Optional binding name discriminating between multiple registrations of the
        /// same type (see <c>NexusDI.Bind(name)</c> / <c>IContextBuilder.Bind(name)</c>).
        /// Null/empty = the default (unnamed) binding.
        /// </summary>
        public string Name { get; set; }

        public InjectAttribute() { }

        /// <param name="name">Binding name this dependency resolves against.</param>
        public InjectAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Marks a parameterless method to run once, immediately after all injections for the
    /// instance have been applied (Strange-style <c>[PostConstruct]</c>). Dependencies are
    /// guaranteed non-null here. Methods run in ascending <see cref="Order"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class PostConstructAttribute : Attribute
    {
        /// <summary>Execution order; lower values run first. Defaults to 0.</summary>
        public int Order { get; set; } = 0;
    }

    /// <summary>
    /// Marks a parameterless method to run when the owning <see cref="NexusDI"/> container
    /// disposes the instance (Strange-style <c>[Deconstruct]</c>). Dependencies are still
    /// non-null here. Methods run in ascending <see cref="Order"/> and are invoked BEFORE
    /// the instance's <see cref="IDisposable.Dispose"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class DeconstructAttribute : Attribute
    {
        /// <summary>Execution order; lower values run first. Defaults to 0.</summary>
        public int Order { get; set; } = 0;
    }

    /// <summary>
    /// Marks the preferred constructor for dependency injection (Strange-style
    /// <c>[Construct]</c> alias). Nexus also accepts <c>[Inject]</c> on constructors; both
    /// spellings select the constructor explicitly. When neither is present, the
    /// parameterless constructor is used when available.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = true)]
        [Preserve]
        public sealed class ConstructAttribute : Attribute
        {
        }

    /// <summary>
    /// Marks an [Inject] field, property, or method parameter as optional.
    /// When strict injection mode is enabled, optional members are silently skipped if the
    /// dependency is not registered (no exception thrown). When the dependency IS registered,
    /// optional members are injected exactly like [Inject] members — so a singleton service
    /// must still not capture a transient (non-singleton) optional dependency: captive-
    /// dependency validation reports it, because a bound optional is indistinguishable from
    /// a required dependency at runtime.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class OptionalInjectAttribute : Attribute
    {
    }

    /// <summary>
    /// Associates a <see cref="View"/>-derived class with its <see cref="Mediator{TView}"/> type.
    /// When a View with this attribute is bound, the mediator is automatically created and wired.
    ///
    /// Optional <see cref="Abstraction"/> enables interface-based mediator resolution (StrangeIoC-style
    /// <c>ToAbstraction&lt;IMediator&gt;()</c>): when set, the mediator is resolved through the
    /// specified interface/abstract type instead of the concrete <paramref name="mediatorType"/>.
    /// The abstraction must be registered in DI (e.g. <c>builder.Bind&lt;IMediator, ConcreteMediator&gt;()</c>).
    /// Pooling still keys off the concrete <paramref name="mediatorType"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MediatorAttribute : Attribute
    {
        /// <summary>The concrete mediator type that handles this view.</summary>
        public Type MediatorType { get; }

        /// <summary>
        /// Optional abstraction type (interface or abstract class) for DI resolution.
        /// When set, the mediator is resolved through this type — the concrete
        /// <see cref="MediatorType"/> must be registered in DI under this abstraction.
        /// When null (default), the mediator is bound and resolved by concrete type.
        /// </summary>
        public Type Abstraction { get; }

        /// <summary>Associates a view with its mediator by concrete type.</summary>
        /// <param name="mediatorType">The <see cref="Mediator{TView}"/> type.</param>
        public MediatorAttribute(Type mediatorType)
        {
            MediatorType = mediatorType;
            Abstraction = null;
        }

        /// <summary>
        /// Associates a view with its mediator by concrete type AND abstraction interface.
        /// The mediator is resolved through <paramref name="abstractionType"/> in DI, enabling
        /// interface-based binding (StrangeIoC-style <c>ToAbstraction&lt;IMediator&gt;()</c>).
        /// Pooling still keys off the concrete <paramref name="mediatorType"/>.
        /// </summary>
        /// <param name="mediatorType">The concrete <see cref="Mediator{TView}"/> type.</param>
        /// <param name="abstractionType">
        /// The abstraction type (interface or abstract class) to resolve the mediator through.
        /// Must be registered in DI, e.g. <c>builder.Bind&lt;TAbstraction, TConcrete&gt;()</c>.
        /// </param>
        public MediatorAttribute(Type mediatorType, Type abstractionType)
        {
            if (abstractionType == null)
                throw new ArgumentNullException(nameof(abstractionType));
            if (!abstractionType.IsAssignableFrom(mediatorType))
                throw new ArgumentException(
                    $"Mediator type '{mediatorType.Name}' must implement or extend abstraction type '{abstractionType.Name}'.");

            MediatorType = mediatorType;
            Abstraction = abstractionType;
        }
    }

    /// <summary>
    /// Specifies an execution timeout for an async command.
    /// If the command does not complete within the specified milliseconds,
    /// the SignalBus cancels the execution via CancellationToken.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [Preserve]
    public sealed class CommandTimeoutAttribute : Attribute
    {
        /// <summary>Timeout in milliseconds.</summary>
        public int Milliseconds { get; }

        /// <param name="milliseconds">Maximum execution time in milliseconds.</param>
        public CommandTimeoutAttribute(int milliseconds)
        {
            Milliseconds = milliseconds;
        }
    }

    /// <summary>
    /// Specifies that a context lifecycle depends on another context lifecycle by its scope name.
    /// Helps build decentralized validation maps and prevents Git merge conflicts in monolit ContextData files.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    [Preserve]
    public sealed class ContextDependsOnAttribute : Attribute
    {
        public string DependencyScopeName { get; }
        public ContextDependsOnAttribute(string dependencyScopeName)
        {
            DependencyScopeName = dependencyScopeName;
        }
    }

    /// <summary>
    /// Marks a service as a stub implementation that logs to console instead of using
    /// a real SDK. BuildValidation warns about stub services so they are not shipped
    /// to production by accident.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [Preserve]
    public sealed class StubServiceAttribute : Attribute
    {
        /// <summary>Optional description of what real service should replace this stub.</summary>
        public string Description { get; }

        public StubServiceAttribute(string description = "")
        {
            Description = description;
        }
    }
}
