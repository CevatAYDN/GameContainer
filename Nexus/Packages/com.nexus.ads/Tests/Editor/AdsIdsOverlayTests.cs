using System.IO;
using Nexus.Ads.Editor;
using Nexus.Ads.Unity;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    public sealed class AdsIdsOverlayTests
    {
        const string Dir = "Assets/_AdsIdsOverlayTests";
        string _asset, _local;

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(Dir);
            _asset = Dir + "/NexusAdsSettings.asset";
            _local = Path.Combine(Path.GetTempPath(), "nexus-ids-" + System.Guid.NewGuid().ToString("N") + ".json");
            var s = ScriptableObject.CreateInstance<AdsSettings>();
            s.SelectedMediation = AdProviderKind.LevelPlay;
            AssetDatabase.CreateAsset(s, _asset);
            AdsIdsFile.PathOverride = _local;
        }

        [TearDown]
        public void TearDown()
        {
            AdsIdsFile.PathOverride = null;
            AdsIdsOverlay.RestoreFromBackup();
            AssetDatabase.DeleteAsset(Dir);
            if (File.Exists(_local)) File.Delete(_local);
        }

        AdsSettings Asset => AssetDatabase.LoadAssetAtPath<AdsSettings>(_asset);

        void WriteLocal(string releaseKey, string testUnit = "test-unit")
        {
            var f = new AdsIdsFile();
            f.Release.LevelPlay.Android.AppKey = releaseKey;
            f.Test.LevelPlay.Android.RewardedUnitId = testUnit;
            f.TestDeviceIds.Add("dev-1");
            f.Write(_local);
        }

        [Test]
        public void Apply_FillsEmptyFields_AndKeepsNonEmptyOnes()
        {
            Asset.Test.LevelPlay.Android.RewardedUnitId = "tracked";
            EditorUtility.SetDirty(Asset); AssetDatabase.SaveAssets();
            WriteLocal("rel-key");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Test.LevelPlay.Android.RewardedUnitId"));
            using (AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local))
            {
                Assert.AreEqual("rel-key", Asset.Release.LevelPlay.Android.AppKey);
                Assert.AreEqual("tracked", Asset.Test.LevelPlay.Android.RewardedUnitId);
                Assert.AreEqual("dev-1", Asset.TestDeviceIds[0]);
            }
        }

        [Test]
        public void Dispose_RestoresTheExactBytes_AndDeletesTheBackup()
        {
            var before = File.ReadAllBytes(_asset);
            WriteLocal("rel-key");
            using (AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local))
            {
                Assert.AreNotEqual(before, File.ReadAllBytes(_asset));
                Assert.IsTrue(File.Exists(AdsIdsOverlay.BackupPath));
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_asset));
            Assert.IsFalse(File.Exists(AdsIdsOverlay.BackupPath));
            Assert.IsFalse(AdsIdsOverlay.IsActive);
        }

        [Test]
        public void MissingFile_WithRequireRelease_Throws_AndNamesTheFile()
        {
            var ex = Assert.Throws<BuildFailedException>(() => AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local));
            StringAssert.Contains(_local, ex.Message);
            Assert.IsFalse(AdsIdsOverlay.IsActive);
        }

        [Test]
        public void MissingReleaseAppKey_Throws_AndRestoresTheAsset()
        {
            var before = File.ReadAllBytes(_asset);
            WriteLocal("");
            Assert.Throws<BuildFailedException>(() => AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_asset));
            Assert.IsFalse(File.Exists(AdsIdsOverlay.BackupPath));
        }

        [Test]
        public void MissingFile_WithoutRequireRelease_IsFine()
        {
            using (AdsIdsOverlay.Apply(false, BuildTarget.Android, _asset, _local)) Assert.IsFalse(AdsIdsOverlay.IsActive);
        }

        [Test]
        public void NestedScopes_RestoreOnlyWhenTheOuterOneEnds()
        {
            var before = File.ReadAllBytes(_asset);
            WriteLocal("rel-key");
            var outer = AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local);
            var inner = AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local);
            inner.Dispose();
            Assert.IsTrue(AdsIdsOverlay.IsActive);
            Assert.AreEqual("rel-key", Asset.Release.LevelPlay.Android.AppKey);
            outer.Dispose();
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_asset));
            Assert.IsFalse(AdsIdsOverlay.IsActive);
        }

        [Test]
        public void CrashRecovery_RestoresTheBackupLeftByADeadBuild()
        {
            var before = File.ReadAllBytes(_asset);
            WriteLocal("rel-key");
            AdsIdsOverlay.Apply(true, BuildTarget.Android, _asset, _local); // never disposed: the "crash"
            Assert.IsTrue(AdsIdsOverlay.RestoreFromBackup());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_asset));
            Assert.IsFalse(AdsIdsOverlay.RestoreFromBackup());
        }

        [Test]
        public void ExportThenScrub_MovesTheIdsToTheFile()
        {
            Asset.Release.LevelPlay.Android.AppKey = "rel-key";
            EditorUtility.SetDirty(Asset); AssetDatabase.SaveAssets();
            AdsIdsOverlay.ExportLocalFile(_asset, _local);
            AdsIdsOverlay.ScrubAsset(_asset, _local);
            Assert.AreEqual("", Asset.Release.LevelPlay.Android.AppKey);
            Assert.AreEqual("rel-key", AdsIdsFile.Read(_local).Release.LevelPlay.Android.AppKey);
        }

        [Test]
        public void EditorLoad_OverlaysInMemory_WithoutTouchingTheAsset()
        {
            WriteLocal("rel-key");
            var before = File.ReadAllBytes(_asset);
            var loaded = AdsSettings.Load();
            Assert.AreEqual("rel-key", loaded.Release.LevelPlay.Android.AppKey);
            Assert.AreEqual("", Asset.Release.LevelPlay.Android.AppKey);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_asset));
            Object.DestroyImmediate(loaded);
        }
    }
}
