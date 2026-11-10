#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace NexusVerify
{
    /// <summary>Compile real Android player assemblies and check #412's release exclusion using Cecil metadata.</summary>
    public static class RewardFaultBuildCheck
    {
        public static void Run()
            => RunChecks(requireProviders: true);

        // The development project has no live SDK installed; provider proofs keep
        // using Run, while #416 verifies the Unity Intent reader itself here.
        public static void RunIntentOnly()
            => RunChecks(requireProviders: false);

        private static void RunChecks(bool requireProviders)
        {
            try
            {
                string output = Environment.GetEnvironmentVariable("NEXUS_FAULT_CHECK_OUTPUT");
                if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("NEXUS_FAULT_CHECK_OUTPUT is required.");
                var cecil = UnityEngine.Assemblies.CurrentAssemblies.LoadFromPath(Path.Combine(EditorApplication.applicationContentsPath,
                    "il2cpp/build/deploy/Mono.Cecil.dll"));
                var read = cecil.GetType("Mono.Cecil.AssemblyDefinition", true).GetMethod("ReadAssembly", new[] { typeof(string) });
                foreach (bool development in new[] { true, false })
                {
                    string folder = Path.Combine(output, development ? "development" : "release");
                    Directory.CreateDirectory(folder);
                    var result = PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings
                    {
                        target = BuildTarget.Android, group = BuildTargetGroup.Android,
                        options = development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None
                    }, folder);
                    if (result.assemblies == null || result.assemblies.Count == 0)
                        throw new InvalidOperationException("Player assembly compilation failed.");
                    int ads = 0, providers = 0, intents = 0;
                    foreach (string file in Directory.GetFiles(folder, "Nexus.Ads.*.dll"))
                    {
                        using var assembly = (IDisposable)read.Invoke(null, new object[] { file });
                        object module = assembly.GetType().GetProperty("MainModule").GetValue(assembly);
                        var types = (System.Collections.IEnumerable)module.GetType().GetProperty("Types").GetValue(module);
                        foreach (object type in types)
                        {
                            string name = (string)type.GetType().GetProperty("FullName").GetValue(type);
                            if (name == "Nexus.Ads.Unity.RewardFaultInjection") ads++;
                            if (name == "Nexus.Ads.Unity.RewardFaultIntent")
                            {
                                intents++;
                                var intentMethods = (System.Collections.IEnumerable)type.GetType().GetProperty("Methods").GetValue(type);
                                var names = intentMethods.Cast<object>().Select(m => (string)m.GetType().GetProperty("Name").GetValue(m)).ToArray();
                                if (names.Contains("Update") || !names.Contains("Start") || !names.Contains("OnApplicationFocus"))
                                    throw new InvalidOperationException("Fault intent must use lifecycle notifications, never idle polling (#416).");
                            }
                            if (!development && (name.Contains("RewardFault") || name.Contains("IRewardFault")))
                                throw new InvalidOperationException("Release contains fault type: " + name);
                            if (name == "Nexus.Ads.LevelPlay.LevelPlayProvider" || name == "Nexus.Ads.AdMob.AdMobProvider")
                            {
                                providers++;
                                var methods = (System.Collections.IEnumerable)type.GetType().GetProperty("Methods").GetValue(type);
                                bool getter = methods.Cast<object>().Any(m => (string)m.GetType().GetProperty("Name").GetValue(m) == "get_RewardFaults");
                                if (getter != development) throw new InvalidOperationException("Wrong debug API gate: " + name);
                            }
                        }
                    }
                    if (ads != (development ? 1 : 0) || intents != (development ? 1 : 0) || (requireProviders && providers == 0))
                        throw new InvalidOperationException("Missing ads/provider assembly coverage.");
                    Debug.Log($"[RewardFaultBuildCheck] {(development ? "development" : "release")} passed; providers={providers}");
                }
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
    }
}
#endif
