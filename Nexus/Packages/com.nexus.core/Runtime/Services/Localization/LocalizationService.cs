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
        // Optional: without it the first run is English and the culture follows the language code.
        [OptionalInject] public LocalizationOptions Options { get; set; }

        private const string LanguageKey = "NT_Language";

        // The device language as a code ("tr", "pt", "zh-hans"); tests replace it.
        internal Func<string> DeviceLanguageSource = DeviceLanguageCode;

        // CurrentLanguage is accessed from multiple threads (editor main, async tasks,
        // and potential worker threads). Mark volatile to ensure reads always observe the
        // most recent write. Accesses that rely on dictionary lookups are synchronized
        // via _tableLock to avoid TOCTOU races.
        private volatile string _currentLanguage = "en";
        public string CurrentLanguage { get => _currentLanguage; private set => _currentLanguage = value; }

        public event Action<string> OnLanguageChanged;

        private CultureInfo _culture;
        public CultureInfo Culture => _culture ?? (_culture = CultureFor(_currentLanguage));

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
        // Languages the game supplied (provider, RegisterLanguageTable, RegisterKey), not the built-in en/tr seeds.
        private readonly HashSet<string> _suppliedLanguages = new HashSet<string>();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            BuildLocalizationDictionary();
            LoadExternalTables();
            LoadSavedLanguage();
            return default;
        }

        public override void OnDispose()
        {
            OnLanguageChanged = null;
            lock (_tableLock)
            {
                _localizedTable.Clear();
                _suppliedLanguages.Clear();
            }
        }

        private void LoadSavedLanguage()
        {
            if (PlayerPrefsService != null && PlayerPrefsService.HasKey(LanguageKey))
                CurrentLanguage = PlayerPrefsService.GetString(LanguageKey, "en");
            else if (Options != null)
                CurrentLanguage = FirstRunLanguage(Options); // not saved: the device language is followed until the player picks one
            _culture = CultureFor(_currentLanguage);
        }

        private string FirstRunLanguage(LocalizationOptions options)
        {
            string fallback = string.IsNullOrEmpty(options.DefaultLanguage) ? "en" : options.DefaultLanguage.ToLowerInvariant();
            if (!options.UseDeviceLanguage) return fallback;
            string device = null;
            try { device = DeviceLanguageSource?.Invoke(); }
            catch (Exception ex) { NexusRuntime.Logger?.LogWarning($"[LocalizationService] Reading the device language failed: {ex.Message}"); }
            if (string.IsNullOrEmpty(device)) return fallback;
            device = device.ToLowerInvariant();
            int dash = device.IndexOf('-');
            string baseLanguage = dash > 0 ? device.Substring(0, dash) : null;
            lock (_tableLock)
            {
                IEnumerable<string> languages = _suppliedLanguages.Count > 0 ? _suppliedLanguages : _localizedTable.Keys;
                string regional = null;
                foreach (var language in languages)
                {
                    if (language == device) return language;
                    if (regional == null && (language == baseLanguage || language.StartsWith(device + "-", StringComparison.Ordinal))) regional = language;
                }
                return regional ?? fallback;
            }
        }

        private CultureInfo CultureFor(string language)
        {
            var cultures = Options?.Cultures;
            if (cultures != null)
            {
                foreach (var entry in cultures)
                    if (entry.Value != null && string.Equals(entry.Key, language, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            }
            try { return CultureInfo.CreateSpecificCulture(language); }
            catch (ArgumentException) { return CultureInfo.InvariantCulture; }
        }

        private static readonly Dictionary<string, string> s_systemLanguageCodes = new Dictionary<string, string>
        {
            { "Afrikaans", "af" }, { "Arabic", "ar" }, { "Basque", "eu" }, { "Belarusian", "be" }, { "Bulgarian", "bg" },
            { "Catalan", "ca" }, { "Chinese", "zh" }, { "ChineseSimplified", "zh-hans" }, { "ChineseTraditional", "zh-hant" },
            { "Czech", "cs" }, { "Danish", "da" }, { "Dutch", "nl" }, { "English", "en" }, { "Estonian", "et" },
            { "Faroese", "fo" }, { "Finnish", "fi" }, { "French", "fr" }, { "German", "de" }, { "Greek", "el" },
            { "Hebrew", "he" }, { "Hindi", "hi" }, { "Hungarian", "hu" }, { "Icelandic", "is" }, { "Indonesian", "id" },
            { "Italian", "it" }, { "Japanese", "ja" }, { "Korean", "ko" }, { "Latvian", "lv" }, { "Lithuanian", "lt" },
            { "Norwegian", "no" }, { "Polish", "pl" }, { "Portuguese", "pt" }, { "Romanian", "ro" }, { "Russian", "ru" },
            { "SerboCroatian", "sr" }, { "Slovak", "sk" }, { "Slovenian", "sl" }, { "Spanish", "es" }, { "Swedish", "sv" },
            { "Thai", "th" }, { "Turkish", "tr" }, { "Ukrainian", "uk" }, { "Vietnamese", "vi" },
        };

        // By name, so a SystemLanguage member an older or newer engine lacks cannot break compilation.
        private static string DeviceLanguageCode() =>
            s_systemLanguageCodes.TryGetValue(Application.systemLanguage.ToString(), out var code) ? code : null;

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
                _culture = CultureFor(normalized);
                if (PlayerPrefsService != null)
                {
                    PlayerPrefsService.SetString(LanguageKey, _currentLanguage);
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
            return FormatRTLIfNeeded(TryLookup(key, out var text) ? text : (!string.IsNullOrEmpty(fallback) ? fallback : key));
        }

        public string Format(string key, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            string pattern = TryLookup(key, out var text) ? text : key;
            string formatted;
            try { formatted = string.Format(Culture, pattern, args ?? Array.Empty<object>()); }
            catch (FormatException ex)
            {
                NexusRuntime.Logger?.LogWarning($"[LocalizationService] '{key}' is not a valid format for {(args == null ? 0 : args.Length)} argument(s): {ex.Message}");
                formatted = pattern;
            }
            // Formatted before the RTL reversal, which would otherwise turn "{0}" into "}0{".
            return FormatRTLIfNeeded(formatted);
        }

        private bool TryLookup(string key, out string text)
        {
            // Capture current language and use it within the lock to avoid TOCTOU.
            lock (_tableLock)
            {
                if (_localizedTable.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out text)) return true;
                if (_localizedTable.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out text)) return true;
            }
            text = null;
            return false;
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
                _suppliedLanguages.Add(key);
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
                _suppliedLanguages.Add(langKey);
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
