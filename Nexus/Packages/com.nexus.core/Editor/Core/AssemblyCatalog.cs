using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Nexus.Editor
{
    /// <summary>
    /// Deep module owning the "which assemblies/types do editor tools scan?" decision.
    ///
    /// The <c>GetLoadedAssemblies() → filter → GetTypes()</c> loop was previously
    /// re-implemented ~24 times across 11 editor files with four divergent filter
    /// predicates, so tools disagreed about what code they could see (e.g. whether
    /// UnityEditor assemblies, test assemblies, or third-party assemblies were in
    /// scope). This catalog centralises the iteration, the predicates, and the safe
    /// type enumeration so every tool sees the same universe of game-relevant types.
    ///
    /// The runtime-side equivalent (player builds cannot reference this editor module)
    /// lives in <see cref="Nexus.Core.Context"/> (<c>GetDefaultScanAssemblies</c> /
    /// <c>GetTypesSafely</c>).
    /// </summary>
    public static class AssemblyCatalog
    {
        private static readonly string[] FrameworkPrefixes =
        {
            "System", "Microsoft", "Unity", "mscorlib", "mono", "nunit", "NUnit", "netstandard"
        };

        // Unity-bundled / third-party assemblies that are not game code.
        private static readonly string[] ThirdPartyPrefixes =
        {
            "Newtonsoft", "Grpc", "ExCSS", "log4net", "TextMateSharp", "JetBrains",
            "Onigwrap", "unityplastic", "Codice", "Plastic", "MCPForUnity"
        };

        /// <summary>All assemblies currently loaded in the editor domain.</summary>
        public static IEnumerable<Assembly> LoadedAssemblies
            => UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();

        /// <summary>True for framework/runtime assemblies that no Nexus tool should scan.</summary>
        public static bool IsFrameworkAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var prefix in FrameworkPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True for known third-party assemblies that are not game code.</summary>
        public static bool IsThirdPartyAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var prefix in ThirdPartyPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True for Unity test assemblies (case-insensitive "tests" in the name).</summary>
        public static bool IsTestAssembly(string name)
            => !string.IsNullOrEmpty(name) && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// True for editor-only assemblies (case-insensitive ".editor" in the name,
        /// e.g. com.nexus.core.editor). The runtime binder must never reference these.
        /// </summary>
        public static bool IsEditorAssembly(string name)
            => !string.IsNullOrEmpty(name) && name.IndexOf(".editor", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Safe simple-name accessor (never throws on corrupt assembly metadata).</summary>
        public static string GetSimpleName(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return null;
            try { return assembly.GetName().Name; }
            catch { return null; }
        }

        /// <summary>
        /// The canonical "game-relevant" assembly universe: everything that is not a
        /// framework or third-party assembly and is not a test assembly (unless
        /// <paramref name="includeTests"/>).
        /// </summary>
        public static IEnumerable<Assembly> GameAssemblies(bool includeTests = false)
        {
            foreach (var assembly in LoadedAssemblies)
            {
                if (assembly.IsDynamic) continue;
                var name = GetSimpleName(assembly);
                if (IsFrameworkAssembly(name) || IsThirdPartyAssembly(name)) continue;
                if (!includeTests && IsTestAssembly(name)) continue;
                if (IsEditorAssembly(name)) continue;
                yield return assembly;
            }
        }

        /// <summary>
        /// Runtime-only assembly set used by binder/codegen paths. Editor-only assemblies
        /// are excluded so runtime generation never references editor types.
        /// </summary>
        public static IEnumerable<Assembly> RuntimeAssemblies(bool includeTests = false)
            => GameAssemblies(includeTests);

        /// <summary>
        /// Safely enumerates a loaded assembly's types. On a <see cref="ReflectionTypeLoadException"/>
        /// it logs a single warning (once per assembly) and yields the types that did load;
        /// it never throws.
        /// </summary>
        public static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                LogTypeLoadWarning(assembly, ex);
                types = ex.Types;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus] Failed to scan assembly '{GetSimpleName(assembly)}': {ex.Message}");
                yield break;
            }

            if (types == null) yield break;
            foreach (var t in types)
            {
                if (t != null) yield return t;
            }
        }

        private static readonly HashSet<string> s_warnedAssemblies = new HashSet<string>();

        private static void LogTypeLoadWarning(Assembly assembly, ReflectionTypeLoadException ex)
        {
            var name = GetSimpleName(assembly);
            if (name == null || !s_warnedAssemblies.Add(name)) return; // warn once per assembly
            var first = ex.LoaderExceptions?.FirstOrDefault(e => e != null)?.Message;
            Debug.LogWarning($"[Nexus] Partial type load in '{name}' — some types skipped. {first}");
        }
    }
}
