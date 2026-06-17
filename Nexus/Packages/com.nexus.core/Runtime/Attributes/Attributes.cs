using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public enum ExecutionMode
    {
        Sequential = 0,
        Concurrent = 1,
        Exclusive = 2,
        Composite = 3
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    [Preserve]
    public sealed class SignalHandlerAttribute : Attribute
    {
        public Type SignalType { get; }
        public int Priority { get; set; }
        public ExecutionMode Mode { get; set; } = ExecutionMode.Sequential;

        public SignalHandlerAttribute(Type signalType)
        {
            SignalType = signalType;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    [Preserve]
    public sealed class CompositeSignalHandlerAttribute : Attribute
    {
        public Type[] SignalTypes { get; }
        public bool OneShot { get; set; } = false;
        public int Priority { get; set; } = 0;

        public CompositeSignalHandlerAttribute(params Type[] signalTypes)
        {
            SignalTypes = signalTypes;
        }
    }

    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class CrossContextAttribute : Attribute
    {
        public string ScopeTag { get; set; }

        public CrossContextAttribute() { }

        public CrossContextAttribute(string scopeTag)
        {
            ScopeTag = scopeTag;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class LiveReloadAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [Preserve]
    public sealed class InjectAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MediatorAttribute : Attribute
    {
        public Type MediatorType { get; }

        public MediatorAttribute(Type mediatorType)
        {
            MediatorType = mediatorType;
        }
    }
}
