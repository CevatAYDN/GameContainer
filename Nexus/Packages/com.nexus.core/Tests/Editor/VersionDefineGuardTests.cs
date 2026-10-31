using System.Collections.Generic;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    /// <summary>Issue #143: a package below a Nexus asmdef's versionDefines expression compiled the assembly empty and nothing said so.</summary>
    public sealed class VersionDefineGuardTests
    {
        private const string LevelPlayAsmdef = "{ \"name\": \"Nexus.Ads.LevelPlay\", \"versionDefines\": [ { \"name\": \"com.unity.services.levelplay\", \"expression\": \"9.7.0\", \"define\": \"NEXUS_LEVELPLAY\" } ] }";
        private const string AdMobFirebaseAsmdef = "{ \"name\": \"Nexus.Ads.AdMob.Firebase\", \"defineConstraints\": [ \"NEXUS_ADMOB\", \"NEXUS_ADMOB_FIREBASE\" ], \"versionDefines\": [ " +
            "{ \"name\": \"com.google.ads.mobile\", \"expression\": \"[11.3.0,12.0.0)\", \"define\": \"NEXUS_ADMOB\" }, { \"name\": \"com.nexus.firebase\", \"expression\": \"0.1.0\", \"define\": \"NEXUS_ADMOB_FIREBASE\" } ] }";
        private const string LevelPlayTestsAsmdef = "{ \"name\": \"com.nexus.ads.levelplay.tests\", \"defineConstraints\": [ \"UNITY_INCLUDE_TESTS\", \"NEXUS_LEVELPLAY\" ], \"versionDefines\": [ " +
            "{ \"name\": \"com.unity.services.levelplay\", \"expression\": \"9.7.0\", \"define\": \"NEXUS_LEVELPLAY\" } ] }";

        private static List<string> Find(string asmdef, Dictionary<string, string> installed, Dictionary<string, ICollection<string>> compiled)
            => VersionDefineGuard.Find(new[] { asmdef }, p => installed.TryGetValue(p, out var v) ? v : null, a => compiled.TryGetValue(a, out var d) ? d : null);

        [Test]
        public void AnAssemblyCompiledWithoutItsVersionDefine_WhileItsPackageIsInstalled_IsAFinding_NamingBothVersions()
        {
            // define-cliff-20261031: expression 9.7.0, LevelPlay 9.6.0 installed, Nexus.Ads.LevelPlay compiled without NEXUS_LEVELPLAY
            var installed = new Dictionary<string, string> { { "com.unity.services.levelplay", "9.6.0" } };
            var compiled = new Dictionary<string, ICollection<string>> { { "Nexus.Ads.LevelPlay", new HashSet<string> { "UNITY_EDITOR" } } };
            var findings = Find(LevelPlayAsmdef, installed, compiled);
            Assert.AreEqual(1, findings.Count);
            StringAssert.Contains("Nexus.Ads.LevelPlay is compiled without NEXUS_LEVELPLAY: NEXUS_LEVELPLAY needs com.unity.services.levelplay 9.7.0 or newer, the project has 9.6.0", findings[0]);

            compiled["Nexus.Ads.LevelPlay"].Add("NEXUS_LEVELPLAY");
            CollectionAssert.IsEmpty(Find(LevelPlayAsmdef, installed, compiled), "the define is on");
            compiled["Nexus.Ads.LevelPlay"].Remove("NEXUS_LEVELPLAY");
            installed.Clear();
            CollectionAssert.IsEmpty(Find(LevelPlayAsmdef, installed, compiled), "without the SDK the provider is meant to stay out");
        }

        [Test]
        public void AnAssemblyItsVersionDefinesGate_IsAFindingWhenNotCompiled_OnlyWhileEveryGatingPackageIsInstalled_AndTestAssembliesAreLeftOut()
        {
            var installed = new Dictionary<string, string> { { "com.google.ads.mobile", "12.1.0" }, { "com.nexus.firebase", "0.1.0" } };
            var compiled = new Dictionary<string, ICollection<string>>();
            var findings = Find(AdMobFirebaseAsmdef, installed, compiled);
            Assert.AreEqual(1, findings.Count);
            StringAssert.Contains("Nexus.Ads.AdMob.Firebase is not compiled", findings[0]);
            StringAssert.Contains("NEXUS_ADMOB needs com.google.ads.mobile in [11.3.0,12.0.0), the project has 12.1.0", findings[0]);

            installed.Remove("com.nexus.firebase");
            CollectionAssert.IsEmpty(Find(AdMobFirebaseAsmdef, installed, compiled), "without com.nexus.firebase the assembly is meant to stay out");

            installed["com.unity.services.levelplay"] = "9.6.0";
            CollectionAssert.IsEmpty(Find(LevelPlayTestsAsmdef, installed, compiled), "a test assembly never fails a game's build");
        }
    }
}
