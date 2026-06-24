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
        public static bool IncludeTestAssemblies { get; set; } = false;

        public static Action<string> InfoLogger { get; set; } = UnityEngine.Debug.Log;
        public static Action<string> WarningLogger { get; set; } = UnityEngine.Debug.LogWarning;
        public static Action<string> ErrorLogger { get; set; } = UnityEngine.Debug.LogError;

        private static class Debug
        {
            public static void Log(string msg) => InfoLogger(msg);
            public static void LogWarning(string msg) => WarningLogger(msg);
            public static void LogError(string msg) => ErrorLogger(msg);
        }

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

                // 5. Validate Command state leak (Plan §6.1.1)
                ValidateCommandStateLeak(ref errorCount, ref warningCount);

                // 6. Validate Composite Trigger reachability (Plan §9.6)
                ValidateCompositeTriggerReachability(ref errorCount, ref warningCount);
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
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
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
                catch (ReflectionTypeLoadException ex)
                {
                    Debug.LogWarning($"[Nexus] Handler validation skipped assembly '{assembly.GetName().Name}': {ex.LoaderExceptions?[0]?.Message ?? ex.Message}");
                }
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

                // Generic command interface check (for performance and AOT compatibility)
                foreach (var handler in handlers)
                {
                    bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(handler.CommandType);
                    bool implementsGeneric = false;
                    if (isAsync)
                    {
                        var genericAsyncType = typeof(IAsyncCommand<>).MakeGenericType(signalType);
                        implementsGeneric = genericAsyncType.IsAssignableFrom(handler.CommandType);
                    }
                    else
                    {
                        var genericType = typeof(ICommand<>).MakeGenericType(signalType);
                        implementsGeneric = genericType.IsAssignableFrom(handler.CommandType);
                    }

                    if (!implementsGeneric)
                    {
                        Debug.LogWarning($"[Nexus Warning] Non-Generic Command Performance Risk: Command {handler.CommandType.FullName} handles signal {signalType.Name} but does not implement ICommand<{signalType.Name}> or IAsyncCommand<{signalType.Name}>. For AOT/IL2CPP compatibility and zero GC allocation on all platforms, implementing generic interfaces is highly recommended.");
                        warningCount++;
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
            if (!type.IsInterface) return false;
            if (!type.Name.EndsWith("Model", StringComparison.OrdinalIgnoreCase)) return false;
            if (type.Name.StartsWith("IReadOnly", StringComparison.OrdinalIgnoreCase)) return false;

            // Interface inheritance reflection fix: recursively scan all parent interfaces
            var allTypes = new List<Type> { type };
            allTypes.AddRange(type.GetInterfaces());

            foreach (var t in allTypes)
            {
                // Check if the interface has any settable properties (writeable indicators)
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    if (prop.CanWrite) return true;
                }

                // Check if the interface has methods that imply mutation (Set*, Update*, Modify*, Reset*, Clear*)
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    string name = m.Name;
                    if (name.StartsWith("Set", StringComparison.OrdinalIgnoreCase) || 
                        name.StartsWith("Update", StringComparison.OrdinalIgnoreCase) || 
                        name.StartsWith("Modify", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Reset", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Clear", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
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
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
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
                catch (ReflectionTypeLoadException ex)
                {
                    Debug.LogWarning($"[Nexus] Model ownership scan skipped assembly '{assembly.GetName().Name}': {ex.LoaderExceptions?[0]?.Message ?? ex.Message}");
                }
            }
        }

        private static void ValidateContextDataDependencies(ref int errorCount, ref int warningCount)
        {
            var dependenciesByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // 1. Gather from ScriptableObjects
            var contextDataAssets = AssetDatabase.FindAssets("t:ContextData");
            foreach (var guid in contextDataAssets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<ContextData>(path);
                if (data != null)
                {
                    string contextName = data.name.Replace("ContextData", "");
                    if (!dependenciesByName.TryGetValue(contextName, out var deps))
                    {
                        deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        dependenciesByName[contextName] = deps;
                    }
                    if (data.DependsOn != null)
                    {
                        foreach (var dep in data.DependsOn)
                        {
                            if (!string.IsNullOrEmpty(dep)) deps.Add(dep);
                        }
                    }
                }
            }

            // 2. Gather from IContextLifecycle Attributes (git-friendly distributed registration)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("Microsoft") || name.StartsWith("mono"))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && typeof(IContextLifecycle).IsAssignableFrom(type))
                        {
                            var attrs = type.GetCustomAttributes<ContextDependsOnAttribute>();
                            if (attrs != null)
                            {
                                string scope = type.Name;
                                if (scope.EndsWith("ContextLifecycle", StringComparison.OrdinalIgnoreCase))
                                    scope = scope.Substring(0, scope.Length - 16);
                                else if (scope.EndsWith("Lifecycle", StringComparison.OrdinalIgnoreCase))
                                    scope = scope.Substring(0, scope.Length - 9);

                                if (!dependenciesByName.TryGetValue(scope, out var deps))
                                {
                                    deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    dependenciesByName[scope] = deps;
                                }

                                foreach (var attr in attrs)
                                {
                                    if (!string.IsNullOrEmpty(attr.DependencyScopeName))
                                    {
                                        deps.Add(attr.DependencyScopeName);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            // DFS-based cycle detection across all dependencies
            foreach (var kvp in dependenciesByName)
            {
                var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (HasDependencyCycle(kvp.Key, dependenciesByName, visiting, visited, new List<string>()))
                {
                    errorCount++;
                }
            }
        }

        private static bool HasDependencyCycle(string current, Dictionary<string, HashSet<string>> dependenciesByName, HashSet<string> visiting, HashSet<string> visited, List<string> path)
        {
            if (visited.Contains(current)) return false;
            if (!visiting.Add(current))
            {
                path.Add(current);
                Debug.LogError($"[Nexus Error] Circular Context Dependency: Circular dependency chain detected involving '{current}'. Chain: {string.Join(" → ", path)}");
                return true;
            }

            path.Add(current);

            if (dependenciesByName.TryGetValue(current, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (HasDependencyCycle(dep, dependenciesByName, visiting, visited, path))
                    {
                        return true;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(current);
            visited.Add(current);
            return false;
        }

        private static void ValidateSceneHierarchy(ref int errorCount, ref int warningCount)
        {
            var roots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            if (roots.Length == 0) return;

            var rootList = new List<Root>(roots);

            foreach (var root in rootList)
            {
                if (root.ContextData == null)
                {
                    Debug.LogError($"[Nexus Error] Missing SO Violation: Root GameObject '{root.gameObject.name}' has a null ContextData configuration.");
                    errorCount++;
                }

                if (TryDetectCircularHierarchy(root, out var chain))
                {
                    var chainStr = string.Join(" → ", chain.ConvertAll(r => $"{r.gameObject.name}"));
                    Debug.LogError($"[Nexus Error] Circular Context Violation: Circular hierarchy detected. Chain: {chainStr}. Fix: Ensure parentRoot references do not form a cycle in the scene.");
                    errorCount++;
                }
            }
        }

        private static bool TryDetectCircularHierarchy(Root startRoot, out List<Root> chain)
        {
            var visited = new List<Root>();
            var visitedSet = new HashSet<Root>();
            var current = startRoot;

            while (current != null)
            {
                if (!visitedSet.Add(current))
                {
                    var cycleStart = visited.IndexOf(current);
                    chain = visited.GetRange(cycleStart, visited.Count - cycleStart);
                    return true;
                }
                visited.Add(current);
                current = current.ParentRoot;
            }

            chain = null;
            return false;
        }

        /// <summary>
        /// Plan §6.1.1 — Command State Leak Validation:
        /// Warns about commands with mutable, non-injected, non-IResettable state fields.
        /// If a command has such fields and does not implement IResettable, it may leak state
        /// across pooled reuses.
        /// </summary>
        private static void ValidateCommandStateLeak(ref int errorCount, ref int warningCount)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("Microsoft") || name.StartsWith("mono"))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;
                        bool isCommand = typeof(ICommand).IsAssignableFrom(type) || typeof(IAsyncCommand).IsAssignableFrom(type);
                        if (!isCommand) continue;

                        bool implementsResettable = typeof(IResettable).IsAssignableFrom(type);

                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var field in fields)
                        {
                            // Skip [Inject]-annotated fields (auto-cleared by CommandPool)
                            if (field.GetCustomAttribute<InjectAttribute>() != null)
                                continue;

                            // Skip auto-property backing fields whose property has [Inject]
                            if (IsAutoPropertyWithInject(field, type))
                                continue;

                            // Skip readonly/const fields (they can't leak)
                            if (field.IsInitOnly || field.IsLiteral)
                                continue;

                            // Skip value types that are primitives (int, bool, etc. — trivially reset)
                            if (field.FieldType.IsPrimitive)
                                continue;

                            // A non-injected, non-readonly, non-primitive mutable field in a command
                            // that does not implement IResettable is a potential state leak
                            if (!implementsResettable)
                            {
                                string strictQA = System.Environment.GetEnvironmentVariable("NEXUS_STRICT_QA_LEAK");
                                bool errorOnLeak = !string.IsNullOrEmpty(strictQA) && (strictQA == "1" || strictQA.Equals("true", StringComparison.OrdinalIgnoreCase));
                                if (errorOnLeak)
                                {
                                    Debug.LogError($"[Nexus Error] Command State Leak Violation: Command {type.FullName} has non-injected mutable field '{field.Name}' ({field.FieldType.Name}) but does not implement IResettable.");
                                    errorCount++;
                                }
                                else
                                {
                                    Debug.LogWarning($"[Nexus Warning] Command State Leak Risk: Command {type.FullName} has non-injected mutable field '{field.Name}' ({field.FieldType.Name}) but does not implement IResettable. This field may retain state across pooled reuses. Fix: Implement IResettable and clear state in Reset(), or mark the field as readonly.");
                                    warningCount++;
                                }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Debug.LogWarning($"[Nexus] Command state leak scan skipped assembly '{assembly.GetName().Name}': {ex.LoaderExceptions?[0]?.Message ?? ex.Message}");
                }
            }
        }

        /// <summary>
        /// Returns true if <paramref name="field"/> is a C# auto-property backing field
        /// (name pattern <code>&lt;PropertyName&gt;k__BackingField</code>) and the declaring
        /// property has <see cref="InjectAttribute"/>. This prevents false positives when
        /// <c>[Inject]</c> is placed on an auto-property — the attribute lives on the property,
        /// not on the compiler-generated backing field.
        /// </summary>
        private static bool IsAutoPropertyWithInject(FieldInfo field, Type declaringType)
        {
            if (!field.Name.EndsWith("k__BackingField")) return false;
            if (!field.Name.StartsWith("<")) return false;
            string propName = field.Name.Substring(1, field.Name.IndexOf('>') - 1);
            var prop = declaringType.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return prop != null && prop.GetCustomAttribute<InjectAttribute>() != null;
        }

        /// <summary>
        /// Plan §9.6 — Composite Trigger unreachable signal:
        /// Warns when a composite trigger references a signal type that is never dispatched
        /// by any registered command (no [SignalHandler] outputs it) making the composite
        /// potentially impossible to complete.
        /// </summary>
        private static void ValidateCompositeTriggerReachability(ref int errorCount, ref int warningCount)
        {
            var compositeSignalSets = new List<(Type CommandType, Type[] SignalTypes)>();
            var allHandledSignalTypes = new HashSet<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("Microsoft") || name.StartsWith("mono"))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;

                        // Collect [SignalHandler] signal types
                        var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                        foreach (var attr in handlerAttrs)
                        {
                            allHandledSignalTypes.Add(attr.SignalType);
                        }

                        // Collect [CompositeSignalHandler] entries
                        var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                        if (compositeAttr != null)
                        {
                            compositeSignalSets.Add((type, compositeAttr.SignalTypes));
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Debug.LogWarning($"[Nexus] Composite trigger scan skipped assembly '{assembly.GetName().Name}': {ex.LoaderExceptions?[0]?.Message ?? ex.Message}");
                }
            }

            // For each composite trigger, warn if any of its constituent signal types
            // are never referenced as a handled signal type (meaning nothing responds to it,
            // indicating it may only come from user code — which is fine, so this is just a warning)
            foreach (var (cmdType, signalTypes) in compositeSignalSets)
            {
                foreach (var sigType in signalTypes)
                {
                    if (!allHandledSignalTypes.Contains(sigType))
                    {
                        // Check if the signal has [CrossContext] — these are typically dispatched externally
                        var crossAttr = sigType.GetCustomAttribute<CrossContextAttribute>();
                        if (crossAttr == null)
                        {
                            Debug.LogWarning($"[Nexus Warning] Composite Trigger Unreachable Signal: Composite command {cmdType.Name} references signal {sigType.Name} which has no [SignalHandler] binding. Ensure this signal is dispatched from user code or other systems.");
                            warningCount++;
                        }
                    }
                }
            }
        }
    }

    // Build Pre-processor hook to automatically validate before build starts
    public class NexusBuildPreProcessor : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            string bypassEnv = System.Environment.GetEnvironmentVariable("NEXUS_DISABLE_VALIDATION");
            bool disableValidation = !string.IsNullOrEmpty(bypassEnv) && (bypassEnv == "1" || bypassEnv.Equals("true", StringComparison.OrdinalIgnoreCase));
            
            string warnEnv = System.Environment.GetEnvironmentVariable("NEXUS_VALIDATION_WARN_ONLY");
            bool warnOnly = !string.IsNullOrEmpty(warnEnv) && (warnEnv == "1" || warnEnv.Equals("true", StringComparison.OrdinalIgnoreCase));

            if (disableValidation)
            {
                UnityEngine.Debug.Log("[Nexus] Architecture Validation bypassed via NEXUS_DISABLE_VALIDATION environment variable.");
                return;
            }

            bool success = BuildValidation.Validate();
            if (!success)
            {
                if (warnOnly)
                {
                    UnityEngine.Debug.LogWarning("[Nexus] Architecture Validation failed, but continuing build because NEXUS_VALIDATION_WARN_ONLY is enabled.");
                }
                else
                {
                    throw new UnityEditor.Build.BuildFailedException("Nexus Architecture Validation Failed. See Console for details.");
                }
            }
        }
    }
}
