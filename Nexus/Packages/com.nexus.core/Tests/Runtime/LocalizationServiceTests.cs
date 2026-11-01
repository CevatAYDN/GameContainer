using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

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
            public System.Action Saving;
            public void Save() => Saving?.Invoke();
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
        public void SavedLanguageWithCapitals_ReadsItsTable()
        {
            // issue #82: a game that saves "pt-BR" itself read every text in English
            var prefs = new TestPlayerPrefsService();
            prefs.SetString("NT_Language", "pt-BR");
            var service = Started(prefs, Tables("en", "pt-BR"), "en", new LocalizationOptions());
            Assert.AreEqual("pt-br", service.CurrentLanguage);
            Assert.AreEqual("pt-BR", service.GetString("title"));
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

        // Issue #187: the expected text was the reversal of itself, with a one-digit number, so a score of 125 reading 521 passed
        [Test]
        public void Format_FillsPlaceholdersBeforeTheRtlReversal()
        {
            var service = new LocalizationService(new TestPlayerPrefsService());
            service.RegisterKey("ar", "points", "{0} نقطة");
            service.SetLanguage("ar");

            Assert.AreEqual("ةطقن 125", service.Format("points", 125));
        }

        // Issue #187: every text element was reversed, digits and Latin letters included
        [TestCase("النقاط 125", "125 طاقنلا")]
        [TestCase("النقاط: 1,250.50", "1,250.50 :طاقنلا")]
        [TestCase("مستوى Level 12", "Level 12 ىوتسم")]
        [TestCase("10 من 20", "20 نم 10")]
        public void RtlText_KeepsDigitAndLatinRunsInTheirOwnOrder(string input, string expected)
        {
            var service = new LocalizationService(new TestPlayerPrefsService());
            service.SetLanguage("ar");

            Assert.AreEqual(expected, service.FormatRTLIfNeeded(input));
        }

        // Issue #187: a key found nowhere showed the key itself, and a language without a table or a text showed English, without a line
        [Test]
        public void MissingTexts_AreReportedOnceEach_NamingTheKeyAndTheLanguage()
        {
            var service = new LocalizationService(new TestPlayerPrefsService());
            service.RegisterKey("en", "title", "Title");
            service.RegisterKey("en", "play", "Play");
            service.RegisterKey("de", "play", "Spielen");
            service.SetLanguage("de");

            LogAssert.Expect(LogType.Warning, new Regex(@"^\[LocalizationService\] 'de' has no text for 'title': the English text is shown"));
            Assert.AreEqual("Title", service.GetString("title"));
            Assert.AreEqual("Title", service.GetString("title"), "once per language and key");

            LogAssert.Expect(LogType.Warning, new Regex(@"^\[LocalizationService\] 'shop' has no text in 'de' or English: the fallback or the key itself is shown"));
            Assert.AreEqual("shop", service.GetString("shop"));
            Assert.AreEqual("Shop", service.GetString("shop", "Shop"), "once per key");

            service.SetLanguage("fr");
            LogAssert.Expect(LogType.Warning, new Regex(@"^\[LocalizationService\] There is no 'fr' table: English texts are shown until one is registered"));
            Assert.AreEqual("Play", service.GetString("play"));
            Assert.AreEqual("Title", service.GetString("title"), "once per language");
            LogAssert.NoUnexpectedReceived();
        }

        // Issue #187: language codes were lower-cased with the current culture, so on a Turkish phone "ID" became "ıd"
        [Test]
        public void ALanguageCode_ReadsTheSameTableOnATurkishPhone()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            try
            {
                var prefs = new TestPlayerPrefsService();
                var service = new LocalizationService(prefs);
                service.RegisterLanguageTable("ID", new Dictionary<string, string> { ["title"] = "Judul" });
                service.SetLanguage("id");
                Assert.AreEqual("Judul", service.GetString("title"), "a table registered as \"ID\" serves \"id\"");

                prefs.SetString("NT_Language", "ID");
                Assert.AreEqual("id", Started(prefs, Tables("en"), "en", null).CurrentLanguage, "a saved \"ID\" reads as \"id\"");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        // Issue #187: SetLanguage held the table lock across the disk write, so every GetString waited for it
        [Test]
        public void SetLanguage_SavesOutsideTheTableLock_SoAReaderOnAnotherThreadIsNotHeld()
        {
            var prefs = new TestPlayerPrefsService();
            var service = new LocalizationService(prefs);
            service.RegisterKey("tr", "title", "Başlık");
            bool readWhileSaving = false;
            prefs.Saving = () => readWhileSaving = System.Threading.Tasks.Task.Run(() => service.GetString("title")).Wait(2000);

            service.SetLanguage("tr");

            Assert.IsTrue(readWhileSaving, "GetString on another thread finished while the language was being saved");
        }
    }
}
