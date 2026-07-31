// Minimal compile-time stubs so the pure-C# Nexus runtime compiles outside Unity.
// Only the small Unity surface the runtime actually touches is stubbed here.

using System;

namespace UnityEngine
{
    /// <summary>Unity log severity — a no-op enum outside Unity.</summary>
    public enum LogType
    {
        Error = 0,
        Assert = 1,
        Warning = 2,
        Log = 3,
        Exception = 4
    }

    /// <summary>Unity log callback signature.</summary>
    public delegate void LogCallback(string condition, string stackTrace, LogType type);

    /// <summary>Unity application surface used by ErrorCollection's log hook.</summary>
    public static class Application
    {
        public static event LogCallback logMessageReceivedThreaded;
    }

    /// <summary>Unity runtime init load type — no-op outside Unity.</summary>
    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad = 0,
        BeforeSceneLoad = 1,
        AfterAssembliesLoaded = 2,
        BeforeSplashScreen = 3,
        SubsystemRegistration = 4
    }

    /// <summary>Unity runtime-init marker — a no-op outside Unity.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    /// <summary>Unity Debug — a no-op outside Unity.</summary>
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
    }

    /// <summary>Unity Time — a no-op outside Unity.</summary>
    public static class Time
    {
        public static float time => 0f;
        public static float deltaTime => 0.0166f;
        public static float unscaledDeltaTime => 0.0166f;
        public static double realtimeSinceStartupAsDouble => 0d;
        public static int frameCount => 0;
    }

    /// <summary>Unity stack-trace helper used by ErrorCollection — a no-op outside Unity.</summary>
    public static class StackTraceUtility
    {
        public static string ExtractStackTrace() => string.Empty;
        public static string ExtractStringFromException(Exception exception) => exception?.ToString() ?? string.Empty;
    }

    /// <summary>Base class for Unity objects — a no-op outside Unity.</summary>
    public class Object
    {
        public string name;
    }

    /// <summary>Unity MonoBehaviour base — a no-op outside Unity.</summary>
    public class MonoBehaviour : Object
    {
        public bool enabled;
        public bool isActiveAndEnabled => enabled;
    }

    /// <summary>Unity ScriptableObject base — a no-op outside Unity.</summary>
    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => null;
        public static ScriptableObject CreateInstance(Type type) => null;
    }

    /// <summary>Unity assembly introspection used by Context auto-discovery.</summary>
    public static class Assemblies
    {
        public static class CurrentAssemblies
        {
            public static System.Reflection.Assembly[] GetLoadedAssemblies() => Array.Empty<System.Reflection.Assembly>();
        }
    }

    namespace Profiling
    {
        /// <summary>Unity ProfilerMarker — a no-op outside Unity.</summary>
        public struct ProfilerMarker
        {
            public ProfilerMarker(string name) { }
            public void Begin() { }
            public void End() { }
        }

        public static class Profiler
        {
            public static long GetTotalAllocatedMemoryLong() => 0L;
            public static long GetTotalReservedMemoryLong() => 0L;
            public static long GetTotalUnusedReservedMemoryLong() => 0L;
            public static long GetMonoUsedSizeLong() => 0L;
            public static long GetMonoHeapSizeLong() => 0L;
        }
    }
}

namespace Unity.Profiling
{
    // SignalBus.cs imports this namespace; only the (guarded) ProfilerMarker usage
    // needs the type, which lives under UnityEngine.Profiling above.
}

namespace UnityEngine.Scripting
{
    /// <summary>Unity's link-time preservation marker — a no-op outside Unity.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
                    AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Constructor |
                    AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate |
                    AttributeTargets.Event, Inherited = false)]
    public sealed class PreserveAttribute : Attribute
    {
    }
}
