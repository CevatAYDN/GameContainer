using System;
using System.Reflection;

namespace Nexus.Core
{
    /// <summary>
    /// Single source of truth for assembly and type scanning policies across runtime and editor.
    /// Eliminates assembly scan duplication and policy divergence between AssemblyCatalog, Context,
    /// and NexusCodeGenerator.
    /// </summary>
    public static class NexusAssemblyPolicy
    {
        private static readonly string[] FrameworkPrefixes =
        {
            "System", "Microsoft", "Unity", "mscorlib", "mono", "nunit", "NUnit", "netstandard"
        };

        private static readonly string[] ThirdPartyPrefixes =
        {
            "Newtonsoft", "Grpc", "ExCSS", "log4net", "TextMateSharp", "JetBrains",
            "Onigwrap", "unityplastic", "Codice", "Plastic", "MCPForUnity",
            "Bee", "NiceIO", "GLTFast", "Google.Protobuf", "I18N", "AndroidPlayerBuildProgram",
            "PlayerBuildProgram", "ScriptCompilation", "WinPlayerBuildProgram", "BuildProgram"
        };

        /// <summary>True for framework/runtime assemblies that should never be scanned for DI/signals.</summary>
        public static bool IsFrameworkAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            for (int i = 0; i < FrameworkPrefixes.Length; i++)
            {
                if (name.StartsWith(FrameworkPrefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True for third-party or build-driver assemblies that are not game code.</summary>
        public static bool IsThirdPartyAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < ThirdPartyPrefixes.Length; i++)
            {
                if (name.StartsWith(ThirdPartyPrefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True for test assemblies containing 'tests' in the simple name.</summary>
        public static bool IsTestAssembly(string name)
            => !string.IsNullOrEmpty(name) && name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True for editor-only assemblies containing '.editor' in the simple name.</summary>
        public static bool IsEditorAssembly(string name)
            => !string.IsNullOrEmpty(name) && name.IndexOf(".editor", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Returns true when an assembly is part of game runtime/code (not framework/3rd-party/editor/test).</summary>
        public static bool IsGameAssembly(Assembly assembly, bool includeTests = false)
        {
            if (assembly == null || assembly.IsDynamic) return false;
            string name = GetSimpleName(assembly);
            if (string.IsNullOrEmpty(name)) return false;
            if (IsFrameworkAssembly(name) || IsThirdPartyAssembly(name)) return false;
            bool isTest = IsTestAssembly(name);
            if (!includeTests && isTest) return false;
            if (IsEditorAssembly(name) && !(includeTests && isTest)) return false;
            return true;
        }

        /// <summary>Safe simple-name reader (never throws).</summary>
        public static string GetSimpleName(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return null;
            try { return assembly.GetName().Name; }
            catch { return null; }
        }
    }
}
