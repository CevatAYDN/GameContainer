using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    [Preserve]
    public class LocalizationService : NexusService<ILocalizationService>, ILocalizationService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        // Optional: built-in "en"/"tr" tables work without it; LoadExternalTables()
        // null-checks. A missing binding must not fail strict injection / validation.
        [OptionalInject] public ILocalizationTableProvider TableProvider { get; set; }

        // CurrentLanguage is accessed from multiple threads (editor main, async tasks,
        // and potential worker threads). Mark volatile to ensure reads always observe the
        // most recent write. Accesses that rely on dictionary lookups are synchronized
        // via _tableLock to avoid TOCTOU races.
        private volatile string _currentLanguage = "en";
        public string CurrentLanguage { get => _currentLanguage; private set => _currentLanguage = value; }

        public event Action<string> OnLanguageChanged;

        public bool IsRTL
        {
            get
            {
                var lang = _currentLanguage;
                return lang == "ar" || lang == "he" || lang == "fa" || lang == "ur";
            }
        }

        public LocalizationService() { }

        public LocalizationService(IPlayerPrefsService prefs, ILocalizationTableProvider tableProvider = null)
        {
            PlayerPrefsService = prefs;
            TableProvider = tableProvider;
        }

        private readonly Dictionary<string, Dictionary<string, string>> _localizedTable = new Dictionary<string, Dictionary<string, string>>();
        private readonly object _tableLock = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            LoadSavedLanguage();
            BuildLocalizationDictionary();
            LoadExternalTables();
            return default;
        }

        public override void OnDispose()
        {
            OnLanguageChanged = null;
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
            // Fast path check
            if (_currentLanguage == normalized) return;

            Action<string> handlersToInvoke = null;
            lock (_tableLock)
            {
                if (_currentLanguage == normalized) return;
                _currentLanguage = normalized;
                if (PlayerPrefsService != null)
                {
                    PlayerPrefsService.SetString("NT_Language", _currentLanguage);
                    PlayerPrefsService.Save();
                }
                // Capture delegate snapshot while under lock to ensure a consistent view
                handlersToInvoke = OnLanguageChanged;
            }

            // Invoke per-subscriber: a throwing subscriber must not silence the rest of
            // the multicast list.
            if (handlersToInvoke != null)
            {
                foreach (var d in handlersToInvoke.GetInvocationList())
                {
                    try
                    {
                        ((Action<string>)d).Invoke(_currentLanguage);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[LocalizationService] OnLanguageChanged handler threw: {ex.Message}");
                    }
                }
            }
        }

        public string GetString(string key, string fallback = "")
        {
            if (string.IsNullOrEmpty(key)) return fallback;

            // Capture current language and use it within the lock to avoid TOCTOU.
            string lang;
            lock (_tableLock)
            {
                lang = _currentLanguage;
                if (_localizedTable.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var val))
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

        /// <summary>
        /// Seeds generic UI strings (buttons and common dialog titles) for the two built-in
        /// languages, so a project gets sensible text before it registers its own tables.
        /// </summary>
        /// <remarks>
        /// Deliberately limited to framework-generic keys. Game-specific content — titles,
        /// dialogue, item names — belongs in the consuming project and is supplied through
        /// <see cref="ILocalizationTableProvider"/> or <see cref="RegisterLanguageTable"/>;
        /// a reusable package must not ship one game's copy to every other game. Keys
        /// registered by the project win, because these defaults are only applied to
        /// languages that have no table yet, and per-key overrides merge on top.
        /// </remarks>
        private void BuildLocalizationDictionary()
        {
            lock (_tableLock)
            {
                if (!_localizedTable.ContainsKey("en"))
                {
                    _localizedTable["en"] = new Dictionary<string, string>
                    {
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
                        { "btn_undo", "Geri Al" },
                        { "btn_ok", "Tamam" },
                        { "btn_cancel", "İptal" },
                        { "btn_play", "Oyna" },
                        { "btn_retry", "Tekrar Dene" },
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

            // The provider enumerates its own languages (default implementation yields the
            // built-in "en"/"tr" pair); a misbehaving provider falls back to that pair so
            // the core languages always load.
            IEnumerable<string> languages = null;
            try
            {
                languages = TableProvider.GetAvailableLanguages();
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[LocalizationService] GetAvailableLanguages threw: {ex.Message}. Falling back to en/tr.");
            }
            languages ??= new[] { "en", "tr" };

            foreach (var lang in languages)
            {
                if (string.IsNullOrEmpty(lang)) continue;
                if (TableProvider.TryGetTable(lang, out var table) && table != null)
                {
                    RegisterLanguageTable(lang, table);
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
