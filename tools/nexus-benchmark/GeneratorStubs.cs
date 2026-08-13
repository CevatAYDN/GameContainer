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
    /// Mirrors the real editor AssemblyCatalog predicate (Nexus/Editor/Core/AssemblyCatalog.cs):
    /// runtime-relevant assemblies = loaded, non-dynamic, non-framework, non-third-party,
    /// non-editor, non-test assemblies. In the harness only the benchmark assembly qualifies,
    /// so GenerateBinder() scans the REAL harness + compiled runtime types — the same universe
    /// the codegen would scan inside Unity.
    /// </summary>
    public static class AssemblyCatalog
    {
        private static readonly string[] FrameworkPrefixes =
        {
            "System", "Microsoft", "Unity", "mscorlib", "mono", "nunit", "NUnit", "netstandard"
        };

        public static IEnumerable<Assembly> RuntimeAssemblies(bool includeTests = false)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                string name = null;
                try { name = assembly.GetName().Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;

                bool framework = false;
                for (int i = 0; i < FrameworkPrefixes.Length; i++)
                {
                    if (name.StartsWith(FrameworkPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        framework = true;
                        break;
                    }
                }
                if (framework) continue;
                if (name.IndexOf(".editor", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!includeTests && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                yield return assembly;
            }
        }

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
