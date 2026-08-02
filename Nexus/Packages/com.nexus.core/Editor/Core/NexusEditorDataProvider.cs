using System;
using System.Collections.Generic;
using System.Reflection;
using Nexus.Core;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Nexus.Editor
{
    internal static class NexusEditorDataProvider
    {
        // ── Cached handler mappings ─────────────────────────────
        private static List<HandlerMapping> s_cachedMappings;
        private static int s_cachedHandlerCount;
        private static bool s_cacheValid;

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_cacheValid = false;
            InvalidateRootCache();
        }

        [InitializeOnLoadMethod]
        private static void RegisterHierarchyEvents()
        {
            EditorApplication.hierarchyChanged -= InvalidateRootCache;
            EditorApplication.hierarchyChanged += InvalidateRootCache;
        }

        private static void EnsureCached()
        {
            if (s_cacheValid && s_cachedMappings != null)
                return;

            s_cachedMappings = new List<HandlerMapping>();
            s_cachedHandlerCount = 0;

            foreach (var assembly in AssemblyCatalog.GameAssemblies())
            {
                try
                {
                    foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                    {
                        if (!type.IsClass || type.IsAbstract) continue;

                        bool hasHandlers = false;

                        foreach (var attr in type.GetCustomAttributes<SignalHandlerAttribute>())
                        {
                            hasHandlers = true;
                            s_cachedMappings.Add(new HandlerMapping
                            {
                                SignalName = attr.SignalType.Name,
                                CommandName = type.Name,
                                Mode = attr.Mode.ToString()
                            });
                        }

                        var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                        if (compositeAttr != null)
                        {
                            hasHandlers = true;
                            var sigs = new System.Text.StringBuilder();
                            foreach (var s in compositeAttr.SignalTypes)
                            {
                                if (sigs.Length > 0) sigs.Append(" + ");
                                sigs.Append(s.Name);
                            }
                            s_cachedMappings.Add(new HandlerMapping
                            {
                                SignalName = sigs.ToString(),
                                CommandName = type.Name,
                                Mode = compositeAttr.OneShot ? "OneShot" : "Re-trigger"
                            });
                        }

                        if (hasHandlers)
                        {
                            s_cachedHandlerCount++;
                        }
                    }
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    // CONTRIBUTING.md: never swallow reflection load failures silently.
                    foreach (var le in rtle.LoaderExceptions ?? Array.Empty<Exception>())
                        Debug.LogWarning($"[NexusDataProvider] Skipping assembly '{assembly.FullName}': {le?.Message}");
                }
            }

            s_cachedMappings.Sort((a, b) => string.Compare(a.SignalName, b.SignalName, StringComparison.Ordinal));
            s_cacheValid = true;
        }

        internal static List<HandlerMapping> GetHandlerMappings()
        {
            EnsureCached();
            return s_cachedMappings;
        }

        internal static int GetHandlerCount()
        {
            EnsureCached();
            int runtimeCount = 0;
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts != null)
            {
                foreach (var ctx in contexts)
                {
                    if (ctx?.SignalBus == null) continue;

                    var handlers = ctx.SignalBus.RegisteredHandlers;
                    if (handlers != null)
                    {
                        foreach (var kvp in handlers)
                            runtimeCount += kvp.Value.Count;
                    }
                }
            }
            return s_cachedHandlerCount + runtimeCount;
        }

        // ── Scene roots ─────────────────────────────────────────
        private static Root[] s_cachedSceneRoots;
        private static double s_lastRootCacheTime;
        private const double RootCacheDuration = 1.0;

        internal static Root[] GetSceneRoots()
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_cachedSceneRoots == null || now - s_lastRootCacheTime > RootCacheDuration)
            {
                s_cachedSceneRoots = UnityEngine.Object.FindObjectsByType<Root>();
                s_lastRootCacheTime = now;
            }
            return s_cachedSceneRoots;
        }

        internal static void InvalidateRootCache()
        {
            s_cachedSceneRoots = null;
        }

        // ── Active contexts ─────────────────────────────────────
        internal static IReadOnlyList<IContext> GetActiveContexts()
        {
            return NexusRuntime.ActiveContexts;
        }

        internal static int GetActiveContextCount()
        {
            var ctx = NexusRuntime.ActiveContexts;
            return ctx?.Count ?? 0;
        }

        internal static bool IsPlaying => Application.isPlaying;

        // ── Live Service / Model Data (Play Mode only) ──────────
        /// <summary>Returns all service types registered in a context's builder.</summary>
        internal static IReadOnlyList<Type> GetLiveServiceTypes(IContext context)
        {
            if (context is not Context ctx) return Array.Empty<Type>();
            try
            {
                var builder = ctx.Builder;
                return builder?.ServiceTypes ?? Array.Empty<Type>();
            }
            catch (Exception ex) { Debug.LogWarning($"[NexusDataProvider] Failed to get live service types: {ex.Message}"); return Array.Empty<Type>(); }
        }

        /// <summary>Attempts to safely resolve a service instance from a context.</summary>
        internal static object TryGetServiceInstance(IContext context, Type serviceType)
        {
            try
            {
                if (context is Context ctx && ctx.Container.IsRegistered(serviceType))
                    return ctx.Container.Resolve(serviceType);
            }
            catch (Exception ex) { Debug.LogWarning($"[NexusDataProvider] Failed to resolve service '{serviceType.Name}': {ex.Message}"); }
            return null;
        }

        /// <summary>Returns all binding types registered in the context's DI container.</summary>
        internal static Dictionary<Type, Type> GetAllBindings(IContext context)
        {
            var result = new Dictionary<Type, Type>();
            if (context is not Context ctx || ctx.Container == null) return result;
            try
            {
                foreach (var (interfaceType, concreteType) in ctx.Container.GetEditorTypeMappings())
                    result[interfaceType] = concreteType;
            }
            catch (Exception ex) { Debug.LogWarning($"[NexusDataProvider] Failed to get type mappings: {ex.Message}"); }
            return result;
        }

        /// <summary>Returns all resolved singleton instances in a context's DI container.</summary>
        internal static List<object> GetResolvedSingletons(IContext context)
        {
            var result = new List<object>();
            if (context is not Context ctx || ctx.Container == null) return result;
            try
            {
                var singletons = ctx.Container.EditorResolvedSingletons;
                result.AddRange(singletons);
            }
            catch (Exception ex) { Debug.LogWarning($"[NexusDataProvider] Failed to get resolved singletons: {ex.Message}"); }
            return result;
        }
    }

    internal struct HandlerMapping
    {
        internal string SignalName;
        internal string CommandName;
        internal string Mode;
    }
}
