using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace Nexus.Editor
{
    /// <summary>
    /// Finds a Nexus assembly whose versionDefines symbol is off although the package the symbol names is installed. Unity then
    /// compiles the code under the symbol out, or does not compile an assembly the symbol gates through its defineConstraints,
    /// and says nothing: com.nexus.ads.levelplay at expression 9.7.0 with LevelPlay 9.6.0 built an empty assembly and every check
    /// stayed green (issue #143). Unity evaluates the expressions; this reads the outcome, the defines it compiled each assembly
    /// with. The editor logs each finding as an error after every script reload, and <see cref="VersionDefineBuildGuard"/> stops
    /// a player build on it. Not installed is not a finding: the package is optional and its code is meant to stay out.
    /// </summary>
    public static class VersionDefineGuard
    {
        public const string Fix = "Move the package to a version the expression holds, or pin the Nexus package at a commit whose expression the installed version meets.";

        [Serializable] private sealed class Asmdef { public string name; public string[] defineConstraints; public VersionDefine[] versionDefines; }
        [Serializable] private sealed class VersionDefine { public string name; public string expression; public string define; }

        /// <summary>
        /// The findings in a set of asmdef texts. <paramref name="installedVersion"/> answers a package's installed version, or
        /// null when it is not installed; <paramref name="compiledDefines"/> answers the defines Unity compiled an assembly with,
        /// or null when it compiled no assembly of that name. Test assemblies (a UNITY_INCLUDE_TESTS constraint) are left out.
        /// </summary>
        public static List<string> Find(IEnumerable<string> asmdefJsons, Func<string, string> installedVersion, Func<string, ICollection<string>> compiledDefines)
        {
            var findings = new List<string>();
            foreach (string json in asmdefJsons)
            {
                var asmdef = JsonUtility.FromJson<Asmdef>(json);
                if (asmdef?.versionDefines == null || asmdef.versionDefines.Length == 0) continue;
                var constraints = asmdef.defineConstraints ?? Array.Empty<string>();
                if (constraints.Any(c => c.Contains("UNITY_INCLUDE_TESTS"))) continue;

                var defines = compiledDefines(asmdef.name);
                if (defines != null)
                {
                    foreach (var d in asmdef.versionDefines)
                    {
                        string installed = installedVersion(d.name);
                        if (installed != null && !defines.Contains(d.define))
                            findings.Add(asmdef.name + " is compiled without " + d.define + ": " + Needs(d, installed) + ", so the code under that symbol is in neither the editor nor the player.");
                    }
                    continue;
                }

                // an assembly the symbols gate is not compiled at all; with every gating package installed, an expression is not met
                var gating = asmdef.versionDefines.Where(d => constraints.Contains(d.define)).ToList();
                var versions = gating.Select(d => installedVersion(d.name)).ToList();
                if (gating.Count == 0 || versions.Any(v => v == null)) continue;
                findings.Add(asmdef.name + " is not compiled although every package its defineConstraints name is installed: " +
                    string.Join("; ", gating.Select((d, i) => Needs(d, versions[i]))) + ".");
            }
            return findings;
        }

        // Unity reads a bare version as that version or newer and brackets as an interval (Assembly Definition manual, 6000.6)
        private static string Needs(VersionDefine d, string installed)
        {
            bool interval = d.expression.StartsWith("[", StringComparison.Ordinal) || d.expression.StartsWith("(", StringComparison.Ordinal);
            return d.define + " needs " + d.name + (interval ? " in " + d.expression : " " + d.expression + " or newer") + ", the project has " + installed;
        }

        /// <summary>The findings in the open project: the asmdefs of every registered com.nexus.* package against what Unity compiled.</summary>
        public static List<string> FindInProject()
        {
            var packages = new Dictionary<string, string>(StringComparer.Ordinal);
            var asmdefs = new List<string>();
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                packages[package.name] = package.version;
                if (!package.name.StartsWith("com.nexus.", StringComparison.Ordinal) || !Directory.Exists(package.resolvedPath)) continue;
                foreach (string file in Directory.GetFiles(package.resolvedPath, "*.asmdef", SearchOption.AllDirectories))
                {
                    // folders ending in ~ (Samples~) are not imported, so their asmdefs compile nothing
                    string relative = file.Substring(package.resolvedPath.Length);
                    if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.EndsWith("~", StringComparison.Ordinal))) continue;
                    asmdefs.Add(File.ReadAllText(file));
                }
            }
            var compiled = new Dictionary<string, ICollection<string>>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor)) compiled[assembly.name] = new HashSet<string>(assembly.defines);
            return Find(asmdefs, name => packages.TryGetValue(name, out var version) ? version : null, name => compiled.TryGetValue(name, out var defines) ? defines : null);
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void ReportAfterScriptReload()
        {
            foreach (string finding in FindInProject()) Debug.LogError("[Nexus] " + finding + " " + Fix);
        }
    }

    /// <summary>Stops a player build while <see cref="VersionDefineGuard"/> has a finding (issue #143).</summary>
    public sealed class VersionDefineBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var findings = VersionDefineGuard.FindInProject();
            if (findings.Count > 0) throw new BuildFailedException("[Nexus] " + string.Join(" ", findings) + " " + VersionDefineGuard.Fix);
        }
    }
}
