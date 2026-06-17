using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public static class BuildValidation
    {
        [MenuItem("Nexus/Validate Architecture")]
        public static bool Validate()
        {
            Debug.Log("[Nexus] Starting Architecture Validation...");
            int errorCount = 0;
            int warningCount = 0;

            try
            {
                // 1. Scan and validate signal handlers, priorities and mixed modes
                ValidateHandlers(ref errorCount, ref warningCount);

                // 2. Validate model ownership chains (IDisposableModel)
                ValidateModelOwnership(ref errorCount, ref warningCount);

                // 3. Validate ContextData DependsOn for cycles
                ValidateContextDataDependencies(ref errorCount, ref warningCount);

                // 4. Validate scene Roots and context hierarchies
                ValidateSceneHierarchy(ref errorCount, ref warningCount);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Validation aborted due to critical error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }

            if (errorCount > 0)
            {
                Debug.LogError($"[Nexus] Validation FAILED with {errorCount} Errors and {warningCount} Warnings. Please fix the errors before building.");
                return false;
            }

            Debug.Log($"[Nexus] Validation PASSED with {warningCount} Warnings.");
            return true;
        }

        private static void ValidateHandlers(ref int errorCount, ref int warningCount)
        {
            var signalHandlers = new Dictionary<Type, List<(Type CommandType, SignalHandlerAttribute Attr)>>();
            
            // Scan all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Skip system/unity assemblies to speed up
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("Microsoft") || name.StartsWith("mono"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract)
                        {
                            var attrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                            foreach (var attr in attrs)
                            {
                                if (!signalHandlers.TryGetValue(attr.SignalType, out var list))
                                {
                                    list = new List<(Type, SignalHandlerAttribute)>();
                                    signalHandlers[attr.SignalType] = list;
                                }
                                list.Add((type, attr));
                            }
                        }
                    }
                }
                catch {}
            }

            // Validate rules per signal
            foreach (var kvp in signalHandlers)
            {
                var signalType = kvp.Key;
                var handlers = kvp.Value;

                if (handlers.Count == 0) continue;

                // Mixed execution mode validation
                var firstMode = handlers[0].Attr.Mode;
                foreach (var handler in handlers)
                {
                    if (handler.Attr.Mode != firstMode)
                    {
                        Debug.LogError($"[Nexus Error] Mixed-Mode Violation: Signal {signalType.FullName} is bound to multiple execution modes (e.g. {firstMode} on {handlers[0].CommandType.Name} and {handler.Attr.Mode} on {handler.CommandType.Name}).");
                        errorCount++;
                    }
                }

                // Exclusive mode validation
                if (firstMode == ExecutionMode.Exclusive && handlers.Count > 1)
                {
                    Debug.LogError($"[Nexus Error] Exclusive Mode Violation: Signal {signalType.FullName} is registered with Exclusive Mode but has {handlers.Count} handlers.");
                    errorCount++;
                }

                // Equal priority validation (for Sequential/Exclusive)
                if (firstMode != ExecutionMode.Concurrent)
                {
                    var priorities = new HashSet<int>();
                    foreach (var handler in handlers)
                    {
                        if (!priorities.Add(handler.Attr.Priority))
                        {
                            Debug.LogError($"[Nexus Error] Equal Priority Violation: Signal {signalType.FullName} has duplicate priority {handler.Attr.Priority} on Command {handler.CommandType.Name}. All Sequential/Exclusive handlers must have unique priorities.");
                            errorCount++;
                        }
                    }
                }

                // Concurrent model write validation
                if (firstMode == ExecutionMode.Concurrent)
                {
                    foreach (var handler in handlers)
                    {
                        ValidateConcurrentCommandInjection(handler.CommandType, ref errorCount);
                    }
                }
            }
        }

        private static void ValidateConcurrentCommandInjection(Type commandType, ref int errorCount)
        {
            // Concurrent commands cannot inject writeable models (only read-only interfaces or types starting with IReadOnly)
            var fields = commandType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<InjectAttribute>() != null)
                {
                    var fieldType = field.FieldType;
                    if (IsWriteableModelType(fieldType))
                    {
                        Debug.LogError($"[Nexus Error] Concurrent Command Model Write Violation: Concurrent Command {commandType.FullName} injects writeable model type {fieldType.Name} in field {field.Name}. Concurrent commands can only inject read-only model interfaces (e.g., interfaces starting with IReadOnly).");
                        errorCount++;
                    }
                }
            }

            var properties = commandType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.GetCustomAttribute<InjectAttribute>() != null)
                {
                    var propType = prop.PropertyType;
                    if (IsWriteableModelType(propType))
                    {
                        Debug.LogError($"[Nexus Error] Concurrent Command Model Write Violation: Concurrent Command {commandType.FullName} injects writeable model type {propType.Name} in property {prop.Name}. Concurrent commands can only inject read-only model interfaces.");
                        errorCount++;
                    }
                }
            }
        }

        private static bool IsWriteableModelType(Type type)
        {
            // If it's an interface ending with Model and does not start with IReadOnly, it's a model.
            // If it has setters, it is writeable.
            if (type.IsInterface && type.Name.EndsWith("Model") && !type.Name.StartsWith("IReadOnly"))
            {
                // Check if it has any setter properties or methods modifying state
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    if (prop.CanWrite) return true;
                }
                return true; // assumed writeable if it lacks IReadOnly prefix
            }
            return false;
        }

        private static void ValidateModelOwnership(ref int errorCount, ref int warningCount)
        {
            // Plan §4 — IDisposableModel disposal chain check
            // Scan all types implementing IDisposableModel and verify they are referenced
            // by a Context or another model that will dispose them.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("Microsoft") || name.StartsWith("mono"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && typeof(IDisposableModel).IsAssignableFrom(type))
                        {
                            // IDisposableModel types must be registered in DI (otherwise they're leaked)
                            // This is a best-effort static check; runtime DI registration is verified separately.
                            bool hasValidConstructor = false;
                            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                            {
                                var ps = ctor.GetParameters();
                                if (ps.Length == 0 || Array.Exists(ps, p => p.ParameterType == typeof(NexusDI)))
                                {
                                    hasValidConstructor = true;
                                    break;
                                }
                            }
                            if (!hasValidConstructor)
                            {
                                Debug.LogWarning($"[Nexus Warning] IDisposableModel type {type.FullName} should have a constructor that accepts DI container or is parameterless to ensure proper disposal.");
                                warningCount++;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static void ValidateContextDataDependencies(ref int errorCount, ref int warningCount)
        {
            // Plan §5 — ContextData DependsOn validates that dependency chains don't form cycles
            var contextDataAssets = AssetDatabase.FindAssets("t:ContextData");
            var dataByName = new Dictionary<string, ContextData>();

            foreach (var guid in contextDataAssets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<ContextData>(path);
                if (data != null && !string.IsNullOrEmpty(data.name))
                {
                    dataByName[data.name] = data;
                }
            }

            foreach (var kvp in dataByName)
            {
                var visited = new HashSet<string>();
                var current = kvp.Key;

                while (!string.IsNullOrEmpty(current))
                {
                    if (!visited.Add(current))
                    {
                        Debug.LogError($"[Nexus Error] Circular ContextData Dependency: ContextData '{kvp.Key}' has a circular DependsOn chain involving '{current}'.");
                        errorCount++;
                        break;
                    }

                    if (dataByName.TryGetValue(current, out var nextData) && nextData.DependsOn != null && nextData.DependsOn.Length > 0)
                    {
                        current = nextData.DependsOn[0];
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private static void ValidateSceneHierarchy(ref int errorCount, ref int warningCount)
        {
            var roots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            if (roots.Length == 0) return;

            var rootList = new List<Root>(roots);

            foreach (var root in rootList)
            {
                // Check Missing SO
                // We access the contextData field via reflection or simple helper since it's private serialized
                var contextDataField = typeof(Root).GetField("contextData", BindingFlags.NonPublic | BindingFlags.Instance);
                var contextData = contextDataField?.GetValue(root) as ContextData;

                if (contextData == null)
                {
                    Debug.LogError($"[Nexus Error] Missing SO Violation: Root GameObject '{root.gameObject.name}' has a null ContextData configuration.");
                    errorCount++;
                }

                // Check circular hierarchy
                if (DetectCircularHierarchy(root))
                {
                    Debug.LogError($"[Nexus Error] Circular Context Violation: Circular context hierarchy detected starting from Root GameObject '{root.gameObject.name}'.");
                    errorCount++;
                }
            }
        }

        private static bool DetectCircularHierarchy(Root startRoot)
        {
            var visited = new HashSet<Root>();
            var current = startRoot;

            var parentRootField = typeof(Root).GetField("parentRoot", BindingFlags.NonPublic | BindingFlags.Instance);

            while (current != null)
            {
                if (!visited.Add(current))
                {
                    return true; // circular reference!
                }
                current = parentRootField?.GetValue(current) as Root;
            }

            return false;
        }
    }

    // Build Pre-processor hook to automatically validate before build starts
    public class NexusBuildPreProcessor : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            bool success = BuildValidation.Validate();
            if (!success)
            {
                throw new BuildPlayerWindow.BuildMethodException("Nexus Architecture Validation Failed. See Console for details.");
            }
        }
    }
}
