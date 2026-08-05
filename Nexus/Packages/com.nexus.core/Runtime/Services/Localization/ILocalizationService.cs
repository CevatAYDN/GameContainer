using System;

namespace Nexus.Core.Services
{
    public interface ILocalizationTableProvider
    {
        bool TryGetTable(string langCode, out System.Collections.Generic.IDictionary<string, string> table);

        /// <summary>
        /// Language codes this provider can supply tables for. The default implementation
        /// preserves the original behavior (only the built-in "en"/"tr" pair is probed),
        /// so existing providers need no changes; providers with more languages override
        /// this to have every table loaded.
        /// </summary>
        System.Collections.Generic.IEnumerable<string> GetAvailableLanguages() => new[] { "en", "tr" };
    }

    public interface ILocalizationService
    {
        string CurrentLanguage { get; }
        event Action<string> OnLanguageChanged;
        bool IsRTL { get; }
        void SetLanguage(string langCode);
        string GetString(string key, string fallback = "");
        string FormatRTLIfNeeded(string text);
        void RegisterLanguageTable(System.Collections.Generic.IDictionary<string, string> dictionary) => RegisterLanguageTable("en", dictionary);
        void RegisterLanguageTable(string langCode, System.Collections.Generic.IDictionary<string, string> dictionary);
        void RegisterKey(string langCode, string key, string value);
    }
}
