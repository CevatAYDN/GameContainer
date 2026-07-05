using System;

namespace Nexus.Core.Services
{
    public interface ILocalizationTableProvider
    {
        bool TryGetTable(string langCode, out System.Collections.Generic.IDictionary<string, string> table);
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
