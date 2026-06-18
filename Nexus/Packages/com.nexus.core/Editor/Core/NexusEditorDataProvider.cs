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
        private static bool s_mappingsValid;

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_mappingsValid = false;
            s_handlerCountValid = false;
        }

        internal static List<HandlerMapping> GetHandlerMappings()
        {
            if (s_mappingsValid && s_cachedMappings != null)
                return s_cachedMappings;

            s_cachedMappings = new List<HandlerMapping>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName;
                if (name.StartsWith("System") || name.StartsWith("Microsoft") || name.StartsWith("Unity") || name.StartsWith("mscorlib"))
                    continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;

                        foreach (var attr in type.GetCustomAttributes<SignalHandlerAttribute>())
                        {
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
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            s_cachedMappings.Sort((a, b) => string.Compare(a.SignalName, b.SignalName, StringComparison.Ordinal));
            s_mappingsValid = true;
            return s_cachedMappings;
        }

        internal static int GetHandlerCount()
        {
            if (s_handlerCountValid)
                return s_cachedHandlerCount;

            int count = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName;
                if (name.StartsWith("System") || name.StartsWith("Microsoft") || name.StartsWith("Unity") || name.StartsWith("mscorlib"))
                    continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract)
                        {
                            if (type.GetCustomAttributes(typeof(SignalHandlerAttribute), true).Length > 0 ||
                                type.GetCustomAttributes(typeof(CompositeSignalHandlerAttribute), true).Length > 0)
                                count++;
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            s_cachedHandlerCount = count;
            s_handlerCountValid = true;
            return count;
        }

        private static int s_cachedHandlerCount;
        private static bool s_handlerCountValid;

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
