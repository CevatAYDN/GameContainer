using System.Collections.Generic;
using System.Globalization;
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

        [Test]
        public void LocalizationService_RTLReversal_PreservesSurrogatePairs()
        {
            var prefs = new TestPlayerPrefsService();
            var service = new LocalizationService(prefs);
            service.SetLanguage("ar");

            // "مرحبا" + an emoji (U+1F600, a UTF-16 surrogate pair). A raw char-array
            // reversal would tear the pair apart; grapheme-aware reversal must keep it.
            string input = "مرحبا" + "\U0001F600";

            string formatted = service.FormatRTLIfNeeded(input);

            Assert.AreEqual("\U0001F600" + "ابحرم", formatted);
            Assert.AreEqual(input.Length, formatted.Length,
                "The surrogate pair must survive the reversal intact (same UTF-16 length).");
        }

        // ── Issue #72: first-run language, a shipped JSON table provider, culture and formatting ──

        private static JsonLocalizationTableProvider Tables(params string[] languages)
        {
            var json = new Dictionary<string, string>();
            foreach (var language in languages) json[language] = "{\"title\": \"" + language + "\", \"score\": \"{0:N1}\"}";
            return new JsonLocalizationTableProvider(json);
        }

        private static LocalizationService Started(TestPlayerPrefsService prefs, ILocalizationTableProvider tables, string device, LocalizationOptions options)
        {
            var service = new LocalizationService(prefs, tables) { Options = options, DeviceLanguageSource = () => device };
            service.InitializeAsync(default).GetAwaiter().GetResult();
            return service;
        }

        [TestCase("tr", "tr")]
        [TestCase("pt", "pt-br")]
        [TestCase("ja", "en")]
        public void FirstRun_PicksTheDeviceLanguageThatHasATable_ElseTheDefault(string device, string expected)
        {
            var prefs = new TestPlayerPrefsService();
            var service = Started(prefs, Tables("en", "tr", "pt-BR"), device, new LocalizationOptions());

            Assert.AreEqual(expected, service.CurrentLanguage);
            Assert.IsFalse(prefs.HasKey("NT_Language"), "The first-run pick is not saved; the player's own choice is.");
        }

        [Test]
        public void FirstRun_IgnoresTheBuiltInTurkishSeedWhenTheGameShipsOnlyEnglish()
        {
            var service = Started(new TestPlayerPrefsService(), Tables("en"), "tr", new LocalizationOptions());
            Assert.AreEqual("en", service.CurrentLanguage);
        }

        [Test]
        public void SavedLanguage_WinsOverTheDeviceLanguage()
        {
            var prefs = new TestPlayerPrefsService();
            prefs.SetString("NT_Language", "en");
            var service = Started(prefs, Tables("en", "tr"), "tr", new LocalizationOptions());
            Assert.AreEqual("en", service.CurrentLanguage);
        }

        [Test]
        public void JsonTableProvider_ReadsFlatTablesWithEscapesAndSkipsNonStrings()
        {
            var provider = new JsonLocalizationTableProvider(new Dictionary<string, string>
            {
                ["en"] = "{\"quote\": \"say \\\"hi\\\"\\nnow\", \"count\": 3, \"nested\": {\"a\": \"b\"}}",
            });

            Assert.IsTrue(provider.TryGetTable("en", out var table));
            Assert.AreEqual("say \"hi\"\nnow", table["quote"]);
            Assert.AreEqual(1, table.Count, "Numbers and objects are not texts.");
            Assert.IsFalse(provider.TryGetTable("de", out _));
        }

        [Test]
        public void Format_UsesTheCultureOfTheCurrentLanguage()
        {
            var service = Started(new TestPlayerPrefsService(), Tables("en", "tr"), "en", new LocalizationOptions());
            Assert.AreEqual("1,234.5", service.Format("score", 1234.5));

            service.SetLanguage("tr");
            Assert.AreEqual("tr-TR", service.Culture.Name);
            Assert.AreEqual("1.234,5", service.Format("score", 1234.5));
        }

        [Test]
        public void Format_UsesTheGamesOwnCultureWhenOneIsSet()
        {
            var fixedSeparators = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            fixedSeparators.NumberFormat.NumberDecimalSeparator = "'";
            var options = new LocalizationOptions { Cultures = new Dictionary<string, CultureInfo> { ["EN"] = fixedSeparators } };
            var service = Started(new TestPlayerPrefsService(), Tables("en"), "en", options);

            Assert.AreEqual("1,234'5", service.Format("score", 1234.5));
        }

        [Test]
        public void Format_FillsPlaceholdersBeforeTheRtlReversal()
        {
            var service = new LocalizationService(new TestPlayerPrefsService());
            service.RegisterKey("ar", "points", "{0} نقاط");
            service.SetLanguage("ar");

            Assert.AreEqual(service.FormatRTLIfNeeded("7 نقاط"), service.Format("points", 7));
        }
    }
}
