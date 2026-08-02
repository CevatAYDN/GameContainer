using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Decorates a command class to automatically bind it to a signal during assembly scanning.
    /// Eliminates manual <c>builder.BindCommand&lt;TSignal, TCommand&gt;()</c> registration in ContextBuilder.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    [Preserve]
    public sealed class RegisterCommandAttribute : Attribute
    {
        /// <summary>The signal struct type that triggers this command.</summary>
        public Type SignalType { get; }

        /// <summary>Execution mode for async dispatch (<see cref="ExecutionMode.Sequential"/> vs <see cref="ExecutionMode.Concurrent"/>).</summary>
        public ExecutionMode Mode { get; set; } = ExecutionMode.Sequential;

        /// <summary>Execution priority (higher priority runs first).</summary>
        public int Priority { get; set; } = 0;

        /// <summary>Whether this command runs asynchronously.</summary>
        public bool IsAsync { get; set; } = false;

        /// <summary>Whether this command unregisters automatically after its first execution.</summary>
        public bool OneShot { get; set; } = false;

        public RegisterCommandAttribute(Type signalType)
        {
            SignalType = signalType ?? throw new ArgumentNullException(nameof(signalType));
        }
    }

    /// <summary>
    /// Decorates a composite command class to automatically bind it to multiple signal triggers during assembly scanning.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [Preserve]
    public sealed class RegisterCompositeCommandAttribute : Attribute
    {
        /// <summary>The array of signal types that must all fire before this command executes.</summary>
        public Type[] SignalTypes { get; }

        /// <summary>Whether this composite command unregisters automatically after its first execution.</summary>
        public bool OneShot { get; set; } = false;

        /// <summary>Execution priority (higher priority runs first).</summary>
        public int Priority { get; set; } = 0;

        /// <summary>Whether this composite command runs asynchronously.</summary>
        public bool IsAsync { get; set; } = false;

        public RegisterCompositeCommandAttribute(params Type[] signalTypes)
        {
            if (signalTypes == null || signalTypes.Length == 0)
                throw new ArgumentException("At least one signal type must be specified for composite command registration.", nameof(signalTypes));
            SignalTypes = signalTypes;
        }
    }
}
