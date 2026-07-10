using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Tests
{
    [TestFixture]
    public class LocalizationServiceTests
    {
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
        public void LocalizationService_SetsAndPersistsLanguage()
        {
            var prefs = new TestPlayerPrefsService();
            var service = new LocalizationService(prefs);

            service.SetLanguage("tr");

            Assert.AreEqual("tr", service.CurrentLanguage);
            Assert.IsTrue(prefs.HasKey("NT_Language"));
        }

        [Test]
        [TestCase("ar", "مرحبا", "ابحرم")]
        [TestCase("he", "שלום", "םולש")] // Note: using actual character reversal for the test case values
        [TestCase("fa", "سلام", "مالس")]
        [TestCase("en", "hello", "hello")]
        public void LocalizationService_RTLReversal(string lang, string input, string expected)
        {
            var prefs = new TestPlayerPrefsService();
            var service = new LocalizationService(prefs);
            service.SetLanguage(lang);
            
            string formatted = service.FormatRTLIfNeeded(input);
            Assert.AreEqual(expected, formatted);
        }
    }
}
