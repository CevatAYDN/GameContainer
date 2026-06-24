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
        }

        private static void EnsureCached()
        {
            if (s_cacheValid && s_cachedMappings != null)
                return;

            s_cachedMappings = new List<HandlerMapping>();
            s_cachedHandlerCount = 0;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName;
                if (name.StartsWith("System") || name.StartsWith("Microsoft") || name.StartsWith("Unity") || name.StartsWith("mscorlib") || name.StartsWith("nunit"))
                    continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
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
                catch (ReflectionTypeLoadException) { }
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
            return s_cachedHandlerCount;
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
    }

    internal struct HandlerMapping
    {
        internal string SignalName;
        internal string CommandName;
        internal string Mode;
    }
}
