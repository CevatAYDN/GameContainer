// Stubs so NexusCodeGenerator.cs (an editor-only file) can be syntax/compile
// validated outside Unity. The generator is the source of truth for the AOT
// binder; compiling it here catches typos that would break the Nexus.Editor
// assembly inside Unity.
using System;
using System.Collections.Generic;
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
    public static class AssemblyCatalog
    {
        public static IEnumerable<Assembly> RuntimeAssemblies() => Array.Empty<Assembly>();
        public static Type[] GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch { return Array.Empty<Type>(); }
        }
    }

    public sealed class NexusEditorSettings
    {
        public string BinderOutputPath => "Assets/Scripts/Nexus";
        public string LinkXmlOutputPath => "Assets/Scripts/Nexus";
        public static NexusEditorSettings GetOrCreateSettings() => new NexusEditorSettings();
    }
}
