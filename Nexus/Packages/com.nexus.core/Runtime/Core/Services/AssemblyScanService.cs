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
    public static class AssemblyScanService
    {
        // (revised) The cache is keyed by the Assembly INSTANCE, not its name.
        // A dynamic/in-memory assembly can have BOTH FullName and GetName().Name null, and the
        // old "<dynamic-assembly>" placeholder key made every unnamed assembly share one cache
        // entry (each served the first one's types). Reference identity has neither problem.
        private static readonly ConcurrentDictionary<Assembly, Type[]> s_typeCache = new();

        public static Type[] GetCachedTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();

            return s_typeCache.GetOrAdd(assembly, static asm =>
            {
                try
                {
                    return asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Partial load: keep the types that DID load instead of dropping them all.
                    LogScanFailure(asm, ex);
                    var valid = new List<Type>();
                    foreach (var t in ex.Types)
                    {
                        if (t != null) valid.Add(t);
                    }
                    return valid.ToArray();
                }
                catch (Exception ex)
                {
                    LogScanFailure(asm, ex);
                    return Array.Empty<Type>();
                }
            });
        }

        private static void LogScanFailure(Assembly assembly, Exception ex)
        {
            string assemblyName = assembly.FullName ?? "<dynamic-assembly>";
            string message = $"[Nexus] Type scan failed for assembly '{assemblyName}': {ex.GetType().Name}: {ex.Message}";
            var logger = NexusRuntime.Logger;
            if (logger != null) logger.LogWarning(message);
            else UnityEngine.Debug.LogWarning(message);
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
