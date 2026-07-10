using NUnit.Framework;
using UnityEngine;
using Nexus.Core;
using Nexus.Editor;
using System.Reflection;

namespace Nexus.Editor.Tests
{
    public class DummyModelData : ModelData
    {
        public override int CurrentVersion => 1;
        public int Value = 10;
    }

    [LiveReload]
    public class DummyModel
    {
        public DummyModelData Data;
        public bool Reloaded = false;

        public DummyModel(DummyModelData data)
        {
            Data = data;
        }

        private void OnLiveReload()
        {
            Reloaded = true;
        }
    }

    [TestFixture]
    public class LiveReloadTests
    {
        [Test]
        public void LiveReload_TriggersOnLiveReload_WhenModelDataChanges()
        {
            // 1. Create context
            var context = new Context();
            
            // 2. Create DummyModelData instance
            var modelData = ScriptableObject.CreateInstance<DummyModelData>();
            modelData.name = "TestDummyModelData";

            // 3. Bind model
            context.Container.BindInstance(modelData);
            context.Container.Bind<DummyModel>();

            // 4. Resolve dummy model to register in resolved singletons
            var dummyModel = context.Container.Resolve<DummyModel>();
            Assert.IsFalse(dummyModel.Reloaded);

            // 5. Trigger live reload via reflection
            var method = typeof(LiveReloadProcessor).GetMethod("TriggerLiveReload", BindingFlags.NonPublic | BindingFlags.Static);
            method?.Invoke(null, new object[] { modelData });

            // 6. Verify OnLiveReload was triggered
            Assert.IsTrue(dummyModel.Reloaded);

            // 7. Cleanup
            context.Dispose();
        }

        [Test]
        public void LiveReload_NoException_WhenNoMatchingModelIsRegistered()
        {
            var modelData = ScriptableObject.CreateInstance<DummyModelData>();

            var method = typeof(LiveReloadProcessor).GetMethod("TriggerLiveReload", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.DoesNotThrow(() => method?.Invoke(null, new object[] { modelData }));

            Object.DestroyImmediate(modelData);
        }
    }
}
