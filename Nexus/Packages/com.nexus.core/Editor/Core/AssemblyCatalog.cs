using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Nexus.Core;

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
    public static class AssemblyCatalog
    {
        /// <summary>All assemblies currently loaded in the editor domain.</summary>
        public static IEnumerable<Assembly> LoadedAssemblies
            => UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();

        /// <summary>True for framework/runtime assemblies that no Nexus tool should scan.</summary>
        public static bool IsFrameworkAssembly(string name) => NexusAssemblyPolicy.IsFrameworkAssembly(name);

        /// <summary>True for known third-party assemblies that are not game code.</summary>
        public static bool IsThirdPartyAssembly(string name) => NexusAssemblyPolicy.IsThirdPartyAssembly(name);

        /// <summary>True for Unity test assemblies and '.testing' support assemblies.</summary>
        public static bool IsTestAssembly(string name) => NexusAssemblyPolicy.IsTestAssembly(name);

        /// <summary>True for editor-only assemblies (case-insensitive ".editor" in the name).</summary>
        public static bool IsEditorAssembly(string name) => NexusAssemblyPolicy.IsEditorAssembly(name);

        /// <summary>Safe simple-name accessor (never throws on corrupt assembly metadata).</summary>
        public static string GetSimpleName(Assembly assembly) => NexusAssemblyPolicy.GetSimpleName(assembly);

        /// <summary>
        /// The canonical "game-relevant" assembly universe: everything that is not a
        /// framework or third-party assembly, is not an editor-only assembly, and is
        /// not a test assembly (unless <paramref name="includeTests"/>). Editor test
        /// assemblies (name contains both ".editor" and "tests") ARE included when
        /// <paramref name="includeTests"/> is true — EditMode validation fixtures
        /// commonly live there.
        /// </summary>
        public static IEnumerable<Assembly> GameAssemblies(bool includeTests = false)
        {
            foreach (var assembly in LoadedAssemblies)
            {
                if (assembly.IsDynamic) continue;
                var name = GetSimpleName(assembly);
                if (IsFrameworkAssembly(name) || IsThirdPartyAssembly(name)) continue;
                bool isTest = IsTestAssembly(name);
                if (!includeTests && isTest) continue;
                // Editor assemblies are out of scope for game scanning — EXCEPT editor
                // test assemblies when test scanning is requested: validation fixtures
                // (e.g. com.nexus.core.editor.tests) host the dummy commands/handlers
                // that EditMode validation suites rely on. Without this exception,
                // IncludeTestAssemblies=true runs silently saw ZERO test types (the
                // name matches both ".editor" and "tests") and reported no violations.
                if (IsEditorAssembly(name) && !(includeTests && isTest)) continue;
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
        /// Game assemblies that reference the Nexus runtime, plus the runtime itself. Only these can contain
        /// [Inject] members, commands or network signals, so this is the exact universe for link.xml preservation.
        /// A precompiled SDK the name policy has never seen (an ads or store plugin) cannot reference Nexus and is excluded.
        /// </summary>
        public static IEnumerable<Assembly> NexusReferencingAssemblies()
        {
            string nexusRuntime = typeof(NexusDI).Assembly.GetName().Name;
            foreach (var assembly in GameAssemblies())
            {
                if (GetSimpleName(assembly) == nexusRuntime) { yield return assembly; continue; }
                bool usesNexus = false;
                AssemblyName[] references;
                try { references = assembly.GetReferencedAssemblies(); }
                catch { continue; }
                foreach (var reference in references)
                    if (reference.Name == nexusRuntime) { usesNexus = true; break; }
                if (usesNexus) yield return assembly;
            }
        }

        /// <summary>
        /// The subset of <see cref="NexusReferencingAssemblies"/> whose types the generated binder may name: the assembly
        /// that compiles <c>NexusGeneratedBinder.g.cs</c> itself and the assemblies it can reference. One unreferenceable
        /// type in the binder breaks compilation, and a project that does not compile cannot run the generator.
        /// When the compilation pipeline cannot answer, every Nexus-referencing assembly is returned.
        /// </summary>
        /// <param name="binderFolder">Folder of NexusGeneratedBinder.g.cs; it decides which assembly compiles the binder.</param>
        public static IEnumerable<Assembly> BinderAssemblies(string binderFolder)
        {
            string binderAssembly = null;
            HashSet<string> visible = null;
            try
            {
                string binderScript = System.IO.Path.Combine(binderFolder, "NexusGeneratedBinder.g.cs").Replace('\\', '/');
                binderAssembly = System.IO.Path.GetFileNameWithoutExtension(
                    UnityEditor.Compilation.CompilationPipeline.GetAssemblyNameFromScriptPath(binderScript));
                var compiled = UnityEditor.Compilation.CompilationPipeline
                    .GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies)
                    .FirstOrDefault(a => a.name == binderAssembly);
                if (compiled != null)
                {
                    visible = new HashSet<string>(compiled.assemblyReferences.Select(r => r.name));
                    foreach (var path in compiled.compiledAssemblyReferences)
                        visible.Add(System.IO.Path.GetFileNameWithoutExtension(path));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus] Could not resolve the binder's assembly references ({ex.Message}); every Nexus-referencing assembly is scanned.");
            }

            foreach (var assembly in NexusReferencingAssemblies())
            {
                string name = GetSimpleName(assembly);
                if (visible != null && name != binderAssembly && !visible.Contains(name)) continue;
                yield return assembly;
            }
        }

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
