// Stubs so NexusCodeGenerator.cs (an editor-only file) can be syntax/compile
// validated outside Unity. The generator is the source of truth for the AOT
// binder; compiling it here catches typos that would break the Nexus.Editor
// assembly inside Unity.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MenuItemAttribute : Attribute
    {
        public MenuItemAttribute(string itemName) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction, int priority) { }
    }

    public static class Menu
    {
        public static void SetChecked(string menuPath, bool isChecked) { }
    }

    public static class EditorPrefs
    {
        public static bool GetBool(string key, bool defaultValue = false) => defaultValue;
        public static void SetBool(string key, bool value) { }
    }

    public static class AssetDatabase
    {
        public static void Refresh() { }
    }
}

namespace UnityEditor.Callbacks
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DidReloadScriptsAttribute : Attribute
    {
        public DidReloadScriptsAttribute() { }
    }
}

namespace Nexus.Editor
{
    /// <summary>
    /// Harness mirror of <c>Nexus/Editor/Core/AssemblyCatalog.cs</c>. Delegates predicates to
    /// <see cref="Nexus.Core.NexusAssemblyPolicy"/> so editor codegen and runtime share one policy.
    /// </summary>
    public static class AssemblyCatalog
    {
        public static IEnumerable<Assembly> LoadedAssemblies
            => AppDomain.CurrentDomain.GetAssemblies();

        public static bool IsFrameworkAssembly(string name) => Nexus.Core.NexusAssemblyPolicy.IsFrameworkAssembly(name);
        public static bool IsThirdPartyAssembly(string name) => Nexus.Core.NexusAssemblyPolicy.IsThirdPartyAssembly(name);
        public static bool IsTestAssembly(string name) => Nexus.Core.NexusAssemblyPolicy.IsTestAssembly(name);
        public static bool IsEditorAssembly(string name) => Nexus.Core.NexusAssemblyPolicy.IsEditorAssembly(name);
        public static bool IsNexusPackageAssembly(string name) => Nexus.Core.NexusAssemblyPolicy.IsNexusPackageAssembly(name);
        public static string GetSimpleName(Assembly assembly) => Nexus.Core.NexusAssemblyPolicy.GetSimpleName(assembly);

        public static IEnumerable<Assembly> GameAssemblies(bool includeTests = false)
        {
            foreach (var assembly in LoadedAssemblies)
            {
                if (Nexus.Core.NexusAssemblyPolicy.IsGameAssembly(assembly, includeTests))
                    yield return assembly;
            }
        }

        public static IEnumerable<Assembly> RuntimeAssemblies(bool includeTests = false)
            => GameAssemblies(includeTests);

        /// <summary>Mirror of the editor rule: game assemblies that reference the Nexus runtime, plus the runtime itself.</summary>
        public static IEnumerable<Assembly> NexusReferencingAssemblies()
        {
            string nexusRuntime = typeof(Nexus.Core.NexusDI).Assembly.GetName().Name;
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
        /// The editor narrows this to what the binder's own assembly can reference via CompilationPipeline; the harness
        /// has no compilation pipeline, so it takes the documented fallback: every Nexus-referencing assembly.
        /// </summary>
        public static IEnumerable<Assembly> BinderAssemblies(string binderFolder) => NexusReferencingAssemblies();

        /// <summary>Source files and defines a harness test hands to the binder's #if reading (CG12), and the binder's assembly.</summary>
        public static Dictionary<string, (string[] Files, string[] Defines)> TestSources;
        public static string TestBinderAssembly;

        /// <summary>No compilation pipeline in the harness: no sources unless a test sets them, so the binder names every type without its #if.</summary>
        public static Dictionary<string, (string[] Files, string[] Defines)> SourceAssemblies(string binderFolder, out string binderAssembly)
        {
            binderAssembly = TestSources == null ? null : TestBinderAssembly;
            return TestSources ?? new Dictionary<string, (string[] Files, string[] Defines)>();
        }

        /// <summary>As the editor's: a partial load returns the types that loaded and warns, so a partial-load regression shows (#198).</summary>
        public static Type[] GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                Console.WriteLine($"[Nexus] Partial type load in '{assembly.GetName().Name}' — some types skipped. {ex.LoaderExceptions?.FirstOrDefault(e => e != null)?.Message}");
                return ex.Types.Where(t => t != null).ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Nexus] Failed to scan assembly '{assembly.GetName().Name}': {ex.Message}");
                return Array.Empty<Type>();
            }
        }
    }

    /// <summary>
    /// Writes the generated binder to a temp directory so the harness can compile it with
    /// Roslyn and boot it — the real editor writes into Assets/, which the harness must not
    /// touch. The temp dir also lets the test verify the emitted file's contents directly.
    /// </summary>
    public sealed class NexusEditorSettings
    {
        public static readonly string OutputRoot = Path.Combine(Path.GetTempPath(), "NexusCodeGenHarness");
        public string BinderOutputPath => OutputRoot;
        public string LinkXmlOutputPath => OutputRoot;
        public static NexusEditorSettings GetOrCreateSettings() => new NexusEditorSettings();
    }
}
