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
        // A name matches an entry when it is the entry or starts with the entry and a dot, ignoring case: "System" matches
        // "System" and "System.Core", never "Systems.Gameplay" (#191). Each entry is a whole assembly-name segment, so a
        // game assembly is never taken for framework or tool code because its name starts with the same letters.
        private static readonly string[] FrameworkNames =
        {
            "System", "Microsoft", "Unity", "UnityEngine", "UnityEditor", "UnityPipeline", "mscorlib", "Mono", "monodoc", "nunit",
            "netstandard", "StandardSocketsHttpHandler", "SystemWebTestShim"
        };

        private static readonly string[] ThirdPartyNames =
        {
            "Newtonsoft", "Grpc", "ExCSS", "log4net", "TextMateSharp", "JetBrains", "Onigwrap", "unityplastic", "Codice", "Plastic",
            "MCPForUnity", "Bee", "BeeBuildProgramCommon", "NiceIO", "GLTFast", "Google.Protobuf", "I18N", "AndroidPlayerBuildProgram",
            "PlayerBuildProgram", "PlayerBuildProgramLibrary", "ScriptCompilation", "ScriptCompilationBuildProgram",
            "WinPlayerBuildProgram", "BuildProgram"
        };

        private static bool MatchesName(string name, string[] entries)
        {
            foreach (var entry in entries)
            {
                if (name.StartsWith(entry, StringComparison.OrdinalIgnoreCase) && (name.Length == entry.Length || name[entry.Length] == '.'))
                    return true;
            }
            return false;
        }

        /// <summary>True for framework/runtime assemblies that should never be scanned for DI/signals.</summary>
        public static bool IsFrameworkAssembly(string name)
            => string.IsNullOrEmpty(name) || MatchesName(name, FrameworkNames);

        /// <summary>True for third-party or build-driver assemblies that are not game code.</summary>
        public static bool IsThirdPartyAssembly(string name)
            => !string.IsNullOrEmpty(name) && MatchesName(name, ThirdPartyNames);

        private static readonly string[] NexusPackagePrefixes = { "com.nexus.", "Nexus.Ads." };

        /// <summary>
        /// True for the assemblies of the Nexus packages: <c>com.nexus.*</c>, and <c>Nexus.Ads.*</c> in the ad family.
        /// A game's generated binder and link.xml register none of their types; each package preserves its own (#110).
        /// </summary>
        public static bool IsNexusPackageAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < NexusPackagePrefixes.Length; i++)
            {
                if (name.StartsWith(NexusPackagePrefixes[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// True for test assemblies: a name segment that is "Tests" (any case) or ends in "Tests" ("Game.Tests",
        /// "Gigawatt.FrameworkTests", "ChipCrisis.Tests.EditMode"), and test-support assemblies ending in ".testing".
        /// "Contests.Runtime" is game code.
        /// </summary>
        public static bool IsTestAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.EndsWith(".testing", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var segment in name.Split('.'))
            {
                if (segment.Equals("tests", StringComparison.OrdinalIgnoreCase) || segment.EndsWith("Tests", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True for editor assemblies: a name segment that is "Editor" (any case) or starts with "Editor" and a capital or a
        /// digit ("Game.EditorTools"), and Unity's default Assembly-CSharp-Editor and Assembly-CSharp-Editor-firstpass.
        /// "Game.Editorial" is game code.
        /// </summary>
        public static bool IsEditorAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.EndsWith("-Editor", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-Editor-firstpass", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var segment in name.Split('.'))
            {
                if (segment.Equals("editor", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.Length > 6 && segment.StartsWith("Editor", StringComparison.Ordinal)
                    && (char.IsUpper(segment[6]) || char.IsDigit(segment[6]))) return true;
            }
            return false;
        }

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
