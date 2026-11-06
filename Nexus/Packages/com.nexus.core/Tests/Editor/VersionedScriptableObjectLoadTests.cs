using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    // #297: a new instance starts at the current version; only data Unity deserialized migrates.
    public class VersionedScriptableObjectLoadTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder("Assets", "NexusVersionedLoadTest_" + System.Guid.NewGuid().ToString("N")));
            // The hidden-hook error is logged once per type and domain: forget the type so each run sees it.
            lock (VersionedScriptableObject.s_deserializeHookHidden)
                VersionedScriptableObject.s_deserializeHookHidden.Remove(typeof(VersionedHidingProbe));
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_folder);

        [Test]
        public void ANewInstance_StartsAtTheCurrentVersion_WithoutMigrating()
        {
            var probe = ScriptableObject.CreateInstance<VersionedLoadProbe>();
            try
            {
                Assert.AreEqual(probe.CurrentVersion, probe.Version);
                Assert.AreEqual(0, probe.Migrations, "Migrate ran for a new instance");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void AnAssetStoredAtVersionZero_MigratesFromZero_WhenLoaded()
        {
            var loaded = SaveAtVersionZeroAndReload<VersionedLoadProbe>("Load.asset");

            Assert.AreEqual(1, loaded.Migrations);
            Assert.AreEqual(0, loaded.MigratedFrom);
            Assert.AreEqual(loaded.CurrentVersion, loaded.Version);
        }

        [Test]
        public void AnAssetOfASubclassThatHidesTheSerializationHook_StillMigrates_AndLogsAnError()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"VersionedHidingProbe implements ISerializationCallbackReceiver itself"));

            var loaded = SaveAtVersionZeroAndReload<VersionedHidingProbe>("Hiding.asset");

            Assert.AreEqual(1, loaded.Migrations, "the loaded asset was taken for new and skipped Migrate");
            Assert.AreEqual(0, loaded.MigratedFrom);
            Assert.AreEqual(loaded.CurrentVersion, loaded.Version);
        }

        private T SaveAtVersionZeroAndReload<T>(string file) where T : VersionedLoadProbe
        {
            string path = _folder + "/" + file;
            var probe = ScriptableObject.CreateInstance<T>();
            probe.SetStoredVersion(0);
            AssetDatabase.CreateAsset(probe, path);
            AssetDatabase.SaveAssets();
            Resources.UnloadAsset(probe);

            var loaded = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.IsNotNull(loaded, $"no {typeof(T).Name} at {path}");
            Assert.IsFalse(ReferenceEquals(loaded, probe), "the asset was not reloaded from disk");
            return loaded;
        }
    }
}
