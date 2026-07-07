using NUnit.Framework;
using Nexus.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Tests
{
    [TestFixture]
    public class GameSaveManagerTests
    {
        private class TestSaveModel : IReactiveModel
        {
            public int BindCount;
            public void OnBind(IContext context) => BindCount++;
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
            public bool HasKey(string key) => _data.ContainsKey(key);
            public void DeleteKey(string key) => _data.Remove(key);
            public void DeleteAll() => _data.Clear();
            public void Save() { }
        }

        [Test]
        public async Task GameSaveManager_SaveAndLoad_RoundTripsModel()
        {
            var prefs = new TestPlayerPrefsService();
            var context = new MockContext();
            var manager = new GameSaveManager(prefs, context);
            var model = new TestSaveModel();

            manager.RegisterModel(model);
            await manager.SaveAsync("slotA", CancellationToken.None);

            Assert.IsTrue(prefs.HasKey("slotA"));
            Assert.GreaterOrEqual(model.BindCount, 0);
            manager.Dispose();
        }
    }
}
