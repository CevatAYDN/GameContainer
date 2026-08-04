using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    public class LocalizationService : ILocalizationService, INexusService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        // Optional: built-in "en"/"tr" tables work without it; LoadExternalTables()
        // null-checks. A missing binding must not fail strict injection / validation.
        [OptionalInject] public ILocalizationTableProvider TableProvider { get; set; }

        public string CurrentLanguage { get; private set; } = "en";
        public event Action<string> OnLanguageChanged;
        public bool IsRTL => CurrentLanguage == "ar" || CurrentLanguage == "he" || CurrentLanguage == "fa" || CurrentLanguage == "ur";

        public LocalizationService() { }

        public LocalizationService(IPlayerPrefsService prefs, ILocalizationTableProvider tableProvider = null)
        {
            PlayerPrefsService = prefs;
            TableProvider = tableProvider;
        }

        private readonly Dictionary<string, Dictionary<string, string>> _localizedTable = new Dictionary<string, Dictionary<string, string>>();
        private readonly object _tableLock = new();

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            LoadSavedLanguage();
            BuildLocalizationDictionary();
            LoadExternalTables();
            return default;
        }

        public void OnDispose()
        {
            lock (_tableLock)
            {
                _localizedTable.Clear();
            }
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
            if (string.IsNullOrEmpty(langCode)) return;

            string normalized = langCode.ToLower();
            if (CurrentLanguage == normalized) return;

            CurrentLanguage = normalized;
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

            lock (_tableLock)
            {
                if (_localizedTable.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
                {
                    return FormatRTLIfNeeded(val);
                }
                if (_localizedTable.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enVal))
                {
                    return FormatRTLIfNeeded(enVal);
                }
            }

            return FormatRTLIfNeeded(!string.IsNullOrEmpty(fallback) ? fallback : key);
        }

        public string FormatRTLIfNeeded(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsRTL) return text;

            // Reverse by text element (grapheme cluster) instead of raw UTF-16 code
            // units: Array.Reverse tears surrogate pairs (emoji, CJK ext) and combining
            // marks apart, producing broken glyphs. Strategy: reverse the whole buffer,
            // then re-reverse each cluster in place so its internal char order survives.
            int[] elements = StringInfo.ParseCombiningCharacters(text);
            if (elements.Length <= 1) return text;

            char[] chars = text.ToCharArray();
            Array.Reverse(chars);

            int n = chars.Length;
            for (int k = 0; k < elements.Length; k++)
            {
                int start = elements[k];
                int len = (k + 1 < elements.Length ? elements[k + 1] : n) - start;
                // After the global reversal this cluster occupies [n - start - len, n - start).
                int lo = n - start - len;
                int hi = n - start;
                for (int a = lo, b = hi - 1; a < b; a++, b--)
                {
                    char t = chars[a]; chars[a] = chars[b]; chars[b] = t;
                }
            }
            return new string(chars);
        }

        private void BuildLocalizationDictionary()
        {
            lock (_tableLock)
            {
                // Default universal UI fallback strings
                if (!_localizedTable.ContainsKey("en"))
                {
                    _localizedTable["en"] = new Dictionary<string, string>
                    {
                        { "app_name", "Neon Transit" },
                        { "btn_undo", "Undo" },
                        { "btn_ok", "OK" },
                        { "btn_cancel", "Cancel" },
                        { "btn_play", "Play" },
                        { "btn_retry", "Retry" },
                        { "btn_close", "Close" },
                        { "btn_settings", "Settings" },
                        { "win_title", "Level Completed!" },
                        { "fail_title", "Game Over!" }
                    };
                }

                if (!_localizedTable.ContainsKey("tr"))
                {
                    _localizedTable["tr"] = new Dictionary<string, string>
                    {
                        { "app_name", "Neon Transit" },
                        { "btn_undo", "Geri Al" },
                        { "btn_ok", "Tamam" },
                        { "btn_cancel", "İptal" },
                        { "btn_play", "Oyna" },
                        { "btn_retry", "Tekrar Denet" },
                        { "btn_close", "Kapat" },
                        { "btn_settings", "Ayarlar" },
                        { "win_title", "Bölüm Tamamlandı!" },
                        { "fail_title", "Oyun Bitti!" }
                    };
                }
            }
        }

        private void LoadExternalTables()
        {
            if (TableProvider == null) return;

            var languages = new[] { "en", "tr" };
            for (int i = 0; i < languages.Length; i++)
            {
                if (TableProvider.TryGetTable(languages[i], out var table) && table != null)
                {
                    RegisterLanguageTable(languages[i], table);
                }
            }
        }

        public void RegisterLanguageTable(string langCode, IDictionary<string, string> dictionary)
        {
            if (string.IsNullOrEmpty(langCode) || dictionary == null) return;
            string key = langCode.ToLower();
            lock (_tableLock)
            {
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
        }

        public void RegisterKey(string langCode, string key, string value)
        {
            if (string.IsNullOrEmpty(langCode) || string.IsNullOrEmpty(key)) return;
            string langKey = langCode.ToLower();
            lock (_tableLock)
            {
                if (!_localizedTable.TryGetValue(langKey, out var table))
                {
                    table = new Dictionary<string, string>();
                    _localizedTable[langKey] = table;
                }
                table[key] = value;
            }
        }
    }
}
