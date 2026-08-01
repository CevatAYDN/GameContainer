using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Extensions;
using Nexus.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    public class GameSaveManagerTests
    {
        private class TestSaveModel : ISaveDataProvider
        {
            public int BindCount;
            public byte[] CaptureSaveData() => System.Text.Encoding.UTF8.GetBytes("test");
            public void RestoreSaveData(byte[] data) => BindCount++;
        }

        private class TestPlayerPrefsService : IPlayerPrefsService
        {
            private readonly System.Collections.Generic.Dictionary<string, string> _data = new();
            public void SetString(string key, string value) => _data[key] = value;
            public string GetString(string key, string defaultValue = "") => _data.TryGetValue(key, out var value) ? value : defaultValue;
            public void SetInt(string key, int value) => _data[key] = value.ToString();
            public int GetInt(string key, int defaultValue = 0) => _data.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;
            public void SetFloat(string key, float value) => _data[key] = value.ToString();
            public float GetFloat(string key, float defaultValue = 0f) => _data.TryGetValue(key, out var value) && float.TryParse(value, out var parsed) ? parsed : defaultValue;
            public void SetLong(string key, long value) => _data[key] = value.ToString();
            public long GetLong(string key, long defaultValue = 0L) => _data.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : defaultValue;
            public bool GetBool(string key, bool defaultValue = false) => _data.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue;
            public void SetBool(string key, bool value) => _data[key] = value.ToString();
            public bool HasKey(string key) => _data.ContainsKey(key);
            public void DeleteKey(string key) => _data.Remove(key);
            public void DeleteAll() => _data.Clear();
            public void Save() { }
        }

        [Test]
        public async Task GameSaveManager_SaveAndLoad_RoundTripsModel()
        {
            var manager = new GameSaveManager();
            var model = new TestSaveModel();

            manager.RegisterModel(model);
            await manager.SaveAsync("slotA", CancellationToken.None);

            Assert.IsTrue(manager.SaveExists("slotA"));
            bool loaded = await manager.LoadAsync("slotA", CancellationToken.None);
            Assert.IsTrue(loaded);
            Assert.AreEqual(1, model.BindCount);
            manager.DeleteSave("slotA");
            manager.Dispose();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(".")]
        [TestCase("..")]
        public void GameSaveManager_InvalidSlotNames_AreRejected(string slotName)
        {
            var manager = new GameSaveManager();

            Assert.Throws<ArgumentException>(() => manager.SaveExists(slotName));
            Assert.Throws<ArgumentException>(() => manager.DeleteSave(slotName));
            manager.Dispose();
        }
    }
}
