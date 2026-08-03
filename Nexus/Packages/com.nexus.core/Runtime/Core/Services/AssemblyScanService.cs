using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Manages static assembly reflection caching and type discovery.
    /// Decouples type scanning from the Context container.
    /// Cache is cleared automatically by <see cref="NexusRuntime.Reset"/> on domain reload
    /// and play-mode transitions to prevent stale type arrays from being returned after scripts
    /// are recompiled (e.g. Enter Play Mode with domain reload disabled).
    /// </summary>
    public sealed class AssemblyScanService
    {
        private static readonly ConcurrentDictionary<string, Type[]> s_typeCache = new();

        public static Type[] GetCachedTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();

            return s_typeCache.GetOrAdd(assembly.FullName ?? assembly.GetName().Name, _ =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    var valid = new List<Type>();
                    foreach (var t in ex.Types)
                    {
                        if (t != null) valid.Add(t);
                    }
                    return valid.ToArray();
                }
                catch
                {
                    return Array.Empty<Type>();
                }
            });
        }

        /// <summary>
        /// Clears the type cache. Called by <see cref="NexusRuntime.Reset"/> to ensure
        /// domain-reload-safe behaviour when Enter Play Mode Options has domain reload disabled.
        /// </summary>
        public static void ClearCache()
        {
            s_typeCache.Clear();
        }
    }
}
