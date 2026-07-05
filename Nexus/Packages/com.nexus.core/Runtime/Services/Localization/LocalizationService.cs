using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    public class LocalizationService : ILocalizationService, INexusService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        public string CurrentLanguage { get; private set; } = "en";
        public event Action<string> OnLanguageChanged;
        public bool IsRTL => CurrentLanguage == "ar";

        private readonly Dictionary<string, Dictionary<string, string>> _localizedTable = new Dictionary<string, Dictionary<string, string>>();

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            LoadSavedLanguage();
            BuildLocalizationDictionary();
            return default;
        }

        public void OnDispose()
        {
            _localizedTable.Clear();
        }

        private void LoadSavedLanguage()
        {
            if (PlayerPrefsService != null)
            {
                CurrentLanguage = PlayerPrefsService.GetString("NT_Language", "en");
            }
        }

        public void SetLanguage(string langCode)
        {
            if (string.IsNullOrEmpty(langCode) || CurrentLanguage == langCode) return;
            CurrentLanguage = langCode.ToLower();
            if (PlayerPrefsService != null)
            {
                PlayerPrefsService.SetString("NT_Language", CurrentLanguage);
                PlayerPrefsService.Save();
            }
            OnLanguageChanged?.Invoke(CurrentLanguage);
        }

        public string GetString(string key, string fallback = "")
        {
            if (string.IsNullOrEmpty(key)) return fallback;

            if (_localizedTable.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            {
                return FormatRTLIfNeeded(val);
            }
            if (_localizedTable.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enVal))
            {
                return FormatRTLIfNeeded(enVal);
            }
            return FormatRTLIfNeeded(!string.IsNullOrEmpty(fallback) ? fallback : key);
        }

        public string FormatRTLIfNeeded(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsRTL) return text;
            char[] chars = text.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        private void BuildLocalizationDictionary()
        {
            // Default universal UI fallback strings
            var en = new Dictionary<string, string>
            {
                { "btn_ok", "OK" },
                { "btn_cancel", "Cancel" },
                { "btn_play", "Play" },
                { "btn_retry", "Retry" },
                { "btn_close", "Close" },
                { "btn_settings", "Settings" },
                { "win_title", "Level Completed!" },
                { "fail_title", "Game Over!" }
            };

            var tr = new Dictionary<string, string>
            {
                { "btn_ok", "Tamam" },
                { "btn_cancel", "İptal" },
                { "btn_play", "Oyna" },
                { "btn_retry", "Tekrar Denet" },
                { "btn_close", "Kapat" },
                { "btn_settings", "Ayarlar" },
                { "win_title", "Bölüm Tamamlandı!" },
                { "fail_title", "Oyun Bitti!" }
            };

            _localizedTable["en"] = en;
            _localizedTable["tr"] = tr;
        }

        public void RegisterLanguageTable(string langCode, IDictionary<string, string> dictionary)
        {
            if (string.IsNullOrEmpty(langCode) || dictionary == null) return;
            string key = langCode.ToLower();
            if (!_localizedTable.TryGetValue(key, out var table))
            {
                table = new Dictionary<string, string>();
                _localizedTable[key] = table;
            }

            foreach (var kvp in dictionary)
            {
                table[kvp.Key] = kvp.Value;
            }
        }

        public void RegisterKey(string langCode, string key, string value)
        {
            if (string.IsNullOrEmpty(langCode) || string.IsNullOrEmpty(key)) return;
            string langKey = langCode.ToLower();
            if (!_localizedTable.TryGetValue(langKey, out var table))
            {
                table = new Dictionary<string, string>();
                _localizedTable[langKey] = table;
            }
            table[key] = value;
        }
    }
}
