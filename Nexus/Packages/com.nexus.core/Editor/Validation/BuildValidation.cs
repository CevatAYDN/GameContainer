using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public static class BuildValidation
    {
        public static bool IncludeTestAssemblies
        {
            get => s_includeTestAssemblies;
            set
            {
                if (s_includeTestAssemblies == value) return;
                s_includeTestAssemblies = value;
                // The scanned type universe changes with this flag — drop shared caches.
                InvalidateCaches();
            }
        }
        private static bool s_includeTestAssemblies;

        // ── Shared scan caches ──────────────────────────────────────────────
        // The validation passes used to each re-scan every loaded assembly, re-materialize
        // every type list, and re-instantiate every attribute — ~10 full reflection passes
        // per run. These caches share the reflection universe across passes: per-assembly
        // types, per-type handler/composite/stub/depends-on attributes, the MonoScript→type
        // map, the signal-type name map, and per-run file contents (so each script file is
        // read at most once per validation run). Invalidated on script reload and whenever
        // IncludeTestAssemblies changes.
        private static Dictionary<Assembly, Type[]> s_assemblyTypes;
        private static Dictionary<Type, SignalHandlerAttribute[]> s_handlerAttrs;
        private static Dictionary<Type, CompositeSignalHandlerAttribute> s_compositeAttrs;
        private static Dictionary<Type, StubServiceAttribute> s_stubAttrs;
        private static Dictionary<Type, ContextDependsOnAttribute[]> s_contextDependsAttrs;
        private static Dictionary<Type, bool> s_writeableModelCache;
        private static Dictionary<Type, string> s_typeScriptCache;
        private static Dictionary<string, Type> s_signalTypeMap;
        private static Dictionary<string, (bool ok, string content)> s_runFileCache;

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded() => InvalidateCaches();

        private static void InvalidateCaches()
        {
            s_assemblyTypes = null;
            s_handlerAttrs = null;
            s_compositeAttrs = null;
            s_stubAttrs = null;
            s_contextDependsAttrs = null;
            s_writeableModelCache = null;
            s_typeScriptCache = null;
            s_signalTypeMap = null;
            // BUG: s_runFileCache was not cleared here — after a script reload, stale
            // file contents from the previous run could be served. Clear it too.
            s_runFileCache = null;
        }

        private static void EnsureCaches()
        {
            if (s_assemblyTypes != null) return;
            s_assemblyTypes = new Dictionary<Assembly, Type[]>();
            s_handlerAttrs = new Dictionary<Type, SignalHandlerAttribute[]>();
            s_compositeAttrs = new Dictionary<Type, CompositeSignalHandlerAttribute>();
            s_stubAttrs = new Dictionary<Type, StubServiceAttribute>();
            s_contextDependsAttrs = new Dictionary<Type, ContextDependsOnAttribute[]>();
            s_writeableModelCache = new Dictionary<Type, bool>();
            // Type lists are materialized lazily per assembly by EnumerateGameTypes
            // (once per assembly per script-reload), so framework/3rd-party assemblies
            // that are never enumerated no longer pay a GetTypes() pass.
        }

        /// <summary>
        /// All game-relevant types (classes AND structs) across loaded assemblies,
        /// EXCLUDING framework, third-party, test (unless includeTests) and editor
        /// assemblies — exactly the universe defined by
        /// <see cref="AssemblyCatalog.GameAssemblies"/>, so every validation pass sees
        /// the same code as every other Nexus editor tool.
        /// </summary>
        private static IEnumerable<Type> EnumerateGameTypes(bool includeTests)
        {
            EnsureCaches();
            foreach (var assembly in AssemblyCatalog.GameAssemblies(includeTests))
            {
                // GetTypesSafe never throws (it logs + yields the partial set); materialize
                // once per assembly so later passes iterate an array instead of re-enumerating.
                if (!s_assemblyTypes.TryGetValue(assembly, out var types))
                {
                    types = AssemblyCatalog.GetTypesSafe(assembly).ToArray();
                    s_assemblyTypes[assembly] = types;
                }
                foreach (var t in types) yield return t;
            }
        }

        /// <summary>Concrete (non-abstract, non-interface) classes across game assemblies.</summary>
        private static IEnumerable<Type> EnumerateGameClasses(bool includeTests)
        {
            foreach (var t in EnumerateGameTypes(includeTests))
            {
                if (t.IsClass && !t.IsAbstract) yield return t;
            }
        }

        private static SignalHandlerAttribute[] GetHandlerAttrs(Type type)
        {
            EnsureCaches();
            if (!s_handlerAttrs.TryGetValue(type, out var attrs))
            {
                attrs = type.IsDefined(typeof(SignalHandlerAttribute), false)
                    ? type.GetCustomAttributes<SignalHandlerAttribute>().ToArray()
                    : Array.Empty<SignalHandlerAttribute>();
                s_handlerAttrs[type] = attrs;
            }
            return attrs;
        }

        private static CompositeSignalHandlerAttribute GetCompositeAttr(Type type)
        {
            EnsureCaches();
            if (!s_compositeAttrs.TryGetValue(type, out var attr))
            {
                attr = type.IsDefined(typeof(CompositeSignalHandlerAttribute), false)
                    ? type.GetCustomAttribute<CompositeSignalHandlerAttribute>()
                    : null;
                s_compositeAttrs[type] = attr;
            }
            return attr;
        }

        private static StubServiceAttribute GetStubAttr(Type type)
        {
            EnsureCaches();
            if (!s_stubAttrs.TryGetValue(type, out var attr))
            {
                attr = type.IsDefined(typeof(StubServiceAttribute), false)
                    ? type.GetCustomAttribute<StubServiceAttribute>()
                    : null;
                s_stubAttrs[type] = attr;
            }
            return attr;
        }

        private static ContextDependsOnAttribute[] GetContextDependsAttrs(Type type)
        {
            EnsureCaches();
            if (!s_contextDependsAttrs.TryGetValue(type, out var attrs))
            {
                attrs = type.IsDefined(typeof(ContextDependsOnAttribute), false)
                    ? type.GetCustomAttributes<ContextDependsOnAttribute>().ToArray()
                    : Array.Empty<ContextDependsOnAttribute>();
                s_contextDependsAttrs[type] = attrs;
            }
            return attrs;
        }

        /// <summary>Reads a file at most once per validation run (files can change between runs).</summary>
        private static (bool ok, string content) ReadFileCached(string path)
        {
            if (s_runFileCache == null) s_runFileCache = new Dictionary<string, (bool, string)>();
            if (!s_runFileCache.TryGetValue(path, out var entry))
            {
                try { entry = (true, System.IO.File.ReadAllText(path)); }
                catch (Exception ex) { entry = (false, ex.Message); }
                s_runFileCache[path] = entry;
            }
            return entry;
        }

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
            // Fresh file-content cache per run: scripts may have been edited since the
            // previous validation, so each path is read once per run (never across runs).
            s_runFileCache = null;
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
            var isTestAssemblyAllowed = IncludeTestAssemblies;

            // Scan all loaded assemblies (shared type + attribute caches: one reflection pass).
            foreach (var type in EnumerateGameClasses(isTestAssemblyAllowed))
            {
                var attrs = GetHandlerAttrs(type);
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
                if (field.IsDefined(typeof(InjectAttribute), false))
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
                if (prop.IsDefined(typeof(InjectAttribute), false))
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

            // Interface shape is type-stable — cache the writeable verdict per interface.
            EnsureCaches();
            if (s_writeableModelCache.TryGetValue(type, out var cached)) return cached;

            // Interface inheritance reflection fix: recursively scan all parent interfaces
            var allTypes = new List<Type> { type };
            allTypes.AddRange(type.GetInterfaces());

            foreach (var t in allTypes)
            {
                // Check if the interface has any settable properties (writeable indicators)
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    if (prop.CanWrite)
                    {
                        s_writeableModelCache[type] = true;
                        return true;
                    }
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
                        s_writeableModelCache[type] = true;
                        return true;
                    }
                }
            }

            s_writeableModelCache[type] = false;
            return false;
        }

        private static void ValidateModelOwnership(ref int errorCount, ref int warningCount)
        {
            var disposableModels = new List<Type>();
            var lifecyclePaths = new List<string>();

            // Find MonoScript cache for locating C# files
            var scriptCache = BuildTypeScriptCache();

            // 1. Gather all disposable models and lifecycle paths (shared type cache)
            foreach (var type in EnumerateGameClasses(IncludeTestAssemblies))
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

            // 2. Verify each disposable model is bound in at least one lifecycle DI container
            foreach (var modelType in disposableModels)
            {
                bool isBound = false;
                foreach (var path in lifecyclePaths)
                {
                    var (ok, content) = ReadFileCached(path);
                    if (!ok)
                    {
                        Debug.LogWarning($"[Nexus Warning] Model ownership validation could not read '{path}': {content}");
                        warningCount++;
                        continue;
                    }
                    // Match the full type name with word boundaries so "Player" does not
                    // falsely match a "PlayerView" reference in an unrelated lifecycle class.
                    if (content.Contains(" " + modelType.Name + " ") || content.Contains(modelType.Name + ";") || content.Contains(modelType.Name + ":") || content.Contains(modelType.Name + ","))
                    {
                        isBound = true;
                        break;
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
            foreach (var type in EnumerateGameClasses(IncludeTestAssemblies))
            {
                if (typeof(IContextLifecycle).IsAssignableFrom(type))
                {
                    var attrs = GetContextDependsAttrs(type);
                    if (attrs.Length > 0)
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

            // Load each asset ONCE and index by scope (tag or name-derived). The previous
            // DependsOn check re-scanned every other asset — including a fresh
            // LoadAssetAtPath per pair — for each dependency: O(n²) asset I/O on large
            // projects. Scope lookup is O(1).
            var scopes = new Dictionary<string, ContextData>(StringComparer.OrdinalIgnoreCase);
            var assets = new List<ContextData>(contextDataAssets.Length);
            foreach (var guid in contextDataAssets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<ContextData>(path);
                if (data == null) continue;
                assets.Add(data);
                string scope = string.IsNullOrEmpty(data.ScopeTag)
                    ? data.name.Replace("ContextData", "")
                    : data.ScopeTag;
                if (!scopes.ContainsKey(scope)) scopes[scope] = data;
            }

            foreach (var data in assets)
            {
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
                        if (!scopes.ContainsKey(dep))
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
            foreach (var type in EnumerateGameClasses(IncludeTestAssemblies))
            {
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
                    if (field.IsDefined(typeof(InjectAttribute), false))
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

        private static void ValidateNoStubServices(ref int errorCount, ref int warningCount)
        {
            foreach (var type in EnumerateGameClasses(IncludeTestAssemblies))
            {
                var stubAttr = GetStubAttr(type);
                if (stubAttr != null)
                {
                    var msg = $"[Nexus Warning] Stub Service: {type.FullName} is a stub{(string.IsNullOrEmpty(stubAttr.Description) ? "" : $" — {stubAttr.Description}")}. Replace with a real SDK implementation before release.";
                    Debug.LogWarning(msg);
                    warningCount++;
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
            return prop != null && prop.IsDefined(typeof(InjectAttribute), false);
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

            foreach (var type in EnumerateGameClasses(IncludeTestAssemblies))
            {
                // Collect [SignalHandler] signal types (cached per type)
                foreach (var attr in GetHandlerAttrs(type))
                {
                    allHandledSignalTypes.Add(attr.SignalType);
                }

                // Collect [CompositeSignalHandler] entries (cached per type)
                var compositeAttr = GetCompositeAttr(type);
                if (compositeAttr != null)
                {
                    compositeSignalSets.Add((type, compositeAttr.SignalTypes));
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

            // 1. Gather all handlers and composite handlers (shared type/attribute caches)
            foreach (var type in EnumerateGameClasses(IncludeTestAssemblies))
            {
                // Standard SignalHandler
                foreach (var attr in GetHandlerAttrs(type))
                {
                    if (!signalCommands.TryGetValue(attr.SignalType, out var list))
                    {
                        list = new List<Type>();
                        signalCommands[attr.SignalType] = list;
                    }
                    if (!list.Contains(type)) list.Add(type);
                }

                // Composite SignalHandler
                var compositeAttr = GetCompositeAttr(type);
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
                    // File read once per path per run (nested command types share one script).
                    var (ok, content) = ReadFileCached(scriptPath);
                    if (!ok)
                    {
                        Debug.LogWarning($"[Nexus Warning] Composite trigger reachability could not read '{scriptPath}': {content}");
                        warningCount++;
                    }
                    else
                    {
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

            // Arrays and generic instantiations are never enumerated directly — the scan
            // yields element types and generic definitions — so check their parts instead.
            // Without this, every array constructor parameter (e.g. GLTFast.MeshGenerator's
            // SubMeshAssignment[], CustomHeaderDownloadProvider's HttpHeader[]) was reported
            // as a phantom "not available in any scanned assembly" even though the element
            // types are present. Value-type arguments are always resolvable.
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                return elementType != null && (elementType.IsValueType || IsTypeAvailable(elementType, availableTypeNames));
            }
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    if (arg.IsValueType) continue;
                    if (!IsTypeAvailable(arg, availableTypeNames)) return false;
                }
                return true;
            }

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

        /// <summary>
        /// True only for types the DI validator should inspect. Compiler-generated
        /// iterators/closures/Burst delegates, attributes, delegates, and framework- or
        /// engine-namespaced types (System.*, Microsoft.*, Unity.*, Mono.*) can never be
        /// dependency-injected — inspecting them only produced "metadata inspection failed"
        /// noise (e.g. iterator state machines with a &lt;&gt;1__state constructor parameter,
        /// NullableAttribute, Burst $PostfixBurstDelegate types hosted in non-excluded
        /// assemblies). The host assembly is irrelevant: the namespace is authoritative.
        /// </summary>
        private static bool IsDiInspectableType(Type type)
        {
            if (typeof(Attribute).IsAssignableFrom(type)) return false;
            if (typeof(Delegate).IsAssignableFrom(type)) return false;
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;

            var ns = type.Namespace;
            if (ns != null)
            {
                // Dot-qualified matching: a user namespace like "UnityGame" or
                // "SystemTools" must NOT be mistaken for the engine/framework.
                if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                    || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal)
                    || ns == "Mono" || ns.StartsWith("Mono.", StringComparison.Ordinal)
                    || ns == "Unity" || ns.StartsWith("Unity.", StringComparison.Ordinal)
                    || ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal)
                    || ns == "UnityEditor" || ns.StartsWith("UnityEditor.", StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when the type or any of its members declares an injection marker
        /// ([Inject]/[OptionalInject]/[Construct]). Only such types are DI candidates:
        /// a metadata failure on an unmarked type means it is simply never constructed
        /// through the container (SignalBus via instance binding, CommandPool via
        /// factory, SecureIntCore via direct new, iterator state machines, attributes)
        /// — not a real configuration problem. Deliberate trade-off: a type bound with
        /// <c>Bind&lt;T&gt;()</c> but never marked and lacking an injectable ctor is also
        /// silenced — unmarked types are outside the DI validation contract.
        /// </summary>
        private static bool HasInjectionMarkers(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (type.GetCustomAttributes(typeof(InjectAttribute), true).Length > 0) return true;
            if (type.GetCustomAttributes(typeof(ConstructAttribute), true).Length > 0) return true;
            if (type.GetCustomAttributes(typeof(OptionalInjectAttribute), true).Length > 0) return true;
            foreach (var ctor in type.GetConstructors(flags))
                if (ctor.GetCustomAttribute<InjectAttribute>() != null || ctor.GetCustomAttribute<ConstructAttribute>() != null) return true;
            foreach (var field in type.GetFields(flags))
                if (field.GetCustomAttribute<InjectAttribute>() != null || field.GetCustomAttribute<OptionalInjectAttribute>() != null) return true;
            foreach (var prop in type.GetProperties(flags))
                if (prop.GetCustomAttribute<InjectAttribute>() != null || prop.GetCustomAttribute<OptionalInjectAttribute>() != null) return true;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetCustomAttribute<InjectAttribute>() != null || method.GetCustomAttribute<OptionalInjectAttribute>() != null) return true;
                // Parameter markers: [OptionalInject] is the only inject-marker the
                // framework permits on parameters (InjectAttribute targets ctor/field/
                // property/method), and NexusDI honors it in the optional-parameter mask.
                foreach (var param in method.GetParameters())
                    if (param.GetCustomAttribute<InjectAttribute>() != null || param.GetCustomAttribute<OptionalInjectAttribute>() != null) return true;
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

            foreach (var type in EnumerateGameTypes(IncludeTestAssemblies))
            {
                if (type.FullName != null)
                {
                    availableTypeNames.Add(type.FullName);
                    if (!type.IsAbstract && !type.IsInterface && !type.IsEnum && !type.IsValueType && IsDiInspectableType(type))
                        scannedTypes.Add(type);
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
                    // Report ONLY when the type actually participates in DI. An unmarked
                    // type that cannot be metadata-built (SignalBus, CommandPool,
                    // SecureIntCore, ViewBinder, ...) is never constructed through the
                    // container, so its failure is expected — logging it was the bulk of
                    // the DI-validation noise (Bee/GLTFast/Protobuf/BCL/editor types all
                    // died at the assembly/namespace/shape filters; the remainder are
                    // framework internals that legitimately take value-type ctor params).
                    // Trade-off: a bound-but-unmarked type with an unsupported ctor is
                    // also silenced — see HasInjectionMarkers.
                    if (HasInjectionMarkers(type))
                    {
                        Debug.LogWarning($"[Nexus Warning] DI metadata inspection failed for '{type.FullName}': {ex.Message}");
                        warningCount++;
                    }
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
            // Built ONCE per script-reload: FindAssets("t:MonoScript") + GetClass() over every
            // script in the project is the single most expensive validation step, and it was
            // previously executed TWICE per run (ValidateModelOwnership + ValidateAsyncCallGraph).
            if (s_typeScriptCache != null) return s_typeScriptCache;

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
            s_typeScriptCache = cache;
            return cache;
        }

        private static Dictionary<string, Type> BuildSignalTypeMap()
        {
            // Cached per script-reload — the original rebuilt the whole struct-name map on
            // every call (ValidateAsyncCallGraph called it once per run).
            if (s_signalTypeMap != null) return s_signalTypeMap;

            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in EnumerateGameTypes(includeTests: true))
            {
                if (type.IsValueType && !type.IsEnum && !type.IsPrimitive)
                {
                    map[type.Name] = type;
                    map[type.FullName] = type;
                    string cleanFullName = type.FullName.Replace("+", ".");
                    map[cleanFullName] = type;
                }
            }
            s_signalTypeMap = map;
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
