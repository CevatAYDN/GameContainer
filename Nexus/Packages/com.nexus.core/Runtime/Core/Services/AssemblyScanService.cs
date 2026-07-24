using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Manages static assembly reflection caching and type discovery.
    /// Decouples type scanning from the Context container.
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

        public static void ClearCache()
        {
            s_typeCache.Clear();
        }
    }
}
