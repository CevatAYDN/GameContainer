// Host-only shims so Reflex's core DI compiles on .NET without UnityEngine.
// They cover exactly the members the compiled core files reference:
//   ContainerBuilder -> UnityEngine.Assertions.Assert
//   TypeInfoCache    -> UnityEngine.Pool.ListPool<T>
//   Diagnosis        -> UnityEngine.Application.dataPath (REFLEX_DEBUG-only code, still compiled)
//   Attributes       -> JetBrains.Annotations (shipped by Unity, not by netstandard)
//   Container        -> Reflex.Logging.ReflexLogger (the Unity logger reads a ScriptableObject from Resources;
//                       the Unity player run sets its LogLevel to Warning, so neither configuration logs on Dispose)
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    // internal: the benchmark compilation also references the harness stubs' UnityEngine.Object.
    internal class Object { }

    public static class Application
    {
        public static string dataPath => AppContext.BaseDirectory;
    }
}

namespace UnityEngine.Assertions
{
    public static class Assert
    {
        public static void IsNotNull(object value, string message = null) { if (value == null) throw new InvalidOperationException(message ?? "Assert.IsNotNull failed"); }
        public static void IsTrue(bool condition, string message = null) { if (!condition) throw new InvalidOperationException(message ?? "Assert.IsTrue failed"); }
        public static void IsFalse(bool condition, string message = null) { if (condition) throw new InvalidOperationException(message ?? "Assert.IsFalse failed"); }
    }
}

namespace UnityEngine.Pool
{
    public readonly struct PooledObject<T> : IDisposable where T : class
    {
        private readonly T _value;
        private readonly Action<T> _release;
        public PooledObject(T value, Action<T> release) { _value = value; _release = release; }
        public void Dispose() => _release(_value);
    }

    public static class ListPool<T>
    {
        private static readonly Stack<List<T>> s_pool = new();
        public static PooledObject<List<T>> Get(out List<T> value)
        {
            value = s_pool.Count > 0 ? s_pool.Pop() : new List<T>();
            return new PooledObject<List<T>>(value, Release);
        }
        private static void Release(List<T> list) { list.Clear(); s_pool.Push(list); }
    }
}

namespace Reflex.Logging
{
    internal static class ReflexLogger
    {
        public static void UpdateLogLevel(LogLevel logLevel) { }
        public static void Log(object message, LogLevel logLevel, UnityEngine.Object context = null) { }
    }
}

namespace JetBrains.Annotations
{
    [Flags]
    public enum ImplicitUseKindFlags { Default = Access | Assign | InstantiatedWithFixedConstructorSignature, Access = 1, Assign = 2, InstantiatedWithFixedConstructorSignature = 4, InstantiatedNoFixedConstructorSignature = 8 }
    [Flags]
    public enum ImplicitUseTargetFlags { Default = Itself, Itself = 1, Members = 2, WithInheritors = 4, WithMembers = Itself | Members }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class MeansImplicitUseAttribute : Attribute
    {
        public MeansImplicitUseAttribute() { }
        public MeansImplicitUseAttribute(ImplicitUseKindFlags useKindFlags) { }
        public MeansImplicitUseAttribute(ImplicitUseTargetFlags targetFlags) { }
        public MeansImplicitUseAttribute(ImplicitUseKindFlags useKindFlags, ImplicitUseTargetFlags targetFlags) { }
    }
}
