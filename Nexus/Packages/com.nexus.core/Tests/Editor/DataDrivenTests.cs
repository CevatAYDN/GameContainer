using NUnit.Framework;
using UnityEngine;
using Nexus.Core;
using System.Reflection;

namespace Nexus.Editor.Tests
{
    public class TestVersionedSO : VersionedScriptableObject
    {
        private int _currentVersion = 1;
        public override int CurrentVersion => _currentVersion;

        public int MigrationCallCount = 0;
        public int LastMigratedFrom = -1;

        public void SetCurrentVersion(int version)
        {
            _currentVersion = version;
        }

        public void SetSerializedVersion(int version)
        {
            var field = typeof(VersionedScriptableObject).GetField("_version", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(this, version);
        }

        public void TriggerOnValidate()
        {
            // Call protected OnValidate via reflection
            var method = typeof(VersionedScriptableObject).GetMethod("OnValidate", BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(this, null);
        }

        protected override void Migrate(int fromVersion)
        {
            MigrationCallCount++;
            LastMigratedFrom = fromVersion;
        }
    }

    [TestFixture]
    public class DataDrivenTests
    {
        [Test]
        public void Migration_Triggers_WhenVersionIsOlder()
        {
            var so = ScriptableObject.CreateInstance<TestVersionedSO>();
            so.SetSerializedVersion(0);
            so.SetCurrentVersion(1);

            so.TriggerOnValidate();

            Assert.AreEqual(1, so.Version);
            Assert.AreEqual(1, so.MigrationCallCount);
            Assert.AreEqual(0, so.LastMigratedFrom);
        }

        [Test]
        public void Migration_DoesNotTrigger_WhenVersionIsCurrent()
        {
            var so = ScriptableObject.CreateInstance<TestVersionedSO>();
            so.SetSerializedVersion(1);
            so.SetCurrentVersion(1);

            so.TriggerOnValidate();

            Assert.AreEqual(1, so.Version);
            Assert.AreEqual(0, so.MigrationCallCount);
        }

        [Test]
        public void Migration_DoesNotTrigger_WhenVersionIsNewer()
        {
            var so = ScriptableObject.CreateInstance<TestVersionedSO>();
            so.SetSerializedVersion(2);
            so.SetCurrentVersion(1);

            so.TriggerOnValidate();

            Assert.AreEqual(2, so.Version);
            Assert.AreEqual(0, so.MigrationCallCount);
        }
    }
}
