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
        /// <summary>Execution priority; lower values run first.</summary>
        public int Priority { get; set; }
        /// <summary>Execution mode for this handler.</summary>
        public ExecutionMode Mode { get; set; } = ExecutionMode.Sequential;

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
        /// <summary>Execution priority; lower values run first.</summary>
        public int Priority { get; set; } = 0;

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
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class InjectAttribute : Attribute
    {
    }

    /// <summary>
    /// Associates a <see cref="View"/>-derived class with its <see cref="Mediator{TView}"/> type.
    /// When a View with this attribute is bound, the mediator is automatically created and wired.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MediatorAttribute : Attribute
    {
        /// <summary>The mediator type that handles this view.</summary>
        public Type MediatorType { get; }

        /// <summary>Associates a view with its mediator.</summary>
        /// <param name="mediatorType">The <see cref="Mediator{TView}"/> type.</param>
        public MediatorAttribute(Type mediatorType)
        {
            MediatorType = mediatorType;
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
}
