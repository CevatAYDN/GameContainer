// Stubs so NexusCodeGenerator.cs (an editor-only file) can be syntax/compile
// validated outside Unity. The generator is the source of truth for the AOT
// binder; compiling it here catches typos that would break the Nexus.Editor
// assembly inside Unity.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MenuItemAttribute : Attribute
    {
        public MenuItemAttribute(string itemName) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction) { }
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

        public static Type[] GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch { return Array.Empty<Type>(); }
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
