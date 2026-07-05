using System;

namespace Nexus.Core.Services
{
    public interface ILocalizationService
    {
        string CurrentLanguage { get; }
        event Action<string> OnLanguageChanged;
        bool IsRTL { get; }
        void SetLanguage(string langCode);
        string GetString(string key, string fallback = "");
        string FormatRTLIfNeeded(string text);
    }
}
