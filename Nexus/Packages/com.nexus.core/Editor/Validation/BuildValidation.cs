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

        public struct Entry { public string Rule; public string Message; public bool IsError; }
        private static readonly List<Entry> s_lastResults = new();
        public static IReadOnlyList<Entry> LastResults => s_lastResults;
        public static int LastErrorCount { get; private set; }
        public static int LastWarningCount { get; private set; }
        public static bool LastRunPassed { get; private set; }
        public static bool HasRun { get; private set; }
        public static string LastRunSummary => HasRun
            ? $"{LastErrorCount} errors, {LastWarningCount} warnings"
            : "Not run yet";

        public static void RunSilent()
        {
            s_lastResults.Clear();
            LastErrorCount = 0;
            LastWarningCount = 0;
            var prevInfo = InfoLogger;
            var prevWarn = WarningLogger;
            var prevErr = ErrorLogger;
            InfoLogger = _ => { };
            WarningLogger = msg => { s_lastResults.Add(new Entry { Message = msg, IsError = false }); LastWarningCount++; };
            ErrorLogger = msg => { s_lastResults.Add(new Entry { Message = msg, IsError = true }); LastErrorCount++; };
            try { LastRunPassed = Validate(); }
            catch (Exception ex)
            {
                s_lastResults.Add(new Entry { Rule = "ValidationException", Message = ex.Message, IsError = true });
                LastRunPassed = false;
                LastErrorCount++;
                throw;
            }
            finally { InfoLogger = prevInfo; WarningLogger = prevWarn; ErrorLogger = prevErr; HasRun = true; }
        }

        private static class Debug
        {
            public static void Log(string msg) => InfoLogger(msg);
            public static void LogWarning(string msg) => WarningLogger(msg);
            public static void LogError(string msg) => ErrorLogger(msg);
        }

        [MenuItem("Nexus/Validate Architecture")]
        public static bool Validate()
        {
            s_lastResults.Clear();
            LastErrorCount = 0;
            LastWarningCount = 0;
            HasRun = true;
            Debug.Log("[Nexus] Starting Architecture Validation...");
            int errorCount = 0;
            int warningCount = 0;

            try
            {
                // 1. Scan and validate signal handlers, priorities and mixed modes
                ValidateHandlers(ref errorCount, ref warningCount);

                // 1b. Validate Async/Sync Call Graph Cycles
                ValidateAsyncCallGraph(ref errorCount, ref warningCount);

                // 2. Validate model ownership chains (IDisposableModel)
                ValidateModelOwnership(ref errorCount, ref warningCount);

                // 3. Validate ContextData DependsOn for cycles
                ValidateContextDataDependencies(ref errorCount, ref warningCount);

                // 3b. Validate ContextData configuration integrity (AssemblyScopes, DependsOn)
                ValidateContextDataConfiguration(ref errorCount, ref warningCount);

                // 4. Validate scene Roots and context hierarchies
                ValidateSceneHierarchy(ref errorCount, ref warningCount);

                // 5. Validate Command state leak (Plan §6.1.1)
                ValidateCommandStateLeak(ref errorCount, ref warningCount);

                // 6. Validate Composite Trigger reachability (Plan §9.6)
                ValidateCompositeTriggerReachability(ref errorCount, ref warningCount);

                // 7. Validate DI binding completeness across assemblies
                ValidateDiBindings(ref errorCount, ref warningCount);

                // 8. Warn about stub services that should be replaced before release
                ValidateNoStubServices(ref errorCount, ref warningCount);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Validation aborted due to critical error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }

            LastErrorCount = errorCount;
            LastWarningCount = warningCount;
            LastRunPassed = errorCount == 0;

            if (errorCount > 0)
            {
                Debug.LogError($"[Nexus] Validation FAILED with {errorCount} Errors and {warningCount} Warnings. Please fix the errors before building.");
                return false;
            }

            Debug.Log($"[Nexus] Validation PASSED with {warningCount} Warnings.");
            return true;
        }

        public static void ValidateOrThrow()
        {
            if (!Validate())
            {
                throw new InvalidOperationException($"Nexus validation failed with {LastErrorCount} errors and {LastWarningCount} warnings.");
            }
        }

        private static void ValidateHandlers(ref int errorCount, ref int warningCount)
        {
            var signalHandlers = new Dictionary<Type, List<(Type CommandType, SignalHandlerAttribute Attr)>>();
            var loadedAssemblies = AssemblyCatalog.LoadedAssemblies;
            var isTestAssemblyAllowed = IncludeTestAssemblies;

            // Scan all loaded assemblies
            foreach (var assembly in loadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!isTestAssemblyAllowed && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (!type.IsClass || type.IsAbstract)
                        continue;

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

                // Generic command interface check (for performance and AOT compatibility).
                // E-4 fix: check BOTH generic interfaces directly so commands that only implement
                // IAsyncCommand<T> (without non-generic IAsyncCommand) are classified correctly.
                foreach (var handler in handlers)
                {
                    bool isSync = typeof(ICommand).IsAssignableFrom(handler.CommandType)
                        || SignalBus.ImplementsGenericInterface(handler.CommandType, typeof(ICommand<>));
                    bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(handler.CommandType)
                        || SignalBus.ImplementsGenericInterface(handler.CommandType, typeof(IAsyncCommand<>));

                    if (!isSync && !isAsync)
                    {
                        Debug.LogError($"[Nexus Error] Generic Command Violation: Command {handler.CommandType.FullName} handles signal {signalType.Name} but does not implement ICommand<{signalType.Name}> or IAsyncCommand<{signalType.Name}>. Implement generic interfaces to eliminate reflection fallback and IL2CPP boxing.");
                        errorCount++;
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
            var disposableModels = new List<Type>();
            var lifecyclePaths = new List<string>();

            // Find MonoScript cache for locating C# files
            var scriptCache = BuildTypeScriptCache();

            // 1. Gather all disposable models and lifecycle paths
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (type.IsClass && !type.IsAbstract)
                    {
                        if (typeof(IDisposableModel).IsAssignableFrom(type))
                        {
                            disposableModels.Add(type);
                        }
                        else if (typeof(IContextLifecycle).IsAssignableFrom(type) && scriptCache.TryGetValue(type, out var path))
                        {
                            lifecyclePaths.Add(path);
                        }
                    }
                }
            }

            // 2. Verify each disposable model is bound in at least one lifecycle DI container
            foreach (var modelType in disposableModels)
            {
                bool isBound = false;
                foreach (var path in lifecyclePaths)
                {
                    try
                    {
                        string content = System.IO.File.ReadAllText(path);
                        // Match the full type name with word boundaries so "Player" does not
                        // falsely match a "PlayerView" reference in an unrelated lifecycle class.
                        if (content.Contains(" " + modelType.Name + " ") || content.Contains(modelType.Name + ";") || content.Contains(modelType.Name + ":") || content.Contains(modelType.Name + ","))
                        {
                            isBound = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Nexus Warning] Model ownership validation could not read '{path}': {ex.Message}");
                        warningCount++;
                    }
                }

                if (!isBound)
                {
                    Debug.LogError($"[Nexus Error] IDisposableModel Leak Violation: Model type {modelType.FullName} implements IDisposableModel but is not registered in any IContextLifecycle DI configuration. Registered models must be bound as singletons in lifecycle classes to ensure proper disposal.");
                    errorCount++;
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
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
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

        private static void ValidateContextDataConfiguration(ref int errorCount, ref int warningCount)
        {
            var contextDataAssets = AssetDatabase.FindAssets("t:ContextData");
            var loadedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                loadedAssemblies.Add(AssemblyCatalog.GetSimpleName(assembly));
            }

            foreach (var guid in contextDataAssets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<ContextData>(path);
                if (data == null) continue;

                string name = data.name.Replace("ContextData", "");

                if (data.AssemblyScopes != null)
                {
                    foreach (var scope in data.AssemblyScopes)
                    {
                        if (!loadedAssemblies.Contains(scope))
                        {
                            Debug.LogWarning($"[Nexus Warning] ContextData '{name}': Assembly scope '{scope}' is not found in loaded assemblies. This assembly may not exist or may not be loaded yet.");
                            warningCount++;
                        }
                    }
                }

                if (data.DependsOn != null)
                {
                    foreach (var dep in data.DependsOn)
                    {
                        bool found = false;
                        foreach (var otherGuid in contextDataAssets)
                        {
                            var otherPath = AssetDatabase.GUIDToAssetPath(otherGuid);
                            var otherData = AssetDatabase.LoadAssetAtPath<ContextData>(otherPath);
                            if (otherData != null && otherData != data)
                            {
                                string otherScope = string.IsNullOrEmpty(otherData.ScopeTag)
                                    ? otherData.name.Replace("ContextData", "")
                                    : otherData.ScopeTag;
                                if (string.Equals(otherScope, dep, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                        if (!found)
                        {
                            Debug.LogWarning($"[Nexus Warning] ContextData '{name}': DependsOn '{dep}' does not match any known ContextData scope tag or name. Dependency may be unresolved.");
                            warningCount++;
                        }
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
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (!type.IsClass || type.IsAbstract) continue;
                    bool isCommand = typeof(ICommand).IsAssignableFrom(type)
                        || typeof(IAsyncCommand).IsAssignableFrom(type)
                        || typeof(ICompositeCommand).IsAssignableFrom(type)
                        || typeof(IAsyncCompositeCommand).IsAssignableFrom(type)
                        || SignalBus.ImplementsGenericInterface(type, typeof(ICommand<>))
                        || SignalBus.ImplementsGenericInterface(type, typeof(IAsyncCommand<>));
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

                        // Skip enum fields — they're backed by integers and trivially re-assigned
                        if (field.FieldType.IsEnum)
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
        }

        private static void ValidateNoStubServices(ref int errorCount, ref int warningCount)
        {
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (!type.IsClass || type.IsAbstract) continue;
                    var stubAttr = type.GetCustomAttribute<StubServiceAttribute>();
                    if (stubAttr != null)
                    {
                        var msg = $"[Nexus Warning] Stub Service: {type.FullName} is a stub{(string.IsNullOrEmpty(stubAttr.Description) ? "" : $" — {stubAttr.Description}")}. Replace with a real SDK implementation before release.";
                        Debug.LogWarning(msg);
                        warningCount++;
                    }
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

            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
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

        private static void ValidateAsyncCallGraph(ref int errorCount, ref int warningCount)
        {
            var commandSignals = new Dictionary<Type, List<Type>>(); // Command -> Fired Signals
            var signalCommands = new Dictionary<Type, List<Type>>(); // Signal -> Handlers

            var scriptCache = BuildTypeScriptCache();
            var signalTypeMap = BuildSignalTypeMap();

            // 1. Gather all handlers and composite handlers
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (type.IsClass && !type.IsAbstract)
                    {
                        // Standard SignalHandler
                        var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                        foreach (var attr in handlerAttrs)
                        {
                            if (!signalCommands.TryGetValue(attr.SignalType, out var list))
                            {
                                list = new List<Type>();
                                signalCommands[attr.SignalType] = list;
                            }
                            if (!list.Contains(type)) list.Add(type);
                        }

                        // Composite SignalHandler
                        var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                        if (compositeAttr != null && compositeAttr.SignalTypes != null)
                        {
                            foreach (var sigType in compositeAttr.SignalTypes)
                            {
                                if (sigType != null)
                                {
                                    if (!signalCommands.TryGetValue(sigType, out var list))
                                    {
                                        list = new List<Type>();
                                        signalCommands[sigType] = list;
                                    }
                                    if (!list.Contains(type)) list.Add(type);
                                }
                            }
                        }
                    }
                }
            }

            // 2. Find fired signals for each unique command type that we found
            var allCommands = new HashSet<Type>();
            foreach (var cmdList in signalCommands.Values)
            {
                allCommands.UnionWith(cmdList);
            }

            foreach (var cmdType in allCommands)
            {
                var firedList = new List<Type>();
                commandSignals[cmdType] = firedList;

                if (scriptCache.TryGetValue(cmdType, out var scriptPath))
                {
                    try
                    {
                        string content = System.IO.File.ReadAllText(scriptPath);

                        // Find generic Fire calls: Fire<SignalType>
                        var genericMatches = s_fireGenericRegex.Matches(content);
                        foreach (System.Text.RegularExpressions.Match match in genericMatches)
                        {
                            if (match.Groups.Count > 1)
                            {
                                string sigName = match.Groups[1].Value;
                                if (signalTypeMap.TryGetValue(sigName, out var sigType))
                                {
                                    if (!firedList.Contains(sigType)) firedList.Add(sigType);
                                }
                            }
                        }

                        // Find new instantiation Fire calls: Fire(new SignalType(...))
                        var newMatches = s_fireNewRegex.Matches(content);
                        foreach (System.Text.RegularExpressions.Match match in newMatches)
                        {
                            if (match.Groups.Count > 1)
                            {
                                string sigName = match.Groups[1].Value;
                                if (signalTypeMap.TryGetValue(sigName, out var sigType))
                                {
                                    if (!firedList.Contains(sigType)) firedList.Add(sigType);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Nexus Warning] Composite trigger reachability could not read '{scriptPath}': {ex.Message}");
                        warningCount++;
                    }
                }
            }

            // 3. Construct Directed Graph
            var graph = new Dictionary<Type, HashSet<Type>>();

            // Add Signal -> Command edges
            foreach (var kvp in signalCommands)
            {
                var sigType = kvp.Key;
                if (!graph.TryGetValue(sigType, out var set))
                {
                    set = new HashSet<Type>();
                    graph[sigType] = set;
                }
                foreach (var cmdType in kvp.Value)
                {
                    set.Add(cmdType);
                }
            }

            // Add Command -> Signal edges
            foreach (var kvp in commandSignals)
            {
                var cmdType = kvp.Key;
                if (!graph.TryGetValue(cmdType, out var set))
                {
                    set = new HashSet<Type>();
                    graph[cmdType] = set;
                }
                foreach (var sigType in kvp.Value)
                {
                    set.Add(sigType);
                }
            }

            // 4. DFS Cycle Detection
            var visited = new HashSet<Type>();
            var visiting = new HashSet<Type>();
            var path = new List<Type>();

            foreach (var node in graph.Keys)
            {
                path.Clear();
                visiting.Clear();
                if (HasCycleDfs(node, graph, visiting, visited, path))
                {
                    // Extract cycle
                    var cycleStartNode = path[path.Count - 1];
                    int startIdx = path.IndexOf(cycleStartNode);
                    var cycleList = path.GetRange(startIdx, path.Count - startIdx);
                    var names = new List<string>();
                    foreach (var t in cycleList)
                    {
                        names.Add(t.Name);
                    }
                    string cycleStr = string.Join(" → ", names);

                    Debug.LogError($"[Nexus Error] Circular Command/Signal Cycle Detected: {cycleStr}. Cycles lead to stack overflows or infinite async loops.");
                    errorCount++;
                }
            }
        }

        private static bool HasCycleDfs(Type current, Dictionary<Type, HashSet<Type>> graph, HashSet<Type> visiting, HashSet<Type> visited, List<Type> path)
        {
            if (visited.Contains(current)) return false;
            if (!visiting.Add(current))
            {
                path.Add(current);
                return true; // Cycle detected!
            }
            path.Add(current);

            if (graph.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (HasCycleDfs(neighbor, graph, visiting, visited, path))
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

        /// <summary>Delegates to <see cref="AssemblyCatalog"/> so validation sees the same
        /// assembly universe as every other Nexus editor tool.</summary>
        private static bool IsAssemblyExcluded(string name)
            => AssemblyCatalog.IsFrameworkAssembly(name) || AssemblyCatalog.IsThirdPartyAssembly(name);

        private static bool IsTypeAvailable(Type type, HashSet<string> availableTypeNames)
        {
            // A type is available if:
            // 1. Its FullName is in the availableTypeNames set, OR
            // 2. Its assembly is excluded by IsAssemblyExcluded (meaning it's a system/Unity/3rd-party type always present at runtime)
            if (type.FullName == null) return true;
            if (availableTypeNames.Contains(type.FullName)) return true;
            try
            {
                var asmName = type.Assembly.GetName().Name;
                if (IsAssemblyExcluded(asmName)) return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus Warning] DI availability inspection failed for '{type.FullName}': {ex.Message}");
            }
            return false;
        }

        private static void ValidateDiBindings(ref int errorCount, ref int warningCount)
        {
            // Build a set of all available types from non-system loaded assemblies.
            // This catches missing assembly references and type resolution failures
            // for types with [Inject] dependencies.
            var availableTypeNames = new HashSet<string>(StringComparer.Ordinal);
            var scannedTypes = new List<Type>();

            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                if (!IncludeTestAssemblies && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (type.FullName != null)
                    {
                        availableTypeNames.Add(type.FullName);
                        if (!type.IsAbstract && !type.IsInterface && !type.IsEnum && !type.IsValueType)
                            scannedTypes.Add(type);
                    }
                }
            }

            // Always-resolvable types auto-provided by NexusDI
            availableTypeNames.Add(typeof(NexusDI).FullName);
            availableTypeNames.Add(typeof(IContext).FullName);
            availableTypeNames.Add(typeof(ISignalBus).FullName);

            foreach (var type in scannedTypes)
            {
                NexusDI.InjectableMetadata meta;
                try { meta = NexusDI.GetOrCreateInjectMetadata(type); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Nexus Warning] DI metadata inspection failed for '{type.FullName}': {ex.Message}");
                    warningCount++;
                    continue;
                }

                // Constructor parameter validation
                if (meta.ConstructorParameterTypes is { Length: > 0 })
                {
                    foreach (var paramType in meta.ConstructorParameterTypes)
                    {
                        if (!IsTypeAvailable(paramType, availableTypeNames))
                        {
                            Debug.LogWarning($"[Nexus Warning] DI: '{type.FullName}' constructor depends on '{paramType.FullName}', which is not available in any scanned assembly. Verify the assembly reference.");
                            warningCount++;
                        }
                    }
                }

                // [Inject] field validation
                foreach (var field in meta.Fields)
                {
                    if (!field.IsOptional && !IsTypeAvailable(field.Type, availableTypeNames))
                    {
                        Debug.LogWarning($"[Nexus Warning] DI: [Inject] field '{type.FullName}.{field.Field.Name}' requires '{field.Type.FullName}', which is not available in any scanned assembly.");
                        warningCount++;
                    }
                }

                // [Inject] property validation
                foreach (var prop in meta.Properties)
                {
                    if (!prop.IsOptional && !IsTypeAvailable(prop.Type, availableTypeNames))
                    {
                        Debug.LogWarning($"[Nexus Warning] DI: [Inject] property '{type.FullName}.{prop.Property.Name}' requires '{prop.Type.FullName}', which is not available in any scanned assembly.");
                        warningCount++;
                    }
                }

                // [Inject] method parameter validation
                foreach (var method in meta.Methods)
                {
                    for (int i = 0; i < method.ParameterTypes.Length; i++)
                    {
                        if (!method.OptionalParameterMask[i] && !IsTypeAvailable(method.ParameterTypes[i], availableTypeNames))
                        {
                            var paramName = method.Method.GetParameters()[i].Name;
                            Debug.LogWarning($"[Nexus Warning] DI: [Inject] method '{type.FullName}.{method.Method.Name}' parameter '{paramName}' requires '{method.ParameterTypes[i].FullName}', which is not available in any scanned assembly.");
                            warningCount++;
                        }
                    }
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex s_fireGenericRegex =
            new(@"\.Fire[A-Za-z]*\s*<\s*([A-Za-z0-9_\.]+)\s*>", System.Text.RegularExpressions.RegexOptions.Compiled);
            
        private static readonly System.Text.RegularExpressions.Regex s_fireNewRegex = 
            new(@"\.Fire[A-Za-z]*\s*\(\s*new\s+([A-Za-z0-9_\.]+)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static Dictionary<Type, string> BuildTypeScriptCache()
        {
            var cache = new Dictionary<Type, string>();
            var guids = AssetDatabase.FindAssets("t:MonoScript");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null)
                {
                    try
                    {
                        var klass = script.GetClass();
                        if (klass != null && !cache.ContainsKey(klass))
                        {
                            cache[klass] = path;
                        }

                        // MonoScript.GetClass() only returns the outermost type.
                        // Nested command/signal types inside the same file are not findable
                        // by type alone, which means ValidateAsyncCallGraph silently misses
                        // their fired signals and cannot detect potential cycles.
                        // Scan all declared nested types and map them to the same script path
                        // so nested [SignalHandler] commands are covered by the validator.
                        if (klass != null)
                        {
                            foreach (var nested in klass.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (!cache.ContainsKey(nested))
                                    cache[nested] = path;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Nexus Warning] Type-script mapping skipped '{path}': {ex.Message}");
                    }
                }
            }
            return cache;
        }

        private static Dictionary<string, Type> BuildSignalTypeMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
            {
                var name = AssemblyCatalog.GetSimpleName(assembly);
                if (IsAssemblyExcluded(name))
                    continue;
                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (type.IsValueType && !type.IsEnum && !type.IsPrimitive)
                    {
                        map[type.Name] = type;
                        map[type.FullName] = type;
                        string cleanFullName = type.FullName.Replace("+", ".");
                        map[cleanFullName] = type;
                    }
                }
            }
            return map;
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

            // Regenerate the AOT binder so a stale injector never ships in the build.
            string autogenEnv = System.Environment.GetEnvironmentVariable("NEXUS_DISABLE_AUTOGEN");
            bool disableAutogen = !string.IsNullOrEmpty(autogenEnv) && (autogenEnv == "1" || autogenEnv.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (!disableAutogen)
            {
                try
                {
                    NexusCodeGenerator.GenerateBinder();
                }
                catch (Exception ex)
                {
                    throw new UnityEditor.Build.BuildFailedException($"[Nexus] AOT Binder generation failed during the pre-build step: {ex.Message}");
                }
            }

            if (disableValidation)
            {
                UnityEngine.Debug.LogWarning("[Nexus] Architecture Validation bypassed via NEXUS_DISABLE_VALIDATION environment variable.");
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
